// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Federation;
using XRegistry.Samples.Bridge;

namespace XRegistry.Bridge.Tests;

public class BridgeRepresentationTests
{
    [Test]
    public async Task BridgeDocOmitsCapturedReasonsWhileApiAndOpaqueMetadataRemainIntact()
    {
        var source = new ControlledSource("one", "v1", ["a"]) { Producer = true };
        source.Override = async (request, _) =>
        {
            var read = await source.ReadDefault(request);
            if (request.Target != "/dirs/g/files/a/versions/v1")
            {
                return read;
            }
            var captured = JsonNode.Parse(read.Metadata.GetRawText())!.AsObject();
            var entity = captured["entity"]!.AsObject();
            entity["formatvalidated"] = false;
            entity["formatvalidatedreason"] = "Captured format was not checked.";
            entity["compatibilityvalidated"] = false;
            entity["compatibilityvalidatedreason"] = "Captured compatibility was not checked.";
            entity["domain"]!["formatvalidatedreason"] = "Opaque domain reason.";
            return FederationReadResult.FromMetadata(read.SelectedXid, read.Context, RegistryJson.Parse(captured.ToJsonString()).RootElement);
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        const string path = "/registry/dirs/g/files/a/versions/v1$details";
        using var apiResponse = await client.GetAsync(path);
        await Assert.That(apiResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var api = await BridgeFixture.Json(apiResponse);
        await Assert.That(api.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(api.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(api.GetProperty("formatvalidatedreason").GetString()).IsEqualTo("Captured format was not checked.");
        await Assert.That(api.GetProperty("compatibilityvalidatedreason").GetString()).IsEqualTo("Captured compatibility was not checked.");
        using var docResponse = await client.GetAsync(path + "?doc");
        await Assert.That(docResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var doc = await BridgeFixture.Json(docResponse);
        await Assert.That(doc.TryGetProperty("formatvalidated", out _)).IsFalse();
        await Assert.That(doc.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(doc.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        await Assert.That(doc.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
        await Assert.That(doc.GetProperty("domain").GetProperty("formatvalidatedreason").GetString()).IsEqualTo("Opaque domain reason.");
        using var after = await client.GetAsync(path);
        await Assert.That((await BridgeFixture.Json(after)).GetRawText()).IsEqualTo(api.GetRawText());
        await Assert.That(source.Reads.Any(static read => read.Operation == FederationOperation.Document)).IsFalse();
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    [Arguments(false, "", "")]
    [Arguments(true, "", "")]
    [Arguments(false, "?inline=*", "")]
    [Arguments(true, "?inline=*", "")]
    [Arguments(false, "?doc", "")]
    [Arguments(true, "?doc&inline=*", "")]
    [Arguments(false, "?inline=model", "model")]
    [Arguments(true, "?inline=modelsource", "modelsource")]
    [Arguments(false, "?inline=capabilities", "capabilities")]
    [Arguments(true, "?inline=model,modelsource,capabilities", "model,modelsource,capabilities")]
    [Arguments(false, "?doc&inline=model,modelsource,capabilities", "model,modelsource,capabilities")]
    public async Task BridgeRootInlineIsExplicitAndDoesNotAddSourceRequests(bool producerOwned, string query, string expected)
    {
        var source = new ControlledSource("one", "v1", ["a"]) { Producer = producerOwned };
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry" + query);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var root = await BridgeFixture.Json(response);
        await Assert.That(root.EnumerateObject().Select(static property => property.Name)
            .Where(static name => name is "model" or "modelsource" or "capabilities").ToArray())
            .IsEquivalentTo(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        await Assert.That(root.GetProperty("registryid").GetString()).IsEqualTo("bridge");
        await Assert.That(root.GetProperty("site").GetString()).IsEqualTo("https://one.example/domain");
        if (root.TryGetProperty("model", out var model))
        {
            await Assert.That(model.GetProperty("attributes").GetProperty("registryid").GetProperty("type").GetString()).IsEqualTo("string");
        }
        if (root.TryGetProperty("modelsource", out var declaration))
        {
            await Assert.That(declaration.GetProperty("attributes").GetProperty("site").GetProperty("type").GetString()).IsEqualTo("url");
        }
        if (root.TryGetProperty("capabilities", out var capabilities))
        {
            await Assert.That(capabilities.GetProperty("federation").GetProperty("resolution").GetString()).IsEqualTo("producer");
            await Assert.That(capabilities.GetProperty("available").GetProperty("entities").GetProperty("mutable").GetBoolean()).IsFalse();
        }
        await Assert.That(source.Reads.Any(static read => read.Target is "/model" or "/modelsource" or "/capabilities")).IsFalse();
        if (query is "" or "?doc" || expected.Length != 0)
        {
            await Assert.That(source.Reads.Select(static read => read.Operation + ":" + read.Target).ToArray())
                .IsEquivalentTo(producerOwned ? ["Entity:/"] : ["Entity:/", "Collection:/dirs"], StringComparer.Ordinal);
        }
        await Assert.That(source.Opens).IsEqualTo(1);
        await Assert.That(source.Closes).IsEqualTo(1);
    }

    [Test]
    [Arguments("resolved", "", false)]
    [Arguments("resolved", "?binary", false)]
    [Arguments("resolved", "?inline=meta", true)]
    [Arguments("resolved", "?inline=*", true)]
    [Arguments("resolved", "?doc", true)]
    [Arguments("missing", "", true)]
    [Arguments("missing", "?inline=meta", true)]
    [Arguments("missing", "?doc", true)]
    [Arguments("second", "", true)]
    [Arguments("second", "?doc&inline=*", true)]
    public async Task BridgeAliasInlinePreservesTheDanglingIdentityException(string state, string query, bool hasMeta)
    {
        var target = state switch
        {
            "missing" => "/dirs/g/files/missing",
            "second" => "/dirs/g/files/second",
            _ => "/dirs/g/files/real"
        };
        var source = AliasSource.Create("one", "v1",
            new Dictionary<string, string> { ["alias"] = target, ["second"] = "/dirs/g/files/real" });
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias$details" + query);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var value = await BridgeFixture.Json(response);
        await Assert.That(value.GetProperty("fileid").GetString()).IsEqualTo("alias");
        await Assert.That(value.GetProperty("xid").GetString()).IsEqualTo("/dirs/g/files/alias");
        await Assert.That(value.TryGetProperty("meta", out _)).IsEqualTo(hasMeta);
        if (hasMeta)
        {
            await Assert.That(value.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo(target);
            await Assert.That(value.GetProperty("meta").GetProperty("fileid").GetString()).IsEqualTo("alias");
        }
        if (state != "resolved" || query.Contains("doc", StringComparison.Ordinal))
        {
            await Assert.That(value.EnumerateObject().Select(static property => property.Name).ToArray())
                .IsEquivalentTo(["fileid", "self", "xid", "metaurl", "meta"], StringComparer.Ordinal);
            await Assert.That(value.GetProperty("meta").EnumerateObject().Select(static property => property.Name).ToArray())
                .IsEquivalentTo(["fileid", "self", "xid", "xref"], StringComparer.Ordinal);
            await Assert.That(source.Reads.Any(static read => read.Operation == FederationOperation.Document)).IsFalse();
        }
        else
        {
            await Assert.That(value.GetProperty("versionid").GetString()).IsEqualTo("v1");
            await Assert.That(value.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
            await Assert.That(value.GetProperty("domain").GetProperty("self").GetString()).IsEqualTo("https://one.example/opaque");
        }
        if (state == "resolved" && query.Length == 0)
        {
            await Assert.That(source.Reads.Select(static read => read.Operation + ":" + read.Target).ToArray())
                .IsEquivalentTo(["Entity:/dirs/g/files/alias", "Entity:/dirs/g/files/real",
                    "Entity:/dirs/g/files/real/meta", "Collection:/dirs/g/files/real/versions"], StringComparer.Ordinal);
        }
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    public async Task AliasFilteringUsesCapturedMetaWithoutReturningItOrFallingBack()
    {
        var source = AliasSource.Create("one", "v1",
            new Dictionary<string, string> { ["alias"] = "/dirs/g/files/real" });
        var later = new ControlledSource("later", "v2", ["alias", "real"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration(), later.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias$details?filter=meta.defaultversionid%3Dv1");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await BridgeFixture.Json(response);
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(metadata.GetProperty("fileid").GetString()).IsEqualTo("alias");
        await Assert.That(metadata.TryGetProperty("meta", out _)).IsFalse();
        await Assert.That(later.Opens).IsEqualTo(0);
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    public async Task AliasMetaInlineStillEnforcesItsSourcePolicy()
    {
        var source = AliasSource.Create("one", "v1",
            new Dictionary<string, string> { ["alias"] = "/dirs/g/files/real" });
        var original = source.Override!;
        source.Override = (request, token) => request.Target == "/dirs/g/files/alias/meta"
            ? throw new FederationException(FederationErrorCode.PolicyDenied, "The source does not authorize Meta.")
            : original(request, token);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var shallow = await client.GetAsync("/registry/dirs/g/files/alias$details");
        await Assert.That(shallow.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(shallow)).TryGetProperty("meta", out _)).IsFalse();
        using var denied = await client.GetAsync("/registry/dirs/g/files/alias$details?inline=meta");
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await BridgeFixture.Json(denied)).GetProperty("code").GetString()).IsEqualTo("source_policydenied");
        await Assert.That(source.Reads.Any(static read => read.Operation == FederationOperation.Document)).IsFalse();
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    public async Task RetainedAliasPagesKeepInlineChoiceAndRequireTheOriginalCursorContext()
    {
        var source = AliasSource.Create("one", "v1",
            new Dictionary<string, string> { ["a"] = "/dirs/g/files/real", ["b"] = "/dirs/g/files/real" });
        var allowed = true;
        var options = BridgeFixture.Options with
        {
            AuthorizeRetainedRead = (_, _, _, _) => ValueTask.FromResult(allowed)
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()], options);
        using var client = BridgeFixture.Client(app);
        using var first = await client.GetAsync("/registry/dirs/g/files?limit=1&sort=fileid");
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(first)).GetProperty("a").TryGetProperty("meta", out _)).IsFalse();
        var next = BridgePagingTests.Next(first);
        var requests = source.Reads.Count;
        source.Override = static (_, _) => throw new InvalidOperationException("A retained page must not reopen source state.");
        using var altered = await client.GetAsync(next.PathAndQuery + "&inline=meta");
        await Assert.That(altered.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        allowed = false;
        using var denied = await client.GetAsync(next.PathAndQuery);
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        allowed = true;
        using var second = await client.GetAsync(next.PathAndQuery);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = (await BridgeFixture.Json(second)).GetProperty("b");
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(metadata.TryGetProperty("meta", out _)).IsFalse();
        await Assert.That(source.Reads.Count).IsEqualTo(requests);
        await Assert.That(source.Opens).IsEqualTo(1);
        await Assert.That(source.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task RootByteLimitFailureClosesTheSourceWithoutAResultOrCursor()
    {
        var source = new ControlledSource("one", "v1", ["a"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], BridgeFixture.Options with
        {
            ReadLimits = new FederationReadLimits(maxResultBytes: 32)
        });
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That((await BridgeFixture.Json(response)).TryGetProperty("registryid", out _)).IsFalse();
        await Assert.That(response.Headers.TryGetValues("Link", out var links) &&
            links.Any(static link => link.Contains("rel=next", StringComparison.Ordinal))).IsFalse();
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }
}
