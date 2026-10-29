// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Buffers;
using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>Implements the OPC UA operations used by Registry reads and document replacement.</summary>
public sealed class OpcUaSessionAdapter : IOpcUaRegistrySession
{
    private const int MaxSubtypeDepth = 32;
    private const int MaxReferencesPerBrowse = 1_000_000;
    private static readonly Lazy<ITelemetryContext> s_telemetry =
        new(() => DefaultTelemetry.Create(static _ => { }));
    private readonly ISessionClient session;
    private readonly NodeId registryType;
    private readonly uint maxReferencesPerNode;
    private NamespaceTable? namespaceUris;

    /// <summary>Creates an adapter; <paramref name="registryType"/> is the server's RegistryType NodeId.</summary>
    public OpcUaSessionAdapter(ISessionClient session, NodeId registryType, uint maxReferencesPerNode = 1000)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfZero(maxReferencesPerNode);
        this.session = session;
        this.registryType = registryType;
        this.maxReferencesPerNode = maxReferencesPerNode;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ReadServerArrayAsync(CancellationToken cancellationToken) =>
        ReadStringArrayAsync(new NodeId(2254u, 0), "ServerArray", cancellationToken);

    /// <inheritdoc />
    public async ValueTask<NamespaceTable> ReadNamespaceArrayAsync(CancellationToken cancellationToken)
    {
        var values = await ReadStringArrayAsync(new NodeId(2255u, 0), "NamespaceArray", cancellationToken)
            .ConfigureAwait(false);
        namespaceUris = new NamespaceTable(values);
        return namespaceUris;
    }

    /// <inheritdoc />
    public async ValueTask<OpcUaExternalNodeId> MapExternalNodeIdAsync(ExpandedNodeId nodeId,
        CancellationToken cancellationToken)
    {
        if (nodeId.IsNull)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The OPC UA ExternalReference is a null ExpandedNodeId.");
        }

        var serverArray = await ReadServerArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (nodeId.ServerIndex >= serverArray.Count)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The ExpandedNodeId ServerIndex is outside the source ServerArray.");
        }

        var serverApplicationUri = serverArray[checked((int)nodeId.ServerIndex)];
        if (string.IsNullOrEmpty(serverApplicationUri))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The ExpandedNodeId ServerIndex does not identify a nonempty Application URI.");
        }

        string namespaceUri;
        if (!string.IsNullOrEmpty(nodeId.NamespaceUri))
        {
            namespaceUri = nodeId.NamespaceUri;
        }
        else
        {
            var namespaceArray = await ReadStringArrayAsync(new NodeId(2255u, 0), "NamespaceArray",
                cancellationToken).ConfigureAwait(false);
            if (nodeId.NamespaceIndex >= namespaceArray.Count)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The ExpandedNodeId NamespaceIndex is outside the source NamespaceArray.");
            }

            namespaceUri = namespaceArray[nodeId.NamespaceIndex];
            if (string.IsNullOrEmpty(namespaceUri))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The ExpandedNodeId NamespaceIndex does not identify a nonempty namespace URI.");
            }
        }

        object identifier;
        switch (nodeId.IdType)
        {
            case IdType.Numeric when nodeId.TryGetValue(out uint numeric):
                identifier = numeric;
                break;
            case IdType.String when nodeId.TryGetValue(out string? text) && text is not null:
                identifier = text;
                break;
            case IdType.Guid when nodeId.TryGetValue(out Guid guid):
                identifier = guid;
                break;
            case IdType.Opaque when nodeId.TryGetValue(out ByteString opaque):
                identifier = opaque.ToArray();
                break;
            default:
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The ExpandedNodeId identifier does not match its OPC UA identifier type.");
        }
        return new(serverApplicationUri, namespaceUri, nodeId.IdType, identifier);
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsRegistryNodeAsync(NodeId node, CancellationToken cancellationToken)
    {
        var definitions = await BrowseOnce(node, BrowseDirection.Forward, ReferenceTypeIds.HasTypeDefinition,
            cancellationToken).ConfigureAwait(false);
        foreach (var definition in definitions)
        {
            if (await IsTypeOrSubtypeAsync(Local(definition.NodeId), registryType, cancellationToken)
                .ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsTypeOrSubtypeAsync(NodeId actualType, NodeId declaredType,
        CancellationToken cancellationToken)
    {
        var current = actualType;
        var visited = new HashSet<NodeId>();
        for (var depth = 0; depth < MaxSubtypeDepth && !current.IsNull; depth++)
        {
            if (current == declaredType)
            {
                return true;
            }
            if (!visited.Add(current))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The OPC UA ObjectType hierarchy contains a cycle.");
            }
            var parents = await BrowseOnce(current, BrowseDirection.Inverse, ReferenceTypeIds.HasSubtype,
                cancellationToken).ConfigureAwait(false);
            if (parents.Count == 0)
            {
                return false;
            }
            if (parents.Count != 1)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "An OPC UA ObjectType has competing supertypes.");
            }
            current = Local(parents[0].NodeId);
        }
        if (!current.IsNull)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded,
                "The OPC UA ObjectType hierarchy exceeds the subtype-depth budget.");
        }
        return false;
    }

    /// <inheritdoc />
    public async ValueTask<OpcUaBrowsePage> BrowseAsync(NodeId node, CancellationToken cancellationToken)
    {
        var response = await session.BrowseAsync(new RequestHeader(), new ViewDescription(), maxReferencesPerNode,
            [Describe(node, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences)],
            cancellationToken).ConfigureAwait(false);
        return await ToPageAsync(response.Results).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OpcUaBrowsePage> BrowseNextAsync(byte[] continuationPoint, bool release,
        CancellationToken cancellationToken)
    {
        var response = await session.BrowseNextAsync(new RequestHeader(), release, [new ByteString(continuationPoint)],
            cancellationToken).ConfigureAwait(false);
        if (release)
        {
            _ = Single(response.Results, IsBad);
            return new OpcUaBrowsePage([]);
        }
        return await ToPageAsync(response.Results, continuationPoint).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<uint> OpenAsync(NodeId file, byte mode, CancellationToken cancellationToken)
    {
        var output = await CallAsync(file, MethodIds.FileType_Open, [new Variant(mode)], cancellationToken)
            .ConfigureAwait(false);
        return output.Count == 1 && output[0].TryGetValue(out uint handle)
            ? handle
            : throw new FederationException(FederationErrorCode.InvalidPackage, "FileType Open returned no handle.");
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadAsync(NodeId file, uint handle, int length, CancellationToken cancellationToken)
    {
        var output = await CallAsync(file, MethodIds.FileType_Read, [new Variant(handle), new Variant(length)],
            cancellationToken).ConfigureAwait(false);
        return output.Count == 1 && output[0].TryGetValue(out ByteString data)
            ? data.ToArray()
            : throw new FederationException(FederationErrorCode.InvalidPackage, "FileType Read returned no data.");
    }

    /// <inheritdoc />
    public async ValueTask SetPositionAsync(NodeId file, uint handle, ulong position,
        CancellationToken cancellationToken) =>
        await CallAsync(file, MethodIds.FileType_SetPosition, [new Variant(handle), new Variant(position)],
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask WriteAsync(NodeId file, uint handle, byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        _ = await CallAsync(file, MethodIds.FileType_Write,
            [new Variant(handle), new Variant(new ByteString(data))], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WriteValueAsync(NodeId variable, Variant value, CancellationToken cancellationToken)
    {
        var response = await session.WriteAsync(new RequestHeader(),
            [new WriteValue
            {
                NodeId = variable,
                AttributeId = Attributes.Value,
                Value = new DataValue(value),
            }], cancellationToken).ConfigureAwait(false);
        _ = Single(response.Results, StatusCode.IsBad);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Variant>> CallMethodAsync(NodeId target, string methodBrowseName,
        IReadOnlyList<Variant> inputArguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(methodBrowseName);
        ArgumentNullException.ThrowIfNull(inputArguments);
        var methods = await BrowseOnce(target, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences,
            cancellationToken).ConfigureAwait(false);
        var matches = methods.Where(reference => reference.NodeClass == NodeClass.Method &&
            string.Equals(reference.BrowseName.Name, methodBrowseName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new FederationException(matches.Length == 0 ? FederationErrorCode.NotFound :
                FederationErrorCode.Ambiguous, $"The OPC UA Object does not have one {methodBrowseName} Method.");
        }
        return await CallAsync(target, Local(matches[0].NodeId), inputArguments.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string> ReadStringAsync(NodeId variable, CancellationToken cancellationToken)
    {
        var response = await session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = variable, AttributeId = Attributes.Value }], cancellationToken)
            .ConfigureAwait(false);
        var result = Single(response.Results, r => StatusCode.IsBad(r.StatusCode));
        return result.WrappedValue.TryGetValue(out string? text) && text is not null
            ? text
            : throw new FederationException(FederationErrorCode.InvalidPackage, "The Variable is not a String.");
    }

    /// <inheritdoc />
    public async ValueTask<ExpandedNodeId> ReadExpandedNodeIdAsync(NodeId variable,
        CancellationToken cancellationToken)
    {
        var response = await session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = variable, AttributeId = Attributes.Value }], cancellationToken)
            .ConfigureAwait(false);
        var result = Single(response.Results, r => StatusCode.IsBad(r.StatusCode));
        return result.WrappedValue.TryGetValue(out ExpandedNodeId reference) &&
            !reference.IsNull
                ? reference
                : throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The Variable is not a non-null ExpandedNodeId.");
    }

    private async ValueTask<IReadOnlyList<string>> ReadStringArrayAsync(NodeId node, string name,
        CancellationToken cancellationToken)
    {
        var response = await session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = node, AttributeId = Attributes.Value }],
            cancellationToken).ConfigureAwait(false);
        var result = Single(response.Results, r => StatusCode.IsBad(r.StatusCode));
        IEnumerable<string>? values = result.WrappedValue.AsBoxedObject() switch
        {
            string[] array => (IEnumerable<string>)array,
            ArrayOf<string> array => array.ToArray(),
            ImmutableArray<string> immutable => (IEnumerable<string>)immutable,
            _ => null,
        };
        if (values is null)
        {
            var actualType = result.WrappedValue.AsBoxedObject()?.GetType().FullName ?? "null";
            throw new FederationException(FederationErrorCode.InvalidPackage,
                $"The Server {name} is not a String array (received {actualType}).");
        }

        return values.ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<OpcUaJsonValue> ReadJsonValueAsync(NodeId variable, CancellationToken cancellationToken)
    {
        var response = await session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = variable, AttributeId = Attributes.Value }], cancellationToken)
            .ConfigureAwait(false);
        var result = Single(response.Results, r => StatusCode.IsBad(r.StatusCode));
        var value = result.WrappedValue.AsBoxedObject();
        if (value is ExtensionObject extensionObject)
        {
            return await JsonExtensionObjectAsync(extensionObject, cancellationToken).ConfigureAwait(false);
        }
        return value switch
        {
            string text => JsonScalar(BuiltInType.String, writer => writer.WriteStringValue(text)),
            bool boolean => JsonScalar(BuiltInType.Boolean, writer => writer.WriteBooleanValue(boolean)),
            sbyte number => JsonScalar(BuiltInType.SByte, writer => writer.WriteNumberValue(number)),
            byte number => JsonScalar(BuiltInType.Byte, writer => writer.WriteNumberValue(number)),
            short number => JsonScalar(BuiltInType.Int16, writer => writer.WriteNumberValue(number)),
            ushort number => JsonScalar(BuiltInType.UInt16, writer => writer.WriteNumberValue(number)),
            int number => JsonScalar(BuiltInType.Int32, writer => writer.WriteNumberValue(number)),
            uint number => JsonScalar(BuiltInType.UInt32, writer => writer.WriteNumberValue(number)),
            long number => JsonScalar(BuiltInType.Int64, writer => writer.WriteNumberValue(number)),
            ulong number => JsonScalar(BuiltInType.UInt64, writer => writer.WriteNumberValue(number)),
            float number when float.IsFinite(number) => JsonScalar(BuiltInType.Float, writer => writer.WriteNumberValue(number)),
            double number when double.IsFinite(number) => JsonScalar(BuiltInType.Double, writer => writer.WriteNumberValue(number)),
            DateTimeUtc timestamp => JsonScalar(BuiltInType.DateTime, writer => writer.WriteStringValue(
                DateTime.SpecifyKind(timestamp.ToDateTime(), DateTimeKind.Utc)
                    .ToString("O", CultureInfo.InvariantCulture))),
            DateTime timestamp => JsonScalar(BuiltInType.DateTime, writer => writer.WriteStringValue(
                (timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() :
                    DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture))),
            _ => throw new FederationException(FederationErrorCode.UnsupportedOperation,
                $"The OPC UA Variable's DataType has no supported JSON mapping (received {value?.GetType().FullName ?? "null"}).")
        };
    }

    private async ValueTask<OpcUaJsonValue> JsonExtensionObjectAsync(ExtensionObject value,
        CancellationToken cancellationToken)
    {
        var context = ServiceMessageContext.CreateEmpty(s_telemetry.Value);
        string? json = null;
        string? dataTypeName = null;
        if (value.TryGetValue<IEncodeable>(out var encodeable, context))
        {
            using var encoder = new JsonEncoder(context, JsonEncoderOptions.Compact);
            encodeable.Encode(encoder);
            json = encoder.CloseAndReturnText();
        }
        else if (!value.TryGetAsJson(out json, context))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The OPC UA ExtensionObject cannot be decoded as JSON.");
        }
        if (json is null)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The OPC UA ExtensionObject decoder returned no JSON value.");
        }

        if (dataTypeName is null)
        {
            var encoding = Local(value.TypeId);
            var dataTypes = await BrowseOnce(encoding, BrowseDirection.Inverse, ReferenceTypeIds.HasEncoding,
                cancellationToken).ConfigureAwait(false);
            if (dataTypes.Count != 1 || string.IsNullOrEmpty(dataTypes[0].BrowseName.Name))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The OPC UA ExtensionObject encoding does not identify one DataType.");
            }
            dataTypeName = dataTypes[0].BrowseName.Name;
        }

        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone(), BuiltInType.ExtensionObject, dataTypeName);
    }

    private static OpcUaJsonValue JsonScalar(BuiltInType builtInType, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new(document.RootElement.Clone(), builtInType);
    }
    /// <inheritdoc />
    public async ValueTask CloseAsync(NodeId file, uint handle, CancellationToken cancellationToken) =>
        await CallAsync(file, MethodIds.FileType_Close, [new Variant(handle)], cancellationToken).ConfigureAwait(false);

    private async ValueTask<IReadOnlyList<ReferenceDescription>> BrowseOnce(NodeId node, BrowseDirection direction,
        NodeId referenceType, CancellationToken cancellationToken)
    {
        var references = new List<ReferenceDescription>();
        var response = await session.BrowseAsync(new RequestHeader(), new ViewDescription(), maxReferencesPerNode,
            [Describe(node, direction, referenceType)], cancellationToken).ConfigureAwait(false);
        var page = Single(response.Results, IsBad);
        while (true)
        {
            var continuation = page.ContinuationPoint.Length == 0 ? null : page.ContinuationPoint.ToArray();
            try
            {
                references.AddRange(page.References.ToArray() ?? []);
                if (references.Count > MaxReferencesPerBrowse)
                {
                    throw new FederationException(FederationErrorCode.LimitExceeded,
                        "The OPC UA type hierarchy exceeds the reference budget.");
                }
                if (continuation is null)
                {
                    return references;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var next = await session.BrowseNextAsync(new RequestHeader(), false, [new ByteString(continuation)],
                    cancellationToken).ConfigureAwait(false);
                page = Single(next.Results, IsBad);
            }
            catch (Exception exception) when (continuation is not null)
            {
                await ReleaseAsync(continuation, exception).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async ValueTask<IReadOnlyList<Variant>> CallAsync(NodeId target, NodeId method, Variant[] input,
        CancellationToken cancellationToken)
    {
        var response = await session.CallAsync(new RequestHeader(),
            [new CallMethodRequest { ObjectId = target, MethodId = method, InputArguments = input }],
            cancellationToken).ConfigureAwait(false);
        var result = Single(response.Results, r => StatusCode.IsBad(r.StatusCode));
        return result.OutputArguments.ToArray() ?? [];
    }

    private static BrowseDescription Describe(NodeId node, BrowseDirection direction, NodeId referenceType) => new()
    {
        NodeId = node,
        BrowseDirection = direction,
        ReferenceTypeId = referenceType,
        IncludeSubtypes = true,
        NodeClassMask = 0,
        ResultMask = (uint)BrowseResultMask.All,
    };

    private OpcUaBrowsePage ToPage(BrowseResult result)
    {
        var references = (result.References.ToArray() ?? [])
            .Select(r => new OpcUaReference(Local(r.NodeId), r.BrowseName, r.NodeClass,
                r.TypeDefinition.IsNull ? null : Local(r.TypeDefinition)))
            .ToArray();
        return new OpcUaBrowsePage(references,
            result.ContinuationPoint.Length == 0 ? null : result.ContinuationPoint.ToArray());
    }

    private async ValueTask<OpcUaBrowsePage> ToPageAsync(ArrayOf<BrowseResult> results,
        byte[]? previousContinuation = null)
    {
        if (results.Count != 1)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The server returned an unexpected result count.");
        }
        var result = results[0];
        if (IsBad(result))
        {
            var failure = new FederationException(FederationErrorCode.Unavailable,
                "The OPC UA service reported a failure.");
            var returnedContinuation = result.ContinuationPoint.Length == 0
                ? null
                : result.ContinuationPoint.ToArray();
            if (returnedContinuation is not null &&
                (previousContinuation is null || !returnedContinuation.AsSpan().SequenceEqual(previousContinuation)))
            {
                await ReleaseAsync(returnedContinuation, failure).ConfigureAwait(false);
            }
            throw failure;
        }

        var outstanding = result.ContinuationPoint.Length == 0 ? null : result.ContinuationPoint.ToArray();
        try
        {
            return ToPage(result);
        }
        catch (Exception exception)
        {
            if (outstanding is not null)
            {
                await ReleaseAsync(outstanding, exception).ConfigureAwait(false);
            }
            if (previousContinuation is not null)
            {
                exception.Data["OpcUa.ContinuationConsumed"] = true;
            }
            throw;
        }
    }

    private static T Single<T>(ArrayOf<T> results, Func<T, bool> failed)
    {
        if (results.Count != 1)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The server returned an unexpected result count.");
        }
        var result = results[0];
        return failed(result)
            ? throw new FederationException(FederationErrorCode.Unavailable, "The OPC UA service reported a failure.")
            : result;
    }

    private NodeId Local(ExpandedNodeId id)
    {
        var namespaces = namespaceUris ?? throw new FederationException(FederationErrorCode.InvalidPackage,
            "The Server NamespaceArray must be read before Browse results are interpreted.");
        var node = ExpandedNodeId.ToNodeId(id, namespaces, updateNamespaceTable: false);
        return node is { IsNull: false }
            ? node
            : throw new FederationException(FederationErrorCode.NotFound,
                "A Browse result refers to a namespace or server that cannot be mapped in this Session.");
    }

    private static bool IsBad(BrowseResult result) => StatusCode.IsBad(result.StatusCode);

    private async ValueTask ReleaseAsync(byte[] continuation, Exception failure)
    {
        try
        {
            await session.BrowseNextAsync(new RequestHeader(), true, [new ByteString(continuation)],
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception releaseFailure)
        {
            failure.Data["OpcUa.ContinuationReleaseFailure"] = releaseFailure;
        }
    }
}
