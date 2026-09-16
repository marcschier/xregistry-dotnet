// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.CatalogPublicationTests;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class CatalogBindingPublicationTests
{
    private const string Entry = "/categories/public/registries/bindings";

    [Test]
    [Arguments("http", "ftp://registry.example/root", "https://registry.example/root", null)]
    [Arguments("http", "https://registry.example/root?", "https://registry.example/root", null)]
    [Arguments("http", "https://registry.example/root#", "https://registry.example/root", null)]
    [Arguments("git", "http://git.example/repo", "https://git.example/repo", """{"revision":"refs/heads/main"}""")]
    [Arguments("git", "ssh://git.example/repo", "https://git.example/repo", """{"revision":"refs/heads/main"}""")]
    [Arguments("git", "https://git.example/repo?ref=main", "https://git.example/repo", """{"revision":"refs/heads/main"}""")]
    [Arguments("oci", "https://artifacts.example/team/repo", "oci://artifacts.example/team/repo", """{"reference":"stable"}""")]
    [Arguments("oci", "urn:x", "oci://artifacts.example/team/repo", """{"reference":"stable"}""")]
    [Arguments("oci", "oci://artifacts.example", "oci://artifacts.example/team/repo", """{"reference":"stable"}""")]
    [Arguments("oci", "oci://artifacts.example/team/repo:tag", "oci://artifacts.example/team/repo", """{"reference":"stable"}""")]
    [Arguments("oci", "oci://artifacts.example/Team/repo", "oci://artifacts.example/team/repo", """{"reference":"stable"}""")]
    [Arguments("oci", "oci://artifacts.example/team//repo", "oci://artifacts.example/team/repo", """{"reference":"stable"}""")]
    [Arguments("oci", "oci://artifacts.example/team/../repo", "oci://artifacts.example/team/repo", """{"reference":"stable"}""")]
    [Arguments("oci", "oci://artifacts.example/team/%72epo", "oci://artifacts.example/team/repo", """{"reference":"stable"}""")]
    [Arguments("file", "https://files.example/catalog", "file:///catalog", """{"layout":"document-tree"}""")]
    [Arguments("file", "file:///catalog?", "file:///catalog", """{"layout":"document-tree"}""")]
    [Arguments("file", "file:///catalog#", "file:///catalog", """{"layout":"document-tree"}""")]
    public async Task BuiltInEndpointsRequireTheirDeclaredTransport(
        string name, string invalidEndpoint, string validEndpoint, string? parameters)
    {
        await RejectAndAccept(Description(name, invalidEndpoint, parameters),
            Description(name, validEndpoint, parameters), "/federationprofiles/0/endpoint");
    }

    [Test]
    [Arguments("git", null, """{"revision":"refs/heads/main"}""", "revision")]
    [Arguments("git", "{}", """{"revision":"refs/heads/main"}""", "revision")]
    [Arguments("git", """{"revision":""}""", """{"revision":"refs/heads/main"}""", "revision")]
    [Arguments("git", """{"revision":17}""", """{"revision":"refs/heads/main"}""", "revision")]
    [Arguments("git", """{"revision":"HEAD"}""", """{"revision":"refs/heads/main"}""", "revision")]
    [Arguments("git", """{"revision":"main"}""", """{"revision":"refs/heads/main"}""", "revision")]
    [Arguments("git", """{"revision":"refs/heads/a..b"}""", """{"revision":"refs/heads/a.b"}""", "revision")]
    [Arguments("git", """{"future":true}""", """{"revision":"refs/heads/main","future":true}""", "revision")]
    [Arguments("git", """{"revision":"refs/heads/main","path":"../root"}""", """{"revision":"refs/heads/main","path":""}""", "path")]
    [Arguments("git", """{"revision":"refs/heads/main","path":"a//b"}""", """{"revision":"refs/heads/main","path":"a/b"}""", "path")]
    [Arguments("git", """{"revision":"refs/heads/main","path":"C:/root"}""", """{"revision":"refs/heads/main","path":"root"}""", "path")]
    [Arguments("git", """{"revision":"refs/heads/main","path":"con.txt"}""", """{"revision":"refs/heads/main","path":"catalog.txt"}""", "path")]
    [Arguments("git", """{"revision":"refs/heads/main","path":false}""", """{"revision":"refs/heads/main","path":""}""", "path")]
    [Arguments("oci", null, """{"reference":"stable"}""", "reference")]
    [Arguments("oci", """{"reference":""}""", """{"reference":"stable"}""", "reference")]
    [Arguments("oci", """{"reference":"bad/tag"}""", """{"reference":"stable"}""", "reference")]
    [Arguments("oci", """{"reference":false}""", """{"reference":"stable"}""", "reference")]
    [Arguments("file", null, """{"layout":"document-tree"}""", "layout")]
    [Arguments("file", """{"layout":"guess"}""", """{"layout":"document-tree"}""", "layout")]
    [Arguments("file", """{"layout":false}""", """{"layout":"document-tree"}""", "layout")]
    [Arguments("file", """{"layout":"document-tree","reference":"stable"}""", """{"layout":"document-tree"}""", "reference")]
    [Arguments("file", """{"layout":"oci-layout"}""", """{"layout":"oci-layout","reference":"stable"}""", "reference")]
    [Arguments("file", """{"layout":"oci-layout","reference":"bad/tag"}""", """{"layout":"oci-layout","reference":"stable"}""", "reference")]
    public async Task KnownParametersAreValidatedEvenWhenAnotherAdvertisementWouldBeSelected(
        string name, string? invalid, string valid, string field)
    {
        var endpoint = Endpoint(name);
        await RejectAndAccept(Description(name, endpoint, invalid), Description(name, endpoint, valid),
            "/federationprofiles/0/parameters/" + field);
    }

    [Test]
    [Arguments("http", """{"future":{"nested":[1,true,null]}}""")]
    [Arguments("git", """{"revision":"refs/heads/main","future":{"path":"../opaque"}}""")]
    [Arguments("oci", """{"reference":"stable","future":17}""")]
    [Arguments("file", """{"layout":"oci-layout","reference":"stable","future":false}""")]
    public async Task UnknownBuiltInParametersAreRetainedRatherThanConferringSupport(string name, string parameters)
    {
        var engine = Create(CatalogModelSource());
        var description = Description(name, Endpoint(name), parameters);
        await Send(engine, RegistryAction.Replace, Entry, description);
        var stored = (await Send(engine, RegistryAction.Read, Entry + "/versions/1")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("federationprofiles")[0].GetProperty("parameters").GetRawText())
            .IsEqualTo(RegistryJson.Parse(parameters).RootElement.GetRawText());
    }

    [Test]
    [Arguments("GIT", "https://git.example/repo?opaque=1")]
    [Arguments("OCI", "oci://artifacts.example/opaque:tag")]
    [Arguments("HTTP", "urn:example:unknown")]
    [Arguments("opcua", "opc.tcp://ua.example:4840/")]
    public async Task FutureBindingsAndOpcUaRemainOpaqueCatalogData(string name, string endpoint)
    {
        var engine = Create(CatalogModelSource());
        var created = await Send(engine, RegistryAction.Replace, Entry,
            Description(name, endpoint, """{"revision":false,"registryroot":[],"reference":{"opaque":true}}"""));
        var advertisement = created.Metadata!.RootElement.GetProperty("federationprofiles")[0];
        await Assert.That(advertisement.GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(advertisement.GetProperty("endpoint").GetString()).IsEqualTo(endpoint);
        await Assert.That(advertisement.GetProperty("parameters").GetRawText())
            .IsEqualTo("""{"revision":false,"registryroot":[],"reference":{"opaque":true}}""");
    }

    private static async Task RejectAndAccept(string invalid, string valid, string attributePath)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(CatalogModelSource(), store);
        using var before = await store.ReadSnapshotAsync();
        var exception = await Assert.That(async () => await Send(engine, RegistryAction.Replace, Entry, invalid))
            .Throws<RegistryException>();
        await Assert.That(exception!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        var cause = exception.InnerException as RegistryException
            ?? throw new InvalidOperationException("The write must retain its precise metadata diagnostic.");
        await Assert.That(cause.Diagnostic.Path).IsEqualTo(attributePath);
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var accepted = await Send(engine, RegistryAction.Replace, Entry, valid);
        await Assert.That(accepted.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("federationprofiles").GetRawText())
            .IsEqualTo(RegistryJson.Parse(valid).RootElement.GetProperty("federationprofiles").GetRawText());
    }

    private static string Endpoint(string name) => name switch
    {
        "http" => "https://registry.example/root",
        "git" => "https://git.example/repo",
        "oci" => "oci://artifacts.example:5000/team/repo",
        "file" => "file:///catalog",
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static string Description(string name, string endpoint, string? parameters)
    {
        var advertisement = new JsonObject { ["name"] = name, ["endpoint"] = endpoint, ["priority"] = 9 };
        if (parameters is not null)
        {
            advertisement["parameters"] = JsonNode.Parse(parameters);
        }
        return new JsonObject
        {
            ["federationprofiles"] = new JsonArray(advertisement,
                new JsonObject { ["name"] = "http", ["endpoint"] = "https://preferred.example/root" })
        }.ToJsonString();
    }
}
