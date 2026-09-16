// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingVersionStateTests
{
    private const string Item = "/documents/main/assets/item";
    private const string Version = Item + "/versions/v1";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompleteVersionCollectionsRejectMissingAndDuplicateDefaultFlags(bool duplicate)
    {
        var tree = Changed(record =>
        {
            var xid = record["entity"]?["xid"]?.GetValue<string>();
            if (xid == (duplicate ? Item + "/versions/v2" : Version)) { record["entity"]!["isdefault"] = duplicate; }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Collection, Item + "/versions")).AsTask(),
            FederationErrorCode.InvalidPackage);
        await NoDocuments(tree);
    }

    [Test]
    [Arguments(FederationOperation.Entity, Version, false)]
    [Arguments(FederationOperation.Entity, Item, true)]
    [Arguments(FederationOperation.Collection, Item + "/versions", false)]
    [Arguments(FederationOperation.Document, Item, true)]
    public async Task SelectedAncestryCannotBeMissingOrCyclic(FederationOperation operation, string xid, bool cycle)
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == Version)
            {
                record["entity"]!["ancestorid"] = cycle ? "v2" : "missing";
            }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(operation, xid)).AsTask(), FederationErrorCode.InvalidPackage);
        await NoDocuments(tree);
    }

    [Test]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task TheCompleteIndexEnforcesRetentionBeforeReturningASelectedVersion(int maximum, bool accepted)
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == "/")
            {
                record["entity"]!["modelsource"]!["groups"]!["documents"]!["resources"]!["assets"]!["maxversions"] = maximum;
            }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        if (accepted)
        {
            var result = await mapping.ReadAsync(new(FederationOperation.Entity, Version));
            await Assert.That(result.SelectedXid).IsEqualTo(Version);
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("isdefault").GetBoolean()).IsTrue();
        }
        else
        {
            await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, Version)).AsTask(),
                FederationErrorCode.InvalidPackage);
        }
        await NoDocuments(tree);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SingleVersionRootIsCheckedAcrossTheResourcesVersionMetadata(bool separateRoot)
    {
        var tree = Changed(record =>
        {
            var xid = record["entity"]?["xid"]?.GetValue<string>();
            if (xid == "/")
            {
                record["entity"]!["modelsource"]!["groups"]!["documents"]!["resources"]!["assets"]!["singleversionroot"] = true;
            }
            if (xid == Item + "/versions/v2" && separateRoot) { record["entity"]!["ancestorid"] = "v2"; }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        if (separateRoot)
        {
            await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, Version)).AsTask(),
                FederationErrorCode.InvalidPackage);
        }
        else
        {
            var result = await mapping.ReadAsync(new(FederationOperation.Entity, Version));
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("ancestorid").GetString()).IsEqualTo("v1");
        }
        await NoDocuments(tree);
    }

    [Test]
    [Arguments(FederationOperation.Entity, Version)]
    [Arguments(FederationOperation.Entity, Item)]
    [Arguments(FederationOperation.Collection, Item + "/versions")]
    [Arguments(FederationOperation.Document, Item)]
    public async Task MatchVersionsCannotBeIgnoredByAnExactOrCompleteRead(FederationOperation operation, string xid)
    {
        var tree = Changed(record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() == "/")
            {
                record["entity"]!["modelsource"]!["groups"]!["documents"]!["resources"]!["assets"]!["attributes"]!["purpose"]!["matchversions"] = true;
            }
        });
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(operation, xid)).AsTask(), FederationErrorCode.InvalidPackage);
        await NoDocuments(tree);
    }

    [Test]
    [Arguments("9007199254740992", "9007199254740992.0", true)]
    [Arguments("9007199254740992", "9007199254740993", false)]
    public async Task NestedMatchingScalarsUseExactNumbers(string first, string second, bool accepted)
    {
        var tree = MatchingScalar("integer", first, second);
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        if (accepted)
        {
            var result = await mapping.ReadAsync(new(FederationOperation.Collection, Item + "/versions"));
            await Assert.That(result.Metadata.GetProperty("entities").GetProperty("v1").GetProperty("settings").GetProperty("value").GetRawText())
                .IsEqualTo(first);
            await Assert.That(result.Metadata.GetProperty("entities").GetProperty("v2").GetProperty("settings").GetProperty("value").GetRawText())
                .IsEqualTo(second);
        }
        else
        {
            await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Collection, Item + "/versions")).AsTask(),
                FederationErrorCode.InvalidPackage);
        }
        await NoDocuments(tree);
    }

    [Test]
    [Arguments("\"2026-01-01T01:00:00.0000000010+01:00\"", "\"2026-01-01T00:00:00.000000001Z\"", true)]
    [Arguments("\"2026-01-01T00:00:00.000000001Z\"", "\"2026-01-01T00:00:00.000000002Z\"", false)]
    public async Task MatchingTimestampsCompareInstantsWithoutDiscardingSubTickPrecision(string first, string second, bool accepted)
    {
        var tree = MatchingScalar("timestamp", first, second);
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        if (accepted)
        {
            var result = await mapping.ReadAsync(new(FederationOperation.Entity, Version));
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("settings").GetProperty("value").GetString())
                .IsEqualTo("2026-01-01T01:00:00.0000000010+01:00");
        }
        else
        {
            await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, Version)).AsTask(),
                FederationErrorCode.InvalidPackage);
        }
        await NoDocuments(tree);
    }

    [Test]
    [Arguments(8, true)]
    [Arguments(9, false)]
    public async Task SelectedAncestryHasAnInclusiveTraversalDepthBudget(int count, bool accepted)
    {
        var tree = Chain(count);
        var budget = new FederationReadBudget(new(maxDepth: 8));
        await using var mapping = await DirectoryMapping.OpenAsync(tree, budget);
        var xid = Item + "/versions/v" + count;

        if (accepted)
        {
            var result = await mapping.ReadAsync(new(FederationOperation.Entity, xid));
            await Assert.That(result.SelectedXid).IsEqualTo(xid);
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("ancestorid").GetString()).IsEqualTo("v7");
        }
        else
        {
            await Check.Error(() => mapping.ReadAsync(new(FederationOperation.Entity, xid)).AsTask(),
                FederationErrorCode.LimitExceeded);
        }
        await NoDocuments(tree);
    }

    private static MemoryTreeReader MatchingScalar(string type, string first, string second) => Changed(record =>
    {
        var xid = record["entity"]?["xid"]?.GetValue<string>();
        if (xid == "/")
        {
            record["entity"]!["modelsource"]!["groups"]!["documents"]!["resources"]!["assets"]!["attributes"]!["settings"] =
                JsonNode.Parse("{\"type\":\"object\",\"attributes\":{\"value\":{\"type\":\"" + type + "\",\"matchversions\":true}}}");
        }
        if (xid is Version or Item + "/versions/v2")
        {
            record["entity"]!["settings"] = JsonNode.Parse("{\"value\":" + (xid == Version ? first : second) + "}");
        }
    });

    private static MemoryTreeReader Chain(int count)
    {
        var original = MemoryTreeReader.Load().Files;
        var additions = new JsonArray();
        for (var index = 3; index <= count; index++)
        {
            var record = JsonNode.Parse(original["records/na.json"])!;
            var xid = Item + "/versions/v" + index;
            record["entity"]!["xid"] = xid;
            record["entity"]!["versionid"] = "v" + index;
            record["entity"]!["ancestorid"] = "v" + (index - 1);
            record["entity"]!["isdefault"] = false;
            var path = "records/chain-v" + index + ".json";
            var bytes = Encoding.UTF8.GetBytes(record.ToJsonString());
            original.Add(path, bytes);
            JsonNode reference = new JsonObject
            {
                ["kind"] = "version",
                ["xid"] = xid,
                ["href"] = path,
                ["size"] = bytes.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            };
            additions.Add(reference);
        }
        return Tree(MappingUriFixture.Rewrite(original, record =>
        {
            if (record["xid"]?.GetValue<string>() != Item + "/versions") { return; }
            var entries = record["entries"]!.AsArray();
            foreach (var addition in additions) { entries.Add(addition!.DeepClone()); }
            record["count"] = entries.Count;
            record["entries"] = new JsonArray(entries.OrderBy(entry => entry!["xid"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(entry => entry!.DeepClone()).ToArray());
        }));
    }

    private static MemoryTreeReader Changed(Action<JsonObject> change) =>
        Tree(MappingUriFixture.Rewrite(MemoryTreeReader.Load().Files, change));

    private static MemoryTreeReader Tree(IReadOnlyDictionary<string, byte[]> files)
    {
        var tree = new MemoryTreeReader();
        foreach (var pair in files) { tree.Files.Add(pair.Key, pair.Value); }
        return tree;
    }

    private static async Task NoDocuments(MemoryTreeReader tree)
    {
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }
}
