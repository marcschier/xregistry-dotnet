// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Opc.Ua;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaTrustedProviderLeaseTests
{
    private const string ServerUri = "urn:lease-tests:server";
    private const string NamespaceUri = ServerUri;
    private const string Version = "1.0-rc4";
    private const string ResourceXid = "/teams/team-a/schemas/schema-a";
    private static readonly NodeId Root = new("Registry", 1);
    private static readonly NodeId Group = new("team-a", 1);
    private static readonly NodeId Resource = new("schema-a", 1);
    private static readonly NodeId GroupType = new("GroupType", 1);
    private static readonly NodeId ResourceType = new("ResourceType", 1);

    [Test]
    public async Task ConstructorNullConnectionRejectsBeforeTakingOwnership()
    {
        var context = new NativeRegistryContext("opcua", "urn:lease-tests:context");

        var error = await Assert.That(() =>
                new OpcUaTrustedProviderLease(null!, context, CreateTypes()))
            .Throws<ArgumentNullException>();

        await Assert.That(error!.ParamName).IsEqualTo("connection");
    }

    [Test]
    public async Task ResolverDelegateReturnsTheLeaseForTheRequestedIdentity()
    {
        var lease = new TrackingLease();
        var identity = new OpcUaExternalNodeId(ServerUri, NamespaceUri, IdType.String, "schema-a");
        using var cancellation = new CancellationTokenSource();
        OpcUaExternalNodeId? receivedIdentity = null;
        CancellationToken receivedToken = default;
        var resolverCalls = 0;
        OpcUaTrustedProviderResolver resolver = (requested, token) =>
        {
            resolverCalls++;
            receivedIdentity = requested;
            receivedToken = token;
            return new ValueTask<IOpcUaTrustedProviderLease>(lease);
        };

        await using (var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
            identity, resolver, version => version == Version, cancellation.Token))
        {
            await Assert.That(ReferenceEquals(receivedIdentity, identity)).IsTrue();
            await Assert.That(receivedToken).IsEqualTo(cancellation.Token);
            await Assert.That(resolverCalls).IsEqualTo(1);
            await Assert.That(result.Node).IsEqualTo(Resource);
            await Assert.That(result.Xid).IsEqualTo(ResourceXid);
            await Assert.That(result.SpecVersion).IsEqualTo(Version);
            await Assert.That(ReferenceEquals(result.ReadSource.Context, lease.Context)).IsTrue();
            await Assert.That(lease.DisposeCalls).IsEqualTo(0);
        }

        await Assert.That(lease.DisposeCalls).IsEqualTo(1);
        await Assert.That(lease.DisposalCompleted).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResolvedExternalResourceDisposeAsyncReleasesLeaseOnce(bool disposalFails)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new TrackingLease(completion.Task);
        var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
            new OpcUaExternalNodeId(ServerUri, NamespaceUri, IdType.String, "schema-a"),
            (_, _) => new ValueTask<IOpcUaTrustedProviderLease>(lease),
            version => version == Version);
        var failure = new InvalidOperationException("lease disposal failed");

        await Assert.That(lease.DisposeCalls).IsEqualTo(0);
        var pendingDisposal = result.DisposeAsync().AsTask();
        try
        {
            await Assert.That(pendingDisposal.IsCompleted).IsFalse();
            await Assert.That(lease.DisposeCalls).IsEqualTo(1);
            await Assert.That(lease.DisposalCompleted).IsFalse();
            var repeatedDisposal = result.DisposeAsync();
            await Assert.That(repeatedDisposal.IsCompletedSuccessfully).IsTrue();
            await repeatedDisposal;
            await Assert.That(lease.DisposeCalls).IsEqualTo(1);
        }
        finally
        {
            if (disposalFails)
            {
                completion.SetException(failure);
            }
            else
            {
                completion.SetResult();
            }
        }

        if (disposalFails)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pendingDisposal);
            await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        }
        else
        {
            await pendingDisposal;
        }
        await Assert.That(lease.DisposalCompleted).IsEqualTo(!disposalFails);
        await result.DisposeAsync();
        await Assert.That(lease.DisposeCalls).IsEqualTo(1);
    }

    private static OpcUaRegistryTypeMap CreateTypes() => new(
        new Dictionary<string, NodeId> { ["teams"] = GroupType },
        new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
        {
            [("teams", "schemas")] = new(ResourceType, new NodeId("ResourceVersionsType", 1))
        },
        new NodeId("AttributesType", 1));

    private sealed class TrackingLease(Task? disposal = null) : IOpcUaTrustedProviderLease
    {
        public string ServerApplicationUri => ServerUri;
        public OpcUaRegistryReader Reader { get; } = new(new OwnershipSession());
        public NodeId RegistryRoot => Root;
        public string SpecVersion => Version;
        public NativeRegistryContext Context { get; } = new("opcua", "urn:lease-tests:context");
        public OpcUaRegistryTypeMap Types { get; } = CreateTypes();
        public int DisposeCalls { get; private set; }
        public bool DisposalCompleted { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await (disposal ?? Task.CompletedTask);
            DisposalCompleted = true;
        }
    }

    // Just enough in-memory address space to obtain the public owning result.
    // Resolver validation permutations belong to Phase 3, not these ownership tests.
    private sealed class OwnershipSession : IOpcUaRegistrySession
    {
        private readonly Dictionary<NodeId, OpcUaBrowsePage> pages = [];
        private readonly Dictionary<NodeId, OpcUaJsonValue> values = [];
        private readonly Dictionary<NodeId, Queue<byte[]>> files = [];

        public OwnershipSession()
        {
            AddEntity(Root, "RegistryId", "registry-a", "/", "Registry A");
            AddProperty(Root, "SpecVersion", Version, BuiltInType.String);
            AddEntity(Group, "GroupId", "team-a", "/teams/team-a", "Team A");
            AddEntity(Resource, "ResourceId", "schema-a", ResourceXid, "Schema A");
            AddFile("Model",
                """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema"}}}}}""");
            AddFile("Capabilities", """{"flags":["doc"]}""");
            AddReference(Root, new(Group, new QualifiedName("team-a", 1), NodeClass.Object, GroupType));
            AddReference(Group, new(Resource, new QualifiedName("schema-a", 1), NodeClass.Object, ResourceType));
        }

        private void AddEntity(NodeId node, string idName, string id, string xid, string name)
        {
            AddProperty(node, idName, id, BuiltInType.String);
            AddProperty(node, "Xid", xid, BuiltInType.String);
            AddProperty(node, "Name", name, BuiltInType.String);
            AddProperty(node, "Epoch", 7u, BuiltInType.UInt32);
            AddProperty(node, "CreatedAt", "2026-01-01T00:00:00Z", BuiltInType.DateTime);
            AddProperty(node, "ModifiedAt", "2026-02-01T00:00:00Z", BuiltInType.DateTime);
        }

        private void AddProperty(NodeId parent, string name, object value, BuiltInType type)
        {
            var variable = new NodeId(parent.IdentifierAsString + ":" + name, 1);
            values.Add(variable, new(JsonSerializer.SerializeToElement(value), type));
            AddReference(parent, new(variable, new QualifiedName(name, 1), NodeClass.Variable));
        }

        private void AddFile(string name, string json)
        {
            var file = new NodeId(name, 1);
            files.Add(file, new Queue<byte[]>([Encoding.UTF8.GetBytes(json), []]));
            AddReference(Root, new(file, new QualifiedName(name, 1), NodeClass.Object, ObjectTypeIds.FileType));
        }

        private void AddReference(NodeId parent, OpcUaReference reference)
        {
            pages.TryGetValue(parent, out var page);
            pages[parent] = new([.. page?.References ?? [], reference]);
        }

        public ValueTask<IReadOnlyList<string>> ReadServerArrayAsync(CancellationToken cancellationToken) =>
            new([ServerUri]);
        public ValueTask<NamespaceTable> ReadNamespaceArrayAsync(CancellationToken cancellationToken) =>
            new(new NamespaceTable(["http://opcfoundation.org/UA/", NamespaceUri]));
        public ValueTask<bool> IsRegistryNodeAsync(NodeId node, CancellationToken cancellationToken) =>
            new(node == Root);
        public ValueTask<bool> IsTypeOrSubtypeAsync(NodeId actualType, NodeId declaredType,
            CancellationToken cancellationToken) => new(actualType == declaredType);
        public ValueTask<OpcUaBrowsePage> BrowseAsync(NodeId node, CancellationToken cancellationToken) =>
            new(pages[node]);
        public ValueTask<string> ReadStringAsync(NodeId variable, CancellationToken cancellationToken) =>
            new(values[variable].Value.GetString()!);
        public ValueTask<OpcUaJsonValue> ReadJsonValueAsync(NodeId variable, CancellationToken cancellationToken) =>
            new(values[variable]);
        public ValueTask<uint> OpenAsync(NodeId file, byte mode, CancellationToken cancellationToken) => new(7u);
        public ValueTask<byte[]> ReadAsync(NodeId file, uint handle, int length, CancellationToken cancellationToken) =>
            new(files[file].Dequeue());
        public ValueTask CloseAsync(NodeId file, uint handle, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<OpcUaBrowsePage> BrowseNextAsync(byte[] continuationPoint, bool release,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetPositionAsync(NodeId file, uint handle, ulong position,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask WriteAsync(NodeId file, uint handle, byte[] data,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask WriteValueAsync(NodeId variable, Variant value,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<Variant>> CallMethodAsync(NodeId target, string methodBrowseName,
            IReadOnlyList<Variant> inputArguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<ExpandedNodeId> ReadExpandedNodeIdAsync(NodeId variable,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OpcUaExternalNodeId> MapExternalNodeIdAsync(ExpandedNodeId nodeId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
