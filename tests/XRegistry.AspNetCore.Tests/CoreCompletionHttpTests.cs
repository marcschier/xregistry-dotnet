// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.AspNetCore.Tests;

public class CoreCompletionHttpTests
{
    [Test]
    [Arguments("uri")]
    [Arguments("url")]
    public async Task AbsoluteTargetValuesCanBePublishedOverOrdinaryHttpWithoutAHostPolicy(string type)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,
              "attributes":{"reference":{"type":"$TYPE","target":"/gs/rs/versions"}}}}}}}
            """.Replace("$TYPE", type, StringComparison.Ordinal)));
        await using var host = await HttpTests.TestHost.StartAsync(model: model);
        using var response = await host.Client.PutAsync("/gs/g/rs/item",
            Json("""{"reference":"https://outside.invalid/unrelated/path"}"""));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var metadata = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync());
        await Assert.That(metadata.RootElement.GetProperty("reference").GetString())
            .IsEqualTo("https://outside.invalid/unrelated/path");
    }

    [Test]
    [Arguments("PUT", "/teams/g/notes/alias", """{"meta":{}}""")]
    [Arguments("POST", "/teams/g/notes", """{"alias":{"meta":{}}}""")]
    public async Task NestedMetaReplacementUnlinksOnlyTheSelectedAliasOverHttp(string method, string path, string body)
    {
        await using var host = await HttpTests.TestHost.StartAsync();
        using var targetCreated = await host.Client.PutAsync("/teams/g/notes/target", Json("""{"name":"target"}"""));
        await Assert.That(targetCreated.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var aliasCreated = await host.Client.PutAsync("/teams/g/notes/alias",
            Json("""{"meta":{"xref":"/teams/g/notes/target"}}"""));
        await Assert.That(aliasCreated.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var beforeTarget = await host.Client.GetAsync("/teams/g/notes/target");
        var before = await beforeTarget.Content.ReadAsStringAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = Json(body) };
        using var changed = await host.Client.SendAsync(request);
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var metaResponse = await host.Client.GetAsync("/teams/g/notes/alias/meta");
        var meta = RegistryJson.Parse(await metaResponse.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(meta.TryGetProperty("xref", out _)).IsFalse();
        using var resourceResponse = await host.Client.GetAsync("/teams/g/notes/alias");
        var resource = RegistryJson.Parse(await resourceResponse.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(resource.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        await Assert.That(resource.TryGetProperty("name", out _)).IsFalse();
        using var targetResponse = await host.Client.GetAsync("/teams/g/notes/target");
        await Assert.That(await targetResponse.Content.ReadAsStringAsync()).IsEqualTo(before);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
