using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace XRegistry.Storage.File;

/// <summary>A SHA-256 identity and exact byte length, not an entity identifier or Epoch.</summary>
public sealed record DocumentReference
{
    /// <summary>Creates a document reference. Its bytes are verified before the store releases a read.</summary>
    public DocumentReference(string sha256, long length)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        if (sha256.Length != 64 || sha256.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A document digest must be 64 lowercase hexadecimal SHA-256 characters.", nameof(sha256));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Sha256 = sha256;
        Length = length;
    }

    /// <summary>Gets the canonical lowercase hexadecimal SHA-256 digest.</summary>
    public string Sha256 { get; }
    /// <summary>Gets the exact byte length, including zero for a present empty Document.</summary>
    public long Length { get; }
}

/// <summary>An immutable metadata record with an optional exact Document.</summary>
public sealed class StorageRecord
{
    internal StorageRecord(string key, JsonElement metadata, DocumentReference? document)
    {
        Key = key;
        Metadata = metadata;
        Document = document;
    }

    /// <summary>Gets the case-sensitive opaque key, which is never a filesystem path.</summary>
    public string Key { get; }
    /// <summary>Gets immutable, independently owned JSON metadata.</summary>
    public JsonElement Metadata { get; }
    /// <summary>Gets the verified document reference, or null for no Document.</summary>
    public DocumentReference? Document { get; }
}

/// <summary>A bounded immutable metadata snapshot and document pin lease. Dispose to release its pins.</summary>
public sealed class StorageSnapshot : IDisposable
{
    private readonly LocalFileStore _owner;
    private readonly FrozenDictionary<string, StorageRecord> _byKey;
    private int _disposed;

    internal StorageSnapshot(LocalFileStore owner, long generation, StorageRecord[] records, long bytes)
    {
        _owner = owner;
        Generation = generation;
        Records = new ReadOnlyCollection<StorageRecord>(records);
        _byKey = records.ToFrozenDictionary(static record => record.Key, StringComparer.Ordinal);
        MetadataBytes = bytes;
    }

    /// <summary>Gets the storage publication generation, not an xRegistry entity Epoch.</summary>
    public long Generation { get; }
    /// <summary>Gets the ordinal-key-ordered immutable records.</summary>
    public IReadOnlyList<StorageRecord> Records { get; }
    internal long MetadataBytes { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Opens verified exact bytes with an independent lease, including after store disposal.</summary>
    /// <remarks>The caller owns the returned stream. A missing key and an absent Document are distinct errors.</remarks>
    public Stream OpenDocument(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var record = _byKey.GetValueOrDefault(key)
            ?? throw new KeyNotFoundException("The snapshot does not contain the requested key.");
        var document = record.Document ?? throw new InvalidOperationException("The record has no Document.");
        return _owner.OpenDocument(this, document, cancellationToken);
    }

    /// <summary>Releases this snapshot's pins; independently returned streams keep their own leases.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.ReleaseSnapshot(this);
        }
    }
}
