# Interaction events

The core `RegistryInteractionEvents` accumulator implements the scoped
`core/events.md` envelope and interaction-wide combination rules. It is not
a subscription service or a second mutation engine.

```csharp
var events = new RegistryInteractionEvents(
    publicRegistryRoot, uniqueInteractionId, processingTime);
events.Record(
    RegistryEventEntity.Group,
    RegistryEventAction.Updated,
    "/dirs/d1",
    ["epoch", "modifiedat", "name"]);

var batch = events.Seal();
// Persist the prepared events with the authoritative mutation before delivery.
foreach (var change in batch)
{
    var structuredCloudEvent = change.ToJson();
}
```

The host supplies the Registry root, processing timestamp and interaction ID.
It must guarantee that interaction IDs are **case-insensitively unique for the
Registry's lifetime**, including after restarts. The accumulator cannot prove
that property without authoritative storage. Its event IDs are stable
`correlation:ordinal` values within that context. The same correlation ID must
appear in the response's `xRegistry-xregcorrelationid` header when events use it;
`CorrelationHeaderValue` provides the shared HTTP codec's safe encoding.

All events use the same UTC interaction time, normally the timestamp assigned
to changed entities. Structured events use CloudEvents `specversion: "1.0"`
and `io.xregistry.<entity>.<action>` types. No runtime serializer discovery is
needed.

## Combination and validation

For each subject, deleted outranks created, which outranks updated. Deprecation
is a separate event, deduplicated by subject/type. Known changed names merge
without duplicates. If any merged observation omits its changed-name knowledge,
the resulting updated/deprecation event omits the list rather than claiming an
incomplete subset. Created/deleted and model/modelsource events cannot receive
changed lists, even an empty list.

Subjects are checked against their entity types and canonicalized without
folding case-sensitive IDs. Resource Meta changes are Resource events whose
subject is the Resource, with names such as `meta.defaultversionid`; no
`io.xregistry.meta.*` type is invented.

`RecordDefaultVersionChange` includes every non-null attribute name from both
the old and new Version metadata and the changed Resource Meta pointer/time
fields. It does **not** emit Version-updated events merely because the default
pointer moved.

Limits bound distinct events, raw observations, supplied/merged names, encoded
name lengths and aggregate retained names. Failed observations do not partially
modify earlier entries. `Seal` precomputes bounded owning JSON before returning
an immutable batch; it cannot be changed afterward.

## Host responsibilities

The lifecycle engine decides which entity changes actually occurred and
records all required parent/membership/model/deprecation observations.
Creating an accumulator does not generate those effects automatically.
It also owns per-entity authorization and whether exposing changed names is
appropriate for the event recipient.

Prepared events must be committed with their mutation, normally through a
durable outbox, before a delivery sink observes them. Delivery retries and
consumer deduplication do not authorize automatic mutation retries. This module
does not define SSE, watch endpoints, transport-specific subscription APIs,
cross-registry ordering or exactly-once delivery. End-to-end server/outbox
integration remains a separate qualification requirement.
