using System.Text;
using System.Text.Json;
using XRegistry.Http;

namespace XRegistry;

/// <summary>Prepares one interaction's events with bounded coalescing, precedence and a single correlation/time context.</summary>
/// <remarks>
/// Not thread-safe. The authoritative host chooses applicable lifecycle events and enforces case-insensitive
/// Registry-lifetime correlation-ID uniqueness. Seal and store the batch with its transaction; do not deliver
/// it before commit. This class does not define subscriptions, replay, SSE or an event transport.
/// </remarks>
public sealed class RegistryInteractionEvents
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private readonly RegistryEventLimits _limits;
    private readonly Dictionary<(string Subject, bool Deprecation), Entry> _entries = [];
    private IReadOnlyList<RegistryChangeEvent>? _sealed;
    private int _observations;
    private int _nameBytes;

    /// <summary>Creates an event context using the authoritative Registry identity, unique interaction ID and processing time.</summary>
    public RegistryInteractionEvents(Uri source, string correlationId, DateTimeOffset time, RegistryEventLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (!source.IsAbsoluteUri || source.UserInfo.Length != 0 || source.Query.Length != 0 ||
            source.Fragment.Length != 0 || !source.IsWellFormedOriginalString() || source.AbsoluteUri.Length > 16384)
        {
            throw new ArgumentException("Events require a credential-free absolute Registry-root URL.", nameof(source));
        }
        if (correlationId.Any(char.IsControl) || s_utf8.GetByteCount(correlationId) > 1024)
        {
            throw new ArgumentException("A correlation ID must be bounded Unicode without control characters.", nameof(correlationId));
        }
        _limits = limits ?? new();
        _limits.Validate();
        Source = source.AbsoluteUri.TrimEnd('/');
        CorrelationId = correlationId;
        Time = time.ToUniversalTime();
    }

    /// <summary>Gets the absolute source Registry URL.</summary>
    public string Source { get; }
    /// <summary>Gets the shared interaction ID whose uniqueness is owned by the host.</summary>
    public string CorrelationId { get; }
    /// <summary>Gets the safe value for the response's xRegistry-xregcorrelationid header.</summary>
    public string CorrelationHeaderValue => RegistryHeaderEncoding.Encode(CorrelationId);
    /// <summary>Gets the normalized UTC processing timestamp.</summary>
    public DateTimeOffset Time { get; }

    /// <summary>Records one lifecycle observation after validating it without partially changing the accumulated batch.</summary>
    /// <remarks>
    /// Deleted outranks created, which outranks updated, for each subject. Deprecation is separate.
    /// Null changed names mean unavailable; combining an unavailable list with a known list cannot claim completeness.
    /// Created/deleted and model/modelsource events cannot receive a changed list.
    /// </remarks>
    public void Record(RegistryEventEntity entity, RegistryEventAction action, string subject,
        IEnumerable<string>? changed = null)
    {
        if (_sealed is not null)
        {
            throw new InvalidOperationException("The interaction's event batch has already been sealed.");
        }
        if (!Enum.IsDefined(entity) || !Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(entity), "The event entity and action must be standardized values.");
        }
        var canonical = ValidateSubject(entity, subject);
        if ((entity is RegistryEventEntity.Registry or RegistryEventEntity.Model or RegistryEventEntity.ModelSource or
                RegistryEventEntity.Capabilities && action != RegistryEventAction.Updated) ||
            (action == RegistryEventAction.Deprecation && entity is not (RegistryEventEntity.Group or RegistryEventEntity.Resource)))
        {
            throw new ArgumentException("This action is not defined for the event entity.", nameof(action));
        }
        var prohibitChanges = action is RegistryEventAction.Created or RegistryEventAction.Deleted ||
            entity is RegistryEventEntity.Model or RegistryEventEntity.ModelSource;
        if (prohibitChanges && changed is not null)
        {
            throw new ArgumentException("This event must not include changed attributes.", nameof(changed));
        }
        if (_observations == _limits.MaxObservations)
        {
            throw Diagnostics.Error("event_limit", subject, "The interaction exhausted its event-observation budget.");
        }

        HashSet<string>? names = changed is null ? null : new(StringComparer.Ordinal);
        var supplied = 0;
        if (changed is not null)
        {
            foreach (var name in changed)
            {
                if (++supplied > _limits.MaxChangedAttributes)
                {
                    throw Diagnostics.Error("event_limit", subject, "An event exceeded its changed-name work budget.");
                }
                ArgumentException.ThrowIfNullOrEmpty(name);
                if (name.Any(char.IsControl) || s_utf8.GetByteCount(name) > _limits.MaxChangedNameBytes)
                {
                    throw new ArgumentException("A changed name must be bounded Unicode without control characters.", nameof(changed));
                }
                names!.Add(name);
            }
        }
        var key = (canonical, action == RegistryEventAction.Deprecation);
        _entries.TryGetValue(key, out var prior);
        if (prior is null && _entries.Count == _limits.MaxEvents)
        {
            throw Diagnostics.Error("event_limit", subject, "The interaction exhausted its distinct-event budget.");
        }
        if (prior is not null)
        {
            if (Priority(prior.Action) > Priority(action))
            {
                action = prior.Action;
                names = prior.Changed;
            }
            else if (Priority(prior.Action) == Priority(action))
            {
                if (names is null || prior.Changed is null)
                {
                    names = null;
                }
                else
                {
                    names.UnionWith(prior.Changed);
                }
            }
        }
        if (names?.Count > _limits.MaxChangedAttributes)
        {
            throw Diagnostics.Error("event_limit", subject, "The merged event exceeds its changed-name budget.");
        }
        var bytes = names?.Sum(name => (long)s_utf8.GetByteCount(name)) ?? 0;
        if (bytes > _limits.MaxTotalChangedNameBytes - (_nameBytes - (prior?.Bytes ?? 0)))
        {
            throw Diagnostics.Error("event_limit", subject, "The interaction exceeds its changed-name byte budget.");
        }
        _entries[key] = new(entity, action, canonical, names, checked((int)bytes));
        _nameBytes = checked(_nameBytes - (prior?.Bytes ?? 0) + (int)bytes);
        _observations++;
    }

    /// <summary>Records a Resource default-pointer change without inventing updates to either Version.</summary>
    /// <remarks>Includes every non-null old/new Version attribute plus the Resource Meta pointer and processing fields.</remarks>
    public void RecordDefaultVersionChange(string resourceSubject, JsonElement previousVersion, JsonElement nextVersion)
    {
        if (previousVersion.ValueKind != JsonValueKind.Object || nextVersion.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Default-Version changes require both Version metadata objects.");
        }
        var names = previousVersion.EnumerateObject().Concat(nextVersion.EnumerateObject())
            .Where(static property => property.Value.ValueKind != JsonValueKind.Null)
            .Select(static property => property.Name)
            .Concat(["meta.defaultversionid", "meta.epoch", "meta.modifiedat"]);
        Record(RegistryEventEntity.Resource, RegistryEventAction.Updated, resourceSubject, names);
    }

    /// <summary>Prepares serializable immutable events and stable per-interaction IDs. Repeated calls return the same batch.</summary>
    public IReadOnlyList<RegistryChangeEvent> Seal()
    {
        if (_sealed is not null)
        {
            return _sealed;
        }
        var result = _entries.Values.OrderBy(static entry => entry.Subject, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Action).Select((entry, ordinal) => new RegistryChangeEvent(
                Source, CorrelationId, Time, ordinal, entry.Entity, entry.Action, entry.Subject,
                entry.Changed?.Order(StringComparer.Ordinal).ToArray())).ToArray();
        foreach (var change in result)
        {
            change.ToJson();
        }
        _sealed = Array.AsReadOnly(result);
        return _sealed;
    }

    private static int Priority(RegistryEventAction action) => action switch
    {
        RegistryEventAction.Updated => 0,
        RegistryEventAction.Created => 1,
        RegistryEventAction.Deleted => 2,
        RegistryEventAction.Deprecation => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static string ValidateSubject(RegistryEventEntity entity, string subject)
    {
        var path = RegistryPath.Parse(subject);
        var expected = entity switch
        {
            RegistryEventEntity.Registry => RegistryPathKind.Registry,
            RegistryEventEntity.Model => RegistryPathKind.Model,
            RegistryEventEntity.ModelSource => RegistryPathKind.ModelSource,
            RegistryEventEntity.Capabilities => RegistryPathKind.Capabilities,
            RegistryEventEntity.Group => RegistryPathKind.Group,
            RegistryEventEntity.Resource => RegistryPathKind.Resource,
            RegistryEventEntity.Version => RegistryPathKind.Version,
            _ => throw new ArgumentOutOfRangeException(nameof(entity))
        };
        if (path.Kind != expected || path.IsDetails)
        {
            throw new ArgumentException("The event subject does not match its entity type.", nameof(subject));
        }
        return expected switch
        {
            RegistryPathKind.Group => RegistryPath.ForGroup(path.GroupType!, path.GroupId!).ToXid(),
            RegistryPathKind.Resource => RegistryPath.ForResource(path.GroupType!, path.GroupId!, path.ResourceType!, path.ResourceId!).ToXid(),
            RegistryPathKind.Version => RegistryPath.ForVersion(path.GroupType!, path.GroupId!, path.ResourceType!, path.ResourceId!, path.VersionId!).ToXid(),
            RegistryPathKind.Model => "/model",
            RegistryPathKind.ModelSource => "/modelsource",
            RegistryPathKind.Capabilities => "/capabilities",
            _ => "/"
        };
    }

    private sealed record Entry(RegistryEventEntity Entity, RegistryEventAction Action, string Subject,
        HashSet<string>? Changed, int Bytes);
}
