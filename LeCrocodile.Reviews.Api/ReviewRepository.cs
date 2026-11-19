using System.Globalization;
using LeCrocodile.Reviews.Contracts;
using Microsoft.Data.Sqlite;

namespace LeCrocodile.Reviews.Api;

public sealed class ReviewRepository
{
    private readonly string connectionString;

    public ReviewRepository(string databasePath)
    {
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version;";
        long version = (long)command.ExecuteScalar()!;
        if (version > 3)
        {
            throw new InvalidDataException(
                "The database schema is newer than this server. Upgrade the server before starting it.");
        }

        if (version == 3)
        {
            return;
        }

        if (version == 2)
        {
            using SqliteTransaction migration = connection.BeginTransaction();
            command.Transaction = migration;
            command.CommandText = """
                ALTER TABLE Reviews ADD COLUMN AlcoholLevel INTEGER CHECK(AlcoholLevel BETWEEN 0 AND 2);
                CREATE TABLE AlcoholSummaries (
                    CocktailId TEXT PRIMARY KEY,
                    Level INTEGER NOT NULL CHECK(Level BETWEEN 0 AND 2)
                ) STRICT;
                PRAGMA user_version=3;
                """;
            command.ExecuteNonQuery();
            migration.Commit();
            return;
        }

        if (version == 1)
        {
            using SqliteTransaction migration = connection.BeginTransaction();
            command.Transaction = migration;
            command.CommandText = """
                CREATE TABLE Reviews_new (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CocktailId TEXT NOT NULL CHECK(length(CocktailId) BETWEEN 1 AND 128),
                    UserId TEXT NOT NULL CHECK(length(UserId) = 32),
                    UserName TEXT NOT NULL CHECK(length(trim(UserName)) BETWEEN 1 AND 40),
                    Rating INTEGER NOT NULL CHECK(Rating BETWEEN 1 AND 5),
                    Comment TEXT NOT NULL CHECK(length(trim(Comment)) <= 2000),
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    UNIQUE(CocktailId, UserId)
                ) STRICT;
                INSERT INTO Reviews_new SELECT * FROM Reviews;
                DROP TABLE Reviews;
                ALTER TABLE Reviews_new RENAME TO Reviews;
                CREATE INDEX IX_Reviews_CocktailId_Id ON Reviews(CocktailId, Id DESC);
                PRAGMA user_version=2;
                """;
            command.ExecuteNonQuery();
            migration.Commit();
            Initialize();
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE Reviews (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CocktailId TEXT NOT NULL CHECK(length(CocktailId) BETWEEN 1 AND 128),
                UserId TEXT NOT NULL CHECK(length(UserId) = 32),
                UserName TEXT NOT NULL CHECK(length(trim(UserName)) BETWEEN 1 AND 40),
                Rating INTEGER NOT NULL CHECK(Rating BETWEEN 1 AND 5),
                Comment TEXT NOT NULL CHECK(length(trim(Comment)) <= 2000),
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                UNIQUE(CocktailId, UserId)
            ) STRICT;
            CREATE INDEX IX_Reviews_CocktailId_Id ON Reviews(CocktailId, Id DESC);
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        Initialize();
    }

    public AlcoholSummary[] GetAlcoholSummaries(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CocktailId, Level FROM AlcoholSummaries ORDER BY CocktailId;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<AlcoholSummary> result = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new AlcoholSummary(reader.GetString(0), reader.GetInt32(1)));
        }

        return result.ToArray();
    }

    public ReviewSummary[] GetSummaries(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CocktailId, COUNT(*), AVG(Rating) FROM Reviews GROUP BY CocktailId ORDER BY CocktailId;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<ReviewSummary> summaries = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            summaries.Add(new ReviewSummary(reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2)));
        }

        return summaries.ToArray();
    }

    public void Backup(string destinationPath)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // Reserve a new destination so an operator cannot accidentally replace an existing backup or the live database.
        using (FileStream reservation = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        using SqliteConnection source = OpenConnection();
        using SqliteConnection destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        // A standalone snapshot must be readable from a read-only import mount without creating WAL sidecars.
        using SqliteCommand command = destination.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        command.ExecuteScalar();
    }

    public ReviewPage GetPage(string cocktailId, Guid userId, long? before, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*), COALESCE(AVG(Rating), 0.0) FROM Reviews WHERE CocktailId=$cocktail;";
        command.Parameters.AddWithValue("$cocktail", cocktailId);
        long totalCount;
        double averageRating;
        using (SqliteDataReader summary = command.ExecuteReader())
        {
            summary.Read();
            totalCount = summary.GetInt64(0);
            averageRating = summary.GetDouble(1);
        }

        command.CommandText = """
            SELECT Id, UserName, Rating, Comment, CreatedAt, UpdatedAt, UserId, AlcoholLevel FROM Reviews
            WHERE CocktailId=$cocktail AND Id < $before ORDER BY Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$before", before ?? long.MaxValue);
        command.Parameters.AddWithValue("$limit", limit + 1);
        List<ReviewResponse> reviews = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                reviews.Add(ReadReview(reader, userId));
            }
        }

        bool hasMore = reviews.Count > limit;
        if (hasMore)
        {
            reviews.RemoveAt(reviews.Count - 1);
        }

        command.CommandText = """
            SELECT Id, UserName, Rating, Comment, CreatedAt, UpdatedAt, UserId, AlcoholLevel FROM Reviews
            WHERE CocktailId=$cocktail AND UserId=$user;
            """;
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        ReviewResponse? ownReview;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            ownReview = reader.Read() ? ReadReview(reader, userId) : null;
        }

        command.CommandText = "SELECT Level FROM AlcoholSummaries WHERE CocktailId=$cocktail;";
        object? levelValue = command.ExecuteScalar();
        int? level = levelValue is null ? null : Convert.ToInt32(levelValue, CultureInfo.InvariantCulture);
        transaction.Commit();
        return new ReviewPage(reviews.ToArray(), totalCount, averageRating, hasMore ? reviews[^1].Id : null, ownReview, level);
    }

    public ReviewResponse Save(string cocktailId, Guid userId, SubmitReviewRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Reviews(CocktailId, UserId, UserName, Rating, Comment, CreatedAt, UpdatedAt, AlcoholLevel)
            VALUES($cocktail, $user, $name, $rating, $comment, $now, $now, $alcohol)
            ON CONFLICT(CocktailId, UserId) DO UPDATE SET
                UserName=excluded.UserName, Rating=excluded.Rating, Comment=excluded.Comment, UpdatedAt=excluded.UpdatedAt,
                AlcoholLevel=COALESCE(excluded.AlcoholLevel, Reviews.AlcoholLevel)
            RETURNING Id, UserName, Rating, Comment, CreatedAt, UpdatedAt, UserId, AlcoholLevel;
            """;
        command.Parameters.AddWithValue("$cocktail", cocktailId);
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$name", request.UserName.Trim());
        command.Parameters.AddWithValue("$rating", request.Rating);
        command.Parameters.AddWithValue("$comment", request.Comment.Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$alcohol", (object?)request.AlcoholLevel ?? DBNull.Value);
        ReviewResponse result;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            reader.Read();
            result = ReadReview(reader, userId);
        }

        if (request.AlcoholLevel is not null)
        {
            command.CommandText = """
                INSERT INTO AlcoholSummaries(CocktailId, Level)
                SELECT $cocktail, CASE WHEN SUM(AlcoholLevel) * 3 < COUNT(*) * 2 THEN 0
                    WHEN SUM(AlcoholLevel) * 3 < COUNT(*) * 4 THEN 1 ELSE 2 END
                FROM Reviews WHERE CocktailId=$cocktail AND AlcoholLevel IS NOT NULL
                ON CONFLICT(CocktailId) DO UPDATE SET Level=excluded.Level;
                """;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return result;
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static ReviewResponse ReadReview(SqliteDataReader reader, Guid userId)
    {
        return new ReviewResponse(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetString(6) == userId.ToString("N"),
            reader.IsDBNull(7) ? null : reader.GetInt32(7));
    }
}
