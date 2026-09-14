using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class ModelAndVersionTests
{
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
}
