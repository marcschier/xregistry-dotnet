// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Numerics;

namespace XRegistry.Publication;

/// <summary>A coherent read of one channel Resource Meta representation.</summary>
public sealed record PublicationChannelMetadata(string Scope, PublicationByteDescriptor Head, BigInteger Epoch);

/// <summary>A caller-authorized binding session for publication channel Meta and exact Version documents.</summary>
public interface IPublicationReadBinding : IAsyncDisposable
{
    /// <summary>Gets the exact binding access context served by this session.</summary>
    PublicationRegistryAccess Access { get; }

    /// <summary>Reads scope, head and epoch from one coherent channel Meta representation.</summary>
    ValueTask<PublicationChannelMetadata> ReadChannelMetadataAsync(string channelResource,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the exact document on the requested Version; the caller owns the returned stream.</summary>
    ValueTask<Stream> OpenVersionDocumentAsync(string version,
        CancellationToken cancellationToken = default);
}

/// <summary>Opens publication contexts through explicitly selected binding sessions.</summary>
/// <remarks>Required extension semantics that this reader cannot interpret are rejected.</remarks>
public sealed class PublicationReadClient
{
    private readonly Func<PublicationRegistryAccess, CancellationToken, ValueTask<IPublicationReadBinding>> _openBinding;
    private readonly long _maxDocumentBytes;

    /// <summary>Creates a reader with an explicit binding resolver and finite per-document byte limit.</summary>
    public PublicationReadClient(
        Func<PublicationRegistryAccess, CancellationToken, ValueTask<IPublicationReadBinding>> openBinding,
        long maxDocumentBytes = 8 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(openBinding);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDocumentBytes, 1);
        _openBinding = openBinding;
        _maxDocumentBytes = maxDocumentBytes;
    }

    /// <summary>Opens a channel once, then pins and verifies its exact manifest and Registry document.</summary>
    public async ValueTask<PublicationOpenedSnapshot> OpenChannelAsync(PublicationRegistryAccess registry,
        string channelResource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(channelResource);
        ValidateChannelResource(channelResource);
        cancellationToken.ThrowIfCancellationRequested();

        PublicationChannelMetadata metadata;
        var binding = await OpenBindingAsync(registry, cancellationToken).ConfigureAwait(false);
        await using (binding.ConfigureAwait(false))
        {
            if (!PublicationReferenceComparer.ResourceReferencesEqual(
                registry, channelResource, binding.Access, channelResource))
            {
                throw PublicationJson.Error("binding_mismatch", "/channel/reference/registry",
                    "The selected binding does not match the requested channel access context.");
            }

            metadata = await binding.ReadChannelMetadataAsync(channelResource, cancellationToken)
                .ConfigureAwait(false);
        }

        if (metadata is null || metadata.Head is null || metadata.Scope is null ||
            metadata.Epoch < BigInteger.Zero)
        {
            throw PublicationJson.Error("invalid_channel_meta", "/channel",
                "The channel Meta selection is incomplete or has a negative epoch.");
        }

        var manifestBytes = await ReadDescriptorAsync(metadata.Head, cancellationToken).ConfigureAwait(false);
        var manifest = PublicationManifest.Parse(manifestBytes);
        manifest.ValidateSnapshotDescriptor(metadata.Head);
        if (!string.Equals(metadata.Scope, manifest.Scope, StringComparison.Ordinal))
        {
            throw PublicationJson.Error("channel_manifest_mismatch", "/scope",
                "The selected channel scope does not match its retained manifest.");
        }

        RejectUnsupportedRequiredExtensions(manifest);
        var registryBytes = await ReadDescriptorAsync(manifest.Registry, cancellationToken).ConfigureAwait(false);
        var openRecord = PublicationOpenRecord.Create(manifest.Scope, metadata.Head, manifest.Registry,
            manifest.Completeness, new(registry, channelResource, metadata.Epoch));
        openRecord.ValidateAgainstManifest(manifest);
        return new(this, openRecord, manifest, manifestBytes, registryBytes);
    }

    /// <summary>Reopens an existing context through its pinned references without reading its channel again.</summary>
    public async ValueTask<PublicationOpenedSnapshot> OpenRecordAsync(PublicationOpenRecord openRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openRecord);
        cancellationToken.ThrowIfCancellationRequested();

        var manifestBytes = await ReadDescriptorAsync(openRecord.Snapshot, cancellationToken).ConfigureAwait(false);
        var manifest = PublicationManifest.Parse(manifestBytes);
        openRecord.ValidateAgainstManifest(manifest);
        RejectUnsupportedRequiredExtensions(manifest);
        var registryBytes = await ReadDescriptorAsync(openRecord.Registry, cancellationToken).ConfigureAwait(false);
        return new(this, openRecord, manifest, manifestBytes, registryBytes);
    }

    private static void RejectUnsupportedRequiredExtensions(PublicationManifest manifest)
    {
        if (manifest.RequiredExtensions.Count > 0)
        {
            throw PublicationJson.Error("unsupported_required_extension", "/requiredExtensions/0",
                $"The reader does not support required extension '{manifest.RequiredExtensions[0]}'.");
        }
    }

    internal async ValueTask<byte[]> ReadDescriptorAsync(PublicationByteDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.Size < BigInteger.Zero || descriptor.Size > _maxDocumentBytes ||
            descriptor.Size > int.MaxValue)
        {
            throw PublicationJson.Error("object_limit", "",
                "The publication document exceeds the configured byte limit.");
        }

        var expectedSize = (int)descriptor.Size;
        var binding = await OpenBindingAsync(descriptor.Reference.Registry, cancellationToken).ConfigureAwait(false);
        await using var bindingLifetime = binding.ConfigureAwait(false);
        if (!PublicationReferenceComparer.ReferencesEqual(
            new(binding.Access, descriptor.Reference.Version), descriptor.Reference))
        {
            throw PublicationJson.Error("binding_mismatch", "/reference/registry",
                "The selected binding does not match the exact Version reference.");
        }

        var stream = await binding.OpenVersionDocumentAsync(
            descriptor.Reference.Version, cancellationToken).ConfigureAwait(false);
        await using var streamLifetime = stream.ConfigureAwait(false);
        if (stream is null || !stream.CanRead)
        {
            throw new InvalidOperationException("The publication binding returned no readable Version document stream.");
        }

        var bytes = new byte[expectedSize];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw PublicationJson.Error("size_mismatch", "",
                    "The retrieved Version document ended before its declared byte size.");
            }

            read += count;
        }

        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw PublicationJson.Error("size_mismatch", "",
                "The retrieved Version document exceeds its declared byte size.");
        }

        PublicationIntegrity.Verify(bytes, descriptor);
        return bytes;
    }

    private async ValueTask<IPublicationReadBinding> OpenBindingAsync(
        PublicationRegistryAccess registry, CancellationToken cancellationToken)
    {
        var binding = await _openBinding(registry, cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            throw new InvalidOperationException("The publication binding resolver returned no binding session.");
        }

        return binding;
    }

    private static void ValidateChannelResource(string channelResource)
    {
        var path = RegistryPath.Parse(channelResource);
        if (path.Kind != RegistryPathKind.Resource ||
            !string.Equals(path.ResourceType, "channels", StringComparison.Ordinal))
        {
            throw PublicationJson.Error("invalid_channel_resource", "/channel/reference/resource",
                "A publication channel Resource XID is required.");
        }
    }
}

/// <summary>A verified snapshot context whose later reads use only its exact retained Version references.</summary>
public sealed class PublicationOpenedSnapshot
{
    private readonly PublicationReadClient _reader;
    private readonly byte[] _manifestBytes;
    private readonly byte[] _registryBytes;

    internal PublicationOpenedSnapshot(PublicationReadClient reader, PublicationOpenRecord openRecord,
        PublicationManifest manifest, byte[] manifestBytes, byte[] registryBytes)
    {
        _reader = reader;
        OpenRecord = openRecord;
        Manifest = manifest;
        _manifestBytes = (byte[])manifestBytes.Clone();
        _registryBytes = (byte[])registryBytes.Clone();
    }

    /// <summary>Gets the caller-visible context selected for this immutable snapshot.</summary>
    public PublicationOpenRecord OpenRecord { get; }

    /// <summary>Gets the checked publication manifest.</summary>
    public PublicationManifest Manifest { get; }

    /// <summary>Returns a copy of the exact selected manifest bytes.</summary>
    public byte[] GetExactManifestBytes() => (byte[])_manifestBytes.Clone();

    /// <summary>Returns a copy of the exact captured Registry JSON bytes.</summary>
    public byte[] GetExactRegistryBytes() => (byte[])_registryBytes.Clone();

    /// <summary>Reads and verifies the bytes associated with an exact view Version in this snapshot.</summary>
    public ValueTask<byte[]> ReadDocumentAsync(string viewVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewVersion);
        var identity = PublicationJson.GetVersionIdentity(viewVersion);
        var document = Manifest.Documents.SingleOrDefault(item =>
            string.Equals(PublicationJson.GetVersionIdentity(item.ViewVersion), identity, StringComparison.Ordinal));
        if (document is null)
        {
            throw PublicationJson.Error("object_not_found", "/viewVersion",
                "The selected Version has no document association in this publication.");
        }

        return _reader.ReadDescriptorAsync(document.Content, cancellationToken);
    }

    /// <summary>Reads and verifies a manifest extension document by its exact URI.</summary>
    public ValueTask<byte[]> ReadExtensionAsync(string extensionUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extensionUri);
        if (!Manifest.Extensions.TryGetValue(extensionUri, out var descriptor))
        {
            throw PublicationJson.Error("object_not_found", "/extensions",
                "The selected manifest has no document for this extension URI.");
        }

        return _reader.ReadDescriptorAsync(descriptor, cancellationToken);
    }
}
