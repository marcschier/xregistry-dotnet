// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingResultLimitTests
{
    [Test]
    [Arguments(FederationOperation.Capabilities)]
    [Arguments(FederationOperation.Model)]
    [Arguments(FederationOperation.Entity)]
    public async Task EveryMetadataResultHonorsItsExactEncodedByteBudget(FederationOperation operation)
    {
        var request = new FederationReadRequest(operation, "/");
        await using var baseline = await DirectoryMapping.OpenAsync(MemoryTreeReader.Load());
        var expected = JsonNode.Parse((await baseline.ReadAsync(request)).Metadata.GetRawText())!.ToJsonString();
        var exact = Encoding.UTF8.GetByteCount(expected);
        await using var accepted = await DirectoryMapping.OpenAsync(MemoryTreeReader.Load(),
            new(new(maxResultBytes: exact)));
        var result = await accepted.ReadAsync(request);
        await Assert.That(JsonNode.Parse(result.Metadata.GetRawText())!.ToJsonString()).IsEqualTo(expected);

        var limitedTree = MemoryTreeReader.Load();
        await using var rejected = await DirectoryMapping.OpenAsync(limitedTree, new(new(maxResultBytes: exact - 1)));
        await Check.Error(() => rejected.ReadAsync(request).AsTask(), FederationErrorCode.LimitExceeded);
        await Assert.That(limitedTree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(limitedTree.DisposedStreams).IsEqualTo(limitedTree.Reads.Count);
    }
}
