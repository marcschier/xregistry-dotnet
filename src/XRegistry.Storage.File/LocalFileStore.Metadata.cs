// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace XRegistry.Storage.File;

public sealed partial class LocalFileStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly HashSet<StorageCandidate> _candidates = [];
    private long _preparedMetadataBytes;
    private bool _faulted;

    /// <summary>
    /// Copies and validates a complete set of changes against a storage generation.
    /// No registry model, entity Epoch or response-representation semantics are inferred.
    /// </summary>
    public async ValueTask<StorageCandidate> PrepareAsync(long expectedGeneration, IReadOnlyList<StorageMutation> mutations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedGeneration);
        ArgumentOutOfRangeException.ThrowIfEqual(expectedGeneration, long.MaxValue);
        using var operation = EnterOperation(cancellationToken);
        RequireGeneration(expectedGeneration);
        if (_candidates.Count >= _limits.MaxPreparedCandidates)
        {
            throw Limit("The prepared candidate count limit was exceeded.");
        }

        if (mutations.Count == 0 || mutations.Count > _limits.MaxMutations)
        {
            throw Limit("A candidate must contain between one and the configured maximum number of changes.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var prepared = new PreparedMutation[mutations.Count];
        long metadataBytes = 0;
        var tokens = 0;
        for (var index = 0; index < mutations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mutation = mutations[index];
            ArgumentNullException.ThrowIfNull(mutation);
            if (mutation.Document is { CanRead: false })
            {
                throw new ArgumentException("A supplied Document stream must be readable.", nameof(mutations));
            }
            ValidateKey(mutation.Key);
            if (!keys.Add(mutation.Key))
            {
                throw new ArgumentException("A candidate cannot change the same ordinal key twice.", nameof(mutations));
            }

            byte[]? json = null;
            if (!mutation.IsDelete)
            {
                if (mutation.MetadataJson.Length > _limits.MaxMetadataBytesPerRecord)
                {
                    throw Limit("The per-record metadata byte limit was exceeded.");
                }

                metadataBytes = AddWithin(metadataBytes, mutation.MetadataJson.Length, _limits.MaxMetadataBytes, "candidate metadata");
                AddWithin(_preparedMetadataBytes, metadataBytes, _limits.MaxPreparedMetadataBytes, "prepared metadata");
                json = mutation.MetadataJson.ToArray();
                ValidateJson(json, ref tokens);
            }

            prepared[index] = new PreparedMutation(mutation.Key, json)
            {
                Document = mutation.PreserveDocument ? ReadDocumentReference(mutation.Key) : null
            };
        }

        ValidateProjectedState(prepared);
        var retained = false;
        try
        {
            for (var index = 0; index < mutations.Count; index++)
            {
                if (mutations[index].Document is { } stream)
                {
                    prepared[index] = await StageDocumentAsync(prepared[index], stream, cancellationToken).ConfigureAwait(false);
                }
            }

            ValidateProjectedState(prepared);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var candidate = new StorageCandidate(this, expectedGeneration, prepared, metadataBytes);
                _candidates.Add(candidate);
                _preparedMetadataBytes += metadataBytes;
                retained = true;
                return candidate;
            }
        }
        finally
        {
            if (!retained)
            {
                DeleteStaged(prepared);
            }
        }
    }

    /// <summary>
    /// Synchronously publishes one candidate in a single SQLite transaction after rechecking its
    /// generation under the write transaction. Returns the new storage generation after durable commit.
    /// </summary>
    public long Commit(StorageCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        using var operation = EnterOperation(cancellationToken);
        if (!ReferenceEquals(candidate.Owner, this))
        {
            throw new ArgumentException("A candidate belongs to its preparing store instance.", nameof(candidate));
        }

        ObjectDisposedException.ThrowIf(candidate.IsDisposed, candidate);
        if (candidate.IsConsumed || !_candidates.Contains(candidate))
        {
            throw new InvalidOperationException("The candidate has already been consumed.");
        }

        using var transaction = _database.BeginTransaction(deferred: false);
        RequireGeneration(candidate.ExpectedGeneration, transaction);
        ValidateProjectedState(candidate.Mutations, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        candidate.IsConsumed = true;
        var commitAttempted = false;
        try
        {
            PublishDocuments(candidate.Mutations, transaction, cancellationToken);
            foreach (var mutation in candidate.Mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = _database.CreateCommand();
                command.Transaction = transaction;
                command.Parameters.AddWithValue("$key", mutation.Key);
                if (mutation.IsDelete)
                {
                    command.CommandText = "DELETE FROM records WHERE key = $key;";
                }
                else
                {
                    command.CommandText = "INSERT INTO records(key, metadata, document_digest) VALUES ($key, $metadata, $document) ON CONFLICT(key) DO UPDATE SET metadata = excluded.metadata, document_digest = excluded.document_digest;";
                    command.Parameters.Add("$metadata", SqliteType.Blob).Value = mutation.Metadata!;
                    command.Parameters.AddWithValue("$document", (object?)mutation.Document?.Sha256 ?? DBNull.Value);
                }

                command.ExecuteNonQuery();
            }

            StoreDatabase.Execute(_database, "DELETE FROM blobs WHERE digest NOT IN (SELECT document_digest FROM records WHERE document_digest IS NOT NULL);", transaction);
            using (var command = _database.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE store_state SET generation = generation + 1 WHERE singleton = 1 AND generation = $expected;";
                command.Parameters.AddWithValue("$expected", candidate.ExpectedGeneration);
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new StorageException(StorageFailure.GenerationMismatch, "The storage generation changed before publication.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            commitAttempted = true;
            transaction.Commit();
            StoreDatabase.Checkpoint(_database);
            return checked(candidate.ExpectedGeneration + 1);
        }
        catch (Exception error) when (commitAttempted && error is SqliteException or IOException)
        {
            _faulted = true;
            throw new StorageException(StorageFailure.CommitOutcomeUnknown, "Commit acknowledgement failed. Do not retry automatically; dispose and reopen to inspect the persisted generation.", error);
        }
        finally
        {
            RemoveCandidate(candidate);
        }
    }

    private void RequireGeneration(long expected, SqliteTransaction? transaction = null)
    {
        if (StoreDatabase.Generation(_database, transaction) != expected)
        {
            throw new StorageException(StorageFailure.GenerationMismatch, "The candidate does not match the current storage generation.");
        }
    }

    private void ValidateProjectedState(PreparedMutation[] mutations, SqliteTransaction? transaction = null)
    {
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        var documents = new Dictionary<string, DocumentReference>(StringComparer.Ordinal);
        using (var command = _database.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT r.key, length(r.metadata), r.document_digest, b.length FROM records r LEFT JOIN blobs b ON b.digest = r.document_digest;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (lengths.Count >= _limits.MaxRecords)
                {
                    throw Limit("The stored record count exceeds the configured limit.");
                }

                lengths.Add(reader.GetString(0), reader.GetInt64(1));
                if (!reader.IsDBNull(2))
                {
                    documents.Add(reader.GetString(0), new DocumentReference(reader.GetString(2), reader.GetInt64(3)));
                }
            }
        }

        foreach (var mutation in mutations)
        {
            if (mutation.IsDelete)
            {
                lengths.Remove(mutation.Key);
                documents.Remove(mutation.Key);
            }
            else
            {
                lengths[mutation.Key] = mutation.Metadata!.Length;
                if (mutation.Document is { } document)
                {
                    documents[mutation.Key] = document;
                }
                else
                {
                    documents.Remove(mutation.Key);
                }
            }
        }

        if (lengths.Count > _limits.MaxRecords)
        {
            throw Limit("The projected metadata record count limit was exceeded.");
        }

        long bytes = 0;
        foreach (var length in lengths.Values)
        {
            bytes = AddWithin(bytes, length, _limits.MaxMetadataBytes, "committed metadata");
        }

        if (documents.Count > _limits.MaxDocumentReferences)
        {
            throw Limit("The projected document reference count limit was exceeded.");
        }

        long documentBytes = 0;
        foreach (var document in documents.Values.DistinctBy(static document => document.Sha256))
        {
            documentBytes = AddWithin(documentBytes, document.Length,
                _limits.MaxReferencedDocumentBytes, "projected document bytes");
        }
    }

    private DocumentReference? ReadDocumentReference(string key)
    {
        using var command = _database.CreateCommand();
        command.CommandText = "SELECT r.document_digest, b.length FROM records r LEFT JOIN blobs b ON b.digest = r.document_digest WHERE r.key = $key;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }
        if (reader.IsDBNull(1))
        {
            throw new StorageException(StorageFailure.IntegrityFailure, "A preserved Document has no committed blob descriptor.");
        }
        return new DocumentReference(reader.GetString(0), reader.GetInt64(1));
    }

    private (StorageRecord[] Records, long Bytes) ReadRecords(
        CancellationToken cancellationToken, string? key = null, bool verifyDocuments = true)
    {
        var records = new List<StorageRecord>();
        long metadataBytes = 0;
        long retainedBytes = 0;
        long documentBytes = 0;
        var documentCount = 0;
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var tokens = 0;
        using var command = _database.CreateCommand();
        command.CommandText = "SELECT length(CAST(r.key AS BLOB)), r.key, length(r.metadata), r.metadata, r.document_digest, b.length FROM records r LEFT JOIN blobs b ON b.digest = r.document_digest" +
            (key is null ? ";" : " WHERE r.key = $key;");
        if (key is not null)
        {
            command.Parameters.AddWithValue("$key", key);
        }
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (records.Count >= _limits.MaxRecords || reader.GetInt64(0) > _limits.MaxKeyBytes ||
                reader.GetInt64(2) > _limits.MaxMetadataBytesPerRecord)
            {
                throw Limit("The stored metadata exceeds configured record or byte limits.");
            }

            var recordKey = reader.GetString(1);
            ValidateKey(recordKey);
            var length = reader.GetInt64(2);
            metadataBytes = AddWithin(metadataBytes, length, _limits.MaxMetadataBytes, "stored metadata");
            retainedBytes = AddWithin(retainedBytes, length + reader.GetInt64(0), _limits.MaxReadSnapshotBytes, "snapshot metadata");
            var bytes = reader.GetFieldValue<byte[]>(3);
            ValidateJson(bytes, ref tokens);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = _limits.MaxJsonDepth });
            DocumentReference? document = null;
            if (!reader.IsDBNull(4))
            {
                if (reader.IsDBNull(5))
                {
                    throw new StorageException(StorageFailure.IntegrityFailure, "Stored metadata refers to an absent document record.");
                }

                document = new DocumentReference(reader.GetString(4), reader.GetInt64(5));
                if (document.Length > _limits.MaxDocumentBytes)
                {
                    throw Limit("The stored Document exceeds its configured byte limit.");
                }
                if (++documentCount > _limits.MaxDocumentReferences)
                {
                    throw Limit("The stored document reference count limit was exceeded.");
                }

                if (verified.Add(document.Sha256))
                {
                    documentBytes = AddWithin(documentBytes, document.Length, _limits.MaxReferencedDocumentBytes, "referenced documents");
                    if (verifyDocuments)
                    {
                        using var content = OpenVerifiedBlob(document, cancellationToken);
                    }
                }
            }

            records.Add(new StorageRecord(recordKey, json.RootElement.Clone(), document));
        }

        records.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        return (records.ToArray(), retainedBytes);
    }

    private void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An opaque storage key cannot contain NUL.", nameof(key));
        }

        if (StrictUtf8.GetByteCount(key) > _limits.MaxKeyBytes)
        {
            throw Limit("The opaque-key UTF-8 byte limit was exceeded.");
        }
    }

    private void ValidateJson(ReadOnlySpan<byte> json, ref int tokens)
    {
        using var document = RegistryJson.ParseDocument(json, new RegistryJsonLimits
        {
            MaxBytes = _limits.MaxMetadataBytesPerRecord,
            MaxDepth = Math.Min(_limits.MaxJsonDepth, 256),
            MaxNodes = _limits.MaxJsonTokens
        });
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = _limits.MaxJsonDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ArgumentException("Metadata must be one well-formed JSON object.", nameof(json));
        }

        do
        {
            if (tokens >= _limits.MaxJsonTokens)
            {
                throw Limit("The JSON token work limit was exceeded.");
            }

            tokens++;
        }
        while (reader.Read());
    }

    private FileStream OpenVerifiedBlob(DocumentReference document, CancellationToken cancellationToken)
    {
        if (document.Length > _limits.MaxDocumentBytes)
        {
            throw Limit("The document byte limit was exceeded.");
        }

        var path = Path.Combine(_directories.Root, "blobs", document.Sha256 + ".blob");
        RequireFile(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != document.Length)
            {
                throw new StorageException(StorageFailure.IntegrityFailure, "The document length does not match its committed reference.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), document.Sha256, StringComparison.Ordinal))
            {
                throw new StorageException(StorageFailure.IntegrityFailure, "The document SHA-256 does not match its committed reference.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static long AddWithin(long current, long amount, long limit, string resource)
    {
        if (amount < 0 || current > limit || amount > limit - current)
        {
            throw Limit("The " + resource + " limit was exceeded.");
        }

        return current + amount;
    }

    private static StorageException Limit(string message) => new(StorageFailure.LimitExceeded, message);

    internal void ReleaseCandidate(StorageCandidate candidate)
    {
        lock (_sync)
        {
            if (!_activeOperation)
            {
                RemoveCandidate(candidate);
            }
        }
    }

    private void RemoveCandidate(StorageCandidate candidate)
    {
        if (_candidates.Remove(candidate))
        {
            DeleteStaged(candidate.Mutations);
            _preparedMetadataBytes -= candidate.MetadataBytes;
        }
    }

    private void RemoveDisposedCandidates()
    {
        foreach (var candidate in _candidates.Where(candidate => _disposed || candidate.IsDisposed).ToArray())
        {
            RemoveCandidate(candidate);
        }
    }
}
