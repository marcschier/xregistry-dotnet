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
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["epoch", "name"]);
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["modifiedat", "name"]);
        events.Record(RegistryEventEntity.Resource, RegistryEventAction.Updated, "/dirs/d1/files/f1", ["name"]);
        events.Record(RegistryEventEntity.Resource, RegistryEventAction.Created, "/dirs/d1/files/f1");
        events.Record(RegistryEventEntity.Resource, RegistryEventAction.Deleted, "/dirs/d1/files/f1");
        var batch = events.Seal();

        await Assert.That(batch.Count).IsEqualTo(2);
        var group = batch.Single(item => item.Subject == "/dirs/d1").ToJson().RootElement;
        await Assert.That(group.GetProperty("type").GetString()).IsEqualTo("io.xregistry.group.updated");
        await Assert.That(group.GetProperty("time").GetString()).IsEqualTo("2025-09-01T12:01:02Z");
        await Assert.That(group.GetProperty("source").GetString()).IsEqualTo("https://example.com/registry");
        await Assert.That(group.GetProperty("xregcorrelationid").GetString()).IsEqualTo("interaction-001");
        await Assert.That(string.Join(',', group.GetProperty("data").GetProperty("changed").EnumerateArray()
            .Select(item => item.GetString()))).IsEqualTo("epoch,modifiedat,name");
        var resource = batch.Single(item => item.Subject == "/dirs/d1/files/f1").ToJson().RootElement;
        await Assert.That(resource.GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.deleted");
        await Assert.That(resource.TryGetProperty("data", out _)).IsFalse();
        await Assert.That(events.Seal()).IsEqualTo(batch);
    }

    [Test]
    public async Task CreatedOutranksLaterUpdatesWithoutSuppressingTheSeparateDeprecationEvent()
    {
        var events = Create();
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Created, "/dirs/d1");
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["name", "deprecated"]);
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Deprecation, "/dirs/d1", ["reason"]);
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Deprecation, "/dirs/d1", ["effectiveat", "reason"]);
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(2);
        await Assert.That(batch.Count(item => item.Action == RegistryEventAction.Created)).IsEqualTo(1);
        await Assert.That(batch.Single(item => item.Action == RegistryEventAction.Created).Changed).IsNull();
        await Assert.That(string.Join(',', batch.Single(item => item.Action == RegistryEventAction.Deprecation).Changed!))
            .IsEqualTo("effectiveat,reason");
        await Assert.That(batch.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(2);
        await Assert.That(batch.All(item => item.Time == events.Time && item.CorrelationId == events.CorrelationId)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task UnknownChangedNamesNeverBecomeAnIncompleteClaimedList(bool unknownFirst)
    {
        var events = Create();
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", unknownFirst ? null : ["name"]);
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", unknownFirst ? ["epoch"] : null);
        var change = events.Seal().Single();
        await Assert.That(change.Changed).IsNull();
        await Assert.That(change.ToJson().RootElement.TryGetProperty("data", out _)).IsFalse();
    }

    [Test]
    public async Task DefaultPointerChangeReportsAllNonNullOldAndNewNamesWithoutVersionUpdates()
    {
        var oldVersion = RegistryJson.Parse("""{"versionid":"v1","epoch":7,"name":"old","oldonly":false,"nil":null}""");
        var newVersion = RegistryJson.Parse("""{"versionid":"v2","epoch":1,"name":"new","newonly":"","nil":null}""");
        var events = Create();
        events.RecordDefaultVersionChange("/dirs/d1/files/f1", oldVersion.RootElement, newVersion.RootElement);
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(batch[0].Type).IsEqualTo("io.xregistry.resource.updated");
        await Assert.That(string.Join(',', batch[0].Changed!))
            .IsEqualTo("epoch,meta.defaultversionid,meta.epoch,meta.modifiedat,name,newonly,oldonly,versionid");
    }

    [Test]
    public async Task AdministrativeEventsRetainDistinctSubjectsAndSuppressOnlyForbiddenChangedLists()
    {
        var events = Create();
        events.Record(RegistryEventEntity.Registry, RegistryEventAction.Updated, "/", ["model", "modelsource", "capabilities"]);
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
    [Arguments(RegistryEventEntity.Model, RegistryEventAction.Deprecation, "/model")]
    [Arguments(RegistryEventEntity.ModelSource, RegistryEventAction.Created, "/modelsource")]
    [Arguments(RegistryEventEntity.Capabilities, RegistryEventAction.Deleted, "/capabilities")]
    [Arguments(RegistryEventEntity.Version, RegistryEventAction.Deprecation, "/dirs/d1/files/f1/versions/v1")]
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
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/%64%31", ["epoch"]);
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["name"]);
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/D1", ["name"]);
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(2);
        await Assert.That(string.Join(',', batch.Single(item => item.Subject == "/dirs/d1").Changed!)).IsEqualTo("epoch,name");
        await Assert.That(batch.Single(item => item.Subject == "/dirs/D1").Subject).IsEqualTo("/dirs/D1");
    }

    [Test]
    public async Task FailedLimitChargesDoNotPartiallyChangePreviouslyPreparedEvents()
    {
        var events = Create(new() { MaxEvents = 1, MaxChangedAttributes = 2, MaxTotalChangedNameBytes = 2 });
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["a"]);
        await Assert.That(() => events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["bc"]))
            .Throws<RegistryException>();
        await Assert.That(() => events.Record(RegistryEventEntity.Group, RegistryEventAction.Created, "/dirs/d2"))
            .Throws<RegistryException>();
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["b"]);
        var batch = events.Seal();
        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(string.Join(',', batch[0].Changed!)).IsEqualTo("a,b");
    }

    [Test]
    public async Task DuplicateInputNamesAndRepeatedObservationsConsumeFiniteWork()
    {
        var names = Create(new() { MaxChangedAttributes = 2 });
        await Assert.That(() => names.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["a", "a", "a"]))
            .Throws<RegistryException>();
        await Assert.That(names.Seal().Count).IsEqualTo(0);
        var observations = Create(new() { MaxObservations = 1 });
        observations.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["name"]);
        await Assert.That(() => observations.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["epoch"]))
            .Throws<RegistryException>();
        await Assert.That(string.Join(',', observations.Seal().Single().Changed!)).IsEqualTo("name");
    }

    [Test]
    public async Task Utf8ChangedNameLimitAcceptsItsBoundaryAndRejectsTheNextByte()
    {
        var events = Create(new() { MaxChangedNameBytes = 2 });
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["\u00e9"]);
        await Assert.That(() => events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["\u00e9x"]))
            .Throws<ArgumentException>();
        await Assert.That(events.Seal().Single().Changed!.Single()).IsEqualTo("\u00e9");
    }

    [Test]
    public async Task SealedBatchIsImmutableAndEmptyBatchIsValid()
    {
        var empty = Create();
        await Assert.That(empty.Seal().Count).IsEqualTo(0);
        await Assert.That(() => empty.Record(RegistryEventEntity.Group, RegistryEventAction.Created, "/dirs/d1"))
            .Throws<InvalidOperationException>();
        var events = Create();
        var names = new List<string> { "name" };
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", names);
        names[0] = "changed-after-recording";
        await Assert.That(events.Seal().Single().Changed!.Single()).IsEqualTo("name");
    }

    [Test]
    public async Task StructuredCloudEventShapeAndEncodedCorrelationMatchTheCoreExample()
    {
        var events = new RegistryInteractionEvents(new Uri("https://example.com"), "B9282-129301",
            new DateTimeOffset(2025, 9, 1, 12, 1, 2, TimeSpan.Zero));
        events.Record(RegistryEventEntity.Group, RegistryEventAction.Updated, "/dirs/d1", ["epoch", "modifiedat", "name"]);
        var json = events.Seal().Single().ToJson().RootElement;
        await Assert.That(json.GetProperty("specversion").GetString()).IsEqualTo("1.0");
        await Assert.That(json.GetProperty("subject").GetString()).IsEqualTo("/dirs/d1");
        await Assert.That(json.GetProperty("source").GetString()).IsEqualTo("https://example.com");
        await Assert.That(json.GetProperty("id").GetString()).IsEqualTo("B9282-129301:0");
        await Assert.That(json.GetProperty("time").GetString()).IsEqualTo("2025-09-01T12:01:02Z");
        await Assert.That(json.GetProperty("data").GetProperty("changed").ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(events.CorrelationHeaderValue).IsEqualTo("B9282-129301");
    }

    private static RegistryInteractionEvents Create(RegistryEventLimits? limits = null) =>
        new(new Uri("https://example.com/"), "interaction", DateTimeOffset.UnixEpoch, limits);
}
