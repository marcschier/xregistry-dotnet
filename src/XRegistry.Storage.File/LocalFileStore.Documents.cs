// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using IOFile = System.IO.File;

namespace XRegistry.Storage.File;

public sealed partial class LocalFileStore
{
    private readonly Dictionary<FileStream, string> _documentStreams = [];

    private async ValueTask<PreparedMutation> StageDocumentAsync(
        PreparedMutation mutation, Stream source, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_directories.Root, "staging");
        var usage = DirectoryUsage(directory);
        if (usage.Count >= _limits.MaxTemporaryFiles)
        {
            throw Limit("The staging file count limit was exceeded.");
        }

        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".stage");
        var completed = false;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length = AddWithin(length, read, _limits.MaxDocumentBytes, "Document bytes");
                AddWithin(usage.Bytes, length, _limits.MaxTemporaryBytes, "staging bytes");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            var document = new DocumentReference(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
            completed = true;
            return mutation with { Document = document, StagingPath = path };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            if (!completed && IOFile.Exists(path))
            {
                IOFile.Delete(path);
            }
        }
    }

    private void PublishDocuments(
        PreparedMutation[] mutations, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_directories.Root, "blobs");
        var usage = DirectoryUsage(directory);
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutation.Document is not { } document)
            {
                continue;
            }

            var destination = Path.Combine(directory, document.Sha256 + ".blob");
            if (IOFile.Exists(destination))
            {
                using var existing = OpenVerifiedBlob(document, cancellationToken);
            }
            else
            {
                if (usage.Count >= _limits.MaxBlobFiles)
                {
                    throw Limit("The physical blob count limit was exceeded.");
                }

                usage = (usage.Count + 1, AddWithin(usage.Bytes, document.Length, _limits.MaxBlobBytes, "physical blob bytes"));
                if (mutation.StagingPath is null)
                {
                    throw new StorageException(StorageFailure.IntegrityFailure, "A prepared Document has no staged bytes.");
                }

                DirectoryDurability.EnsureRegularFile(mutation.StagingPath);
                DirectoryDurability.PlaceWithoutReplacement(mutation.StagingPath, destination);
            }

            using var command = _database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO blobs(digest, length) VALUES($digest, $length) ON CONFLICT(digest) DO NOTHING;";
            command.Parameters.AddWithValue("$digest", document.Sha256);
            command.Parameters.AddWithValue("$length", document.Length);
            command.ExecuteNonQuery();
        }

        _directories.Flush(directory);
        _directories.Flush(Path.Combine(_directories.Root, "staging"));
    }

    private static (int Count, long Bytes) DirectoryUsage(string directory)
    {
        var count = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            DirectoryDurability.EnsureRegularFile(path);
            count = checked(count + 1);
            bytes = checked(bytes + new FileInfo(path).Length);
        }

        return (count, bytes);
    }

    private static void DeleteStaged(IEnumerable<PreparedMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            if (mutation.StagingPath is { } path && IOFile.Exists(path))
            {
                DirectoryDurability.EnsureRegularFile(path);
                IOFile.Delete(path);
            }
        }
    }

    internal Stream OpenDocument(
        StorageSnapshot snapshot, DocumentReference document, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(snapshot.IsDisposed || !_snapshots.Contains(snapshot), snapshot);
            if (_documentStreams.Count >= _limits.MaxOpenDocumentStreams)
            {
                throw Limit("The open Document stream limit was exceeded.");
            }

            var stream = OpenVerifiedBlob(document, cancellationToken);
            _documentStreams.Add(stream, document.Sha256);
            return new DocumentLease(this, stream);
        }
    }

    private void ReleaseDocument(FileStream stream)
    {
        lock (_sync)
        {
            _documentStreams.Remove(stream);
            FinishDisposal();
        }
    }

    /// <summary>Removes a bounded number of unreferenced store-owned blob/staging files, preserving every active lease.</summary>
    public int CollectOrphans(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        using (var command = _database.CreateCommand())
        {
            command.CommandText = "SELECT digest FROM blobs;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                retained.Add(reader.GetString(0));
            }
        }

        var staging = _candidates.SelectMany(static candidate => candidate.Mutations)
            .Where(static mutation => mutation.StagingPath is not null)
            .Select(static mutation => mutation.StagingPath!)
            .ToHashSet(StringComparer.Ordinal);
        lock (_sync)
        {
            foreach (var snapshot in _snapshots)
            {
                retained.UnionWith(snapshot.Records.Where(static record => record.Document is not null)
                    .Select(static record => record.Document!.Sha256));
            }

            retained.UnionWith(_documentStreams.Values);
        }

        var removed = 0;
        foreach (var folder in new[] { "blobs", "staging" })
        {
            var directory = Path.Combine(_directories.Root, folder);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removed >= _limits.MaxOrphansPerCollection)
                {
                    break;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                var owned = folder == "blobs"
                    ? Path.GetExtension(path) == ".blob" && name.Length == 64 &&
                        name.All(static value => char.IsAsciiDigit(value) || value is >= 'a' and <= 'f') &&
                        !retained.Contains(name)
                    : Path.GetExtension(path) == ".stage" && Guid.TryParseExact(name, "N", out _) && !staging.Contains(path);
                if (owned)
                {
                    DirectoryDurability.EnsureRegularFile(path);
                    IOFile.Delete(path);
                    removed++;
                }
            }

            _directories.Flush(directory);
        }

        return removed;
    }

    private sealed class DocumentLease(LocalFileStore owner, FileStream stream) : Stream
    {
        private int _disposed;

        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => stream.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            stream.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            stream.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void Flush() => stream.Flush();
        public override void SetLength(long value) => throw new NotSupportedException("Document leases are read-only.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Document leases are read-only.");

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    stream.Dispose();
                }
                finally
                {
                    owner.ReleaseDocument(stream);
                }
            }

            base.Dispose(disposing);
        }
    }
}
