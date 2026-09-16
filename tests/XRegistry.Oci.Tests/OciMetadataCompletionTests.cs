// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciMetadataCompletionTests
{
    [Test]
    public async Task MatchVersionsUsesTimestampInstantsRatherThanCapturedSpellings()
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (record, media) =>
        {
            if (media != GraphFixture.Config) { return GraphFixture.Encode(record); }
            var xid = record["entity"]!["xid"]!.GetValue<string>();
            if (xid == "/")
            {
                var source = record["modelresolved"]!;
                source["groups"]!["dirs"]!["resources"]!["files"]!["attributes"]!["observed"] =
                    new JsonObject { ["type"] = "timestamp", ["matchversions"] = true };
                record["entity"]!["modelsource"] = source.DeepClone();
                record["entity"]!["model"] = JsonNode.Parse(
                    RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString())).EffectiveModel.RootElement.GetRawText());
            }
            if (xid.StartsWith(OciDocumentTests.Sample + "/versions/", StringComparison.Ordinal))
            {
                record["entity"]!["observed"] = xid.EndsWith("/v1", StringComparison.Ordinal)
                    ? "2026-04-30T02:00:00.120000000+02:00"
                    : "2026-04-30T00:00:00.12Z";
            }
            return GraphFixture.Encode(record);
        });

        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ValidateAsync();

        await Assert.That(result.Configs).IsEqualTo(25);
        await Assert.That(result.RootDigest).IsEqualTo(snapshot.RootDigest);
        await Assert.That(result.SnapshotClass).IsEqualTo("offline-complete");
    }

    [Test]
    public async Task MatchVersionsMaterializesAnOmittedDefaultWithoutRewritingTheCapturedConfig()
    {
        var tree = WithModel((source, resource) =>
        {
            resource["attributes"]!["stable"] = new JsonObject
            {
                ["type"] = "string",
                ["required"] = true,
                ["default"] = "fixed",
                ["matchversions"] = true,
            };
        }, (entity, xid) =>
        {
            if (xid == OciDocumentTests.Sample + "/versions/v2") { entity["stable"] = "fixed"; }
        });
        var captured = GraphFixture.Capture(tree).Records.Single(record =>
            record.RootElement.GetProperty("entity").GetProperty("xid").GetString() == OciDocumentTests.Sample + "/versions/v1");
        var original = captured.RootElement.GetRawText();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");

        await snapshot.ValidateAsync();
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, OciDocumentTests.Sample));

        await Assert.That(result.Value.GetProperty("versions").GetProperty("v1").GetProperty("stable").GetString()).IsEqualTo("fixed");
        await Assert.That(result.Value.GetProperty("versions").GetProperty("v2").GetProperty("stable").GetString()).IsEqualTo("fixed");
        await Assert.That(captured.RootElement.GetProperty("entity").TryGetProperty("stable", out _)).IsFalse();
        await Assert.That(captured.RootElement.GetRawText()).IsEqualTo(original);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task GroupEqualsConstraintsUseTheGroupsNormalizedDefault(bool agrees)
    {
        var tree = WithModel((source, resource) =>
        {
            source["groups"]!["dirs"]!["attributes"] = new JsonObject
            {
                ["site"] = new JsonObject { ["type"] = "string", ["required"] = true, ["default"] = "east" },
            };
            resource["attributes"]!["site"] = new JsonObject { ["type"] = "string", ["required"] = true, ["default"] = "east" };
            source["groups"]!["dirs"]!["constraints"] = new JsonObject
            {
                ["files.site"] = new JsonObject { ["equals"] = "site" },
            };
        }, (entity, xid) =>
        {
            if (xid == OciDocumentTests.Sample + "/versions/v1") { entity["site"] = agrees ? "east" : "west"; }
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        if (!agrees)
        {
            await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample)).AsTask(),
                FederationErrorCode.InvalidPackage);
            return;
        }
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, "/dirs/main"));
        await Assert.That(result.Value.GetProperty("site").GetString()).IsEqualTo("east");
        await Assert.That(result.Value.GetProperty("files").GetProperty("sample").GetProperty("versions")
            .GetProperty("v1").GetProperty("site").GetString()).IsEqualTo("east");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConstraintDefaultsActivateConditionalAttributesBeforeValidationAndMaterialization(bool invalid)
    {
        var tree = WithModel((source, resource) =>
        {
            resource["attributes"]!["mode"] = new JsonObject
            {
                ["type"] = "string",
                ["required"] = true,
                ["ifvalues"] = new JsonObject
                {
                    ["live"] = new JsonObject
                    {
                        ["siblingattributes"] = new JsonObject
                        {
                            ["detail"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["required"] = true,
                                ["default"] = "ready",
                                ["enum"] = new JsonArray("ready"),
                            },
                        },
                    },
                },
            };
            source["groups"]!["dirs"]!["constraints"] = new JsonObject
            {
                ["files.mode"] = new JsonObject { ["default"] = "LIVE" },
            };
        }, (entity, xid) =>
        {
            if (invalid && xid == OciDocumentTests.Sample + "/versions/v1") { entity["detail"] = "invalid"; }
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        if (invalid)
        {
            await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Entity, OciDocumentTests.Sample + "/versions/v1")).AsTask(),
                FederationErrorCode.InvalidPackage);
            return;
        }
        var result = await snapshot.ReadAsync(new(FederationOperation.Entity, OciDocumentTests.Sample + "/versions/v1"));
        await Assert.That(result.Value.GetProperty("mode").GetString()).IsEqualTo("LIVE");
        await Assert.That(result.Value.GetProperty("detail").GetString()).IsEqualTo("ready");
    }

    [Test]
    [Arguments("timestamp", "\"2026-04-30T00:00:00.120000000Z\"", "\"2026-04-30T00:00:00.120000001Z\"", false)]
    [Arguments("string", "\"2026-04-30T00:00:00.1200Z\"", "\"2026-04-30T00:00:00.12Z\"", false)]
    [Arguments("decimal", "9007199254740993.0", "90071992547409930e-1", true)]
    [Arguments("decimal", "9007199254740993.0", "9007199254740992.0", false)]
    public async Task MatchingUsesTypedScalarSemanticsWithoutFractionOrNumberTruncation(
        string type, string first, string second, bool agrees)
    {
        var tree = WithModel((source, resource) =>
        {
            resource["attributes"]!["stable"] = new JsonObject { ["type"] = type, ["matchversions"] = true };
        }, (entity, xid) =>
        {
            if (xid.StartsWith(OciDocumentTests.Sample + "/versions/", StringComparison.Ordinal))
            {
                entity["stable"] = JsonNode.Parse(xid.EndsWith("/v1", StringComparison.Ordinal) ? first : second);
            }
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        if (agrees) { await Assert.That((await snapshot.ValidateAsync()).Configs).IsEqualTo(25); }
        else { await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage); }
    }

    [Test]
    public async Task NormalizedDocumentMediaTypeIsConsistentAcrossReaderAndProducer()
    {
        const string mediaType = "application/x-example; charset=utf-8";
        var tree = WithModel((source, resource) =>
        {
            resource["attributes"]!["contenttype"] = new JsonObject
            {
                ["type"] = "string",
                ["required"] = true,
                ["default"] = mediaType,
            };
        }, (entity, xid) =>
        {
            if (xid.StartsWith(OciDocumentTests.Sample + "/versions/", StringComparison.Ordinal))
            {
                entity.AsObject().Remove("contenttype");
            }
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample));
        await Assert.That(result.Document!.ContentType).IsEqualTo(mediaType);

        var capture = GraphFixture.Capture(tree);
        var key = OciDocumentTests.Sample + "/versions/v1";
        capture.Documents[key] = new FederationDocument(await Check.Bytes(capture.Documents[key]), mediaType);
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents));
        await using var produced = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest);
        var document = await produced.ReadAsync(new(FederationOperation.Document, key));
        await Assert.That(document.Document!.ContentType).IsEqualTo(mediaType);
        await Assert.That(System.Text.Encoding.UTF8.GetString(await Check.Bytes(document.Document)))
            .IsEqualTo("{\"type\":\"string\"}\n");
    }

    [Test]
    public async Task NormalizedDefaultsCannotAddAnExternalLocatorToAnEmbeddedDocument()
    {
        var tree = WithModel((source, resource) =>
        {
            resource["attributes"]!["fileurl"] = new JsonObject
            {
                ["type"] = "url",
                ["required"] = true,
                ["default"] = "https://not-acquired.invalid/default",
            };
        }, static (_, _) => { });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Entity,
            OciDocumentTests.Sample + "/versions/v1")).AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CachedNormalizedMetadataHasAnAggregateBoundSeparateFromActualInputBytes()
    {
        var source = RegistryJson.Parse(new JsonObject
        {
            ["groups"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["singular"] = "item",
                    ["attributes"] = new JsonObject
                    {
                        ["payload"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["required"] = true,
                            ["default"] = new string('a', 4000),
                        },
                    },
                },
            },
        }.ToJsonString());
        var records = AuthoredCapture.Groups(100);
        records[0] = AuthoredCapture.Registry(source);
        var package = await OciSnapshotWriter.CreateAsync(new(records));
        var wireBytes = package.Objects.Sum(item => item.Size);
        var budget = new FederationReadBudget(new(maxTotalBytes: wireBytes));
        await using var snapshot = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest, budget);

        await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.LimitExceeded);

        await Assert.That(budget.BytesRead <= wireBytes).IsTrue();
    }

    private static MemoryLayout WithModel(Action<JsonNode, JsonNode> configureModel, Action<JsonNode, string> configureEntity)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (record, media) =>
        {
            if (media != GraphFixture.Config) { return GraphFixture.Encode(record); }
            var entity = record["entity"]!;
            var xid = entity["xid"]!.GetValue<string>();
            if (xid == "/")
            {
                var source = record["modelresolved"]!;
                configureModel(source, source["groups"]!["dirs"]!["resources"]!["files"]!);
                entity["modelsource"] = source.DeepClone();
                entity["model"] = JsonNode.Parse(RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()))
                    .EffectiveModel.RootElement.GetRawText());
            }
            configureEntity(entity, xid);
            return GraphFixture.Encode(record);
        });
        return tree;
    }
}
