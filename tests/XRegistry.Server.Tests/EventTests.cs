// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.RegistryCapabilityTests;

namespace XRegistry.Server.Tests;

public class EventTests
{
    [Test]
    [Arguments("registry")]
    [Arguments("group")]
    [Arguments("resource")]
    [Arguments("default-version")]
    [Arguments("other-version")]
    [Arguments("meta")]
    public async Task AttributeEventsMatchTheChangedEntityAndItsDefaultProjection(string target)
    {
        var engine = await CreateVersionedNotes();
        const string resource = "/teams/g/notes/n";
        var (path, subjects) = target switch
        {
            "registry" => ("/", new[] { "/" }),
            "group" => ("/teams/g", ["/teams/g"]),
            "resource" => (resource, [resource, resource + "/versions/v1"]),
            "default-version" => (resource + "/versions/v1", [resource, resource + "/versions/v1"]),
            "other-version" => (resource + "/versions/v2", [resource + "/versions/v2"]),
            "meta" => (resource + "/meta", [resource]),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        var result = await Send(engine, RegistryAction.Patch, path,
            target == "meta" ? """{"labels":{"site":"east"}}""" : """{"name":"changed"}""");
        var events = await EventsFor(engine, result);
        await Assert.That(events.Select(static item => item.GetProperty("subject").GetString()!).ToArray()).IsEquivalentTo(subjects, StringComparer.Ordinal);
        foreach (var item in events)
        {
            var subject = item.GetProperty("subject").GetString();
            var entity = subject == "/" ? "registry" : subject == "/teams/g" ? "group" :
                subject == resource ? "resource" : "version";
            await Assert.That(item.GetProperty("type").GetString()).IsEqualTo("io.xregistry." + entity + ".updated");
            var expected = target == "meta" ? new[] { "meta.epoch", "meta.modifiedat", "meta.labels" } :
                ["epoch", "modifiedat", "name"];
            await Assert.That(ChangedNames(item)).IsEquivalentTo(expected, StringComparer.Ordinal);
        }
    }

    [Test]
    [Arguments("group")]
    [Arguments("resource")]
    [Arguments("version")]
    public async Task DeletionEventsCoverDescendantsAndOnlyTheImmediateParentCollection(string target)
    {
        var engine = await CreateVersionedNotes();
        const string resource = "/teams/g/notes/n";
        var (path, parent, deleted, changed) = target switch
        {
            "group" => ("/teams/g", "/", new[] { "/teams/g", resource, resource + "/versions/v1", resource + "/versions/v2" },
                new[] { "epoch", "modifiedat", "teams", "teamscount" }),
            "resource" => (resource, "/teams/g", [resource, resource + "/versions/v1", resource + "/versions/v2"],
                ["epoch", "modifiedat", "notes", "notescount"]),
            "version" => (resource + "/versions/v2", resource, [resource + "/versions/v2"],
                ["meta.epoch", "meta.modifiedat", "versions", "versionscount"]),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        var result = await Send(engine, RegistryAction.Delete, path);
        var events = await EventsFor(engine, result);
        await Assert.That(events.Select(static item => item.GetProperty("subject").GetString()!).ToArray())
            .IsEquivalentTo(deleted.Append(parent).ToArray(), StringComparer.Ordinal);
        foreach (var item in events)
        {
            var subject = item.GetProperty("subject").GetString();
            var entity = subject == "/" ? "registry" : subject == "/teams/g" ? "group" :
                subject == resource ? "resource" : "version";
            await Assert.That(item.GetProperty("type").GetString()).IsEqualTo("io.xregistry." + entity +
                (subject == parent ? ".updated" : ".deleted"));
            if (subject == parent)
            {
                await Assert.That(ChangedNames(item)).IsEquivalentTo(changed, StringComparer.Ordinal);
                await AssertEventEpochMatchesRead(engine, item, parent, parent == resource ? parent + "/meta" : null);
            }
            else
            {
                await Assert.That(item.TryGetProperty("data", out _)).IsFalse();
            }
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DeprecationChangesAndRemovalHaveSeparateObjectAndEntityChangedLists(bool resource, bool remove)
    {
        var engine = await CreateVersionedNotes();
        var subject = resource ? "/teams/g/notes/n" : "/teams/g";
        var path = resource ? subject + "/meta" : subject;
        const string deprecation = """{"deprecated":{"effective":"2025-01-01T00:00:00Z","reason":"retired"}}""";
        if (remove) { await Send(engine, RegistryAction.Patch, path, deprecation); }
        var result = await Send(engine, RegistryAction.Patch, path, remove ? """{"deprecated":null}""" : deprecation);
        var events = await EventsFor(engine, result);
        var prefix = resource ? "io.xregistry.resource." : "io.xregistry.group.";
        await Assert.That(events.Select(static item => item.GetProperty("type").GetString()!).ToArray())
            .IsEquivalentTo([prefix + "updated", prefix + "deprecated"], StringComparer.Ordinal);
        await Assert.That(events.All(item => item.GetProperty("subject").GetString() == subject)).IsTrue();
        var deprecationEvent = events.Single(item => item.GetProperty("type").GetString() == prefix + "deprecated");
        await Assert.That(HasChangedData(deprecationEvent)).IsFalse();
        var expected = resource ? new[] { "meta.epoch", "meta.modifiedat", "meta.deprecated" } :
            ["epoch", "modifiedat", "deprecated"];
        await Assert.That(ChangedNames(events.Single(item => item.GetProperty("type").GetString() == prefix + "updated")))
            .IsEquivalentTo(expected, StringComparer.Ordinal);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewResourceAndNondefaultVersionCreationReportExactMembershipChanges(bool newResource)
    {
        var engine = await CreateVersionedNotes();
        var resource = newResource ? "/teams/g/notes/second" : "/teams/g/notes/n";
        var path = resource + "/versions/v3";
        var result = await Send(engine, RegistryAction.Replace, path, "{}");
        var events = await EventsFor(engine, result);
        await Assert.That(events.Select(static item => item.GetProperty("subject").GetString()!).ToArray())
            .IsEquivalentTo(newResource ? new[] { "/teams/g", resource, path } : [resource, path], StringComparer.Ordinal);
        var createdVersion = events.Single(item => item.GetProperty("subject").GetString() == path);
        await Assert.That(createdVersion.GetProperty("type").GetString()).IsEqualTo("io.xregistry.version.created");
        await Assert.That(createdVersion.GetProperty("data").TryGetProperty("changed", out _)).IsFalse();
        await AssertEventEpochMatchesRead(engine, createdVersion, path);
        var resourceEvent = events.Single(item => item.GetProperty("subject").GetString() == resource);
        await Assert.That(resourceEvent.GetProperty("type").GetString())
            .IsEqualTo("io.xregistry.resource." + (newResource ? "created" : "updated"));
        if (newResource)
        {
            await Assert.That(resourceEvent.GetProperty("data").TryGetProperty("changed", out _)).IsFalse();
            await AssertEventEpochMatchesRead(engine, resourceEvent, resource, resource + "/meta");
            var group = events.Single(static item => item.GetProperty("subject").GetString() == "/teams/g");
            await Assert.That(group.GetProperty("type").GetString()).IsEqualTo("io.xregistry.group.updated");
            await Assert.That(ChangedNames(group)).IsEquivalentTo(["epoch", "modifiedat", "notes", "notescount"], StringComparer.Ordinal);
            await AssertEventEpochMatchesRead(engine, group, "/teams/g");
        }
        else
        {
            await Assert.That(ChangedNames(resourceEvent)).IsEquivalentTo(["meta.epoch", "meta.modifiedat", "versions", "versionscount"], StringComparer.Ordinal);
            await AssertEventEpochMatchesRead(engine, resourceEvent, resource, resource + "/meta");
        }
    }

    [Test]
    public async Task CommittedEventsUseCoreStructuredIdsAndOneCanonicalInteractionTime()
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "core-events",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
            TimeProvider = new EventClock()
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        var result = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        var batch = (await engine.ReadEventBatchesAsync(Writer())).Single();
        var events = batch.Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events[0].GetProperty("id").GetString()).IsEqualTo(result.CorrelationId + ":0");
        await Assert.That(events[^1].GetProperty("id").GetString()).IsEqualTo(result.CorrelationId + ":3");
        await Assert.That(events.All(static item => item.GetProperty("time").GetString() == "2025-01-01T00:00:00Z")).IsTrue();
        await Assert.That(events.All(static item => item.GetProperty("specversion").GetString() == "1.0")).IsTrue();
        await Assert.That(events.All(static item => item.GetProperty("source").GetString() == "https://registry.example")).IsTrue();
    }

    [Test]
    public async Task CorrelationReservationsSurviveOutboxAcknowledgementAndEngineRestart()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        var result = await Send(engine, RegistryAction.Replace, "/teams/g", "{}");
        var correlation = result.CorrelationId!;
        await engine.AcknowledgeEventBatchAsync(correlation.ToUpperInvariant(), Writer());
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        using var acknowledged = await store.ReadSnapshotAsync();
        var reservation = acknowledged.Find("$correlations/" + correlation);
        await Assert.That(reservation).IsNotNull();
        await Assert.That(reservation!.Metadata.RootElement.GetProperty("correlationid").GetString()).IsEqualTo(correlation);
        var restarted = Create(persistence: store);
        var next = await Send(restarted, RegistryAction.Patch, "/teams/g", "{}");
        await Assert.That(string.Equals(next.CorrelationId, correlation, StringComparison.OrdinalIgnoreCase)).IsFalse();
        using var current = await store.ReadSnapshotAsync();
        await Assert.That(current.GetChildren("$correlations").Count()).IsEqualTo(2);
        await Assert.That(current.Find("$correlations/" + correlation)).IsNotNull();
    }

    [Test]
    public async Task CorrelationReservationAndOutboxShareTheAtomicPublicationQuota()
    {
        var store = new InMemoryRegistryPersistence(maxRecords: 4);
        var engine = Create(persistence: store);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g", "{}"), "operation_limit");
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
        await Assert.That(snapshot.EnumerateRecords().Count()).IsEqualTo(0);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task DefaultPointerEventsIncludeBothVersionProjectionsWithoutInventingVersionUpdates()
    {
        var engine = Create("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,
              "attributes":{"oldonly":{"type":"boolean"},"newonly":{"type":"string"}}}}}}}
            """);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1","oldonly":false}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"v2","newonly":""}""");
        var result = await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"v1"}""");
        var batch = (await engine.ReadEventBatchesAsync(Writer())).Single(batch => batch.CorrelationId == result.CorrelationId);
        var events = batch.Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.updated");
        await Assert.That(events[0].GetProperty("subject").GetString()).IsEqualTo("/teams/g/notes/n");
        var names = events[0].GetProperty("data").GetProperty("changed").EnumerateArray().Select(static value => value.GetString()).ToArray();
        await Assert.That(names.Contains("oldonly", StringComparer.Ordinal)).IsTrue();
        await Assert.That(names.Contains("newonly", StringComparer.Ordinal)).IsTrue();
        await Assert.That(names.Contains("meta.defaultversionid", StringComparer.Ordinal)).IsTrue();
        await Assert.That(names.Contains("meta.epoch", StringComparer.Ordinal)).IsTrue();
        await Assert.That(names.Contains("meta.modifiedat", StringComparer.Ordinal)).IsTrue();
        await Assert.That(names.Contains("note", StringComparer.Ordinal)).IsFalse();
    }

    [Test]
    public async Task DefaultPointerEventsIncludeTheStoredDocumentAttributeWithoutUpdatingVersions()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f/versions/v1$details",
            """{"contenttype":"application/json","file":{"value":1}}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f/versions/v2$details",
            """{"contenttype":"application/json","file":{"value":2}}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/files/f$details", null, new KeyValuePair<string, string?>("inline", "file")))
            .Metadata!.RootElement;
        await Assert.That(before.GetProperty("versionid").GetString()).IsEqualTo("v2");
        await Assert.That(before.GetProperty("file").GetProperty("value").GetInt32()).IsEqualTo(2);
        var result = await Send(engine, RegistryAction.Patch, "/teams/g/files/f/meta", """{"defaultversionid":"v1"}""");
        var after = (await Send(engine, RegistryAction.Read, "/teams/g/files/f$details", null, new KeyValuePair<string, string?>("inline", "file")))
            .Metadata!.RootElement;
        await Assert.That(after.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(after.GetProperty("file").GetProperty("value").GetInt32()).IsEqualTo(1);
        var batch = (await engine.ReadEventBatchesAsync(Writer())).Single(batch => batch.CorrelationId == result.CorrelationId);
        var events = batch.Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.updated");
        await Assert.That(events[0].GetProperty("subject").GetString()).IsEqualTo("/teams/g/files/f");
        var names = events[0].GetProperty("data").GetProperty("changed").EnumerateArray().Select(static value => value.GetString()).ToArray();
        await Assert.That(names.Contains("file", StringComparer.Ordinal)).IsTrue();
        await Assert.That(names.Contains("meta.defaultversionid", StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task DefaultPointerEventsDistinguishEmptyStoredDocumentsFromExternalOnlyContent(
        bool previousStored, bool nextStored)
    {
        var engine = CreateDocumentReferenceEngine();
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f/versions/v1$details", nextStored
            ? """{"filebase64":""}"""
            : """{"fileurl":"https://documents.example/v1"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f/versions/v2$details", previousStored
            ? """{"filebase64":""}"""
            : """{"fileurl":"https://documents.example/v2"}""");
        var result = await Send(engine, RegistryAction.Patch, "/teams/g/files/f/meta", """{"defaultversionid":"v1"}""");
        var batch = (await engine.ReadEventBatchesAsync(Writer())).Single(batch => batch.CorrelationId == result.CorrelationId);
        var events = batch.Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.updated");
        var names = events[0].GetProperty("data").GetProperty("changed").EnumerateArray().Select(static value => value.GetString()).ToArray();
        await Assert.That(names.Contains("file", StringComparer.Ordinal)).IsEqualTo(previousStored || nextStored);
        await Assert.That(names.Contains("fileurl", StringComparer.Ordinal)).IsEqualTo(!previousStored || !nextStored);
        await Assert.That(names.Count(static name => name == "file")).IsEqualTo(previousStored || nextStored ? 1 : 0);
    }

    [Test]
    [Arguments("")]
    [Arguments("AAE=")]
    public async Task ExternalReferenceReplacementReportsRemovalOfTheStoredDocument(string base64)
    {
        var engine = CreateDocumentReferenceEngine();
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f/versions/v1$details",
            $$"""{"filebase64":"{{base64}}"}""");
        var result = await Send(engine, RegistryAction.Patch, "/teams/g/files/f/versions/v1$details",
            """{"fileurl":"https://documents.example/replacement"}""");
        var redirected = await Send(engine, RegistryAction.Read, "/teams/g/files/f");
        await Assert.That(redirected.Kind).IsEqualTo(RegistryResultKind.SeeOther);
        await Assert.That(redirected.Location!.AbsoluteUri).IsEqualTo("https://documents.example/replacement");
        await Assert.That(redirected.Document).IsNull();
        var batch = (await engine.ReadEventBatchesAsync(Writer())).Single(batch => batch.CorrelationId == result.CorrelationId);
        var events = batch.Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Select(static item => item.GetProperty("type").GetString()!).ToArray())
            .IsEquivalentTo(["io.xregistry.resource.updated", "io.xregistry.version.updated"], StringComparer.Ordinal);
        foreach (var item in events)
        {
            var names = item.GetProperty("data").GetProperty("changed").EnumerateArray().Select(static value => value.GetString()).ToArray();
            await Assert.That(names.Contains("file", StringComparer.Ordinal)).IsTrue();
            await Assert.That(names.Contains("fileurl", StringComparer.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task CreationAndDeprecationRemainSeparateWhileLifecycleUpdatesCoalesce()
    {
        var engine = Create();
        var result = await Send(engine, RegistryAction.Replace, "/teams/g", """
            {"deprecated":{"effective":"2025-01-01T00:00:00Z"},"notes":{"n":{}}}
            """);
        var batch = (await engine.ReadEventBatchesAsync(Writer())).Single();
        var group = batch.Events.RootElement.EnumerateArray().Where(static item => item.GetProperty("subject").GetString() == "/teams/g").ToArray();
        await Assert.That(group.Select(static item => item.GetProperty("type").GetString()!).ToArray())
            .IsEquivalentTo(["io.xregistry.group.created", "io.xregistry.group.deprecated"], StringComparer.Ordinal);
        var createdGroup = group.Single(static item => item.GetProperty("type").GetString() == "io.xregistry.group.created");
        await Assert.That(createdGroup.GetProperty("data").TryGetProperty("changed", out _)).IsFalse();
        await AssertEventEpochMatchesRead(engine, createdGroup, "/teams/g");
        var deprecatedEvent = group.Single(static item => item.GetProperty("type").GetString() == "io.xregistry.group.deprecated");
        await Assert.That(HasChangedData(deprecatedEvent)).IsFalse();
        await Assert.That(group.All(item => item.GetProperty("xregcorrelationid").GetString() == result.CorrelationId)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CrossReferenceResourceEventsUseTheFinalVisibleEpochs(bool secondHop)
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/target", """{"name":"target"}""");
        var target = "/teams/g/notes/target";
        if (secondHop)
        {
            await Send(engine, RegistryAction.Replace, "/teams/g/notes/first-alias",
                "{\"meta\":{\"xref\":\"" + target + "\"}}");
            target = "/teams/g/notes/first-alias";
        }

        const string alias = "/teams/g/notes/alias";
        var result = await Send(engine, RegistryAction.Replace, alias,
            "{\"meta\":{\"xref\":\"" + target + "\"}}");
        var resourceEvent = (await EventsFor(engine, result))
            .Single(item => item.GetProperty("subject").GetString() == alias);

        await Assert.That(resourceEvent.GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.created");
        await AssertEventEpochMatchesRead(engine, resourceEvent, alias, alias + "/meta");
    }

    [Test]
    [Arguments("registry")]
    [Arguments("group")]
    [Arguments("resource")]
    [Arguments("version")]
    public async Task UpdatedLifecycleEventsCarryFinalEpochsAndResourceMetaEpoch(string target)
    {
        var engine = target == "version" ? await CreateVersionedNotes() : Create();

        // Creating a nested entity also updates the Registry in the same interaction.
        var created = target == "version" ? null :
            await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        JsonElement lifecycleEvent;
        string eventType;
        string readPath;
        string? metaPath = null;
        if (target == "registry")
        {
            lifecycleEvent = (await EventsFor(engine, created!))
                .Single(item => item.GetProperty("subject").GetString() == "/");
            eventType = "io.xregistry.registry.updated";
            readPath = "/";
        }
        else
        {
            var subject = target switch
            {
                "group" => "/teams/g",
                "resource" => "/teams/g/notes/n",
                "version" => "/teams/g/notes/n/versions/v1",
                _ => throw new ArgumentOutOfRangeException(nameof(target))
            };
            var (path, body, entity) = target switch
            {
                "group" => (subject, """{"name":"group-updated"}""", "group"),
                "resource" => (subject + "/meta", """{"labels":{"site":"west"}}""", "resource"),
                "version" => (subject, """{"name":"version-updated"}""", "version"),
                _ => throw new ArgumentOutOfRangeException(nameof(target))
            };
            var update = await Send(engine, RegistryAction.Patch, path, body);
            lifecycleEvent = (await EventsFor(engine, update))
                .Single(item => item.GetProperty("subject").GetString() == subject);
            eventType = "io.xregistry." + entity + ".updated";
            readPath = subject;
            if (target == "resource") { metaPath = subject + "/meta"; }
        }

        await Assert.That(lifecycleEvent.GetProperty("type").GetString()).IsEqualTo(eventType);
        await AssertEventEpochMatchesRead(engine, lifecycleEvent, readPath, metaPath);
    }

    [Test]
    public async Task RepeatedResourceUpdatesCoalesceChangedNamesAndKeepFinalEpochs()
    {
        var engine = Create("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,
              "attributes":{"oldonly":{"type":"boolean"},"newonly":{"type":"string"}}}}}}}
            """);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1","oldonly":false}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"v2","newonly":""}""");

        // This one operation records the ordinary Resource update and the default-Version projection update.
        var result = await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta",
            """{"defaultversionid":"v1","labels":{"site":"west"}}""");
        var events = await EventsFor(engine, result);
        await Assert.That(events.Length).IsEqualTo(1);
        var resourceEvent = events.Single();
        await Assert.That(resourceEvent.GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.updated");
        await Assert.That(resourceEvent.GetProperty("subject").GetString()).IsEqualTo("/teams/g/notes/n");
        var mergedNames = ChangedNames(resourceEvent);
        await Assert.That(mergedNames).IsEquivalentTo([
            "ancestorid", "createdat", "epoch", "isdefault", "meta.defaultversionid",
            "meta.defaultversionsticky", "meta.defaultversionurl", "meta.epoch", "meta.labels",
            "meta.modifiedat", "modifiedat", "newonly", "noteid", "oldonly", "self", "versionid", "xid"
        ], StringComparer.Ordinal);
        await AssertEventEpochMatchesRead(engine, resourceEvent, "/teams/g/notes/n", "/teams/g/notes/n/meta");
    }

    [Test]
    public async Task ModelUpdatesProduceDistinctPreparedAdministrativeEvents()
    {
        var engine = Create();
        var result = await Send(engine, RegistryAction.Replace, "/modelsource", Model);
        var events = (await engine.ReadEventBatchesAsync(Writer())).Single().Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Select(static item => item.GetProperty("type").GetString()!).ToArray())
            .IsEquivalentTo(["io.xregistry.registry.updated", "io.xregistry.model.updated", "io.xregistry.modelsource.updated"], StringComparer.Ordinal);
        await Assert.That(events.Single(static item => item.GetProperty("subject").GetString() == "/model")
            .TryGetProperty("data", out _)).IsFalse();
        await Assert.That(events.Single(static item => item.GetProperty("subject").GetString() == "/modelsource")
            .TryGetProperty("data", out _)).IsFalse();
        await Assert.That(ChangedNames(events.Single(static item => item.GetProperty("subject").GetString() == "/")))
            .IsEquivalentTo(["epoch", "modifiedat", "model", "modelsource"], StringComparer.Ordinal);
        await Assert.That(events.All(item => item.GetProperty("xregcorrelationid").GetString() == result.CorrelationId)).IsTrue();
    }

    [Test]
    public async Task RegistryAttributeUpdatesReportChangedNamesAndFinalEpoch()
    {
        var engine = Create();
        var result = await Send(engine, RegistryAction.Patch, "/", """{"name":"registry"}""");
        var registryEvent = (await EventsFor(engine, result)).Single();

        await Assert.That(registryEvent.GetProperty("type").GetString()).IsEqualTo("io.xregistry.registry.updated");
        await Assert.That(ChangedNames(registryEvent))
            .IsEquivalentTo(["epoch", "modifiedat", "name"], StringComparer.Ordinal);
        await AssertEventEpochMatchesRead(engine, registryEvent, "/");
    }

    [Test]
    public async Task CapabilityUpdatesEmitBothCapabilityAndRegistryEvents()
    {
        var engine = MutableEngine();
        var result = await Send(engine, RegistryAction.Patch, "/", """{"capabilities":{"pagination":true}}""");
        var events = await EventsFor(engine, result);
        var registryEvent = events.Single(static item => item.GetProperty("subject").GetString() == "/");
        var capabilitiesEvent = events.Single(static item =>
            item.GetProperty("subject").GetString() == "/capabilities");

        await Assert.That(events.Select(static item => item.GetProperty("type").GetString()!).ToArray())
            .IsEquivalentTo([
                "io.xregistry.capabilities.updated",
                "io.xregistry.registry.updated"
            ], StringComparer.Ordinal);
        await Assert.That(ChangedNames(registryEvent))
            .IsEquivalentTo(["capabilities", "epoch", "modifiedat"], StringComparer.Ordinal);
        await Assert.That(ChangedNames(capabilitiesEvent)).IsEquivalentTo(["flags", "pagination"], StringComparer.Ordinal);
        await AssertEventEpochMatchesRead(engine, registryEvent, "/");
    }

    [Test]
    public async Task OutboxEventsAreAtomicDeduplicatedAndCorrelated()
    {
        var engine = Create();
        var result = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"created"}""");
        var batches = await engine.ReadEventBatchesAsync(Writer());
        await Assert.That(batches.Count).IsEqualTo(1);
        await Assert.That(batches[0].CorrelationId).IsEqualTo(result.CorrelationId);
        var events = batches[0].Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Select(static item => item.GetProperty("type").GetString()!).ToArray())
            .IsEquivalentTo(["io.xregistry.registry.updated", "io.xregistry.group.created",
                "io.xregistry.resource.created", "io.xregistry.version.created"], StringComparer.Ordinal);
        await Assert.That(events.Select(static item => item.GetProperty("subject").GetString()).Distinct().Count()).IsEqualTo(4);
        await Assert.That(events.All(item => item.GetProperty("xregcorrelationid").GetString() == result.CorrelationId)).IsTrue();
        await Assert.That(events.Select(static item => item.GetProperty("time").GetString()).Distinct().Count()).IsEqualTo(1);
        var registryEvent = events.Single(static item => item.GetProperty("subject").GetString() == "/");
        var groupEvent = events.Single(static item => item.GetProperty("subject").GetString() == "/teams/g");
        var resourceEvent = events.Single(static item => item.GetProperty("subject").GetString() == "/teams/g/notes/n");
        var versionEvent = events.Single(static item =>
            item.GetProperty("type").GetString() == "io.xregistry.version.created");
        await AssertEventEpochMatchesRead(engine, registryEvent, "/");
        await AssertEventEpochMatchesRead(engine, groupEvent, "/teams/g");
        await AssertEventEpochMatchesRead(engine, resourceEvent, "/teams/g/notes/n", "/teams/g/notes/n/meta");
        await AssertEventEpochMatchesRead(engine, versionEvent, versionEvent.GetProperty("subject").GetString()!);
        await Assert.That(resourceEvent.GetProperty("data").TryGetProperty("changed", out _)).IsFalse();
        await Assert.That(versionEvent.GetProperty("data").TryGetProperty("changed", out _)).IsFalse();
        await Assert.That(ChangedNames(events.Single(static item => item.GetProperty("subject").GetString() == "/")))
            .IsEquivalentTo(["epoch", "modifiedat", "teams", "teamscount"], StringComparer.Ordinal);
        await Assert.That(async () => await Send(engine, RegistryAction.Patch, "/", """{"teams":{"bad":{"unknown":1}}}"""))
            .Throws<RegistryException>();
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        await engine.AcknowledgeEventBatchAsync(result.CorrelationId!, Writer());
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    private static RegistryEngine CreateDocumentReferenceEngine() => new(new()
    {
        RegistryId = "event-documents",
        PublicRoot = new Uri("https://registry.example"),
        Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
        DocumentReferencePolicy = new ValidationIntegrationTests.ReferencePolicy()
    }, new InMemoryRegistryPersistence(), new PermitPolicy());

    private static async Task<RegistryEngine> CreateVersionedNotes()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/v1", """{"name":"first"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/v2", """{"name":"second"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta",
            """{"defaultversionid":"v1","defaultversionsticky":true}""");
        return engine;
    }

    private static async Task<JsonElement[]> EventsFor(RegistryEngine engine, RegistryResult result) =>
        (await engine.ReadEventBatchesAsync(Writer())).Single(batch => batch.CorrelationId == result.CorrelationId)
            .Events.RootElement.EnumerateArray().ToArray();

    private static string[] ChangedNames(JsonElement item) => item.GetProperty("data").GetProperty("changed")
        .EnumerateArray().Select(static value => value.GetString()!).ToArray();

    private static bool HasChangedData(JsonElement cloudEvent) =>
        cloudEvent.TryGetProperty("data", out var data) && data.TryGetProperty("changed", out _);

    private static async Task AssertEventEpochMatchesRead(RegistryEngine engine, JsonElement item, string path,
        string? metaPath = null)
    {
        var expectedEpoch = (await Send(engine, RegistryAction.Read, path)).Metadata!.RootElement
            .GetProperty("epoch").GetUInt64();
        var data = item.GetProperty("data");
        await Assert.That(data.GetProperty("epoch").GetUInt64()).IsEqualTo(expectedEpoch);
        if (metaPath is not null)
        {
            var expectedMetaEpoch = (await Send(engine, RegistryAction.Read, metaPath)).Metadata!.RootElement
                .GetProperty("epoch").GetUInt64();
            await Assert.That(data.GetProperty("meta.epoch").GetUInt64()).IsEqualTo(expectedMetaEpoch);
        }
    }

    private sealed class EventClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
