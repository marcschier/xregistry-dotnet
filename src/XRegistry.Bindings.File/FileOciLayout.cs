using System.Security.Cryptography;
using System.Text.Json;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Bindings.File;

/// <summary>Observable checkpoints of an explicitly requested local OCI producer operation.</summary>
public enum FileOciLayoutPublicationStage
{
    /// <summary>The authorized local writer lease and storage checks succeeded.</summary>
    WriterLocked,
    /// <summary>Every required immutable object and its directory entries completed durability barriers.</summary>
    ObjectsDurable,
    /// <summary>The prepared, flushed entrypoint is about to replace the selected name.</summary>
    BeforeReferenceCommit,
    /// <summary>The entrypoint name was atomically replaced; acknowledgement is still provisional.</summary>
    ReferenceReplaced,
    /// <summary>The entrypoint and parent directory barriers completed.</summary>
    ReferenceDurable,
}

/// <summary>Finite local publication policy. Observer callbacks are trusted host code, never artifact content.</summary>
public sealed record FileOciLayoutPublicationOptions
{
    /// <summary>Cumulative exact-byte/object/work bounds, including verification of existing objects.</summary>
    public FederationReadLimits Limits { get; init; } = new(maxTotalBytes: 256 * 1024 * 1024,
        maxRequests: 32_768, maxObjects: 32_768, maxWork: 4_000_000);
    /// <summary>Maximum descriptors in the merged entrypoint, at most 256.</summary>
    public int MaxEntryPointDescriptors { get; init; } = 256;
    /// <summary>Maximum exact encoded entrypoint bytes, at most 1,048,576.</summary>
    public int MaxEntryPointBytes { get; init; } = 1_048_576;
    /// <summary>Finite publication/observer deadline; native filesystem barriers complete under the OS's I/O contract.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Optional trusted progress/fault/cancellation checkpoint. Exceptions abort acknowledgement.</summary>
    public Func<FileOciLayoutPublicationStage, CancellationToken, ValueTask>? Checkpoint { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntryPointDescriptors, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEntryPointDescriptors, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntryPointBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEntryPointBytes, 1_048_576);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(10)) { throw new ArgumentOutOfRangeException(nameof(Timeout)); }
    }
}

/// <summary>A receipt issued only after the selected local OCI reference completed durability barriers.</summary>
public sealed record FileOciLayoutPublicationResult
{
    internal FileOciLayoutPublicationResult(NativeRegistryContext context, string entryPointSha256, int written, int reused)
    {
        Context = context;
        EntryPointSha256 = entryPointSha256;
        ObjectsWritten = written;
        ObjectsReused = reused;
    }
    /// <summary>The File locator plus selected immutable OCI digest and requested reference.</summary>
    public NativeRegistryContext Context { get; }
    /// <summary>SHA-256 of the exact mutable entrypoint bytes, distinct from the Registry root digest.</summary>
    public string EntryPointSha256 { get; }
    /// <summary>New content-addressed files durably written by this operation.</summary>
    public int ObjectsWritten { get; }
    /// <summary>Existing content-addressed files verified and flushed without replacing their bytes.</summary>
    public int ObjectsReused { get; }
}

/// <summary>Explicit local OCI producer publication, separate from read-only File federation.</summary>
public static class FileOciLayout
{
    /// <summary>Publishes a complete package inside an existing approved NTFS/ext4 directory and acknowledges only a durable entrypoint.</summary>
    /// <remarks>Other processes must honor the directory writer lease. No arbitrary process with filesystem-owner privileges can be made unable to corrupt its own files.</remarks>
    public static async ValueTask<FileOciLayoutPublicationResult> PublishAsync(OciSnapshotPackage package, Uri destination,
        string reference, Uri? authorizedRoot = null, FileOciLayoutPublicationOptions? options = null,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new();
        options.Validate();
        budget ??= new(options.Limits);
        cancellationToken.ThrowIfCancellationRequested();
        var entrypoint = package.CreateLayoutEntryPoint(reference);
        budget.CheckObjectBytes(entrypoint.Length);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        Publisher? publisher = null;
        try
        {
            using var root = FileRegistryRoot.Open(destination, authorizedRoot, budget.Limits.MaxObjectBytes);
            using var storage = new FileOciLayoutNative(root.Reader, budget);
            publisher = new Publisher(storage, package, entrypoint, reference, options, budget, deadline.Token);
            await publisher.InitializeAsync().ConfigureAwait(false);
            await package.PublishAsync(publisher, reference, budget, deadline.Token).ConfigureAwait(false);
            var context = new NativeRegistryContext("file", destination.AbsoluteUri, package.RootDigest, true, package.RootDigest[7..])
            {
                RequestedRevision = reference,
            };
            return new(context, publisher.EntryPointHash!, publisher.Written, publisher.Reused);
        }
        catch (FederationException exception) when (publisher?.ReferenceMayBeVisible == true)
        {
            throw UnknownAcknowledgement(exception);
        }
        catch (OperationCanceledException exception) when (publisher?.ReferenceMayBeVisible == true)
        {
            throw UnknownAcknowledgement(exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "The local OCI publication deadline expired.",
                "publication_timeout", exception);
        }
        catch (IOException exception)
        {
            if (publisher?.ReferenceMayBeVisible == true) { throw UnknownAcknowledgement(exception); }
            throw new FederationException(FederationErrorCode.Unavailable, "Local OCI publication I/O failed.", innerException: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (publisher?.ReferenceMayBeVisible == true) { throw UnknownAcknowledgement(exception); }
            throw new FederationException(FederationErrorCode.PolicyDenied, "Local OCI publication access was denied.", innerException: exception);
        }
    }

    private static FederationException UnknownAcknowledgement(Exception exception) =>
        new(FederationErrorCode.Unavailable,
            "The OCI entrypoint may have changed, but durable publication acknowledgement is unknown.",
            "publication_ack_unknown", exception);

    private sealed class Publisher(FileOciLayoutNative storage, OciSnapshotPackage package, FederationDocument entrypoint, string selectedReference,
        FileOciLayoutPublicationOptions options, FederationReadBudget budget, CancellationToken cancellationToken) : IOciPublisher
    {
        private FileDocumentTreeReader? blobs;
        private FileDocumentTreeReader? digests;
        private byte[]? originalIndex;
        private byte[]? indexBytes;
        private byte[]? markerBytes;
        internal bool ReferenceMayBeVisible { get; private set; }
        internal string? EntryPointHash { get; private set; }
        internal int Written { get; private set; }
        internal int Reused { get; private set; }
        public NativeRegistryContext Context => storage.Root.Context;

        internal async ValueTask InitializeAsync()
        {
            await NotifyAsync(FileOciLayoutPublicationStage.WriterLocked).ConfigureAwait(false);
            markerBytes = await ReadAsync(storage.Root, "oci-layout", options.MaxEntryPointBytes).ConfigureAwait(false);
            originalIndex = await ReadAsync(storage.Root, "index.json", options.MaxEntryPointBytes).ConfigureAwait(false);
            if (markerBytes is not null)
            {
                var value = FileOciLayoutEntryPoint.Parse(markerBytes, options, budget, cancellationToken);
                if (!value.TryGetProperty("imageLayoutVersion", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "1.0.0")
                {
                    throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported OCI layout marker version.");
                }
            }
            else if (originalIndex is not null)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage, "An existing OCI entrypoint requires its layout marker.");
            }
            using (var input = entrypoint.OpenRead())
            using (var buffer = new MemoryStream())
            {
                await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                indexBytes = buffer.ToArray();
            }
            indexBytes = FileOciLayoutEntryPoint.Merge(originalIndex, indexBytes, selectedReference, options, budget, cancellationToken);
            blobs = storage.EnsureDirectory(storage.Root, "blobs");
            digests = storage.EnsureDirectory(blobs, "sha256");
        }

        public ValueTask PutBlobAsync(OciSnapshotObject blob, CancellationToken cancellationToken = default) => PutObjectAsync(blob, cancellationToken);
        public ValueTask PutManifestAsync(OciSnapshotObject manifest, CancellationToken cancellationToken = default) => PutObjectAsync(manifest, cancellationToken);

        private async ValueTask PutObjectAsync(OciSnapshotObject item, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (await VerifyExistingAsync(digests!, item.Digest[7..], item.Size, item.Digest).ConfigureAwait(false)) { Reused++; return; }
            using var input = item.OpenRead();
            var placed = await WriteAsync(digests!, item.Digest[7..], input, item.Size, item.Digest, replace: false).ConfigureAwait(false);
            if (placed) { Written++; }
            else
            {
                if (!await VerifyExistingAsync(digests!, item.Digest[7..], item.Size, item.Digest).ConfigureAwait(false))
                {
                    throw FileOciLayoutNative.Changed("A concurrently placed immutable OCI object disappeared.");
                }
                Reused++;
            }
        }

        public async ValueTask CommitReferenceAsync(string reference, OciSnapshotObject root, CancellationToken cancellationToken = default)
        {
            if (root.Digest != package.RootDigest) { throw new FederationException(FederationErrorCode.IntegrityError, "The publisher root changed."); }
            storage.Flush(digests!);
            storage.Flush(blobs!);
            storage.Flush(storage.Root);
            await NotifyAsync(FileOciLayoutPublicationStage.ObjectsDurable).ConfigureAwait(false);
            if (markerBytes is null)
            {
                var bytes = """{"imageLayoutVersion":"1.0.0"}"""u8.ToArray();
                using var content = new MemoryStream(bytes, writable: false);
                if (!await WriteAsync(storage.Root, "oci-layout", content, bytes.Length, Digest(bytes), replace: false).ConfigureAwait(false))
                {
                    throw FileOciLayoutNative.Changed("The OCI marker appeared during publication.");
                }
                storage.Flush(storage.Root);
                markerBytes = bytes;
            }
            await CheckControlsAsync().ConfigureAwait(false);
            using var input = new MemoryStream(indexBytes!, writable: false);
            if (!await WriteAsync(storage.Root, "index.json", input, indexBytes!.Length, Digest(indexBytes),
                replace: originalIndex is not null, entrypoint: true).ConfigureAwait(false))
            {
                throw FileOciLayoutNative.Changed("An OCI entrypoint appeared at commit.");
            }
            storage.Flush(storage.Root);
            storage.FlushParent();
            EntryPointHash = Digest(indexBytes)[7..];
            await NotifyAsync(FileOciLayoutPublicationStage.ReferenceDurable).ConfigureAwait(false);
        }

        private async ValueTask<bool> WriteAsync(FileDocumentTreeReader directory, string name, Stream input,
            long size, string digest, bool replace, bool entrypoint = false)
        {
            budget.CheckObjectBytes(size);
            var temporary = ".xregistry-oci-" + Guid.NewGuid().ToString("N") + ".tmp";
            using var handle = storage.CreateTemporary(directory, temporary);
            using var output = new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
            var placed = false;
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long count = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - count + 1)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) { break; }
                    budget.ChargeBytes(read);
                    budget.ChargeWork();
                    count += read;
                    if (count > size) { throw Integrity(); }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (count != size || "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != digest) { throw Integrity(); }
                output.Flush(flushToDisk: true);
                if (storage.VerifyFile(handle).Size != (ulong)size) { throw Integrity(); }
                if (entrypoint)
                {
                    await NotifyAsync(FileOciLayoutPublicationStage.BeforeReferenceCommit).ConfigureAwait(false);
                    await CheckControlsAsync().ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (entrypoint) { ReferenceMayBeVisible = true; }
                placed = storage.Place(handle, directory, temporary, name, replace);
                if (entrypoint && !placed) { ReferenceMayBeVisible = false; }
                if (placed && entrypoint)
                {
                    await NotifyAsync(FileOciLayoutPublicationStage.ReferenceReplaced).ConfigureAwait(false);
                }
                return placed;
            }
            finally { if (!placed) { FileOciLayoutNative.RemoveTemporary(handle, directory, temporary); } }
        }

        private async ValueTask<bool> VerifyExistingAsync(FileDocumentTreeReader directory, string name, long size, string digest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeRequest();
            using var handle = storage.OpenExisting(directory, name);
            if (handle is null) { return false; }
            var initial = storage.VerifyFile(handle);
            if (initial.Size != (ulong)size) { throw Integrity(); }
            budget.CheckObjectBytes(size);
            using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long count = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) { break; }
                count += read;
                budget.ChargeBytes(read);
                if (count > size) { throw Integrity(); }
                hash.AppendData(buffer, 0, read);
            }
            if (storage.VerifyFile(handle) != initial) { throw FileOciLayoutNative.Changed("An existing OCI blob changed during verification."); }
            if (count != size || "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != digest) { throw Integrity(); }
            FileOciLayoutNative.FlushFile(handle);
            return true;
        }

        private async ValueTask<byte[]?> ReadAsync(FileDocumentTreeReader directory, string name, int maxBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeRequest();
            using var handle = storage.OpenExisting(directory, name);
            if (handle is null) { return null; }
            budget.ChargeObject();
            var initial = storage.VerifyFile(handle);
            if (initial.Size > (ulong)maxBytes) { throw FileRegistryLocation.Limit("OCI entrypoint metadata exceeds its exact byte bound."); }
            budget.CheckObjectBytes((long)initial.Size);
            using var stream = new FileStream(handle, FileAccess.Read, 8192, isAsync: false);
            var bytes = new byte[(int)initial.Size];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) { throw FileOciLayoutNative.Changed("OCI entrypoint metadata was truncated during its read."); }
                budget.ChargeBytes(read);
                count += read;
            }
            if (storage.VerifyFile(handle) != initial) { throw FileOciLayoutNative.Changed("OCI entrypoint metadata changed during its read."); }
            FileOciLayoutNative.FlushFile(handle);
            return bytes;
        }

        private async ValueTask CheckControlsAsync()
        {
            var marker = await ReadAsync(storage.Root, "oci-layout", options.MaxEntryPointBytes).ConfigureAwait(false);
            var index = await ReadAsync(storage.Root, "index.json", options.MaxEntryPointBytes).ConfigureAwait(false);
            if (marker is null || !marker.AsSpan().SequenceEqual(markerBytes) ||
                (originalIndex is null ? index is not null : index is null || !index.AsSpan().SequenceEqual(originalIndex)))
            {
                throw FileOciLayoutNative.Changed("The OCI layout controls changed outside the selected writer lease.");
            }
        }

        private async ValueTask NotifyAsync(FileOciLayoutPublicationStage stage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (options.Checkpoint is not null)
            {
                await options.Checkpoint(stage, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static FederationException Integrity() => new(FederationErrorCode.IntegrityError, "An OCI publication object's exact size or SHA-256 does not match.");
}
