// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Opc.Ua;
using XRegistry;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>A validated xRegistry event decoded from an OPC UA EventFieldList.</summary>
public sealed record OpcUaRegistryEvent(
    string Type,
    string Id,
    NodeId SourceNode,
    string SourceName,
    string SourceUrl,
    string Subject,
    DateTimeOffset Time,
    uint? Epoch,
    uint? MetaEpoch,
    IReadOnlyList<string>? Changed,
    string? CorrelationId);

/// <summary>Decodes event fields selected in the xRegistry OPC UA binding's canonical field order.</summary>
public sealed class OpcUaRegistryEventDecoder
{
    private const int FieldCount = 11;
    private readonly ImmutableDictionary<NodeId, EventType> eventTypes;

    /// <summary>Creates a decoder using EventType NodeIds resolved from the companion model.</summary>
    public OpcUaRegistryEventDecoder(IReadOnlyDictionary<NodeId, string> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        var parsed = new Dictionary<NodeId, EventType>();
        foreach (var (nodeId, type) in eventTypes)
        {
            if (nodeId.IsNull || !TryParseType(type, out var eventType))
            {
                throw new ArgumentException("Event types must map non-null NodeIds to canonical xRegistry event types.",
                    nameof(eventTypes));
            }
            parsed.Add(nodeId, eventType);
        }
        this.eventTypes = parsed.ToImmutableDictionary();
    }

    /// <summary>
    /// Decodes fields selected in this order: EventId, EventType, SourceNode, SourceName, Time,
    /// SourceUrl, Subject, Epoch, MetaEpoch, Changed, CorrelationId.
    /// </summary>
    /// <returns>False for notifications whose EventType is not declared by the companion model.</returns>
    public bool TryDecode(EventFieldList notification, out OpcUaRegistryEvent? decoded)
    {
        ArgumentNullException.ThrowIfNull(notification);
        decoded = null;
        var fields = notification.EventFields.ToArray() ?? [];
        if (fields.Length != FieldCount)
        {
            throw InvalidEvent("The notification does not contain the eleven selected xRegistry event fields.");
        }

        if (fields[1].AsBoxedObject() is not NodeId eventTypeNode)
        {
            throw InvalidEvent("The notification EventType field is not a NodeId.");
        }
        if (!eventTypes.TryGetValue(eventTypeNode, out var type))
        {
            return false;
        }

        var eventId = ReadEventId(fields[0]);
        var sourceNode = ReadRequired<NodeId>(fields[2], "SourceNode");
        if (sourceNode.IsNull)
        {
            throw InvalidEvent("The notification SourceNode is null.");
        }
        var sourceName = ReadRequired<string>(fields[3], "SourceName");
        var time = ReadTime(fields[4]);
        var sourceUrl = ReadRequired<string>(fields[5], "SourceUrl");
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out _))
        {
            throw InvalidEvent("The notification SourceUrl is not an absolute URI.");
        }
        var subject = ReadRequired<string>(fields[6], "Subject");
        ValidateSubject(type, subject);
        var epoch = ReadOptionalUInt32(fields[7], "Epoch");
        var metaEpoch = ReadOptionalUInt32(fields[8], "MetaEpoch");
        var changed = ReadChanged(fields[9]);
        var correlationId = ReadOptionalString(fields[10], "CorrelationId");
        ValidateOptionalFields(type, epoch, metaEpoch, changed, correlationId);

        decoded = new OpcUaRegistryEvent(type.CanonicalName, Convert.ToBase64String(eventId), sourceNode,
            sourceName, sourceUrl, subject, time, epoch, metaEpoch, changed, correlationId);
        return true;
    }

    private static void ValidateOptionalFields(EventType type, uint? epoch, uint? metaEpoch,
        IReadOnlyList<string>? changed, string? correlationId)
    {
        var epochRequired = (type.Entity is "registry" or "group" or "resource" or "version") &&
            (type.Action is "created" or "updated");
        if (epochRequired != epoch.HasValue)
        {
            throw InvalidEvent(epochRequired
                ? "The event requires its final Epoch."
                : "The event must not contain Epoch.");
        }

        var metaEpochRequired = type.Entity == "resource" && (type.Action is "created" or "updated");
        if (metaEpochRequired != metaEpoch.HasValue)
        {
            throw InvalidEvent(metaEpochRequired
                ? "The Resource event requires its final MetaEpoch."
                : "The event must not contain MetaEpoch.");
        }

        if ((type.Action != "updated" || type.Entity is "model" or "modelsource") && changed is not null)
        {
            throw InvalidEvent("Changed is only defined on updated events and is not defined for model events.");
        }

        if (correlationId is { Length: 0 })
        {
            throw InvalidEvent("A present CorrelationId must not be empty.");
        }
    }

    private static void ValidateSubject(EventType type, string subject)
    {
        RegistryPath path;
        try
        {
            path = RegistryPath.Parse(subject);
        }
        catch (RegistryException exception)
        {
            throw InvalidEvent("The notification Subject is not a valid xRegistry xid.", exception);
        }

        var expectedKind = type.Entity switch
        {
            "registry" => RegistryPathKind.Registry,
            "model" => RegistryPathKind.Model,
            "modelsource" => RegistryPathKind.ModelSource,
            "capabilities" => RegistryPathKind.Capabilities,
            "group" => RegistryPathKind.Group,
            "resource" => RegistryPathKind.Resource,
            "version" => RegistryPathKind.Version,
            _ => throw InvalidEvent("The event type has no xRegistry subject kind.")
        };
        if (path.Kind != expectedKind || path.IsDetails)
        {
            throw InvalidEvent("The notification Subject does not match its event type.");
        }
    }

    private static byte[] ReadEventId(Variant value)
    {
        var bytes = value.AsBoxedObject() switch
        {
            ByteString byteString => byteString.ToArray(),
            byte[] array => array.ToArray(),
            _ => null
        };
        return bytes is { Length: > 0 }
            ? bytes
            : throw InvalidEvent("The notification EventId is not a nonempty ByteString.");
    }

    private static DateTimeOffset ReadTime(Variant value) =>
        value.AsBoxedObject() is DateTimeUtc time && !time.IsNull
            ? time.ToDateTimeOffset()
            : throw InvalidEvent("The notification Time is not a non-null OPC UA DateTime.");

    private static T ReadRequired<T>(Variant value, string name) =>
        value.AsBoxedObject() is T typed
            ? typed
            : throw InvalidEvent($"The notification {name} field has the wrong OPC UA type or is null.");

    private static uint? ReadOptionalUInt32(Variant value, string name) =>
        value.IsNull
            ? null
            : value.AsBoxedObject() is uint typed
                ? typed
                : throw InvalidEvent($"The notification {name} field is not UInt32.");

    private static string? ReadOptionalString(Variant value, string name) =>
        value.IsNull
            ? null
            : value.AsBoxedObject() is string typed
                ? typed
                : throw InvalidEvent($"The notification {name} field is not String.");

    private static ReadOnlyCollection<string>? ReadChanged(Variant value)
    {
        if (value.IsNull)
        {
            return null;
        }
        var values = value.AsBoxedObject() switch
        {
            string[] array => array,
            ArrayOf<string> array => array.ToArray(),
            ImmutableArray<string> array => array.ToArray(),
            _ => throw InvalidEvent("The notification Changed field is not a one-dimensional String array.")
        };
        if (values is null)
        {
            throw InvalidEvent("The notification Changed field is null.");
        }
        if (values.Any(static item => string.IsNullOrEmpty(item)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw InvalidEvent("The notification Changed field contains an empty or duplicate name.");
        }
        return Array.AsReadOnly(values);
    }

    private static bool TryParseType(string type, out EventType parsed)
    {
        parsed = default;
        const string prefix = "io.xregistry.";
        if (string.IsNullOrEmpty(type) || !type.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var parts = type[prefix.Length..].Split('.');
        if (parts.Length != 2)
        {
            return false;
        }
        var validEntityAction = (parts[0], parts[1]) switch
        {
            ("registry", "created" or "updated" or "deleted") => true,
            ("model", "updated") => true,
            ("modelsource", "updated") => true,
            ("capabilities", "updated") => true,
            ("group", "created" or "updated" or "deprecation" or "deleted") => true,
            ("resource", "created" or "updated" or "deprecation" or "deleted") => true,
            ("version", "created" or "updated" or "deleted") => true,
            _ => false
        };
        if (validEntityAction)
        {
            parsed = new(parts[0], parts[1]);
        }
        return validEntityAction;
    }

    private static FederationException InvalidEvent(string message, Exception? innerException = null) =>
        new(FederationErrorCode.InvalidPackage, message, innerException: innerException);

    private readonly record struct EventType(string Entity, string Action)
    {
        internal string CanonicalName => $"io.xregistry.{Entity}.{Action}";
    }
}
