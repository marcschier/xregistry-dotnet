// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class OpenUsdHostIntegrationTests
{
    [Test]
    public async Task OpaqueArtifactsNeverReachTheFormatValidatorOrReceiveFabricatedValidationFlags()
    {
        var validator = new RecordingValidator();
        var engine = CreateOpenUsd(validator: validator);
        await Seed(engine);
        validator.Calls = 0;
        validator.FormatsSeen.Clear();
        var result = await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/texture.png$details",
            Artifact("texture.png", "Texture", "Opaque/1.0").ToJsonString());

        await Assert.That(validator.Calls).IsEqualTo(0);
        await Assert.That(validator.FormatsSeen.Count).IsEqualTo(0);
        await Assert.That(result.Metadata!.RootElement.TryGetProperty("formatvalidated", out _)).IsFalse();
        await Assert.That(result.Metadata.RootElement.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
    }

    [Test]
    [Arguments("a/b.png", "a.b.png")]
    [Arguments("texture.png", "./texture.png")]
    public async Task AnExistingResourcesAssetIdentifierCannotBeReboundEvenWhenItsSymbolicIdWouldMatch(string original, string changed)
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        var id = OpenUsdIdentifiers.CreateSymbolicIdCandidate(original, 4096);
        var path = "/usdassetgroups/g/usdassets/" + id + "$details";
        await Send(engine, RegistryAction.Replace, path, Artifact(original, "Texture", "Opaque/1.0").ToJsonString());
        var before = (await Send(engine, RegistryAction.Read, path)).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Replace, path,
            Artifact(changed, "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");
        await ExpectCode(() => Send(engine, RegistryAction.Post, path,
            Artifact(changed, "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, path)).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    [Arguments("wrong-id")]
    [Arguments("wrong-name")]
    [Arguments("unnormalized")]
    [Arguments("missing-name")]
    public async Task AssetIdentityAndVerbatimNamesAreValidatedBeforePublishing(string mutation)
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        var artifact = Artifact("textures/a.png", "Texture", "Opaque/1.0");
        var id = "textures.a.png";
        if (mutation == "wrong-id") { id = "other"; }
        if (mutation == "wrong-name") { artifact["name"] = "display name"; }
        if (mutation == "unnormalized") { artifact["assetidentifier"] = "./textures/a.png"; artifact["name"] = "./textures/a.png"; }
        if (mutation == "missing-name") { artifact.Remove("name"); }

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/" + id + "$details",
            artifact.ToJsonString()), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement.GetProperty("usdassetscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    [Arguments("missing-root")]
    [Arguments("duplicate-root")]
    [Arguments("wrong-rootlayer")]
    [Arguments("missing-dependency")]
    [Arguments("missing-group-name")]
    public async Task InvalidFinalGroupGraphsFailAtomically(string mutation)
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = CreateOpenUsd(persistence);
        var group = Group();
        var assets = group["usdassets"]!.AsObject();
        if (mutation == "missing-root") { assets.Clear(); }
        if (mutation == "duplicate-root") { assets["second.usda"] = Artifact("second.usda", "RootLayer", "OpenUSD/1.0"); }
        if (mutation == "wrong-rootlayer") { group["rootlayer"] = "missing.usda"; }
        if (mutation == "missing-dependency") { assets["scene.usda"]!["dependson"] = new JsonArray("missing.png"); }
        if (mutation == "missing-group-name") { group.Remove("name"); }

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g", group.ToJsonString()), "invalid_attribute");
        using var snapshot = await persistence.ReadSnapshotAsync();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DependenciesMustBeUpdatedBeforeAReferencedArtifactCanBeDeleted()
    {
        var engine = CreateOpenUsd();
        var group = Group();
        group["usdassets"]!["textures.a.png"] = Artifact("textures/a.png", "Texture", "Opaque/1.0");
        group["usdassets"]!["scene.usda"]!["dependson"] = new JsonArray("textures/a.png");
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", group.ToJsonString());
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Delete, "/usdassetgroups/g/usdassets/textures.a.png"), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", Group().ToJsonString());
        await Send(engine, RegistryAction.Delete, "/usdassetgroups/g/usdassets/textures.a.png");
        var result = (await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement;
        await Assert.That(result.GetProperty("usdassetscount").GetInt32()).IsEqualTo(1);
        await Send(engine, RegistryAction.Delete, "/usdassetgroups/g");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("usdassetgroupscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task RootRolesAndTheEntryPointCanMoveTogetherInOneAtomicGroupUpdate()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        var replacement = Group();
        replacement["rootlayer"] = "second.usda";
        replacement["usdassets"]!["scene.usda"]!["assetkind"] = "Reference";
        replacement["usdassets"]!["second.usda"] = Artifact("second.usda", "RootLayer", "OpenUSD/1.0");
        replacement["usdassets"]!["second.usda"]!["dependson"] = new JsonArray("scene.usda");
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", replacement.ToJsonString());

        var group = (await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement;
        await Assert.That(group.GetProperty("rootlayer").GetString()).IsEqualTo("second.usda");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/scene.usda$details"))
            .Metadata!.RootElement.GetProperty("assetkind").GetString()).IsEqualTo("Reference");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/second.usda$details"))
            .Metadata!.RootElement.GetProperty("assetkind").GetString()).IsEqualTo("RootLayer");
    }

    [Test]
    public async Task ACanonicalPackageSatisfiesItsUnlistedMemberDependencies()
    {
        var engine = CreateOpenUsd();
        var group = Group();
        group["usdassets"]!["assets.usdz"] = Artifact("assets.usdz", "Package", "USDZ/1.0");
        group["usdassets"]!["scene.usda"]!["dependson"] = new JsonArray("assets.usdz[tex/a.png]");
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", group.ToJsonString());
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement.GetProperty("usdassetscount").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task UpdatingATargetCannotInvalidateASeparateGroupsBorrowedRoot()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/borrowed", """
            {"name":"borrowed","rootlayer":"scene.usda","usdassets":{
              "scene.usda":{"meta":{"xref":"/usdassetgroups/g/usdassets/scene.usda"}}
            }}
            """);
        var replacement = Group();
        replacement["rootlayer"] = "second.usda";
        replacement["usdassets"]!["scene.usda"]!["assetkind"] = "Reference";
        replacement["usdassets"]!["second.usda"] = Artifact("second.usda", "RootLayer", "OpenUSD/1.0");
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g", replacement.ToJsonString()), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement.GetProperty("rootlayer").GetString())
            .IsEqualTo("scene.usda");
    }

    [Test]
    public async Task ReverseConstraintChecksDoNotDiscloseOrRequireReadAccessToTheReferringGroup()
    {
        var policy = new ReadPolicy();
        var engine = CreateOpenUsd(authorization: policy);
        await Seed(engine);
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/borrowed", """
            {"name":"borrowed","rootlayer":"scene.usda","usdassets":{
              "scene.usda":{"meta":{"xref":"/usdassetgroups/g/usdassets/scene.usda"}}
            }}
            """);
        policy.DenyBorrowedReads = true;
        await Send(engine, RegistryAction.Patch, "/usdassetgroups/g/usdassets/scene.usda$details", """{"description":"still valid"}""");
        var replacement = Group();
        replacement["rootlayer"] = "second.usda";
        replacement["usdassets"]!["scene.usda"]!["assetkind"] = "Reference";
        replacement["usdassets"]!["second.usda"] = Artifact("second.usda", "RootLayer", "OpenUSD/1.0");
        var error = await Assert.That(async () => await Send(engine, RegistryAction.Replace,
            "/usdassetgroups/g", replacement.ToJsonString())).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the existing reverse constraint to reject the update.");

        await Assert.That(error.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Diagnostic.Path).IsEqualTo("/usdassetgroups/g");
        await Assert.That(error.Message.Contains("borrowed", StringComparison.Ordinal)).IsFalse();
        await Assert.That(policy.Denied).IsEqualTo(0);
    }

    [Test]
    [Arguments("missing-algorithm")]
    [Arguments("malformed")]
    [Arguments("wrong-bytes")]
    public async Task PublishedDigestDeclarationsMustMatchTheStoredDocument(string mutation)
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        var artifact = Artifact("empty.bin", "Texture", "Opaque/1.0");
        artifact["digest"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        artifact["digestalg"] = "Sha256";
        if (mutation == "missing-algorithm") { artifact.Remove("digestalg"); }
        if (mutation == "malformed") { artifact["digest"] = "not-hex"; }
        if (mutation == "wrong-bytes") { artifact["usdassetbase64"] = "AQ=="; }

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/empty.bin$details",
            artifact.ToJsonString()), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement.GetProperty("usdassetscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task AnExactZeroByteDigestIsVerifiedWithoutImplyingFormatValidity()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        var artifact = Artifact("empty.bin", "Texture", "Opaque/1.0");
        artifact["digest"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        artifact["digestalg"] = "Sha256";
        var result = await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/empty.bin$details", artifact.ToJsonString());

        await Assert.That(result.Metadata!.RootElement.GetProperty("digest").GetString()).IsEqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        await Assert.That(result.Metadata.RootElement.TryGetProperty("formatvalidated", out _)).IsFalse();
    }

    [Test]
    public async Task MixedOpaqueAndInspectableVersionsDoNotFabricateCrossVersionCompatibility()
    {
        var validator = new RecordingValidator();
        var engine = CreateOpenUsd(validator: validator);
        await Seed(engine);
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/texture.png$details",
            Artifact("texture.png", "Texture", "Opaque/1.0").ToJsonString());
        await Send(engine, RegistryAction.Patch, "/usdassetgroups/g/usdassets/texture.png/meta", """{"compatibility":"backward"}""");
        validator.Calls = 0;
        validator.FormatsSeen.Clear();
        var next = Artifact("texture.png", "Texture", "ImageTest/1.0");
        var result = await Send(engine, RegistryAction.Post, "/usdassetgroups/g/usdassets/texture.png$details", next.ToJsonString());

        await Assert.That(validator.Calls).IsEqualTo(1);
        await Assert.That(string.Join(",", validator.FormatsSeen)).IsEqualTo("ImageTest/1.0");
        await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(result.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        var old = (await Send(engine, RegistryAction.Read, "/usdassetgroups/g/usdassets/texture.png/versions/1$details")).Metadata!.RootElement;
        await Assert.That(old.TryGetProperty("formatvalidated", out _)).IsFalse();
    }

    [Test]
    public async Task SimilarCustomModelsDoNotInheritOpenUsdRestrictionsOrBypassTheirValidators()
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "ordinary",
            PublicRoot = new Uri("https://registry.example/ordinary"),
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"usdassetgroups":{"singular":"usdassetgroup","resources":{
                  "usdassets":{"singular":"usdasset","validateformat":true,"attributes":{"*":{"type":"any"}}}
                }}}}
                """)),
            ResourceValidator = new RecordingValidator(),
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/arbitrary$details",
            """{"format":"Opaque/1.0"}"""), "format_violation");
    }

    [Test]
    public async Task UnreadableGraphMembersPreventPublicationWithoutRevealingTheirContents()
    {
        var persistence = new InMemoryRegistryPersistence();
        var policy = new ReadPolicy();
        var engine = CreateOpenUsd(persistence, authorization: policy);
        await Seed(engine);
        using var before = await persistence.ReadSnapshotAsync();
        policy.DenyVersionReads = true;

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/texture.png$details",
            Artifact("texture.png", "Texture", "Opaque/1.0").ToJsonString()), "forbidden");
        using var after = await persistence.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That(policy.Denied).IsGreaterThan(0);
        policy.DenyVersionReads = false;
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement.GetProperty("usdassetscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task DeletingTheEntireGroupDoesNotRequireReadingItsAlreadyRemovedDomainGraph()
    {
        var policy = new ReadPolicy();
        var engine = CreateOpenUsd(authorization: policy);
        await Seed(engine);
        policy.DenyAllReads = true;
        await Send(engine, RegistryAction.Delete, "/usdassetgroups/g");
        await Assert.That(policy.Denied).IsEqualTo(0);
        policy.DenyAllReads = false;
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("usdassetgroupscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments("root")]
    [Arguments("manifest-format")]
    [Arguments("schema-format")]
    [Arguments("manifest-name")]
    public async Task PluginGroupsEnforceTheirDistinctRolesAndDeclaredPluginName(string mutation)
    {
        var engine = CreateOpenUsd();
        var group = Plugin();
        if (mutation == "root") { group["usdassets"]!["generatedSchema.usda"]!["assetkind"] = "RootLayer"; }
        if (mutation == "manifest-format") { group["usdassets"]!["plugInfo.json"]!["format"] = "OpenUSD/1.0"; }
        if (mutation == "schema-format") { group["usdassets"]!["generatedSchema.usda"]!["format"] = "OpenUSD/1.0"; }
        if (mutation == "manifest-name") { group["usdassets"]!["plugInfo.json"]!["usdassetbase64"] = Convert.ToBase64String("""{"Plugins":[{"Name":"Other"}]}"""u8); }

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdschemaplugingroups/Vendor", group.ToJsonString()), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    [Test]
    public async Task AValidPluginRetainsExactManifestIdentityAndBothArtifactRoles()
    {
        var engine = CreateOpenUsd();
        await Send(engine, RegistryAction.Replace, "/usdschemaplugingroups/Vendor", Plugin().ToJsonString());
        var result = (await Send(engine, RegistryAction.Read, "/usdschemaplugingroups/Vendor")).Metadata!.RootElement;
        await Assert.That(result.GetProperty("name").GetString()).IsEqualTo("Vendor");
        await Assert.That(result.GetProperty("usdassetscount").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task PluginGroupsCanAliasTheSameResourceTypeDeclaredInAnAssetContainer()
    {
        var engine = CreateOpenUsd();
        var container = Group();
        var manifest = Artifact("plugInfo.json", "SchemaPlugin", "USD-PlugInfo/1.0");
        const string document = """{"Plugins":[{"Name":"Vendor","Type":"resource"}]}""";
        manifest["usdassetbase64"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(document));
        container["usdassets"]!["plugInfo.json"] = manifest;
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", container.ToJsonString());
        var plugin = Plugin();
        plugin["usdassets"]!["plugInfo.json"] = JsonNode.Parse("""
            {"meta":{"xref":"/usdassetgroups/g/usdassets/plugInfo.json"}}
            """);
        await Send(engine, RegistryAction.Replace, "/usdschemaplugingroups/Vendor", plugin.ToJsonString());

        var metadata = (await Send(engine, RegistryAction.Read, "/usdschemaplugingroups/Vendor/usdassets/plugInfo.json$details"))
            .Metadata!.RootElement;
        await Assert.That(metadata.GetProperty("xid").GetString()).IsEqualTo("/usdschemaplugingroups/Vendor/usdassets/plugInfo.json");
        await Assert.That(metadata.GetProperty("assetidentifier").GetString()).IsEqualTo("plugInfo.json");
        var result = await Send(engine, RegistryAction.Read, "/usdschemaplugingroups/Vendor/usdassets/plugInfo.json");
        using var stream = result.Document!.OpenRead();
        using var reader = new StreamReader(stream);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo(document);
    }

    internal static JsonObject Artifact(string identifier, string kind, string format) => new()
    {
        ["name"] = identifier,
        ["assetidentifier"] = identifier,
        ["assetkind"] = kind,
        ["format"] = format,
        ["contenttype"] = "application/octet-stream",
        ["usdassetbase64"] = "",
    };

    internal static JsonObject Group() => new()
    {
        ["name"] = "g",
        ["rootlayer"] = "scene.usda",
        ["usdassets"] = new JsonObject { ["scene.usda"] = Artifact("scene.usda", "RootLayer", "OpenUSD/1.0") },
    };

    private static JsonObject Plugin()
    {
        var manifest = Artifact("plugInfo.json", "SchemaPlugin", "USD-PlugInfo/1.0");
        manifest["usdassetbase64"] = Convert.ToBase64String("""{"Plugins":[{"Name":"Vendor","Type":"resource"}]}"""u8);
        return new()
        {
            ["name"] = "Vendor",
            ["usdassets"] = new JsonObject
            {
                ["plugInfo.json"] = manifest,
                ["generatedSchema.usda"] = Artifact("generatedSchema.usda", "GeneratedSchema", "USD-GeneratedSchema/1.0"),
            },
        };
    }

    internal static async Task Seed(RegistryEngine engine) =>
        await Send(engine, RegistryAction.Replace, "/usdassetgroups/g", Group().ToJsonString());

    internal static RegistryEngine CreateOpenUsd(InMemoryRegistryPersistence? persistence = null,
        IRegistryResourceValidator? validator = null, IRegistryAuthorizationPolicy? authorization = null) => new(new()
        {
            RegistryId = "openusd-host",
            PublicRoot = new Uri("https://registry.example/openusd"),
            Model = BuiltInRegistryModels.Compile(RegistryModelKind.OpenUsd),
            ResourceValidator = validator,
        }, persistence ?? new InMemoryRegistryPersistence(), authorization ?? new PermitPolicy());

    private sealed class RecordingValidator : IRegistryResourceValidator
    {
        internal int Calls { get; set; }
        internal List<string> FormatsSeen { get; } = [];
        public IReadOnlyList<string> Formats => ["OpenUSD/1.0", "Opaque/1.0", "ImageTest/1.0"];
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Compatibilities { get; } =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["ImageTest/1.0"] = new[] { "backward" } };

        public ValueTask<IReadOnlyDictionary<string, RegistryVersionValidation>> ValidateAsync(
            RegistryResourceValidationContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            var result = new Dictionary<string, RegistryVersionValidation>(StringComparer.Ordinal);
            foreach (var version in context.Versions)
            {
                var format = version.Metadata.RootElement.GetProperty("format").GetString()!;
                FormatsSeen.Add(format);
                result.Add(version.VersionId, new(format == "Opaque/1.0" ? RegistryValidationStatus.Invalid : RegistryValidationStatus.Valid,
                    RegistryValidationStatus.Valid));
            }
            return ValueTask.FromResult<IReadOnlyDictionary<string, RegistryVersionValidation>>(result);
        }
    }

    private sealed class ReadPolicy : IRegistryAuthorizationPolicy
    {
        internal bool DenyVersionReads { get; set; }
        internal bool DenyAllReads { get; set; }
        internal bool DenyBorrowedReads { get; set; }
        internal int Denied { get; private set; }

        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            var denied = access == RegistryAccess.Read && (DenyAllReads || DenyVersionReads && path.Kind == RegistryPathKind.Version ||
                DenyBorrowedReads && path.EscapedPath.StartsWith("/usdassetgroups/borrowed", StringComparison.Ordinal));
            if (denied) { Denied++; }
            return ValueTask.FromResult(!denied);
        }
    }
}
