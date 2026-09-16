// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Git.Tests;

public class GitPackTests
{
    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task VersionsTwoAndThreeReturnOnlyFullyVerifiedOwnedObjects(GitHashAlgorithm algorithm)
    {
        foreach (var name in new[] { "plainV2", "plainV3", "snapshot" })
        {
            using var source = new ChunkedReadStream(Fixtures.Pack(algorithm, name), 3);
            var objects = GitObjectReader.ReadPack(source, algorithm);
            await Assert.That(objects.Algorithm).IsEqualTo(algorithm);
            await Assert.That(objects.Count).IsEqualTo(name == "snapshot" ? 11 : 1);
            var id = GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "base"));
            var value = objects.Get(id);
            await Assert.That(value.Type).IsEqualTo(GitObjectType.Blob);
            await Assert.That(Convert.ToHexString(value.Content)).IsEqualTo("68656C6C6F20776F726C640A");
            await Assert.That(source.Disposed).IsFalse();
        }

        using var empty = new MemoryStream(Fixtures.Pack(algorithm, "emptyV2"));
        var emptyObjects = GitObjectReader.ReadPack(empty, algorithm,
            new GitReadLimits { MaxObjects = 0, MaxTotalDecompressedBytes = 0, MaxDeflateBlocks = 0 });
        await Assert.That(emptyObjects.Count).IsEqualTo(0);
        await Assert.That(emptyObjects.Objects.Count).IsEqualTo(0);
    }
}
