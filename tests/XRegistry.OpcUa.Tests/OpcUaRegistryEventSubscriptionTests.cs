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
    private static readonly NodeId s_resourceDeprecated = new(302u, 2);
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
            new(s_resourceDeprecated, "ResourceDeprecatedEventType"),
        ];

        var eventTypes = await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
            session, s_namespaceUris, s_notifier);

        await Assert.That(eventTypes.Count).IsEqualTo(3);
        await Assert.That(eventTypes[s_registryCreated]).IsEqualTo("io.xregistry.registry.created");
        await Assert.That(eventTypes[s_groupDeprecated]).IsEqualTo("io.xregistry.group.deprecated");
        await Assert.That(eventTypes[s_resourceDeprecated]).IsEqualTo("io.xregistry.resource.deprecated");
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

        await Assert.That(eventTypes[s_groupDeprecated]).IsEqualTo("io.xregistry.group.deprecated");
        await Assert.That(proxy.BrowseRequests.Any(request =>
            request.NodeId == s_notifier && request.ReferenceTypeId == ReferenceTypeIds.HasNotifier)).IsTrue();
    }

    [Test]
    [Arguments("Group", "group", "/teams/g")]
    [Arguments("Resource", "resource", "/teams/g/files/f")]
    public async Task DiscoveryAndDecodingRetainBothNativeDeprecationTransitions(
        string typePrefix, string entity, string subject)
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        var deprecated = new NodeId(310u, 2);
        var undeprecated = new NodeId(311u, 2);
        proxy.GeneratedEvents[s_registryType] =
        [
            new(deprecated, $"{typePrefix}DeprecatedEventType"),
            new(undeprecated, $"{typePrefix}UndeprecatedEventType"),
        ];

        var eventTypes = await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
            session, s_namespaceUris, s_notifier);
        var canonicalType = $"io.xregistry.{entity}.deprecated";
        await Assert.That(eventTypes.Count).IsEqualTo(2);
        await Assert.That(eventTypes[deprecated]).IsEqualTo(canonicalType);
        await Assert.That(eventTypes[undeprecated]).IsEqualTo(canonicalType);
        var decoder = new OpcUaRegistryEventDecoder(eventTypes);

        var foundDeprecated = decoder.TryDecode(
            OpcUaRegistryEventDecoderTests.Notification(deprecated, subject, [1]), out var first);
        var foundUndeprecated = decoder.TryDecode(
            OpcUaRegistryEventDecoderTests.Notification(undeprecated, subject, [2]), out var second);

        await Assert.That(foundDeprecated).IsTrue();
        await Assert.That(foundUndeprecated).IsTrue();
        await Assert.That(first!.Type).IsEqualTo(canonicalType);
        await Assert.That(second!.Type).IsEqualTo(canonicalType);
        await Assert.That(first.EventType).IsEqualTo(deprecated);
        await Assert.That(second.EventType).IsEqualTo(undeprecated);
        await Assert.That(first.Changed).IsNull();
        await Assert.That(second.Changed).IsNull();
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
        await Assert.That(filter.WhereClause.Elements.Count).IsEqualTo(0);
        await Assert.That(clauses.All(clause => string.IsNullOrEmpty(clause.IndexRange))).IsTrue();
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

    [Test]
    [Arguments("read")]
    [Arguments("browse")]
    [Arguments("browse-next")]
    public async Task DiscoveryRejectsUncertainResultsAndRetainsNativeDiagnostics(string operation)
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.GeneratedEvents[s_registryType] = [new(s_registryCreated, "RegistryCreatedEventType")];
        proxy.ReadStatus = operation == "read" ? StatusCodes.Uncertain : StatusCodes.Good;
        proxy.BrowseStatus = operation == "browse" ? StatusCodes.Uncertain : StatusCodes.Good;
        proxy.NextStatus = operation == "browse-next" ? StatusCodes.Uncertain : StatusCodes.Good;
        proxy.ReturnContinuationForGeneratedEvents = operation == "browse-next";

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(error.InnerException).IsTypeOf<ServiceResultException>();
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode).IsEqualTo(StatusCodes.Uncertain);
        await Assert.That(error.Data["OpcUa.ResponseHeader"]).IsSameReferenceAs(proxy.Header);
        var diagnostics = (ArrayOf<DiagnosticInfo>)error.Data["OpcUa.DiagnosticInfos"]!;
        await Assert.That(diagnostics[0]).IsSameReferenceAs(proxy.Diagnostics[0]);
        if (operation == "browse-next")
        {
            await Assert.That(proxy.BrowseNextReleaseFlags.Count).IsEqualTo(2);
            await Assert.That(proxy.BrowseNextReleaseFlags[0]).IsFalse();
            await Assert.That(proxy.BrowseNextReleaseFlags[1]).IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiscoveryRetainsFailedBrowseAndEveryContinuationCleanupDiagnostic(bool nextPage)
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.ReturnContinuationForGeneratedEvents = true;
        proxy.ContinuedEvents = [new(s_registryCreated, "RegistryCreatedEventType")];
        proxy.InitialEventBrowseStatus = nextPage ? StatusCodes.Good : StatusCodes.BadUserAccessDenied;
        proxy.NextStatus = StatusCodes.BadUserAccessDenied;
        proxy.NextContinuation = [2];
        proxy.ReleaseStatus = StatusCodes.Uncertain;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.BadUserAccessDenied);
        await Assert.That(proxy.ReleasedContinuations.Count).IsEqualTo(nextPage ? 2 : 1);
        await Assert.That(proxy.ReleasedContinuations[0]).IsEquivalentTo(s_continuationPoint);
        Exception[] cleanupFailures;
        if (nextPage)
        {
            await Assert.That(proxy.ReleasedContinuations[1]).IsEquivalentTo(new byte[] { 2 });
            var aggregate = (AggregateException)error.Data["OpcUa.ContinuationReleaseFailure"]!;
            await Assert.That(aggregate.InnerExceptions.Count).IsEqualTo(2);
            cleanupFailures = aggregate.InnerExceptions.ToArray();
        }
        else
        {
            cleanupFailures = [(Exception)error.Data["OpcUa.ContinuationReleaseFailure"]!];
        }
        foreach (var cleanup in cleanupFailures)
        {
            var failure = (FederationException)cleanup;
            await Assert.That(((ServiceResultException)failure.InnerException!).StatusCode)
                .IsEqualTo(StatusCodes.Uncertain);
            await Assert.That(failure.Data["OpcUa.ResponseHeader"]).IsSameReferenceAs(proxy.Header);
        }
    }

    [Test]
    [Arguments("ReadAsync", false)]
    [Arguments("BrowseAsync", false)]
    [Arguments("BrowseNextAsync", false)]
    [Arguments("ReadAsync", true)]
    [Arguments("BrowseAsync", true)]
    [Arguments("BrowseNextAsync", true)]
    public async Task DiscoveryMapsServiceFailureAndRetainsOriginalException(string operation, bool failAsynchronously)
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.ReturnContinuationForGeneratedEvents = true;
        proxy.FailOperation = operation;
        proxy.FailAsynchronously = failAsynchronously;
        var original = proxy.ServiceFailure;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(error.InnerException).IsSameReferenceAs(original);
        if (operation == "BrowseNextAsync")
        {
            await Assert.That(proxy.ReleasedContinuations.Count).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiscoveryRejectsNonGoodServiceDespiteGoodOperation(bool uncertain)
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        var status = uncertain ? StatusCodes.Uncertain : StatusCodes.BadUserAccessDenied;
        proxy.Header.ServiceResult = status;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(uncertain
            ? FederationErrorCode.Unavailable : FederationErrorCode.PolicyDenied);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode).IsEqualTo(status);
        await Assert.That(error.Data["OpcUa.ResponseHeader"]).IsSameReferenceAs(proxy.Header);
        await Assert.That(proxy.BrowseRequests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DiscoveryAcceptsGoodStatusSubcodes()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.GeneratedEvents[s_registryType] = [new(s_registryCreated, "RegistryCreatedEventType")];
        proxy.ReadStatus = StatusCodes.GoodClamped;
        proxy.BrowseStatus = StatusCodes.GoodClamped;

        var eventTypes = await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
            session, s_namespaceUris, s_notifier);

        await Assert.That(eventTypes.Count).IsEqualTo(1);
        await Assert.That(eventTypes[s_registryCreated]).IsEqualTo("io.xregistry.registry.created");
    }

    [Test]
    [Arguments("definition-class")]
    [Arguments("definition-mixed")]
    [Arguments("definition-duplicate")]
    [Arguments("parent-class")]
    [Arguments("parent-mixed")]
    [Arguments("parent-duplicate")]
    [Arguments("event-class")]
    [Arguments("child-class")]
    [Arguments("definition-namespace")]
    [Arguments("parent-namespace")]
    [Arguments("event-namespace")]
    [Arguments("child-namespace")]
    public async Task DiscoveryRejectsMalformedTypeAndNotifierDeclarations(string shape)
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.GeneratedEvents[s_registryType] = [new(s_registryCreated, "RegistryCreatedEventType")];
        proxy.ReferenceOverride = request =>
        {
            var relation = shape.Split('-')[0];
            var expected = relation switch
            {
                "definition" => ReferenceTypeIds.HasTypeDefinition,
                "parent" => ReferenceTypeIds.HasSubtype,
                "event" => ReferenceTypeIds.GeneratesEvent,
                "child" => ReferenceTypeIds.HasNotifier,
                _ => throw new InvalidOperationException("Unknown test relation.")
            };
            if (request.ReferenceTypeId != expected ||
                request.NodeId != (relation is "definition" or "child" ? s_notifier : s_registryType))
            {
                return null;
            }
            var type = relation switch
            {
                "definition" => s_registryType,
                "parent" => s_baseType,
                "event" => s_registryCreated,
                _ => s_groupNotifier
            };
            var valid = Declaration(type, relation == "child" ? NodeClass.Object : NodeClass.ObjectType);
            return shape.Split('-')[1] switch
            {
                "class" => [Declaration(type, NodeClass.Variable)],
                "mixed" => [valid, Declaration(s_otherBaseType, NodeClass.Variable)],
                "duplicate" => [valid, valid],
                "namespace" => [Declaration(new NodeId(42u, 9),
                    relation == "child" ? NodeClass.Object : NodeClass.ObjectType)],
                _ => throw new InvalidOperationException("Unknown malformed shape.")
            };
        };

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        if (shape.EndsWith("-namespace", StringComparison.Ordinal))
        {
            await Assert.That(error.Message).IsEqualTo(
                "The event declaration refers to a NodeId outside the connected Server AddressSpace.");
        }
        await Assert.That(s_namespaceUris.Count).IsEqualTo(3);
    }

    [Test]
    public async Task DiscoveryRejectsUnmappedSelectedNotifierBeforeReadingIt()
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
                session, s_namespaceUris, new NodeId(42u, 9)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(proxy.ReadCount).IsEqualTo(0);
        await Assert.That(proxy.BrowseRequests.Count).IsEqualTo(0);
        await Assert.That(s_namespaceUris.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    public async Task DiscoveryEnforcesExactTypeAndNotifierDepthBudgets(bool typeHierarchy, bool exceedsBudget)
    {
        var session = CreateSession();
        var proxy = (SessionProxy)(object)session;
        proxy.TypeDefinition = s_registryType;
        proxy.GeneratedEvents[s_registryType] = [new(s_registryCreated, "RegistryCreatedEventType")];
        var maxEdges = typeHierarchy ? 31 : 63;
        var edges = maxEdges + (exceedsBudget ? 1 : 0);
        var parent = typeHierarchy ? s_registryType : s_notifier;
        for (var index = 0; index < edges; index++)
        {
            var next = new NodeId((uint)(1000 + index), 2);
            if (typeHierarchy)
            {
                proxy.ParentTypes[parent] = [next];
            }
            else
            {
                proxy.NotifierChildren[parent] = [next];
                proxy.NotifierTypes[next] = s_registryType;
            }
            parent = next;
        }

        if (exceedsBudget)
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(session, s_namespaceUris, s_notifier));
            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            await Assert.That(error.Message).IsEqualTo(typeHierarchy
                ? "The notifier ObjectType hierarchy exceeds the event discovery depth limit."
                : "The notifier hierarchy exceeds the event discovery depth limit.");
        }
        else
        {
            var events = await OpcUaRegistryEventSubscription.DiscoverEventTypesAsync(
                session, s_namespaceUris, s_notifier);
            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[s_registryCreated]).IsEqualTo("io.xregistry.registry.created");
        }
        await Assert.That(proxy.ReadCount).IsEqualTo(typeHierarchy ? 1 : 64);
        await Assert.That(s_namespaceUris.Count).IsEqualTo(3);
    }

    private static ReferenceDescription Declaration(NodeId node, NodeClass nodeClass) => new()
    {
        NodeId = new ExpandedNodeId(node),
        BrowseName = new QualifiedName("RegistryCreatedEventType", 2),
        NodeClass = nodeClass
    };

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
        public StatusCode ReadStatus { get; set; }
        public StatusCode BrowseStatus { get; set; }
        public StatusCode NextStatus { get; set; }
        public StatusCode InitialEventBrowseStatus { get; set; }
        public StatusCode ReleaseStatus { get; set; }
        public byte[] NextContinuation { get; set; } = [];
        public List<byte[]> ReleasedContinuations { get; } = [];
        public string? FailOperation { get; set; }
        public bool FailAsynchronously { get; set; }
        public ServiceResultException ServiceFailure { get; } = new(StatusCodes.BadUserAccessDenied);
        public ResponseHeader Header { get; } = new();
        public ArrayOf<DiagnosticInfo> Diagnostics { get; } = [new DiagnosticInfo { AdditionalInfo = "native diagnostic" }];
        public Func<BrowseDescription, ReferenceDescription[]?>? ReferenceOverride { get; set; }
        public int ReadCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
#pragma warning disable CA2012
            if (targetMethod?.Name == FailOperation &&
                (FailOperation != "BrowseNextAsync" || !(bool)args![1]!))
            {
                if (!FailAsynchronously)
                {
                    throw ServiceFailure;
                }
                return FailOperation switch
                {
                    "ReadAsync" => ValueTask.FromException<ReadResponse>(ServiceFailure),
                    "BrowseAsync" => ValueTask.FromException<BrowseResponse>(ServiceFailure),
                    "BrowseNextAsync" => ValueTask.FromException<BrowseNextResponse>(ServiceFailure),
                    _ => throw new InvalidOperationException("Unexpected faulted service.")
                };
            }
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
            ReadCount++;
            var value = nodes[0].AttributeId == Attributes.EventNotifier
                ? new Variant(EventNotifier)
                : default;
            return new(new ReadResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new DataValue(value, ReadStatus)]
            });
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
                        ResponseHeader = Header,
                        DiagnosticInfos = Diagnostics,
                        Results = [new BrowseResult
                        {
                            References = new ArrayOf<ReferenceDescription>(),
                            StatusCode = InitialEventBrowseStatus,
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
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new BrowseResult
                {
                    References = new ArrayOf<ReferenceDescription>(ReferenceOverride?.Invoke(request) ?? references),
                    StatusCode = BrowseStatus
                }]
            });
        }

        private ValueTask<BrowseNextResponse> BrowseNext(bool release, ArrayOf<ByteString> continuationPoints)
        {
            BrowseNextReleaseFlags.Add(release);
            if (continuationPoints.Count != 1 ||
                (!continuationPoints[0].ToArray().SequenceEqual(s_continuationPoint) &&
                 !continuationPoints[0].ToArray().SequenceEqual(NextContinuation)))
            {
                throw new InvalidOperationException("The test received an unexpected continuation point.");
            }
            if (release)
            {
                ReleasedContinuations.Add(continuationPoints[0].ToArray());
            }
            var references = release
                ? []
                : ContinuedEvents.Select(item => Reference(item.NodeId, item.BrowseName, NodeClass.ObjectType)).ToArray();
            return new(new BrowseNextResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new BrowseResult
                {
                    References = new ArrayOf<ReferenceDescription>(references),
                    StatusCode = release ? ReleaseStatus : NextStatus,
                    ContinuationPoint = release ? default : new ByteString(NextContinuation)
                }]
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
