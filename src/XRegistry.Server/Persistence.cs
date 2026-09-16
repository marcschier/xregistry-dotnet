// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Server;

/// <summary>An opaque, independently owned metadata record and optional exact Document.</summary>
public sealed record RegistryRecord
{
    /// <summary>Creates a record. Metadata must not borrow disposable storage.</summary>
    public RegistryRecord(string key, RegistryJson metadata, bool hasDocument = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(metadata);
        Key = key;
        Metadata = metadata;
        HasDocument = hasDocument;
    }

    /// <summary>Gets the ordinal opaque key, never a filesystem path.</summary>
    public string Key { get; }
    /// <summary>Gets owned immutable JSON; its internal schema belongs to the engine.</summary>
    public RegistryJson Metadata { get; }
    /// <summary>Gets whether a Document exists, including a present zero-byte Document.</summary>
    public bool HasDocument { get; }
}

/// <summary>The Document action accompanying a metadata replacement.</summary>
public enum RegistryDocumentAction
{
    /// <summary>Retains the previous Document, if any, without rereading it.</summary>
    Preserve,
    /// <summary>Removes the previous Document.</summary>
    Remove,
    /// <summary>Replaces the Document with all bytes of the supplied stream, including zero bytes.</summary>
    Replace
}

/// <summary>One atomic opaque-key mutation. Input streams remain caller-owned.</summary>
public sealed class RegistryMutation
{
    private RegistryMutation(string key, RegistryJson? metadata, RegistryDocumentAction documentAction, Stream? document)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Key = key;
        Metadata = metadata;
        DocumentAction = documentAction;
        Document = document;
    }

    /// <summary>Gets the ordinal key.</summary>
    public string Key { get; }
    /// <summary>Gets the replacement metadata, or null for deletion.</summary>
    public RegistryJson? Metadata { get; }
    /// <summary>Gets whether this mutation deletes the entire record and its Document.</summary>
    public bool IsDelete => Metadata is null;
    /// <summary>Gets how the Document is changed.</summary>
    public RegistryDocumentAction DocumentAction { get; }
    /// <summary>Gets the caller-owned stream, read only during PrepareAsync and never disposed by persistence.</summary>
    public Stream? Document { get; }

    /// <summary>Replaces metadata while retaining any existing Document.</summary>
    public static RegistryMutation Put(string key, RegistryJson metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new(key, metadata, RegistryDocumentAction.Preserve, null);
    }

    /// <summary>Replaces metadata and removes any Document.</summary>
    public static RegistryMutation PutWithoutDocument(string key, RegistryJson metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new(key, metadata, RegistryDocumentAction.Remove, null);
    }

    /// <summary>Replaces metadata and stages the exact remaining bytes of a readable stream.</summary>
    public static RegistryMutation PutDocument(string key, RegistryJson metadata, Stream document)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CanRead)
        {
            throw new ArgumentException("The Document stream must be readable.", nameof(document));
        }

        return new(key, metadata, RegistryDocumentAction.Replace, document);
    }

    /// <summary>Deletes a record. An absent key is a no-op within the atomic batch.</summary>
    public static RegistryMutation Delete(string key) => new(key, null, RegistryDocumentAction.Remove, null);
}

/// <summary>A pinned consistent view. Point reads must not copy the entire registry.</summary>
public interface IRegistrySnapshot : IDisposable
{
    /// <summary>Gets the publication generation, not an entity Epoch.</summary>
    long Generation { get; }
    /// <summary>Finds an ordinal key without copying the registry; null means absent.</summary>
    RegistryRecord? Find(string key);
    /// <summary>Enumerates direct children whose key prefix is collectionKey + "/"; descendants are excluded.</summary>
    IEnumerable<RegistryRecord> GetChildren(string collectionKey);
    /// <summary>Enumerates all records for explicitly budgeted administrative operations.</summary>
    IEnumerable<RegistryRecord> EnumerateRecords();
    /// <summary>Opens a caller-owned exact-byte lease that remains valid after snapshot disposal.</summary>
    Stream OpenDocument(string key, CancellationToken cancellationToken = default);
}

/// <summary>A privately staged atomic candidate. Disposal without CommitAsync abandons it.</summary>
public interface IRegistryCommit : IDisposable
{
    /// <summary>
    /// Rechecks the expected generation and publishes every mutation or none. Cancellation is observed
    /// before publication, never reported as rollback after publication. An uncertain outcome must throw
    /// RegistryCommitOutcomeUnknownException, not a conflict or a success-shaped result.
    /// </summary>
    ValueTask<long> CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>The engine's persistence boundary. Implementations own their durability and concurrency policy.</summary>
public interface IRegistryPersistence
{
    /// <summary>Gets whether mutation preparation is prohibited.</summary>
    bool IsReadOnly { get; }
    /// <summary>Returns a caller-owned pinned snapshot; concurrent independent snapshots may coexist.</summary>
    ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Stages a unique-key batch without publication. Reads Document streams before returning;
    /// the candidate owns staged content thereafter. Must reject stale expected generations.
    /// </summary>
    ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
        CancellationToken cancellationToken = default);
}

/// <summary>A failed expected-generation check; no part of the candidate was published.</summary>
public sealed class RegistryConcurrencyException : InvalidOperationException
{
    /// <summary>Creates a known-not-committed conflict.</summary>
    public RegistryConcurrencyException() : base("The registry publication generation changed.") { }
}

/// <summary>A persistence failure for which publication cannot safely be determined. Never automatically retry.</summary>
public sealed class RegistryCommitOutcomeUnknownException : IOException
{
    /// <summary>Creates an explicitly uncertain commit failure.</summary>
    public RegistryCommitOutcomeUnknownException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
