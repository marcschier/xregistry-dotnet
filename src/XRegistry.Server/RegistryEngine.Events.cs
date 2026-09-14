using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

/// <summary>An owned array of committed CloudEvents from one atomic Registry interaction.</summary>
public sealed record RegistryEventBatch(string CorrelationId, RegistryJson Events);

/// <summary>A host-defined event delivery boundary. There is no standardized HTTP watch/SSE endpoint.</summary>
public interface IRegistryEventSink
{
    /// <summary>Delivers a committed batch. Consumers must deduplicate event IDs because acknowledgement can fail.</summary>
    ValueTask PublishAsync(RegistryEventBatch batch, CancellationToken cancellationToken = default);
}

public sealed partial class RegistryEngine
{
    private const string EventsKey = "$events";
    private const string CorrelationsKey = "$correlations";

    /// <summary>Reads at most limit pending atomic batches. This is an authenticated host API, not a core HTTP route.</summary>
    public async ValueTask<IReadOnlyList<RegistryEventBatch>> ReadEventBatchesAsync(RegistryOperationContext context,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, Limits.MaxEntityOperations);
        await AuthorizeAsync(context, RegistryPath.Parse("/"), RegistryAccess.ReadEvents, cancellationToken).ConfigureAwait(false);
        using var snapshot = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<RegistryEventBatch>();
        long bytes = 0;
        foreach (var record in snapshot.GetChildren(EventsKey).Take(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += System.Text.Encoding.UTF8.GetByteCount(record.Metadata.RootElement.GetRawText());
            if (bytes > Limits.MaxResponseBytes)
            {
                throw ServerErrors.Create("too_large", "/", "The pending event batch response exceeds its byte budget.");
            }

            result.Add(new(record.Metadata.RootElement.GetProperty("correlationid").GetString()!,
                RegistryJson.FromElement(record.Metadata.RootElement.GetProperty("events"), Limits.Json with { MaxBytes = Limits.MaxResponseBytes })));
        }

        return result.AsReadOnly();
    }

    /// <summary>Atomically acknowledges one delivered batch without changing any entity Epoch.</summary>
    public async ValueTask AcknowledgeEventBatchAsync(string correlationId, RegistryOperationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        if (!Guid.TryParseExact(correlationId, "N", out var parsedId))
        {
            throw new ArgumentException("A correlation ID must be a compact GUID returned by the engine.", nameof(correlationId));
        }

        if (IsReadOnly)
        {
            throw ServerErrors.Create("readonly", "/", "The event outbox is read-only.");
        }

        await AuthorizeAsync(context, RegistryPath.Parse("/"), RegistryAccess.AcknowledgeEvents, cancellationToken).ConfigureAwait(false);
        using var snapshot = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        using var candidate = await _persistence.PrepareAsync(snapshot.Generation,
            [RegistryMutation.Delete(EventsKey + "/" + parsedId.ToString("N"))], cancellationToken).ConfigureAwait(false);
        await candidate.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delivers then acknowledges committed batches. A failure leaves unacknowledged batches eligible for redelivery.</summary>
    public async ValueTask<int> DeliverEventBatchesAsync(IRegistryEventSink sink, RegistryOperationContext context,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        await AuthorizeAsync(context, RegistryPath.Parse("/"), RegistryAccess.AcknowledgeEvents, cancellationToken).ConfigureAwait(false);
        var batches = await ReadEventBatchesAsync(context, limit, cancellationToken).ConfigureAwait(false);
        foreach (var batch in batches)
        {
            await sink.PublishAsync(batch, cancellationToken).ConfigureAwait(false);
            await AcknowledgeEventBatchAsync(batch.CorrelationId, context, cancellationToken).ConfigureAwait(false);
        }

        return batches.Count;
    }

    private sealed partial class Request
    {
        private string NewCorrelationId()
        {
            string id;
            do
            {
                Work();
                id = Guid.NewGuid().ToString("N");
            }
            while (_snapshot.Find(CorrelationsKey + "/" + id) is not null || _snapshot.Find(EventsKey + "/" + id) is not null);
            return id;
        }

        private RegistryMutation? EventMutation(string correlationId)
        {
            var interaction = new RegistryInteractionEvents(_engine.PublicRoot, correlationId,
                DateTimeOffset.Parse(_now, CultureInfo.InvariantCulture), new()
                {
                    MaxEvents = Math.Min(_engine.Limits.MaxEntityOperations, 65536),
                    MaxObservations = (int)Math.Min((long)_engine.Limits.MaxEntityOperations * 8, int.MaxValue),
                    MaxChangedAttributes = Math.Min(_engine.Limits.Json.MaxNodes, 65536),
                    MaxTotalChangedNameBytes = Math.Min(_engine.Limits.MaxResponseBytes, 8 * 1024 * 1024)
                });
            foreach (var entity in _entities.Values.Where(static entity => entity.Dirty).ToArray())
            {
                Work();
                var path = RegistryPath.Parse(entity.Key);
                var kind = path.Kind switch
                {
                    RegistryPathKind.Registry => RegistryEventEntity.Registry,
                    RegistryPathKind.Group => RegistryEventEntity.Group,
                    RegistryPathKind.Resource => RegistryEventEntity.Resource,
                    RegistryPathKind.Version => RegistryEventEntity.Version,
                    _ => throw new InvalidOperationException("An event was requested for a non-entity.")
                };
                var action = entity.Deleted ? RegistryEventAction.Deleted :
                    entity.IsNew ? RegistryEventAction.Created : RegistryEventAction.Updated;
                var changed = Changed(entity.Original, entity.Attributes);
                if (path.Kind == RegistryPathKind.Resource)
                {
                    changed = changed.Select(static name => "meta." + name).ToHashSet(StringComparer.Ordinal);
                }

                if (path.Kind == RegistryPathKind.Version &&
                    (entity.DocumentAction == RegistryDocumentAction.Replace ||
                     entity.DocumentAction == RegistryDocumentAction.Remove && entity.HadDocument))
                {
                    changed.Add(ResourceDefinition(path).Singular);
                }

                interaction.Record(kind, action, entity.Key, action == RegistryEventAction.Updated ? changed : null);
                if (path.Kind == RegistryPathKind.Group && (entity.IsNew || entity.Deleted))
                {
                    interaction.Record(RegistryEventEntity.Registry, RegistryEventAction.Updated, "/",
                        [path.GroupType!, path.GroupType + "count", "epoch", "modifiedat"]);
                }
                else if (path.Kind == RegistryPathKind.Resource && (entity.IsNew || entity.Deleted))
                {
                    interaction.Record(RegistryEventEntity.Group, RegistryEventAction.Updated,
                        ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!)),
                        [path.ResourceType!, path.ResourceType + "count", "epoch", "modifiedat"]);
                }
                else if (path.Kind == RegistryPathKind.Version)
                {
                    var resourceKey = ResourceKey(path);
                    _entities.TryGetValue(resourceKey, out var resource);
                    resource ??= Find(resourceKey);
                    if (entity.IsNew || entity.Deleted)
                    {
                        interaction.Record(RegistryEventEntity.Resource, RegistryEventAction.Updated, resourceKey,
                            ["versions", "versionscount", "meta.epoch", "meta.modifiedat"]);
                    }

                    if (resource is not null && (Id(entity) == ServerJson.Text(resource.Attributes, "defaultversionid") ||
                        Id(entity) == ServerJson.Text(resource.Original, "defaultversionid")))
                    {
                        interaction.Record(RegistryEventEntity.Resource, RegistryEventAction.Updated, resourceKey, changed);
                    }
                }

                if (path.Kind == RegistryPathKind.Resource &&
                    !ServerJson.Equal(entity.Original["defaultversionid"], entity.Attributes["defaultversionid"]))
                {
                    var previous = DefaultVersionState(entity, previous: true);
                    var next = DefaultVersionState(entity, previous: false);
                    if (previous.Metadata.HasValue && next.Metadata.HasValue)
                    {
                        interaction.RecordDefaultVersionChange(entity.Key, previous.Metadata.Value, next.Metadata.Value);
                        if (previous.HasDocument || next.HasDocument)
                        {
                            // Stored Documents are Version attributes even though their bytes are separate from metadata.
                            interaction.Record(RegistryEventEntity.Resource, RegistryEventAction.Updated, entity.Key,
                                [ResourceDefinition(path).Singular]);
                        }
                    }
                    else
                    {
                        interaction.Record(RegistryEventEntity.Resource, RegistryEventAction.Updated, entity.Key);
                    }
                }

                if (!entity.Deleted && kind is RegistryEventEntity.Group or RegistryEventEntity.Resource &&
                    !ServerJson.Equal(entity.Original["deprecated"], entity.Attributes["deprecated"]))
                {
                    interaction.Record(kind, RegistryEventAction.Deprecation, entity.Key,
                        Changed(entity.Original["deprecated"] as JsonObject ?? new(), entity.Attributes["deprecated"] as JsonObject ?? new()));
                }
            }

            if (_modelChanged)
            {
                interaction.Record(RegistryEventEntity.Registry, RegistryEventAction.Updated, "/", ["model", "modelsource"]);
                interaction.Record(RegistryEventEntity.Model, RegistryEventAction.Updated, "/model");
                interaction.Record(RegistryEventEntity.ModelSource, RegistryEventAction.Updated, "/modelsource");
            }

            if (_capabilitiesChanged)
            {
                interaction.Record(RegistryEventEntity.Registry, RegistryEventAction.Updated, "/", ["capabilities"]);
                interaction.Record(RegistryEventEntity.Capabilities, RegistryEventAction.Updated, "/capabilities",
                    Changed(ServerJson.Object(_previousCapabilities.Metadata), ServerJson.Object(_capabilities.Metadata)));
            }

            var events = new JsonArray();
            foreach (var change in interaction.Seal())
            {
                _ct.ThrowIfCancellationRequested();
                events.Add(JsonNode.Parse(change.ToJson().RootElement.GetRawText()));
            }

            if (events.Count == 0)
            {
                return null;
            }

            return RegistryMutation.Put(EventsKey + "/" + correlationId, ServerJson.Own(new JsonObject
            {
                ["correlationid"] = correlationId,
                ["events"] = events
            }, _engine.Limits.Json with { MaxBytes = _engine.Limits.MaxResponseBytes }));
        }

        private (JsonElement? Metadata, bool HasDocument) DefaultVersionState(Entity resource, bool previous)
        {
            var metadata = previous ? resource.Original : resource.Attributes;
            if (metadata["xref"] is not null)
            {
                return default;
            }

            var id = ServerJson.Text(metadata, "defaultversionid");
            if (id is null)
            {
                return (RegistryJson.Parse("{}").RootElement, false);
            }

            var key = resource.Key + "/versions/" + Uri.EscapeDataString(id);
            var version = _entities.GetValueOrDefault(key) ?? Find(key);
            if (version is null)
            {
                return default;
            }

            var hasDocument = previous ? version.HadDocument :
                version.DocumentAction == RegistryDocumentAction.Replace ||
                version.DocumentAction == RegistryDocumentAction.Preserve && version.HadDocument;
            return (ServerJson.Own(previous ? version.Original : version.Attributes, _engine.Limits.Json).RootElement, hasDocument);
        }

        private static HashSet<string> Changed(JsonObject before, JsonObject after) =>
            before.Select(static property => property.Key).Concat(after.Select(static property => property.Key))
                .Where(name => !ServerJson.Equal(before[name], after[name])).ToHashSet(StringComparer.Ordinal);
    }
}
