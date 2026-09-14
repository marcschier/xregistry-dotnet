using System.Net;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Federation;

namespace XRegistry.Bridge.Tests;

public class BridgeIdentityTests
{
    [Test]
    [Arguments(false, "item:1@home")]
    [Arguments(false, "item%3A1%40home")]
    [Arguments(true, "item:1@home")]
    [Arguments(true, "item%3A1%40home")]
    public async Task EquivalentEntityXidsPreserveRawIdsAndSelectedOrigin(bool producerOwned, string requestId)
    {
        var source = new ControlledSource("first", "v:1@stable", ["item:1@home"]) { Producer = producerOwned };
        EscapeWireXids(source);
        var later = new ControlledSource("later", "other", ["item:1@home"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration(), later.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var resource = await client.GetAsync("/registry/dirs/g/files/" + requestId + "$details");
        await Assert.That(resource.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await BridgeFixture.Json(resource);
        await Assert.That(metadata.GetProperty("xid").GetString()).IsEqualTo("/dirs/g/files/item%3a1%40home");
        await Assert.That(metadata.GetProperty("fileid").GetString()).IsEqualTo("item:1@home");
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo("v:1@stable");
        await Assert.That(metadata.GetProperty("self").GetString())
            .IsEqualTo("https://bridge.example/registry/dirs/g/files/item%3A1%40home$details");
        await Assert.That(metadata.GetProperty("domain").GetProperty("self").GetString()).IsEqualTo("https://first.example/opaque");
        using var meta = await client.GetAsync("/registry/dirs/g/files/" + requestId + "/meta");
        await Assert.That(meta.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metaValue = await BridgeFixture.Json(meta);
        await Assert.That(metaValue.GetProperty("xid").GetString()).IsEqualTo("/dirs/g/files/item%3a1%40home/meta");
        await Assert.That(metaValue.GetProperty("defaultversionid").GetString()).IsEqualTo("v:1@stable");
        await Assert.That(metaValue.GetProperty("defaultversionurl").GetString())
            .IsEqualTo("https://bridge.example/registry/dirs/g/files/item%3A1%40home/versions/v%3A1%40stable$details");
        using var version = await client.GetAsync("/registry/dirs/g/files/" + requestId + "/versions/v%3a1%40stable$details");
        await Assert.That(version.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(version)).GetProperty("versionid").GetString()).IsEqualTo("v:1@stable");
        await Assert.That(later.Opens).IsEqualTo(0);
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EscapedDocumentSelectionKeepsExactBytesAndSingleEncodedContentLocation(bool producerOwned)
    {
        var source = new ControlledSource("first", "v:1@stable", ["item:1@home"]) { Producer = producerOwned };
        EscapeWireXids(source);
        var later = new ControlledSource("later", "other", ["item:1@home"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration(), later.Registration()]);
        using var client = BridgeFixture.Client(app);
        foreach (var path in new[]
        {
            "/registry/dirs/g/files/item:1@home",
            "/registry/dirs/g/files/item%3A1%40home/versions/v:1@stable"
        })
        {
            using var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(Convert.ToHexString(await response.Content.ReadAsByteArrayAsync())).IsEqualTo("00FF0D0A41");
            await Assert.That(response.Content.Headers.ContentLocation!.AbsoluteUri)
                .IsEqualTo("https://bridge.example/registry/dirs/g/files/item%3A1%40home/versions/v%3A1%40stable");
            await Assert.That(response.Headers.GetValues("xRegistry-fileid").Single()).IsEqualTo("item:1@home");
            await Assert.That(response.Headers.GetValues("xRegistry-versionid").Single()).IsEqualTo("v:1@stable");
        }
        await Assert.That(later.Opens).IsEqualTo(0);
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EscapedAliasMetaAndXrefRetainSourceLocalMeaning(bool producerOwned)
    {
        const string reference = "/dirs/%67/files/%72eal";
        var source = AliasSource.Create("first", "v:1@stable",
            new Dictionary<string, string> { ["alias:1@home"] = reference });
        source.Producer = producerOwned;
        EscapeWireXids(source);
        var later = new ControlledSource("later", "other", ["alias:1@home", "real"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration(), later.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias%3A1%40home$details");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var value = await BridgeFixture.Json(response);
        await Assert.That(value.GetProperty("fileid").GetString()).IsEqualTo("alias:1@home");
        await Assert.That(value.GetProperty("versionid").GetString()).IsEqualTo("v:1@stable");
        await Assert.That(value.TryGetProperty("meta", out _)).IsFalse();
        using var inlined = await client.GetAsync("/registry/dirs/g/files/alias%3A1%40home$details?inline=meta");
        await Assert.That(inlined.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(inlined)).GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo(reference);
        using var meta = await client.GetAsync("/registry/dirs/g/files/alias:1@home/meta");
        await Assert.That(meta.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(meta)).GetProperty("xref").GetString()).IsEqualTo(reference);
        using var document = await client.GetAsync("/registry/dirs/g/files/alias:1@home/versions/v%3A1%40stable");
        await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await document.Content.ReadAsByteArrayAsync())).IsEqualTo("00FF0D0A41");
        await Assert.That(document.Content.Headers.ContentLocation!.AbsoluteUri)
            .IsEqualTo("https://bridge.example/registry/dirs/g/files/alias%3A1%40home/versions/v%3A1%40stable");
        using var native = await client.GetAsync("/registry/dirs/g/files/alias:1@home?doc&inline=meta");
        await Assert.That(native.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var nativeValue = await BridgeFixture.Json(native);
        await Assert.That(nativeValue.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo(reference);
        await Assert.That(nativeValue.GetProperty("meta").GetProperty("self").GetString()).IsEqualTo("#/meta");
        await Assert.That(nativeValue.TryGetProperty("versions", out _)).IsFalse();
        await Assert.That(later.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task EscapedAliasTargetCannotResolveAgainstAnUnrelatedAggregateShadow()
    {
        var local = new ControlledSource("local", "local-v", ["real"]);
        var remote = AliasSource.Create("remote", "remote-v",
            new Dictionary<string, string> { ["alias:1@home"] = "/dirs/%67/files/%72eal" });
        EscapeWireXids(remote);
        await using var app = await BridgeFixture.StartAsync([local.Registration(), remote.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias%3A1%40home$details");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("alias_origin_conflict");
        await Assert.That(remote.Reads.Any(read => read.Operation == FederationOperation.Document)).IsFalse();
        await Assert.That(local.Reads.Any(read => read.Operation == FederationOperation.Document)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EscapedCollectionsRetainSharedOrderingAndFrozenDocumentPointers(bool producerOwned)
    {
        var source = new ControlledSource("first", "v:1@stable", ["item:1@home", "zebra"]) { Producer = producerOwned };
        EscapeWireXids(source);
        var options = BridgeFixture.Options with
        {
            AuthorizeRetainedRead = static (_, _, _, _) => ValueTask.FromResult(true)
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()], options);
        using var client = BridgeFixture.Client(app);
        using var sorted = await client.GetAsync(new Uri(client.BaseAddress!.AbsoluteUri.TrimEnd('/') +
            "/registry/%64irs/g/files?filter=versionid%3Dv%3A1%40stable&sort=fileid%3Ddesc",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
        await Assert.That(sorted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", (await BridgeFixture.Json(sorted)).EnumerateObject().Select(property => property.Name)))
            .IsEqualTo("zebra,item:1@home");
        using var first = await client.GetAsync("/registry/dirs/g/files?filter=versionid%3Dv%3A1%40stable&sort=fileid%3Ddesc&limit=1&doc&inline=meta,versions.file");
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", (await BridgeFixture.Json(first)).EnumerateObject().Select(property => property.Name))).IsEqualTo("zebra");
        var next = BridgePagingTests.Next(first);
        var reads = source.Reads.Count;
        source.Override = static (_, _) => throw new InvalidOperationException("Frozen encoded pages must not reread their sources.");
        using var second = await client.GetAsync(next.PathAndQuery);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await BridgeFixture.Json(second);
        await Assert.That(string.Join(",", page.EnumerateObject().Select(property => property.Name))).IsEqualTo("item:1@home");
        var resource = page.GetProperty("item:1@home");
        await Assert.That(resource.GetProperty("self").GetString()).IsEqualTo("#/item%3A1%40home");
        await Assert.That(resource.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/item%3A1%40home/versions/v%3A1%40stable");
        await Assert.That(resource.GetProperty("versions").GetProperty("v:1@stable").GetProperty("filebase64").GetString()).IsEqualTo("AP8NCkE=");
        await Assert.That(resource.TryGetProperty("filebase64", out _)).IsFalse();
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    [Arguments("entity", "/dirs/g/files/other", "source_invalidpackage")]
    [Arguments("entity", "/dirs/g/files/Item:1@home", "source_invalidpackage")]
    [Arguments("entity", "/dirs/g/files/item%252Fescape", "source_invalidpackage")]
    [Arguments("entity", "/dirs/g/files/item%2Fescape", "source_invalidpackage")]
    [Arguments("entity", "/dirs/g/files/item%5Cescape", "source_invalidpackage")]
    [Arguments("entity", "/dirs/g/files/item%00escape", "source_invalidpackage")]
    [Arguments("entity", "/dirs/g/files/item%GGescape", "source_invalidpackage")]
    [Arguments("attribute", "item%3A1%40home", "source_invalidpackage")]
    [Arguments("member", "/dirs/g/files/other", "source_invalidpackage")]
    [Arguments("collection", "/dirs/other/files", "source_inconsistentsnapshot")]
    [Arguments("document", "/dirs/g/files/other/versions/v:1@stable", "source_inconsistentsnapshot")]
    [Arguments("document", "/dirs/g/files/item:1@home/versions/other", "source_inconsistentsnapshot")]
    [Arguments("document", "/dirs/g/files/item:1@home/versions/v%252Fescape", "source_invalidpackage")]
    public async Task InvalidSourceXidsNeverChangeResourceOrVersionIdentity(string surface, string invalid, string code)
    {
        const string owner = "/dirs/g/files/item:1@home";
        var source = new ControlledSource("first", "v:1@stable", ["item:1@home"]);
        source.Override = async (request, _) =>
        {
            var result = await source.ReadDefault(request);
            if (request.Operation == FederationOperation.Document && surface == "document")
            {
                return FederationReadResult.FromDocument(invalid, result.Context, result.Document!);
            }
            var value = JsonNode.Parse(result.Metadata.GetRawText())!.AsObject();
            if (request.Target == owner && surface is "entity" or "attribute")
            {
                value["entity"]![surface == "entity" ? "xid" : "fileid"] = invalid;
            }
            if (request.Operation == FederationOperation.Collection && surface == "member")
            {
                value["entities"]!["item:1@home"]!["xid"] = invalid;
            }
            return FederationReadResult.FromMetadata(
                request.Operation == FederationOperation.Collection && surface == "collection" ? invalid : result.SelectedXid,
                result.Context, RegistryJson.Parse(value.ToJsonString()).RootElement);
        };
        var later = new ControlledSource("later", "v:1@stable", ["item:1@home"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration(), later.Registration()]);
        using var client = BridgeFixture.Client(app);
        var path = surface switch
        {
            "member" or "collection" => "/dirs/g/files",
            "document" => owner + "/versions/v:1@stable",
            _ => owner + "$details"
        };
        using var response = await client.GetAsync("/registry" + path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(response.Content.Headers.ContentLocation).IsNull();
        await Assert.That(later.Opens).IsEqualTo(0);
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    [Arguments("item%252Fescape")]
    [Arguments("item%2Fescape")]
    [Arguments("item%5Cescape")]
    [Arguments("item%00escape")]
    [Arguments("item%GGescape")]
    [Arguments("item%")]
    [Arguments("item%C0%AFescape")]
    public async Task MalformedEscapedRequestIdsAreRejectedBeforeOpeningSources(string id)
    {
        var source = new ControlledSource("first", "v1", ["item"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync(new Uri(client.BaseAddress!.AbsoluteUri.TrimEnd('/') +
            "/registry/dirs/g/files/" + id + "$details",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(source.Opens).IsEqualTo(0);
        await Assert.That(source.Reads.Count).IsEqualTo(0);
    }

    private static void EscapeWireXids(ControlledSource source)
    {
        var prior = source.Override;
        source.Override = async (request, token) =>
        {
            var target = request.Target == "/" ? "/" :
                "/" + string.Join('/', request.Target[1..].Split('/').Select(segment => RegistryId.ParseEscaped(segment).Value));
            var raw = new FederationReadRequest(request.Operation, target, request.Selector, request.Representation);
            var result = await (prior is null ? source.ReadDefault(raw) : prior(raw, token));
            var selected = Escape(result.SelectedXid);
            if (result.Document is { } document) { return FederationReadResult.FromDocument(selected, result.Context, document); }
            if (result.ExternalDocument.ValueKind != System.Text.Json.JsonValueKind.Undefined)
            {
                return FederationReadResult.FromExternalDocument(selected, result.Context, result.ExternalDocument);
            }
            var metadata = JsonNode.Parse(result.Metadata.GetRawText())!.AsObject();
            Encode(metadata);
            return FederationReadResult.FromMetadata(selected, result.Context, RegistryJson.Parse(metadata.ToJsonString()).RootElement);
        };

        static string Escape(string xid) => xid.Replace(":", "%3a", StringComparison.Ordinal).Replace("@", "%40", StringComparison.Ordinal);
        static void Encode(JsonObject value)
        {
            if (value["xid"] is JsonValue xid) { value["xid"] = Escape(xid.GetValue<string>()); }
            foreach (var name in new[] { "entity", "meta" })
            {
                if (value[name] is JsonObject entity) { Encode(entity); }
            }
            foreach (var name in new[] { "entities", "versions" })
            {
                if (value[name] is JsonObject collection)
                {
                    foreach (var member in collection)
                    {
                        if (member.Value is JsonObject entity) { Encode(entity); }
                    }
                }
            }
        }
    }
}
