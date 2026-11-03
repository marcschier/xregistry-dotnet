// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using Opc.Ua.Client;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>Receives one validated xRegistry event from an OPC UA subscription.</summary>
public sealed class OpcUaRegistryEventEventArgs(OpcUaRegistryEvent value) : EventArgs
{
    /// <summary>The decoded event.</summary>
    public OpcUaRegistryEvent Value { get; } = value;
}

/// <summary>Discovers xRegistry EventTypes and owns an OPC UA event Subscription.</summary>
public sealed class OpcUaRegistryEventSubscription : IAsyncDisposable
{
    private const byte SubscribeToEventsMask = 1;
    private const int MaxTypeDepth = 32;
    private const int MaxNotifierDepth = 64;
    private const uint MaxReferencesPerBrowse = 1000;
    private static readonly Lazy<ITelemetryContext> s_telemetry =
        new(() => DefaultTelemetry.Create(static _ => { }));

    private static readonly Dictionary<string, string> s_canonicalTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RegistryCreatedEventType"] = "io.xregistry.registry.created",
            ["RegistryUpdatedEventType"] = "io.xregistry.registry.updated",
            ["RegistryDeletedEventType"] = "io.xregistry.registry.deleted",
            ["ModelUpdatedEventType"] = "io.xregistry.model.updated",
            ["ModelSourceUpdatedEventType"] = "io.xregistry.modelsource.updated",
            ["CapabilitiesUpdatedEventType"] = "io.xregistry.capabilities.updated",
            ["GroupCreatedEventType"] = "io.xregistry.group.created",
            ["GroupUpdatedEventType"] = "io.xregistry.group.updated",
            ["GroupDeprecatedEventType"] = "io.xregistry.group.deprecated",
            ["GroupUndeprecatedEventType"] = "io.xregistry.group.deprecated",
            ["GroupDeletedEventType"] = "io.xregistry.group.deleted",
            ["ResourceCreatedEventType"] = "io.xregistry.resource.created",
            ["ResourceUpdatedEventType"] = "io.xregistry.resource.updated",
            ["ResourceDeprecatedEventType"] = "io.xregistry.resource.deprecated",
            ["ResourceUndeprecatedEventType"] = "io.xregistry.resource.deprecated",
            ["ResourceDeletedEventType"] = "io.xregistry.resource.deleted",
            ["VersionCreatedEventType"] = "io.xregistry.version.created",
            ["VersionUpdatedEventType"] = "io.xregistry.version.updated",
            ["VersionDeletedEventType"] = "io.xregistry.version.deleted",
        };

    private readonly ManagedSession session;
    private readonly Subscription subscription;
    private readonly MonitoredItem monitoredItem;
    private readonly OpcUaRegistryEventDecoder decoder;
    private readonly object stopGate = new();
    private Task? stopTask;
    private uint remoteSubscriptionId;

    private OpcUaRegistryEventSubscription(ManagedSession session, Subscription subscription,
        MonitoredItem monitoredItem, OpcUaRegistryEventDecoder decoder)
    {
        this.session = session;
        this.subscription = subscription;
        this.monitoredItem = monitoredItem;
        this.decoder = decoder;
        monitoredItem.Notification += OnNotification;
    }

    /// <summary>Raised for each recognized, valid xRegistry event.</summary>
    public event EventHandler<OpcUaRegistryEventEventArgs>? EventReceived;

    /// <summary>Raised when a subscription notification cannot be decoded.</summary>
    public event EventHandler<Exception>? NotificationFailed;

    /// <summary>Discovers declared xRegistry EventTypes across the notifier hierarchy and ObjectType supertypes.</summary>
    public static async ValueTask<IReadOnlyDictionary<NodeId, string>> DiscoverEventTypesAsync(
        ISessionClient session, NamespaceTable namespaceUris, NodeId notifier,
        int maxBrowsePages = 10_000, int maxReferences = 1_000_000, int maxNotifiers = 10_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await DiscoverEventTypesForSessionAsync(session, namespaceUris, notifier, session.SessionId,
            maxBrowsePages, maxReferences, maxNotifiers, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<IReadOnlyDictionary<NodeId, string>> DiscoverEventTypesForSessionAsync(
        ISessionClient session, NamespaceTable namespaceUris, NodeId notifier, NodeId expectedSession,
        int maxBrowsePages = 10_000, int maxReferences = 1_000_000, int maxNotifiers = 10_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(namespaceUris);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBrowsePages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReferences);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNotifiers);
        if (notifier.IsNull)
        {
            throw new ArgumentException("A notifier NodeId is required.", nameof(notifier));
        }
        _ = ToLocalNodeId(namespaceUris, new ExpandedNodeId(notifier));
        RequireSession(session, expectedSession);
        var budget = new DiscoveryBudget(maxBrowsePages, maxReferences, maxNotifiers);

        var rootSupportsEvents =
            await SupportsEventSubscriptionsAsync(session, expectedSession, notifier, cancellationToken)
                .ConfigureAwait(false);
        if (!rootSupportsEvents)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The selected OPC UA Object does not support event subscriptions.");
        }

        var discovered = new Dictionary<NodeId, string>();
        var pendingNotifiers = new Queue<(NodeId Node, int Depth)>();
        var visitedNotifiers = new HashSet<NodeId>();
        budget.ChargeNotifier();
        pendingNotifiers.Enqueue((notifier, 0));
        while (pendingNotifiers.TryDequeue(out var pending))
        {
            if (pending.Depth >= MaxNotifierDepth)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded,
                    "The notifier hierarchy exceeds the event discovery depth limit.");
            }
            if (!visitedNotifiers.Add(pending.Node))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The notifier hierarchy contains a cycle or a notifier with competing parents.");
            }

            var supportsEvents = pending.Node == notifier ||
                await SupportsEventSubscriptionsAsync(session, expectedSession, pending.Node, cancellationToken)
                    .ConfigureAwait(false);
            if (supportsEvents)
            {
                var typeDefinitions = await BrowseAllAsync(session, pending.Node, BrowseDirection.Forward,
                    ReferenceTypeIds.HasTypeDefinition, expectedSession, budget, cancellationToken)
                    .ConfigureAwait(false);
                if (typeDefinitions.Count != 1 || typeDefinitions[0].NodeClass != NodeClass.ObjectType)
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        "An event notifier must have exactly one ObjectType definition.");
                }
                await AddEventTypesForTypeHierarchyAsync(session, namespaceUris,
                    ToLocalNodeId(namespaceUris, typeDefinitions[0].NodeId), discovered,
                    expectedSession, budget, cancellationToken).ConfigureAwait(false);
            }

            var children = await BrowseAllAsync(session, pending.Node, BrowseDirection.Forward,
                ReferenceTypeIds.HasNotifier, expectedSession, budget, cancellationToken).ConfigureAwait(false);
            foreach (var child in children)
            {
                if (child.NodeClass != NodeClass.Object)
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        "A HasNotifier reference must identify an Object.");
                }
                var childNode = ToLocalNodeId(namespaceUris, child.NodeId);
                budget.ChargeNotifier();
                pendingNotifiers.Enqueue((childNode, pending.Depth + 1));
            }
        }

        if (discovered.Count == 0)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The notifier hierarchy declares no recognized xRegistry EventTypes.");
        }
        return discovered;
    }

    private sealed class DiscoveryBudget(int pages, int references, int notifiers)
    {
        private int remainingPages = pages;
        private int remainingReferences = references;
        private int remainingNotifiers = notifiers;

        public void ChargePage() => Charge(ref remainingPages, 1, "page");
        public void ChargeReferences(int count) => Charge(ref remainingReferences, count, "reference");
        public void ChargeNotifier() => Charge(ref remainingNotifiers, 1, "notifier");

        private static void Charge(ref int remaining, int count, string kind)
        {
            if (count > remaining)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded,
                    $"The event discovery {kind} budget is exhausted.");
            }
            remaining -= count;
        }
    }

    private static async ValueTask AddEventTypesForTypeHierarchyAsync(ISessionClient session,
        NamespaceTable namespaceUris, NodeId typeDefinition, Dictionary<NodeId, string> discovered,
        NodeId expectedSession, DiscoveryBudget budget, CancellationToken cancellationToken)
    {
        var visited = new HashSet<NodeId>();
        var current = typeDefinition;
        for (var depth = 0; !current.IsNull; depth++)
        {
            if (depth >= MaxTypeDepth)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded,
                    "The notifier ObjectType hierarchy exceeds the event discovery depth limit.");
            }
            if (!visited.Add(current))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The notifier ObjectType hierarchy contains a cycle.");
            }

            var eventReferences = await BrowseAllAsync(session, current, BrowseDirection.Forward,
                ReferenceTypeIds.GeneratesEvent, expectedSession, budget, cancellationToken).ConfigureAwait(false);
            foreach (var reference in eventReferences)
            {
                if (reference.BrowseName.Name is { } browseName &&
                    s_canonicalTypes.TryGetValue(browseName, out var canonicalType))
                {
                    if (reference.NodeClass != NodeClass.ObjectType)
                    {
                        throw new FederationException(FederationErrorCode.InvalidPackage,
                            "A recognized GeneratesEvent declaration must identify an ObjectType.");
                    }
                    var eventTypeNode = ToLocalNodeId(namespaceUris, reference.NodeId);
                    if (discovered.TryGetValue(eventTypeNode, out var existing) &&
                        !string.Equals(existing, canonicalType, StringComparison.Ordinal))
                    {
                        throw new FederationException(FederationErrorCode.InvalidPackage,
                            "One OPC UA EventType NodeId maps to multiple xRegistry event types.");
                    }
                    discovered[eventTypeNode] = canonicalType;
                }
            }

            var parents = await BrowseAllAsync(session, current, BrowseDirection.Inverse,
                ReferenceTypeIds.HasSubtype, expectedSession, budget, cancellationToken).ConfigureAwait(false);
            if (parents.Count > 1)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The notifier ObjectType has competing supertypes.");
            }
            if (parents.Count == 1 && parents[0].NodeClass != NodeClass.ObjectType)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The notifier ObjectType hierarchy contains a non-ObjectType supertype.");
            }
            current = parents.Count == 0 ? NodeId.Null : ToLocalNodeId(namespaceUris, parents[0].NodeId);
        }
    }

    /// <summary>Discovers EventTypes, creates a Reporting event MonitoredItem, and starts publishing.</summary>
    public static async ValueTask<OpcUaRegistryEventSubscription> CreateAsync(
        OpcUaSessionConnection connection, NodeId notifier, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var expectedSession = connection.Session.SessionId;
        var namespaceUris = await connection.Adapter.ReadNamespaceArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireSession(connection.Session, expectedSession);
        var eventTypes = await DiscoverEventTypesForSessionAsync(connection.Session, namespaceUris, notifier,
            expectedSession, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        RequireSession(connection.Session, expectedSession);
        var decoder = new OpcUaRegistryEventDecoder(eventTypes);
        var eventNamespace = eventTypes.Keys.Select(static node => node.NamespaceIndex).Distinct().ToArray();
        if (eventNamespace.Length != 1)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "Discovered xRegistry EventTypes do not share one namespace index.");
        }

        var subscription = new Subscription(s_telemetry.Value, new SubscriptionOptions { PublishingEnabled = true });
        var monitoredItem = new MonitoredItem(s_telemetry.Value, new MonitoredItemOptions
        {
            StartNodeId = notifier,
            AttributeId = Attributes.EventNotifier,
            MonitoringMode = MonitoringMode.Reporting,
            Filter = CreateEventFilter(eventNamespace[0]),
        });
        var result = new OpcUaRegistryEventSubscription(connection.Session, subscription, monitoredItem, decoder);
        subscription.AddItem(monitoredItem);
        try
        {
            RequireSession(connection.Session, expectedSession);
        }
        catch
        {
            monitoredItem.Notification -= result.OnNotification;
            subscription.Dispose();
            throw;
        }
        if (!connection.Session.AddSubscription(subscription))
        {
            monitoredItem.Notification -= result.OnNotification;
            subscription.Dispose();
            throw new FederationException(FederationErrorCode.Unavailable,
                "The OPC UA Session rejected the event Subscription.");
        }

        try
        {
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
            result.remoteSubscriptionId = subscription.Id;
            return result;
        }
        catch (Exception createFailure)
        {
            monitoredItem.Notification -= result.OnNotification;
            try
            {
                _ = await connection.Session.RemoveSubscriptionAsync(subscription, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                subscription.Dispose();
                throw new AggregateException("Creating the OPC UA event Subscription failed and cleanup also failed.",
                    createFailure, cleanupFailure);
            }
            subscription.Dispose();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(createFailure).Throw();
            throw;
        }
    }

    /// <summary>Deletes the monitored item and Subscription from the Server.</summary>
    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (stopGate)
        {
            if (stopTask is null || stopTask.IsFaulted || stopTask.IsCanceled)
            {
                stopTask = StopCoreAsync(cancellationToken);
            }
            return new ValueTask(stopTask.WaitAsync(cancellationToken));
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        monitoredItem.Notification -= OnNotification;
        try
        {
            if (subscription.Created)
            {
                remoteSubscriptionId = subscription.Id;
            }
            else if (remoteSubscriptionId != 0)
            {
                // SDK deletion clears Id even when the remote deletion fails.
                var response = await OpcUaSessionAdapter.InvokeAsync(() => session.DeleteSubscriptionsAsync(
                    new RequestHeader(), [remoteSubscriptionId], cancellationToken)).ConfigureAwait(false);
                _ = OpcUaSessionAdapter.Single(response.Results,
                    static status => status.CodeBits == StatusCodes.BadSubscriptionIdInvalid.CodeBits
                        ? StatusCodes.Good : status,
                    response.DiagnosticInfos, response.ResponseHeader);
            }
            _ = await OpcUaSessionAdapter.InvokeAsync(async () =>
                await session.RemoveSubscriptionAsync(subscription, cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            remoteSubscriptionId = 0;
        }
        catch (FederationException failure) when (cancellationToken.IsCancellationRequested &&
            failure.InnerException is ServiceResultException native &&
            native.StatusCode.CodeBits == StatusCodes.BadRequestInterrupted.CodeBits)
        {
            monitoredItem.Notification += OnNotification;
            throw new OperationCanceledException("The Subscription deletion request was cancelled.",
                failure, cancellationToken);
        }
        catch
        {
            monitoredItem.Notification += OnNotification;
            throw;
        }
        subscription.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);

    private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs args)
    {
        if (args.NotificationValue is not EventFieldList fields)
        {
            NotificationFailed?.Invoke(this, new InvalidOperationException(
                "The OPC UA event MonitoredItem returned a non-event notification."));
            return;
        }

        OpcUaRegistryEvent? decoded;
        bool recognized;
        try
        {
            recognized = decoder.TryDecode(fields, out decoded);
        }
        catch (FederationException exception)
        {
            NotificationFailed?.Invoke(this, exception);
            return;
        }

        if (recognized && decoded is not null)
        {
            EventReceived?.Invoke(this, new OpcUaRegistryEventEventArgs(decoded));
        }
    }

    private static EventFilter CreateEventFilter(ushort eventNamespace)
    {
        var fields = new[]
        {
            "EventId", "EventType", "SourceNode", "SourceName", "Time",
            "SourceUrl", "Subject", "Epoch", "MetaEpoch", "Changed", "CorrelationId",
        };
        var selectClauses = fields.Select(name => new SimpleAttributeOperand(
            ObjectTypeIds.BaseEventType,
            new QualifiedName(name, name is "EventId" or "EventType" or "SourceNode" or "SourceName" or "Time"
                ? (ushort)0
                : eventNamespace))).ToArray();
        return new EventFilter
        {
            SelectClauses = new ArrayOf<SimpleAttributeOperand>(selectClauses),
            WhereClause = new ContentFilter(),
        };
    }

    private static async ValueTask<Variant> ReadValueAsync(ISessionClient session, NodeId expectedSession,
        NodeId node, uint attributeId, CancellationToken cancellationToken)
    {
        RequireSession(session, expectedSession);
        var response = await OpcUaSessionAdapter.InvokeAsync(() => session.ReadAsync(
            new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = node, AttributeId = attributeId }], cancellationToken))
            .ConfigureAwait(false);
        var value = OpcUaSessionAdapter.Single(response.Results, static result => result.StatusCode,
            response.DiagnosticInfos, response.ResponseHeader).WrappedValue;
        RequireSession(session, expectedSession);
        return value;
    }

    private static async ValueTask<bool> SupportsEventSubscriptionsAsync(ISessionClient session,
        NodeId expectedSession, NodeId notifier, CancellationToken cancellationToken)
    {
        var notifierValue = await ReadValueAsync(session, expectedSession, notifier,
            Attributes.EventNotifier, cancellationToken).ConfigureAwait(false);
        if (notifierValue.AsBoxedObject() is not byte eventNotifier)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The notifier's EventNotifier Attribute is not a Byte.");
        }
        return (eventNotifier & SubscribeToEventsMask) != 0;
    }

    private static async ValueTask<IReadOnlyList<ReferenceDescription>> BrowseAllAsync(ISessionClient session,
        NodeId node, BrowseDirection direction, NodeId referenceType, NodeId expectedSession,
        DiscoveryBudget budget, CancellationToken cancellationToken)
    {
        var references = new List<ReferenceDescription>();
        budget.ChargePage();
        var outstanding = new List<byte[]>();
        try
        {
            RequireSession(session, expectedSession);
            var response = await OpcUaSessionAdapter.InvokeAsync(() => session.BrowseAsync(
                new RequestHeader(), new ViewDescription(), MaxReferencesPerBrowse,
                [new BrowseDescription
                {
                    NodeId = node,
                    BrowseDirection = direction,
                    ReferenceTypeId = referenceType,
                    IncludeSubtypes = true,
                    NodeClassMask = (uint)NodeClass.ObjectType | (uint)NodeClass.Object,
                    ResultMask = (uint)BrowseResultMask.All,
                }], cancellationToken)).ConfigureAwait(false);
            TrackContinuation(response.Results, outstanding);
            var result = OpcUaSessionAdapter.Single(response.Results, static result => result.StatusCode,
                response.DiagnosticInfos, response.ResponseHeader);
            RequireSession(session, expectedSession);
            var continuation = result.ContinuationPoint.ToArray();
            budget.ChargeReferences(result.References.Count);
            references.AddRange(result.References.ToArray() ?? []);
            while (continuation.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireSession(session, expectedSession);
                budget.ChargePage();
                var next = await OpcUaSessionAdapter.InvokeAsync(() => session.BrowseNextAsync(
                    new RequestHeader(), false, [new ByteString(continuation)], cancellationToken)).ConfigureAwait(false);
                TrackContinuation(next.Results, outstanding);
                result = OpcUaSessionAdapter.Single(next.Results, static result => result.StatusCode,
                    next.DiagnosticInfos, next.ResponseHeader);
                RequireSession(session, expectedSession);
                continuation = result.ContinuationPoint.ToArray();
                outstanding.Clear();
                TrackContinuation(next.Results, outstanding);
                budget.ChargeReferences(result.References.Count);
                references.AddRange(result.References.ToArray() ?? []);
            }
        }
        catch (Exception failure) when (outstanding.Count > 0)
        {
            if (session.SessionId != expectedSession)
            {
                failure.Data["OpcUa.ContinuationReleaseFailure"] =
                    new FederationException(FederationErrorCode.Unavailable,
                        "The OPC UA Session changed; its Browse continuation cannot be released on the replacement Session.");
                throw;
            }
            foreach (var continuation in outstanding)
            {
                try
                {
                    RequireSession(session, expectedSession);
                    var release = await OpcUaSessionAdapter.InvokeAsync(() => session.BrowseNextAsync(
                        new RequestHeader(), true, [new ByteString(continuation)], CancellationToken.None))
                        .ConfigureAwait(false);
                    RequireSession(session, expectedSession);
                    _ = OpcUaSessionAdapter.Single(release.Results, static result => result.StatusCode,
                        release.DiagnosticInfos, release.ResponseHeader);
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
            throw;
        }
        return references;
    }

    private static void RequireSession(ISessionClient session, NodeId expectedSession)
    {
        if (session.SessionId != expectedSession)
        {
            throw new FederationException(FederationErrorCode.Unavailable,
                "The OPC UA Session changed during event-type discovery.");
        }
    }

    private static void TrackContinuation(ArrayOf<BrowseResult> results, List<byte[]> outstanding)
    {
        foreach (var result in results)
        {
            var continuation = result.ContinuationPoint.ToArray();
            if (continuation.Length > 0 &&
                !outstanding.Any(previous => previous.AsSpan().SequenceEqual(continuation)))
            {
                outstanding.Add(continuation);
            }
        }
    }

    private static NodeId ToLocalNodeId(NamespaceTable namespaceUris, ExpandedNodeId expandedNodeId)
    {
        var node = ExpandedNodeId.ToNodeId(expandedNodeId, namespaceUris, updateNamespaceTable: false);
        return node is { IsNull: false } && node.NamespaceIndex < namespaceUris.Count &&
            expandedNodeId.ServerIndex == 0
            ? node
            : throw new FederationException(FederationErrorCode.InvalidPackage,
                "The event declaration refers to a NodeId outside the connected Server AddressSpace.");
    }
}
