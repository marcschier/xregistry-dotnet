using System.Globalization;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

namespace XRegistry.Storage.File;

internal static class StoreDatabase
{
    internal const int Version = 1;
    private const long ApplicationId = 0x58525346;
    private const string StateSchema = "CREATE TABLE store_state (singleton INTEGER PRIMARY KEY CHECK(singleton = 1), store_id TEXT NOT NULL CHECK(length(store_id) = 32), generation INTEGER NOT NULL CHECK(generation >= 0)) STRICT";
    private const string BlobsSchema = "CREATE TABLE blobs (digest TEXT PRIMARY KEY CHECK(length(digest) = 64 AND digest NOT GLOB '*[^0-9a-f]*'), length INTEGER NOT NULL CHECK(length >= 0)) STRICT, WITHOUT ROWID";
    private const string RecordsSchema = "CREATE TABLE records (key TEXT PRIMARY KEY, metadata BLOB NOT NULL, document_digest TEXT REFERENCES blobs(digest)) STRICT, WITHOUT ROWID";

    internal static SqliteConnection Open(string root, string storeId, FileStoreLimits limits, bool create)
    {
        var path = Path.Combine(root, "store.sqlite");
        if (!create)
        {
            DirectoryDurability.EnsureRegularFile(path);
            var length = new FileInfo(path).Length;
            if (length == 0 || length > limits.MaxDatabaseBytes)
            {
                throw new StorageException(StorageFailure.InvalidStore, "The initialized database is empty or exceeds its configured size limit.");
            }
        }

        var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        try
        {
            database.Open();
            Execute(database, "PRAGMA trusted_schema = OFF; PRAGMA foreign_keys = ON; PRAGMA mmap_size = 0; PRAGMA synchronous = FULL;");
            if (create)
            {
                if (!string.Equals(Scalar(database, "PRAGMA journal_mode = WAL;") as string, "wal", StringComparison.Ordinal))
                {
                    throw new StorageException(StorageFailure.UnsupportedStorage, "SQLite WAL mode could not be enabled.");
                }
            }
            else
            {
                Validate(database, storeId);
                if (!string.Equals(Scalar(database, "PRAGMA journal_mode;") as string, "wal", StringComparison.Ordinal))
                {
                    throw new StorageException(StorageFailure.InvalidStore, "The initialized database is not in WAL mode.");
                }
            }

            Execute(database, "PRAGMA cache_size = -" + (limits.SqliteCacheBytes / 1024).ToString(CultureInfo.InvariantCulture) + ";");
            Execute(database, "PRAGMA max_page_count = " + (limits.MaxDatabaseBytes / 4096).ToString(CultureInfo.InvariantCulture) + ";");
            Execute(database, "PRAGMA journal_size_limit = 0; PRAGMA wal_autocheckpoint = 0;");
            if (create)
            {
                using var transaction = database.BeginTransaction(deferred: false);
                Execute(database, "PRAGMA application_id = 0x58525346; PRAGMA user_version = 1;", transaction);
                Execute(database, StateSchema, transaction);
                Execute(database, BlobsSchema, transaction);
                Execute(database, RecordsSchema, transaction);
                using var command = database.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO store_state(singleton, store_id, generation) VALUES (1, $id, 0);";
                command.Parameters.AddWithValue("$id", storeId);
                command.ExecuteNonQuery();
                transaction.Commit();
                Checkpoint(database);
            }

            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    internal static void Validate(SqliteConnection database, string storeId)
    {
        if (Scalar(database, "PRAGMA application_id;") is not long applicationId || applicationId != ApplicationId ||
            Scalar(database, "PRAGMA user_version;") is not long version || version != Version)
        {
            throw new StorageException(StorageFailure.InvalidStore, "The database has a foreign application identity or unsupported schema version.");
        }

        if (!string.Equals(Scalar(database, "PRAGMA integrity_check;") as string, "ok", StringComparison.Ordinal))
        {
            throw new StorageException(StorageFailure.IntegrityFailure, "SQLite integrity verification failed.");
        }

        using (var command = database.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            var expected = new[] { ("blobs", BlobsSchema), ("records", RecordsSchema), ("store_state", StateSchema) };
            foreach (var (name, sql) in expected)
            {
                if (!reader.Read() || reader.GetString(0) != name || reader.GetString(1) != sql)
                {
                    throw new StorageException(StorageFailure.InvalidStore, "The database schema does not match storage schema version 1.");
                }
            }

            if (reader.Read())
            {
                throw new StorageException(StorageFailure.InvalidStore, "The database contains unexpected schema objects.");
            }
        }

        using (var command = database.CreateCommand())
        {
            command.CommandText = "SELECT store_id, generation FROM store_state WHERE singleton = 1;";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != storeId || reader.GetInt64(1) < 0 || reader.Read())
            {
                throw new StorageException(StorageFailure.InvalidStore, "The database identity does not match its initialization markers.");
            }
        }

        using (var command = database.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                throw new StorageException(StorageFailure.IntegrityFailure, "SQLite contains a missing document reference.");
            }
        }
    }

    internal static long Generation(SqliteConnection database, SqliteTransaction? transaction = null) =>
        (long)(Scalar(database, "SELECT generation FROM store_state WHERE singleton = 1;", transaction)
            ?? throw new StorageException(StorageFailure.InvalidStore, "The database generation is missing."));

    internal static void Checkpoint(SqliteConnection database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != 0)
        {
            throw new StorageException(StorageFailure.CommitOutcomeUnknown, "The committed WAL could not be checkpointed; dispose and reopen before continuing.");
        }
    }

    internal static object? Scalar(SqliteConnection database, string sql, SqliteTransaction? transaction = null)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteScalar();
    }

    internal static void Execute(SqliteConnection database, string sql, SqliteTransaction? transaction = null)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}
