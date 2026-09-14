using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Federation;

namespace XRegistry.Bridge.Tests;

public class BridgeViewTests
{
    [Test]
    public async Task DocumentViewUsesResponseLocalPointersWithoutDefaultVersionDuplication()
    {
        var local = new ControlledSource("local", "v1", ["shared"]);
        var remote = new ControlledSource("remote", "v2", ["remote"]);
        await using var app = await BridgeFixture.StartAsync([local.Registration(), remote.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry?doc&inline=dirs.files.versions.file,dirs.files.meta");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var root = await BridgeFixture.Json(response);
        await Assert.That(root.GetProperty("self").GetString()).IsEqualTo("#/");
        await Assert.That(root.GetProperty("dirsurl").GetString()).IsEqualTo("#/dirs");
        var resource = root.GetProperty("dirs").GetProperty("g").GetProperty("files").GetProperty("shared");
        await Assert.That(resource.GetProperty("self").GetString()).IsEqualTo("#/dirs/g/files/shared");
        await Assert.That(resource.TryGetProperty("versionid", out _)).IsFalse();
        await Assert.That(resource.TryGetProperty("epoch", out _)).IsFalse();
        await Assert.That(resource.TryGetProperty("filebase64", out _)).IsFalse();
        await Assert.That(resource.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/dirs/g/files/shared/versions/v1");
        var version = resource.GetProperty("versions").GetProperty("v1");
        await Assert.That(version.GetProperty("filebase64").GetString()).IsEqualTo("AP8NCkE=");
        await Assert.That(version.GetProperty("self").GetString()).IsEqualTo("#/dirs/g/files/shared/versions/v1");
        await Assert.That(version.GetProperty("domain").GetProperty("self").GetString()).IsEqualTo("https://local.example/opaque");
        await Assert.That(root.TryGetProperty("model", out _)).IsFalse();
        await Assert.That(root.TryGetProperty("modelsource", out _)).IsFalse();
    }

    [Test]
    public async Task DocumentAliasStaysUnresolvedAndAliasVersionViewsHaveSpecificCoreError()
    {
        var source = AliasSource.Create("local", "v1", new Dictionary<string, string> { ["alias"] = "/dirs/g/files/real" });
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias?doc&inline=*");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var alias = await BridgeFixture.Json(response);
        await Assert.That(alias.GetProperty("self").GetString()).IsEqualTo("#/");
        await Assert.That(alias.GetProperty("meta").GetProperty("self").GetString()).IsEqualTo("#/meta");
        await Assert.That(alias.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo("/dirs/g/files/real");
        await Assert.That(alias.TryGetProperty("versions", out _)).IsFalse();
        await Assert.That(alias.GetProperty("meta").TryGetProperty("epoch", out _)).IsFalse();
        await Assert.That(source.Reads.Any(read => read.Target.StartsWith("/dirs/g/files/real", StringComparison.Ordinal))).IsFalse();
        using var versions = await client.GetAsync("/registry/dirs/g/files/alias/versions?doc");
        await Assert.That(versions.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await BridgeFixture.Json(versions)).GetProperty("code").GetString()).IsEqualTo("cannot_doc_xref");
    }

    [Test]
    [Arguments("/dirs?doc&inline=files.meta,files.versions", "g", "#/g")]
    [Arguments("/dirs/g?doc&inline=files.meta,files.versions", "files/shared", "#/files/shared")]
    [Arguments("/dirs/g/files?doc&inline=meta,versions", "shared", "#/shared")]
    [Arguments("/dirs/g/files/shared?doc&inline=meta,versions", "", "#/")]
    [Arguments("/dirs/g/files/shared/meta?doc", "", "#/")]
    [Arguments("/dirs/g/files/shared/versions?doc", "v1", "#/v1")]
    [Arguments("/dirs/g/files/shared/versions/v1?doc", "", "#/")]
    public async Task EveryEntityAndCollectionUsesItsActualResponseRoot(string path, string location, string expected)
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry" + path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var value = await BridgeFixture.Json(response);
        foreach (var segment in location.Split('/', StringSplitOptions.RemoveEmptyEntries)) { value = value.GetProperty(segment); }
        await Assert.That(value.GetProperty("self").GetString()).IsEqualTo(expected);
        if (path.StartsWith("/dirs/g/files/shared/meta", StringComparison.Ordinal))
        {
            await Assert.That(value.GetProperty("defaultversionurl").GetString())
                .IsEqualTo("https://bridge.example/registry/dirs/g/files/shared/versions/v1$details");
        }
    }

    [Test]
    public async Task CollectionsViewOmitsOnlyTheTopEntityAndKeepsNestedMetadataAndPointers()
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g?collections&doc");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var value = await BridgeFixture.Json(response);
        await Assert.That(string.Join(",", value.EnumerateObject().Select(property => property.Name))).IsEqualTo("files,notes");
        var resource = value.GetProperty("files").GetProperty("shared");
        await Assert.That(resource.GetProperty("fileid").GetString()).IsEqualTo("shared");
        await Assert.That(resource.GetProperty("meta").GetProperty("self").GetString()).IsEqualTo("#/files/shared/meta");
        await Assert.That(resource.GetProperty("versions").GetProperty("v1").GetProperty("filebase64").GetString()).IsEqualTo("AP8NCkE=");
        await Assert.That(resource.TryGetProperty("versionid", out _)).IsFalse();
    }

    [Test]
    public async Task ApiInlineKeepsItsDefaultProjectionAndAbsoluteModelAwareMetadataLinks()
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/shared$details?inline=meta,versions");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var value = await BridgeFixture.Json(response);
        await Assert.That(value.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(value.GetProperty("self").GetString()).IsEqualTo("https://bridge.example/registry/dirs/g/files/shared$details");
        await Assert.That(value.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("https://bridge.example/registry/dirs/g/files/shared/versions/v1$details");
        await Assert.That(value.GetProperty("versions").GetProperty("v1").GetProperty("self").GetString())
            .IsEqualTo("https://bridge.example/registry/dirs/g/files/shared/versions/v1$details");
    }

    [Test]
    public async Task BinaryChangesOnlyRequestedDocumentEncodingAndJsonDomainValuesRemainUntouched()
    {
        var source = DocumentSource(Encoding.UTF8.GetBytes("{\"x\":1}\n"), "application/json");
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var json = await client.GetAsync("/registry/dirs/g/files/shared/versions/v1?doc&inline=file");
        var first = await BridgeFixture.Json(json);
        await Assert.That(json.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(first.GetProperty("file").GetProperty("x").GetInt32()).IsEqualTo(1);
        await Assert.That(first.TryGetProperty("filebase64", out _)).IsFalse();
        using var binary = await client.GetAsync("/registry/dirs/g/files/shared/versions/v1?doc&inline=file&binary");
        var second = await BridgeFixture.Json(binary);
        await Assert.That(second.GetProperty("filebase64").GetString()).IsEqualTo("eyJ4IjoxfQo=");
        await Assert.That(second.TryGetProperty("file", out _)).IsFalse();
        using var noInline = await client.GetAsync("/registry/dirs/g/files/shared/versions/v1$details?binary");
        var third = await BridgeFixture.Json(noInline);
        await Assert.That(third.TryGetProperty("file", out _)).IsFalse();
        await Assert.That(third.TryGetProperty("filebase64", out _)).IsFalse();
        var opaque = DocumentSource(Encoding.UTF8.GetBytes("{\"self\":\"https://domain.example/keep\"}"), "application/json");
        await using var otherApp = await BridgeFixture.StartAsync([opaque.Registration()]);
        using var otherClient = BridgeFixture.Client(otherApp);
        using var document = await otherClient.GetAsync("/registry/dirs/g/files/shared/versions/v1?doc&inline=file");
        await Assert.That((await BridgeFixture.Json(document)).GetProperty("file").GetProperty("self").GetString())
            .IsEqualTo("https://domain.example/keep");
    }

    [Test]
    [Arguments("", "")]
    [Arguments("00FF", "AP8=")]
    public async Task EmptyAndInvalidJsonDocumentsUseExactBase64Fallback(string hex, string expected)
    {
        var source = DocumentSource(Convert.FromHexString(hex), "application/json");
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/shared/versions/v1?doc&inline=file");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("filebase64").GetString()).IsEqualTo(expected);
    }

    [Test]
    public async Task InvalidInlineAndCollectionsInputsFailBeforeAnySourceAcquisition()
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        foreach (var path in new[]
        {
            "/registry?inline=dirs.g.files", "/registry?inline=dirs.*.files",
            "/registry/dirs/g/files/shared?inline=filebase64", "/registry/dirs/g/files/shared?collections",
            "/registry?doc&doc", "/registry?inline=site"
        })
        {
            using var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
        await Assert.That(source.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task ExternalInlineDescriptorRemainsAUrlRatherThanEmptyOrFetchedBytes()
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        source.Override = (request, _) => request.Operation == FederationOperation.Document
            ? ValueTask.FromResult(FederationReadResult.FromExternalDocument("/dirs/g/files/shared/versions/v1", source.Context,
                RegistryJson.Parse("""{"kind":"external","uri":"https://documents.example/item?sig=%41+%2B"}""").RootElement))
            : source.ReadDefault(request);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/shared/versions/v1?doc&inline=file&binary");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await BridgeFixture.Json(response);
        await Assert.That(metadata.GetProperty("fileurl").GetString()).IsEqualTo("https://documents.example/item?sig=%41+%2B");
        await Assert.That(metadata.TryGetProperty("file", out _)).IsFalse();
        await Assert.That(metadata.TryGetProperty("filebase64", out _)).IsFalse();
        using var redirect = await client.GetAsync("/registry/dirs/g/files/shared/versions/v1");
        await Assert.That(redirect.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    private static ControlledSource DocumentSource(byte[] bytes, string contentType)
    {
        var source = new ControlledSource("local", "v1", ["shared"]);
        source.Override = async (request, _) =>
        {
            if (request.Operation == FederationOperation.Document)
            {
                return FederationReadResult.FromDocument("/dirs/g/files/shared/versions/v1", source.Context, new FederationDocument(bytes, contentType));
            }
            var read = await source.ReadDefault(request);
            var value = JsonNode.Parse(read.Metadata.GetRawText())!.AsObject();
            if (value["entity"] is JsonObject entity && entity.ContainsKey("contenttype")) { entity["contenttype"] = contentType; }
            if (value["entities"] is JsonObject entities)
            {
                foreach (var item in entities)
                {
                    if (item.Value is JsonObject member && member.ContainsKey("contenttype")) { member["contenttype"] = contentType; }
                }
            }
            return FederationReadResult.FromMetadata(read.SelectedXid, read.Context, RegistryJson.Parse(value.ToJsonString()).RootElement);
        };
        return source;
    }
}
