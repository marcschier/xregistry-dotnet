using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.OpenUsdHostIntegrationTests;

namespace XRegistry.Server.Tests;

public class OpenUsdCollisionIdentityTests
{
    [Test]
    public async Task CollidingSourcesKeepThePublishedCandidateAndUseTheExactSourceFallback()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        const string candidate = "/usdassetgroups/g/usdassets/a.b$details";
        const string fallback = "/usdassetgroups/g/usdassets/a.b.2e7336dc$details";
        await Send(engine, RegistryAction.Replace, candidate, Artifact("a/b", "Texture", "Opaque/1.0").ToJsonString());
        var before = (await Send(engine, RegistryAction.Read, candidate)).Metadata!.RootElement.GetRawText();

        await Send(engine, RegistryAction.Replace, fallback, Artifact("a.b", "Texture", "Opaque/1.0").ToJsonString());

        await Assert.That((await Send(engine, RegistryAction.Read, candidate)).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        var second = (await Send(engine, RegistryAction.Read, fallback)).Metadata!.RootElement;
        await Assert.That(second.GetProperty("usdassetid").GetString()).IsEqualTo("a.b.2e7336dc");
        await Assert.That(second.GetProperty("assetidentifier").GetString()).IsEqualTo("a.b");
        await Assert.That(second.GetProperty("name").GetString()).IsEqualTo("a.b");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement
            .GetProperty("usdassetscount").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task CaseCollisionsUseAFallbackWithoutChangingCoreCaseSensitiveLookup()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/Pump$details",
            Artifact("Pump", "Texture", "Opaque/1.0").ToJsonString());
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/pump$details",
            Artifact("pump", "Texture", "Opaque/1.0").ToJsonString()), "mismatched_id");
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/pump.0b203c46$details",
            Artifact("pump", "Texture", "Opaque/1.0").ToJsonString());
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/Pump$details"))
            .Metadata!.RootElement.GetProperty("assetidentifier").GetString()).IsEqualTo("Pump");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/pump.0b203c46$details"))
            .Metadata!.RootElement.GetProperty("assetidentifier").GetString()).IsEqualTo("pump");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/pump$details"), "not_found");
    }

    [Test]
    [Arguments(120, "2f3d3354")]
    [Arguments(128, "6836cf13")]
    public async Task CollisionOnlyLongIdsAreReservedWithinCoreLimits(int length, string hash)
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        var source = new string('a', length);
        var existing = new string('A', length);
        var fallback = new string('a', 119) + "." + hash;
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/" + existing + "$details",
            Artifact(existing, "Texture", "Opaque/1.0").ToJsonString());
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/" + fallback + "$details",
            Artifact(source, "Texture", "Opaque/1.0").ToJsonString());
        var metadata = (await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/" + fallback + "$details")).Metadata!.RootElement;
        await Assert.That(metadata.GetProperty("usdassetid").GetString()).IsEqualTo(fallback);
        await Assert.That(metadata.GetProperty("assetidentifier").GetString()).IsEqualTo(source);
        await Assert.That(metadata.GetProperty("usdassetid").GetString()!.Length).IsEqualTo(128);
    }

    [Test]
    public async Task PublishedFallbackSurvivesUpdatesCompetitorDeletionAndEngineRestart()
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = CreateOpenUsd(persistence);
        await Seed(engine);
        const string path = "/usdassetgroups/g/usdassets/a.b.2e7336dc$details";
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/a.b$details",
            Artifact("a/b", "Texture", "Opaque/1.0").ToJsonString());
        await Send(engine, RegistryAction.Replace, path, Artifact("a.b", "Texture", "Opaque/1.0").ToJsonString());
        await Send(engine, RegistryAction.Post, path, Artifact("a.b", "Texture", "Opaque/1.0").ToJsonString());
        await Send(engine, RegistryAction.Delete, "/usdassetgroups/g/usdassets/a.b");

        var restarted = CreateOpenUsd(persistence);
        await Send(restarted, RegistryAction.Patch, path, """{"description":"stable after restart"}""");
        var metadata = (await Send(restarted, RegistryAction.Read, path)).Metadata!.RootElement;
        await Assert.That(metadata.GetProperty("usdassetid").GetString()).IsEqualTo("a.b.2e7336dc");
        await Assert.That(metadata.GetProperty("assetidentifier").GetString()).IsEqualTo("a.b");
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo("2");
        await Assert.That(metadata.GetProperty("description").GetString()).IsEqualTo("stable after restart");

        var events = (await restarted.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(restarted, RegistryAction.Replace, "/usdassetgroups/g/usdassets/a.b$details",
            Artifact("a.b", "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");
        await Assert.That((await restarted.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Assert.That((await Send(restarted, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement
            .GetProperty("usdassetscount").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task CandidateAndNaturalFallbackOwnersCannotBeOverwrittenOrRebound()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        const string candidate = "/usdassetgroups/g/usdassets/a.b$details";
        const string fallback = "/usdassetgroups/g/usdassets/a.b.2e7336dc$details";
        await Send(engine, RegistryAction.Replace, candidate, Artifact("a/b", "Texture", "Opaque/1.0").ToJsonString());
        await Send(engine, RegistryAction.Replace, fallback, Artifact("a.b.2e7336dc", "Texture", "Opaque/1.0").ToJsonString());
        var first = (await Send(engine, RegistryAction.Read, candidate)).Metadata!.RootElement.GetRawText();
        var second = (await Send(engine, RegistryAction.Read, fallback)).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Replace, candidate,
            Artifact("a.b", "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, fallback,
            Artifact("a.b", "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");
        await ExpectCode(() => Send(engine, RegistryAction.Post, fallback,
            Artifact("a.b", "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");

        await Assert.That((await Send(engine, RegistryAction.Read, candidate)).Metadata!.RootElement.GetRawText()).IsEqualTo(first);
        await Assert.That((await Send(engine, RegistryAction.Read, fallback)).Metadata!.RootElement.GetRawText()).IsEqualTo(second);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task RealSecondaryHashCollisionFailsAtomicallyWithoutInventingAThirdId()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        const string fallback = "/usdassetgroups/g/usdassets/test.example.asset.df41192b$details";
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/test.example.asset$details",
            Artifact("test.example.asset", "Texture", "Opaque/1.0").ToJsonString());
        await Send(engine, RegistryAction.Replace, fallback,
            Artifact("https://example.test/asset?i=184995", "Texture", "Opaque/1.0").ToJsonString());
        var before = (await Send(engine, RegistryAction.Read, fallback)).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Replace, fallback,
            Artifact("https://example.test/asset?i=191756", "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/test.example.asset.df41192b.1$details",
            Artifact("https://example.test/asset?i=191756", "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");

        await Assert.That((await Send(engine, RegistryAction.Read, fallback)).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task AnUnoccupiedCandidateCannotBeReplacedByUnconditionalHashing()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/a.b.2e7336dc$details",
            Artifact("a.b", "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AtomicBatchVerifiesExplicitReservationsIndependentOfJsonMemberOrder(bool reverse)
    {
        var engine = CreateOpenUsd();
        var group = Group();
        var assets = group["usdassets"]!.AsObject();
        if (reverse) { assets["a.b.2e7336dc"] = Artifact("a.b", "Texture", "Opaque/1.0"); }
        assets["a.b"] = Artifact("a/b", "Texture", "Opaque/1.0");
        if (!reverse) { assets["a.b.2e7336dc"] = Artifact("a.b", "Texture", "Opaque/1.0"); }
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", group.ToJsonString());
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/a.b$details"))
            .Metadata!.RootElement.GetProperty("assetidentifier").GetString()).IsEqualTo("a/b");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/a.b.2e7336dc$details"))
            .Metadata!.RootElement.GetProperty("assetidentifier").GetString()).IsEqualTo("a.b");
    }

    [Test]
    public async Task AliasRetargetingCannotRebindAPublishedAuthoritativeIdentifier()
    {
        var engine = CreateOpenUsd();
        var first = Group();
        first["usdassets"]!["a.b"] = Artifact("a/b", "Texture", "Opaque/1.0");
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", first.ToJsonString());
        var second = Group();
        second["name"] = "other";
        second["usdassets"]!["a.b"] = Artifact("a.b", "Texture", "Opaque/1.0");
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/other", second.ToJsonString());
        var borrower = Group();
        borrower["name"] = "borrower";
        borrower["usdassets"]!["a.b"] = new JsonObject
        {
            ["meta"] = new JsonObject { ["xref"] = "/usdassetgroups/g/usdassets/a.b" },
        };
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/borrower", borrower.ToJsonString());
        const string path = "/usdassetgroups/borrower/usdassets/a.b";
        var before = (await Send(engine, RegistryAction.Read, path + "$details")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Patch, path + "/meta",
            """{"xref":"/usdassetgroups/other/usdassets/a.b"}"""), "invalid_attribute");

        await Assert.That((await Send(engine, RegistryAction.Read, path + "$details")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task AliasRetargetingToTheSameAuthoritativeSourceRemainsAllowed()
    {
        var engine = CreateOpenUsd();
        var first = Group();
        first["usdassets"]!["a.b"] = Artifact("a/b", "Texture", "Opaque/1.0");
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", first.ToJsonString());
        var second = Group();
        second["name"] = "other";
        second["usdassets"]!["a.b"] = Artifact("a/b", "Texture", "Opaque/1.0");
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/other", second.ToJsonString());
        var borrower = Group();
        borrower["name"] = "borrower";
        borrower["usdassets"]!["a.b"] = new JsonObject
        {
            ["meta"] = new JsonObject { ["xref"] = "/usdassetgroups/g/usdassets/a.b" },
        };
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/borrower", borrower.ToJsonString());
        await Send(engine, RegistryAction.Patch, "/usdassetgroups/borrower/usdassets/a.b/meta",
            """{"xref":"/usdassetgroups/other/usdassets/a.b"}""");

        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/borrower/usdassets/a.b$details"))
            .Metadata!.RootElement.GetProperty("assetidentifier").GetString()).IsEqualTo("a/b");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/borrower/usdassets/a.b/meta"))
            .Metadata!.RootElement.GetProperty("xref").GetString()).IsEqualTo("/usdassetgroups/other/usdassets/a.b");
    }

    [Test]
    public async Task ASecondaryCollisionCannotReplaceThePublishedOwnersDocument()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/a.b$details",
            Artifact("a/b", "Texture", "Opaque/1.0").ToJsonString());
        var original = Artifact("a.b.2e7336dc", "Texture", "Opaque/1.0");
        original["usdassetbase64"] = "AQI=";
        const string path = "/usdassetgroups/g/usdassets/a.b.2e7336dc";
        await Send(engine, RegistryAction.Replace, path + "$details", original.ToJsonString());
        var replacement = Artifact("a.b", "Texture", "Opaque/1.0");
        replacement["usdassetbase64"] = "Aw==";
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Replace, path + "$details", replacement.ToJsonString()), "invalid_attribute");

        using var bytes = (await Send(engine, RegistryAction.Read, path)).Document!.OpenRead();
        await Assert.That(bytes.ReadByte()).IsEqualTo(1);
        await Assert.That(bytes.ReadByte()).IsEqualTo(2);
        await Assert.That(bytes.ReadByte()).IsEqualTo(-1);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task PublicEngineMetadataResolvesACaseCollisionWithoutAcquiringArtifactBytes()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/Pump$details",
            Artifact("Pump", "Texture", "Opaque/1.0").ToJsonString());
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/pump.0b203c46$details",
            Artifact("pump", "Texture", "Opaque/1.0").ToJsonString());
        var paths = new List<string>();
        var match = await OpenUsdResolution.ResolveAsync("pump", async (id, allowance, token) =>
        {
            var path = "/usdassetgroups/g/usdassets/" + id + "$details";
            paths.Add(path);
            try
            {
                var result = await Send(engine, RegistryAction.Read, path);
                await Assert.That(result.Document).IsNull();
                return (ReadOnlyMemory<byte>?)Encoding.UTF8.GetBytes(result.Metadata!.RootElement.GetRawText());
            }
            catch (RegistryException exception) when (exception.Diagnostic.Code == "not_found")
            {
                return null;
            }
        }, new OpenUsdResolutionLimits(4, 4096));

        await Assert.That(match.ResourceId).IsEqualTo("pump.0b203c46");
        await Assert.That(match.AssetIdentifier).IsEqualTo("pump");
        await Assert.That(string.Join(",", paths)).IsEqualTo(
            "/usdassetgroups/g/usdassets/pump$details,/usdassetgroups/g/usdassets/pump.0b203c46$details");
    }

    [Test]
    public async Task MissingSourceIdStopsAfterTwoAuthorizedMetadataOperationsBeforeArtifactAcquisition()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        var paths = new List<string>();
        var documentReads = 0;
        await Assert.That(async () =>
        {
            var match = await OpenUsdResolution.ResolveAsync("a.b", async (id, allowance, token) =>
            {
                var path = "/usdassetgroups/g/usdassets/" + id + "$details";
                paths.Add(path);
                try
                {
                    var result = await Send(engine, RegistryAction.Read, path);
                    return (ReadOnlyMemory<byte>?)Encoding.UTF8.GetBytes(result.Metadata!.RootElement.GetRawText());
                }
                catch (RegistryException exception) when (exception.Diagnostic.Code == "not_found")
                {
                    return null;
                }
            }, new OpenUsdResolutionLimits(3, 4096));
            documentReads++;
            await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/" + match.ResourceId);
        }).Throws<KeyNotFoundException>();

        await Assert.That(documentReads).IsEqualTo(0);
        await Assert.That(string.Join(",", paths)).IsEqualTo(
            "/usdassetgroups/g/usdassets/a.b$details,/usdassetgroups/g/usdassets/a.b.2e7336dc$details");
    }

    [Test]
    public async Task SymbolicContainerCollisionsUseFallbacksWhileLegalCoreContainerIdsStayVerbatim()
    {
        var engine = CreateOpenUsd();
        var first = Group();
        first["name"] = "a.b";
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/a.b", first.ToJsonString());
        var second = Group();
        second["name"] = "a/b";
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/a.b.c14cddc0", second.ToJsonString());
        var legal = Group();
        legal["name"] = "A~:@";
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/A~:@", legal.ToJsonString());

        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/a.b")).Metadata!.RootElement
            .GetProperty("name").GetString()).IsEqualTo("a.b");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/a.b.c14cddc0")).Metadata!.RootElement
            .GetProperty("name").GetString()).IsEqualTo("a/b");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/A~:@")).Metadata!.RootElement
            .GetProperty("usdassetgroupid").GetString()).IsEqualTo("A~:@");
    }

    [Test]
    public async Task SymbolicPluginGroupFallbacksKeepManifestIdentityAfterDeletionAndRestart()
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = CreateOpenUsd(persistence);
        await Send(engine, RegistryAction.Replace, "/usdschemaplugingroups/a.b", PluginGroup("a/b").ToJsonString());
        await Send(engine, RegistryAction.Replace, "/usdschemaplugingroups/a.b.2e7336dc", PluginGroup("a.b").ToJsonString());
        await Send(engine, RegistryAction.Delete, "/usdschemaplugingroups/a.b");
        var restarted = CreateOpenUsd(persistence);
        await Send(restarted, RegistryAction.Patch, "/usdschemaplugingroups/a.b.2e7336dc", """{"description":"stable"}""");

        var group = (await Send(restarted, RegistryAction.Read, "/usdschemaplugingroups/a.b.2e7336dc")).Metadata!.RootElement;
        await Assert.That(group.GetProperty("usdschemaplugingroupid").GetString()).IsEqualTo("a.b.2e7336dc");
        await Assert.That(group.GetProperty("name").GetString()).IsEqualTo("a.b");
        var artifact = await Send(restarted, RegistryAction.Read, "/usdschemaplugingroups/a.b.2e7336dc/usdassets/plugInfo.json");
        using var document = artifact.Document!.OpenRead();
        using var reader = new StreamReader(document);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("""{"Plugins":[{"Name":"a.b"}]}""");
        var events = (await restarted.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(restarted, RegistryAction.Replace, "/usdschemaplugingroups/a.b",
            PluginGroup("a.b").ToJsonString()), "invalid_attribute");
        await Assert.That((await restarted.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task APublishedSymbolicGroupCannotBeReboundToAnotherCollidingName()
    {
        var engine = CreateOpenUsd();
        var group = Group();
        group["name"] = "a/b";
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/a.b", group.ToJsonString());
        var before = (await Send(engine, RegistryAction.Read, "/usdassetgroups/a.b")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/usdassetgroups/a.b", """{"name":"a.b"}"""), "invalid_attribute");

        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/a.b")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task LegalContainerIdsAndUnoccupiedSymbolicCandidatesCannotBeUnconditionallyHashed()
    {
        var engine = CreateOpenUsd();
        var legal = Group();
        legal["name"] = "Pump";
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/Pump", legal.ToJsonString());
        var collision = Group();
        collision["name"] = "pump";
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/pump.0b203c46",
            collision.ToJsonString()), "invalid_attribute");
        var unnecessary = Group();
        unnecessary["name"] = "a/b";
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/a.b.c14cddc0",
            unnecessary.ToJsonString()), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement
            .GetProperty("usdassetgroupscount").GetInt32()).IsEqualTo(1);
    }

    private static JsonObject PluginGroup(string name)
    {
        var manifest = Artifact("plugInfo.json", "SchemaPlugin", "USD-PlugInfo/1.0");
        var document = new JsonObject { ["Plugins"] = new JsonArray(new JsonObject { ["Name"] = name }) };
        manifest["usdassetbase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(document.ToJsonString()));
        return new JsonObject
        {
            ["name"] = name,
            ["usdassets"] = new JsonObject { ["plugInfo.json"] = manifest },
        };
    }
}
