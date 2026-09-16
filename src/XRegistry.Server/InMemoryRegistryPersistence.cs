// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text;

namespace XRegistry.Server;

/// <summary>
/// Bounded transient persistence using structurally shared immutable indexes. It provides atomic
/// process-local publication and pinned snapshots, not crash durability.
/// </summary>
public sealed class InMemoryRegistryPersistence : IRegistryPersistence
{
    private readonly object _gate = new();
    private readonly int _maxRecords;
    private readonly long _maxBytes;
    private readonly int _maxDocumentBytes;
    private State _state = new(0, ImmutableDictionary<string, Entry>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImmutableSortedSet<string>>.Empty.WithComparers(StringComparer.Ordinal), 0);

    /// <summary>Creates a finite transient store; retained old snapshots remain the caller's responsibility.</summary>
    public InMemoryRegistryPersistence(int maxRecords = 100_000, long maxBytes = 256 * 1024 * 1024,
        int maxDocumentBytes = 8 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDocumentBytes, 1);
        _maxRecords = maxRecords;
        _maxBytes = maxBytes;
        _maxDocumentBytes = maxDocumentBytes;
    }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IRegistrySnapshot>(new Snapshot(_state));
        }
    }

    /// <inheritdoc />
    public async ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        cancellationToken.ThrowIfCancellationRequested();
        if (mutations.Count > _maxRecords)
        {
            throw ServerErrors.Create("operation_limit", "", "The candidate exceeds the persistence mutation-count budget.");
        }

        State state;
        lock (_gate)
        {
            state = _state;
            if (state.Generation != expectedGeneration)
            {
                throw new RegistryConcurrencyException();
            }
        }

        var entries = state.Entries;
        var children = state.Children;
        var bytes = state.Bytes;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(mutation);
            if (mutation.Key.Length > 8192)
            {
                throw ServerErrors.Create("operation_limit", "", "The opaque key exceeds its character budget.");
            }

            if (!keys.Add(mutation.Key))
            {
                throw new ArgumentException("A candidate must contain unique ordinal keys.", nameof(mutations));
            }

            var parent = Parent(mutation.Key);
            var siblings = children.GetValueOrDefault(parent) ?? ImmutableSortedSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            entries.TryGetValue(mutation.Key, out var previous);
            bytes -= previous?.Bytes ?? 0;
            if (mutation.IsDelete)
            {
                entries = entries.Remove(mutation.Key);
                siblings = siblings.Remove(mutation.Key);
            }
            else
            {
                var document = mutation.DocumentAction switch
                {
                    RegistryDocumentAction.Replace => await BoundedContent.ReadAsync(mutation.Document!,
                        _maxDocumentBytes, cancellationToken).ConfigureAwait(false),
                    RegistryDocumentAction.Preserve => previous?.Document,
                    _ => null
                };
                var record = new RegistryRecord(mutation.Key, mutation.Metadata!, document is not null);
                var size = Encoding.UTF8.GetByteCount(record.Metadata.RootElement.GetRawText()) +
                    Encoding.UTF8.GetByteCount(record.Key) + (long)(document?.Length ?? 0);
                entries = entries.SetItem(mutation.Key, new(record, document, size));
                siblings = siblings.Add(mutation.Key);
                bytes += size;
            }

            children = siblings.IsEmpty ? children.Remove(parent) : children.SetItem(parent, siblings);
            if (entries.Count > _maxRecords || bytes > _maxBytes)
            {
                throw ServerErrors.Create("operation_limit", mutation.Key, "The transient persistence quota is exhausted.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new Candidate(this, expectedGeneration, new(checked(expectedGeneration + 1), entries, children, bytes));
    }

    private static string Parent(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash <= 0 ? "" : key[..slash];
    }

    private sealed record Entry(RegistryRecord Record, byte[]? Document, long Bytes);
    private sealed record State(long Generation, ImmutableDictionary<string, Entry> Entries,
        ImmutableDictionary<string, ImmutableSortedSet<string>> Children, long Bytes);

    private sealed class Snapshot(State state) : IRegistrySnapshot
    {
        private bool _disposed;
        public long Generation => state.Generation;

        public RegistryRecord? Find(string key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return state.Entries.GetValueOrDefault(key)?.Record;
        }

        public IEnumerable<RegistryRecord> GetChildren(string collectionKey)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return state.Children.TryGetValue(collectionKey, out var keys)
                ? keys.Select(key => state.Entries[key].Record) : [];
        }

        public IEnumerable<RegistryRecord> EnumerateRecords()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return state.Entries.Values.Select(static entry => entry.Record);
        }

        public Stream OpenDocument(string key, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var entry = state.Entries.GetValueOrDefault(key) ?? throw new KeyNotFoundException("The record is absent.");
            var bytes = entry.Document ?? throw new InvalidOperationException("The record has no Document.");
            return new MemoryStream(bytes, writable: false);
        }

        public void Dispose() => _disposed = true;
    }

    private sealed class Candidate(InMemoryRegistryPersistence owner, long expectedGeneration, State state) : IRegistryCommit
    {
        private bool _disposed;
        private bool _committed;

        public ValueTask<long> CommitAsync(CancellationToken cancellationToken = default)
        {
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_committed)
                {
                    throw new InvalidOperationException("The candidate has already been committed.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (owner._state.Generation != expectedGeneration)
                {
                    throw new RegistryConcurrencyException();
                }

                owner._state = state;
                _committed = true;
                return ValueTask.FromResult(state.Generation);
            }
        }

        public void Dispose() => _disposed = true;
    }
}
