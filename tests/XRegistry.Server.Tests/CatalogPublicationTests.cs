// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class CatalogPublicationTests
{
    private const string CatalogModelUri = "https://xregistry.io/xreg/domains/registry/specs/model.json";
    private const string Entry = "/categories/public/registries/entry";

    [Test]
    public async Task RelativeHttpRootIsRejectedBeforePublication()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(CatalogModelSource(), store);
        using var before = await store.ReadSnapshotAsync();

        var exception = await Assert.That(async () => await Send(engine, RegistryAction.Replace, Entry,
            """{"xregurl":"relative/root"}""")).Throws<RegistryException>();

        await Assert.That(exception!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(exception.Diagnostic.Path).IsEqualTo(Entry + "/versions/1");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        var root = (await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement;
        await Assert.That(root.GetProperty("categoriescount").GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);

        var accepted = await Send(engine, RegistryAction.Replace, Entry,
            """{"xregurl":"https://registry.example/root"}""");
        await Assert.That(accepted.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("xregurl").GetString())
            .IsEqualTo("https://registry.example/root");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
    }

    [Test]
    [Arguments("""{"xregurl":"file:///catalog"}""", """{"xregurl":"https://registry.example/root"}""", "/xregurl")]
    [Arguments("""{"xregurl":"https://registry.example/root?"}""", """{"xregurl":"https://registry.example/root"}""", "/xregurl")]
    [Arguments("""{"xregurl":"https://registry.example/root#"}""", """{"xregurl":"https://registry.example/root"}""", "/xregurl")]
    [Arguments("""{"xregurl":"https://user@registry.example/root"}""", """{"xregurl":"https://registry.example/root"}""", "/xregurl")]
    [Arguments("""{"xregurl":"https://@registry.example/root"}""", """{"xregurl":"https://registry.example/root"}""", "/xregurl")]
    [Arguments("""{"registrytypes":["schema"]}""", """{"registrytypes":["urn:example:schema"]}""", "/registrytypes/0")]
    [Arguments("""{"registrytypes":[""]}""", """{"registrytypes":["urn:example:schema"]}""", "/registrytypes/0")]
    [Arguments("""{"federationprofiles":[{"name":"","endpoint":"urn:example:catalog"}]}""",
        """{"federationprofiles":[{"name":"future","endpoint":"urn:example:catalog"}]}""", "/federationprofiles/0/name")]
    [Arguments("""{"federationprofiles":[{"name":"future","endpoint":"/relative"}]}""",
        """{"federationprofiles":[{"name":"future","endpoint":"urn:example:catalog"}]}""", "/federationprofiles/0/endpoint")]
    [Arguments("""{"federationprofiles":[{"name":"future","endpoint":"https://user@registry.example/root"}]}""",
        """{"federationprofiles":[{"name":"future","endpoint":"https://registry.example/root"}]}""", "/federationprofiles/0/endpoint")]
    [Arguments("""{"federationprofiles":[{"name":"future","endpoint":"https://@registry.example/root"}]}""",
        """{"federationprofiles":[{"name":"future","endpoint":"https://registry.example/root"}]}""", "/federationprofiles/0/endpoint")]
    [Arguments("""{"federationprofiles":[{"name":"future","endpoint":"urn:example:catalog","priority":0.5}]}""",
        """{"federationprofiles":[{"name":"future","endpoint":"urn:example:catalog","priority":0}]}""", "/federationprofiles/0/priority")]
    [Arguments("""{"relationships":[{"type":"","target":"/categories/public/registries/future"}]}""",
        """{"relationships":[{"type":"future","target":"/categories/public/registries/future"}]}""", "/relationships/0/type")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"../future"}]}""",
        """{"relationships":[{"type":"mirrors","target":"/categories/public/registries/future"}]}""", "/relationships/0/target")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"//remote.example/categories/public/registries/future"}]}""",
        """{"relationships":[{"type":"mirrors","target":"https://remote.example/categories/public/registries/future"}]}""", "/relationships/0/target")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"/categories/public"}]}""",
        """{"relationships":[{"type":"mirrors","target":"/categories/public/registries/future"}]}""", "/relationships/0/target")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"/categories/public/registries/future/versions/1"}]}""",
        """{"relationships":[{"type":"mirrors","target":"/categories/public/registries/future"}]}""", "/relationships/0/target")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"/categories/public/registries/future/meta"}]}""",
        """{"relationships":[{"type":"mirrors","target":"/categories/public/registries/future"}]}""", "/relationships/0/target")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"/teams/public/registries/future"}]}""",
        """{"relationships":[{"type":"mirrors","target":"/categories/public/registries/future"}]}""", "/relationships/0/target")]
    public async Task SuppliedCommonFieldsRejectBeforePublication(string invalid, string valid, string attributePath)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(CatalogModelSource(), store);
        using var before = await store.ReadSnapshotAsync();
        var exception = await Assert.That(async () => await Send(engine, RegistryAction.Replace, Entry, invalid))
            .Throws<RegistryException>();
        await Assert.That(exception!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(exception.Diagnostic.Path).IsEqualTo(Entry + "/versions/1");
        var cause = exception.InnerException as RegistryException
            ?? throw new InvalidOperationException("The write must retain its precise metadata diagnostic.");
        await Assert.That(cause.Diagnostic.Path).IsEqualTo(attributePath);
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement
            .GetProperty("categoriescount").GetInt32()).IsEqualTo(0);

        var accepted = await Send(engine, RegistryAction.Replace, Entry, valid);
        await Assert.That(accepted.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
        foreach (var property in RegistryJson.Parse(valid).RootElement.EnumerateObject())
        {
            await Assert.That(accepted.Metadata.RootElement.GetProperty(property.Name).GetRawText())
                .IsEqualTo(property.Value.GetRawText());
        }
    }

    [Test]
    [Arguments("https://registry.example/root/")]
    [Arguments("https://REGISTRY.example/root")]
    [Arguments("https://other.example/root")]
    public async Task HttpLocatorsUseExactAgreementWithoutNormalization(string different)
    {
        var engine = Create(CatalogModelSource());
        var exception = await Assert.That(async () => await Send(engine, RegistryAction.Replace, Entry, $$"""
            {"xregurl":"https://registry.example/root",
             "federationprofiles":[{"name":"http","endpoint":"{{different}}"}]}
            """)).Throws<RegistryException>();
        await Assert.That(exception!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var accepted = await Send(engine, RegistryAction.Replace, Entry, $$"""
            {"xregurl":"https://registry.example/root",
             "federationprofiles":[{"name":"http","endpoint":"{{different}}","priority":9},
               {"name":"http","endpoint":"https://registry.example/root","priority":3}]}
            """);
        var profiles = accepted.Metadata!.RootElement.GetProperty("federationprofiles");
        await Assert.That(profiles.GetArrayLength()).IsEqualTo(2);
        await Assert.That(profiles[0].GetProperty("endpoint").GetString()).IsEqualTo(different);
        await Assert.That(profiles[1].GetProperty("endpoint").GetString()).IsEqualTo("https://registry.example/root");
        await Assert.That(profiles[1].GetProperty("priority").GetInt32()).IsEqualTo(3);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"weburl":"about/registries"}""")]
    [Arguments("""{"labels":{},"registrytypes":[],"relationships":[],"federationprofiles":[]}""")]
    [Arguments("""{"authority":"organizations/team","registrytypes":["urn:example:model","urn:example:model"]}""")]
    [Arguments("""{"authority":"x:a","registrytypes":["x:a"],"federationprofiles":[{"name":"future","endpoint":"x:a"}]}""")]
    [Arguments("""{"federationprofiles":[{"name":"HTTP","endpoint":"urn:example:future","parameters":{"revision":false,"future":[1,{"nested":"data"}]}}]}""")]
    [Arguments("""{"relationships":[{"type":"future","target":"/categories/missing/registries/missing","labels":{"note":""}}]}""")]
    [Arguments("""{"extension":{"registrytypes":["schema"],"federationprofiles":[{"name":"","endpoint":"relative"}]},"federationprofiles":[{"name":" ","endpoint":"urn:example:extension"}],"relationships":[{"type":" ","target":"/categories/missing/registries/missing"}]}""")]
    public async Task BaseDescriptionsNeedNoResolvableAdvertisement(string metadata)
    {
        var engine = Create(CatalogModelSource());
        var created = await Send(engine, RegistryAction.Replace, Entry, metadata);
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        var read = (await Send(engine, RegistryAction.Read, Entry + "/versions/1")).Metadata!.RootElement;
        foreach (var property in RegistryJson.Parse(metadata).RootElement.EnumerateObject())
        {
            await Assert.That(read.GetProperty(property.Name).GetRawText()).IsEqualTo(property.Value.GetRawText());
        }
        await Assert.That(read.TryGetProperty("xregurl", out _)).IsFalse();
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
    }

    internal static string CatalogModelSource(bool compatible = true)
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Registry);
        var model = JsonNode.Parse(source.RootElement.GetRawText())!.AsObject();
        if (compatible)
        {
            model["groups"]!["categories"]!["resources"]!["registries"]!["modelcompatiblewith"] = CatalogModelUri;
        }
        return model.ToJsonString();
    }
}
