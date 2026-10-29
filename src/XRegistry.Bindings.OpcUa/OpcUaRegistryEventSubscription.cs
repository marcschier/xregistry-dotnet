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
            ["GroupDeprecatedEventType"] = "io.xregistry.group.deprecation",
            ["GroupUndeprecatedEventType"] = "io.xregistry.group.deprecation",
            ["GroupDeletedEventType"] = "io.xregistry.group.deleted",
            ["ResourceCreatedEventType"] = "io.xregistry.resource.created",
            ["ResourceUpdatedEventType"] = "io.xregistry.resource.updated",
            ["ResourceDeprecatedEventType"] = "io.xregistry.resource.deprecation",
            ["ResourceUndeprecatedEventType"] = "io.xregistry.resource.deprecation",
            ["ResourceDeletedEventType"] = "io.xregistry.resource.deleted",
            ["VersionCreatedEventType"] = "io.xregistry.version.created",
            ["VersionUpdatedEventType"] = "io.xregistry.version.updated",
            ["VersionDeletedEventType"] = "io.xregistry.version.deleted",
        };

    private readonly ManagedSession session;
    private readonly Subscription subscription;
    private readonly MonitoredItem monitoredItem;
    private readonly OpcUaRegistryEventDecoder decoder;
    private int stopped;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(namespaceUris);
        if (notifier.IsNull)
        {
            throw new ArgumentException("A notifier NodeId is required.", nameof(notifier));
        }

        var rootSupportsEvents =
            await SupportsEventSubscriptionsAsync(session, notifier, cancellationToken).ConfigureAwait(false);
        if (!rootSupportsEvents)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The selected OPC UA Object does not support event subscriptions.");
        }

        var discovered = new Dictionary<NodeId, string>();
        var pendingNotifiers = new Queue<(NodeId Node, int Depth)>();
        var visitedNotifiers = new HashSet<NodeId>();
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
                await SupportsEventSubscriptionsAsync(session, pending.Node, cancellationToken).ConfigureAwait(false);
            if (supportsEvents)
            {
                var typeDefinitions = await BrowseAllAsync(session, pending.Node, BrowseDirection.Forward,
                    ReferenceTypeIds.HasTypeDefinition, cancellationToken).ConfigureAwait(false);
                var typeDefinitionNodes = typeDefinitions
                    .Where(static reference => reference.NodeClass == NodeClass.ObjectType)
                    .Select(reference => ToLocalNodeId(namespaceUris, reference.NodeId))
                    .Distinct()
                    .ToArray();
                if (typeDefinitionNodes.Length != 1)
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        "An event notifier must have exactly one ObjectType definition.");
                }
                await AddEventTypesForTypeHierarchyAsync(session, namespaceUris, typeDefinitionNodes[0], discovered,
                    cancellationToken).ConfigureAwait(false);
            }

            var children = await BrowseAllAsync(session, pending.Node, BrowseDirection.Forward,
                ReferenceTypeIds.HasNotifier, cancellationToken).ConfigureAwait(false);
            foreach (var child in children.Where(static reference => reference.NodeClass == NodeClass.Object))
            {
                pendingNotifiers.Enqueue((ToLocalNodeId(namespaceUris, child.NodeId), pending.Depth + 1));
            }
        }

        if (discovered.Count == 0)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The notifier hierarchy declares no recognized xRegistry EventTypes.");
        }
        return discovered;
    }

    private static async ValueTask AddEventTypesForTypeHierarchyAsync(ISessionClient session,
        NamespaceTable namespaceUris, NodeId typeDefinition, Dictionary<NodeId, string> discovered,
        CancellationToken cancellationToken)
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
                ReferenceTypeIds.GeneratesEvent, cancellationToken).ConfigureAwait(false);
            foreach (var reference in eventReferences)
            {
                if (reference.BrowseName.Name is { } browseName &&
                    s_canonicalTypes.TryGetValue(browseName, out var canonicalType))
                {
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
                ReferenceTypeIds.HasSubtype, cancellationToken).ConfigureAwait(false);
            var parentNodes = parents
                .Where(static reference => reference.NodeClass == NodeClass.ObjectType)
                .Select(reference => ToLocalNodeId(namespaceUris, reference.NodeId))
                .Distinct()
                .ToArray();
            if (parentNodes.Length > 1)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "The notifier ObjectType has competing supertypes.");
            }
            current = parentNodes.Length == 0 ? NodeId.Null : parentNodes[0];
        }
    }

    /// <summary>Discovers EventTypes, creates a Reporting event MonitoredItem, and starts publishing.</summary>
    public static async ValueTask<OpcUaRegistryEventSubscription> CreateAsync(
        OpcUaSessionConnection connection, NodeId notifier, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var namespaceUris = await connection.Adapter.ReadNamespaceArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var eventTypes = await DiscoverEventTypesAsync(connection.Session, namespaceUris, notifier, cancellationToken)
            .ConfigureAwait(false);
        var decoder = new OpcUaRegistryEventDecoder(eventTypes);
        var eventNamespace = eventTypes.Keys.Select(static node => node.NamespaceIndex).Distinct().ToArray();
        if (eventNamespace.Length != 1)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "Discovered xRegistry EventTypes do not share one namespace index.");
        }

        var subscription = new Subscription(s_telemetry.Value, new SubscriptionOptions());
        var monitoredItem = new MonitoredItem(s_telemetry.Value, new MonitoredItemOptions
        {
            StartNodeId = notifier,
            AttributeId = Attributes.EventNotifier,
            MonitoringMode = MonitoringMode.Reporting,
            Filter = CreateEventFilter(eventNamespace[0]),
        });
        var result = new OpcUaRegistryEventSubscription(connection.Session, subscription, monitoredItem, decoder);
        subscription.AddItem(monitoredItem);
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
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
        {
            return;
        }

        monitoredItem.Notification -= OnNotification;
        try
        {
            _ = await session.RemoveSubscriptionAsync(subscription, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref stopped, 0);
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

    private static async ValueTask<Variant> ReadValueAsync(ISessionClient session, NodeId node,
        uint attributeId, CancellationToken cancellationToken)
    {
        var response = await session.ReadAsync(new RequestHeader(), 0, TimestampsToReturn.Neither,
            [new ReadValueId { NodeId = node, AttributeId = attributeId }], cancellationToken)
            .ConfigureAwait(false);
        if (response.Results.Count != 1 || StatusCode.IsBad(response.Results[0].StatusCode))
        {
            throw new FederationException(FederationErrorCode.Unavailable,
                "The OPC UA Server could not read the notifier's EventNotifier Attribute.");
        }
        return response.Results[0].WrappedValue;
    }

    private static async ValueTask<bool> SupportsEventSubscriptionsAsync(ISessionClient session, NodeId notifier,
        CancellationToken cancellationToken)
    {
        var notifierValue = await ReadValueAsync(session, notifier, Attributes.EventNotifier, cancellationToken)
            .ConfigureAwait(false);
        if (notifierValue.AsBoxedObject() is not byte eventNotifier)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The notifier's EventNotifier Attribute is not a Byte.");
        }
        return (eventNotifier & SubscribeToEventsMask) != 0;
    }

    private static async ValueTask<IReadOnlyList<ReferenceDescription>> BrowseAllAsync(ISessionClient session,
        NodeId node, BrowseDirection direction, NodeId referenceType, CancellationToken cancellationToken)
    {
        var references = new List<ReferenceDescription>();
        var response = await session.BrowseAsync(new RequestHeader(), new ViewDescription(), MaxReferencesPerBrowse,
            [new BrowseDescription
            {
                NodeId = node,
                BrowseDirection = direction,
                ReferenceTypeId = referenceType,
                IncludeSubtypes = true,
                NodeClassMask = (uint)NodeClass.ObjectType | (uint)NodeClass.Object,
                ResultMask = (uint)BrowseResultMask.All,
            }], cancellationToken).ConfigureAwait(false);
        var result = GetBrowseResult(response.Results);
        var continuation = result.ContinuationPoint.ToArray();
        try
        {
            references.AddRange(result.References.ToArray() ?? []);
            while (continuation.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = await session.BrowseNextAsync(new RequestHeader(), false, [new ByteString(continuation)],
                    cancellationToken).ConfigureAwait(false);
                result = GetBrowseResult(next.Results);
                continuation = result.ContinuationPoint.ToArray();
                references.AddRange(result.References.ToArray() ?? []);
            }
        }
        catch (Exception failure) when (continuation.Length > 0)
        {
            try
            {
                var release = await session.BrowseNextAsync(new RequestHeader(), true,
                    [new ByteString(continuation)], CancellationToken.None).ConfigureAwait(false);
                _ = GetBrowseResult(release.Results);
            }
            catch (Exception releaseFailure)
            {
                failure.Data["OpcUa.ContinuationReleaseFailure"] = releaseFailure;
            }
            throw;
        }
        return references;
    }

    private static BrowseResult GetBrowseResult(ArrayOf<BrowseResult> results) =>
        results.Count == 1 && !StatusCode.IsBad(results[0].StatusCode)
            ? results[0]
            : throw new FederationException(FederationErrorCode.Unavailable,
                "The OPC UA Server returned an invalid Browse result.");

    private static NodeId ToLocalNodeId(NamespaceTable namespaceUris, ExpandedNodeId expandedNodeId)
    {
        var node = ExpandedNodeId.ToNodeId(expandedNodeId, namespaceUris, updateNamespaceTable: false);
        return node is { IsNull: false } && expandedNodeId.ServerIndex == 0
            ? node
            : throw new FederationException(FederationErrorCode.InvalidPackage,
                "The event declaration refers to a NodeId outside the connected Server AddressSpace.");
    }
}
