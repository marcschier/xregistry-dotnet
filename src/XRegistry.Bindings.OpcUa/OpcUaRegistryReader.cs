// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json;
using XRegistry;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>One forward reference returned by Browse.</summary>
public sealed record OpcUaReference(NodeId Target, QualifiedName BrowseName,
    NodeClass NodeClass = NodeClass.Unspecified, NodeId? TypeDefinition = null);

/// <summary>A JSON value and its OPC UA type identity, retained for exact attribute type checks.</summary>
public sealed record OpcUaJsonValue(JsonElement Value, BuiltInType BuiltInType, string? DataTypeName = null);

/// <summary>One Browse or BrowseNext page; a continuation point means more results remain.</summary>
public sealed record OpcUaBrowsePage(IReadOnlyList<OpcUaReference> References, byte[]? ContinuationPoint = null);

/// <summary>The session operations used by Registry reads and document replacement.</summary>
public interface IOpcUaRegistrySession
{
    /// <summary>Reads the connected server's ServerArray.</summary>
    ValueTask<IReadOnlyList<string>> ReadServerArrayAsync(CancellationToken cancellationToken);
    /// <summary>Reads the connected server's NamespaceArray.</summary>
    ValueTask<NamespaceTable> ReadNamespaceArrayAsync(CancellationToken cancellationToken);
    /// <summary>Returns true when the node exists and its TypeDefinition is the RegistryType or a subtype.</summary>
    ValueTask<bool> IsRegistryNodeAsync(NodeId node, CancellationToken cancellationToken);
    /// <summary>Checks an OPC UA ObjectType against a declared type or its supertypes.</summary>
    ValueTask<bool> IsTypeOrSubtypeAsync(NodeId actualType, NodeId declaredType, CancellationToken cancellationToken);
    /// <summary>Starts a forward hierarchical Browse of the node's components.</summary>
    ValueTask<OpcUaBrowsePage> BrowseAsync(NodeId node, CancellationToken cancellationToken);
    /// <summary>Continues, or when <paramref name="release"/> is true only releases, a continuation point.</summary>
    ValueTask<OpcUaBrowsePage> BrowseNextAsync(byte[] continuationPoint, bool release, CancellationToken cancellationToken);
    /// <summary>Calls FileType Open and returns the file handle.</summary>
    ValueTask<uint> OpenAsync(NodeId file, byte mode, CancellationToken cancellationToken);
    /// <summary>Calls FileType Read; an empty result is end of file and a short read is not.</summary>
    ValueTask<byte[]> ReadAsync(NodeId file, uint handle, int length, CancellationToken cancellationToken);
    /// <summary>Sets the current FileType handle position.</summary>
    ValueTask SetPositionAsync(NodeId file, uint handle, ulong position, CancellationToken cancellationToken);
    /// <summary>Writes one FileType chunk to an open handle.</summary>
    ValueTask WriteAsync(NodeId file, uint handle, byte[] data, CancellationToken cancellationToken);
    /// <summary>Writes a Variable's Value attribute.</summary>
    ValueTask WriteValueAsync(NodeId variable, Variant value, CancellationToken cancellationToken);
    /// <summary>Calls a Method selected by its BrowseName on an Object.</summary>
    ValueTask<IReadOnlyList<Variant>> CallMethodAsync(NodeId target, string methodBrowseName,
        IReadOnlyList<Variant> inputArguments, CancellationToken cancellationToken);
    /// <summary>Reads the Value attribute of a String Variable.</summary>
    ValueTask<string> ReadStringAsync(NodeId variable, CancellationToken cancellationToken);
    /// <summary>Reads a Variable Value and converts supported OPC UA scalar values to JSON.</summary>
    ValueTask<OpcUaJsonValue> ReadJsonValueAsync(NodeId variable, CancellationToken cancellationToken);
    /// <summary>Reads a Variable Value as an OPC UA ExpandedNodeId.</summary>
    ValueTask<ExpandedNodeId> ReadExpandedNodeIdAsync(NodeId variable, CancellationToken cancellationToken);
    /// <summary>Maps an ExpandedNodeId through this Session's ServerArray and NamespaceArray.</summary>
    ValueTask<OpcUaExternalNodeId> MapExternalNodeIdAsync(ExpandedNodeId nodeId,
        CancellationToken cancellationToken);
    /// <summary>Calls FileType Close.</summary>
    ValueTask CloseAsync(NodeId file, uint handle, CancellationToken cancellationToken);
}

/// <summary>Protocol rules for reading a Registry over an OPC UA Session, independent of the stack transport.</summary>
public sealed class OpcUaRegistryReader
{
    private const byte ReadMode = 1;
    private const string ContinuationConsumedKey = "OpcUa.ContinuationConsumed";
    private static readonly NodeId s_fileType = ObjectTypeIds.FileType;
    private readonly IOpcUaRegistrySession session;
    private readonly int maxBytes;
    private readonly int chunkSize;
    private readonly int maxReferences;
    private RegistryModel? registryModel;
    private bool specVersionValidated;
    private string? acceptedSpecVersion;

    /// <summary>Creates a reader with finite file-size and browse-count budgets.</summary>
    public OpcUaRegistryReader(IOpcUaRegistrySession session, int maxBytes = 64 * 1024 * 1024,
        int chunkSize = 64 * 1024, int maxReferences = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReferences);
        this.session = session;
        this.maxBytes = maxBytes;
        this.chunkSize = chunkSize;
        this.maxReferences = maxReferences;
    }

    /// <summary>Maps the advertised root and confirms it is a Registry; never selects another Registry.</summary>
    public async ValueTask<NodeId> VerifyRootAsync(OpcUaFederationTarget target, NamespaceTable namespaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateServerNamespaceIdentity(
            await session.ReadServerArrayAsync(cancellationToken).ConfigureAwait(false), namespaces);
        var root = target.ResolveRegistryRoot(namespaces);
        return await VerifyRegistryRootAsync(root, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateServerNamespaceIdentity(
        IReadOnlyList<string> serverArray, NamespaceTable namespaceArray)
    {
        ArgumentNullException.ThrowIfNull(serverArray);
        ArgumentNullException.ThrowIfNull(namespaceArray);

        var namespaceServerUri = namespaceArray.Count > 1 ? namespaceArray.GetString(1u) : null;
        if (serverArray.Count == 0 || string.IsNullOrEmpty(serverArray[0]) ||
            string.IsNullOrEmpty(namespaceServerUri) ||
            !string.Equals(serverArray[0], namespaceServerUri, StringComparison.Ordinal))
        {
            throw new FederationException(FederationErrorCode.PolicyDenied,
                "The Session ServerArray[0] must match NamespaceArray[1].");
        }
    }

    /// <summary>Confirms that a Session-local node is a RegistryType instance.</summary>
    public async ValueTask<NodeId> VerifyRegistryRootAsync(NodeId root,
        CancellationToken cancellationToken = default)
    {
        if (root.IsNull || !await session.IsRegistryNodeAsync(root, cancellationToken).ConfigureAwait(false))
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "The advertised root is absent or is not a RegistryType instance.");
        }
        return root;
    }

    /// <summary>Reads the server's NamespaceArray and verifies the advertised root against it.</summary>
    public async ValueTask<NodeId> VerifyRootAsync(OpcUaFederationTarget target,
        CancellationToken cancellationToken = default)
    {
        var namespaces = await session.ReadNamespaceArrayAsync(cancellationToken).ConfigureAwait(false);
        return await VerifyRootAsync(target, namespaces, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Browses all pages; an early stop or failure releases the continuation point.</summary>
    public async ValueTask<IReadOnlyList<OpcUaReference>> BrowseAllAsync(NodeId node,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        return await BrowseAllCoreAsync(node, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<OpcUaReference>> BrowseAllCoreAsync(NodeId node,
        CancellationToken cancellationToken)
    {
        var all = new List<OpcUaReference>();
        var page = await session.BrowseAsync(node, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var continuation = page.ContinuationPoint is { Length: > 0 } c ? c : null;
            try
            {
                if (all.Count + page.References.Count > maxReferences)
                {
                    throw new FederationException(FederationErrorCode.LimitExceeded,
                        "The Browse result exceeds the reference budget.");
                }
                all.AddRange(page.References);
                if (continuation is null)
                {
                    return all;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var next = continuation;
                var nextPage = await session.BrowseNextAsync(next, false, cancellationToken).ConfigureAwait(false);
                continuation = null;
                page = nextPage;
            }
            catch (Exception exception) when (continuation is not null)
            {
                if (exception.Data[ContinuationConsumedKey] is not true)
                {
                    await ReleaseAsync(continuation, exception).ConfigureAwait(false);
                }
                throw;
            }
        }
    }

    /// <summary>Reads a FileType Object completely with Open, repeated Read, and an always-attempted Close.</summary>
    public ValueTask<byte[]> ReadFileAsync(NodeId file, CancellationToken cancellationToken = default) =>
        ReadFileAsync(file, null, cancellationToken);

    private async ValueTask<byte[]> ReadFileAsync(NodeId file, Func<ValueTask>? whileOpen,
        CancellationToken cancellationToken)
    {
        EnsureSpecVersionValidated();
        var handle = await session.OpenAsync(file, ReadMode, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        Exception? failure = null;
        try
        {
            if (whileOpen is not null)
            {
                await whileOpen().ConfigureAwait(false);
            }
            while (true)
            {
                var chunk = await session.ReadAsync(file, handle, chunkSize, cancellationToken).ConfigureAwait(false);
                if (chunk.Length == 0)
                {
                    break;
                }
                if (buffer.Length + chunk.Length > maxBytes)
                {
                    throw new FederationException(FederationErrorCode.LimitExceeded,
                        "The file exceeds the byte budget.");
                }
                buffer.Write(chunk);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await session.CloseAsync(file, handle, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception closeFailure) when (failure is not null)
        {
            failure.Data["OpcUa.FileCloseFailure"] = closeFailure;
        }
        catch (Exception ex)
        {
            throw new FederationException(FederationErrorCode.Unavailable,
                "The file could not be closed after a complete read.", innerException: ex);
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return buffer.ToArray();
    }

    /// <summary>Reads <c>SpecVersion</c> and requires the caller to accept it before any entity is interpreted.</summary>
    public async ValueTask<string> ReadSpecVersionAsync(NodeId root, Func<string, bool> isSupported,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isSupported);
        specVersionValidated = false;
        acceptedSpecVersion = null;
        var version = await session.ReadStringAsync(await ChildAsync(root, "SpecVersion", NodeClass.Variable, cancellationToken)
            .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (!isSupported(version))
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion,
                "The Registry reports an unsupported SpecVersion.");
        }
        specVersionValidated = true;
        acceptedSpecVersion = version;
        return version;
    }

    /// <summary>Reads the Registry's required Core Properties and optional common text Properties.</summary>
    public async ValueTask<JsonElement> ReadRegistryPropertiesAsync(NodeId root,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        var references = await BrowseAllAsync(root, cancellationToken).ConfigureAwait(false);
        var components = references.Where(reference =>
                reference.NodeClass == NodeClass.Variable && reference.BrowseName.Name is not null)
            .GroupBy(reference => reference.BrowseName.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var values = new Dictionary<string, OpcUaJsonValue>(StringComparer.Ordinal);
        foreach (var name in new[] { "RegistryId", "SpecVersion", "Xid", "Epoch", "CreatedAt", "ModifiedAt" })
        {
            if (!components.TryGetValue(name, out var matches))
            {
                throw new FederationException(FederationErrorCode.NotFound,
                    $"The Registry has no {name} Property.");
            }
            values.Add(name, await session.ReadJsonValueAsync(Unique(matches, name).Target, cancellationToken)
                .ConfigureAwait(false));
        }

        if (values["SpecVersion"].BuiltInType != BuiltInType.String ||
            values["SpecVersion"].Value.ValueKind != JsonValueKind.String)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The Registry SpecVersion Property is not a String.");
        }

        if (values["SpecVersion"].Value.GetString() != acceptedSpecVersion)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "The Registry SpecVersion changed after it was accepted by this Session.");
        }

        if (values["RegistryId"].BuiltInType != BuiltInType.String ||
            values["RegistryId"].Value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(values["RegistryId"].Value.GetString()) ||
            values["SpecVersion"].BuiltInType != BuiltInType.String ||
            values["SpecVersion"].Value.ValueKind != JsonValueKind.String ||
            values["Xid"].BuiltInType != BuiltInType.String ||
            values["Xid"].Value.ValueKind != JsonValueKind.String || values["Xid"].Value.GetString() != "/" ||
            values["Epoch"].BuiltInType != BuiltInType.UInt32 ||
            values["Epoch"].Value.ValueKind != JsonValueKind.Number ||
            !values["Epoch"].Value.TryGetUInt32(out _) ||
            values["CreatedAt"].BuiltInType != BuiltInType.DateTime ||
            values["CreatedAt"].Value.ValueKind != JsonValueKind.String ||
            !IsRfc3339Timestamp(values["CreatedAt"].Value.GetString()!) ||
            values["ModifiedAt"].BuiltInType != BuiltInType.DateTime ||
            values["ModifiedAt"].Value.ValueKind != JsonValueKind.String ||
            !IsRfc3339Timestamp(values["ModifiedAt"].Value.GetString()!))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The Registry's required Properties have invalid values or contradict the selected root or SpecVersion.");
        }

        var entity = new JsonObject();
        foreach (var pair in values)
        {
            entity[CoreName(pair.Key)] = JsonNode.Parse(pair.Value.Value.GetRawText());
        }
        foreach (var name in new[] { "Name", "Description", "Documentation" })
        {
            if (!components.TryGetValue(name, out var matches))
            {
                continue;
            }
            var value = await session.ReadJsonValueAsync(Unique(matches, name).Target, cancellationToken)
                .ConfigureAwait(false);
            if (value.BuiltInType != BuiltInType.String || value.Value.ValueKind != JsonValueKind.String)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The Registry's optional {name} Property is not a String.");
            }
            entity[CoreName(name)] = JsonNode.Parse(value.Value.GetRawText());
        }
        if (registryModel is { } model)
        {
            await AddModelledPropertiesAsync(root, entity, model.Attributes, metaProperties: false,
                "Registry", cancellationToken).ConfigureAwait(false);
        }
        return RegistryJson.Parse(entity.ToJsonString()).RootElement;
    }

    /// <summary>Reads one model-typed Group and maps its required Core Properties.</summary>
    public async ValueTask<JsonElement> ReadGroupPropertiesAsync(NodeId registryRoot, string groupCollection,
        string groupId, NodeId groupType, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireGroupDefinition(groupCollection);
        _ = new FederationReadRequest(FederationOperation.Collection, "/" + groupCollection);
        _ = new FederationReadRequest(FederationOperation.Entity, GroupXid(groupCollection, groupId));
        var groups = await FindGroupsAsync(registryRoot, groupType, cancellationToken).ConfigureAwait(false);
        var group = Unique(groups.Where(candidate =>
                string.Equals(candidate.BrowseName.Name, groupId, StringComparison.Ordinal)).ToArray(), groupId);
        return await ReadGroupEntityAsync(group, groupCollection, groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves and validates one model-typed Group and returns its Session-local NodeId.</summary>
    public async ValueTask<NodeId> ResolveGroupNodeAsync(NodeId registryRoot, string groupCollection,
        string groupId, NodeId groupType, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireGroupDefinition(groupCollection);
        _ = new FederationReadRequest(FederationOperation.Collection, "/" + groupCollection);
        var xid = GroupXid(groupCollection, groupId);
        _ = new FederationReadRequest(FederationOperation.Entity, xid);
        var groups = await FindGroupsAsync(registryRoot, groupType, cancellationToken).ConfigureAwait(false);
        var group = Unique(groups.Where(candidate =>
                string.Equals(candidate.BrowseName.Name, groupId, StringComparison.Ordinal)).ToArray(), groupId);
        _ = await ReadGroupEntityAsync(group, groupCollection, groupId, cancellationToken).ConfigureAwait(false);
        return group.Target;
    }

    /// <summary>Reads all direct Group instances of one model-declared GroupType.</summary>
    public async ValueTask<JsonElement> ReadGroupCollectionAsync(NodeId registryRoot, string groupCollection,
        NodeId groupType, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireGroupDefinition(groupCollection);
        _ = new FederationReadRequest(FederationOperation.Collection, "/" + groupCollection);
        var groups = await FindGroupsAsync(registryRoot, groupType, cancellationToken).ConfigureAwait(false);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var collection = new JsonArray();
        foreach (var group in groups)
        {
            var groupId = group.BrowseName.Name;
            if (string.IsNullOrEmpty(groupId) || !identifiers.Add(groupId))
            {
                throw new FederationException(FederationErrorCode.Ambiguous,
                    "The Group collection contains an empty or duplicate GroupId.");
            }
            _ = new FederationReadRequest(FederationOperation.Entity, GroupXid(groupCollection, groupId));
            var entity = await ReadGroupEntityAsync(group, groupCollection, groupId, cancellationToken)
                .ConfigureAwait(false);
            collection.Add(JsonNode.Parse(entity.GetRawText()));
        }
        return RegistryJson.Parse(collection.ToJsonString()).RootElement;
    }

    /// <summary>Reads one ResourceType file's fixed metadata without reading its document bytes.</summary>
    public async ValueTask<JsonElement> ReadResourcePropertiesAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, string? versionId = null,
        NodeId? resourceVersionsType = null, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireResourceDefinition(groupXid, resourceCollection);
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resource = await FindSelectedResourceAsync(groupNode, resourceId, resourceType, resourceVersionsType,
            versionId, cancellationToken).ConfigureAwait(false);
        return await ReadResourceEntityAsync(resource, groupXid, resourceCollection, resourceId, versionId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves and validates a metadata-only Resource by its Registry path.</summary>
    public async ValueTask<NodeId> ResolveMetadataResourceNodeAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId metadataResourceType,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        EnsureMetadataOnlyDefinition(RequireResourceDefinition(groupXid, resourceCollection));
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resource = await FindTypedChildAsync(groupNode, resourceId, metadataResourceType, cancellationToken)
            .ConfigureAwait(false);
        _ = await ReadMetadataResourceAsync(resource, groupXid, resourceCollection, resourceId, null,
            cancellationToken).ConfigureAwait(false);
        return resource.Target;
    }

    /// <summary>Reads Resource Meta properties from the logical Resource, independently of its default Version.</summary>
    public async ValueTask<JsonElement> ReadResourceMetaPropertiesAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireResourceDefinition(groupXid, resourceCollection);
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resource = await FindTypedChildAsync(groupNode, resourceId, resourceType, cancellationToken)
            .ConfigureAwait(false);
        var resourceXid = GroupXidFromParent(groupXid, resourceCollection, resourceId, null);
        var properties = await ReadPropertyValuesAsync(resource.Target,
            ["ResourceId", "Xid", "MetaEpoch", "MetaCreatedAt", "MetaModifiedAt", "MetaName", "MetaDescription",
                "MetaDocumentation", "MetaXref", "MetaDefaultVersionSticky", "MetaReadonly",
                "MetaCompatibility", "MetaDeprecated", "VersionId"],
            cancellationToken).ConfigureAwait(false);
        if (!TryReadUaString(properties, "ResourceId", out var actualResourceId) ||
            actualResourceId != resourceId)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The logical Resource ResourceId Property does not match the selected Resource.");
        }
        if (properties.TryGetValue("Xid", out var xid) &&
            (xid.BuiltInType != BuiltInType.String || xid.Value.ValueKind != JsonValueKind.String ||
             xid.Value.GetString() != resourceXid))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The logical Resource Xid Property contradicts the selected AddressSpace path.");
        }
        var entity = new JsonObject { ["xid"] = resourceXid + "/meta" };

        if (properties.TryGetValue("MetaEpoch", out var epoch))
        {
            if (epoch.BuiltInType != BuiltInType.UInt32 ||
                epoch.Value.ValueKind != JsonValueKind.Number || !epoch.Value.TryGetUInt32(out _))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The Resource Meta Epoch is not a UInt32.");
            }
            entity["epoch"] = JsonNode.Parse(epoch.Value.GetRawText());
        }

        foreach (var name in new[] { "MetaCreatedAt", "MetaModifiedAt" })
        {
            if (!properties.TryGetValue(name, out var value))
            {
                continue;
            }
            if (value.BuiltInType != BuiltInType.DateTime ||
                value.Value.ValueKind != JsonValueKind.String ||
                !IsRfc3339Timestamp(value.Value.GetString()!))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The Resource Meta {name} Property is not a zoned DateTime.");
            }
            entity[CoreName(name["Meta".Length..])] = JsonNode.Parse(value.Value.GetRawText());
        }

        foreach (var name in new[] { "MetaName", "MetaDescription", "MetaDocumentation", "MetaXref", "MetaCompatibility" })
        {
            if (!properties.TryGetValue(name, out var value))
            {
                continue;
            }
            if (value.BuiltInType != BuiltInType.String || value.Value.ValueKind != JsonValueKind.String)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The Resource Meta {name} Property is not a String.");
            }
            entity[CoreName(name["Meta".Length..])] = JsonNode.Parse(value.Value.GetRawText());
        }

        foreach (var name in new[] { "MetaDefaultVersionSticky", "MetaReadonly" })
        {
            if (!properties.TryGetValue(name, out var value))
            {
                continue;
            }
            if (value.BuiltInType != BuiltInType.Boolean || value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The Resource Meta {name} Property is not a Boolean.");
            }
            entity[CoreName(name["Meta".Length..])] = JsonNode.Parse(value.Value.GetRawText());
        }

        if (properties.TryGetValue("MetaDeprecated", out var deprecated))
        {
            entity["deprecated"] = ReadDeprecationValue(deprecated);
        }

        await AddModelledPropertiesAsync(resource.Target, entity,
            RequireResourceDefinition(groupXid, resourceCollection).MetaAttributes, metaProperties: true,
            "Resource Meta", cancellationToken).ConfigureAwait(false);

        if (properties.ContainsKey("MetaXref") && properties.ContainsKey("VersionId"))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "An xref Resource cannot also select a default Version.");
        }
        else if (properties.TryGetValue("VersionId", out var defaultVersion))
        {
            if (defaultVersion.BuiltInType != BuiltInType.String ||
                defaultVersion.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(defaultVersion.Value.GetString()))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The logical Resource VersionId is not a nonempty String.");
            }
            entity["defaultversionid"] = JsonNode.Parse(defaultVersion.Value.GetRawText());
        }

        return RegistryJson.Parse(entity.ToJsonString()).RootElement;
    }

    private static JsonObject ReadDeprecationValue(OpcUaJsonValue value)
    {
        if (value.BuiltInType != BuiltInType.ExtensionObject ||
            value.DataTypeName != "DeprecationDataType" || value.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The Resource Meta MetaDeprecated Property is not a DeprecationDataType.");
        }

        var result = new JsonObject();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.Value.EnumerateObject())
        {
            var name = property.Name switch
            {
                "Effective" => "effective",
                "Removal" => "removal",
                "Alternative" => "alternative",
                "Documentation" => "documentation",
                _ => throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The Resource Meta MetaDeprecated Property contains an unknown '{property.Name}' field.")
            };
            if (!seen.Add(name))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The Resource Meta MetaDeprecated Property contains duplicate '{property.Name}' fields.");
            }

            var field = property.Value;
            if (property.Name is "Effective" or "Removal")
            {
                if (field.ValueKind != JsonValueKind.String || !IsRfc3339Timestamp(field.GetString()!))
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        $"The Resource Meta MetaDeprecated {property.Name} field is not a zoned DateTime.");
                }
            }
            else if (field.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(field.GetString()) ||
                field.GetString()!.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
                !Uri.TryCreate(field.GetString(), UriKind.RelativeOrAbsolute, out _))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The Resource Meta MetaDeprecated {property.Name} field is not a UriString.");
            }

            result[name] = JsonNode.Parse(field.GetRawText());
        }

        return result;
    }

    /// <summary>Resolves and validates the logical Resource or exact Version selected by the request.</summary>
    public async ValueTask<NodeId> ResolveResourceNodeAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, string? versionId = null,
        NodeId? resourceVersionsType = null, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireResourceDefinition(groupXid, resourceCollection);
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resource = await FindSelectedResourceAsync(groupNode, resourceId, resourceType,
            resourceVersionsType, versionId, cancellationToken).ConfigureAwait(false);
        _ = await ReadResourceEntityAsync(resource, groupXid, resourceCollection, resourceId, versionId,
            cancellationToken).ConfigureAwait(false);
        return resource.Target;
    }

    /// <summary>Reads the document bytes of a model-declared document-bearing ResourceType.</summary>
    public async ValueTask<byte[]> ReadResourceDocumentAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, string? versionId = null,
        NodeId? resourceVersionsType = null, CancellationToken cancellationToken = default)
    {
        var file = await ResolveResourceDocumentAsync(groupNode, groupXid, resourceCollection, resourceId,
            resourceType, versionId, resourceVersionsType, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
    }

    internal IOpcUaRegistrySession Session => session;
    internal int MaxBytes => maxBytes;
    internal int ChunkSize => chunkSize;
    internal void EnsureSpecVersionValidatedForWrite() => EnsureSpecVersionValidated();

    internal async ValueTask<NodeId> ResolveResourceDocumentAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, string? versionId,
        NodeId? resourceVersionsType, CancellationToken cancellationToken)
    {
        EnsureSpecVersionValidated();
        if (!RequireResourceDefinition(groupXid, resourceCollection).HasDocument)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The resource model declares hasdocument=false.");
        }
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resource = await FindSelectedResourceAsync(groupNode, resourceId, resourceType, resourceVersionsType,
            versionId, cancellationToken).ConfigureAwait(false);
        _ = await ReadResourceEntityAsync(resource, groupXid, resourceCollection, resourceId, versionId,
            cancellationToken).ConfigureAwait(false);
        return resource.Target;
    }

    /// <summary>Reads default resources identified by matching ResourceId and BrowseName values.</summary>
    public async ValueTask<JsonElement> ReadResourceCollectionAsync(NodeId groupNode, string groupXid,
        string resourceCollection, NodeId resourceType, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireResourceDefinition(groupXid, resourceCollection);
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        _ = new FederationReadRequest(FederationOperation.Collection,
            groupXid.TrimEnd('/') + "/" + resourceCollection);
        var resources = await FindTypedChildrenAsync(groupNode, resourceType, cancellationToken).ConfigureAwait(false);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var collection = new JsonArray();
        foreach (var resource in resources)
        {
            var resourceId = resource.BrowseName.Name;
            if (string.IsNullOrEmpty(resourceId))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "A ResourceType child has an empty BrowseName.");
            }
            var idValues = await ReadPropertyValuesAsync(resource.Target, ["ResourceId"], cancellationToken)
                .ConfigureAwait(false);
            if (!idValues.TryGetValue("ResourceId", out var idValue) ||
                idValue.BuiltInType != BuiltInType.String || idValue.Value.ValueKind != JsonValueKind.String)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "A ResourceType child has no valid ResourceId Property.");
            }
            if (!string.Equals(idValue.Value.GetString(), resourceId, StringComparison.Ordinal))
            {
                continue;
            }
            if (!identifiers.Add(resourceId))
            {
                throw new FederationException(FederationErrorCode.Ambiguous,
                    $"The resource collection contains duplicate ResourceId '{resourceId}'.");
            }
            var entity = await ReadResourceEntityAsync(resource, groupXid, resourceCollection, resourceId, null,
                cancellationToken).ConfigureAwait(false);
            collection.Add(JsonNode.Parse(entity.GetRawText()));
        }
        return RegistryJson.Parse(collection.ToJsonString()).RootElement;
    }

    /// <summary>Reads metadata-only Resources through their read-only Metadata FileType view.</summary>
    public async ValueTask<JsonElement> ReadMetadataResourceCollectionAsync(NodeId groupNode, string groupXid,
        string resourceCollection, NodeId metadataResourceType, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        var definition = RequireResourceDefinition(groupXid, resourceCollection);
        EnsureMetadataOnlyDefinition(definition);
        _ = new FederationReadRequest(FederationOperation.Collection,
            groupXid.TrimEnd('/') + "/" + resourceCollection);

        var resources = await FindTypedChildrenAsync(groupNode, metadataResourceType, cancellationToken)
            .ConfigureAwait(false);
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        var collection = new JsonArray();
        foreach (var resource in resources)
        {
            var resourceId = resource.BrowseName.Name;
            if (string.IsNullOrEmpty(resourceId))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "A MetadataResourceType child has an empty BrowseName.");
            }
            if (!resourceIds.Add(resourceId))
            {
                throw new FederationException(FederationErrorCode.Ambiguous,
                    $"The metadata resource collection contains duplicate ResourceId '{resourceId}'.");
            }
            var properties = await ReadPropertyValuesAsync(resource.Target, ["ResourceId"], cancellationToken)
                .ConfigureAwait(false);
            if (!properties.TryGetValue("ResourceId", out var identifier) ||
                identifier.BuiltInType != BuiltInType.String ||
                identifier.Value.ValueKind != JsonValueKind.String ||
                !string.Equals(identifier.Value.GetString(), resourceId, StringComparison.Ordinal))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "A MetadataResourceType ResourceId does not match its BrowseName.");
            }
            collection.Add((JsonNode?)new JsonObject { ["resourceid"] = resourceId });
        }
        return RegistryJson.Parse(collection.ToJsonString()).RootElement;
    }

    /// <summary>Reads one metadata-only Resource or its sole Version without inventing a Versions folder.</summary>
    public async ValueTask<JsonElement> ReadMetadataResourceAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId metadataResourceType, string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        EnsureMetadataOnlyDefinition(RequireResourceDefinition(groupXid, resourceCollection));
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resource = await FindTypedChildAsync(groupNode, resourceId, metadataResourceType, cancellationToken)
            .ConfigureAwait(false);
        return await ReadMetadataResourceAsync(resource, groupXid, resourceCollection, resourceId, versionId,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> ReadMetadataResourceAsync(OpcUaReference resource, string groupXid,
        string resourceCollection, string resourceId, string? versionId, CancellationToken cancellationToken)
    {
        var resourceXid = GroupXidFromParent(groupXid, resourceCollection, resourceId, null);
        var selectedXid = GroupXidFromParent(groupXid, resourceCollection, resourceId, versionId);
        var file = await FindFileComponentAsync(resource.Target, "Metadata", s_fileType, cancellationToken)
            .ConfigureAwait(false);
        JsonElement projected = default;
        byte[] bytes = await ReadFileAsync(file, async () =>
        {
            var properties = await ReadPropertyValuesAsync(resource.Target,
                ["ResourceId", "VersionId", "Xid", "Epoch", "CreatedAt", "ModifiedAt", "Name",
                    "Description", "Documentation"],
                cancellationToken).ConfigureAwait(false);
            if (properties.TryGetValue("VersionId", out var version))
            {
                if (version.BuiltInType != BuiltInType.String ||
                    version.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(version.Value.GetString()))
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        "The metadata Resource VersionId is not a nonempty String.");
                }
                if (versionId is not null &&
                    !string.Equals(version.Value.GetString(), versionId, StringComparison.Ordinal))
                {
                    throw new FederationException(FederationErrorCode.NotFound,
                        "The requested metadata VersionId does not match the observed sole Version.");
                }
            }
            else if (versionId is not null)
            {
                throw new FederationException(FederationErrorCode.NotFound,
                    "The metadata Resource has no observed VersionId to select.");
            }
            projected = BuildEntityProperties(properties, "ResourceId", resourceId, resourceXid, [],
                ["Name", "Description", "Documentation"]);
        }, cancellationToken).ConfigureAwait(false);
        var entity = JsonNode.Parse(projected.GetRawText())!.AsObject();
        entity["xid"] = selectedXid;
        if (versionId is not null)
        {
            entity["versionid"] = versionId;
        }

        JsonElement metadata;
        try
        {
            metadata = RegistryJson.Parse(bytes, new RegistryJsonLimits { MaxBytes = maxBytes }).RootElement;
        }
        catch (RegistryException exception)
        {
            var code = exception.Diagnostic.Code.EndsWith("_limit", StringComparison.Ordinal)
                ? FederationErrorCode.LimitExceeded : FederationErrorCode.InvalidPackage;
            throw new FederationException(code, "The Metadata FileType does not contain valid bounded JSON.",
                exception.Diagnostic.Code, exception);
        }
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The Metadata FileType JSON root must be an object.");
        }

        foreach (var property in metadata.EnumerateObject())
        {
            var value = JsonNode.Parse(property.Value.GetRawText());
            if (entity.TryGetPropertyValue(property.Name, out var existing) &&
                !JsonNode.DeepEquals(existing, value))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The Metadata FileType contradicts the '{property.Name}' Property projection.");
            }
            entity[property.Name] = value;
        }
        return RegistryJson.Parse(entity.ToJsonString()).RootElement;
    }

    private static void EnsureMetadataOnlyDefinition(RegistryResourceDefinition definition)
    {
        if (definition.HasDocument || definition.MaxVersions != 1)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "A MetadataResourceType mapping requires hasdocument=false and maxversions=1.");
        }
    }

    /// <summary>Reads sibling ResourceType files materialized as versions of one resource.</summary>
    public async ValueTask<JsonElement> ReadVersionCollectionAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, NodeId resourceVersionsType,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireResourceDefinition(groupXid, resourceCollection);
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resourceXid = GroupXidFromParent(groupXid, resourceCollection, resourceId, null);
        _ = new FederationReadRequest(FederationOperation.Collection, resourceXid + "/versions");
        var logicalResource = await FindTypedChildAsync(groupNode, resourceId, resourceType, cancellationToken)
            .ConfigureAwait(false);
        var versionsContainer = await FindVersionsContainerAsync(logicalResource.Target, resourceVersionsType,
            cancellationToken).ConfigureAwait(false);
        var resources = await FindTypedChildrenAsync(versionsContainer.Target, resourceType, cancellationToken)
            .ConfigureAwait(false);
        var versions = new JsonArray();
        var versionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var browseName = resource.BrowseName.Name;
            if (string.IsNullOrEmpty(browseName))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "A ResourceType child has an empty BrowseName.");
            }
            if (!versionIds.Add(browseName))
            {
                throw new FederationException(FederationErrorCode.Ambiguous,
                    $"The version collection contains duplicate VersionId '{browseName}'.");
            }
            var entity = await ReadResourceEntityAsync(resource, groupXid, resourceCollection, resourceId,
                browseName, cancellationToken).ConfigureAwait(false);
            versions.Add(JsonNode.Parse(entity.GetRawText()));
        }
        return RegistryJson.Parse(versions.ToJsonString()).RootElement;
    }

    private async ValueTask<JsonElement> ReadResourceEntityAsync(OpcUaReference resource, string groupXid,
        string resourceCollection, string resourceId, string? versionId, CancellationToken cancellationToken)
    {
        var resourceXid = GroupXidFromParent(groupXid, resourceCollection, resourceId, versionId);
        var properties = await ReadPropertyValuesAsync(resource.Target,
            ["ResourceId", "VersionId", "Xid", "Epoch", "CreatedAt", "ModifiedAt", "Name",
                "Description", "Documentation", "Format", "ContentType", "ResourceUrl"],
            cancellationToken).ConfigureAwait(false);
        var entity = BuildEntityProperties(properties, "ResourceId", resourceId, resourceXid,
            ["Name"], ["Description", "Documentation", "VersionId", "Format", "ContentType", "ResourceUrl"]);
        var entityObject = JsonNode.Parse(entity.GetRawText())!.AsObject();
        await AddModelledPropertiesAsync(resource.Target, entityObject,
            RequireResourceDefinition(groupXid, resourceCollection).Attributes, metaProperties: false,
            versionId is null ? "Resource" : "Version", cancellationToken).ConfigureAwait(false);
        if (properties.TryGetValue("VersionId", out var actualVersion) &&
            (actualVersion.BuiltInType != BuiltInType.String ||
             actualVersion.Value.ValueKind != JsonValueKind.String ||
             string.IsNullOrEmpty(actualVersion.Value.GetString())) ||
            versionId is not null && (!properties.TryGetValue("VersionId", out actualVersion) ||
                actualVersion.Value.GetString() != versionId))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The ResourceType VersionId does not match the selected resource or version.");
        }
        return RegistryJson.Parse(entityObject.ToJsonString()).RootElement;
    }

    /// <summary>Reads the selected Resource or Version federation target without converting it to an xRegistry URL.</summary>
    public async ValueTask<ExpandedNodeId?> ReadExternalReferenceAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, string? versionId = null,
        NodeId? resourceVersionsType = null, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireResourceDefinition(groupXid, resourceCollection);
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resource = await FindSelectedResourceAsync(groupNode, resourceId, resourceType, resourceVersionsType,
            versionId, cancellationToken).ConfigureAwait(false);
        _ = await ReadResourceEntityAsync(resource, groupXid, resourceCollection, resourceId, versionId,
            cancellationToken).ConfigureAwait(false);
        var properties = (await BrowseAllAsync(resource.Target, cancellationToken).ConfigureAwait(false))
            .Where(reference => reference.NodeClass == NodeClass.Variable &&
                string.Equals(reference.BrowseName.Name, "ExternalReference", StringComparison.Ordinal))
            .ToArray();
        if (properties.Length == 0)
        {
            return null;
        }
        return await session.ReadExpandedNodeIdAsync(Unique(properties, "ExternalReference").Target, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads a federation target with its table indexes resolved to exact URI identity.</summary>
    public async ValueTask<OpcUaExternalNodeId?> ReadExternalNodeIdAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, string? versionId = null,
        NodeId? resourceVersionsType = null, CancellationToken cancellationToken = default)
    {
        var reference = await ReadExternalReferenceAsync(groupNode, groupXid, resourceCollection, resourceId,
            resourceType, versionId, resourceVersionsType, cancellationToken).ConfigureAwait(false);
        if (reference is not { } nodeId)
        {
            return null;
        }

        return await session.MapExternalNodeIdAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OpcUaReference> FindSelectedResourceAsync(NodeId groupNode, string resourceId,
        NodeId resourceType, NodeId? resourceVersionsType, string? versionId,
        CancellationToken cancellationToken)
    {
        var logicalResource = await FindTypedChildAsync(groupNode, resourceId, resourceType, cancellationToken)
            .ConfigureAwait(false);
        if (versionId is null)
        {
            return logicalResource;
        }
        var versionsType = resourceVersionsType is { IsNull: false } type
            ? type
            : throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The ResourceVersionsType NodeId is required to resolve an exact Version.");
        var versions = await FindVersionsContainerAsync(logicalResource.Target, versionsType, cancellationToken)
            .ConfigureAwait(false);
        return await FindTypedChildAsync(versions.Target, versionId, resourceType, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<OpcUaReference> FindVersionsContainerAsync(NodeId logicalResource,
        NodeId resourceVersionsType, CancellationToken cancellationToken)
    {
        var candidates = (await BrowseAllAsync(logicalResource, cancellationToken).ConfigureAwait(false))
            .Where(reference => reference.NodeClass == NodeClass.Object &&
                string.Equals(reference.BrowseName.Name, "Versions", StringComparison.Ordinal))
            .ToArray();
        var versions = Unique(candidates, "Versions");
        if (versions.TypeDefinition is not { } actualType ||
            !await session.IsTypeOrSubtypeAsync(actualType, resourceVersionsType, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "The logical Resource has no ResourceVersionsType component named Versions.");
        }
        return versions;
    }

    private async ValueTask<IReadOnlyList<OpcUaReference>> FindGroupsAsync(NodeId registryRoot, NodeId groupType,
        CancellationToken cancellationToken)
    {
        var candidates = await BrowseAllAsync(registryRoot, cancellationToken).ConfigureAwait(false);
        var matches = new List<OpcUaReference>();
        foreach (var candidate in candidates.Where(reference => reference.NodeClass == NodeClass.Object))
        {
            if (candidate.TypeDefinition is { } actualType &&
                await session.IsTypeOrSubtypeAsync(actualType, groupType, cancellationToken).ConfigureAwait(false))
            {
                matches.Add(candidate);
            }
        }
        return matches;
    }

    private async ValueTask<JsonElement> ReadGroupEntityAsync(OpcUaReference group, string groupCollection,
        string groupId, CancellationToken cancellationToken)
    {
        var expectedXid = GroupXid(groupCollection, groupId);
        var properties = await ReadPropertyValuesAsync(group.Target,
            ["GroupId", "Xid", "Epoch", "CreatedAt", "ModifiedAt", "Name", "Description", "Documentation"],
            cancellationToken).ConfigureAwait(false);
        var entity = JsonNode.Parse(BuildEntityProperties(properties, "GroupId", groupId, expectedXid,
            ["Name"], ["Description", "Documentation"]).GetRawText())!.AsObject();
        await AddModelledPropertiesAsync(group.Target, entity,
            RequireGroupDefinition(groupCollection).Attributes, metaProperties: false, "Group", cancellationToken)
            .ConfigureAwait(false);
        return RegistryJson.Parse(entity.ToJsonString()).RootElement;
    }

    private static string GroupXid(string groupCollection, string groupId) =>
        "/" + groupCollection + "/" + Uri.EscapeDataString(groupId);

    private static string GroupXidFromParent(string groupXid, string resourceCollection,
        string resourceId, string? versionId)
    {
        var resourceXid = groupXid.TrimEnd('/') + "/" + resourceCollection + "/" +
            Uri.EscapeDataString(resourceId);
        return versionId is null
            ? resourceXid
            : resourceXid + "/versions/" + Uri.EscapeDataString(versionId);
    }

    private static bool TryReadUaString(Dictionary<string, OpcUaJsonValue> properties,
        string property, out string value)
    {
        if (properties.TryGetValue(property, out var typed) &&
            typed.BuiltInType == BuiltInType.String &&
            typed.Value.ValueKind == JsonValueKind.String &&
            typed.Value.GetString() is { } text)
        {
            value = text;
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Reads one exact String Property or Variable component of the selected Registry.</summary>
    public async ValueTask<string> ReadStringComponentAsync(NodeId parent, string browseName,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        var child = await ChildAsync(parent, browseName, NodeClass.Variable, cancellationToken).ConfigureAwait(false);
        return await session.ReadStringAsync(child, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one exact Variable component as its JSON scalar value.</summary>
    public async ValueTask<JsonElement> ReadJsonValueComponentAsync(NodeId parent, string browseName,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        var child = await ChildAsync(parent, browseName, NodeClass.Variable, cancellationToken).ConfigureAwait(false);
        return (await session.ReadJsonValueAsync(child, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Reads the String Property Variables in an entity's model-typed Labels object.</summary>
    public async ValueTask<JsonElement> ReadAttributesAsync(NodeId entity, NodeId attributesType,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        return await ReadAttributeContainerAsync(entity, "Labels", attributesType, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads string-valued Resource Meta label Properties from its model-typed <c>MetaLabels</c> Object.</summary>
    public async ValueTask<JsonElement> ReadResourceMetaAttributesAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, NodeId attributesType,
        CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        _ = RequireResourceDefinition(groupXid, resourceCollection);
        _ = new FederationReadRequest(FederationOperation.Entity, groupXid);
        var resource = await FindTypedChildAsync(groupNode, resourceId, resourceType, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAttributeContainerAsync(resource.Target, "MetaLabels", attributesType, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> ReadAttributeContainerAsync(NodeId entity, string containerName,
        NodeId attributesType, CancellationToken cancellationToken)
    {
        var references = await BrowseAllAsync(entity, cancellationToken).ConfigureAwait(false);
        var candidates = references.Where(reference => reference.NodeClass == NodeClass.Object &&
            string.Equals(reference.BrowseName.Name, containerName, StringComparison.Ordinal)).ToArray();
        var labels = new List<OpcUaReference>();
        foreach (var reference in candidates)
        {
            if (reference.TypeDefinition is { } actualType &&
                await session.IsTypeOrSubtypeAsync(actualType, attributesType, cancellationToken).ConfigureAwait(false))
            {
                labels.Add(reference);
            }
        }

        if (labels.Count == 0)
        {
            if (candidates.Length != 0)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The {containerName} component is not an AttributesType Object.");
            }
            return RegistryJson.Parse("{}").RootElement;
        }
        var container = Unique(labels, containerName);
        var attributes = await BrowseAllAsync(container.Target, cancellationToken).ConfigureAwait(false);
        var variables = attributes.Where(reference => reference.NodeClass == NodeClass.Variable).ToArray();
        if (variables.Any(reference => string.IsNullOrEmpty(reference.BrowseName.Name)))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                $"A {containerName} Property has an empty BrowseName.");
        }
        var properties = new Dictionary<string, List<OpcUaReference>>(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            var name = variable.BrowseName.Name!;
            if (!properties.TryGetValue(name, out var matches))
            {
                matches = [];
                properties.Add(name, matches);
            }
            matches.Add(variable);
        }
        var result = new JsonObject();
        foreach (var pair in properties)
        {
            var value = await session.ReadStringAsync(Unique(pair.Value, pair.Key).Target, cancellationToken)
                .ConfigureAwait(false);
            result[pair.Key] = JsonValue.Create(value);
        }
        return RegistryJson.Parse(result.ToJsonString()).RootElement;
    }

    /// <summary>Resolves one named Object component and verifies its FileType.</summary>
    public async ValueTask<NodeId> FindFileComponentAsync(NodeId parent, string browseName,
        NodeId fileType, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        var references = await BrowseAllAsync(parent, cancellationToken).ConfigureAwait(false);
        var matches = new List<OpcUaReference>();
        foreach (var reference in references.Where(reference => reference.BrowseName.Name == browseName))
        {
            if (reference.NodeClass == NodeClass.Object && reference.TypeDefinition is { } actualType &&
                await session.IsTypeOrSubtypeAsync(actualType, fileType, cancellationToken).ConfigureAwait(false))
            {
                matches.Add(reference);
            }
        }
        return Unique(matches, browseName).Target;
    }

    /// <summary>Reads the authoritative <c>Capabilities</c> JSON document through its FileType Object.</summary>
    public ValueTask<System.Text.Json.JsonDocument> ReadCapabilitiesAsync(NodeId root,
        CancellationToken cancellationToken = default) => ReadJsonAsync(root, "Capabilities", cancellationToken);

    /// <summary>Reads the <c>Model</c> JSON document through its FileType Object.</summary>
    public ValueTask<System.Text.Json.JsonDocument> ReadModelAsync(NodeId root,
        CancellationToken cancellationToken = default) => ReadJsonAsync(root, "Model", cancellationToken);

    /// <summary>Reads and compiles the Registry Model used to validate xRegistry collection names and behavior.</summary>
    public async ValueTask<RegistryModel> ReadModelDefinitionAsync(NodeId root,
        RegistryModelCompilationOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureSpecVersionValidated();
        using var document = await ReadModelAsync(root, cancellationToken).ConfigureAwait(false);
        return CompileModelDefinition(document.RootElement, options);
    }

    internal RegistryModel CompileModelDefinition(JsonElement document,
        RegistryModelCompilationOptions? options = null)
    {
        EnsureSpecVersionValidated();
        try
        {
            registryModel = RegistryModel.Compile(RegistryJson.FromElement(document), options);
            return registryModel;
        }
        catch (RegistryException exception)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The Registry Model is invalid or could not be resolved.",
                $"{exception.Diagnostic.Code} at {exception.Diagnostic.Path}", exception);
        }
    }

    private async ValueTask<System.Text.Json.JsonDocument> ReadJsonAsync(NodeId root, string name,
        CancellationToken cancellationToken)
    {
        EnsureSpecVersionValidated();
        var file = await FindFileComponentAsync(root, name, s_fileType, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        try
        {
            return System.Text.Json.JsonDocument.Parse(bytes);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                $"The {name} file is not a JSON document.", innerException: ex);
        }
    }

    private async ValueTask<NodeId> ChildAsync(NodeId root, string name, NodeClass nodeClass,
        CancellationToken cancellationToken)
    {
        var matches = (await BrowseAllCoreAsync(root, cancellationToken).ConfigureAwait(false))
            .Where(reference => reference.BrowseName.Name == name && reference.NodeClass == nodeClass).ToArray();
        return Unique(matches, name).Target;
    }

    private async ValueTask<Dictionary<string, OpcUaJsonValue>> ReadPropertyValuesAsync(NodeId entity,
        IReadOnlyCollection<string> propertyNames, CancellationToken cancellationToken)
    {
        var references = await BrowseAllAsync(entity, cancellationToken).ConfigureAwait(false);
        var groups = references.Where(reference =>
                reference.NodeClass == NodeClass.Variable && reference.BrowseName.Name is { } name &&
                propertyNames.Contains(name, StringComparer.Ordinal))
            .GroupBy(reference => reference.BrowseName.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var values = new Dictionary<string, OpcUaJsonValue>(StringComparer.Ordinal);
        foreach (var pair in groups)
        {
            values.Add(pair.Key, await session.ReadJsonValueAsync(
                Unique(pair.Value, pair.Key).Target, cancellationToken).ConfigureAwait(false));
        }
        return values;
    }

    private async ValueTask AddModelledPropertiesAsync(NodeId entityNode, JsonObject entity,
        IReadOnlyDictionary<string, RegistryAttributeDefinition> declarations, bool metaProperties,
        string entityName, CancellationToken cancellationToken)
    {
        var definitions = declarations
            .Where(static entry => !entry.Value.IsSystemDefined && entry.Key != "*")
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
        if (definitions.Count == 0)
        {
            return;
        }

        var values = new JsonObject();
        var browseNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in await BrowseAllAsync(entityNode, cancellationToken).ConfigureAwait(false))
        {
            if (property.NodeClass != NodeClass.Variable || string.IsNullOrEmpty(property.BrowseName.Name))
            {
                continue;
            }

            var browseName = property.BrowseName.Name;
            var attributeName = ModelAttributeName(browseName, metaProperties);
            if (!definitions.TryGetValue(attributeName, out var definition))
            {
                continue;
            }
            if (browseNames.TryGetValue(attributeName, out var priorBrowseName))
            {
                throw new FederationException(FederationErrorCode.Ambiguous,
                    $"The {entityName} has multiple Properties for model attribute '{attributeName}': " +
                    $"'{priorBrowseName}' and '{browseName}'.");
            }

            var value = await session.ReadJsonValueAsync(property.Target, cancellationToken).ConfigureAwait(false);
            if (value.BuiltInType == BuiltInType.ExtensionObject && string.IsNullOrEmpty(value.DataTypeName))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The {entityName} Property '{browseName}' has no resolved OPC UA DataType.");
            }

            values.Add(attributeName, NormalizeModelValue(value.Value, definition));
            browseNames.Add(attributeName, browseName);
        }

        RegistryMetadataValidationResult validated;
        try
        {
            validated = RegistryMetadataValidator.Validate(
                RegistryJson.Parse(values.ToJsonString()),
                definitions,
                new RegistryMetadataValidationOptions
                {
                    Mode = RegistryMetadataMode.CompleteEntity,
                    Model = registryModel
                },
                cancellationToken);
        }
        catch (RegistryException exception)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                $"The {entityName} contains a Property that does not conform to the xRegistry Model.",
                $"{exception.Diagnostic.Code} at {exception.Diagnostic.Path}", exception);
        }

        foreach (var property in validated.Metadata.RootElement.EnumerateObject())
        {
            entity[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    private static string ModelAttributeName(string browseName, bool metaProperties)
    {
        if (metaProperties && browseName.StartsWith("Meta", StringComparison.Ordinal))
        {
            browseName = browseName["Meta".Length..];
        }
        return LowerAscii(browseName);
    }

    private static string LowerAscii(string value)
    {
        var characters = value.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] is >= 'A' and <= 'Z')
            {
                characters[index] = (char)(characters[index] + ('a' - 'A'));
            }
        }
        return new string(characters);
    }

    private static JsonNode? NormalizeModelValue(JsonElement value, RegistryAttributeDefinition definition)
    {
        if (definition.Type == RegistryValueType.Object && value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var wildcard = definition.Attributes.GetValueOrDefault("*");
            foreach (var property in value.EnumerateObject())
            {
                var name = LowerAscii(property.Name);
                if (!seen.Add(name))
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        $"A structured Property contains duplicate model field '{name}'.");
                }
                var childDefinition = definition.Attributes.GetValueOrDefault(name) ?? wildcard;
                result.Add(name, childDefinition is null
                    ? JsonNode.Parse(property.Value.GetRawText())
                    : NormalizeModelValue(property.Value, childDefinition));
            }
            return result;
        }

        if (definition.Type == RegistryValueType.Array && value.ValueKind == JsonValueKind.Array &&
            definition.Item is { } item)
        {
            var result = new JsonArray();
            foreach (var element in value.EnumerateArray())
            {
                result.Add(NormalizeModelValue(element, item));
            }
            return result;
        }

        if (definition.Type == RegistryValueType.Map && value.ValueKind == JsonValueKind.Object &&
            definition.Item is { } mapItem)
        {
            var result = new JsonObject();
            foreach (var property in value.EnumerateObject())
            {
                if (result.ContainsKey(property.Name))
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        $"A structured Property contains duplicate map key '{property.Name}'.");
                }
                result.Add(property.Name, NormalizeModelValue(property.Value, mapItem));
            }
            return result;
        }

        return JsonNode.Parse(value.GetRawText());
    }

    private RegistryGroupDefinition RequireGroupDefinition(string groupCollection)
    {
        var model = registryModel ?? throw new FederationException(FederationErrorCode.UnsupportedOperation,
            "The Registry Model must be read and compiled before collection operations.");
        _ = new FederationReadRequest(FederationOperation.Collection, "/" + groupCollection);
        return model.Groups.TryGetValue(groupCollection, out var group)
            ? group
            : throw new FederationException(FederationErrorCode.NotFound,
                $"The Registry Model has no group collection '{groupCollection}'.");
    }

    private RegistryResourceDefinition RequireResourceDefinition(string groupXid, string resourceCollection)
    {
        var model = registryModel ?? throw new FederationException(FederationErrorCode.UnsupportedOperation,
            "The Registry Model must be read and compiled before collection operations.");
        var parts = new FederationReadRequest(FederationOperation.Entity, groupXid).Target
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (parts.Length != 2 || !model.Groups.TryGetValue(parts[0], out var group))
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "The selected Group does not belong to a modeled collection.");
        }
        _ = new FederationReadRequest(FederationOperation.Collection, groupXid.TrimEnd('/') + "/" + resourceCollection);
        return group.Resources.TryGetValue(resourceCollection, out var resource)
            ? resource
            : throw new FederationException(FederationErrorCode.NotFound,
                $"The Registry Model has no resource collection '{resourceCollection}' in '{parts[0]}'.");
    }

    private async ValueTask<OpcUaReference> FindTypedChildAsync(NodeId parent, string browseName, NodeId expectedType,
        CancellationToken cancellationToken)
    {
        var references = await FindTypedChildrenAsync(parent, expectedType, cancellationToken).ConfigureAwait(false);
        return Unique(references.Where(reference =>
                string.Equals(reference.BrowseName.Name, browseName, StringComparison.Ordinal)).ToArray(), browseName);
    }

    private async ValueTask<IReadOnlyList<OpcUaReference>> FindTypedChildrenAsync(NodeId parent, NodeId expectedType,
        CancellationToken cancellationToken)
    {
        var references = await BrowseAllAsync(parent, cancellationToken).ConfigureAwait(false);
        var matches = new List<OpcUaReference>();
        foreach (var reference in references.Where(reference => reference.NodeClass == NodeClass.Object))
        {
            if (reference.TypeDefinition is { } actualType &&
                await session.IsTypeOrSubtypeAsync(actualType, expectedType, cancellationToken).ConfigureAwait(false))
            {
                matches.Add(reference);
            }
        }
        return matches;
    }

    private static JsonElement BuildEntityProperties(Dictionary<string, OpcUaJsonValue> properties,
        string identifierProperty, string expectedIdentifier, string expectedXid,
        IReadOnlyCollection<string> requiredStringProperties,
        IReadOnlyCollection<string> optionalStringProperties)
    {
        if (!properties.TryGetValue(identifierProperty, out var identifier) ||
            identifier.BuiltInType != BuiltInType.String ||
            identifier.Value.ValueKind != JsonValueKind.String ||
            identifier.Value.GetString() != expectedIdentifier)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                $"The entity's {identifierProperty} Property does not match the selected entity.");
        }

        var entity = new JsonObject { [CoreName(identifierProperty)] = JsonNode.Parse(identifier.Value.GetRawText()) };
        foreach (var name in requiredStringProperties)
        {
            if (!properties.TryGetValue(name, out var value) ||
                value.BuiltInType != BuiltInType.String || value.Value.ValueKind != JsonValueKind.String)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The entity has no valid mandatory {name} Property.");
            }
            entity[CoreName(name)] = JsonNode.Parse(value.Value.GetRawText());
        }

        if (properties.TryGetValue("Xid", out var xid))
        {
            if (xid.BuiltInType != BuiltInType.String ||
                xid.Value.ValueKind != JsonValueKind.String || xid.Value.GetString() != expectedXid)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The entity's Xid Property contradicts the selected AddressSpace path.");
            }
        }
        entity["xid"] = JsonValue.Create(expectedXid);
        if (properties.TryGetValue("Epoch", out var epoch))
        {
            if (epoch.BuiltInType != BuiltInType.UInt32 ||
                epoch.Value.ValueKind != JsonValueKind.Number || !epoch.Value.TryGetUInt32(out _))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The entity's Epoch Property is not a UInt32.");
            }
            entity["epoch"] = JsonNode.Parse(epoch.Value.GetRawText());
        }
        foreach (var name in new[] { "CreatedAt", "ModifiedAt" })
        {
            if (properties.TryGetValue(name, out var timestamp))
            {
                if (timestamp.BuiltInType != BuiltInType.DateTime ||
                    timestamp.Value.ValueKind != JsonValueKind.String ||
                    !IsRfc3339Timestamp(timestamp.Value.GetString()!))
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        $"The entity's {name} Property is not a zoned DateTime.");
                }
                entity[CoreName(name)] = JsonNode.Parse(timestamp.Value.GetRawText());
            }
        }
        foreach (var name in optionalStringProperties)
        {
            if (!properties.TryGetValue(name, out var value))
            {
                continue;
            }
            if (value.BuiltInType != BuiltInType.String || value.Value.ValueKind != JsonValueKind.String)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    $"The entity's optional {name} Property is not a String.");
            }
            entity[CoreName(name)] = JsonNode.Parse(value.Value.GetRawText());
        }
        return RegistryJson.Parse(entity.ToJsonString()).RootElement;
    }

    private static string CoreName(string propertyName) => propertyName.ToLowerInvariant();

    private static bool IsRfc3339Timestamp(string value)
    {
        var normalized = value.Replace('t', 'T').Replace('z', 'Z');
        var hasZone = normalized.EndsWith('Z') ||
            normalized.Length >= 6 && normalized[^6] is '+' or '-' &&
            normalized[^3] == ':' && char.IsAsciiDigit(normalized[^5]) &&
            char.IsAsciiDigit(normalized[^4]) && char.IsAsciiDigit(normalized[^2]) &&
            char.IsAsciiDigit(normalized[^1]);
        return hasZone && DateTimeOffset.TryParseExact(normalized,
            ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static OpcUaReference Unique(IReadOnlyCollection<OpcUaReference> matches, string name) =>
        matches.Count switch
        {
            1 => matches.First(),
            0 => throw new FederationException(FederationErrorCode.NotFound, $"The Registry has no {name} component."),
            _ => throw new FederationException(FederationErrorCode.Ambiguous, $"The Registry has several {name} components."),
        };

    private void EnsureSpecVersionValidated()
    {
        if (!specVersionValidated)
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion,
                "SpecVersion must be read and accepted before interpreting Registry contents.");
        }
    }

    private async ValueTask ReleaseAsync(byte[] continuation, Exception failure)
    {
        try
        {
            await session.BrowseNextAsync(continuation, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception releaseFailure)
        {
            failure.Data["OpcUa.ContinuationReleaseFailure"] = releaseFailure;
        }
    }
}
