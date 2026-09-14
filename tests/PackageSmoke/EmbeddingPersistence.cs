using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using XRegistry;
using XRegistry.Server;

namespace EmbeddingConsumer;

// Application-owned, bounded copy-on-write storage. Publication is atomic in this process, not crash-durable.
internal sealed class ApplicationRecordStore : IDisposable
{
    private readonly object _gate = new();
    private Image _image = new(0, new Dictionary<string, Entry>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal));
    private bool _disposed;
    private long _publications;

    internal sealed record Entry(string Key, RegistryJson Metadata, byte[]? Document);
    internal sealed record Image(long Generation, FrozenDictionary<string, Entry> Entries);
    internal long Publications => Interlocked.Read(ref _publications);
    internal bool IsDisposed { get { lock (_gate) { return _disposed; } } }

    internal Image Read()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _image;
        }
    }

    internal long Publish(long expectedGeneration, Image image, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_image.Generation != expectedGeneration)
            {
                throw new RegistryConcurrencyException();
            }

            _image = image;
            Interlocked.Increment(ref _publications);
            return image.Generation;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}

// This adapter translates the public persistence contract, not private HTTP prepare/replay operations.
internal sealed class ApplicationRegistryPersistence(ApplicationRecordStore store, bool readOnly = false) :
    IRegistryPersistence, IDisposable
{
    private const int MaxRecords = 256;
    private const int MaxDocumentBytes = 64 * 1024;
    private const long MaxImageBytes = 1024 * 1024;
    private bool _disposed;
    private long _reads;
    private long _preparations;

    public bool IsReadOnly => readOnly;
    internal long SnapshotReads => Interlocked.Read(ref _reads);
    internal long Preparations => Interlocked.Read(ref _preparations);
    internal bool IsDisposed => _disposed;

    public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _reads);
        return ValueTask.FromResult<IRegistrySnapshot>(new Snapshot(store.Read()));
    }

    public async ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration,
        IReadOnlyList<RegistryMutation> mutations, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mutations);
        cancellationToken.ThrowIfCancellationRequested();
        if (readOnly)
        {
            throw new NotSupportedException("The application adapter is read-only.");
        }

        Interlocked.Increment(ref _preparations);
        var previous = store.Read();
        if (previous.Generation != expectedGeneration)
        {
            throw new RegistryConcurrencyException();
        }

        if (mutations.Count > MaxRecords)
        {
            throw new InvalidDataException("The application mutation budget is exhausted.");
        }

        var records = previous.Entries.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutation.Key.Length > 8192 || !seen.Add(mutation.Key))
            {
                throw new InvalidDataException("The application batch requires bounded, unique ordinal keys.");
            }

            if (mutation.IsDelete)
            {
                records.Remove(mutation.Key);
                continue;
            }

            var document = mutation.DocumentAction switch
            {
                RegistryDocumentAction.Preserve => records.GetValueOrDefault(mutation.Key)?.Document,
                RegistryDocumentAction.Remove => null,
                RegistryDocumentAction.Replace => await ReadDocumentAsync(mutation.Document!, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException("Unsupported application Document action.")
            };
            var metadata = RegistryJson.FromElement(mutation.Metadata!.RootElement,
                new RegistryJsonLimits { MaxBytes = 64 * 1024, MaxDepth = 64 });
            records[mutation.Key] = new(mutation.Key, metadata, document);
        }

        long bytes = 0;
        foreach (var entry in records.Values)
        {
            bytes += Encoding.UTF8.GetByteCount(entry.Key) +
                Encoding.UTF8.GetByteCount(entry.Metadata.RootElement.GetRawText()) + (entry.Document?.Length ?? 0);
            if (records.Count > MaxRecords || bytes > MaxImageBytes)
            {
                throw new InvalidDataException("The application image budget is exhausted.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new ApplicationRecordStore.Image(checked(expectedGeneration + 1),
            records.ToFrozenDictionary(StringComparer.Ordinal));
        return new Candidate(this, expectedGeneration, candidate);
    }

    private static async ValueTask<byte[]> ReadDocumentAsync(Stream input, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var length = (int)Math.Min(buffer.Length, MaxDocumentBytes - output.Length + 1);
                var read = await input.ReadAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (read > MaxDocumentBytes - output.Length)
                {
                    throw new InvalidDataException("The application Document budget is exhausted.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public void Dispose() => _disposed = true;

    private long Publish(long expected, ApplicationRecordStore.Image image, CancellationToken cancellationToken) =>
        store.Publish(expected, image, cancellationToken);

    private sealed class Snapshot(ApplicationRecordStore.Image image) : IRegistrySnapshot
    {
        private bool _disposed;
        public long Generation => image.Generation;

        public RegistryRecord? Find(string key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return image.Entries.TryGetValue(key, out var entry) ? Record(entry) : null;
        }

        public IEnumerable<RegistryRecord> GetChildren(string collectionKey)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var prefix = collectionKey + "/";
            return image.Entries.Values.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                !entry.Key.AsSpan(prefix.Length).Contains('/')).OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(Record).ToArray();
        }

        public IEnumerable<RegistryRecord> EnumerateRecords()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return image.Entries.Values.Select(Record).ToArray();
        }

        public Stream OpenDocument(string key, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var entry = image.Entries.GetValueOrDefault(key) ?? throw new KeyNotFoundException("The record is absent.");
            return new MemoryStream(entry.Document ?? throw new InvalidOperationException("The Document is absent."), writable: false);
        }

        private static RegistryRecord Record(ApplicationRecordStore.Entry entry) =>
            new(entry.Key, entry.Metadata, entry.Document is not null);

        public void Dispose() => _disposed = true;
    }

    private sealed class Candidate(ApplicationRegistryPersistence owner, long expected, ApplicationRecordStore.Image image) : IRegistryCommit
    {
        private bool _disposed;
        private bool _attempted;

        public ValueTask<long> CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            if (_attempted)
            {
                throw new InvalidOperationException("The candidate has already been submitted.");
            }

            _attempted = true;
            return ValueTask.FromResult(owner.Publish(expected, image, cancellationToken));
        }

        public void Dispose() => _disposed = true;
    }
}
