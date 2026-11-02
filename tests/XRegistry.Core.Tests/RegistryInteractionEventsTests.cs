// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryInteractionEventsTests
{
    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("interaction\n")]
    public async Task EmptyOrControlContainingCorrelationIdsAreRejected(string correlationId)
    {
        await Assert.That(() => new RegistryInteractionEvents(new Uri("https://registry.example"),
            correlationId, DateTimeOffset.UnixEpoch)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("relative")]
    [Arguments("https://user:password@registry.example")]
    [Arguments("https://registry.example?query=1")]
    [Arguments("https://registry.example#fragment")]
    public async Task EventSourceMustBeAnUndecoratedAbsoluteRegistryRoot(string source)
    {
        await Assert.That(() => new RegistryInteractionEvents(new Uri(source, UriKind.RelativeOrAbsolute),
            "interaction", DateTimeOffset.UnixEpoch)).Throws<ArgumentException>();
    }

    [Test]
    public async Task CorrelationUtf8ByteLimitAcceptsItsBoundaryAndRejectsTheNextByte()
    {
        var correlation = new string('\u00e9', 512);
        var events = new RegistryInteractionEvents(new Uri("https://registry.example"),
            correlation, DateTimeOffset.UnixEpoch);
        await Assert.That(events.CorrelationId).IsEqualTo(correlation);
        await Assert.That(events.CorrelationHeaderValue).IsEqualTo(string.Concat(Enumerable.Repeat("%C3%A9", 512)));
        await Assert.That(() => new RegistryInteractionEvents(new Uri("https://registry.example"),
            correlation + "x", DateTimeOffset.UnixEpoch)).Throws<ArgumentException>();
    }

    [Test]
    public async Task OneInteractionMergesChangedNamesAndUsesDeletedCreatedUpdatedPrecedence()
    {
        var events = new RegistryInteractionEvents(new Uri("https://example.com/registry/"),
            "interaction-001", new DateTimeOffset(2025, 9, 1, 14, 1, 2, TimeSpan.FromHours(2)));
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["epoch", "name"], 2);
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["modifiedat", "name"], 3);
        RecordResource(events, RegistryEventAction.Updated, "/dirs/d1/files/f1", ["name"], 4, 5);
        RecordResource(events, RegistryEventAction.Created, "/dirs/d1/files/f1");
        RecordResource(events, RegistryEventAction.Deleted, "/dirs/d1/files/f1");
        var batch = events.Seal();

        await Assert.That(batch.Count).IsEqualTo(2);
        var group = batch.Single(item => item.Subject == "/dirs/d1").ToJson().RootElement;
        await Assert.That(group.GetProperty("type").GetString()).IsEqualTo("io.xregistry.group.updated");
        await Assert.That(group.GetProperty("time").GetString()).IsEqualTo("2025-09-01T12:01:02Z");
        await Assert.That(group.GetProperty("source").GetString()).IsEqualTo("https://example.com/registry");
        await Assert.That(group.GetProperty("xregcorrelationid").GetString()).IsEqualTo("interaction-001");
        await Assert.That(group.GetProperty("data").GetProperty("epoch").GetInt32()).IsEqualTo(3);
        await Assert.That(string.Join(',', group.GetProperty("data").GetProperty("changed").EnumerateArray()
            .Select(item => item.GetString()))).IsEqualTo("epoch,modifiedat,name");
        var resource = batch.Single(item => item.Subject == "/dirs/d1/files/f1").ToJson().RootElement;
        await Assert.That(resource.GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.deleted");
        await Assert.That(resource.TryGetProperty("data", out _)).IsFalse();
        await Assert.That(events.Seal()).IsEqualTo(batch);
    }

    [Test]
    public async Task CreatedOutranksLaterUpdatesAndDeprecationUsesItsOwnChangedFreeEvent()
    {
        var events = Create();
        RecordGroup(events, RegistryEventAction.Created, "/dirs/d1", epoch: 7);
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["name", "deprecated"], 8);
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Deprecated, "/dirs/d1");
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Deprecated, "/dirs/d1");
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(2);
        await Assert.That(batch.Count(item => item.Action == RegistryEventAction.Created)).IsEqualTo(1);
        var created = batch.Single(item => item.Action == RegistryEventAction.Created);
        await Assert.That(created.Type).IsEqualTo("io.xregistry.group.created");
        await Assert.That(created.Changed).IsNull();
        await Assert.That(HasChangedData(created.ToJson().RootElement)).IsFalse();
        await Assert.That(created.ToJson().RootElement.GetProperty("data").GetProperty("epoch").GetInt32()).IsEqualTo(8);
        var deprecated = batch.Single(item => item.Action == RegistryEventAction.Deprecated);
        await Assert.That(deprecated.Type).IsEqualTo("io.xregistry.group.deprecated");
        await Assert.That(deprecated.Changed).IsNull();
        await Assert.That(HasChangedData(deprecated.ToJson().RootElement)).IsFalse();
        await Assert.That(batch.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(2);
        await Assert.That(batch.All(item => item.Time == events.Time && item.CorrelationId == events.CorrelationId)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task UnknownChangedNamesNeverBecomeAnIncompleteClaimedList(bool unknownFirst)
    {
        var events = Create();
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", unknownFirst ? null : ["name"]);
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", unknownFirst ? ["epoch"] : null);
        var change = events.Seal().Single();
        await Assert.That(change.Changed).IsNull();
        var data = change.ToJson().RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("epoch").GetInt32()).IsEqualTo(1);
        await Assert.That(data.TryGetProperty("changed", out _)).IsFalse();
    }

    [Test]
    public async Task DefaultPointerChangeReportsAllNonNullOldAndNewNamesWithoutVersionUpdates()
    {
        var oldVersion = RegistryJson.Parse("""{"versionid":"v1","epoch":7,"name":"old","oldonly":false,"nil":null}""");
        var newVersion = RegistryJson.Parse("""{"versionid":"v2","epoch":1,"name":"new","newonly":"","nil":null}""");
        var events = Create();
        events.RecordDefaultVersionChange("/dirs/d1/files/f1", oldVersion.RootElement, newVersion.RootElement, 8, 3);
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(batch[0].Type).IsEqualTo("io.xregistry.resource.updated");
        await Assert.That(string.Join(',', batch[0].Changed!))
            .IsEqualTo("epoch,meta.defaultversionid,meta.epoch,meta.modifiedat,name,newonly,oldonly,versionid");
        var data = batch[0].ToJson().RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("epoch").GetInt32()).IsEqualTo(8);
        await Assert.That(data.GetProperty("meta.epoch").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task AdministrativeEventsRetainDistinctSubjectsAndSuppressOnlyForbiddenChangedLists()
    {
        var events = Create();
        events.Record(RegistryEventEntity.Registry, RegistryEventAction.Updated, "/",
            ["model", "modelsource", "capabilities"], 1);
        events.Record(RegistryEventEntity.Model, RegistryEventAction.Updated, "/model");
        events.Record(RegistryEventEntity.ModelSource, RegistryEventAction.Updated, "/modelsource");
        events.Record(RegistryEventEntity.Capabilities, RegistryEventAction.Updated, "/capabilities", ["flags"]);
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(4);
        await Assert.That(batch.Single(item => item.Subject == "/model").Type).IsEqualTo("io.xregistry.model.updated");
        await Assert.That(batch.Single(item => item.Subject == "/model").Changed).IsNull();
        await Assert.That(batch.Single(item => item.Subject == "/modelsource").Type).IsEqualTo("io.xregistry.modelsource.updated");
        await Assert.That(batch.Single(item => item.Subject == "/modelsource").Changed).IsNull();
        await Assert.That(string.Join(',', batch.Single(item => item.Subject == "/capabilities").Changed!)).IsEqualTo("flags");
    }

    [Test]
    [Arguments(RegistryEventEntity.Registry, RegistryEventAction.Created, "/")]
    [Arguments(RegistryEventEntity.Registry, RegistryEventAction.Deleted, "/")]
    [Arguments(RegistryEventEntity.Model, RegistryEventAction.Deprecated, "/model")]
    [Arguments(RegistryEventEntity.ModelSource, RegistryEventAction.Created, "/modelsource")]
    [Arguments(RegistryEventEntity.Capabilities, RegistryEventAction.Deleted, "/capabilities")]
    [Arguments(RegistryEventEntity.Version, RegistryEventAction.Deprecated, "/dirs/d1/files/f1/versions/v1")]
    [Arguments(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs")]
    [Arguments(RegistryEventEntity.Resource, RegistryEventAction.Updated, "/dirs/d1/files/f1/meta")]
    [Arguments(RegistryEventEntity.Resource, RegistryEventAction.Updated, "/dirs/d1/files/f1$details")]
    public async Task UndefinedActionAndSubjectCombinationsNeverEnterTheBatch(
        RegistryEventEntity entity, RegistryEventAction action, string subject)
    {
        var events = Create();
        await Assert.That(() => events.Record(entity, action, subject)).Throws<ArgumentException>();
        await Assert.That(events.Seal().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(RegistryEventEntity.Group, RegistryEventAction.Created, "/dirs/d1")]
    [Arguments(RegistryEventEntity.Group, RegistryEventAction.Deleted, "/dirs/d1")]
    [Arguments(RegistryEventEntity.Model, RegistryEventAction.Updated, "/model")]
    [Arguments(RegistryEventEntity.ModelSource, RegistryEventAction.Updated, "/modelsource")]
    public async Task ForbiddenChangedFieldsAreRejectedEvenWhenTheSuppliedListIsEmpty(
        RegistryEventEntity entity, RegistryEventAction action, string subject)
    {
        var events = Create();
        await Assert.That(() => events.Record(entity, action, subject, [])).Throws<ArgumentException>();
        await Assert.That(events.Seal().Count).IsEqualTo(0);
    }

    [Test]
    public async Task CanonicalSubjectIdentityCoalescesEscapesWithoutFoldingCaseSensitiveIds()
    {
        var events = Create();
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/%64%31", ["epoch"]);
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["name"]);
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/D1", ["name"]);
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(2);
        await Assert.That(string.Join(',', batch.Single(item => item.Subject == "/dirs/d1").Changed!)).IsEqualTo("epoch,name");
        await Assert.That(batch.Single(item => item.Subject == "/dirs/D1").Subject).IsEqualTo("/dirs/D1");
    }

    [Test]
    public async Task FailedLimitChargesDoNotPartiallyChangePreviouslyPreparedEvents()
    {
        var events = Create(new() { MaxEvents = 1, MaxChangedAttributes = 2, MaxTotalChangedNameBytes = 2 });
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["a"]);
        await Assert.That(() => RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["bc"]))
            .Throws<RegistryException>();
        await Assert.That(() => RecordGroup(events, RegistryEventAction.Created, "/dirs/d2"))
            .Throws<RegistryException>();
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["b"]);
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(string.Join(',', batch[0].Changed!)).IsEqualTo("a,b");
    }

    [Test]
    public async Task DuplicateInputNamesAndRepeatedObservationsConsumeFiniteWork()
    {
        var names = Create(new() { MaxChangedAttributes = 2 });
        await Assert.That(() => RecordGroup(names, RegistryEventAction.Updated, "/dirs/d1", ["a", "a", "a"]))
            .Throws<RegistryException>();
        await Assert.That(names.Seal().Count).IsEqualTo(0);
        var observations = Create(new() { MaxObservations = 1 });
        RecordGroup(observations, RegistryEventAction.Updated, "/dirs/d1", ["name"]);
        await Assert.That(() => RecordGroup(observations, RegistryEventAction.Updated, "/dirs/d1", ["epoch"]))
            .Throws<RegistryException>();
        await Assert.That(string.Join(',', observations.Seal().Single().Changed!)).IsEqualTo("name");
    }

    [Test]
    public async Task Utf8ChangedNameLimitAcceptsItsBoundaryAndRejectsTheNextByte()
    {
        var events = Create(new() { MaxChangedNameBytes = 2 });
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["\u00e9"]);
        await Assert.That(() => RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["\u00e9x"]))
            .Throws<ArgumentException>();
        await Assert.That(events.Seal().Single().Changed!.Single()).IsEqualTo("\u00e9");
    }

    [Test]
    public async Task SealedBatchIsImmutableAndEmptyBatchIsValid()
    {
        var empty = Create();
        await Assert.That(empty.Seal().Count).IsEqualTo(0);
        await Assert.That(() => RecordGroup(empty, RegistryEventAction.Created, "/dirs/d1"))
            .Throws<InvalidOperationException>();
        var events = Create();
        var names = new List<string> { "name" };
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", names);
        names[0] = "changed-after-recording";
        await Assert.That(events.Seal().Single().Changed!.Single()).IsEqualTo("name");
    }

    [Test]
    public async Task StructuredCloudEventShapeAndEncodedCorrelationMatchTheCoreExample()
    {
        var events = new RegistryInteractionEvents(new Uri("https://example.com"), "B9282-129301",
            new DateTimeOffset(2025, 9, 1, 12, 1, 2, TimeSpan.Zero));
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", ["epoch", "modifiedat", "name"]);
        var json = events.Seal().Single().ToJson().RootElement;
        await Assert.That(json.GetProperty("specversion").GetString()).IsEqualTo("1.0");
        await Assert.That(json.GetProperty("subject").GetString()).IsEqualTo("/dirs/d1");
        await Assert.That(json.GetProperty("source").GetString()).IsEqualTo("https://example.com");
        await Assert.That(json.GetProperty("id").GetString()).IsEqualTo("B9282-129301:0");
        await Assert.That(json.GetProperty("time").GetString()).IsEqualTo("2025-09-01T12:01:02Z");
        await Assert.That(json.GetProperty("data").GetProperty("changed").ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(json.GetProperty("data").GetProperty("epoch").GetInt32()).IsEqualTo(1);
        await Assert.That(events.CorrelationHeaderValue).IsEqualTo("B9282-129301");
    }

    [Test]
    public async Task DeprecatedEventsRejectChangedListsAndCreatedOrUpdatedEntitiesRequireFinalEpochs()
    {
        var events = Create();
        await Assert.That(() => events.Record(RegistryEventEntity.Group, RegistryEventAction.Deprecated,
            "/dirs/d1", [])).Throws<ArgumentException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated,
            "/dirs/d1", ["name"])).Throws<ArgumentException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Resource, RegistryEventAction.Updated,
            "/dirs/d1/files/f1", epoch: 1)).Throws<ArgumentException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Resource, RegistryEventAction.Updated,
            "/dirs/d1/files/f1", metaEpoch: 1)).Throws<ArgumentException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated,
            "/dirs/d1", epoch: 1, metaEpoch: 1)).Throws<ArgumentException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Resource, RegistryEventAction.Deleted,
            "/dirs/d1/files/f1", epoch: 1, metaEpoch: 1)).Throws<ArgumentException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated,
            "/dirs/d1", epoch: -1)).Throws<ArgumentException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Resource, RegistryEventAction.Updated,
            "/dirs/d1/files/f1", epoch: 1, metaEpoch: -1)).Throws<ArgumentException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Model, RegistryEventAction.Updated,
            "/model", epoch: 1)).Throws<ArgumentException>();
        await Assert.That(events.Seal().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(RegistryEventEntity.Registry, "/")]
    [Arguments(RegistryEventEntity.Group, "/dirs/d1")]
    [Arguments(RegistryEventEntity.Resource, "/dirs/d1/files/f1")]
    [Arguments(RegistryEventEntity.Version, "/dirs/d1/files/f1/versions/v1")]
    public async Task EveryCreatedOrUpdatedLifecycleEntityRequiresItsEpoch(
        RegistryEventEntity entity, string subject)
    {
        var events = Create();
        await Assert.That(() => events.Record(entity, RegistryEventAction.Updated, subject,
            epoch: null, metaEpoch: entity == RegistryEventEntity.Resource ? BigInteger.One : null))
            .Throws<ArgumentException>();
        await Assert.That(events.Seal().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("0")]
    [Arguments("18446744073709551616")]
    public async Task EpochValuesSerializeAsExactUnsignedJsonIntegers(string rawEpoch)
    {
        var epoch = BigInteger.Parse(rawEpoch, CultureInfo.InvariantCulture);
        var events = Create();
        RecordGroup(events, RegistryEventAction.Updated, "/dirs/d1", epoch: epoch);

        var serializedEpoch = events.Seal().Single().ToJson().RootElement.GetProperty("data")
            .GetProperty("epoch").GetRawText();

        await Assert.That(serializedEpoch).IsEqualTo(rawEpoch);
    }

    [Test]
    [Arguments("0")]
    [Arguments("18446744073709551616")]
    public async Task ResourceMetaEpochSerializesAsAnExactUnsignedJsonInteger(string rawEpoch)
    {
        var metaEpoch = BigInteger.Parse(rawEpoch, CultureInfo.InvariantCulture);
        var events = Create();
        RecordResource(events, RegistryEventAction.Updated, "/dirs/d1/files/f1", epoch: 1, metaEpoch: metaEpoch);
        var data = events.Seal().Single().ToJson().RootElement.GetProperty("data");

        await Assert.That(data.GetProperty("epoch").GetRawText()).IsEqualTo("1");
        await Assert.That(data.GetProperty("meta.epoch").GetRawText()).IsEqualTo(rawEpoch);
    }

    private static void RecordGroup(RegistryInteractionEvents events, RegistryEventAction action,
        string subject, IEnumerable<string>? changed = null, BigInteger? epoch = null)
    {
        var finalEpoch = epoch ?? BigInteger.One;
        events.Record(RegistryEventEntity.Group, action, subject, changed,
            action is RegistryEventAction.Created or RegistryEventAction.Updated ? finalEpoch : null);
    }

    private static void RecordResource(RegistryInteractionEvents events, RegistryEventAction action,
        string subject, IEnumerable<string>? changed = null, BigInteger? epoch = null, BigInteger? metaEpoch = null)
    {
        var hasEpochs = action is RegistryEventAction.Created or RegistryEventAction.Updated;
        events.Record(RegistryEventEntity.Resource, action, subject, changed,
            hasEpochs ? epoch ?? BigInteger.One : null, hasEpochs ? metaEpoch ?? BigInteger.One : null);
    }

    private static RegistryInteractionEvents Create(RegistryEventLimits? limits = null) =>
        new(new Uri("https://example.com/"), "interaction", DateTimeOffset.UnixEpoch, limits);

    private static bool HasChangedData(JsonElement cloudEvent) =>
        cloudEvent.TryGetProperty("data", out var data) && data.TryGetProperty("changed", out _);
}
