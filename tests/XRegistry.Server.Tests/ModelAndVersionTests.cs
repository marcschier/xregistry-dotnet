// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class ModelAndVersionTests
{
    [Test]
    public async Task ManualAncestryUsesAssignedRootIdentityAndExactCaseReferences()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        var root = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"ancestorid":"request"}""");
        var assigned = root.Metadata!.RootElement.GetProperty("versionid").GetString();
        await Assert.That(assigned).IsNotNullOrEmpty();
        await Assert.That(assigned).IsNotEqualTo("request");
        await Assert.That(root.Metadata.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo(assigned);
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"Root","ancestorid":"Root"}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"Child","ancestorid":"Root"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        foreach (var ancestor in new[] { "root", "missing" })
        {
            await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Child",
                "{\"ancestorid\":\"" + ancestor + "\"}"), "unknown_id");
            await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Root",
            """{"ancestorid":"Child"}"""), "ancestor_circular_reference");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Child", """{"name":"updated"}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement
            .GetProperty("ancestorid").GetString()).IsEqualTo("Root");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Child", """{"name":"replaced"}""");
        var restarted = Create(persistence: store);
        var retained = (await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement;
        await Assert.That(retained.GetProperty("ancestorid").GetString()).IsEqualTo("Root");
        await Assert.That(retained.GetProperty("name").GetString()).IsEqualTo("replaced");
        var restoredRoot = await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/versions/" + assigned);
        await Assert.That(restoredRoot.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo(assigned);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitAndDeletionRepairedAncestryUpdateEpochAndModifiedTime(bool deleteAncestor)
    {
        var clock = new AncestryClock();
        var store = new InMemoryRegistryPersistence();
        var options = new RegistryEngineOptions
        {
            RegistryId = "ancestry-time",
            PublicRoot = new Uri("https://registry.example"),
            TimeProvider = clock,
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"teams":{"singular":"team","resources":{"notes":{
                  "singular":"note","hasdocument":false,"versionmode":"manual"
                }}}}}
                """))
        };
        var engine = new RegistryEngine(options, store, new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Root", """{"ancestorid":"Root"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Child", """{"ancestorid":"Root"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement;
        clock.Now = clock.Now.AddMinutes(1);
        if (deleteAncestor)
        {
            await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/Root");
        }
        else
        {
            await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Child", """{"ancestorid":"Child"}""");
        }
        var after = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement;
        await Assert.That(after.GetProperty("ancestorid").GetString()).IsEqualTo("Child");
        await Assert.That(RegistryNumber.Parse(after.GetProperty("epoch").GetRawText()).ToBigInteger())
            .IsEqualTo(RegistryNumber.Parse(before.GetProperty("epoch").GetRawText()).ToBigInteger() + 1);
        await Assert.That(after.GetProperty("createdat").GetString()).IsEqualTo(before.GetProperty("createdat").GetString());
        await Assert.That(after.GetProperty("modifiedat").GetDateTimeOffset()).IsEqualTo(clock.Now);
        var restarted = new RegistryEngine(options, store, new PermitPolicy());
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(after.GetRawText());
        await Assert.That((await restarted.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(3);
    }

    [Test]
    public async Task ModifiedAtOrderingReachesAStableAncestryAfterIndirectTimestampTouches()
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "ordering",
            PublicRoot = new Uri("https://registry.example"),
            TimeProvider = new OrderingClock(),
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"teams":{"singular":"team","resources":{"notes":{
                  "singular":"note","hasdocument":false,"versionmode":"modifiedat"
                }}}}}
                """))
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/a", """{"modifiedat":"2000-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/b", """{"modifiedat":"2010-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/c", """{"modifiedat":"2020-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/a", """{"modifiedat":"2030-01-01T00:00:00Z"}""");
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("c");
        var versions = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
        await Assert.That(versions.GetProperty("a").GetProperty("ancestorid").GetString()).IsEqualTo("a");
        await Assert.That(versions.GetProperty("b").GetProperty("ancestorid").GetString()).IsEqualTo("a");
        await Assert.That(versions.GetProperty("c").GetProperty("ancestorid").GetString()).IsEqualTo("b");
    }

    [Test]
    public async Task TimestampOrderingTreatsEquivalentZeroFractionsAsTies()
    {
        var engine = Create("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"versionmode":"createdat"
            }}}}}
            """);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/z", """{"createdat":"2020-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/a", """{"createdat":"2020-01-01T00:00:00.0000Z"}""");
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("z");
        var newest = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/z");
        await Assert.That(newest.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("a");
    }

    [Test]
    public async Task ModelNullResetAndReadonlyCapabilitiesInputFollowRootProcessing()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Patch, "/", """{"capabilities":{"not":"mutable"},"modelsource":null}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetRawText()).IsEqualTo("{}");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/teams"), "unknown_group_type");
    }

    [Test]
    public async Task PersistedModelFreezesResolvedIncludesWithoutRefetchingOnRestart()
    {
        var resolver = new IncludeResolver();
        var options = new RegistryEngineOptions
        {
            RegistryId = "frozen",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse("""{"$include":"https://models.example/model"}"""),
                new() { Resolver = resolver }),
            ModelCompilation = new() { Resolver = resolver }
        };
        var store = new InMemoryRegistryPersistence();
        var engine = new RegistryEngine(options, store, new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g", "{}");
        resolver.Available = false;
        var restarted = new RegistryEngine(options, store, new PermitPolicy());
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g")).Metadata!.RootElement.GetProperty("teamid").GetString()).IsEqualTo("g");
        await Assert.That((await Send(restarted, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetProperty("$include").GetString())
            .IsEqualTo("https://models.example/model");
        await Assert.That(resolver.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("createdat", "late", "early")]
    [Arguments("modifiedat", "late", "early")]
    [Arguments("semver", "2.0.0", "1.0.0-rc.1")]
    public async Task VersionOrderingRebuildsAncestry(string mode, string newest, string oldest)
    {
        var model = $$"""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"versionmode":"{{mode}}","singleversionroot":true
            } } } } }
            """;
        var engine = Create(model);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/" + newest,
            """{"createdat":"2025-06-01T00:00:00Z","modifiedat":"2025-06-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/" + oldest,
            """{"createdat":"2020-01-01T00:00:00Z","modifiedat":"2020-01-01T00:00:00Z"}""");
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo(newest);
        var version = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/" + newest);
        await Assert.That(version.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo(oldest);
    }

    [Test]
    public async Task InvalidAncestryAndMultipleRootsRollBack()
    {
        var engine = Create("""{"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,"singleversionroot":true}}}}}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1"}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"v2"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/v1", """{"ancestorid":"v2"}"""), "ancestor_circular_reference");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/v2", """{"ancestorid":"v2"}"""), "multiple_roots");
        var version = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/v1");
        await Assert.That(version.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("v1");
    }

    [Test]
    public async Task ModelReplacementValidatesExistingEntitiesAtomicallyAndSurvivesEngineRestart()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"existing"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", "{}"), "model_compliance_error");
        var replacement = """{"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false},"files":{"singular":"file","maxversions":2}}},"projects":{"singular":"project"}}}""";
        await Send(engine, RegistryAction.Replace, "/modelsource", replacement);
        var restarted = Create(persistence: store);
        var projects = await Send(restarted, RegistryAction.Replace, "/projects/p", """{"name":"new type"}""");
        await Assert.That(projects.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g/notes/n")).Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("existing");
        await Assert.That((await Send(restarted, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetProperty("groups").TryGetProperty("projects", out _)).IsTrue();
    }

    [Test]
    public async Task ServerChosenVersionIdsContinueAfterDeletionAndRespectSetVersionId()
    {
        var engine = Create("""{"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,"setversionid":false}}}}}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        var second = await Send(engine, RegistryAction.Post, "/teams/g/notes/n", "{}", new KeyValuePair<string, string?>("setdefaultversionid", "request"));
        await Assert.That(second.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("2");
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/2");
        var third = await Send(engine, RegistryAction.Post, "/teams/g/notes/n", "{}");
        await Assert.That(third.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("3");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"chosen"}"""), "versionid_not_allowed");
    }

    [Test]
    public async Task ApiGroupAndVersionFormatsMatchCaseInsensitively()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", """{"format":"OpenAPI/3.1"}""");
        await Send(engine, RegistryAction.Replace, "/apigroups/g/apis/a", """{"format":"OpenAPI/3.1"}""");

        var second = await Send(engine, RegistryAction.Post, "/apigroups/g/apis/a",
            """{"versionid":"v2","format":"openapi/3.1"}""");

        await Assert.That(second.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("v2");
    }

    [Test]
    public async Task ApiVersionFormatCaseOnlyAndExactValuesAreAcceptedByOrdinaryMutation()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", """{"format":"OpenAPI/3.1"}""");
        await Send(engine, RegistryAction.Replace, "/apigroups/g/apis/a", """{"versionid":"v1","format":"OpenAPI/3.1"}""");
        await Send(engine, RegistryAction.Post, "/apigroups/g/apis/a",
            """{"versionid":"v2","format":"openapi/3.1"}""");
        await Send(engine, RegistryAction.Post, "/apigroups/g/apis/a",
            """{"versionid":"v3","format":"OpenAPI/3.1"}""");

        var versions = (await Send(engine, RegistryAction.Read, "/apigroups/g/apis/a/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(3);
        await Assert.That(versions.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(versions.GetProperty("v2").GetProperty("format").GetString()).IsEqualTo("openapi/3.1");
        await Assert.That(versions.GetProperty("v3").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
    }

    [Test]
    [Arguments("OpenAPI/3.0")]
    [Arguments("AsyncAPI/3.1")]
    public async Task ApiGroupAndVersionFormatsRejectDistinctCompleteFormatOnMutation(string differentFormat)
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", "{}");
        await Send(engine, RegistryAction.Replace, "/apigroups/g/apis/a", """{"versionid":"v1","format":"OpenAPI/3.1"}""");

        await ExpectCode(() => Send(engine, RegistryAction.Post, "/apigroups/g/apis/a",
            $$"""{"versionid":"v2","format":"{{differentFormat}}"}"""), "mismatched_version_attribute");

        var versions = (await Send(engine, RegistryAction.Read, "/apigroups/g/apis/a/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Select(static item => item.Name).ToArray())
            .IsEquivalentTo(["v1"], StringComparer.Ordinal);
        await Assert.That(versions.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(versions.TryGetProperty("v2", out _)).IsFalse();
    }

    [Test]
    public async Task ApiVersionFormatsCaseOnlyAndExactValuesAreAcceptedDuringCompleteResourceReconciliation()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", """
            {"apis":{"a":{"versions":{
              "v1":{"format":"OpenAPI/3.1"},
              "v2":{"format":"openapi/3.1"},
              "v3":{"format":"OpenAPI/3.1"}
            }}}}
            """);

        var versions = (await Send(engine, RegistryAction.Read, "/apigroups/g/apis/a/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(3);
        await Assert.That(versions.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(versions.GetProperty("v2").GetProperty("format").GetString()).IsEqualTo("openapi/3.1");
        await Assert.That(versions.GetProperty("v3").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
    }

    [Test]
    [Arguments("OpenAPI/3.0")]
    [Arguments("AsyncAPI/3.1")]
    public async Task DistinctApiVersionFormatsAreRejectedDuringCompleteResourceReconciliationWithoutChangingStoredState(
        string differentFormat)
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", """
            {"apis":{"a":{"versions":{"v1":{"format":"OpenAPI/3.1"}}}}}
            """);

        var importedGroup = new System.Text.Json.Nodes.JsonObject
        {
            ["apis"] = new System.Text.Json.Nodes.JsonObject
            {
                ["a"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["versions"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["v1"] = new System.Text.Json.Nodes.JsonObject { ["format"] = "OpenAPI/3.1" },
                        ["v2"] = new System.Text.Json.Nodes.JsonObject { ["format"] = differentFormat }
                    }
                }
            }
        };
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/apigroups/g", importedGroup.ToJsonString()),
            "mismatched_version_attribute");

        var versions = (await Send(engine, RegistryAction.Read, "/apigroups/g/apis/a/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Select(static item => item.Name).ToArray())
            .IsEquivalentTo(["v1"], StringComparer.Ordinal);
        await Assert.That(versions.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(versions.TryGetProperty("v2", out _)).IsFalse();
    }

    private sealed class IncludeResolver : IRegistryModelResolver
    {
        internal bool Available { get; set; } = true;
        internal int Calls { get; private set; }
        public RegistryJson Resolve(Uri documentUri)
        {
            Calls++;
            return Available ? RegistryJson.Parse(Model) : throw new InvalidOperationException("A stored model must not refetch a mutable include.");
        }
    }

    private sealed class OrderingClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class AncestryClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
