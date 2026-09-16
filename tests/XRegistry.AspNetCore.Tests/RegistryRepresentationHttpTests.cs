// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Claims;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class RegistryRepresentationHttpTests
{
    [Test]
    [Arguments("", false, false)]
    [Arguments("", true, false)]
    [Arguments("", false, true)]
    [Arguments("", true, true)]
    [Arguments("/teams/g", false, false)]
    [Arguments("/teams/g", true, false)]
    [Arguments("/teams/g", false, true)]
    [Arguments("/teams/g", true, true)]
    public async Task MappedCollectionsOnlyResponseKeepsNestedShortSelf(string path, bool shortSelf, bool doc)
    {
        await using var host = await HttpTests.TestHost.StartAsync(allowCapabilityUpdates: true,
            httpOptions: new() { MountPath = "/catalog" });
        using var configured = await host.Client.PatchAsync("/catalog/capabilities",
            Json(shortSelf ? """{"shortself":true}""" : """{"shortself":false}"""));
        await Assert.That(configured.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var note = await host.Client.PutAsync("/catalog/teams/g/notes/n", Json("""{"name":"Nested note"}"""));
        await Assert.That(note.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var file = await host.Client.PutAsync("/catalog/teams/g/files/f$details", Json("""
            {"contenttype":"application/json","file":{"shortself":"domain short","formatvalidatedreason":"domain reason"}}
            """));
        await Assert.That(file.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var response = await host.Client.GetAsync("/catalog" + path + (doc ? "?collections&doc" : "?collections"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var top = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(top.EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(path.Length == 0 ? ["teams"] : ["notes", "files"], StringComparer.Ordinal);
        var group = path.Length == 0 ? top.GetProperty("teams").GetProperty("g") : top;
        if (path.Length == 0)
        {
            await Assert.That(group.GetProperty("teamid").GetString()).IsEqualTo("g");
            await Assert.That(group.TryGetProperty("shortself", out _)).IsEqualTo(shortSelf && !doc);
        }
        var resource = group.GetProperty("notes").GetProperty("n");
        await Assert.That(resource.TryGetProperty("shortself", out _)).IsEqualTo(shortSelf && !doc);
        await Assert.That(resource.GetProperty("meta").TryGetProperty("shortself", out _)).IsEqualTo(shortSelf && !doc);
        var version = resource.GetProperty("versions").GetProperty("1");
        await Assert.That(version.TryGetProperty("shortself", out _)).IsEqualTo(shortSelf && !doc);
        await Assert.That(version.GetProperty("name").GetString()).IsEqualTo("Nested note");
        var documentResource = group.GetProperty("files").GetProperty("f");
        var document = (doc ? documentResource.GetProperty("versions").GetProperty("1") : documentResource).GetProperty("file");
        await Assert.That(document.GetProperty("shortself").GetString()).IsEqualTo("domain short");
        await Assert.That(document.GetProperty("formatvalidatedreason").GetString()).IsEqualTo("domain reason");
        await Assert.That(response.Headers.GetValues("Link").Single()).IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
    }

    [Test]
    [Arguments("/teams/g/files/f/versions/1", "")]
    [Arguments("/teams/g/files/f/versions", "1")]
    [Arguments("/teams/g/files/f", "versions/1")]
    [Arguments("", "teams/g/files/f/versions/1")]
    public async Task MappedDocVersionsOmitReasonsWithoutChangingApiValidation(string path, string location)
    {
        await using var host = await HttpTests.TestHost.StartAsync(model: ValidatingModel,
            httpOptions: new() { MountPath = "/catalog" });
        using var created = await host.Client.PutAsync("/catalog/teams/g/files/f$details", Json("""
            {"format":"unregistered/v1","contenttype":"application/octet-stream",
             "filebase64":"AP8=","meta":{"compatibility":"backward"}}
            """));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        const string versionPath = "/catalog/teams/g/files/f/versions/1$details";
        using var beforeResponse = await host.Client.GetAsync(versionPath);
        await Assert.That(beforeResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var before = RegistryJson.Parse(await beforeResponse.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(before.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(before.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(before.GetProperty("formatvalidatedreason").GetString()).IsNotNullOrEmpty();
        await Assert.That(before.GetProperty("compatibilityvalidatedreason").GetString()).IsNotNullOrEmpty();

        using var response = await host.Client.GetAsync("/catalog" + path + "?doc&inline=*");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        var value = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        foreach (var part in location.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            value = value.GetProperty(part);
        }
        await Assert.That(value.TryGetProperty("formatvalidated", out _)).IsFalse();
        await Assert.That(value.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(value.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        await Assert.That(value.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
        await Assert.That(value.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(value.GetProperty("filebase64").GetString()).IsEqualTo("AP8=");
        using var afterResponse = await host.Client.GetAsync(versionPath);
        await Assert.That(RegistryJson.Parse(await afterResponse.Content.ReadAsByteArrayAsync()).RootElement.GetRawText())
            .IsEqualTo(before.GetRawText());
        using var bytes = await host.Client.GetAsync("/catalog/teams/g/files/f/versions/1");
        await Assert.That(bytes.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await bytes.Content.ReadAsByteArrayAsync())).IsEqualTo("00FF");
    }

    [Test]
    public async Task MappedDocReadsStillRequireVersionAuthorization()
    {
        var policy = new VersionPolicy();
        await using var host = await HttpTests.TestHost.StartAsync(model: ValidatingModel, policy: policy);
        using var created = await host.Client.PutAsync("/teams/g/files/f$details", Json("""
            {"format":"unregistered/v1","filebase64":"AP8=","meta":{"compatibility":"backward"}}
            """));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        policy.HideVersions = true;
        using var denied = await host.Client.GetAsync("/teams/g/files/f/versions/1?doc");
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var problem = RegistryJson.Parse(await denied.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("forbidden");
        await Assert.That(problem.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(problem.TryGetProperty("filebase64", out _)).IsFalse();
    }

    [Test]
    public async Task MappedCollectionsProjectionCannotBypassTheResponseByteBudget()
    {
        var persistence = new InMemoryRegistryPersistence();
        await using (var setup = await HttpTests.TestHost.StartAsync(persistence: persistence, allowCapabilityUpdates: true))
        {
            using var configured = await setup.Client.PatchAsync("/capabilities", Json("""{"shortself":true}"""));
            await Assert.That(configured.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var created = await setup.Client.PutAsync("/teams/g/notes/n", Json("""{"name":"Retained note"}"""));
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }
        await using var host = await HttpTests.TestHost.StartAsync(persistence: persistence,
            allowCapabilityUpdates: true, limits: new() { MaxResponseBytes = 128 });
        using var rejected = await host.Client.GetAsync("/?collections");
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.NotAcceptable);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("too_large");
        await Assert.That(problem.TryGetProperty("teams", out _)).IsFalse();
        await Assert.That(rejected.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
    }

    private static RegistryModel ValidatingModel => RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"teams":{"singular":"team","resources":{"files":{
          "singular":"file","validateformat":true,"validatecompatibility":true
        }}}}}
        """));

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private sealed class VersionPolicy : IRegistryAuthorizationPolicy
    {
        internal bool HideVersions { get; set; }
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(!HideVersions || access != RegistryAccess.Read || path.Kind != RegistryPathKind.Version);
    }
}
