// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.File;
using XRegistry.Federation;

namespace XRegistry.Bridge.Tests;

public class BridgeAliasTests
{
    [Test]
    [Arguments(false, "")]
    [Arguments(true, "")]
    [Arguments(false, "?doc")]
    [Arguments(true, "?doc")]
    public async Task DirectAliasMetaViewsHonorLogicalSourceAuthorization(bool dangling, string query)
    {
        var source = AliasSource.Create("one", "v1",
            new Dictionary<string, string> { ["alias"] = dangling ? "/dirs/g/files/missing" : "/dirs/g/files/real" });
        var inherited = source.Override!;
        source.Override = (request, token) => request.Target == "/dirs/g/files/alias/meta"
            ? throw new FederationException(FederationErrorCode.PolicyDenied, "The logical alias Meta is not authorized.")
            : inherited(request, token);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias/meta" + query);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("source_policydenied");
        await Assert.That(source.Reads.Any(read => read.Target == "/dirs/g/files/alias/meta")).IsTrue();
        await Assert.That(source.Reads.Any(read => read.Operation == FederationOperation.Document)).IsFalse();
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    public async Task OneHopAliasKeepsAliasIdentityAndEveryReadInItsSelectedSource()
    {
        var source = AliasSource.Create("local", "v1", new Dictionary<string, string>
        {
            ["alias"] = "/dirs/g/files/real"
        });
        var later = new ControlledSource("later", "v2", ["alias", "real"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration(), later.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var shallow = await client.GetAsync("/registry/dirs/g/files/alias$details");
        await Assert.That(shallow.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var shallowMetadata = await BridgeFixture.Json(shallow);
        await Assert.That(shallowMetadata.TryGetProperty("meta", out _)).IsFalse();
        await Assert.That(shallowMetadata.GetProperty("fileid").GetString()).IsEqualTo("alias");
        await Assert.That(shallowMetadata.GetProperty("versionid").GetString()).IsEqualTo("v1");
        using var resource = await client.GetAsync("/registry/dirs/g/files/alias$details?inline=meta");
        await Assert.That(resource.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await BridgeFixture.Json(resource);
        await Assert.That(metadata.GetProperty("fileid").GetString()).IsEqualTo("alias");
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(metadata.GetProperty("self").GetString()).IsEqualTo("https://bridge.example/registry/dirs/g/files/alias$details");
        await Assert.That(metadata.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo("/dirs/g/files/real");
        await Assert.That(metadata.GetProperty("meta").GetProperty("fileid").GetString()).IsEqualTo("alias");
        using var meta = await client.GetAsync("/registry/dirs/g/files/alias/meta");
        await Assert.That((await BridgeFixture.Json(meta)).GetProperty("defaultversionid").GetString()).IsEqualTo("v1");
        using var version = await client.GetAsync("/registry/dirs/g/files/alias/versions/v1$details");
        var versionJson = await BridgeFixture.Json(version);
        await Assert.That(versionJson.GetProperty("xid").GetString()).IsEqualTo("/dirs/g/files/alias/versions/v1");
        await Assert.That(versionJson.GetProperty("fileid").GetString()).IsEqualTo("alias");
        using var document = await client.GetAsync("/registry/dirs/g/files/alias");
        await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await document.Content.ReadAsByteArrayAsync())).IsEqualTo("00FF0D0A41");
        await Assert.That(document.Content.Headers.ContentLocation!.AbsoluteUri)
            .IsEqualTo("https://bridge.example/registry/dirs/g/files/alias/versions/v1");
        using var missing = await client.GetAsync("/registry/dirs/g/files/alias/versions/v2");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(later.Opens).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DanglingAndSecondHopAliasesHaveOnlyIdentityShapeAndNeverFollowAgain(bool secondHop)
    {
        var source = AliasSource.Create("local", "v1", new Dictionary<string, string>
        {
            ["alias"] = secondHop ? "/dirs/g/files/second" : "/dirs/g/files/absent",
            ["second"] = "/dirs/g/files/real"
        });
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var resource = await client.GetAsync("/registry/dirs/g/files/alias$details");
        var value = await BridgeFixture.Json(resource);
        await Assert.That(resource.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)))
            .IsEqualTo("fileid,meta,metaurl,self,xid");
        await Assert.That(value.GetProperty("meta").GetProperty("xref").GetString())
            .IsEqualTo(secondHop ? "/dirs/g/files/second" : "/dirs/g/files/absent");
        using var versions = await client.GetAsync("/registry/dirs/g/files/alias/versions");
        await Assert.That(versions.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(versions)).EnumerateObject().Count()).IsEqualTo(0);
        using var explicitVersion = await client.GetAsync("/registry/dirs/g/files/alias/versions/v1$details");
        await Assert.That(explicitVersion.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var document = await client.GetAsync("/registry/dirs/g/files/alias");
        await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await document.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        await Assert.That(source.Reads.Any(read => read.Target.StartsWith("/dirs/g/files/real", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task AliasCannotBeProjectedOntoAnUnrelatedAggregateTargetShadow()
    {
        var local = new ControlledSource("local", "local-v", ["real"]);
        var remote = AliasSource.Create("remote", "v1", new Dictionary<string, string> { ["alias"] = "/dirs/g/files/real" });
        await using var app = await BridgeFixture.StartAsync([local.Registration(), remote.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias$details");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("alias_origin_conflict");
        await Assert.That(remote.Reads.Any(read => read.Operation == FederationOperation.Document)).IsFalse();
    }

    [Test]
    public async Task LocalNormalResourceShadowsARemoteAliasAsOneResourceUnit()
    {
        var local = new ControlledSource("local", "local-v", ["alias"]);
        var remote = AliasSource.Create("remote", "remote-v", new Dictionary<string, string> { ["alias"] = "/dirs/g/files/real" });
        await using var app = await BridgeFixture.StartAsync([local.Registration(), remote.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias$details");
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("versionid").GetString()).IsEqualTo("local-v");
        await Assert.That(remote.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task StructurallyIdenticalButIndependentTypesCannotBeAliasTargets()
    {
        var document = JsonNode.Parse(BridgeFixture.ModelText)!.AsObject();
        document["groups"]!["independent"] = new JsonObject
        {
            ["singular"] = "independent",
            ["resources"] = new JsonObject { ["files"] = document["groups"]!["dirs"]!["resources"]!["files"]!.DeepClone() }
        };
        var model = RegistryModel.Compile(RegistryJson.Parse(document.ToJsonString()));
        var source = AliasSource.Create("local", "v1", new Dictionary<string, string>
        {
            ["alias"] = "/independent/g/files/real"
        });
        source.Model = model;
        await using var app = await BridgeFixture.StartAsync([source.Registration()], BridgeFixture.Options with { Model = model });
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias$details");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("malformed_xref");
        await Assert.That(source.Reads.Any(read => read.Target.StartsWith("/independent/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FrozenOciImportedTypeAliasPreservesDefaultExplicitVersionAndBytes(bool producerOwned)
    {
        var directory = Path.Combine(BridgeFixture.Repository, "tests", "Conformance", "Sources",
            "workingdrafts", "federation", "samples", "oci", "layout");
        var endpoint = new Uri(directory + Path.DirectorySeparatorChar);
        await using var bootstrap = await FileRegistrySource.OpenAsync(endpoint, FileRegistryLayout.OciLayout, "offline", endpoint);
        var registration = new FederationSourceRegistration("oci-file", FederationRepresentation.DocumentView, async (budget, token) =>
        {
            var opened = await FileRegistrySource.OpenAsync(endpoint, FileRegistryLayout.OciLayout, "offline", endpoint, budget, token);
            return new FederationSourceLease(producerOwned ? new ProducerSource(opened) : opened, opened.DisposeAsync);
        });
        await using var app = await BridgeFixture.StartAsync([registration], BridgeFixture.Options with { Model = bootstrap.Model });
        using var client = BridgeFixture.Client(app);
        using var metadata = await client.GetAsync("/registry/imports/shared/files/alias$details");
        await Assert.That(metadata.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var value = await BridgeFixture.Json(metadata);
        await Assert.That(value.GetProperty("fileid").GetString()).IsEqualTo("alias");
        await Assert.That(value.TryGetProperty("meta", out _)).IsFalse();
        using var inlined = await client.GetAsync("/registry/imports/shared/files/alias$details?inline=meta");
        await Assert.That(inlined.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(inlined)).GetProperty("meta").GetProperty("xref").GetString())
            .IsEqualTo("/dirs/main/files/sample");
        using var versionMetadata = await client.GetAsync("/registry/imports/shared/files/alias/versions/v1$details");
        await Assert.That(versionMetadata.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(versionMetadata)).GetProperty("fileid").GetString()).IsEqualTo("alias");
        using var versions = await client.GetAsync("/registry/imports/shared/files/alias/versions");
        await Assert.That(versions.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(versions)).TryGetProperty("v1", out _)).IsTrue();
        using var document = await client.GetAsync("/registry/imports/shared/files/alias/versions/v1");
        await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await document.Content.ReadAsByteArrayAsync())).IsEqualTo("7B2274797065223A22737472696E67227D0A");
        await Assert.That(document.Content.Headers.ContentLocation!.AbsoluteUri)
            .IsEqualTo("https://bridge.example/registry/imports/shared/files/alias/versions/v1");
    }

    private sealed class ProducerSource(IFederationReadSource source) : IFederationReadSource
    {
        public NativeRegistryContext Context => source.Context;
        public RegistryModel? Model => source.Model;
        public System.Text.Json.JsonElement Capabilities => RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement;
        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default) =>
            source.ReadAsync(request, cancellationToken);
    }
}

internal static class AliasSource
{
    internal static ControlledSource Create(string name, string version, IReadOnlyDictionary<string, string> aliases)
    {
        var source = new ControlledSource(name, version, ["real", .. aliases.Keys]);
        source.Override = (request, _) =>
        {
            var parts = request.Target.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && aliases.TryGetValue(parts[3], out var target))
            {
                if (request.Operation != FederationOperation.Entity || parts.Length > 5)
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation, "The producer must resolve this native alias in its own source.");
                }
                var owner = "/dirs/g/files/" + parts[3];
                var meta = new JsonObject
                {
                    ["fileid"] = parts[3],
                    ["xid"] = owner + "/meta",
                    ["self"] = source.Context.Source + owner + "/meta",
                    ["xref"] = target
                };
                var entity = parts.Length == 5 ? meta : new JsonObject
                {
                    ["fileid"] = parts[3],
                    ["xid"] = owner,
                    ["self"] = source.Context.Source + owner + "$details",
                    ["metaurl"] = source.Context.Source + owner + "/meta",
                    ["meta"] = meta
                };
                return ValueTask.FromResult(FederationReadResult.FromMetadata(request.Target, source.Context,
                    RegistryJson.Parse(new JsonObject { ["entity"] = entity }.ToJsonString()).RootElement));
            }
            return source.ReadDefault(request);
        };
        return source;
    }
}
