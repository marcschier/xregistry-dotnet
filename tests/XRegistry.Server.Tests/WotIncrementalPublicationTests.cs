// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class WotIncrementalPublicationTests
{
    private const string Resource = "/thingmodelgroups/g/thingmodels/pump";
    private const string Document = """{ "id": "urn:example:pump", "title": "exact bytes" }""";
    private const string Line = """{"compatibilityline":"urn:example:line:a"}""";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LinelessPublicationAndExactReplayReportTheAssignedVersionWithoutChangingBytes(bool description)
    {
        var path = description ? "/thingdescriptiongroups/g/thingdescriptions/pump" : Resource;
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        var created = await Publish(engine, path, Document, "{}");
        var notice = created.Notices.Single();
        await Assert.That(notice.Code).IsEqualTo("wot-line-ineligible");
        await Assert.That(notice.Path).IsEqualTo(path + "/versions/1");
        await Assert.That(notice.Message)
            .IsEqualTo("This revision is not eligible for automatic compatibility-line selection.");
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        var replay = await Publish(engine, path + "/versions/1", Document, "{}");
        await Assert.That(replay.Notices.Single()).IsEqualTo(notice);
        var read = await Send(engine, RegistryAction.Read, path + "/versions/1");
        using var reader = new StreamReader(read.Document!.OpenRead());
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo(Document);
        await Assert.That(read.Notices.Count).IsEqualTo(0);
        var next = await Publish(engine, path, Document, Line, RegistryAction.Post);
        await Assert.That(next.Path.VersionId!.Value).IsEqualTo("2");
        await Assert.That(next.Notices.Count).IsEqualTo(0);
        var details = await Send(engine, RegistryAction.Read, path + "/versions/1$details");
        await Assert.That(details.Metadata!.RootElement.TryGetProperty("compatibilityline", out _)).IsFalse();
    }

    [Test]
    public async Task NestedLinelessPublicationsReportOnlyTheirExactVersionsInStableOrder()
    {
        var result = await Send(CreateEngine(), RegistryAction.Replace, "/", """
            {"thingmodelgroups":{"g":{"thingmodels":{
              "z":{"thingmodel":{"id":"urn:example:z"}},
              "a":{"thingmodel":{"id":"urn:example:a"}},
              "lined":{"compatibilityline":"urn:example:line:a","thingmodel":{"id":"urn:example:lined"}}
            }}}}
            """);
        await Assert.That(string.Join(",", result.Notices.Select(notice => notice.Path)))
            .IsEqualTo("/thingmodelgroups/g/thingmodels/a/versions/1,/thingmodelgroups/g/thingmodels/z/versions/1");
        await Assert.That(result.Notices.All(notice => notice.Code == "wot-line-ineligible")).IsTrue();
    }

    [Test]
    public async Task MetadataChangesAndDeletionDoNotRepeatPublicationNotices()
    {
        var engine = CreateEngine();
        await Publish(engine, Resource, Document, "{}");
        var patched = await Send(engine, RegistryAction.Patch, Resource + "/versions/1$details",
            """{"description":"updated metadata"}""");
        await Assert.That(patched.Notices.Count).IsEqualTo(0);
        var deleted = await Send(engine, RegistryAction.Delete, Resource);
        await Assert.That(deleted.Notices.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SemVerPublicationDoesNotReportAnIncrementalLineNotice()
    {
        var engine = Create(WotRegistryProfiles.Compile(WotVersionProfile.SemVer).Source.RootElement.GetRawText());
        var result = await Publish(engine, Resource, Document, "{}");
        await Assert.That(result.Notices.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ResultNoticesAreIndependentOfTheSuppliedMutableCollection()
    {
        var notice = new RegistryOperationNotice("example", "/", "Example advisory.");
        var supplied = new List<RegistryOperationNotice> { notice };
        var result = new RegistryResult(RegistryResultKind.Success, RegistryPath.Parse("/"), notices: supplied);
        supplied.Clear();
        await Assert.That(result.Notices.Single()).IsEqualTo(notice);
        await Assert.That(() => ((IList<RegistryOperationNotice>)result.Notices).Clear())
            .Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(WotVersionProfile.SemVer, "semver", true)]
    [Arguments(WotVersionProfile.Incremental, "manual", false)]
    public async Task ProfilePresetIsFixedAndDoesNotMutateThePackagedModel(
        WotVersionProfile profile, string mode, bool singleRoot)
    {
        var model = WotRegistryProfiles.Compile(profile);
        await Assert.That(WotRegistryProfiles.Read(model)).IsEqualTo(profile);
        var marker = model.Attributes["wotversionprofile"];
        await Assert.That(marker.Required).IsTrue();
        await Assert.That(marker.ReadOnly).IsTrue();
        await Assert.That(marker.Strict).IsTrue();
        await Assert.That(marker.EnumValues.Count).IsEqualTo(1);
        foreach (var resource in model.Groups.Values.SelectMany(group => group.Resources.Values))
        {
            await Assert.That(resource.VersionMode).IsEqualTo(mode);
            await Assert.That(resource.SingleVersionRoot).IsEqualTo(singleRoot);
        }
        await Assert.That(BuiltInRegistryModels.Compile(RegistryModelKind.Wot)
            .Groups["thingmodelgroups"].Resources["thingmodels"].VersionMode).IsEqualTo("semver");
        await Assert.That(BuiltInRegistryModels.Compile(RegistryModelKind.Wot)
            .Attributes.ContainsKey("wotversionprofile")).IsFalse();
    }

    [Test]
    [Arguments("thingmodelgroups", "thingmodels", "model")]
    [Arguments("thingdescriptiongroups", "thingdescriptions", "instance")]
    public async Task PublishedAuthoredAndGeneratedRevisionsShareOneAtomicHighWaterSequence(
        string group, string resources, string field)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        var path = "/" + group + "/g/" + resources + "/pump";
        var authored = """{"id":"urn:example:pump","version":{"FIELD":"41"}}"""
            .Replace("FIELD", field, StringComparison.Ordinal);
        await Publish(engine, path + "/versions/41", authored, Line);
        var generated = await Publish(engine, path, Document, Line, RegistryAction.Post);
        await Assert.That(generated.Path.VersionId!.Value).IsEqualTo("42");
        var restarted = CreateEngine(store);
        var next = await Publish(restarted, path, Document, Line, RegistryAction.Post);
        await Assert.That(next.Path.VersionId!.Value).IsEqualTo("43");
        var details = await Send(restarted, RegistryAction.Read, path + "/versions/42$details");
        await Assert.That(details.Metadata!.RootElement.GetProperty("versionorigin").GetString()).IsEqualTo("generated");
        await Assert.That(details.Metadata.RootElement.GetProperty("compatibilityline").GetString()).IsEqualTo("urn:example:line:a");
        var result = await Send(restarted, RegistryAction.Read, path + "/versions/41");
        using var reader = new StreamReader(result.Document!.OpenRead());
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo(authored);
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Find("$wotalloc" + path)!.Metadata.RootElement.GetProperty("highwater").GetString())
            .IsEqualTo("43");
        await Assert.That((await Send(restarted, RegistryAction.Read, "/")).Metadata!.RootElement
            .GetProperty("wotversionprofile").GetString()).IsEqualTo("incremental");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeletingHistoryAndRecreatingTheResourceCannotResetItsCounter(bool deleteGroup)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        await Publish(engine, Resource, """{"id":"urn:example:pump","version":{"model":"99"}}""", Line);
        await Send(engine, RegistryAction.Delete, deleteGroup ? "/thingmodelgroups/g" : Resource);
        var restarted = CreateEngine(store);
        var result = await Publish(restarted, Resource, Document, Line, RegistryAction.Post);
        await Assert.That(result.Path.VersionId!.Value).IsEqualTo("100");
        await ExpectCode(async () => await Publish(restarted, Resource + "/versions/99",
            """{"id":"urn:example:pump","version":{"model":"99"}}""", Line), "invalid-version");
    }

    [Test]
    public async Task RejectedWritesDoNotAllocateCountersOrChangeBytesOrOutbox()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        await Publish(engine, Resource, Document, Line);
        using var before = await store.ReadSnapshotAsync();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(async () => await Publish(engine, Resource, Document,
            """{"compatibilityline":"relative"}""", RegistryAction.Post), "invalid-line");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That(after.Find("$wotalloc" + Resource)!.Metadata.RootElement.GetProperty("highwater").GetString())
            .IsEqualTo("1");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        var next = await Publish(engine, Resource, Document, Line, RegistryAction.Post);
        await Assert.That(next.Path.VersionId!.Value).IsEqualTo("2");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FixedLineIncludesAbsenceAndCannotBeChangedByMetadataWrites(bool withLine)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        await Publish(engine, Resource, Document, withLine ? Line : "{}");
        using var before = await store.ReadSnapshotAsync();
        await ExpectCode(async () => await Send(engine, RegistryAction.Patch, Resource + "/versions/1$details",
            withLine ? """{"compatibilityline":null}""" : Line), "invalid-line");
        await ExpectCode(async () => await Send(engine, RegistryAction.Patch, Resource + "/versions/1$details",
            """{"compatibilityline":"urn:example:line:b"}"""), "invalid-line");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        var details = await Send(engine, RegistryAction.Read, Resource + "/versions/1$details");
        await Assert.That(details.Metadata!.RootElement.TryGetProperty("compatibilityline", out _)).IsEqualTo(withLine);
    }

    [Test]
    public async Task ExactDocumentReplayRetainsTheRevisionButCannotReplaceItsBytesOrFixedLine()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        await Publish(engine, Resource, Document, Line);
        await Publish(engine, Resource + "/versions/1", Document, Line);
        await ExpectCode(async () => await Publish(engine, Resource + "/versions/1",
            Document + " ", Line), "invalid_attribute");
        await ExpectCode(async () => await Publish(engine, Resource + "/versions/1", Document,
            """{"compatibilityline":"urn:example:line:b"}"""), "invalid-line");
        var next = await Publish(engine, Resource, Document, Line, RegistryAction.Post);
        await Assert.That(next.Path.VersionId!.Value).IsEqualTo("2");
    }

    [Test]
    public async Task ExpandedDocumentLinesAndAuthorizedMetadataMustAgreeExactly()
    {
        var engine = CreateEngine();
        const string document = """
            {"id":"urn:example:pump",
             "http://opcfoundation.org/UA/WoT-Binding/compatibilityLine":"urn:example:line:a"}
            """;
        var explicitLine = await Publish(engine, Resource, document, Line);
        await Assert.That(explicitLine.Notices.Count).IsEqualTo(0);
        await ExpectCode(async () => await Publish(engine, Resource, document,
            """{"compatibilityline":"urn:example:line:A"}""", RegistryAction.Post), "invalid-line");
        var derived = await Publish(engine, Resource, document, "{}", RegistryAction.Post);
        await Assert.That(derived.Notices.Count).IsEqualTo(0);
        var details = await Send(engine, RegistryAction.Read, derived.Path.EscapedPath + "$details");
        await Assert.That(details.Metadata!.RootElement.GetProperty("compatibilityline").GetString())
            .IsEqualTo("urn:example:line:a");
    }

    [Test]
    public async Task PublishedProfileCannotSwitchEvenAfterAllVisibleHistoryIsDeleted()
    {
        var engine = CreateEngine();
        await Publish(engine, Resource, Document, Line);
        await Send(engine, RegistryAction.Delete, "/thingmodelgroups/g");
        await ExpectCode(async () => await Send(engine, RegistryAction.Replace, "/modelsource",
            WotRegistryProfiles.Compile(WotVersionProfile.SemVer).Source.RootElement.GetRawText()),
            "model_compliance_error");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement
            .GetProperty("wotversionprofile").GetString()).IsEqualTo("incremental");
    }

    [Test]
    public async Task ResourceWideCompatibilityAndDocumentlessCreationCannotBypassTheProfile()
    {
        var engine = CreateEngine();
        await ExpectCode(async () => await Send(engine, RegistryAction.Replace, Resource + "/meta", "{}"),
            "invalid_attribute");
        await ExpectCode(async () => await Send(engine, RegistryAction.Replace, Resource + "/versions/1$details", "{}"),
            "invalid_attribute");
        await Publish(engine, Resource, Document, Line);
        await ExpectCode(async () => await Send(engine, RegistryAction.Patch, Resource + "/meta",
            """{"compatibility":"backward"}"""), "invalid_attribute");
    }

    [Test]
    public async Task UnknownMutableAndMismatchedModelProfilesFailBeforePublication()
    {
        foreach (var mutation in new[] { "unknown", "mutable", "mode", "roots" })
        {
            var source = JsonNode.Parse(WotRegistryProfiles.Compile(WotVersionProfile.Incremental)
                .Source.RootElement.GetRawText())!.AsObject();
            if (mutation == "unknown")
            {
                source["attributes"]!["wotversionprofile"]!["default"] = "future";
                source["attributes"]!["wotversionprofile"]!["enum"] = new JsonArray(JsonValue.Create("future"));
            }
            if (mutation == "mutable") { source["attributes"]!["wotversionprofile"]!["readonly"] = false; }
            if (mutation == "mode") { source["groups"]!["thingmodelgroups"]!["resources"]!["thingmodels"]!["versionmode"] = "semver"; }
            if (mutation == "roots") { source["groups"]!["thingmodelgroups"]!["resources"]!["thingmodels"]!["singleversionroot"] = true; }
            await ExpectCode(async () => await Publish(Create(source.ToJsonString()), Resource, Document, Line),
                mutation == "mode" ? "model_error" : "unknown-profile");
        }
    }

    [Test]
    public async Task FailedResponsePreparationLeavesTheAllocationAvailableForRetry()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Document));
        RegistryOperationNotice? proposed = null;
        var context = new RegistryOperationContext(Writer().Caller)
        {
            PrepareResponseAsync = (result, _) =>
            {
                proposed = result.Notices.Single();
                throw new IOException("injected response failure");
            }
        };
        await Assert.That(async () => await engine.ExecuteAsync(new(RegistryAction.Post, RegistryPath.Parse(Resource))
        {
            Metadata = RegistryJson.Parse("{}"), Document = stream, ContentType = "application/json"
        }, context)).Throws<IOException>();
        await Assert.That(proposed!.Code).IsEqualTo("wot-line-ineligible");
        await Assert.That(proposed.Path).IsEqualTo(Resource + "/versions/1");
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
        await Assert.That(snapshot.Find("$wotalloc" + Resource)).IsNull();
        var result = await Publish(engine, Resource, Document, Line, RegistryAction.Post);
        await Assert.That(result.Path.VersionId!.Value).IsEqualTo("1");
    }

    [Test]
    [Arguments("""{"thingmodel":null}""")]
    [Arguments("""{"thingmodelbase64":null}""")]
    [Arguments("""{"thingmodelurl":"https://example.test/other"}""")]
    [Arguments("""{"wotid":"urn:example:different"}""")]
    public async Task MetadataRepresentationsCannotClearOrReplaceAnExistingRevision(string metadata)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        await Publish(engine, Resource, Document, Line);
        using var before = await store.ReadSnapshotAsync();
        await ExpectCode(async () => await Send(engine, RegistryAction.Patch, Resource + "/versions/1$details",
            metadata), "invalid_attribute");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        var read = await Send(engine, RegistryAction.Read, Resource + "/versions/1");
        using var reader = new StreamReader(read.Document!.OpenRead());
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo(Document);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"highwater":null}""")]
    [Arguments("""{"highwater":12}""")]
    [Arguments("""{"highwater":"0"}""")]
    [Arguments("""{"highwater":"01"}""")]
    [Arguments("""{"highwater":"invalid"}""")]
    [Arguments(null)]
    public async Task DamagedOrMissingCountersNeverRestartPublishedHistory(string? counter)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        await Publish(engine, Resource, Document, Line);
        using var before = await store.ReadSnapshotAsync();
        using (var damage = await store.PrepareAsync(before.Generation,
            [counter is null ? RegistryMutation.Delete("$wotalloc" + Resource) :
                RegistryMutation.Put("$wotalloc" + Resource, RegistryJson.Parse(counter))]))
        {
            await damage.CommitAsync();
        }
        using var damaged = await store.ReadSnapshotAsync();
        await ExpectCode(async () => await Publish(CreateEngine(store), Resource, Document, Line,
            RegistryAction.Post), "invalid-version");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(damaged.Generation);
        await Assert.That(after.Find(Resource + "/versions/2")).IsNull();
    }

    [Test]
    public async Task ConcurrentAnonymousPublicationsCommitOnlyOneAllocationAndRetryUsesTheSuccessor()
    {
        var store = new InMemoryRegistryPersistence();
        var first = CreateEngine(store);
        var second = CreateEngine(store);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var participants = 0;
        var context = new RegistryOperationContext(Writer().Caller)
        {
            PrepareResponseAsync = async (_, ct) =>
            {
                if (Interlocked.Increment(ref participants) == 2) { ready.SetResult(); }
                await ready.Task.WaitAsync(ct);
            }
        };
        async Task<bool> Attempt(RegistryEngine engine)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Document));
            try
            {
                var result = await engine.ExecuteAsync(new(RegistryAction.Post, RegistryPath.Parse(Resource))
                {
                    Document = stream, ContentType = "application/json", Metadata = RegistryJson.Parse(Line)
                }, context);
                await Assert.That(result.Path.VersionId!.Value).IsEqualTo("1");
                return true;
            }
            catch (RegistryConcurrencyException)
            {
                return false;
            }
        }
        var outcomes = await Task.WhenAll(Attempt(first), Attempt(second)).WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(outcomes.Count(success => success)).IsEqualTo(1);
        using var committed = await store.ReadSnapshotAsync();
        await Assert.That(committed.Generation).IsEqualTo(1L);
        await Assert.That(committed.Find("$wotalloc" + Resource)!.Metadata.RootElement.GetProperty("highwater").GetString())
            .IsEqualTo("1");
        await Assert.That((await first.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        var retry = await Publish(second, Resource, Document, Line, RegistryAction.Post);
        await Assert.That(retry.Path.VersionId!.Value).IsEqualTo("2");
    }

    [Test]
    public async Task CounterBehindRetainedHistoryCannotAllocateAnUnusedButOlderRevision()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEngine(store);
        await Publish(engine, Resource, """{"id":"urn:example:pump","version":{"model":"99"}}""", Line);
        using var snapshot = await store.ReadSnapshotAsync();
        using (var damage = await store.PrepareAsync(snapshot.Generation,
            [RegistryMutation.Put("$wotalloc" + Resource, RegistryJson.Parse("""{"highwater":"1"}"""))]))
        {
            await damage.CommitAsync();
        }
        await ExpectCode(async () => await Publish(CreateEngine(store), Resource, Document, Line,
            RegistryAction.Post), "invalid-version");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(snapshot.Generation + 1);
        await Assert.That(after.Find(Resource + "/versions/2")).IsNull();
    }

    private static RegistryEngine CreateEngine(IRegistryPersistence? store = null) =>
        Create(WotRegistryProfiles.Compile(WotVersionProfile.Incremental).Source.RootElement.GetRawText(), store);

    private static async Task<RegistryResult> Publish(RegistryEngine engine, string path, string document,
        string metadata, RegistryAction action = RegistryAction.Replace)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
        return await engine.ExecuteAsync(new(action, RegistryPath.Parse(path))
        {
            Metadata = RegistryJson.Parse(metadata), Document = stream, ContentType = "application/json"
        }, Writer());
    }

    private static async Task ExpectCode(Func<Task> action, string code)
    {
        var failure = await Assert.That(action).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected " + code);
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
    }
}
