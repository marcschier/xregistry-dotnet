// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.RegistryCapabilityTests;

namespace XRegistry.Server.Tests;

public class RegistryRepresentationTests
{
    [Test]
    public async Task IncludedEmptyCollectionsAreSerializedAsEmptyMaps()
    {
        var engine = Create();
        var root = (await Send(engine, RegistryAction.Read, "/", null, new KeyValuePair<string, string?>("inline", "*"))).Metadata!.RootElement;
        await Assert.That(root.GetProperty("teams").EnumerateObject().Any()).IsFalse();
        await Assert.That(root.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("teamsurl").GetString()).IsEqualTo("https://registry.example/catalog/teams");

        await Send(engine, RegistryAction.Replace, "/teams/g", "{}");
        var group = (await Send(engine, RegistryAction.Read, "/teams/g", null, new KeyValuePair<string, string?>("inline", "*"))).Metadata!.RootElement;
        foreach (var collection in new[] { "notes", "files" })
        {
            await Assert.That(group.GetProperty(collection).EnumerateObject().Any()).IsFalse();
            await Assert.That(group.GetProperty(collection + "count").GetInt32()).IsEqualTo(0);
            await Assert.That(group.GetProperty(collection + "url").GetString())
                .IsEqualTo("https://registry.example/catalog/teams/g/" + collection);
        }
    }

    [Test]
    public async Task GroupRequiredMetadataUsesCanonicalEntityValuesInBothViews()
    {
        var engine = MutableEngine();
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":true}""");
        var api = (await Send(engine, RegistryAction.Replace, "/teams/g", """
            {"self":"https://forged.example/group","shortself":"forged","xid":"/forged","epoch":99}
            """)).Metadata!.RootElement;

        await Assert.That(api.GetProperty("teamid").GetString()).IsEqualTo("g");
        await Assert.That(api.GetProperty("self").GetString()).IsEqualTo("https://registry.example/catalog/teams/g");
        await Assert.That(api.GetProperty("shortself").GetString()).IsNotNullOrEmpty();
        await Assert.That(api.GetProperty("shortself").GetString()).IsNotEqualTo("forged");
        await Assert.That(api.GetProperty("xid").GetString()).IsEqualTo("/teams/g");
        await Assert.That(api.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That(api.GetProperty("createdat").GetString()).IsNotNullOrEmpty();
        await Assert.That(api.GetProperty("modifiedat").GetString()).IsNotNullOrEmpty();

        var doc = (await Send(engine, RegistryAction.Read, "/teams/g", null,
            new KeyValuePair<string, string?>("doc", null))).Metadata!.RootElement;
        await Assert.That(doc.GetProperty("teamid").GetString()).IsEqualTo("g");
        await Assert.That(doc.GetProperty("self").GetString()).IsEqualTo("#/");
        await Assert.That(doc.TryGetProperty("shortself", out _)).IsFalse();
        await Assert.That(doc.GetProperty("xid").GetString()).IsEqualTo("/teams/g");
        await Assert.That(doc.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That(doc.GetProperty("createdat").GetString()).IsEqualTo(api.GetProperty("createdat").GetString());
        await Assert.That(doc.GetProperty("modifiedat").GetString()).IsEqualTo(api.GetProperty("modifiedat").GetString());

        var updated = (await Send(engine, RegistryAction.Patch, "/teams/g", """{"name":"updated"}""")).Metadata!.RootElement;
        await Assert.That(updated.GetProperty("epoch").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task CollectionsOnlyRootDoesNotExposeItsShortSelf()
    {
        var engine = MutableEngine();
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":true}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"Nested note"}""");

        var response = await Send(engine, RegistryAction.Read, "/", null, new KeyValuePair<string, string?>("collections", null));
        var root = response.Metadata!.RootElement;
        await Assert.That(root.EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["teams"], StringComparer.Ordinal);
        var group = root.GetProperty("teams").GetProperty("g");
        await Assert.That(group.GetProperty("shortself").GetString()).IsNotNullOrEmpty();
        var resource = group.GetProperty("notes").GetProperty("n");
        await Assert.That(resource.GetProperty("name").GetString()).IsEqualTo("Nested note");
        await Assert.That(resource.GetProperty("shortself").GetString()).IsNotNullOrEmpty();
    }

    [Test]
    public async Task DocVersionOmitsValidationReasonsButApiRetainsThem()
    {
        var engine = Create("""
            {"groups":{"teams":{"singular":"team","resources":{"files":{
              "singular":"file","validateformat":true,"validatecompatibility":true
            }}}}}
            """);
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """
            {"format":"unregistered/v1","filebase64":"AP8=","meta":{"compatibility":"backward"}}
            """);
        const string path = "/teams/g/files/f/versions/1$details";
        var api = (await Send(engine, RegistryAction.Read, path)).Metadata!.RootElement;
        await Assert.That(api.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(api.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(api.GetProperty("formatvalidatedreason").GetString()).IsNotNullOrEmpty();
        await Assert.That(api.GetProperty("compatibilityvalidatedreason").GetString()).IsNotNullOrEmpty();

        var doc = (await Send(engine, RegistryAction.Read, path, null, new KeyValuePair<string, string?>("doc", null)))
            .Metadata!.RootElement;
        await Assert.That(doc.TryGetProperty("formatvalidated", out _)).IsFalse();
        await Assert.That(doc.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        await Assert.That(doc.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(doc.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
        await Assert.That(doc.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(doc.GetProperty("self").GetString()).IsEqualTo("#/");
        await Assert.That((await Send(engine, RegistryAction.Read, path)).Metadata!.RootElement.GetRawText())
            .IsEqualTo(api.GetRawText());
    }

    [Test]
    [Arguments("/", false, false)]
    [Arguments("/", true, false)]
    [Arguments("/", false, true)]
    [Arguments("/", true, true)]
    [Arguments("/teams/g", false, false)]
    [Arguments("/teams/g", true, false)]
    [Arguments("/teams/g", false, true)]
    [Arguments("/teams/g", true, true)]
    public async Task CollectionsProjectionKeepsOnlyNestedEntityShortSelf(string path, bool shortSelf, bool doc)
    {
        var engine = MutableEngine();
        await Send(engine, RegistryAction.Patch, "/capabilities", shortSelf ? """{"shortself":true}""" : """{"shortself":false}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"Nested note"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """
            {"contenttype":"application/json","file":{"shortself":"opaque short","formatvalidatedreason":"opaque reason"}}
            """);
        KeyValuePair<string, string?>[] flags = doc ? [new("collections", null), new("doc", null)] : [new("collections", null)];
        var top = (await Send(engine, RegistryAction.Read, path, null, flags)).Metadata!.RootElement;
        await Assert.That(top.EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(path == "/" ? ["teams"] : ["notes", "files"], StringComparer.Ordinal);
        var group = path == "/" ? top.GetProperty("teams").GetProperty("g") : top;
        if (path == "/")
        {
            await Assert.That(group.GetProperty("teamid").GetString()).IsEqualTo("g");
            await Assert.That(group.TryGetProperty("shortself", out _)).IsEqualTo(shortSelf && !doc);
        }
        var resource = group.GetProperty("notes").GetProperty("n");
        await Assert.That(resource.GetProperty("xid").GetString()).IsEqualTo("/teams/g/notes/n");
        await Assert.That(resource.TryGetProperty("shortself", out _)).IsEqualTo(shortSelf && !doc);
        await Assert.That(resource.GetProperty("meta").TryGetProperty("shortself", out _)).IsEqualTo(shortSelf && !doc);
        var version = resource.GetProperty("versions").GetProperty("1");
        await Assert.That(version.GetProperty("xid").GetString()).IsEqualTo("/teams/g/notes/n/versions/1");
        await Assert.That(version.TryGetProperty("shortself", out _)).IsEqualTo(shortSelf && !doc);
        await Assert.That(version.GetProperty("name").GetString()).IsEqualTo("Nested note");
        var file = group.GetProperty("files").GetProperty("f");
        var content = (doc ? file.GetProperty("versions").GetProperty("1") : file).GetProperty("file");
        await Assert.That(content.GetProperty("shortself").GetString()).IsEqualTo("opaque short");
        await Assert.That(content.GetProperty("formatvalidatedreason").GetString()).IsEqualTo("opaque reason");
    }

    [Test]
    [Arguments("/teams/g/files/f/versions/1$details", "")]
    [Arguments("/teams/g/files/f/versions", "1")]
    [Arguments("/teams/g/files/f$details", "versions/1")]
    [Arguments("/", "teams/g/files/f/versions/1")]
    public async Task DocProjectionRemovesValidationReasonsAtEveryVersionDepth(string path, string location)
    {
        var engine = Create("""
            {"groups":{"teams":{"singular":"team","resources":{"files":{
              "singular":"file","validateformat":true,"validatecompatibility":true
            }}}}}
            """);
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """
            {"format":"unregistered/v1","contenttype":"application/octet-stream",
             "filebase64":"AP8=","meta":{"compatibility":"backward"}}
            """);
        const string versionPath = "/teams/g/files/f/versions/1$details";
        var before = (await Send(engine, RegistryAction.Read, versionPath)).Metadata!.RootElement;
        var value = (await Send(engine, RegistryAction.Read, path, null, new("doc", null), new("inline", "*")))
            .Metadata!.RootElement;
        foreach (var segment in location.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            value = value.GetProperty(segment);
        }
        await Assert.That(value.TryGetProperty("formatvalidated", out _)).IsFalse();
        await Assert.That(value.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(value.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        await Assert.That(value.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
        await Assert.That(value.GetProperty("filebase64").GetString()).IsEqualTo("AP8=");
        await Assert.That(value.GetProperty("versionid").GetString()).IsEqualTo("1");
        var after = (await Send(engine, RegistryAction.Read, versionPath)).Metadata!.RootElement;
        await Assert.That(after.GetRawText()).IsEqualTo(before.GetRawText());
        await Assert.That(after.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(after.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(after.GetProperty("formatvalidatedreason").GetString()).IsNotNullOrEmpty();
        await Assert.That(after.GetProperty("compatibilityvalidatedreason").GetString()).IsNotNullOrEmpty();
    }
}
