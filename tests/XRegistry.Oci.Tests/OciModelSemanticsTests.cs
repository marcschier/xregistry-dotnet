// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciModelSemanticsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task VersionReadsDischargeOwningGroupEqualsConstraints(bool agrees)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (record, media) =>
        {
            if (media != GraphFixture.Config) { return GraphFixture.Encode(record); }
            var xid = record["entity"]!["xid"]!.GetValue<string>();
            if (xid == "/")
            {
                var source = record["modelresolved"]!;
                source["groups"]!["dirs"]!["attributes"] = new JsonObject { ["site"] = new JsonObject { ["type"] = "string" } };
                source["groups"]!["dirs"]!["resources"]!["files"]!["attributes"]!["site"] = new JsonObject { ["type"] = "string" };
                source["groups"]!["dirs"]!["constraints"] = new JsonObject { ["files.site"] = new JsonObject { ["equals"] = "site" } };
                record["entity"]!["modelsource"] = source.DeepClone();
                record["entity"]!["model"] = JsonNode.Parse(RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString())).EffectiveModel.RootElement.GetRawText());
            }
            if (xid == "/dirs/main") { record["entity"]!["site"] = "east"; }
            if (record["kind"]!.GetValue<string>() == "version" && xid.StartsWith("/dirs/main/files/", StringComparison.Ordinal))
            {
                record["entity"]!["site"] = xid == OciDocumentTests.Sample + "/versions/v1" && !agrees ? "west" : "east";
            }
            return GraphFixture.Encode(record);
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        if (agrees)
        {
            var result = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample));
            await Assert.That(result.SelectedXid).IsEqualTo(OciDocumentTests.Sample + "/versions/v1");
        }
        else
        {
            await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample)).AsTask(),
                FederationErrorCode.InvalidPackage);
        }
    }

    [Test]
    public async Task OriginalIncludesRemainProvenanceAndResolvedImportsNeverFetchNetworkModels()
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Record(tree, "/", record =>
            record["entity"]!["modelsource"] = new JsonObject { ["$include"] = "https://never-fetch.invalid/model.json#/model" });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Model, "/"));
        await Assert.That(result.Value.GetProperty("modelsource").GetProperty("$include").GetString())
            .IsEqualTo("https://never-fetch.invalid/model.json#/model");
        await Assert.That(snapshot.ResolvedModelSource.GetProperty("groups").GetProperty("imports").GetProperty("ximportresources")[0].GetString())
            .IsEqualTo("/dirs/files");
        await Assert.That(tree.Reads.Count).IsEqualTo(5);
    }

    [Test]
    public async Task StructurallyIdenticalResourceTypesDoNotAuthorizeLocalAliases()
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Record(tree, "/", record =>
        {
            foreach (var source in new[] { record["modelresolved"]!, record["entity"]!["modelsource"]! })
            {
                source["groups"]!["imports"]!.AsObject().Remove("ximportresources");
                source["groups"]!["imports"]!["resources"] = new JsonObject
                {
                    ["files"] = source["groups"]!["dirs"]!["resources"]!["files"]!.DeepClone(),
                };
            }
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, "/imports/shared/files/alias")).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("default-missing")]
    [Arguments("default-flag")]
    [Arguments("ancestor-missing")]
    [Arguments("ancestor-cycle")]
    [Arguments("embedded-with-url")]
    [Arguments("metadata-document")]
    [Arguments("external-relative")]
    [Arguments("alias-remote")]
    [Arguments("computed")]
    [Arguments("bad-timestamp")]
    public async Task FullClosureRejectsContradictoryCoreRecordSemantics(string mutation)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (record, media) =>
        {
            if (media != GraphFixture.Config) { return GraphFixture.Encode(record); }
            var xid = record["entity"]!["xid"]!.GetValue<string>();
            if (xid == OciDocumentTests.Sample + "/meta" && mutation == "default-missing") { record["entity"]!["defaultversionid"] = "v99"; }
            if (xid == "/dirs/main/files/alias/meta" && mutation == "alias-remote") { record["entity"]!["xref"] = "https://remote.example.org/files/target"; }
            if (xid == OciDocumentTests.Sample + "/versions/v1")
            {
                if (mutation == "default-flag") { record["entity"]!["isdefault"] = false; }
                if (mutation == "ancestor-missing") { record["entity"]!["ancestorid"] = "absent"; }
                if (mutation == "ancestor-cycle") { record["entity"]!["ancestorid"] = "v2"; }
                if (mutation == "embedded-with-url") { record["entity"]!["fileurl"] = "https://external.example.org/doc"; }
                if (mutation == "external-relative")
                {
                    record["document"]!["mode"] = "external";
                    record["entity"]!["fileurl"] = "../unresolved";
                }
                if (mutation == "computed") { record["entity"]!["self"] = "oci://invented/view"; }
                if (mutation == "bad-timestamp") { record["entity"]!["createdat"] = "2026-02-30T00:00:00Z"; }
            }
            if (xid == "/dirs/main/notes/info/versions/v1" && mutation == "metadata-document") { record["document"]!["mode"] = "embedded"; }
            return GraphFixture.Encode(record);
        });
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("unresolved")]
    [Arguments("full-not-full")]
    [Arguments("hasdocument")]
    [Arguments("writable")]
    [Arguments("flags")]
    [Arguments("include-shape")]
    [Arguments("missing-mutable")]
    [Arguments("missing-entities")]
    [Arguments("xref-type")]
    public async Task CapturedModelAndCapabilitiesMustActuallyDescribeTheReadOnlySnapshot(string mutation)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Record(tree, "/", record =>
        {
            switch (mutation)
            {
                case "unresolved": record["modelresolved"]!["$include"] = "https://never-fetch.invalid/"; break;
                case "full-not-full": record["entity"]!["model"] = record["modelresolved"]!.DeepClone(); break;
                case "hasdocument": record["entity"]!["model"]!["groups"]!["dirs"]!["resources"]!["files"]!["hasdocument"] = false; break;
                case "writable": record["entity"]!["capabilities"]!["available"]!["entities"]!["mutable"] = true; break;
                case "flags": record["entity"]!["capabilities"]!["flags"] = new JsonArray("doc"); break;
                case "include-shape": record["entity"]!["modelsource"] = new JsonObject { ["$include"] = 7 }; break;
                case "missing-mutable": record["entity"]!["capabilities"]!["available"]!["entities"]!.AsObject().Remove("mutable"); break;
                case "missing-entities": record["entity"]!["capabilities"]!["available"]!.AsObject().Remove("entities"); break;
                case "xref-type": record["entity"]!["model"]!["groups"]!["dirs"]!["resources"]!["files"]!["metaattributes"]!["xref"]!["type"] = 7; break;
            }
        });
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task AnOfflineCompleteClaimCannotHideAnExternalVersionInAnUnvisitedBranch()
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Rewrite(tree, (record, media) =>
        {
            if (media == GraphFixture.Config && record["kind"]!.GetValue<string>() == "registry")
            {
                record["snapshot"] = "offline-complete";
            }
            return GraphFixture.Encode(record);
        }, "linked");
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "linked");
        var selected = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample));
        await Assert.That(selected.Document!.Length).IsEqualTo(18L);
        await Check.Error(() => snapshot.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
    }
}
