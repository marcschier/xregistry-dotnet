// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opc.Ua;
using XRegistry;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>Session-local ObjectType mapping for one model-declared Resource collection.</summary>
public sealed record OpcUaResourceTypeBinding
{
    /// <summary>Creates a mapping for a ResourceType and its typed Versions component.</summary>
    public OpcUaResourceTypeBinding(NodeId resourceType, NodeId resourceVersionsType)
    {
        if (resourceType.IsNull || resourceVersionsType.IsNull)
        {
            throw new ArgumentException("Resource and ResourceVersions type NodeIds must be non-null.");
        }
        ResourceType = resourceType;
        ResourceVersionsType = resourceVersionsType;
        IsMetadataOnly = false;
    }

    private OpcUaResourceTypeBinding(NodeId metadataResourceType)
    {
        if (metadataResourceType.IsNull)
        {
            throw new ArgumentException("MetadataResourceType NodeId must be non-null.", nameof(metadataResourceType));
        }
        ResourceType = metadataResourceType;
        ResourceVersionsType = NodeId.Null;
        IsMetadataOnly = true;
    }

    /// <summary>Creates a mapping for a metadata-only ResourceType and its Metadata FileType view.</summary>
    public static OpcUaResourceTypeBinding ForMetadataOnly(NodeId metadataResourceType) =>
        new(metadataResourceType);

    /// <summary>The selected model's ResourceType, MetadataResourceType or subtype.</summary>
    public NodeId ResourceType { get; }
    /// <summary>The selected model's ResourceVersionsType or subtype, or Null for metadata-only mappings.</summary>
    public NodeId ResourceVersionsType { get; }
    /// <summary>Whether this mapping uses the metadata-only Resource contract.</summary>
    public bool IsMetadataOnly { get; }
}

/// <summary>Explicit mapping from xRegistry model collections to OPC UA ObjectTypes.</summary>
public sealed class OpcUaRegistryTypeMap
{
    /// <summary>Creates mappings. Collection names are exact, ordinal xRegistry model names.</summary>
    public OpcUaRegistryTypeMap(IReadOnlyDictionary<string, NodeId> groupTypes,
        IReadOnlyDictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding> resourceTypes,
        NodeId attributesType)
    {
        if (groupTypes is null || resourceTypes is null || attributesType.IsNull)
        {
            throw new ArgumentException("Type maps and a non-null AttributesType NodeId are required.");
        }

        var groups = new Dictionary<string, NodeId>(StringComparer.Ordinal);
        foreach (var entry in groupTypes)
        {
            ArgumentException.ThrowIfNullOrEmpty(entry.Key);
            if (entry.Value.IsNull)
            {
                throw new ArgumentException("GroupType NodeIds must be non-null.", nameof(groupTypes));
            }
            groups.Add(entry.Key, entry.Value);
        }

        var resources = new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>();
        foreach (var entry in resourceTypes)
        {
            ArgumentException.ThrowIfNullOrEmpty(entry.Key.GroupCollection);
            ArgumentException.ThrowIfNullOrEmpty(entry.Key.ResourceCollection);
            if (entry.Value is null)
            {
                throw new ArgumentException("Resource type mappings must be non-null.", nameof(resourceTypes));
            }
            resources.Add(entry.Key, entry.Value);
        }

        GroupTypes = new ReadOnlyDictionary<string, NodeId>(groups);
        ResourceTypes = new ReadOnlyDictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>(resources);
        AttributesType = attributesType;
    }

    /// <summary>The GroupType mapping by Group collection name.</summary>
    public IReadOnlyDictionary<string, NodeId> GroupTypes { get; }
    /// <summary>The Resource type mapping by Group and Resource collection names.</summary>
    public IReadOnlyDictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding> ResourceTypes { get; }
    /// <summary>The AttributesType used by Labels and MetaLabels Objects.</summary>
    public NodeId AttributesType { get; }
}

/// <summary>A bounded, read-only federation source over one already selected OPC UA Registry Session.</summary>
/// <remarks>The source owns neither the Session nor its connection. It materializes complete Core document views with local JSON Pointers.</remarks>
public sealed class OpcUaRegistryReadSource : IFederationReadSource
{
    private readonly OpcUaRegistryReader _reader;
    private readonly NodeId _registryRoot;
    private readonly OpcUaRegistryTypeMap _types;
    private readonly JsonElement _modelDocument;
    private int _reading;

    private OpcUaRegistryReadSource(OpcUaRegistryReader reader, NodeId registryRoot,
        NativeRegistryContext context, OpcUaRegistryTypeMap types, RegistryModel model,
        JsonElement modelDocument, JsonElement capabilities, string specVersion)
    {
        _reader = reader;
        _registryRoot = registryRoot;
        _types = types;
        Context = context;
        Model = model;
        _modelDocument = modelDocument.Clone();
        Capabilities = capabilities.Clone();
        SpecVersion = specVersion;
    }

    /// <inheritdoc />
    public NativeRegistryContext Context { get; }
    /// <inheritdoc />
    public JsonElement Capabilities { get; }
    /// <inheritdoc />
    public RegistryModel Model { get; }
    /// <summary>The accepted Core version reported by the selected Registry.</summary>
    public string SpecVersion { get; }

    /// <summary>Validates the Registry root, compiles its Model and captures its enabled Capabilities.</summary>
    public static async ValueTask<OpcUaRegistryReadSource> OpenAsync(OpcUaRegistryReader reader,
        NodeId registryRoot, NativeRegistryContext context, OpcUaRegistryTypeMap types,
        CancellationToken cancellationToken = default)
    {
        if (reader is null || context is null || types is null || registryRoot.IsNull)
        {
            throw new ArgumentException("A reader, selected Registry root and type map are required.");
        }
        if (!string.Equals(context.Binding, "opcua", StringComparison.Ordinal))
        {
            throw new ArgumentException("The source context binding must be 'opcua'.", nameof(context));
        }

        var registry = await reader.ReadRegistryPropertiesAsync(registryRoot, cancellationToken).ConfigureAwait(false);
        using var modelDocument = await reader.ReadModelAsync(registryRoot, cancellationToken).ConfigureAwait(false);
        var model = reader.CompileModelDefinition(modelDocument.RootElement);
        using var capabilitiesDocument = await reader.ReadCapabilitiesAsync(registryRoot, cancellationToken)
            .ConfigureAwait(false);
        var capabilities = capabilitiesDocument.RootElement.Clone();
        _ = FederationCapabilities.GetResolutionOwner(capabilities);
        return new(reader, registryRoot, context, types, model,
            modelDocument.RootElement, capabilities, registry.GetProperty("specversion").GetString()!);
    }

    /// <summary>
    /// Resolves an external NodeId only through a caller-supplied trusted-provider lease.
    /// The returned result owns the lease and must remain alive while its read source is used.
    /// </summary>
    public static async ValueTask<OpcUaResolvedExternalResource> ResolveExternalTargetAsync(
        OpcUaExternalNodeId identity, OpcUaTrustedProviderResolver trustedProviderResolver,
        Func<string, bool> isSupportedSpecVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(trustedProviderResolver);
        ArgumentNullException.ThrowIfNull(isSupportedSpecVersion);

        var lease = await trustedProviderResolver(identity, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            throw new FederationException(FederationErrorCode.Unavailable,
                "The trusted-provider resolver returned no target lease.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reader = lease.Reader;
            var context = lease.Context;
            var types = lease.Types;
            var registryRoot = lease.RegistryRoot;
            var serverApplicationUri = lease.ServerApplicationUri;
            var leaseSpecVersion = lease.SpecVersion;
            if (reader is null || context is null || types is null || registryRoot.IsNull ||
                string.IsNullOrEmpty(serverApplicationUri))
            {
                throw new FederationException(FederationErrorCode.Unavailable,
                    "The trusted-provider resolver returned an incomplete target lease.");
            }

            var root = await reader.VerifyRegistryRootAsync(registryRoot, cancellationToken)
                .ConfigureAwait(false);
            var serverArray = await reader.Session.ReadServerArrayAsync(cancellationToken).ConfigureAwait(false);
            if (serverArray.Count == 0 ||
                !string.Equals(serverArray[0], serverApplicationUri, StringComparison.Ordinal))
            {
                throw new FederationException(FederationErrorCode.PolicyDenied,
                    "The target Session ServerArray does not match the trusted-provider lease identity.");
            }

            var namespaces = await reader.Session.ReadNamespaceArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            OpcUaRegistryReader.ValidateServerNamespaceIdentity(serverArray, namespaces);
            var acceptedVersion = await reader.ReadSpecVersionAsync(registryRoot,
                isSupportedSpecVersion, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(acceptedVersion, leaseSpecVersion, StringComparison.Ordinal))
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The target Registry SpecVersion changed after its Session was opened.");
            }

            var targetSource = await OpenAsync(reader, root, context, types, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(targetSource.SpecVersion, acceptedVersion, StringComparison.Ordinal))
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The target Registry SpecVersion changed while its model and Capabilities were read.");
            }
            var targetNode = identity.MapToLocalNodeId(serverApplicationUri, namespaces);
            var targetXid = await reader.ReadStringComponentAsync(targetNode, "Xid", cancellationToken)
                .ConfigureAwait(false);
            var target = new FederationReadRequest(FederationOperation.Entity, targetXid);
            var parts = ParseParts(target.Target);
            if (parts.Length is not (4 or 6) || parts.Length == 6 && parts[4] != "versions")
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The external NodeId does not identify a Resource or Version XID.");
            }

            var groupNode = await reader.ResolveGroupNodeAsync(root, parts[0], parts[1],
                targetSource.GroupType(parts[0]), cancellationToken).ConfigureAwait(false);
            var binding = targetSource.ResourceType(parts[0], parts[2]);
            var versionId = parts.Length == 6 ? parts[5] : null;
            var resolvedNode = binding.IsMetadataOnly
                ? versionId is null
                    ? await reader.ResolveMetadataResourceNodeAsync(groupNode, XidOf(parts[..2]), parts[2],
                        parts[3], binding.ResourceType, cancellationToken).ConfigureAwait(false)
                    : throw new FederationException(FederationErrorCode.InvalidPackage,
                        "A metadata-only Resource cannot contain a Version.")
                : await reader.ResolveResourceNodeAsync(groupNode, XidOf(parts[..2]), parts[2], parts[3],
                    binding.ResourceType, versionId, binding.ResourceVersionsType, cancellationToken)
                    .ConfigureAwait(false);

            if (resolvedNode != targetNode)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The external NodeId does not match the Resource or Version selected by its XID.");
            }

            return new OpcUaResolvedExternalResource(lease, targetSource, resolvedNode, target.Target,
                acceptedVersion);
        }
        catch (Exception failure)
        {
            await DisposeFailedLeaseAsync(lease, failure).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask DisposeFailedLeaseAsync(IOpcUaTrustedProviderLease lease, Exception failure)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposalFailure)
        {
            failure.Data["OpcUa.TrustedProviderDisposeFailure"] = disposalFailure;
        }
    }

    /// <inheritdoc />
    public async ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException("An OPC UA source permits only one active read.");
        }

        try
        {
            if (request.Operation == FederationOperation.Model)
            {
                return Metadata("/", new JsonObject
                {
                    ["model"] = JsonNode.Parse(_modelDocument.GetRawText())
                });
            }
            if (request.Operation == FederationOperation.Capabilities)
            {
                return Metadata("/", JsonNode.Parse(Capabilities.GetRawText())!.AsObject());
            }
            if (request.Operation == FederationOperation.Document)
            {
                return await ReadDocumentAsync(request, false, cancellationToken).ConfigureAwait(false);
            }
            if (request.Representation != FederationRepresentation.DocumentView)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "The OPC UA source can materialize the Core document view but has no retrievable Core API URL.");
            }

            var parts = ParseParts(request.Target);
            if (request.Operation == FederationOperation.Entity)
            {
                ValidateEntityPath(parts);
                var entity = await BuildEntityAsync(parts, "/entity", cancellationToken).ConfigureAwait(false);
                return Metadata(request.Target, new JsonObject
                {
                    ["kind"] = Kind(parts.Length),
                    ["entity"] = entity
                });
            }
            if (request.Operation == FederationOperation.Collection)
            {
                ValidateCollectionPath(parts);
                var collection = await BuildCollectionAsync(parts, cancellationToken).ConfigureAwait(false);
                if (request.Selector is null)
                {
                    return Metadata(request.Target, new JsonObject
                    {
                        ["kind"] = "collection",
                        ["xid"] = request.Target,
                        ["complete"] = true,
                        ["entities"] = collection
                    });
                }

                var matches = new List<KeyValuePair<string, JsonObject>>();
                foreach (var member in collection)
                {
                    using var document = JsonDocument.Parse(member.Value!.ToJsonString());
                    var labels = document.RootElement.TryGetProperty("labels", out var value) ? value : default;
                    if (request.Selector.Matches(labels))
                    {
                        matches.Add(new(member.Key, JsonNode.Parse(member.Value.ToJsonString())!.AsObject()));
                    }
                }
                if (matches.Count != 1)
                {
                    throw new FederationException(matches.Count == 0 ? FederationErrorCode.NotFound :
                        FederationErrorCode.Ambiguous,
                        "The complete OPC UA collection does not contain exactly one literal label match.");
                }

                var selected = matches[0];
                RebasePointers(selected.Value, "/entities/" + PointerToken(selected.Key), "/entity");
                var selectedParts = parts.Append(selected.Key).ToArray();
                return Metadata(XidOf(selectedParts), new JsonObject
                {
                    ["kind"] = Kind(selectedParts.Length),
                    ["entity"] = selected.Value
                });
            }

            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The OPC UA source does not support this abstract read operation.");
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    private async ValueTask<JsonObject> BuildEntityAsync(string[] parts, string pointer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parts.Length == 0)
        {
            var entity = Object(await _reader.ReadRegistryPropertiesAsync(_registryRoot, cancellationToken)
                .ConfigureAwait(false));
            entity["self"] = Fragment(pointer);
            foreach (var groupCollection in Model.Groups.Keys)
            {
                var members = await BuildCollectionAsync([groupCollection], cancellationToken, pointer)
                    .ConfigureAwait(false);
                entity[groupCollection] = members;
                entity[groupCollection + "url"] = Fragment(pointer + "/" + PointerToken(groupCollection));
            }
            return entity;
        }
        if (parts.Length == 2)
        {
            var groupCollection = parts[0];
            var groupId = parts[1];
            var groupType = GroupType(groupCollection);
            var groupNode = await _reader.ResolveGroupNodeAsync(_registryRoot, groupCollection, groupId,
                groupType, cancellationToken).ConfigureAwait(false);
            var entity = Object(await _reader.ReadGroupPropertiesAsync(_registryRoot, groupCollection,
                groupId, groupType, cancellationToken).ConfigureAwait(false));
            AddLabels(entity, await _reader.ReadAttributesAsync(groupNode, _types.AttributesType, cancellationToken)
                .ConfigureAwait(false));
            entity["self"] = Fragment(pointer);
            foreach (var resourceCollection in GroupDefinition(groupCollection).Resources.Keys)
            {
                var members = await BuildCollectionAsync([.. parts, resourceCollection], cancellationToken, pointer)
                    .ConfigureAwait(false);
                entity[resourceCollection] = members;
                entity[resourceCollection + "url"] = Fragment(pointer + "/" + PointerToken(resourceCollection));
            }
            return entity;
        }
        if (parts.Length == 4)
        {
            return await BuildResourceAsync(parts, pointer, cancellationToken).ConfigureAwait(false);
        }
        if (parts.Length == 5 && parts[4] == "meta")
        {
            var (groupNode, binding) = await ResolveResourceParentAsync(parts, cancellationToken).ConfigureAwait(false);
            if (binding.IsMetadataOnly)
            {
                var resource = Object(await _reader.ReadMetadataResourceAsync(groupNode, XidOf(parts[..2]),
                    parts[2], parts[3], binding.ResourceType, cancellationToken: cancellationToken)
                    .ConfigureAwait(false));
                if (resource["meta"] is not JsonObject metadata)
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation,
                        "The metadata-only Resource has no declared Resource Meta projection.");
                }
                metadata["self"] = Fragment(pointer);
                return (JsonObject)metadata.DeepClone();
            }
            var meta = Object(await _reader.ReadResourceMetaPropertiesAsync(groupNode, XidOf(parts[..2]),
                parts[2], parts[3], binding.ResourceType, cancellationToken).ConfigureAwait(false));
            AddLabels(meta, await _reader.ReadResourceMetaAttributesAsync(groupNode, XidOf(parts[..2]),
                parts[2], parts[3], binding.ResourceType, _types.AttributesType, cancellationToken)
                .ConfigureAwait(false));
            meta["self"] = Fragment(pointer);
            return meta;
        }
        if (parts.Length == 6 && parts[4] == "versions")
        {
            return await BuildVersionAsync(parts, pointer, cancellationToken).ConfigureAwait(false);
        }

        throw new FederationException(FederationErrorCode.UnsupportedOperation,
            "The OPC UA source does not support this entity path shape.");
    }

    private async ValueTask<JsonObject> BuildResourceAsync(string[] parts, string pointer,
        CancellationToken cancellationToken)
    {
        var (groupNode, binding) = await ResolveResourceParentAsync(parts, cancellationToken).ConfigureAwait(false);
        var xid = XidOf(parts);
        if (binding.IsMetadataOnly)
        {
            var metadataEntity = Object(await _reader.ReadMetadataResourceAsync(groupNode, XidOf(parts[..2]),
                parts[2], parts[3], binding.ResourceType, cancellationToken: cancellationToken)
                .ConfigureAwait(false));
            if (metadataEntity["meta"] is JsonObject metadata)
            {
                metadata["self"] = Fragment(pointer + "/meta");
                metadataEntity["metaurl"] = Fragment(pointer + "/meta");
            }
            metadataEntity["self"] = Fragment(pointer);
            return metadataEntity;
        }
        var entity = new JsonObject();
        var meta = Object(await _reader.ReadResourceMetaPropertiesAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, cancellationToken).ConfigureAwait(false));
        AddLabels(meta, await _reader.ReadResourceMetaAttributesAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, _types.AttributesType, cancellationToken)
            .ConfigureAwait(false));
        if (meta.ContainsKey("xref"))
        {
            var alias = await ResolveXrefAsync(meta["xref"]!.GetValue<string>(), binding, cancellationToken)
                .ConfigureAwait(false);
            var targetParts = alias;
            var targetGroupNode = await _reader.ResolveGroupNodeAsync(_registryRoot, targetParts[0],
                targetParts[1], GroupType(targetParts[0]), cancellationToken).ConfigureAwait(false);
            var targetBinding = ResourceType(targetParts[0], targetParts[2]);
            var targetMetadata = Object(await _reader.ReadResourceMetaPropertiesAsync(targetGroupNode,
                XidOf(targetParts[..2]), targetParts[2], targetParts[3], targetBinding.ResourceType, cancellationToken)
                .ConfigureAwait(false));
            if (targetMetadata.ContainsKey("xref"))
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "An OPC UA Resource Meta xref cannot be resolved through a second alias.");
            }
            var delegated = Object(await _reader.ReadResourcePropertiesAsync(targetGroupNode, XidOf(targetParts[..2]),
                targetParts[2], targetParts[3], targetBinding.ResourceType,
                cancellationToken: cancellationToken).ConfigureAwait(false));
            delegated.Remove("versionid");
            delegated["resourceid"] = parts[3];
            delegated["xid"] = xid;
            entity = delegated;
            var targetResource = await _reader.ResolveResourceNodeAsync(targetGroupNode, XidOf(targetParts[..2]),
                targetParts[2], targetParts[3], targetBinding.ResourceType, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AddLabels(entity, await _reader.ReadAttributesAsync(targetResource, _types.AttributesType, cancellationToken)
                .ConfigureAwait(false));
            entity["meta"] = meta;
            entity["metaurl"] = Fragment(pointer + "/meta");
            entity["self"] = Fragment(pointer);
            return entity;
        }

        entity = Object(await _reader.ReadResourcePropertiesAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, cancellationToken: cancellationToken).ConfigureAwait(false));
        entity.Remove("versionid");
        var logicalResource = await _reader.ResolveResourceNodeAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, cancellationToken: cancellationToken).ConfigureAwait(false);
        AddLabels(entity, await _reader.ReadAttributesAsync(logicalResource, _types.AttributesType, cancellationToken)
            .ConfigureAwait(false));

        var defaultVersionId = meta["defaultversionid"]?.GetValue<string>();
        if (string.IsNullOrEmpty(defaultVersionId))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "A non-alias Resource Meta has no defaultversionid.");
        }
        var versions = await BuildCollectionAsync([.. parts, "versions"], cancellationToken, pointer)
            .ConfigureAwait(false);
        if (!versions.ContainsKey(defaultVersionId))
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "Resource Meta selects a Version absent from the complete Versions collection.");
        }
        meta["self"] = Fragment(pointer + "/meta");
        entity["meta"] = meta;
        entity["metaurl"] = Fragment(pointer + "/meta");
        entity["versions"] = versions;
        entity["versionsurl"] = Fragment(pointer + "/versions");
        meta["defaultversionurl"] = Fragment(pointer + "/versions/" + PointerToken(defaultVersionId));
        entity["self"] = Fragment(pointer);
        return entity;
    }

    private async ValueTask<JsonObject> BuildVersionAsync(string[] parts, string pointer,
        CancellationToken cancellationToken)
    {
        var (groupNode, binding) = await ResolveResourceParentAsync(parts, cancellationToken).ConfigureAwait(false);
        if (binding.IsMetadataOnly)
        {
            var metadataVersion = Object(await _reader.ReadMetadataResourceAsync(groupNode, XidOf(parts[..2]),
                parts[2], parts[3], binding.ResourceType, parts[5], cancellationToken)
                .ConfigureAwait(false));
            metadataVersion["isdefault"] = true;
            metadataVersion["self"] = Fragment(pointer);
            return metadataVersion;
        }
        var version = Object(await _reader.ReadResourcePropertiesAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, parts[5], binding.ResourceVersionsType, cancellationToken)
            .ConfigureAwait(false));
        var exactVersion = await _reader.ResolveResourceNodeAsync(groupNode, XidOf(parts[..2]), parts[2],
            parts[3], binding.ResourceType, parts[5], binding.ResourceVersionsType, cancellationToken)
            .ConfigureAwait(false);
        AddLabels(version, await _reader.ReadAttributesAsync(exactVersion, _types.AttributesType, cancellationToken)
            .ConfigureAwait(false));
        var meta = await _reader.ReadResourceMetaPropertiesAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, cancellationToken).ConfigureAwait(false);
        version["isdefault"] = meta.TryGetProperty("defaultversionid", out var defaultId) &&
            defaultId.ValueKind == JsonValueKind.String &&
            string.Equals(defaultId.GetString(), parts[5], StringComparison.Ordinal);
        version["self"] = Fragment(pointer);
        return version;
    }

    private async ValueTask<JsonObject> BuildCollectionAsync(string[] parts, CancellationToken cancellationToken,
        string parentPointer = "")
    {
        JsonElement members;
        if (parts.Length == 1)
        {
            members = await _reader.ReadGroupCollectionAsync(_registryRoot, parts[0], GroupType(parts[0]),
                cancellationToken).ConfigureAwait(false);
        }
        else if (parts.Length == 3)
        {
            var groupNode = await _reader.ResolveGroupNodeAsync(_registryRoot, parts[0], parts[1],
                GroupType(parts[0]), cancellationToken).ConfigureAwait(false);
            var binding = ResourceType(parts[0], parts[2]);
            members = binding.IsMetadataOnly
                ? await _reader.ReadMetadataResourceCollectionAsync(groupNode, XidOf(parts[..2]), parts[2],
                    binding.ResourceType, cancellationToken).ConfigureAwait(false)
                : await _reader.ReadResourceCollectionAsync(groupNode, XidOf(parts[..2]), parts[2],
                    binding.ResourceType, cancellationToken).ConfigureAwait(false);
        }
        else if (parts.Length == 5 && parts[4] == "versions")
        {
            var (groupNode, binding) = await ResolveResourceParentAsync(parts[..4], cancellationToken)
                .ConfigureAwait(false);
            if (binding.IsMetadataOnly)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "The metadata-only Resource has no materialized Versions collection.");
            }
            members = await _reader.ReadVersionCollectionAsync(groupNode, XidOf(parts[..2]), parts[2],
                parts[3], binding.ResourceType, binding.ResourceVersionsType, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The OPC UA source does not support this collection path shape.");
        }

        var result = new JsonObject();
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members.EnumerateArray())
        {
            var idName = parts.Length switch
            {
                1 => "groupid",
                3 => "resourceid",
                _ => "versionid"
            };
            if (!member.TryGetProperty(idName, out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(idValue.GetString()))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"A {idName} is missing from the OPC UA collection.");
            }
            var id = idValue.GetString()!;
            if (!folded.Add(id))
            {
                throw new FederationException(FederationErrorCode.Ambiguous,
                    "The OPC UA collection contains case-insensitively colliding identifiers.");
            }
            var childParts = parts.Append(id).ToArray();
            var childPointer = parentPointer.Length == 0
                ? "/entities/" + PointerToken(id)
                : parentPointer + "/" + PointerToken(parts[^1]) + "/" + PointerToken(id);
            var child = await BuildEntityAsync(childParts, childPointer, cancellationToken).ConfigureAwait(false);
            result.Add(id, child);
        }
        return result;
    }

    private async ValueTask<FederationReadResult> ReadDocumentAsync(FederationReadRequest request,
        bool followedXref, CancellationToken cancellationToken)
    {
        var parts = ParseParts(request.Target);
        if (parts.Length is not (4 or 6))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "An OPC UA document read targets a Resource or an exact Version.");
        }
        if (parts.Length == 6 && parts[4] != "versions")
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "An exact Version document path must contain the versions segment.");
        }
        var bindingForDocument = ResourceType(parts[0], parts[2]);
        if (bindingForDocument.IsMetadataOnly)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The resource model declares hasdocument=false.");
        }
        var (groupNode, binding) = await ResolveResourceParentAsync(parts[..4], cancellationToken)
            .ConfigureAwait(false);
        var meta = await _reader.ReadResourceMetaPropertiesAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, cancellationToken).ConfigureAwait(false);
        if (meta.TryGetProperty("xref", out var xref))
        {
            if (followedXref)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "An OPC UA Resource Meta xref cannot be resolved through a second alias.");
            }
            var targetParts = await ResolveXrefAsync(xref.GetString()!, binding, cancellationToken)
                .ConfigureAwait(false);
            if (parts.Length == 6)
            {
                targetParts = [.. targetParts, "versions", parts[5]];
            }
            return await ReadDocumentAsync(new(FederationOperation.Document, XidOf(targetParts)), true,
                cancellationToken).ConfigureAwait(false);
        }

        var versionId = parts.Length == 6
            ? parts[5]
            : meta.TryGetProperty("defaultversionid", out var defaultId) &&
                defaultId.ValueKind == JsonValueKind.String
                ? defaultId.GetString()!
                : throw new FederationException(FederationErrorCode.InvalidPackage,
                    "Resource Meta has no defaultversionid for document selection.");
        var selectedVersionXid = XidOf([.. parts[..4], "versions", versionId]);
        var selected = await _reader.ReadResourcePropertiesAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, versionId,
            binding.ResourceVersionsType, cancellationToken).ConfigureAwait(false);
        var contentType = selected.TryGetProperty("contenttype", out var contentTypeValue) &&
            contentTypeValue.ValueKind == JsonValueKind.String ? contentTypeValue.GetString() : null;
        var externalReference = await _reader.ReadExternalNodeIdAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, versionId,
            binding.ResourceVersionsType, cancellationToken).ConfigureAwait(false);
        if (externalReference is not null ||
            selected.TryGetProperty("resourceurl", out var resourceUrl) && resourceUrl.ValueKind == JsonValueKind.String)
        {
            var descriptor = new JsonObject { ["kind"] = "external", ["binding"] = "opcua" };
            if (externalReference is not null)
            {
                descriptor["expandednodeid"] = new JsonObject
                {
                    ["serverapplicationuri"] = externalReference.ServerApplicationUri,
                    ["namespaceuri"] = externalReference.NamespaceUri,
                    ["identifiertype"] = externalReference.IdentifierType.ToString(),
                    ["identifier"] = externalReference.Identifier switch
                    {
                        uint numeric => JsonValue.Create(numeric),
                        string text => JsonValue.Create(text),
                        Guid guid => JsonValue.Create(guid),
                        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
                        _ => throw new FederationException(FederationErrorCode.InvalidPackage,
                            "The external OPC UA identifier type is not supported.")
                    }
                };
            }
            if (selected.TryGetProperty("resourceurl", out resourceUrl) && resourceUrl.ValueKind == JsonValueKind.String)
            {
                descriptor["resourceurl"] = resourceUrl.GetString();
            }
            return FederationReadResult.FromExternalDocument(selectedVersionXid, Context,
                RegistryJson.Parse(descriptor.ToJsonString()).RootElement);
        }

        var document = await _reader.ReadResourceDocumentAsync(groupNode, XidOf(parts[..2]),
            parts[2], parts[3], binding.ResourceType, versionId,
            binding.ResourceVersionsType, cancellationToken).ConfigureAwait(false);
        if (parts.Length == 4)
        {
            var after = await _reader.ReadResourceMetaPropertiesAsync(groupNode, XidOf(parts[..2]),
                parts[2], parts[3], binding.ResourceType, cancellationToken).ConfigureAwait(false);
            if (!JsonNode.DeepEquals(JsonNode.Parse(meta.GetRawText()), JsonNode.Parse(after.GetRawText())))
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "Resource Meta changed while selecting and reading the default Version document.");
            }
        }
        return FederationReadResult.FromDocument(selectedVersionXid, Context,
            new FederationDocument(document, contentType));
    }

    private async ValueTask<(NodeId GroupNode, OpcUaResourceTypeBinding Binding)> ResolveResourceParentAsync(
        string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 4)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "A Resource operation requires a Group and Resource identity.");
        }
        var groupNode = await _reader.ResolveGroupNodeAsync(_registryRoot, parts[0], parts[1],
            GroupType(parts[0]), cancellationToken).ConfigureAwait(false);
        return (groupNode, ResourceType(parts[0], parts[2]));
    }

    private async ValueTask<string[]> ResolveXrefAsync(string xref,
        OpcUaResourceTypeBinding sourceBinding, CancellationToken cancellationToken)
    {
        var target = new FederationReadRequest(FederationOperation.Entity, xref);
        var parts = ParseParts(target.Target);
        if (parts.Length != 4)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "MetaXref must name a Resource in the selected Registry.");
        }
        var targetBinding = ResourceType(parts[0], parts[2]);
        if (targetBinding.ResourceType != sourceBinding.ResourceType)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "MetaXref must name a Resource with the same mapped ResourceType.");
        }
        var groupNode = await _reader.ResolveGroupNodeAsync(_registryRoot, parts[0], parts[1],
            GroupType(parts[0]), cancellationToken).ConfigureAwait(false);
        _ = await _reader.ReadResourceMetaPropertiesAsync(groupNode, XidOf(parts[..2]), parts[2], parts[3],
            targetBinding.ResourceType, cancellationToken).ConfigureAwait(false);
        return parts;
    }

    private NodeId GroupType(string collection)
    {
        _ = GroupDefinition(collection);
        return _types.GroupTypes.TryGetValue(collection, out var type)
            ? type
            : throw new FederationException(FederationErrorCode.UnsupportedOperation,
                $"No OPC UA GroupType mapping was supplied for model collection '{collection}'.");
    }

    private RegistryGroupDefinition GroupDefinition(string collection) =>
        Model.Groups.TryGetValue(collection, out var group)
            ? group
            : throw new FederationException(FederationErrorCode.NotFound,
                $"The Registry Model has no group collection '{collection}'.");

    private OpcUaResourceTypeBinding ResourceType(string groupCollection, string resourceCollection)
    {
        var group = GroupDefinition(groupCollection);
        if (!group.Resources.TryGetValue(resourceCollection, out var definition))
        {
            throw new FederationException(FederationErrorCode.NotFound,
                $"The Registry Model has no resource collection '{resourceCollection}' in '{groupCollection}'.");
        }
        if (!_types.ResourceTypes.TryGetValue((groupCollection, resourceCollection), out var binding))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                $"No OPC UA ResourceType mapping was supplied for '{groupCollection}/{resourceCollection}'.");
        }
        if (binding.IsMetadataOnly != !definition.HasDocument ||
            binding.IsMetadataOnly && definition.MaxVersions != 1)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The OPC UA ResourceType mapping does not match the model's document and Version contract.");
        }
        return binding;
    }

    private static void ValidateEntityPath(string[] parts)
    {
        if (parts.Length is not (0 or 2 or 4 or 5 or 6) ||
            parts.Length == 5 && parts[4] != "meta" ||
            parts.Length == 6 && parts[4] != "versions")
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The OPC UA source does not support this entity path shape.");
        }
    }

    private static void ValidateCollectionPath(string[] parts)
    {
        if (parts.Length is not (1 or 3 or 5) || parts.Length == 5 && parts[4] != "versions")
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The OPC UA source does not support this collection path shape.");
        }
    }

    private static string[] ParseParts(string xid) => xid == "/"
        ? []
        : xid[1..].Split('/').Select(Uri.UnescapeDataString).ToArray();

    private static string XidOf(IEnumerable<string> parts) =>
        "/" + string.Join('/', parts.Select(Uri.EscapeDataString));

    private static string Kind(int segments) => segments switch
    {
        0 => "registry",
        2 => "group",
        4 => "resource",
        5 => "meta",
        6 => "version",
        _ => throw new FederationException(FederationErrorCode.UnsupportedOperation,
            "The OPC UA source does not support this entity path shape.")
    };

    private static string PointerToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Fragment(string pointer) => "#" + pointer;

    private static JsonObject Object(JsonElement element) =>
        JsonNode.Parse(element.GetRawText())!.AsObject();

    private static void AddLabels(JsonObject entity, JsonElement labels)
    {
        if (labels.ValueKind == JsonValueKind.Object && labels.EnumerateObject().Any())
        {
            entity["labels"] = JsonNode.Parse(labels.GetRawText());
        }
    }

    private static void RebasePointers(JsonNode node, string from, string to)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj.ToArray())
            {
                if (entry.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
                    (entry.Key == "self" || entry.Key == "shortself" ||
                     entry.Key.EndsWith("url", StringComparison.Ordinal)))
                {
                    var prefix = Fragment(from);
                    if (text == prefix || text.StartsWith(prefix + "/", StringComparison.Ordinal))
                    {
                        obj[entry.Key] = Fragment(to) + text[prefix.Length..];
                    }
                }
                else if (entry.Value is not null)
                {
                    RebasePointers(entry.Value, from, to);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RebasePointers(item, from, to);
                }
            }
        }
    }

    private FederationReadResult Metadata(string selectedXid, JsonObject metadata) =>
        FederationReadResult.FromMetadata(selectedXid, Context,
            RegistryJson.Parse(metadata.ToJsonString()).RootElement);
}
