// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class CatalogServerPresetTests
{
    private const string Entry = "/categories/public/registries/entry";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BuiltInServerPresetsRejectInvalidCatalogPublication(bool all)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreatePreset(all, store);
        using var before = await store.ReadSnapshotAsync();

        var exception = await Assert.That(async () => await Send(engine, RegistryAction.Replace, Entry,
            """{"xregurl":"relative/root"}""")).Throws<RegistryException>();

        await Assert.That(exception!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(exception.Diagnostic.Path).IsEqualTo(Entry + "/versions/1");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement
            .GetProperty("categoriescount").GetInt32()).IsEqualTo(0);

        var accepted = await Send(engine, RegistryAction.Replace, Entry, "{}");
        await Assert.That(accepted.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(accepted.Metadata.RootElement.TryGetProperty("xregurl", out _)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerPresetsRejectInvalidBindingDeclarationsWithoutRequiringResolution(bool all)
    {
        var engine = CreatePreset(all, new InMemoryRegistryPersistence());
        await ExpectCode(() => Send(engine, RegistryAction.Replace, Entry,
            """{"federationprofiles":[{"name":"git","endpoint":"https://git.example/repo","parameters":{"revision":"HEAD"}}]}"""),
            "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var accepted = await Send(engine, RegistryAction.Replace, Entry, """
            {"federationprofiles":[{"name":"git","endpoint":"https://git.example/repo",
              "parameters":{"revision":"refs/heads/main","path":""}},
              {"name":"future","endpoint":"urn:example:opaque","parameters":{"revision":false}}]}
            """);
        var profiles = accepted.Metadata!.RootElement.GetProperty("federationprofiles");
        await Assert.That(profiles.GetArrayLength()).IsEqualTo(2);
        await Assert.That(profiles[0].GetProperty("parameters").GetProperty("revision").GetString()).IsEqualTo("refs/heads/main");
        await Assert.That(profiles[0].GetProperty("parameters").GetProperty("path").GetString()).IsEqualTo("");
        await Assert.That(profiles[1].GetProperty("parameters").GetProperty("revision").GetBoolean()).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerPresetCompatibilityPersistsAcrossFrozenModelRestart(bool all)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreatePreset(all, store);
        await Send(engine, RegistryAction.Replace, Entry, """{"weburl":"about/catalog"}""");
        var source = (await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement;
        await Assert.That(source.GetProperty("groups").GetProperty("categories").GetProperty("resources")
            .GetProperty("registries").GetProperty("modelcompatiblewith").GetString())
            .IsEqualTo("https://xregistry.io/xreg/domains/registry/specs/model.json");
        var before = (await Send(engine, RegistryAction.Read, Entry)).Metadata!.RootElement.GetRawText();
        using var snapshot = await store.ReadSnapshotAsync();

        var restarted = CreateEngine(BuiltInRegistryModels.Compile(RegistryModelKind.Registry), store);
        await ExpectCode(() => Send(restarted, RegistryAction.Patch, Entry, """{"xregurl":"relative/root"}"""), "invalid_attribute");
        await Assert.That((await Send(restarted, RegistryAction.Read, Entry)).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(snapshot.Generation);
        await Assert.That((await restarted.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrdinaryCompilationDoesNotAcquireServerPresetRules(bool all)
    {
        var model = all ? BuiltInRegistryModels.CompileAll() : BuiltInRegistryModels.Compile(RegistryModelKind.Registry);
        var engine = CreateEngine(model, new InMemoryRegistryPersistence());
        var created = await Send(engine, RegistryAction.Replace, Entry, """{"xregurl":"relative/root","registrytypes":["schema"]}""");
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(created.Metadata!.RootElement.GetProperty("xregurl").GetString()).IsEqualTo("relative/root");
        await Assert.That(model.Groups["categories"].Resources["registries"].Annotations.ModelCompatibleWith).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CustomModelSourcesRequireAnExplicitCompatibilityClaim(bool compatible)
    {
        using var packaged = BuiltInRegistryModels.LoadSource(RegistryModelKind.Registry);
        var source = JsonNode.Parse(packaged.RootElement.GetRawText())!.AsObject();
        if (compatible)
        {
            source["groups"]!["categories"]!["resources"]!["registries"]!["modelcompatiblewith"] =
                "https://xregistry.io/xreg/domains/registry/specs/model.json";
        }
        var model = RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()),
            new() { SourceUri = new Uri("file:///D:/models/custom.json") });
        var engine = CreateEngine(model, new InMemoryRegistryPersistence());
        if (compatible)
        {
            await ExpectCode(() => Send(engine, RegistryAction.Replace, Entry, """{"xregurl":"relative/root"}"""), "invalid_attribute");
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        }
        else
        {
            var accepted = await Send(engine, RegistryAction.Replace, Entry, """{"xregurl":"relative/root"}""");
            await Assert.That(accepted.Metadata!.RootElement.GetProperty("xregurl").GetString()).IsEqualTo("relative/root");
            await Assert.That(model.Source.RootElement.GetProperty("groups").GetProperty("categories")
                .GetProperty("resources").GetProperty("registries").TryGetProperty("modelcompatiblewith", out _)).IsFalse();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExistingCustomStoreIsNotSilentlyOptedInByAServerPreset(bool all)
    {
        var store = new InMemoryRegistryPersistence();
        var original = CreateEngine(BuiltInRegistryModels.Compile(RegistryModelKind.Registry), store);
        await Send(original, RegistryAction.Replace, Entry, """{"xregurl":"relative/original"}""");
        var restarted = CreatePreset(all, store);
        var accepted = await Send(restarted, RegistryAction.Patch, Entry, """{"xregurl":"relative/updated"}""");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("xregurl").GetString()).IsEqualTo("relative/updated");
        var retainedSource = (await Send(restarted, RegistryAction.Read, "/modelsource")).Metadata!.RootElement;
        await Assert.That(retainedSource.GetProperty("groups").GetProperty("categories").GetProperty("resources")
            .GetProperty("registries").TryGetProperty("modelcompatiblewith", out _)).IsFalse();
    }

    private static RegistryEngine CreatePreset(bool all, IRegistryPersistence persistence) =>
        CreateEngine(all ? BuiltInRegistryModels.CompileAllForServer() :
            BuiltInRegistryModels.CompileForServer(RegistryModelKind.Registry), persistence);

    private static RegistryEngine CreateEngine(RegistryModel model, IRegistryPersistence persistence) =>
        new(new()
        {
            RegistryId = "catalog-server-preset",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = model
        }, persistence, new PermitPolicy());
}
