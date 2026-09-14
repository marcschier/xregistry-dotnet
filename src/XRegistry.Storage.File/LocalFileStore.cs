using System.Text;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

namespace XRegistry.Storage.File;

/// <summary>
/// A single authoritative local SQLite/immutable-file writer. This primitive stores opaque JSON
/// records, not registry lifecycle semantics. SQLite operations are synchronous and never queued.
/// </summary>
public sealed partial class LocalFileStore : IDisposable
{
    private const string IdentityName = "store.identity";
    private const string ReadyName = "store.ready";
    private const string DatabaseName = "store.sqlite";
    private const string MarkerPrefix = "xregistry-file-store\n1\n";
    private readonly object _sync = new();
    private readonly DirectoryDurability _directories;
    private readonly FileStream _writerLock;
    private readonly SqliteConnection _database;
    private readonly FileStoreLimits _limits;
    private readonly HashSet<StorageSnapshot> _snapshots = [];
    private bool _activeOperation;
    private bool _disposed;
    private bool _databaseClosed;
    private bool _handlesClosed;
    private long _snapshotBytes;

    private LocalFileStore(DirectoryDurability directories, FileStream writerLock, SqliteConnection database, FileStoreLimits limits)
    {
        _directories = directories;
        _writerLock = writerLock;
        _database = database;
        _limits = limits;
    }

    /// <summary>Initializes an existing empty, privately administered local directory. Never replaces an existing store.</summary>
    public static LocalFileStore Initialize(string directory, FileStoreLimits? limits = null, CancellationToken cancellationToken = default) =>
        OpenCore(directory, limits, initialize: true, recoverInitialization: false, cancellationToken);

    /// <summary>Opens an initialized store and verifies its schema and content. Missing state is never created.</summary>
    public static LocalFileStore Open(string directory, FileStoreLimits? limits = null, CancellationToken cancellationToken = default) =>
        OpenCore(directory, limits, initialize: false, recoverInitialization: false, cancellationToken);

    /// <summary>
    /// Explicitly finishes interrupted first initialization only when its existing database is
    /// intact, generation zero and empty. Missing or malformed databases are never reset.
    /// </summary>
    public static LocalFileStore RecoverInitialization(string directory, FileStoreLimits? limits = null, CancellationToken cancellationToken = default) =>
        OpenCore(directory, limits, initialize: false, recoverInitialization: true, cancellationToken);

    /// <summary>Gets the immutable bounds applied to this store instance.</summary>
    public FileStoreLimits Limits => _limits;

    /// <summary>Reads the current publication generation through one indexed SQLite scalar query, without loading records or Documents.</summary>
    public long ReadGeneration(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        return StoreDatabase.Generation(_database);
    }

    /// <summary>Reads an immutable, verified snapshot. The caller must dispose its document pin lease.</summary>
    public StorageSnapshot ReadSnapshot(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        return RetainSnapshot(null, cancellationToken);
    }

    /// <summary>Reads owning metadata and pins Document references without opening all Document contents.</summary>
    /// <remarks>Every OpenDocument still verifies exact length/hash. Ordinary Open and ReadSnapshot retain full integrity verification.</remarks>
    public StorageSnapshot ReadMetadataSnapshot(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        return RetainSnapshot(null, cancellationToken, verifyDocuments: false);
    }

    /// <summary>Reads one ordinal key through SQLite's primary-key index without scanning unrelated records or Documents.</summary>
    /// <remarks>A missing key produces an empty snapshot with the current generation. The snapshot retains its normal Document lease.</remarks>
    public StorageSnapshot ReadSnapshot(string key, CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        ValidateKey(key);
        return RetainSnapshot(key, cancellationToken);
    }

    private StorageSnapshot RetainSnapshot(string? key, CancellationToken cancellationToken, bool verifyDocuments = true)
    {
        var generation = StoreDatabase.Generation(_database);
        var (records, bytes) = ReadRecords(cancellationToken, key, verifyDocuments);
        var snapshot = new StorageSnapshot(this, generation, records, bytes);
        lock (_sync)
        {
            if (_snapshots.Count >= _limits.MaxReadSnapshots)
            {
                throw new StorageException(StorageFailure.LimitExceeded, "The snapshot lease count limit was exceeded.");
            }

            _snapshotBytes = AddWithin(_snapshotBytes, bytes, _limits.MaxReadSnapshotBytes, "live snapshot metadata");
            _snapshots.Add(snapshot);
        }

        return snapshot;
    }

    private static LocalFileStore OpenCore(string directory, FileStoreLimits? limits, bool initialize, bool recoverInitialization, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new FileStoreLimits();
        limits.Validate();
        DirectoryDurability? directories = null;
        FileStream? writerLock = null;
        SqliteConnection? database = null;
        try
        {
            directories = DirectoryDurability.Open(directory);
            var root = directories.Root;
            var lockPath = Path.Combine(root, "writer.lock");
            if (!initialize)
            {
                RequireFile(lockPath);
            }

            if (IOFile.Exists(lockPath))
            {
                DirectoryDurability.EnsureRegularFile(lockPath);
            }

            try
            {
                writerLock = new FileStream(lockPath, initialize ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    writerLock.Lock(0, 1);
                }
            }
            catch (IOException error)
            {
                throw new StorageException(StorageFailure.WriterUnavailable, "The storage directory has another writer or its exclusive lock is unavailable.", error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string identity;
            if (initialize)
            {
                if (Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) != "writer.lock"))
                {
                    throw new StorageException(StorageFailure.InvalidStore, "Initialization requires an empty directory; existing evidence is never removed.");
                }

                identity = Guid.NewGuid().ToString("N");
                WriteMarker(directories, IdentityName, identity);
                Directory.CreateDirectory(Path.Combine(root, "blobs"));
                Directory.CreateDirectory(Path.Combine(root, "staging"));
                directories.Flush(root);
                directories.FlushParent();
            }
            else
            {
                identity = ReadMarker(root, IdentityName);
                if (recoverInitialization)
                {
                    if (IOFile.Exists(Path.Combine(root, ReadyName)))
                    {
                        throw new StorageException(StorageFailure.InvalidStore, "An already initialized store cannot use first-initialization recovery.");
                    }
                }
                else if (!IOFile.Exists(Path.Combine(root, ReadyName)))
                {
                    throw new StorageException(StorageFailure.InitializationIncomplete, "The durable ready marker is missing; ordinary open cannot infer empty state.");
                }
                else if (ReadMarker(root, ReadyName) != identity)
                {
                    throw new StorageException(StorageFailure.InvalidStore, "The initialization markers disagree.");
                }

                RequireFile(Path.Combine(root, DatabaseName));
            }

            directories.RetainChild("blobs");
            directories.RetainChild("staging");
            database = StoreDatabase.Open(root, identity, limits, create: initialize);
            cancellationToken.ThrowIfCancellationRequested();
            if (recoverInitialization &&
                (StoreDatabase.Generation(database) != 0 || (long)StoreDatabase.Scalar(database, "SELECT count(*) FROM records;")! != 0 ||
                 (long)StoreDatabase.Scalar(database, "SELECT count(*) FROM blobs;")! != 0))
            {
                throw new StorageException(StorageFailure.InvalidStore, "Recovery cannot reset or relabel a store containing committed state.");
            }

            if (initialize || recoverInitialization)
            {
                directories.Flush(root);
                WriteMarker(directories, ReadyName, identity);
            }

            var store = new LocalFileStore(directories, writerLock, database, limits);
            store.ReadRecords(cancellationToken);
            return store;
        }
        catch (SqliteException error)
        {
            database?.Dispose();
            writerLock?.Dispose();
            directories?.Dispose();
            throw new StorageException(StorageFailure.InvalidStore, "SQLite could not open or validate the storage database.", error);
        }
        catch
        {
            database?.Dispose();
            writerLock?.Dispose();
            directories?.Dispose();
            throw;
        }
    }

    private static void RequireFile(string path)
    {
        if (!IOFile.Exists(path))
        {
            throw new StorageException(StorageFailure.InvalidStore, "An initialized storage artifact is missing; no replacement was created.");
        }

        DirectoryDurability.EnsureRegularFile(path);
    }

    private static string ReadMarker(string root, string name)
    {
        var path = Path.Combine(root, name);
        RequireFile(path);
        if (new FileInfo(path).Length != MarkerPrefix.Length + 33)
        {
            throw new StorageException(StorageFailure.InvalidStore, "A storage initialization marker is malformed.");
        }

        var text = IOFile.ReadAllText(path, Encoding.ASCII);
        if (!text.StartsWith(MarkerPrefix, StringComparison.Ordinal) ||
            text[^1] != '\n' || !Guid.TryParseExact(text.AsSpan(MarkerPrefix.Length, 32), "N", out var id) ||
            text.AsSpan(MarkerPrefix.Length, 32).ContainsAnyInRange('A', 'F'))
        {
            throw new StorageException(StorageFailure.InvalidStore, "A storage initialization marker is malformed.");
        }

        return id.ToString("N");
    }

    private static void WriteMarker(DirectoryDurability directories, string name, string identity)
    {
        var bytes = Encoding.ASCII.GetBytes(MarkerPrefix + identity + "\n");
        using (var stream = new FileStream(Path.Combine(directories.Root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        directories.Flush(directories.Root);
    }

    private OperationLease EnterOperation(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_faulted)
            {
                throw new StorageException(StorageFailure.UnusableStore, "This store must be disposed and reopened after an uncertain commit.");
            }
            if (_activeOperation)
            {
                throw new StorageException(StorageFailure.Busy, "Another storage operation occupies the single execution slot; no work was queued.");
            }

            _activeOperation = true;
            return new OperationLease(this);
        }
    }

    private void ExitOperation()
    {
        lock (_sync)
        {
            _activeOperation = false;
            RemoveDisposedCandidates();
            FinishDisposal();
        }
    }

    internal void ReleaseSnapshot(StorageSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_snapshots.Remove(snapshot))
            {
                _snapshotBytes -= snapshot.MetadataBytes;
            }

            FinishDisposal();
        }
    }

    /// <summary>
    /// Rejects new operations. Existing snapshot and stream leases retain their content and
    /// writer exclusion; underlying handles close only after those leases finish.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            FinishDisposal();
        }
    }

    private void FinishDisposal()
    {
        if (!_disposed || _activeOperation)
        {
            return;
        }

        if (!_databaseClosed)
        {
            RemoveDisposedCandidates();
            _database.Dispose();
            _databaseClosed = true;
        }

        if (!_handlesClosed && _snapshots.Count == 0 && _documentStreams.Count == 0)
        {
            _writerLock.Dispose();
            _directories.Dispose();
            _handlesClosed = true;
        }
    }

    private readonly struct OperationLease(LocalFileStore owner) : IDisposable
    {
        public void Dispose() => owner.ExitOperation();
    }
}
