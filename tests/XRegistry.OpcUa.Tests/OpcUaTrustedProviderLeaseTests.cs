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
    [Arguments(false)]
    [Arguments(true)]
    public async Task PinnedResolutionRejectsChangedXidAndDisposesProvider(bool changed)
    {
        var lease = new TrackingLease();
        var origin = OpcUaRegistryOrigin.Stable("urn:origin");
        var original = new OpcUaFederationSourcePin(origin,
            changed ? "/teams/team-b/schemas/schema-a" : ResourceXid,
            OpcUaExternalTargetRole.LogicalResource, new(ServerUri, NamespaceUri, IdType.String, "schema-a"));
        var pin = OpcUaFederationSourcePin.Parse(original.Serialize());
        var calls = 0;
        ValueTask<OpcUaResolvedExternalResource> Resolve() => OpcUaRegistryReadSource.ResolvePinnedTargetAsync(
            pin, origin, (_, _, _) =>
            {
                calls++;
                return ValueTask.FromResult<IOpcUaTrustedProviderLease>(lease);
            }, version => version == Version);

        if (changed)
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () => await Resolve());
            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        }
        else
        {
            await using var result = await Resolve();
            await Assert.That(result.SourcePin).IsNotNull();
            pin.ValidateReconnect(result.SourcePin!);
            await Assert.That(result.Xid).IsEqualTo(ResourceXid);
        }
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(lease.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task PinnedResolutionRequiresCurrentIndependentOriginBeforeProviderSelection()
    {
        var pin = new OpcUaFederationSourcePin(OpcUaRegistryOrigin.Stable("urn:old-origin"), ResourceXid,
            OpcUaExternalTargetRole.LogicalResource, new(ServerUri, NamespaceUri, IdType.String, "schema-a"));
        var calls = 0;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryReadSource.ResolvePinnedTargetAsync(pin, OpcUaRegistryOrigin.Stable("urn:new-origin"),
                (_, _, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("The stored pin established its own trust.");
                }, _ => true));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OriginAdmissionPassesIndependentBindingToProviderAndRetainsOrigin(bool stable)
    {
        var lease = new TrackingLease();
        var identity = new OpcUaExternalNodeId(ServerUri, NamespaceUri, IdType.String, "schema-a");
        var captured = stable ? OpcUaRegistryOrigin.Stable("urn:Origin:Exact%2f") :
            OpcUaRegistryOrigin.Application(new(ServerUri, NamespaceUri, IdType.String, "Registry"));
        var trusted = stable ? OpcUaRegistryOrigin.Stable("urn:Origin:Exact%2f") :
            OpcUaRegistryOrigin.Application(new(ServerUri, NamespaceUri, IdType.String, "Registry"));
        OpcUaRegistryOrigin? received = null;
        var calls = 0;

        await using (var result = await OpcUaRegistryReadSource.ResolveOriginTargetAsync(
            captured, trusted, identity, (origin, target, _) =>
            {
                calls++;
                received = origin;
                if (!ReferenceEquals(target, identity))
                {
                    throw new InvalidOperationException("The selected target changed.");
                }
                return ValueTask.FromResult<IOpcUaTrustedProviderLease>(lease);
            }, version => version == Version, OpcUaExternalTargetRole.LogicalResource))
        {
            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(ReferenceEquals(received, trusted)).IsTrue();
            await Assert.That(ReferenceEquals(result.Origin, trusted)).IsTrue();
            await Assert.That(result.Xid).IsEqualTo(ResourceXid);
            await Assert.That(result.Role).IsEqualTo(OpcUaExternalTargetRole.LogicalResource);
        }
        await Assert.That(lease.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments("uri-case")]
    [Arguments("uri-escape")]
    [Arguments("uri-space")]
    [Arguments("form")]
    [Arguments("server")]
    [Arguments("namespace")]
    [Arguments("root")]
    [Arguments("kind")]
    public async Task OriginAdmissionRejectsDifferentAuthoritativeIdentityBeforeProviderSelection(string difference)
    {
        var native = difference is "server" or "namespace" or "root" or "kind";
        var captured = native
            ? OpcUaRegistryOrigin.Application(new(ServerUri, NamespaceUri, IdType.String, "Registry"))
            : OpcUaRegistryOrigin.Stable("urn:Origin:Exact%2f");
        var trusted = difference switch
        {
            "uri-case" => OpcUaRegistryOrigin.Stable("urn:origin:Exact%2f"),
            "uri-escape" => OpcUaRegistryOrigin.Stable("urn:Origin:Exact%2F"),
            "uri-space" => OpcUaRegistryOrigin.Stable("urn:Origin:Exact%2f "),
            "form" => OpcUaRegistryOrigin.Application(new(ServerUri, NamespaceUri, IdType.String, "Registry")),
            _ => OpcUaRegistryOrigin.Application(new(
                difference == "server" ? "urn:other" : ServerUri,
                difference == "namespace" ? "urn:other" : NamespaceUri,
                difference == "kind" ? IdType.Numeric : IdType.String,
                difference == "kind" ? 42u : difference == "root" ? "OtherRegistry" : "Registry"))
        };
        var calls = 0;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryReadSource.ResolveOriginTargetAsync(captured, trusted,
                new(ServerUri, NamespaceUri, IdType.String, "schema-a"), (_, _, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("An unapproved origin selected a provider.");
                }, _ => true, OpcUaExternalTargetRole.LogicalResource));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(captured.Equals(trusted)).IsFalse();
    }

    [Test]
    public async Task OriginAdmissionRejectsDifferentTargetApplicationBeforeProviderSelection()
    {
        var origin = OpcUaRegistryOrigin.Application(new(ServerUri, NamespaceUri, IdType.String, "Registry"));
        var calls = 0;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryReadSource.ResolveOriginTargetAsync(origin, origin,
                new("urn:other", NamespaceUri, IdType.String, "schema-a"), (_, _, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("A different target application selected a provider.");
                }, _ => true, OpcUaExternalTargetRole.LogicalResource));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OriginAdmissionRejectsWrongSelectedRegistryBeforeOpeningDocumentsAndDisposesLease(
        bool disposalFails)
    {
        var cleanupFailure = new IOException("Synthetic origin provider disposal failure.");
        var lease = new TrackingLease(disposalFails ? Task.FromException(cleanupFailure) : null);
        var origin = OpcUaRegistryOrigin.Application(new(ServerUri, NamespaceUri, IdType.String, "OtherRegistry"));

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryReadSource.ResolveOriginTargetAsync(origin, origin,
                new(ServerUri, NamespaceUri, IdType.String, "schema-a"),
                (_, _, _) => ValueTask.FromResult<IOpcUaTrustedProviderLease>(lease),
                _ => true, OpcUaExternalTargetRole.LogicalResource));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(lease.Session.OpenCount).IsEqualTo(0);
        await Assert.That(lease.DisposeCalls).IsEqualTo(1);
        await Assert.That(error.Data.Contains("OpcUa.TrustedProviderDisposeFailure")).IsEqualTo(disposalFails);
        if (disposalFails)
        {
            await Assert.That(ReferenceEquals(error.Data["OpcUa.TrustedProviderDisposeFailure"], cleanupFailure))
                .IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OriginAdmissionCancellationAndInvalidRoleDoNotAcquireProvider(bool cancelled)
    {
        var origin = OpcUaRegistryOrigin.Stable("urn:origin");
        using var cancellation = new CancellationTokenSource();
        if (cancelled)
        {
            cancellation.Cancel();
        }
        var calls = 0;
        ValueTask<OpcUaResolvedExternalResource> Resolve() => OpcUaRegistryReadSource.ResolveOriginTargetAsync(
            origin, origin, new(ServerUri, NamespaceUri, IdType.String, "schema-a"), (_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("The rejected operation acquired a provider.");
            }, _ => true, cancelled ? OpcUaExternalTargetRole.LogicalResource : (OpcUaExternalTargetRole)99,
            cancellation.Token);

        if (cancelled)
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await Resolve());
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Resolve());
        }
        await Assert.That(calls).IsEqualTo(0);
    }

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
            identity, resolver, version => version == Version,
            OpcUaExternalTargetRole.LogicalResource, cancellation.Token))
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
    public async Task CancelledResolutionDoesNotAcquireProvider()
    {
        var lease = new TrackingLease();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        OpcUaTrustedProviderResolver resolver = (_, _) =>
        {
            calls++;
            return ValueTask.FromResult<IOpcUaTrustedProviderLease>(lease);
        };

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await using var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
                new OpcUaExternalNodeId(ServerUri, NamespaceUri, IdType.String, "schema-a"),
                resolver, _ => true, OpcUaExternalTargetRole.LogicalResource, cancellation.Token);
        });

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(lease.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationDuringAcquisitionDisposesTransferredLeaseAndRetainsCleanupError(
        bool disposalFails)
    {
        var failure = new IOException("Synthetic provider disposal failure.");
        var lease = new TrackingLease(disposalFails ? Task.FromException(failure) : null);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        OpcUaTrustedProviderResolver resolver = (_, _) =>
        {
            calls++;
            cancellation.Cancel();
            return ValueTask.FromResult<IOpcUaTrustedProviderLease>(lease);
        };

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await using var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
                new OpcUaExternalNodeId(ServerUri, NamespaceUri, IdType.String, "schema-a"),
                resolver, _ => true, OpcUaExternalTargetRole.LogicalResource, cancellation.Token);
        });

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(lease.DisposeCalls).IsEqualTo(1);
        await Assert.That(lease.DisposalCompleted).IsEqualTo(!disposalFails);
        await Assert.That(error.Data.Contains("OpcUa.TrustedProviderDisposeFailure")).IsEqualTo(disposalFails);
        if (disposalFails)
        {
            await Assert.That(ReferenceEquals(error.Data["OpcUa.TrustedProviderDisposeFailure"], failure))
                .IsTrue();
        }
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
            version => version == Version, OpcUaExternalTargetRole.LogicalResource);
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

    private sealed class TrackingLease : IOpcUaTrustedProviderLease
    {
        private readonly Task? disposal;
        public TrackingLease(Task? disposal = null)
        {
            this.disposal = disposal;
            Reader = new(Session);
        }
        public OwnershipSession Session { get; } = new();
        public string ServerApplicationUri => ServerUri;
        public OpcUaRegistryReader Reader { get; }
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
        public int OpenCount { get; private set; }
        public ValueTask<uint> OpenAsync(NodeId file, byte mode, CancellationToken cancellationToken)
        {
            OpenCount++;
            return new(7u);
        }
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
        public ValueTask<OpcUaRegistryOriginValue> ReadRegistryOriginAsync(NodeId variable, NodeId expectedDataType,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ExpandedNodeId> ReadExpandedNodeIdAsync(NodeId variable,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OpcUaExternalNodeId> MapExternalNodeIdAsync(ExpandedNodeId nodeId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
