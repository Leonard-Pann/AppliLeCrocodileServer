using Microsoft.Data.Sqlite;

namespace LeCrocodile.Reviews.Api;

public static class DatabaseMaintenance
{
    public static long Inspect(string databasePath)
    {
        using SqliteConnection connection = OpenReadOnly(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SQLite integrity validation failed.");
        }

        command.CommandText = "PRAGMA user_version;";
        if ((long)command.ExecuteScalar()! is not (1 or 2))
        {
            throw new InvalidDataException("Only review database schema versions 1 and 2 can be imported.");
        }

        command.CommandText = "SELECT COUNT(*) FROM Reviews;";
        return (long)command.ExecuteScalar()!;
    }

    public static long Import(string sourcePath, string destinationPath)
    {
        string destination = Path.GetFullPath(destinationPath);
        if (File.Exists(destination) || File.Exists(destination + "-wal") || File.Exists(destination + "-shm"))
        {
            throw new IOException("Import requires a new database path without SQLite sidecar files.");
        }

        Inspect(sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string stagingPath = destination + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            using (SqliteConnection source = OpenReadOnly(sourcePath))
            using (SqliteConnection target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = stagingPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
                using SqliteCommand command = target.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE;";
                command.ExecuteScalar();
            }

            long count = Inspect(stagingPath);
            File.Move(stagingPath, destination, overwrite: false);
            return count;
        }
        finally
        {
            File.Delete(stagingPath);
            File.Delete(stagingPath + "-wal");
            File.Delete(stagingPath + "-shm");
        }
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
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
}
