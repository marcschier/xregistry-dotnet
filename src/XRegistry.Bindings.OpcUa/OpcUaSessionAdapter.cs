// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Collections.Concurrent;
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
    private readonly int maxBrowsePages;
    private readonly ConcurrentDictionary<(NodeId Session, NodeId File, uint Handle), byte> readHandles = new();
    private NamespaceTable? namespaceUris;

    /// <summary>Creates an adapter; <paramref name="registryType"/> is the server's RegistryType NodeId.</summary>
    public OpcUaSessionAdapter(ISessionClient session, NodeId registryType, uint maxReferencesPerNode = 1000,
        int maxBrowsePages = 10_000)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfZero(maxReferencesPerNode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBrowsePages);
        this.session = session;
        this.registryType = registryType;
        this.maxReferencesPerNode = maxReferencesPerNode;
        this.maxBrowsePages = maxBrowsePages;
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
        if (definitions.Count == 0)
        {
            return false;
        }
        if (definitions.Count != 1 || definitions[0].NodeClass != NodeClass.ObjectType)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The OPC UA Registry root must have one ObjectType TypeDefinition.");
        }
        return await IsTypeOrSubtypeAsync(Local(definitions[0].NodeId), registryType, cancellationToken)
            .ConfigureAwait(false);
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
            if (parents[0].NodeClass != NodeClass.ObjectType)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The OPC UA ObjectType hierarchy contains a non-ObjectType supertype.");
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
        var response = await InvokeAsync(() => session.BrowseAsync(new RequestHeader(), new ViewDescription(), maxReferencesPerNode,
            [Describe(node, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences)],
            cancellationToken)).ConfigureAwait(false);
        return await ToPageAsync(response.Results, response.DiagnosticInfos, response.ResponseHeader)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<OpcUaBrowsePage> BrowseNextAsync(byte[] continuationPoint, bool release,
        CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(() => session.BrowseNextAsync(new RequestHeader(), release,
            [new ByteString(continuationPoint)], cancellationToken)).ConfigureAwait(false);
        if (release)
        {
            _ = Single(response.Results, static result => result.StatusCode,
                response.DiagnosticInfos, response.ResponseHeader);
            return new OpcUaBrowsePage([]);
        }
        return await ToPageAsync(response.Results, response.DiagnosticInfos, response.ResponseHeader,
            continuationPoint).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<uint> OpenAsync(NodeId file, byte mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode != 1)
        {
            RequireMutationSecurity(cancellationToken);
        }
        var sourceSession = session.SessionId;
        var output = await CallFileMethodAsync(file, BrowseNames.Open, [new Variant(mode)],
            mode != 1, cancellationToken)
            .ConfigureAwait(false);
        if (output.Count != 1 || !output[0].TryGetValue(out uint handle))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage, "FileType Open returned no handle.");
        }
        if (mode == 1)
        {
            readHandles[(sourceSession, file, handle)] = 0;
        }
        else
        {
            readHandles.TryRemove((sourceSession, file, handle), out _);
        }
        return handle;
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadAsync(NodeId file, uint handle, int length, CancellationToken cancellationToken)
    {
        var output = await CallFileMethodAsync(file, BrowseNames.Read, [new Variant(handle), new Variant(length)],
            mutation: false, cancellationToken).ConfigureAwait(false);
        return output.Count == 1 && output[0].TryGetValue(out ByteString data)
            ? data.ToArray()
            : throw new FederationException(FederationErrorCode.InvalidPackage, "FileType Read returned no data.");
    }

    /// <inheritdoc />
    public async ValueTask SetPositionAsync(NodeId file, uint handle, ulong position,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!readHandles.ContainsKey((session.SessionId, file, handle)))
        {
            RequireMutationSecurity(cancellationToken);
        }
        _ = await CallFileMethodAsync(file, BrowseNames.SetPosition, [new Variant(handle), new Variant(position)],
            !readHandles.ContainsKey((session.SessionId, file, handle)), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(NodeId file, uint handle, byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        RequireMutationSecurity(cancellationToken);
        _ = await CallFileMethodAsync(file, BrowseNames.Write,
            [new Variant(handle), new Variant(new ByteString(data))], mutation: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WriteValueAsync(NodeId variable, Variant value, CancellationToken cancellationToken)
    {
        RequireMutationSecurity(cancellationToken);
        var response = await InvokeAsync(() => session.WriteAsync(new RequestHeader(),
            [new WriteValue
            {
                NodeId = variable,
                AttributeId = Attributes.Value,
                Value = new DataValue(value),
            }], cancellationToken)).ConfigureAwait(false);
        _ = Single(response.Results, static status => status,
            response.DiagnosticInfos, response.ResponseHeader);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Variant>> CallMethodAsync(NodeId target, string methodBrowseName,
        IReadOnlyList<Variant> inputArguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(methodBrowseName);
        ArgumentNullException.ThrowIfNull(inputArguments);
        RequireMutationSecurity(cancellationToken);
        return await CallNamedMethodAsync(target, methodBrowseName, inputArguments.ToArray(),
            mutation: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<Variant>> CallFileMethodAsync(NodeId file, string methodBrowseName,
        Variant[] input, bool mutation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mutation)
        {
            RequireMutationSecurity(cancellationToken);
        }
        if (namespaceUris is null)
        {
            _ = await ReadNamespaceArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        return await CallNamedMethodAsync(file, methodBrowseName, input, mutation, cancellationToken,
            methodNamespace: 0)
            .ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<Variant>> CallNamedMethodAsync(NodeId target, string methodBrowseName,
        Variant[] input, bool mutation, CancellationToken cancellationToken, ushort? methodNamespace = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var methods = await BrowseOnce(target, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences,
            cancellationToken).ConfigureAwait(false);
        var matches = methods.Where(reference => reference.NodeClass == NodeClass.Method &&
            (methodNamespace is null || reference.BrowseName.NamespaceIndex == methodNamespace) &&
            string.Equals(reference.BrowseName.Name, methodBrowseName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new FederationException(matches.Length == 0 ? FederationErrorCode.NotFound :
                FederationErrorCode.Ambiguous, $"The OPC UA Object does not have one {methodBrowseName} Method.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (mutation)
        {
            RequireMutationSecurity(cancellationToken);
        }
        if (matches[0].NodeId.ServerIndex != 0)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The selected Method is outside the connected Server AddressSpace.");
        }
        return await CallAsync(target, Local(matches[0].NodeId), input, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string> ReadStringAsync(NodeId variable, CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(() => session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = variable, AttributeId = Attributes.Value }], cancellationToken))
            .ConfigureAwait(false);
        var result = Single(response.Results, static result => result.StatusCode,
            response.DiagnosticInfos, response.ResponseHeader);
        return result.WrappedValue.TryGetValue(out string? text) && text is not null
            ? text
            : throw new FederationException(FederationErrorCode.InvalidPackage, "The Variable is not a String.");
    }

    /// <inheritdoc />
    public async ValueTask<ExpandedNodeId> ReadExpandedNodeIdAsync(NodeId variable,
        CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(() => session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = variable, AttributeId = Attributes.Value }], cancellationToken))
            .ConfigureAwait(false);
        var result = Single(response.Results, static result => result.StatusCode,
            response.DiagnosticInfos, response.ResponseHeader);
        return result.WrappedValue.TryGetValue(out ExpandedNodeId reference) &&
            !reference.IsNull
                ? reference
                : throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The Variable is not a non-null ExpandedNodeId.");
    }

    private async ValueTask<IReadOnlyList<string>> ReadStringArrayAsync(NodeId node, string name,
        CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(() => session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = node, AttributeId = Attributes.Value }],
            cancellationToken)).ConfigureAwait(false);
        var result = Single(response.Results, static result => result.StatusCode,
            response.DiagnosticInfos, response.ResponseHeader);
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
    public async ValueTask<OpcUaRegistryOriginValue> ReadRegistryOriginAsync(NodeId variable,
        NodeId expectedDataType, CancellationToken cancellationToken)
    {
        if (variable.IsNull || expectedDataType.IsNull)
        {
            throw new ArgumentException("The origin Variable and expected DataType must be non-null.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var typeResponse = await InvokeAsync(() => session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = variable, AttributeId = Attributes.DataType }], cancellationToken))
            .ConfigureAwait(false);
        var declaredType = Single(typeResponse.Results, static result => result.StatusCode,
            typeResponse.DiagnosticInfos, typeResponse.ResponseHeader);
        if (declaredType.WrappedValue.AsBoxedObject() is not NodeId dataType || dataType != expectedDataType)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The OriginRegistry Variable does not declare the expected RegistryOriginDataType.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var response = await InvokeAsync(() => session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = variable, AttributeId = Attributes.Value }], cancellationToken))
            .ConfigureAwait(false);
        var result = Single(response.Results, static result => result.StatusCode,
            response.DiagnosticInfos, response.ResponseHeader);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.WrappedValue.AsBoxedObject() is not ExtensionObject value)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "OriginRegistry is not a scalar ExtensionObject.");
        }
        var dataTypes = await BrowseOnce(Local(value.TypeId), BrowseDirection.Inverse,
            ReferenceTypeIds.HasEncoding, cancellationToken).ConfigureAwait(false);
        if (dataTypes.Count != 1 || dataTypes[0].NodeClass != NodeClass.DataType ||
            Local(dataTypes[0].NodeId) != expectedDataType)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "OriginRegistry does not use the expected RegistryOriginDataType.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var context = ServiceMessageContext.CreateEmpty(s_telemetry.Value);
        try
        {
            if (!value.TryGetAsBinary(out var bytes, context))
            {
                if (!value.TryGetValue<IEncodeable>(out var encodeable, context))
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation,
                        "The OriginRegistry encoding cannot be decoded without losing its typed fields.");
                }
                if (Local(encodeable.TypeId) != expectedDataType)
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        "The decoded OriginRegistry body does not identify the expected RegistryOriginDataType.");
                }
                using var encoder = new BinaryEncoder(context);
                encodeable.Encode(encoder);
                bytes = ByteString.From(encoder.CloseAndReturnBuffer() ??
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        "The decoded OriginRegistry body produced no binary fields."));
            }
            if (bytes.Length > 64 * 1024)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded,
                    "The OriginRegistry structure exceeds the byte budget.");
            }
            using var decoder = new BinaryDecoder(bytes.ToArray(), context);
            var origin = new OpcUaRegistryOriginValue(decoder.ReadString("OriginUri"),
                decoder.ReadString("ServerUri"), decoder.ReadExpandedNodeId("RegistryNodeId"));
            if (decoder.Position != bytes.Length)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "OriginRegistry has fields beyond its three declared scalar fields.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return origin;
        }
        catch (ServiceResultException failure)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "OriginRegistry does not encode its three declared scalar fields.",
                failure.StatusCode.ToString(), failure);
        }
    }

    /// <inheritdoc />
    public async ValueTask<OpcUaJsonValue> ReadJsonValueAsync(NodeId variable, CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(() => session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = variable, AttributeId = Attributes.Value }], cancellationToken))
            .ConfigureAwait(false);
        var result = Single(response.Results, static result => result.StatusCode,
            response.DiagnosticInfos, response.ResponseHeader);
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
    public async ValueTask CloseAsync(NodeId file, uint handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (session.SessionId, file, handle);
        if (!readHandles.ContainsKey(key))
        {
            RequireMutationSecurity(cancellationToken);
        }
        _ = await CallFileMethodAsync(file, BrowseNames.Close, [new Variant(handle)],
            !readHandles.ContainsKey(key), cancellationToken).ConfigureAwait(false);
        readHandles.TryRemove(key, out _);
    }

    private void RequireMutationSecurity(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session.Endpoint?.SecurityMode != MessageSecurityMode.SignAndEncrypt)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied,
                "Registry mutations require a SignAndEncrypt SecureChannel.");
        }
    }

    private async ValueTask<IReadOnlyList<ReferenceDescription>> BrowseOnce(NodeId node, BrowseDirection direction,
        NodeId referenceType, CancellationToken cancellationToken)
    {
        var references = new List<ReferenceDescription>();
        var response = await InvokeAsync(() => session.BrowseAsync(new RequestHeader(), new ViewDescription(),
            maxReferencesPerNode, [Describe(node, direction, referenceType)], cancellationToken)).ConfigureAwait(false);
        var page = await CheckedBrowseAsync(response.Results, response.DiagnosticInfos,
            response.ResponseHeader).ConfigureAwait(false);
        var pages = 1;
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
                if (pages >= maxBrowsePages)
                {
                    throw new FederationException(FederationErrorCode.LimitExceeded,
                        "The OPC UA Browse result exceeds the page budget.");
                }
                var next = await InvokeAsync(() => session.BrowseNextAsync(new RequestHeader(), false,
                    [new ByteString(continuation)], cancellationToken)).ConfigureAwait(false);
                page = await CheckedBrowseAsync(next.Results, next.DiagnosticInfos,
                    next.ResponseHeader, continuation).ConfigureAwait(false);
                pages++;
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
        var response = await InvokeAsync(() => session.CallAsync(new RequestHeader(),
            [new CallMethodRequest { ObjectId = target, MethodId = method, InputArguments = input }],
            cancellationToken)).ConfigureAwait(false);
        var result = Single(response.Results, static result => result.StatusCode,
            response.DiagnosticInfos, response.ResponseHeader);
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
        ArrayOf<DiagnosticInfo> diagnostics, ResponseHeader? responseHeader, byte[]? previousContinuation = null)
    {
        var result = await CheckedBrowseAsync(results, diagnostics, responseHeader, previousContinuation)
            .ConfigureAwait(false);

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

    internal static T Single<T>(ArrayOf<T> results, Func<T, StatusCode> getStatus,
        ArrayOf<DiagnosticInfo> diagnostics, ResponseHeader? responseHeader)
    {
        if (responseHeader is not null && !StatusCode.IsGood(responseHeader.ServiceResult))
        {
            var failure = StatusFailure(responseHeader.ServiceResult, diagnostics, responseHeader);
            failure.Data["OpcUa.Results"] = results;
            throw failure;
        }
        if (results.Count != 1)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The server returned an unexpected result count.");
        }
        var result = results[0];
        var status = getStatus(result);
        if (StatusCode.IsGood(status) && result is CallMethodResult method)
        {
            foreach (var argument in method.InputArgumentResults)
            {
                if (!StatusCode.IsGood(argument))
                {
                    status = argument;
                    break;
                }
            }
        }
        if (!StatusCode.IsGood(status))
        {
            var failure = StatusFailure(status, diagnostics, responseHeader);
            if (result is CallMethodResult call)
            {
                failure.Data["OpcUa.MethodStatusCode"] = call.StatusCode;
                failure.Data["OpcUa.InputArgumentResults"] = call.InputArgumentResults;
                failure.Data["OpcUa.InputArgumentDiagnosticInfos"] = call.InputArgumentDiagnosticInfos;
            }
            throw failure;
        }
        return result;
    }

    private static FederationException StatusFailure(StatusCode status,
        ArrayOf<DiagnosticInfo> diagnostics = default, ResponseHeader? responseHeader = null,
        ServiceResultException? innerException = null)
    {
        var code = status.CodeBits switch
        {
            var value when value == StatusCodes.BadNotSupported.CodeBits ||
                value == StatusCodes.BadMethodInvalid.CodeBits => FederationErrorCode.UnsupportedOperation,
            var value when value == StatusCodes.BadNodeIdUnknown.CodeBits ||
                value == StatusCodes.BadNotFound.CodeBits => FederationErrorCode.NotFound,
            var value when value == StatusCodes.BadUserAccessDenied.CodeBits ||
                value == StatusCodes.BadSecurityChecksFailed.CodeBits => FederationErrorCode.PolicyDenied,
            var value when value == StatusCodes.BadInvalidArgument.CodeBits => FederationErrorCode.InvalidPackage,
            var value when value == StatusCodes.BadContinuationPointInvalid.CodeBits =>
                FederationErrorCode.InconsistentSnapshot,
            var value when value == StatusCodes.BadTooManyOperations.CodeBits ||
                value == StatusCodes.BadEncodingLimitsExceeded.CodeBits => FederationErrorCode.LimitExceeded,
            _ => FederationErrorCode.Unavailable
        };
        var failure = new FederationException(code, "The OPC UA operation did not return a Good status.",
            diagnostic: status.ToString(), innerException: innerException ?? new ServiceResultException(status));
        failure.Data["OpcUa.DiagnosticInfos"] = diagnostics;
        if (responseHeader is not null)
        {
            failure.Data["OpcUa.ResponseHeader"] = responseHeader;
        }
        return failure;
    }

    internal static async ValueTask<T> InvokeAsync<T>(Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ServiceResultException failure)
        {
            throw StatusFailure(failure.StatusCode, innerException: failure);
        }
    }

    private async ValueTask<BrowseResult> CheckedBrowseAsync(ArrayOf<BrowseResult> results,
        ArrayOf<DiagnosticInfo> diagnostics, ResponseHeader? responseHeader, byte[]? previousContinuation = null)
    {
        try
        {
            return Single(results, static result => result.StatusCode, diagnostics, responseHeader);
        }
        catch (FederationException failure) when (results.Count == 1)
        {
            var continuation = results[0].ContinuationPoint.ToArray();
            if (continuation is { Length: > 0 } &&
                (previousContinuation is null || !continuation.SequenceEqual(previousContinuation)))
            {
                await ReleaseAsync(continuation, failure).ConfigureAwait(false);
            }
            throw;
        }
    }

    private NodeId Local(ExpandedNodeId id)
    {
        var namespaces = namespaceUris ?? throw new FederationException(FederationErrorCode.InvalidPackage,
            "The Server NamespaceArray must be read before Browse results are interpreted.");
        var node = ExpandedNodeId.ToNodeId(id, namespaces, updateNamespaceTable: false);
        return node is { IsNull: false } && node.NamespaceIndex < namespaces.Count
            ? node
            : throw new FederationException(FederationErrorCode.NotFound,
                "A Browse result refers to a namespace or server that cannot be mapped in this Session.");
    }

    private async ValueTask ReleaseAsync(byte[] continuation, Exception failure)
    {
        try
        {
            var response = await InvokeAsync(() => session.BrowseNextAsync(new RequestHeader(), true,
                [new ByteString(continuation)], CancellationToken.None)).ConfigureAwait(false);
            _ = Single(response.Results, static result => result.StatusCode,
                response.DiagnosticInfos, response.ResponseHeader);
        }
        catch (Exception releaseFailure)
        {
            failure.Data["OpcUa.ContinuationReleaseFailure"] =
                failure.Data["OpcUa.ContinuationReleaseFailure"] is Exception previousFailure
                    ? new AggregateException("Multiple OPC UA continuation releases failed.",
                        previousFailure, releaseFailure)
                    : releaseFailure;
        }
    }
}
