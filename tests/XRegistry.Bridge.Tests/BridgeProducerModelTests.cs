// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Federation;

namespace XRegistry.Bridge.Tests;

public class BridgeProducerModelTests
{
    [Test]
    public async Task ExplicitTargetPolicyReadsCannotBypassPrivateEntityAccessThroughCollectionSeeds()
    {
        var node = JsonNode.Parse(BridgeFixture.ModelText)!;
        node["groups"]!["dirs"]!["resources"]!["files"]!["attributes"]!["reference"] =
            JsonNode.Parse("""{"type":"url","target":"/dirs/files"}""");
        var model = RegistryModel.Compile(RegistryJson.Parse(node.ToJsonString()));
        var source = new ControlledSource("selected", "v1", ["item", "target"]) { Model = model };
        source.Override = async (request, _) =>
        {
            if (request.Operation == FederationOperation.Entity && request.Target == "/dirs/g/files/target")
            {
                throw new FederationException(FederationErrorCode.PolicyDenied, "The target entity is private.");
            }
            return await TransformAsync(source, request, (path, value) =>
            {
                if (path.StartsWith("/dirs/g/files/item", StringComparison.Ordinal) && !path.EndsWith("/meta", StringComparison.Ordinal))
                {
                    value["reference"] = "/dirs/g/files/target";
                }
            });
        };
        var registration = new FederationSourceRegistration("selected", FederationRepresentation.ApiView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask)),
            validateTarget: async (context, _) =>
            {
                var target = await context.ReadMetadataAsync(RegistryPath.Parse("/dirs/g/files/target"));
                return target.GetProperty("fileid").GetString() == "target";
            });
        await using var app = await BridgeFixture.StartAsync([registration], BridgeFixture.Options with { Model = model });
        using var client = BridgeFixture.Client(app);

        using var response = await client.GetAsync("/registry/dirs/g/files");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var problem = await BridgeFixture.Json(response);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("source_policydenied");
        await Assert.That(problem.TryGetProperty("item", out _)).IsFalse();
        await Assert.That(response.Headers.Contains("X-Bridge-Sources")).IsFalse();
        await Assert.That(source.Reads.Any(r => r.Operation == FederationOperation.Entity && r.Target == "/dirs/g/files/target")).IsTrue();
    }

    [Test]
    [Arguments("JsonSchema/draft/2020-12")]
    [Arguments("OpenUSD/1.0")]
    public async Task ValidationEnabledModelsServeMetadataAndExactDocumentsWithoutImplicitRevalidation(string format)
    {
        var node = JsonNode.Parse(BridgeFixture.ModelText)!;
        var resource = node["groups"]!["dirs"]!["resources"]!["files"]!;
        resource["validateformat"] = true;
        resource["validatecompatibility"] = true;
        resource["strictvalidation"] = true;
        var model = RegistryModel.Compile(RegistryJson.Parse(node.ToJsonString()));
        var source = new ControlledSource("selected", "v1", ["item"]) { Model = model };
        source.Override = (request, _) => TransformAsync(source, request, (path, value) =>
        {
            if (path.StartsWith("/dirs/g/files/item", StringComparison.Ordinal) && !path.EndsWith("/meta", StringComparison.Ordinal))
            {
                value["format"] = format;
            }
        });
        await using var app = await BridgeFixture.StartAsync([source.Registration()], BridgeFixture.Options with { Model = model });
        using var client = BridgeFixture.Client(app);

        using var metadata = await client.GetAsync("/registry/dirs/g/files/item/versions/v1$details");
        await Assert.That(metadata.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await BridgeFixture.Json(metadata);
        await Assert.That(body.GetProperty("format").GetString()).IsEqualTo(format);
        await Assert.That(body.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(body.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        await Assert.That(source.Reads.Any(r => r.Operation == FederationOperation.Document)).IsFalse();

        using var document = await client.GetAsync("/registry/dirs/g/files/item/versions/v1");
        await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await document.Content.ReadAsByteArrayAsync())).IsEqualTo("00FF0D0A41");
        using var doc = await client.GetAsync("/registry/dirs/g/files/item/versions/v1?doc");
        await Assert.That(doc.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(doc)).TryGetProperty("formatvalidated", out _)).IsFalse();
    }

    [Test]
    public async Task GroupConstraintFailureRollsBackHttpPreparationWithoutPublishingPartialMetadata()
    {
        var node = JsonNode.Parse(BridgeFixture.ModelText)!;
        var group = node["groups"]!["dirs"]!;
        group["attributes"] = JsonNode.Parse("""{"expected":"string"}""");
        group["constraints"] = JsonNode.Parse("""{"files.color":{"equals":"expected","enum":["red","blue"]}}""");
        group["resources"]!["files"]!["attributes"]!["color"] = "string";
        var model = RegistryModel.Compile(RegistryJson.Parse(node.ToJsonString()));
        var invalid = true;
        var source = new ControlledSource("selected", "v1", ["a", "b"]) { Model = model };
        source.Override = (request, _) => TransformAsync(source, request, (path, value) =>
        {
            if (path == "/dirs/g") { value["expected"] = "red"; }
            else if (path.StartsWith("/dirs/g/files/", StringComparison.Ordinal) && !path.EndsWith("/meta", StringComparison.Ordinal))
            {
                value["color"] = invalid && path.StartsWith("/dirs/g/files/b", StringComparison.Ordinal) ? "blue" : "red";
            }
        });
        await using var app = await BridgeFixture.StartAsync([source.Registration()], BridgeFixture.Options with { Model = model });
        using var client = BridgeFixture.Client(app);

        using var failure = await client.GetAsync("/registry/dirs/g/files?inline=versions");
        await Assert.That(failure.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        var problem = await BridgeFixture.Json(failure);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("source_invalidpackage");
        await Assert.That(problem.TryGetProperty("a", out _)).IsFalse();
        await Assert.That(problem.TryGetProperty("b", out _)).IsFalse();
        await Assert.That(failure.Headers.Contains("X-Bridge-Sources")).IsFalse();
        invalid = false;
        using var success = await client.GetAsync("/registry/dirs/g/files?inline=versions");
        await Assert.That(success.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(success)).GetProperty("b").GetProperty("color").GetString()).IsEqualTo("red");
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    private static async ValueTask<FederationReadResult> TransformAsync(ControlledSource source,
        FederationReadRequest request, Action<string, JsonObject> transform)
    {
        var result = await source.ReadDefault(request);
        if (result.Metadata.ValueKind == JsonValueKind.Undefined) { return result; }
        var metadata = JsonNode.Parse(result.Metadata.GetRawText())!.AsObject();
        if (metadata["entity"] is JsonObject entity) { transform(request.Target, entity); }
        if (metadata["entities"] is JsonObject entities)
        {
            foreach (var member in entities)
            {
                transform(request.Target + "/" + member.Key, member.Value!.AsObject());
            }
        }
        return FederationReadResult.FromMetadata(result.SelectedXid, result.Context, RegistryJson.Parse(metadata.ToJsonString()).RootElement);
    }
}
