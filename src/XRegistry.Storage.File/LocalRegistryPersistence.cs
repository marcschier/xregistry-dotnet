// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Text;
using XRegistry.Server;

namespace XRegistry.Storage.File;

/// <summary>Adapts the durable SQLite/file primitive to the engine's opaque transactional persistence contract.</summary>
/// <remarks>
/// Builds immutable point/child indexes once per observed generation, not per request. Old snapshots pin
/// their generation and Documents. SQLite execution remains synchronous and bounded; no Task.Run queue is hidden.
/// </remarks>
public sealed class LocalRegistryPersistence : IRegistryPersistence, IDisposable
{
    private readonly object _gate = new();
    private readonly LocalFileStore _store;
    private readonly bool _ownsStore;
    private readonly int _maxSnapshots;
    private Image? _cached;
    private int _snapshots;
    private bool _disposed;

    /// <summary>Creates an adapter. By default the caller retains store ownership.</summary>
    public LocalRegistryPersistence(LocalFileStore store, bool ownsStore = false, int maxSnapshots = 64)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSnapshots);
        _store = store;
        _ownsStore = ownsStore;
        _maxSnapshots = maxSnapshots;
    }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_snapshots == _maxSnapshots)
            {
                throw new RegistryException(new("server_busy", "", "The persistence snapshot limit is exhausted."));
            }
            try
            {
                var generation = _store.ReadGeneration(cancellationToken);
                if (_cached is null || _cached.Generation != generation)
                {
                    var lease = _store.ReadMetadataSnapshot(cancellationToken);
                    Image replacement;
                    try
                    {
                        replacement = new Image(lease);
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                    _cached?.Release();
                    _cached = replacement;
                }
                _cached.Retain();
                _snapshots++;
                return ValueTask.FromResult<IRegistrySnapshot>(new Snapshot(this, _cached));
            }
            catch (StorageException exception)
            {
                throw Translate(exception);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration,
        IReadOnlyList<RegistryMutation> mutations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        if (mutations.Count > _store.Limits.MaxMutations)
        {
            throw new RegistryException(new("too_large", "", "The persistence mutation-count budget is exhausted."));
        }
        var changes = new List<StorageMutation>(mutations.Count);
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(mutation);
            if (mutation.IsDelete)
            {
                changes.Add(StorageMutation.Delete(mutation.Key));
                continue;
            }
            var bytes = Encoding.UTF8.GetBytes(mutation.Metadata!.RootElement.GetRawText());
            changes.Add(mutation.DocumentAction switch
            {
                RegistryDocumentAction.Preserve => StorageMutation.PutPreservingDocument(mutation.Key, bytes),
                RegistryDocumentAction.Replace => StorageMutation.Put(mutation.Key, bytes, mutation.Document!),
                _ => StorageMutation.Put(mutation.Key, bytes)
            });
        }
        try
        {
            var candidate = await _store.PrepareAsync(expectedGeneration, changes, cancellationToken).ConfigureAwait(false);
            return new Candidate(this, candidate);
        }
        catch (StorageException exception)
        {
            throw Translate(exception);
        }
    }

    private long Commit(StorageCandidate candidate, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var generation = _store.Commit(candidate, cancellationToken);
                _cached?.Release();
                _cached = null;
                return generation;
            }
            catch (StorageException exception)
            {
                throw Translate(exception);
            }
            catch (IOException exception)
            {
                throw new RegistryCommitOutcomeUnknownException(
                    "The persistence adapter could not establish commit acknowledgement; inspect after reopening.", exception);
            }
        }
    }

    private void Release(Image image)
    {
        lock (_gate)
        {
            image.Release();
            _snapshots--;
        }
    }

    private static Exception Translate(StorageException exception) => exception.Failure switch
    {
        StorageFailure.GenerationMismatch => new RegistryConcurrencyException(),
        StorageFailure.Busy => new RegistryException(new("server_busy", "", exception.Message), exception),
        StorageFailure.LimitExceeded => new RegistryException(new("too_large", "", exception.Message), exception),
        StorageFailure.CommitOutcomeUnknown or StorageFailure.UnusableStore =>
            new RegistryCommitOutcomeUnknownException(exception.Message, exception),
        _ => exception
    };

    /// <summary>Releases the adapter's cache and optionally its store; previously returned snapshots/streams retain their leases.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cached?.Release();
            _cached = null;
            if (_ownsStore)
            {
                _store.Dispose();
            }
        }
    }

    private sealed class Image
    {
        private int _references = 1;
        internal Image(StorageSnapshot snapshot)
        {
            Lease = snapshot;
            Records = snapshot.Records.Select(record =>
                new RegistryRecord(record.Key, RegistryJson.FromElement(record.Metadata), record.Document is not null))
                .ToFrozenDictionary(static record => record.Key, StringComparer.Ordinal);
            Children = Records.Values.GroupBy(static record =>
            {
                var separator = record.Key.LastIndexOf('/');
                return separator <= 0 ? "" : record.Key[..separator];
            }, StringComparer.Ordinal).ToFrozenDictionary(static group => group.Key,
                static group => group.OrderBy(static record => record.Key, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        }
        internal long Generation => Lease.Generation;
        internal StorageSnapshot Lease { get; }
        internal FrozenDictionary<string, RegistryRecord> Records { get; }
        internal FrozenDictionary<string, RegistryRecord[]> Children { get; }
        internal void Retain() => _references++;
        internal void Release()
        {
            if (--_references == 0)
            {
                Lease.Dispose();
            }
        }
    }

    private sealed class Snapshot(LocalRegistryPersistence owner, Image image) : IRegistrySnapshot
    {
        private int _disposed;
        public long Generation => image.Generation;
        public RegistryRecord? Find(string key)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return image.Records.GetValueOrDefault(key);
        }
        public IEnumerable<RegistryRecord> GetChildren(string collectionKey)
        {
            ArgumentNullException.ThrowIfNull(collectionKey);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return image.Children.TryGetValue(collectionKey, out var children) ? children.Select(static record => record) : [];
        }
        public IEnumerable<RegistryRecord> EnumerateRecords()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return image.Records.Values;
        }
        public Stream OpenDocument(string key, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return image.Lease.OpenDocument(key, cancellationToken);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(image);
            }
        }
    }

    private sealed class Candidate(LocalRegistryPersistence owner, StorageCandidate candidate) : IRegistryCommit
    {
        public ValueTask<long> CommitAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(owner.Commit(candidate, cancellationToken));
        public void Dispose() => candidate.Dispose();
    }
}
