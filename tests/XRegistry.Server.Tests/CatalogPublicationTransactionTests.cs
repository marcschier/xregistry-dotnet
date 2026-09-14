using System.Security.Claims;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.CatalogPublicationTests;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class CatalogPublicationTransactionTests
{
    private const string Entry = "/categories/public/registries/entry";
    private const string ValidHttp = """
        {"xregurl":"https://registry.example/root","labels":{"note":"retained"},
         "federationprofiles":[{"name":"http","endpoint":"https://registry.example/root"}]}
        """;

    [Test]
    public async Task RejectedPatchRetainsDataOutboxGenerationAndVersionAllocation()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(CatalogModelSource(), store);
        await Send(engine, RegistryAction.Replace, Entry, ValidHttp);
        var before = await Capture(engine, store);

        await ExpectCode(() => Send(engine, RegistryAction.Patch, Entry,
            """{"xregurl":"https://other.example/root","labels":{"note":"must-not-publish"}}"""), "invalid_attribute");
        await AssertUnchanged(engine, store, before);
        await ExpectCode(() => Send(engine, RegistryAction.Post, Entry,
            """{"federationprofiles":[{"name":"git","endpoint":"https://git.example/repo","parameters":{"revision":"HEAD"}}]}"""),
            "invalid_attribute");
        await AssertUnchanged(engine, store, before);

        var next = await Send(engine, RegistryAction.Post, Entry, "{}");
        await Assert.That(next.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("2");
        await Assert.That(next.Metadata.RootElement.TryGetProperty("xregurl", out _)).IsFalse();
        await Assert.That((await Send(engine, RegistryAction.Read, Entry + "/meta")).Metadata!.RootElement
            .GetProperty("defaultversionid").GetString()).IsEqualTo("2");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation + 1);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(2);
    }

    [Test]
    public async Task InvalidNestedDescriptionRollsBackValidSiblings()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(CatalogModelSource(), store);
        await Send(engine, RegistryAction.Replace, Entry, ValidHttp);
        var before = await Capture(engine, store);
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/", """
            {"categories":{"batch":{"registries":{
              "a-good":{"xregurl":"https://registry.example/root"},
              "z-bad":{"registrytypes":["schema"]}
            }}}}
            """), "invalid_attribute");
        await AssertUnchanged(engine, store, before);
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/categories/batch"), "not_found");
    }

    [Test]
    public async Task NondefaultAndExplicitVersionWritesHaveTheSameCatalogRules()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(CatalogModelSource(), store);
        await Send(engine, RegistryAction.Replace, Entry, ValidHttp);
        await Send(engine, RegistryAction.Post, Entry, "{}");
        var before = await Capture(engine, store);
        await ExpectCode(() => Send(engine, RegistryAction.Patch, Entry + "/versions/1",
            """{"registrytypes":["schema"]}"""), "invalid_attribute");
        await AssertUnchanged(engine, store, before);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, Entry + "/versions/explicit",
            """{"relationships":[{"type":"mirrors","target":"../entry"}]}"""), "invalid_attribute");
        await AssertUnchanged(engine, store, before);
        await ExpectCode(() => Send(engine, RegistryAction.Read, Entry + "/versions/explicit"), "not_found");
    }

    [Test]
    public async Task AddingCompatibilityValidatesAllStoredVersionsAndSurvivesRestart()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(CatalogModelSource(compatible: false), store);
        await Send(engine, RegistryAction.Replace, Entry, """{"registrytypes":["schema"]}""");
        await Send(engine, RegistryAction.Post, Entry, "{}");
        var before = await Capture(engine, store);
        var modelBefore = (await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetRawText();

        var exception = await Assert.That(async () => await Send(engine, RegistryAction.Replace, "/modelsource", CatalogModelSource()))
            .Throws<RegistryException>();
        await Assert.That(exception!.Diagnostic.Code).IsEqualTo("model_compliance_error");
        await Assert.That(exception.Diagnostic.Path).IsEqualTo("/model");
        await AssertUnchanged(engine, store, before);
        await Assert.That((await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetRawText()).IsEqualTo(modelBefore);

        await Send(engine, RegistryAction.Patch, Entry + "/versions/1", """{"registrytypes":["urn:example:schema"]}""");
        await Send(engine, RegistryAction.Replace, "/modelsource", CatalogModelSource());
        var restarted = Create(CatalogModelSource(compatible: false), store);
        var optedIn = await Capture(restarted, store);
        await ExpectCode(() => Send(restarted, RegistryAction.Patch, Entry + "/versions/1",
            """{"registrytypes":["schema"]}"""), "invalid_attribute");
        await AssertUnchanged(restarted, store, optedIn);
        var retainedModel = (await Send(restarted, RegistryAction.Read, "/modelsource")).Metadata!.RootElement;
        await Assert.That(retainedModel.GetProperty("groups").GetProperty("categories").GetProperty("resources")
            .GetProperty("registries").GetProperty("modelcompatiblewith").GetString())
            .IsEqualTo("https://xregistry.io/xreg/domains/registry/specs/model.json");
    }

    [Test]
    public async Task RemovingCompatibilityDoesNotWeakenOrdinaryCoreMetadataConstraints()
    {
        var engine = Create(CatalogModelSource());
        await Send(engine, RegistryAction.Replace, Entry, "{}");
        await Send(engine, RegistryAction.Replace, "/modelsource", CatalogModelSource(compatible: false));
        var ordinary = await Send(engine, RegistryAction.Patch, Entry, """{"registrytypes":["schema"]}""");
        await Assert.That(ordinary.Metadata!.RootElement.GetProperty("registrytypes")[0].GetString()).IsEqualTo("schema");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, Entry, """{"registrytypes":[17]}"""), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, Entry)).Metadata!.RootElement
            .GetProperty("registrytypes")[0].GetString()).IsEqualTo("schema");
    }

    [Test]
    [Arguments("")]
    [Arguments("https://xregistry.io/xreg/domains/registry/specs/model.json#other")]
    [Arguments("https://xregistry.io/xreg/domains/Registry/specs/model.json")]
    public async Task SameSpellingDoesNotOptOrdinaryResourceModelsIntoCatalogRules(string compatibility)
    {
        var model = JsonNode.Parse(CatalogModelSource(compatible: false))!.AsObject();
        if (compatibility.Length != 0)
        {
            model["groups"]!["categories"]!["resources"]!["registries"]!["modelcompatiblewith"] = compatibility;
        }
        var engine = Create(model.ToJsonString());
        var accepted = await Send(engine, RegistryAction.Replace, Entry, """
            {"xregurl":"relative/root","registrytypes":["schema"],
             "federationprofiles":[{"name":"","endpoint":"relative"}],
             "relationships":[{"type":"","target":"../entry"}]}
            """);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("xregurl").GetString()).IsEqualTo("relative/root");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("federationprofiles")[0].GetProperty("name").GetString()).IsEqualTo("");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("relationships")[0].GetProperty("target").GetString()).IsEqualTo("../entry");
    }

    [Test]
    public async Task GroupCompatibilityAloneDoesNotActivateResourceDescriptionRules()
    {
        var model = JsonNode.Parse(CatalogModelSource(compatible: false))!.AsObject();
        model["groups"]!["categories"]!["modelcompatiblewith"] = "https://xregistry.io/xreg/domains/registry/specs/model.json";
        var engine = Create(model.ToJsonString());
        await Send(engine, RegistryAction.Replace, "/categories/public", """{"xregurl":"relative/group"}""");
        var accepted = await Send(engine, RegistryAction.Replace, Entry, """{"xregurl":"relative/entry"}""");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("xregurl").GetString()).IsEqualTo("relative/entry");
        await Assert.That((await Send(engine, RegistryAction.Read, "/categories/public")).Metadata!.RootElement
            .GetProperty("xregurl").GetString()).IsEqualTo("relative/group");
    }

    [Test]
    public async Task ExplicitCompatibilityDispatchDoesNotDependOnCollectionSpelling()
    {
        var source = CatalogModelSource().Replace("\"categories\"", "\"buckets\"", StringComparison.Ordinal)
            .Replace("\"category\"", "\"bucket\"", StringComparison.Ordinal)
            .Replace("\"registries\"", "\"entries\"", StringComparison.Ordinal)
            .Replace("\"registry\"", "\"entry\"", StringComparison.Ordinal);
        var engine = Create(source);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/buckets/public/entries/entry",
            """{"xregurl":"relative/root"}"""), "invalid_attribute");
        var accepted = await Send(engine, RegistryAction.Replace, "/buckets/public/entries/entry", "{}");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("entryid").GetString()).IsEqualTo("entry");
        await Assert.That(accepted.Metadata.RootElement.TryGetProperty("xregurl", out _)).IsFalse();
    }

    [Test]
    public async Task CatalogRulesInspectCompletedReadonlyDefaultsNotDiscardedInput()
    {
        var model = JsonNode.Parse(CatalogModelSource())!.AsObject();
        var root = model["groups"]!["categories"]!["resources"]!["registries"]!["attributes"]!["xregurl"]!;
        root["readonly"] = true;
        root["required"] = true;
        root["default"] = "relative/root";
        await ExpectCode(() => Send(Create(model.ToJsonString()), RegistryAction.Replace, Entry, "{}"), "invalid_attribute");
        root["default"] = "https://registry.example/root";
        var engine = Create(model.ToJsonString());
        await Send(engine, RegistryAction.Replace, Entry, """{"xregurl":"relative/discarded"}""");
        var patched = await Send(engine, RegistryAction.Patch, Entry, """{"xregurl":null}""");
        await Assert.That(patched.Metadata!.RootElement.GetProperty("xregurl").GetString()).IsEqualTo("https://registry.example/root");
    }

    [Test]
    public async Task AdvertisementsNeverAcquireReferencesOrForwardIncomingCredentials()
    {
        var forbidden = new ForbiddenReferenceAccess();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "no-acquisition",
            PublicRoot = new Uri("https://catalog.example/root"),
            Model = RegistryModel.Compile(RegistryJson.Parse(CatalogModelSource())),
            ModelCompilation = new() { Resolver = forbidden },
            DocumentReferencePolicy = forbidden
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        var caller = new RegistryOperationContext(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "catalog-writer"), new Claim("test-secret", "never-forward")], "test")));
        var result = await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse(Entry))
        {
            Metadata = RegistryJson.Parse("""
                {"weburl":"https://127.0.0.1:1/site","authority":"urn:example:untrusted",
                 "registrytypes":["https://127.0.0.1:1/model.json"],
                 "federationprofiles":[
                  {"name":"http","endpoint":"https://127.0.0.1:1/root"},
                  {"name":"git","endpoint":"https://127.0.0.1:1/repo","parameters":{"revision":"refs/heads/main"}},
                  {"name":"file","endpoint":"file:///catalog-advertisement-not-acquired","parameters":{"layout":"document-tree"}},
                  {"name":"oci","endpoint":"oci://127.0.0.1:1/team/repo","parameters":{"reference":"stable"}}],
                 "relationships":[{"type":"mirrors","target":"https://127.0.0.1:1/categories/missing/registries/missing"}]}
                """)
        }, caller);
        await Assert.That(result.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(result.Metadata!.RootElement.GetProperty("federationprofiles").GetArrayLength()).IsEqualTo(4);
        await Assert.That(result.Metadata.RootElement.GetRawText().Contains("never-forward", StringComparison.Ordinal)).IsFalse();
        var events = await engine.ReadEventBatchesAsync(caller);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Events.RootElement.GetRawText().Contains("never-forward", StringComparison.Ordinal)).IsFalse();
        await Assert.That(forbidden.ModelCalls).IsEqualTo(0);
        await Assert.That(forbidden.ReferenceCalls).IsEqualTo(0);
    }

    [Test]
    public async Task CanceledCatalogWritesNeverPublish()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(CatalogModelSource(), store);
        using var before = await store.ReadSnapshotAsync();
        await Assert.That(async () => await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse(Entry))
        {
            Metadata = RegistryJson.Parse(ValidHttp)
        }, Writer(), new CancellationToken(canceled: true))).Throws<OperationCanceledException>();
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    private static async Task<CatalogState> Capture(RegistryEngine engine, IRegistryPersistence store)
    {
        using var snapshot = await store.ReadSnapshotAsync();
        var metadata = new List<string>();
        foreach (var path in new[] { "/", "/categories/public", Entry, Entry + "/meta", Entry + "/versions" })
        {
            metadata.Add((await Send(engine, RegistryAction.Read, path)).Metadata!.RootElement.GetRawText());
        }
        var events = await engine.ReadEventBatchesAsync(Writer());
        return new(snapshot.Generation, metadata.ToArray(),
            events.Select(static batch => batch.CorrelationId + ":" + batch.Events.RootElement.GetRawText()).ToArray());
    }

    private static async Task AssertUnchanged(RegistryEngine engine, IRegistryPersistence store, CatalogState expected)
    {
        var actual = await Capture(engine, store);
        await Assert.That(actual.Generation).IsEqualTo(expected.Generation);
        await Assert.That(actual.Metadata).IsEquivalentTo(expected.Metadata, StringComparer.Ordinal);
        await Assert.That(actual.Events).IsEquivalentTo(expected.Events, StringComparer.Ordinal);
    }

    private sealed record CatalogState(long Generation, string[] Metadata, string[] Events);

    private sealed class ForbiddenReferenceAccess : IRegistryModelResolver, IRegistryDocumentReferencePolicy
    {
        internal int ModelCalls { get; private set; }
        internal int ReferenceCalls { get; private set; }

        public RegistryJson Resolve(Uri documentUri)
        {
            ModelCalls++;
            throw new InvalidOperationException("Catalog publication must not acquire a model reference.");
        }

        public ValueTask AuthorizeAsync(ClaimsPrincipal caller, Uri reference, CancellationToken cancellationToken = default)
        {
            ReferenceCalls++;
            throw new InvalidOperationException("An advertisement must not authorize an external Document reference.");
        }
    }
}
