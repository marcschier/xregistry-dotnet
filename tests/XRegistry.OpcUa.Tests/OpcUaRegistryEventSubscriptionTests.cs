// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaRegistryEventSubscriptionTests
{
    private static readonly NodeId s_notifier = new(100u, 2);
    private static readonly NodeId s_groupNotifier = new(101u, 2);
    private static readonly NodeId s_registryType = new(200u, 2);
    private static readonly NodeId s_baseType = new(201u, 2);
    private static readonly NodeId s_groupType = new(202u, 2);
    private static readonly NodeId s_otherBaseType = new(203u, 2);
    private static readonly NodeId s_registryCreated = new(300u, 2);
    private static readonly NodeId s_groupDeprecated = new(301u, 2);
    private static readonly NodeId s_unknown = new(399u, 2);
    private static readonly byte[] s_continuationPoint = [1];
    private static readonly NamespaceTable s_namespaceUris =
        new(["http://opcfoundation.org/UA/", "urn:example:other", "urn:example:xregistry"]);

    [Test]
    public async Task DiscoveryReadsGeneratedTypesFromNotifierTypeAndItsSupertypes()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.ParentTypes[s_registryType] = [s_baseType];
        proxy.GeneratedEvents[s_registryType] =
        [
            new(s_registryCreated, "RegistryCreatedEventType"),
            new(s_unknown, "VendorSpecificEventType"),
        ];
        proxy.GeneratedEvents[s_baseType] =
        [
            new(s_groupDeprecated, "GroupDeprecatedEventType"),
        ];

        var eventTypes = await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
            session, s_namespaceUris, s_notifier);

        await Assert.That(eventTypes.Count).IsEqualTo(2);
        await Assert.That(eventTypes[s_registryCreated]).IsEqualTo("io.xregistry.registry.created");
        await Assert.That(eventTypes[s_groupDeprecated]).IsEqualTo("io.xregistry.group.deprecation");
        await Assert.That(proxy.BrowseRequests.Any(request =>
            request.NodeId == s_registryType && request.ReferenceTypeId == ReferenceTypeIds.GeneratesEvent)).IsTrue();
        await Assert.That(proxy.BrowseRequests.Any(request =>
            request.NodeId == s_baseType && request.ReferenceTypeId == ReferenceTypeIds.GeneratesEvent)).IsTrue();
    }

    [Test]
    public async Task DiscoveryReadsGeneratedTypesFromDescendantNotifiers()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.NotifierChildren[s_notifier] = [s_groupNotifier];
        proxy.NotifierTypes[s_groupNotifier] = s_groupType;
        proxy.GeneratedEvents[s_groupType] =
        [
            new(s_groupDeprecated, "GroupDeprecatedEventType"),
        ];

        var eventTypes = await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
            session, s_namespaceUris, s_notifier);

        await Assert.That(eventTypes[s_groupDeprecated]).IsEqualTo("io.xregistry.group.deprecation");
        await Assert.That(proxy.BrowseRequests.Any(request =>
            request.NodeId == s_notifier && request.ReferenceTypeId == ReferenceTypeIds.HasNotifier)).IsTrue();
    }

    [Test]
    public async Task EventFilterSelectsCanonicalFieldsInDecoderOrder()
    {
        var method = typeof(OpcUaRegistryEventSubscription).GetMethod(
            "CreateEventFilter", BindingFlags.NonPublic | BindingFlags.Static)!;
        var filter = (EventFilter)method.Invoke(null, [(ushort)2])!;
        var clauses = filter.SelectClauses.ToArray() ?? [];
        var selectedPaths = string.Join(",", clauses.Select(clause =>
            $"{clause.BrowsePath[0].NamespaceIndex}:{clause.BrowsePath[0].Name}"));

        await Assert.That(clauses.Length).IsEqualTo(11);
        await Assert.That(clauses.All(clause => clause.TypeDefinitionId == ObjectTypeIds.BaseEventType)).IsTrue();
        await Assert.That(selectedPaths).IsEqualTo(
            "0:EventId,0:EventType,0:SourceNode,0:SourceName,0:Time," +
            "2:SourceUrl,2:Subject,2:Epoch,2:MetaEpoch,2:Changed,2:CorrelationId");
    }

    [Test]
    public async Task DiscoveryFollowsBrowseContinuationPoints()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.ReturnContinuationForGeneratedEvents = true;
        proxy.ContinuedEvents =
        [
            new(s_registryCreated, "RegistryCreatedEventType"),
        ];

        var eventTypes = await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
            session, s_namespaceUris, s_notifier);

        await Assert.That(eventTypes[s_registryCreated]).IsEqualTo("io.xregistry.registry.created");
        await Assert.That(proxy.BrowseNextReleaseFlags.Count).IsEqualTo(1);
        await Assert.That(proxy.BrowseNextReleaseFlags[0]).IsFalse();
    }

    [Test]
    public async Task DiscoveryReleasesContinuationWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.ReturnContinuationForGeneratedEvents = true;
        proxy.CancellationAfterEventBrowse = cancellation;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
                session, s_namespaceUris, s_notifier, cancellation.Token));

        await Assert.That(proxy.BrowseNextReleaseFlags.Count).IsEqualTo(1);
        await Assert.That(proxy.BrowseNextReleaseFlags[0]).IsTrue();
    }

    [Test]
    public async Task DiscoveryRejectsObjectsWithoutSubscribeToEvents()
    {
        var session = CreateSession();
        ((SessionProxy)(object)session).EventNotifier = 0;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task DiscoveryRejectsNotifiersWithoutRecognizedXRegistryEventTypes()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.GeneratedEvents[s_registryType] = [new(s_unknown, "VendorSpecificEventType")];

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task DiscoveryRejectsEventDeclarationsOutsideTheConnectedServer()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.GeneratedEvents[s_registryType] =
        [
            new(new ExpandedNodeId(s_registryCreated, null, 1u), "RegistryCreatedEventType"),
        ];

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task DiscoveryRejectsObjectTypesWithCompetingSupertypes()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.ParentTypes[s_registryType] = [s_baseType, s_otherBaseType];

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task DiscoveryRejectsObjectTypeCycles()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.ParentTypes[s_registryType] = [s_baseType];
        proxy.ParentTypes[s_baseType] = [s_registryType];

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task DiscoveryRejectsCyclesInNotifierHierarchy()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.NotifierChildren[s_notifier] = [s_groupNotifier];
        proxy.NotifierChildren[s_groupNotifier] = [s_notifier];
        proxy.NotifierTypes[s_groupNotifier] = s_groupType;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    private static ISessionClient CreateSession() => DispatchProxy.Create<ISessionClient, SessionProxy>();

    public class SessionProxy : DispatchProxy
    {
        public byte EventNotifier { get; set; } = 1;
        public NodeId TypeDefinition { get; set; } = NodeId.Null;
        public Dictionary<NodeId, NodeId[]> ParentTypes { get; } = [];
        public Dictionary<NodeId, (ExpandedNodeId NodeId, string BrowseName)[]> GeneratedEvents { get; } = [];
        public Dictionary<NodeId, NodeId> NotifierTypes { get; } = [];
        public Dictionary<NodeId, NodeId[]> NotifierChildren { get; } = [];
        public List<BrowseDescription> BrowseRequests { get; } = [];
        public List<bool> BrowseNextReleaseFlags { get; } = [];
        public bool ReturnContinuationForGeneratedEvents { get; set; }
        public (ExpandedNodeId NodeId, string BrowseName)[] ContinuedEvents { get; set; } = [];
        public CancellationTokenSource? CancellationAfterEventBrowse { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
#pragma warning disable CA2012
            return targetMethod?.Name switch
            {
                "ReadAsync" => Read((ArrayOf<ReadValueId>)args![3]!),
                "BrowseAsync" => Browse((ArrayOf<BrowseDescription>)args![3]!),
                "BrowseNextAsync" => BrowseNext(
                    (bool)args![1]!, (ArrayOf<ByteString>)args[2]!),
                _ => throw new NotSupportedException($"Unexpected OPC UA service call: {targetMethod?.Name}."),
            };
#pragma warning restore CA2012
        }

        private ValueTask<ReadResponse> Read(ArrayOf<ReadValueId> nodes)
        {
            var value = nodes[0].AttributeId == Attributes.EventNotifier
                ? new Variant(EventNotifier)
                : default;
            return new(new ReadResponse { Results = [new DataValue(value)] });
        }

        private ValueTask<BrowseResponse> Browse(ArrayOf<BrowseDescription> nodes)
        {
            var request = nodes[0];
            BrowseRequests.Add(request);
            ReferenceDescription[] references;
            if (request.ReferenceTypeId == ReferenceTypeIds.HasTypeDefinition &&
                (request.NodeId == s_notifier || NotifierTypes.ContainsKey(request.NodeId)))
            {
                var type = request.NodeId == s_notifier ? TypeDefinition : NotifierTypes[request.NodeId];
                references = [Reference(new ExpandedNodeId(type), "RegistryType", NodeClass.ObjectType)];
            }
            else if (request.ReferenceTypeId == ReferenceTypeIds.HasNotifier &&
                NotifierChildren.TryGetValue(request.NodeId, out var children))
            {
                references = children.Select(node =>
                    Reference(new ExpandedNodeId(node), "Notifier", NodeClass.Object)).ToArray();
            }
            else if (request.ReferenceTypeId == ReferenceTypeIds.GeneratesEvent)
            {
                if (ReturnContinuationForGeneratedEvents)
                {
                    ReturnContinuationForGeneratedEvents = false;
                    CancellationAfterEventBrowse?.Cancel();
                    return new(new BrowseResponse
                    {
                        Results = [new BrowseResult
                        {
                            References = new ArrayOf<ReferenceDescription>(),
                            ContinuationPoint = new ByteString(s_continuationPoint),
                        }]
                    });
                }
                references = GeneratedEvents.TryGetValue(request.NodeId, out var events)
                    ? events.Select(item => Reference(item.NodeId, item.BrowseName, NodeClass.ObjectType)).ToArray()
                    : [];
            }
            else if (request.ReferenceTypeId == ReferenceTypeIds.HasSubtype &&
                request.BrowseDirection == BrowseDirection.Inverse &&
                ParentTypes.TryGetValue(request.NodeId, out var parentTypes))
            {
                references = parentTypes.Select(parentType =>
                    Reference(new ExpandedNodeId(parentType), "BaseType", NodeClass.ObjectType)).ToArray();
            }
            else
            {
                references = [];
            }

            return new(new BrowseResponse
            {
                Results = [new BrowseResult { References = new ArrayOf<ReferenceDescription>(references) }]
            });
        }

        private ValueTask<BrowseNextResponse> BrowseNext(bool release, ArrayOf<ByteString> continuationPoints)
        {
            BrowseNextReleaseFlags.Add(release);
            if (continuationPoints.Count != 1 ||
                !continuationPoints[0].ToArray().SequenceEqual(s_continuationPoint))
            {
                throw new InvalidOperationException("The test received an unexpected continuation point.");
            }
            var references = release
                ? []
                : ContinuedEvents.Select(item => Reference(item.NodeId, item.BrowseName, NodeClass.ObjectType)).ToArray();
            return new(new BrowseNextResponse
            {
                Results = [new BrowseResult { References = new ArrayOf<ReferenceDescription>(references) }]
            });
        }

        private static ReferenceDescription Reference(
            ExpandedNodeId node, string browseName, NodeClass nodeClass) => new()
        {
            NodeId = node,
            BrowseName = new QualifiedName(browseName, node.NamespaceIndex),
            NodeClass = nodeClass,
        };
    }
}
