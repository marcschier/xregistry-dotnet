// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace XRegistry;

/// <summary>An entity family with standardized xRegistry events.</summary>
public enum RegistryEventEntity
{
    /// <summary>Registry attributes and Group membership.</summary>
    Registry,
    /// <summary>The effective model.</summary>
    Model,
    /// <summary>The declared model source.</summary>
    ModelSource,
    /// <summary>Enabled capabilities.</summary>
    Capabilities,
    /// <summary>A Group.</summary>
    Group,
    /// <summary>A Resource, including changes to its Meta entity.</summary>
    Resource,
    /// <summary>A Version.</summary>
    Version
}

/// <summary>A standardized xRegistry entity action.</summary>
public enum RegistryEventAction
{
    /// <summary>The entity was created.</summary>
    Created,
    /// <summary>Attributes or containment changed.</summary>
    Updated,
    /// <summary>The Group or Resource deprecation object changed.</summary>
    Deprecation,
    /// <summary>The entity was deleted.</summary>
    Deleted
}

/// <summary>An immutable standardized event, prepared with its interaction rather than sent before commit.</summary>
public sealed class RegistryChangeEvent
{
    private readonly Lazy<RegistryJson> _json;

    internal RegistryChangeEvent(string source, string correlationId, DateTimeOffset time, int ordinal,
        RegistryEventEntity entity, RegistryEventAction action, string subject, string[]? changed)
    {
        Source = source;
        CorrelationId = correlationId;
        Time = time;
        Entity = entity;
        Action = action;
        Subject = subject;
        Id = correlationId + ":" + ordinal.ToString(CultureInfo.InvariantCulture);
        Changed = changed is null ? null : Array.AsReadOnly(changed);
        _json = new Lazy<RegistryJson>(Serialize);
    }

    /// <summary>Gets the event ID, unique if the host enforces Registry-lifetime correlation-ID uniqueness.</summary>
    public string Id { get; }
    /// <summary>Gets the absolute Registry-root URL, without a trailing slash.</summary>
    public string Source { get; }
    /// <summary>Gets the interaction ID, shared by every event and the associated response.</summary>
    public string CorrelationId { get; }
    /// <summary>Gets the common UTC interaction timestamp.</summary>
    public DateTimeOffset Time { get; }
    /// <summary>Gets the affected entity family.</summary>
    public RegistryEventEntity Entity { get; }
    /// <summary>Gets the action after interaction-wide precedence has been applied.</summary>
    public RegistryEventAction Action { get; }
    /// <summary>Gets the canonical Registry-relative event subject.</summary>
    public string Subject { get; }
    /// <summary>Gets the complete merged changed-name list, or null when unavailable or prohibited.</summary>
    public IReadOnlyList<string>? Changed { get; }
    /// <summary>Gets the exact standardized CloudEvents type.</summary>
    public string Type => "io.xregistry." + EntityName(Entity) + "." + ActionName(Action);

    /// <summary>Produces an owning structured JSON CloudEvent without reflection-based serialization.</summary>
    public RegistryJson ToJson() => _json.Value;

    private RegistryJson Serialize() => RegistryJson.Create(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("specversion", "1.0");
        writer.WriteString("type", Type);
        writer.WriteString("source", Source);
        writer.WriteString("subject", Subject);
        writer.WriteString("id", Id);
        writer.WriteString("time", Time.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
        writer.WriteString("xregcorrelationid", CorrelationId);
        if (Changed is not null)
        {
            writer.WriteStartObject("data");
            writer.WriteStartArray("changed");
            foreach (var name in Changed)
            {
                writer.WriteStringValue(name);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    });

    private static string EntityName(RegistryEventEntity entity) => entity switch
    {
        RegistryEventEntity.Registry => "registry",
        RegistryEventEntity.Model => "model",
        RegistryEventEntity.ModelSource => "modelsource",
        RegistryEventEntity.Capabilities => "capabilities",
        RegistryEventEntity.Group => "group",
        RegistryEventEntity.Resource => "resource",
        RegistryEventEntity.Version => "version",
        _ => throw new ArgumentOutOfRangeException(nameof(entity))
    };

    private static string ActionName(RegistryEventAction action) => action switch
    {
        RegistryEventAction.Created => "created",
        RegistryEventAction.Updated => "updated",
        RegistryEventAction.Deprecation => "deprecation",
        RegistryEventAction.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
