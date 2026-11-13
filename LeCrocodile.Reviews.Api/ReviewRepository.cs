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
        if (version > 1)
        {
            throw new InvalidDataException(
                "The database schema is newer than this server. Upgrade the server before starting it.");
        }

        if (version == 1)
        {
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
                Comment TEXT NOT NULL CHECK(length(trim(Comment)) BETWEEN 1 AND 2000),
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                UNIQUE(CocktailId, UserId)
            ) STRICT;
            CREATE INDEX IX_Reviews_CocktailId_Id ON Reviews(CocktailId, Id DESC);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
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
            SELECT Id, UserName, Rating, Comment, CreatedAt, UpdatedAt, UserId FROM Reviews
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

        transaction.Commit();
        return new ReviewPage(reviews.ToArray(), totalCount, averageRating, hasMore ? reviews[^1].Id : null);
    }

    public ReviewResponse Save(string cocktailId, Guid userId, SubmitReviewRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Reviews(CocktailId, UserId, UserName, Rating, Comment, CreatedAt, UpdatedAt)
            VALUES($cocktail, $user, $name, $rating, $comment, $now, $now)
            ON CONFLICT(CocktailId, UserId) DO UPDATE SET
                UserName=excluded.UserName, Rating=excluded.Rating, Comment=excluded.Comment, UpdatedAt=excluded.UpdatedAt
            RETURNING Id, UserName, Rating, Comment, CreatedAt, UpdatedAt, UserId;
            """;
        command.Parameters.AddWithValue("$cocktail", cocktailId);
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$name", request.UserName.Trim());
        command.Parameters.AddWithValue("$rating", request.Rating);
        command.Parameters.AddWithValue("$comment", request.Comment.Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        return ReadReview(reader, userId);
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
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetString(6) == userId.ToString("N"));
    }
}
