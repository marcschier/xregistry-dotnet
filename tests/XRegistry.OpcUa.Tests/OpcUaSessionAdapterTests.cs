// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaSessionAdapterTests
{
    private static readonly string[] s_namespaceUris = ["http://opcfoundation.org/UA/"];
    private static readonly string[] s_serverUris =
        ["urn:example:local-server", "urn:remote:server-one", "urn:Remote:Server%2fTwo"];
    private static readonly string[] s_externalNamespaceUris =
        ["http://opcfoundation.org/UA/", "urn:example:namespace-one", "urn:Exact:Namespace%2fTwo"];

    [Test]
    public async Task ReadJsonValueDecodesExtensionObjectAndResolvesItsDataType()
    {
        var value = new BuildInfo
        {
            ProductUri = "urn:example:product",
            ManufacturerName = "Example",
            ProductName = "Registry",
            SoftwareVersion = "1.2.3",
            BuildNumber = "42"
        };
        var session = DispatchProxy.Create<ISessionClient, SessionProxy>();
        var proxy = (SessionProxy)(object)session;
        proxy.Extension = new ExtensionObject(value, true);
        proxy.DataTypeBrowseName = "ProductInformation";
        var adapter = new OpcUaSessionAdapter(session, new NodeId("RegistryType", 1));
        typeof(OpcUaSessionAdapter).GetField("namespaceUris", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(adapter, new NamespaceTable(s_namespaceUris));
        var result = await adapter.ReadJsonValueAsync(new NodeId("metadata", 1), CancellationToken.None);

        await Assert.That(result.BuiltInType).IsEqualTo(BuiltInType.ExtensionObject);
        await Assert.That(result.DataTypeName).IsEqualTo("ProductInformation");
        await Assert.That(result.Value.GetProperty("ProductName").GetString()).IsEqualTo("Registry");
        await Assert.That(result.Value.GetProperty("BuildNumber").GetString()).IsEqualTo("42");
    }

    [Test]
    public async Task ReadServerArrayReturnsTheExactSessionServerUris()
    {
        var (adapter, _) = CreateAdapter();

        var serverArray = await adapter.ReadServerArrayAsync(CancellationToken.None);

        await Assert.That(serverArray).IsEquivalentTo(s_serverUris);
        await Assert.That(serverArray[0]).IsEqualTo("urn:example:local-server");
    }

    [Test]
    public async Task MapExternalNodeIdUsesSourceServerAndNamespaceUrisAndPreservesIdentifier()
    {
        var (adapter, _) = CreateAdapter();
        var expandedNodeId = new ExpandedNodeId(new NodeId(42u, 2), null, 2u);

        var result = await adapter.MapExternalNodeIdAsync(expandedNodeId, CancellationToken.None);

        await Assert.That(result.ServerApplicationUri).IsEqualTo("urn:Remote:Server%2fTwo");
        await Assert.That(result.NamespaceUri).IsEqualTo("urn:Exact:Namespace%2fTwo");
        await Assert.That(result.IdentifierType).IsEqualTo(IdType.Numeric);
        await Assert.That(result.Identifier).IsEqualTo(42u);
        var targetNamespaceUris = new NamespaceTable(
            ["http://opcfoundation.org/UA/", "urn:target:other", "urn:Exact:Namespace%2fTwo"]);
        var targetNodeId = result.MapToLocalNodeId("urn:Remote:Server%2fTwo", targetNamespaceUris);
        await Assert.That(targetNodeId.NamespaceIndex).IsEqualTo((ushort)2);
        await Assert.That(targetNodeId.TryGetValue(out uint targetIdentifier) && targetIdentifier == 42u).IsTrue();
    }

    [Test]
    public async Task MapExternalNodeIdPreservesExpandedNodeIdNamespaceUriExactly()
    {
        var (adapter, _) = CreateAdapter();
        const string namespaceUri = "urn:Exact:Namespace%2fAlreadyPresent";
        var expandedNodeId = new ExpandedNodeId("resource", namespaceUri, 1);

        var result = await adapter.MapExternalNodeIdAsync(expandedNodeId, CancellationToken.None);

        await Assert.That(result.ServerApplicationUri).IsEqualTo("urn:remote:server-one");
        await Assert.That(result.NamespaceUri).IsEqualTo(namespaceUri);
        await Assert.That(result.IdentifierType).IsEqualTo(IdType.String);
        await Assert.That(result.Identifier).IsEqualTo("resource");
    }

    [Test]
    public async Task MapExternalNodeIdPreservesGuidAndOpaqueIdentifiers()
    {
        var (adapter, _) = CreateAdapter();
        var guid = Guid.Parse("47c31b9c-52e6-41f2-b064-ea8c44e086f0");
        byte[] opaque = [0x00, 0x7f, 0x80, 0xff];
        var guidResult = await adapter.MapExternalNodeIdAsync(
            new ExpandedNodeId(guid, 2, null, 2u), CancellationToken.None);
        var opaqueResult = await adapter.MapExternalNodeIdAsync(
            new ExpandedNodeId(ByteString.From(opaque), 2, null, 2u), CancellationToken.None);
        var targetNamespaceUris = new NamespaceTable(
            ["http://opcfoundation.org/UA/", "urn:target:other", "urn:Exact:Namespace%2fTwo"]);

        await Assert.That(guidResult.IdentifierType).IsEqualTo(IdType.Guid);
        await Assert.That(guidResult.Identifier).IsEqualTo(guid);
        await Assert.That(opaqueResult.IdentifierType).IsEqualTo(IdType.Opaque);
        await Assert.That((byte[])opaqueResult.Identifier).IsEquivalentTo(opaque);
        var targetGuid = guidResult.MapToLocalNodeId("urn:Remote:Server%2fTwo", targetNamespaceUris);
        var targetOpaque = opaqueResult.MapToLocalNodeId("urn:Remote:Server%2fTwo", targetNamespaceUris);
        await Assert.That(targetGuid.NamespaceIndex).IsEqualTo((ushort)2);
        await Assert.That(targetGuid.TryGetValue(out Guid mappedGuid) && mappedGuid == guid).IsTrue();
        await Assert.That(targetOpaque.NamespaceIndex).IsEqualTo((ushort)2);
        await Assert.That(targetOpaque.TryGetValue(out ByteString mappedOpaque)).IsTrue();
        await Assert.That(mappedOpaque.ToArray()).IsEquivalentTo(opaque);
    }

    [Test]
    public async Task ExternalOpaqueIdentifierCopiesInputAndReturnedBytes()
    {
        byte[] source = [0x00, 0x7f, 0x80, 0xff];
        var identity = new OpcUaExternalNodeId("urn:remote:server", "urn:namespace", IdType.Opaque, source);
        source[0] = 0xff;
        var firstRead = (byte[])identity.Identifier;
        firstRead[1] = 0xff;
        var namespaceUris = new NamespaceTable(["http://opcfoundation.org/UA/", "urn:namespace"]);
        var target = identity.MapToLocalNodeId("urn:remote:server", namespaceUris);

        await Assert.That((byte[])identity.Identifier).IsEquivalentTo(new byte[] { 0x00, 0x7f, 0x80, 0xff });
        await Assert.That(target.TryGetValue(out ByteString targetIdentifier)).IsTrue();
        await Assert.That(targetIdentifier.ToArray()).IsEquivalentTo(new byte[] { 0x00, 0x7f, 0x80, 0xff });
    }

    [Test]
    public async Task MapExternalNodeIdRejectsMissingTargetNamespaceInsteadOfUsingNamespaceZero()
    {
        var identity = new OpcUaExternalNodeId("urn:remote:server", "urn:missing:namespace",
            IdType.Numeric, 42u);
        var namespaceUris = new NamespaceTable(["http://opcfoundation.org/UA/"]);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
        {
            _ = identity.MapToLocalNodeId("urn:remote:server", namespaceUris);
            await Task.CompletedTask;
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task MapExternalNodeIdRejectsDifferentTargetApplicationUri()
    {
        var identity = new OpcUaExternalNodeId("urn:Remote:Server%2fTwo", "urn:namespace",
            IdType.Numeric, 42u);
        var namespaceUris = new NamespaceTable(["http://opcfoundation.org/UA/", "urn:namespace"]);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
        {
            _ = identity.MapToLocalNodeId("urn:remote:server%2fTwo", namespaceUris);
            await Task.CompletedTask;
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
    }

    [Test]
    public async Task MapExternalNodeIdRejectsOutOfRangeServerIndex()
    {
        var (adapter, _) = CreateAdapter();
        var expandedNodeId = new ExpandedNodeId(new NodeId(42u, 1), null, 3u);

        await Assert.ThrowsAsync<FederationException>(
            async () => await adapter.MapExternalNodeIdAsync(expandedNodeId, CancellationToken.None));
    }

    [Test]
    public async Task MapExternalNodeIdRejectsOutOfRangeNamespaceIndex()
    {
        var (adapter, _) = CreateAdapter();
        var expandedNodeId = new ExpandedNodeId(new NodeId(42u, 3), null, 1u);

        await Assert.ThrowsAsync<FederationException>(
            async () => await adapter.MapExternalNodeIdAsync(expandedNodeId, CancellationToken.None));
    }

    private static (OpcUaSessionAdapter Adapter, SessionProxy Proxy) CreateAdapter()
    {
        var session = DispatchProxy.Create<ISessionClient, SessionProxy>();
        var proxy = (SessionProxy)(object)session;
        proxy.ServerArray = s_serverUris;
        proxy.NamespaceArray = s_externalNamespaceUris;
        return (new OpcUaSessionAdapter(session, new NodeId("RegistryType", 1)), proxy);
    }

    public class SessionProxy : DispatchProxy
    {
        public ExtensionObject Extension { get; set; }
        public string DataTypeBrowseName { get; set; } = "BuildInfo";
        public string[] ServerArray { get; set; } = [];
        public string[] NamespaceArray { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "ReadAsync":
#pragma warning disable CA2012
                    return Read((ArrayOf<ReadValueId>)args![3]!);
#pragma warning restore CA2012
                case "BrowseAsync":
#pragma warning disable CA2012
                    return Browse((ArrayOf<BrowseDescription>)args![3]!);
#pragma warning restore CA2012
                default:
                    throw new NotSupportedException($"Unexpected OPC UA service call: {targetMethod?.Name}.");
            }
        }

        private ValueTask<ReadResponse> Read(ArrayOf<ReadValueId> nodes)
        {
            var node = nodes[0].NodeId;
            var value = node.TryGetValue(out uint identifier) && node.NamespaceIndex == 0
                ? identifier switch
                {
                    2254u => Variant.From(ServerArray),
                    2255u => Variant.From(NamespaceArray),
                    _ => Variant.From(Extension)
                }
                : Variant.From(Extension);
            return new(new ReadResponse { Results = [new DataValue(value)] });
        }

        private ValueTask<BrowseResponse> Browse(ArrayOf<BrowseDescription> nodes)
        {
            var dataType = new ReferenceDescription
            {
                ReferenceTypeId = ReferenceTypeIds.HasEncoding,
                IsForward = false,
                NodeId = new ExpandedNodeId(new NodeId(338u, 0)),
                BrowseName = new QualifiedName(DataTypeBrowseName, 0),
                NodeClass = NodeClass.DataType
            };
            return new(new BrowseResponse
            {
                Results = [new BrowseResult { References = [dataType] }]
            });
        }
    }
}
