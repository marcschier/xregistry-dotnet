// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.AspNetCore.Tests;

public class RegistrySharedQueryHttpTests
{
    [Test]
    public async Task SharedLogicalFactsSurviveRawHttpDefaultMetaFilteringAndBinaryDocumentProjection()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"workspaces":{"singular":"workspace","resources":{"schemas":{"singular":"schema",
              "attributes":{"rank":{"type":"decimal"}}}}}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model, httpOptions: new() { MountPath = "/registry" });
        using var first = await host.Client.PutAsync("/registry/workspaces/g%3Aone/schemas/r$details",
            Json("""{"rank":9007199254740993,"contenttype":"text/plain","schema":"x"}"""));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var second = await host.Client.PostAsync("/registry/workspaces/g%3Aone/schemas/r$details",
            Json("""{"versionid":"2","rank":0,"contenttype":"text/plain","schema":"y"}"""));
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var sticky = await host.Client.PatchAsync("/registry/workspaces/g%3Aone/schemas/r/meta",
            Json("""{"defaultversionid":"1"}"""));
        await Assert.That(sticky.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/registry/workspaces/g%3aone/schemas?filter=rank%3E9007199254740992%2Cmeta.defaultversionid%3D1" +
            "&sort=rank%3Ddesc&inline=meta%2Cversions.schema&doc&binary");
        request.Headers.Host = "untrusted.invalid";
        using var response = await host.Client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.GetValues("xRegistry-count").Single()).IsEqualTo("1");
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.EnumerateObject().Single().Name).IsEqualTo("r");
        var resource = body.GetProperty("r");
        await Assert.That(resource.GetProperty("self").GetString()).IsEqualTo("#/r");
        await Assert.That(resource.TryGetProperty("rank", out _)).IsFalse();
        await Assert.That(resource.GetProperty("meta").GetProperty("defaultversionurl").GetString()).IsEqualTo("#/r/versions/1");
        await Assert.That(resource.GetProperty("versions").GetProperty("1").GetProperty("schemabase64").GetString()).IsEqualTo("eA==");
        await Assert.That(resource.GetProperty("versions").GetProperty("2").GetProperty("schemabase64").GetString()).IsEqualTo("eQ==");
        await Assert.That(response.Headers.GetValues("Link").Any(value =>
            value.Contains("<https://public.example/registry>;rel=xregistry-root", StringComparison.Ordinal))).IsTrue();

        using var invalid = await host.Client.GetAsync("/registry/workspaces/g%3Aone/schemas?sort=versions.rank");
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await invalid.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("bad_sort");
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
