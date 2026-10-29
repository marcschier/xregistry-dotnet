// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XRegistry.Publication;
using XRegistry.Server;

namespace XRegistry.Publication.Server;

/// <summary>Exact bytes retained for one descriptor in a publication snapshot.</summary>
public sealed class PublicationStoredObject
{
    private readonly byte[] _bytes;

    /// <summary>Creates a stored object after checking its declared size and digest.</summary>
    public PublicationStoredObject(PublicationByteDescriptor descriptor, ReadOnlySpan<byte> exactBytes)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        PublicationIntegrity.Verify(exactBytes, descriptor);
        Descriptor = descriptor;
        _bytes = exactBytes.ToArray();
    }

    /// <summary>Gets the reference, digest and size identifying the bytes.</summary>
    public PublicationByteDescriptor Descriptor { get; }

    /// <summary>Returns a copy of the exact source bytes.</summary>
    public byte[] GetExactBytes() => (byte[])_bytes.Clone();

    internal ReadOnlyMemory<byte> Content => _bytes;
}

/// <summary>An immutable manifest and the exact objects available for offline replay.</summary>
public sealed class PublicationBundle
{
    private readonly byte[] _manifestBytes;

    internal PublicationBundle(PublicationOpenRecord context, PublicationManifest manifest, byte[] manifestBytes,
        IReadOnlyList<PublicationStoredObject> objects)
    {
        Context = context;
        Manifest = manifest;
        _manifestBytes = (byte[])manifestBytes.Clone();
        Objects = Array.AsReadOnly(objects.ToArray());
    }

    /// <summary>Gets the open context selecting this immutable snapshot.</summary>
    public PublicationOpenRecord Context { get; }

    /// <summary>Gets the parsed manifest retaining its original bytes.</summary>
    public PublicationManifest Manifest { get; }

    /// <summary>Gets the retained immutable objects required by this offline-complete snapshot.</summary>
    public IReadOnlyList<PublicationStoredObject> Objects { get; }

    /// <summary>Returns a copy of the exact manifest bytes.</summary>
    public byte[] GetExactManifestBytes() => (byte[])_manifestBytes.Clone();

    /// <summary>Writes this offline-complete snapshot as a portable Publication package directory.</summary>
    public async ValueTask WritePackageAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Context.Completeness, "offline-complete", StringComparison.Ordinal) ||
            Context.Sources.Count != 0)
        {
            throw new PublicationException("bundle_incomplete", "/completeness",
                "Only a complete single-snapshot bundle can be written as a portable package.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (File.Exists(root))
        {
            throw new IOException("The Publication package destination is a file.");
        }

        if (Directory.Exists(root))
        {
            var existing = new DirectoryInfo(root);
            if (existing.LinkTarget is not null || Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new IOException("The Publication package destination must be a new or empty directory.");
            }
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var mappings = new List<PublicationPackageObject>();
        AddObject(Context.Snapshot, _manifestBytes);
        foreach (var item in Objects)
        {
            AddObject(item.Descriptor, item.GetExactBytes());
        }

        var package = PublicationPackage.Create(Context, mappings);
        Directory.CreateDirectory(root);
        foreach (var item in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, item.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, item.Value, cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllBytesAsync(Path.Combine(root, "publication-package.json"),
            package.GetExactBytes(), cancellationToken).ConfigureAwait(false);

        void AddObject(PublicationByteDescriptor descriptor, byte[] bytes)
        {
            PublicationIntegrity.Verify(bytes, descriptor);
            var path = "objects/" + descriptor.Digest["sha256:".Length..] + ".bin";
            if (files.TryGetValue(path, out var existingBytes))
            {
                if (!existingBytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new PublicationException("digest_collision", "",
                        "Different object bytes cannot share one package path.");
                }
            }
            else
            {
                files.Add(path, bytes);
            }

            mappings.Add(new PublicationPackageObject(descriptor.Reference, path));
        }
    }
}

/// <summary>A known channel epoch conflict; the attempted publication was not committed.</summary>
public sealed class PublicationChannelConflictException : InvalidOperationException
{
    /// <summary>Creates a conflict for the selected channel.</summary>
    public PublicationChannelConflictException(BigInteger expectedEpoch, BigInteger? actualEpoch)
        : base("The publication channel epoch changed.")
    {
        ExpectedEpoch = expectedEpoch;
        ActualEpoch = actualEpoch;
    }

    /// <summary>Gets the epoch supplied by the caller.</summary>
    public BigInteger ExpectedEpoch { get; }

    /// <summary>Gets the selected channel epoch, or null when the channel does not exist.</summary>
    public BigInteger? ActualEpoch { get; }
}

/// <summary>
/// Retains exact publication bytes and advances channel heads in the registry's atomic persistence
/// boundary. Durable guarantees depend on the supplied persistence implementation.
/// </summary>
public sealed class PublicationChannelStore
{
    private const string KeyPrefix = "publication/v1";
    private const string ChannelFormat = "xregistry-publication-channel/1";
    private readonly IRegistryPersistence _persistence;
    private readonly PublicationRegistryAccess _registry;
    private readonly int _maxDocumentBytes;

    /// <summary>Creates a publication store over the supplied registry persistence.</summary>
    public PublicationChannelStore(IRegistryPersistence persistence, PublicationRegistryAccess registry,
        int maxDocumentBytes = 8 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDocumentBytes, 1);
        _persistence = persistence;
        _registry = registry;
        _maxDocumentBytes = maxDocumentBytes;
    }

    /// <summary>
    /// Retains a manifest and its supplied objects, then advances the selected channel from the
    /// caller's expected epoch. The new artifacts and head become visible in one persistence commit.
    /// </summary>
    public async ValueTask<PublicationOpenRecord> PublishAsync(string channelResource,
        ReadOnlyMemory<byte> exactManifestBytes, IReadOnlyList<PublicationStoredObject> objects,
        BigInteger expectedEpoch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelResource);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedEpoch);
        if (_persistence.IsReadOnly)
        {
            throw new InvalidOperationException("The publication store requires writable registry persistence.");
        }

        var channelPath = ParseChannelResource(channelResource);
        var manifest = PublicationManifest.Parse(exactManifestBytes.Span);
        var manifestBytes = manifest.GetExactBytes();
        if (manifestBytes.Length > _maxDocumentBytes)
        {
            throw new PublicationException("object_limit", "",
                "The manifest exceeds the configured retained-object byte limit.");
        }

        var snapshotDescriptor = CreateSnapshotDescriptor(channelPath, manifest, manifestBytes);
        var content = ValidateContent(manifest, objects, snapshotDescriptor, manifestBytes);
        var channelKey = ChannelKey(channelResource);

        using var snapshot = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var current = ReadChannel(snapshot.Find(channelKey), channelResource);
        if (current is null)
        {
            if (expectedEpoch != BigInteger.Zero)
            {
                throw new PublicationChannelConflictException(expectedEpoch, null);
            }
        }
        else
        {
            if (!string.Equals(current.Scope, manifest.Scope, StringComparison.Ordinal))
            {
                throw new PublicationException("scope_immutable", "/scope",
                    "A channel's publication scope cannot change.");
            }

            if (current.Epoch != expectedEpoch)
            {
                throw new PublicationChannelConflictException(expectedEpoch, current.Epoch);
            }
        }

        var nextEpoch = expectedEpoch + BigInteger.One;
        var mutations = new List<RegistryMutation>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ObjectKey(item.Descriptor);
            if (!keys.Add(key))
            {
                throw new PublicationException("duplicate_reference", "",
                    "A publication cannot retain multiple objects for one reference.");
            }

            var existing = snapshot.Find(key);
            if (existing is not null)
            {
                await VerifyStoredObjectAsync(snapshot, existing, item.Descriptor, item.Content,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var metadata = CreateObjectMetadata(item.Descriptor);
            mutations.Add(RegistryMutation.PutDocument(key, metadata,
                new MemoryStream(item.Content.ToArray(), writable: false)));
        }

        var channelMetadata = CreateChannelMetadata(channelResource, manifest.Scope, manifest.Completeness,
            nextEpoch, snapshotDescriptor);
        mutations.Add(RegistryMutation.PutWithoutDocument(channelKey, channelMetadata));
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var candidate = await _persistence.PrepareAsync(snapshot.Generation, mutations, cancellationToken)
                .ConfigureAwait(false);
            await candidate.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RegistryConcurrencyException)
        {
            using var latest = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var actual = ReadChannel(latest.Find(channelKey), channelResource)?.Epoch;
            throw new PublicationChannelConflictException(expectedEpoch, actual);
        }

        return CreateOpenRecord(channelResource, manifest, snapshotDescriptor, nextEpoch);
    }

    /// <summary>Opens the currently selected channel head and verifies the retained manifest bytes.</summary>
    public async ValueTask<PublicationOpenRecord> OpenChannelAsync(string channelResource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelResource);
        ParseChannelResource(channelResource);
        using var snapshot = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var state = ReadChannel(snapshot.Find(ChannelKey(channelResource)), channelResource)
            ?? throw new KeyNotFoundException("The publication channel does not exist.");
        var descriptor = state.Snapshot;
        var storedManifest = snapshot.Find(ObjectKey(descriptor))
            ?? throw new PublicationException("snapshot_missing", "/snapshot",
                "The channel head does not have a retained manifest.");
        var manifestBytes = await ReadStoredBytesAsync(snapshot, storedManifest, descriptor, cancellationToken)
            .ConfigureAwait(false);
        var manifest = PublicationManifest.Parse(manifestBytes);
        manifest.ValidateSnapshotDescriptor(descriptor);
        if (!string.Equals(manifest.Scope, state.Scope, StringComparison.Ordinal) ||
            !string.Equals(manifest.Completeness, state.Completeness, StringComparison.Ordinal))
        {
            throw new PublicationException("channel_manifest_mismatch", "",
                "The retained manifest does not match the channel metadata.");
        }

        return CreateOpenRecord(state.Resource, manifest, descriptor, state.Epoch);
    }

    /// <summary>Reads and verifies every object named by an offline-complete channel head.</summary>
    public async ValueTask<PublicationBundle> ReplayOfflineBundleAsync(string channelResource,
        CancellationToken cancellationToken = default)
    {
        var open = await OpenChannelAsync(channelResource, cancellationToken).ConfigureAwait(false);
        var descriptor = open.Snapshot;
        using var snapshot = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var snapshotRecord = snapshot.Find(ObjectKey(descriptor))
            ?? throw new PublicationException("snapshot_missing", "/snapshot",
                "The channel head does not have a retained manifest.");
        var manifestBytes = await ReadStoredBytesAsync(snapshot, snapshotRecord, descriptor, cancellationToken)
            .ConfigureAwait(false);
        var manifest = PublicationManifest.Parse(manifestBytes);
        if (!string.Equals(manifest.Completeness, "offline-complete", StringComparison.Ordinal))
        {
            throw new PublicationException("bundle_incomplete", "/completeness",
                "Only an offline-complete publication can be replayed as a bundle.");
        }

        var descriptors = GetRequiredContent(manifest);
        var storedObjects = new List<PublicationStoredObject>(descriptors.Count);
        foreach (var required in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = snapshot.Find(ObjectKey(required))
                ?? throw new PublicationException("bundle_object_missing", "",
                    "An offline-complete publication is missing a referenced object.");
            var bytes = await ReadStoredBytesAsync(snapshot, record, required, cancellationToken)
                .ConfigureAwait(false);
            storedObjects.Add(new PublicationStoredObject(required, bytes));
        }

        return new PublicationBundle(open, manifest, manifestBytes, storedObjects);
    }

    private List<PublicationStoredObject> ValidateContent(PublicationManifest manifest,
        IReadOnlyList<PublicationStoredObject> objects, PublicationByteDescriptor snapshotDescriptor,
        byte[] manifestBytes)
    {
        var supplied = new Dictionary<string, PublicationStoredObject>(StringComparer.Ordinal);
        foreach (var item in objects)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Content.Length > _maxDocumentBytes)
            {
                throw new PublicationException("object_limit", "",
                    "A retained publication object exceeds the configured byte limit.");
            }

            var key = PublicationReferenceComparer.GetReferenceIdentityKey(item.Descriptor.Reference);
            if (!supplied.TryAdd(key, item))
            {
                throw new PublicationException("duplicate_reference", "",
                    "A publication cannot retain multiple objects for one reference.");
            }
        }

        var required = GetRequiredContent(manifest);
        var requiredByKey = required.ToDictionary(
            static descriptor => PublicationReferenceComparer.GetReferenceIdentityKey(descriptor.Reference),
            StringComparer.Ordinal);
        foreach (var item in supplied.Values)
        {
            var itemKey = PublicationReferenceComparer.GetReferenceIdentityKey(item.Descriptor.Reference);
            if (!requiredByKey.TryGetValue(itemKey, out var requiredDescriptor))
            {
                throw new PublicationException("unexpected_object", "",
                    "A retained object must be referenced by the publication manifest.");
            }

            if (!PublicationReferenceComparer.DescriptorsEqual(requiredDescriptor, item.Descriptor))
            {
                throw new PublicationException("descriptor_conflict", "",
                    "The supplied bytes do not match the descriptor in the publication manifest.");
            }
        }

        var registryKey = PublicationReferenceComparer.GetReferenceIdentityKey(manifest.Registry.Reference);
        if (!supplied.ContainsKey(registryKey))
        {
            throw new PublicationException("registry_object_missing", "/registry",
                "The Registry bytes named by the manifest must be supplied.");
        }

        if (string.Equals(manifest.Completeness, "offline-complete", StringComparison.Ordinal))
        {
            foreach (var requiredDescriptor in required)
            {
                if (!supplied.ContainsKey(PublicationReferenceComparer.GetReferenceIdentityKey(requiredDescriptor.Reference)))
                {
                    throw new PublicationException("bundle_object_missing", "",
                        "An offline-complete publication must supply every referenced object.");
                }
            }
        }

        var result = supplied.Values.ToList();
        result.Add(new PublicationStoredObject(snapshotDescriptor, manifestBytes));
        return result;
    }

    private static List<PublicationByteDescriptor> GetRequiredContent(PublicationManifest manifest)
    {
        var result = new List<PublicationByteDescriptor> { manifest.Registry };
        result.AddRange(manifest.Documents.Select(static document => document.Content));
        result.AddRange(manifest.Extensions.Values);
        var unique = new Dictionary<string, PublicationByteDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in result)
        {
            var key = PublicationReferenceComparer.GetReferenceIdentityKey(descriptor.Reference);
            if (unique.TryGetValue(key, out var existing))
            {
                if (!PublicationReferenceComparer.DescriptorsEqual(existing, descriptor))
                {
                    throw new PublicationException("descriptor_conflict", "",
                        "One reference cannot identify different bytes in a publication.");
                }
            }
            else
            {
                unique.Add(key, descriptor);
            }
        }

        return unique.Values.ToList();
    }

    private async ValueTask VerifyStoredObjectAsync(IRegistrySnapshot snapshot, RegistryRecord record,
        PublicationByteDescriptor descriptor, ReadOnlyMemory<byte> expectedBytes, CancellationToken cancellationToken)
    {
        if (!record.HasDocument || !TryReadObjectMetadata(record.Metadata, out var storedDescriptor))
        {
            throw new PublicationException("stored_object_mismatch", "",
                "The retained object metadata does not match its publication descriptor.");
        }

        if (!PublicationReferenceComparer.DescriptorsEqual(storedDescriptor, descriptor))
        {
            throw new PublicationException("immutable_object_conflict", "",
                "A retained publication reference already identifies different bytes.");
        }

        var bytes = await ReadStoredBytesAsync(snapshot, record, descriptor, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().SequenceEqual(expectedBytes.Span))
        {
            throw new PublicationException("immutable_object_conflict", "",
                "A retained publication reference already identifies different bytes.");
        }
    }

    private async ValueTask<byte[]> ReadStoredBytesAsync(IRegistrySnapshot snapshot, RegistryRecord record,
        PublicationByteDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!record.HasDocument || !TryReadObjectMetadata(record.Metadata, out var storedDescriptor) ||
            !PublicationReferenceComparer.DescriptorsEqual(storedDescriptor, descriptor) ||
            descriptor.Size > _maxDocumentBytes || descriptor.Size > int.MaxValue)
        {
            throw new PublicationException("stored_object_mismatch", "",
                "The retained object metadata does not match its publication descriptor.");
        }

        var bytes = new byte[(int)descriptor.Size];
        using var stream = snapshot.OpenDocument(record.Key, cancellationToken);
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new PublicationException("stored_object_truncated", "",
                    "The retained object ended before its declared byte size.");
            }

            read += count;
        }

        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new PublicationException("stored_object_oversized", "",
                "The retained object is larger than its declared byte size.");
        }

        PublicationIntegrity.Verify(bytes, descriptor);
        return bytes;
    }

    private PublicationOpenRecord CreateOpenRecord(string channelResource, PublicationManifest manifest,
        PublicationByteDescriptor snapshot, BigInteger epoch)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "xregistry-open/1");
            writer.WriteString("scope", manifest.Scope);
            writer.WritePropertyName("snapshot");
            WriteDescriptor(writer, snapshot);
            writer.WritePropertyName("registry");
            WriteDescriptor(writer, manifest.Registry);
            writer.WriteString("completeness", manifest.Completeness);
            writer.WritePropertyName("channel");
            writer.WriteStartObject();
            writer.WritePropertyName("reference");
            writer.WriteStartObject();
            writer.WritePropertyName("registry");
            WriteRegistry(writer);
            writer.WriteString("resource", channelResource);
            writer.WriteEndObject();
            writer.WritePropertyName("epoch");
            writer.WriteRawValue(epoch.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteStartArray("sources");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return PublicationOpenRecord.Parse(buffer.ToArray());
    }

    private static void WriteDescriptor(Utf8JsonWriter writer, PublicationByteDescriptor descriptor)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("reference");
        writer.WriteStartObject();
        writer.WritePropertyName("registry");
        WriteRegistry(writer, descriptor.Reference.Registry);
        writer.WriteString("version", descriptor.Reference.Version);
        writer.WriteEndObject();
        writer.WriteString("digest", descriptor.Digest);
        writer.WritePropertyName("size");
        writer.WriteRawValue(descriptor.Size.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private void WriteRegistry(Utf8JsonWriter writer) => WriteRegistry(writer, _registry);

    private static void WriteRegistry(Utf8JsonWriter writer, PublicationRegistryAccess registry)
    {
        writer.WriteStartObject();
        writer.WriteString("binding", registry.Binding);
        writer.WriteString("endpoint", registry.Endpoint);
        if (registry.Parameters.Count != 0)
        {
            writer.WriteStartObject("parameters");
            foreach (var parameter in registry.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(parameter.Key, parameter.Value);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static RegistryJson CreateObjectMetadata(PublicationByteDescriptor descriptor) =>
        RegistryJson.Create(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("format", "xregistry-publication-object/1");
            writer.WritePropertyName("descriptor");
            WriteDescriptor(writer, descriptor);
            writer.WriteEndObject();
        });

    private static RegistryJson CreateChannelMetadata(string channelResource, string scope, string completeness,
        BigInteger epoch, PublicationByteDescriptor snapshot) =>
        RegistryJson.Create(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("format", ChannelFormat);
            writer.WriteString("resource", channelResource);
            writer.WriteString("scope", scope);
            writer.WriteString("completeness", completeness);
            writer.WritePropertyName("epoch");
            writer.WriteRawValue(epoch.ToString(CultureInfo.InvariantCulture));
            writer.WritePropertyName("snapshot");
            WriteDescriptor(writer, snapshot);
            writer.WriteEndObject();
        });

    private static bool TryReadObjectMetadata(RegistryJson metadata, out PublicationByteDescriptor descriptor)
    {
        var root = metadata.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.String &&
            string.Equals(format.GetString(), "xregistry-publication-object/1", StringComparison.Ordinal) &&
            root.TryGetProperty("descriptor", out var value))
        {
            descriptor = PublicationByteDescriptor.Parse(value);
            return true;
        }

        descriptor = null!;
        return false;
    }

    private static ChannelState? ReadChannel(RegistryRecord? record, string requestedResource)
    {
        if (record is null)
        {
            return null;
        }

        var root = record.Metadata.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("format", out var format) ||
            format.ValueKind != JsonValueKind.String ||
            !string.Equals(format.GetString(), ChannelFormat, StringComparison.Ordinal) ||
            !root.TryGetProperty("resource", out var resource) ||
            resource.ValueKind != JsonValueKind.String ||
            !string.Equals(
                PublicationReferenceComparer.GetResourceIdentityKey(resource.GetString()!),
                PublicationReferenceComparer.GetResourceIdentityKey(requestedResource),
                StringComparison.Ordinal) ||
            !root.TryGetProperty("scope", out var scope) ||
            scope.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("completeness", out var completeness) ||
            completeness.ValueKind != JsonValueKind.String ||
            completeness.GetString() is not ("linked" or "offline-complete") ||
            !root.TryGetProperty("epoch", out var epochElement) ||
            epochElement.ValueKind != JsonValueKind.Number ||
            !BigInteger.TryParse(epochElement.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture,
                out var epoch) ||
            epoch < BigInteger.Zero ||
            !root.TryGetProperty("snapshot", out var snapshot))
        {
            throw new PublicationException("invalid_channel_record", "",
                "The persisted publication channel record is invalid.");
        }

        return new(resource.GetString()!, scope.GetString()!, completeness.GetString()!, epoch,
            PublicationByteDescriptor.Parse(snapshot));
    }

    private PublicationByteDescriptor CreateSnapshotDescriptor(RegistryPath channelPath,
        PublicationManifest manifest, ReadOnlySpan<byte> manifestBytes)
    {
        var resource = RegistryPath.ForVersion(channelPath.GroupType!, channelPath.GroupId!,
            "snapshots", RegistryId.Parse(manifest.SnapshotId), RegistryId.Parse("manifest")).ToXid();
        return new(new PublicationVersionReference(
                _registry, resource),
            "sha256:" + Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(), manifestBytes.Length);
    }

    private static RegistryPath ParseChannelResource(string channelResource)
    {
        var path = RegistryPath.Parse(channelResource);
        if (path.Kind != RegistryPathKind.Resource ||
            !string.Equals(path.ResourceType, "channels", StringComparison.Ordinal))
        {
            throw new PublicationException("invalid_channel_resource", "/channel/reference/resource",
                "A publication channel Resource XID is required.");
        }

        return path;
    }

    private static string ChannelKey(string channelResource) =>
        KeyPrefix + "/channels/" + Hash(PublicationReferenceComparer.GetResourceIdentityKey(channelResource));

    private static string ObjectKey(PublicationByteDescriptor descriptor) =>
        KeyPrefix + "/objects/" + PublicationReferenceComparer.GetReferenceIdentityKey(descriptor.Reference);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ChannelState(string Resource, string Scope, string Completeness,
        BigInteger Epoch, PublicationByteDescriptor Snapshot);
}
