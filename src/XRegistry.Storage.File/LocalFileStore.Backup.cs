// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace XRegistry.Storage.File;

public sealed partial class LocalFileStore
{
    /// <summary>Writes a consistent independently reopenable backup to an existing empty local directory.</summary>
    /// <returns>The preserved source storage generation.</returns>
    /// <remarks>
    /// Copies only committed records and referenced immutable Documents. A ready marker is written last.
    /// Cancellation or failure can leave an explicitly incomplete destination; no existing data is removed.
    /// The source's single execution slot remains occupied until completion. Restore into a stopped deployment,
    /// not over an active writer; restoring older state is an explicit operator action.
    /// </remarks>
    public async ValueTask<long> CreateBackupAsync(string directory, CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        var generation = StoreDatabase.Generation(_database);
        var (records, _) = ReadRecords(cancellationToken);
        using var destination = DirectoryDurability.Open(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (destination.Root.Equals(_directories.Root, comparison) ||
            destination.Root.StartsWith(_directories.Root + Path.DirectorySeparatorChar, comparison) ||
            _directories.Root.StartsWith(destination.Root + Path.DirectorySeparatorChar, comparison))
        {
            throw new StorageException(StorageFailure.InvalidStore, "Backup and source directories must not overlap.");
        }

        if (Directory.EnumerateFileSystemEntries(destination.Root).Any())
        {
            throw new StorageException(StorageFailure.InvalidStore, "A backup destination must be empty; existing evidence is never overwritten.");
        }

        using var destinationLock = new FileStream(Path.Combine(destination.Root, "writer.lock"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            destinationLock.Lock(0, 1);
        }

        var identity = ReadMarker(_directories.Root, IdentityName);
        WriteMarker(destination, IdentityName, identity);
        Directory.CreateDirectory(Path.Combine(destination.Root, "blobs"));
        Directory.CreateDirectory(Path.Combine(destination.Root, "staging"));
        destination.RetainChild("blobs");
        destination.RetainChild("staging");
        destination.Flush(destination.Root);
        destination.FlushParent();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var document in records.Where(static record => record.Document is not null)
                .Select(static record => record.Document!).DistinctBy(static document => document.Sha256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = OpenVerifiedBlob(document, cancellationToken);
                using var target = new FileStream(
                    Path.Combine(destination.Root, "blobs", document.Sha256 + ".blob"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long written = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    written = AddWithin(written, read, document.Length, "backup Document bytes");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (written != document.Length ||
                    !Convert.ToHexString(hash.GetHashAndReset()).Equals(document.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StorageException(StorageFailure.IntegrityFailure, "A Document changed while being backed up.");
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        destination.Flush(Path.Combine(destination.Root, "blobs"));
        cancellationToken.ThrowIfCancellationRequested();
        using (var database = StoreDatabase.Open(destination.Root, identity, _limits, create: true))
        {
            try
            {
                _database.BackupDatabase(database);
                StoreDatabase.Validate(database, identity);
                if (StoreDatabase.Generation(database) != generation)
                {
                    throw new StorageException(StorageFailure.IntegrityFailure, "The backup generation changed during capture.");
                }

                StoreDatabase.Checkpoint(database);
            }
            catch (SqliteException exception)
            {
                throw new StorageException(StorageFailure.InvalidStore, "SQLite could not produce a verified backup.", exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        destination.Flush(destination.Root);
        WriteMarker(destination, ReadyName, identity);
        return generation;
    }
}
