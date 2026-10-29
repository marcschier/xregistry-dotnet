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

    [Test]
    [Arguments("OpenAPI/3.1", "openapi/3.1")]
    [Arguments("OpenAPI/3.1", "OpenAPI/3.1")]
    public async Task ApiVersionFormatsCaseOnlyDifferencesAreAcceptedDuringSnapshotValidation(
        string firstFormat, string secondFormat)
    {
        var tree = await ApiLayout(firstFormat, secondFormat);
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");

        var result = await snapshot.ValidateAsync();
        var versions = await snapshot.ReadAsync(new(FederationOperation.Collection, ApiItem + "/versions"));

        await Assert.That(result.Configs).IsEqualTo(6);
        await Assert.That(result.RootDigest).IsEqualTo(snapshot.RootDigest);
        await Assert.That(result.SnapshotClass).IsEqualTo("offline-complete");
        await Assert.That(versions.Value.EnumerateObject().Count()).IsEqualTo(2);
        await Assert.That(versions.Value.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo(firstFormat);
        await Assert.That(versions.Value.GetProperty("v2").GetProperty("format").GetString()).IsEqualTo(secondFormat);
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    [Arguments("OpenAPI/3.1", "OpenAPI/3.0")]
    [Arguments("OpenAPI/3.1", "AsyncAPI/3.1")]
    public async Task DistinctApiVersionFormatsAreRejectedDuringSnapshotValidation(
        string firstFormat, string secondFormat)
    {
        var tree = await ApiLayout(firstFormat, firstFormat);
        GraphFixture.Rewrite(tree, (record, media) =>
        {
            if (media == GraphFixture.Config &&
                record["entity"]?["xid"]?.GetValue<string>() == ApiItem + "/versions/v2")
            {
                record["entity"]!["format"] = secondFormat;
            }
            return GraphFixture.Encode(record);
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");

        var error = await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);

        await Assert.That(error.Message).IsEqualTo(
            "Captured API Versions do not use one case-insensitively equal format identifier.");
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task SingleApiVersionFormatIsAcceptedDuringSnapshotValidation()
    {
        var tree = await ApiLayout("OpenAPI/3.1");
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");

        var result = await snapshot.ValidateAsync();
        var versions = await snapshot.ReadAsync(new(FederationOperation.Collection, ApiItem + "/versions"));

        await Assert.That(result.Configs).IsEqualTo(5);
        await Assert.That(versions.Value.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(versions.Value.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    private const string ApiItem = "/groups/main/apis/sample";

    private static async Task<MemoryLayout> ApiLayout(string firstFormat, string? secondFormat = null)
    {
        var model = RegistryJson.Parse("""
            {"groups":{"groups":{"singular":"group","resources":{"apis":{"singular":"api","hasdocument":false,
              "attributes":{"format":{"type":"string","required":true},"*":{"type":"any"}}
            }}}}}
            """);
        var records = new List<RegistryJson> { AuthoredCapture.Registry(model) };
        records.Add(ApiRecord("group", new JsonObject
        {
            ["xid"] = "/groups/main",
            ["groupid"] = "main",
            ["epoch"] = 1,
            ["createdat"] = "2026-09-01T12:00:00Z",
            ["modifiedat"] = "2026-09-01T12:00:00Z",
        }));
        records.Add(ApiRecord("resource", new JsonObject
        {
            ["xid"] = ApiItem,
            ["apiid"] = "sample",
        }));
        records.Add(ApiRecord("meta", new JsonObject
        {
            ["xid"] = ApiItem + "/meta",
            ["apiid"] = "sample",
            ["epoch"] = 1,
            ["createdat"] = "2026-09-01T12:00:00Z",
            ["modifiedat"] = "2026-09-01T12:00:00Z",
            ["readonly"] = false,
            ["defaultversionsticky"] = false,
            ["defaultversionid"] = "v1",
        }));
        records.Add(ApiVersion("v1", firstFormat, isDefault: true));
        if (secondFormat is not null) { records.Add(ApiVersion("v2", secondFormat, isDefault: false)); }

        var package = await OciSnapshotWriter.CreateAsync(new(records));
        var tree = new MemoryLayout();
        tree.Files.Add("oci-layout", System.Text.Encoding.UTF8.GetBytes("""{"imageLayoutVersion":"1.0.0"}"""));
        tree.Files.Add("index.json", await Check.Bytes(package.CreateLayoutEntryPoint("offline")));
        foreach (var item in package.Objects)
        {
            await using var stream = item.OpenRead();
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            tree.Files.Add("blobs/sha256/" + item.Digest[7..], output.ToArray());
        }
        return tree;
    }

    private static RegistryJson ApiVersion(string id, string format, bool isDefault) => ApiRecord("version", new JsonObject
    {
        ["xid"] = ApiItem + "/versions/" + id,
        ["apiid"] = "sample",
        ["epoch"] = 1,
        ["createdat"] = "2026-09-01T12:00:00Z",
        ["modifiedat"] = "2026-09-01T12:00:00Z",
        ["versionid"] = id,
        ["ancestorid"] = "v1",
        ["isdefault"] = isDefault,
        ["format"] = format,
    });

    private static RegistryJson ApiRecord(string kind, JsonObject entity)
    {
        var record = new JsonObject
        {
            ["formatversion"] = 1,
            ["kind"] = kind,
            ["entity"] = entity,
        };
        if (kind == "version") { record["document"] = new JsonObject { ["mode"] = "metadata-only" }; }
        return RegistryJson.Parse(record.ToJsonString());
    }
}
