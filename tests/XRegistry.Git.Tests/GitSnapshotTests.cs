// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Git.Tests;

public class GitSnapshotTests
{
    [Test]
    [Arguments(GitHashAlgorithm.Sha1, "commit")]
    [Arguments(GitHashAlgorithm.Sha1, "tag")]
    [Arguments(GitHashAlgorithm.Sha1, "nestedTag")]
    [Arguments(GitHashAlgorithm.Sha256, "commit")]
    [Arguments(GitHashAlgorithm.Sha256, "tag")]
    [Arguments(GitHashAlgorithm.Sha256, "nestedTag")]
    public async Task VerifiedTagsPinExactCommitAndNestedBinaryBlob(GitHashAlgorithm algorithm, string selected)
    {
        using var pack = new MemoryStream(Fixtures.Pack(algorithm, "snapshot"));
        var objects = GitObjectReader.ReadPack(pack, algorithm);
        var snapshot = GitSnapshot.Open(objects, GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, selected)));

        await Assert.That(snapshot.CommitId.ToString()).IsEqualTo(Fixtures.Id(algorithm, "commit"));
        await Assert.That(snapshot.RootTreeId.ToString()).IsEqualTo(Fixtures.Id(algorithm, "root"));
        await Assert.That(Convert.ToHexString(snapshot.ReadBlob("nested/raw.bin").Content)).IsEqualTo("00FF0D0A62696E6172790A");
        await Assert.That(snapshot.ReadBlob("empty").Length).IsEqualTo(0L);
        await Assert.That(Convert.ToHexString(snapshot.ReadBlob("hello").Content)).IsEqualTo("68656C6C6F0A");
    }

    [Test]
    public async Task TagDepthAndTreeVisitsHaveIndependentInclusiveLimits()
    {
        const GitHashAlgorithm algorithm = GitHashAlgorithm.Sha256;
        using var pack = new MemoryStream(Fixtures.Pack(algorithm, "snapshot"));
        var objects = GitObjectReader.ReadPack(pack, algorithm);
        var selected = GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "nestedTag"));
        await Assert.That(GitSnapshot.Open(objects, selected, new GitReadLimits { MaxTagDepth = 2 }).CommitId.ToString())
            .IsEqualTo(Fixtures.Id(algorithm, "commit"));
        await TestAssert.Fails(() => GitSnapshot.Open(objects, selected, new GitReadLimits { MaxTagDepth = 1 }),
            GitFailure.LimitExceeded);
        var snapshot = GitSnapshot.Open(objects, selected, new GitReadLimits { MaxTraversalEntries = 3 });
        await TestAssert.Fails(() => snapshot.ReadBlob("nested/raw.bin"), GitFailure.LimitExceeded);
    }

    [Test]
    public async Task MappingAdapterDistinguishesMissingPathsFromMissingPinnedObjects()
    {
        const GitHashAlgorithm algorithm = GitHashAlgorithm.Sha1;
        using var pack = new MemoryStream(Fixtures.Pack(algorithm, "snapshot"));
        var objects = GitObjectReader.ReadPack(pack, algorithm);
        var snapshot = GitSnapshot.Open(objects, GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "tag")));
        var reader = new GitDocumentTreeReader(snapshot, new Uri("https://git.example/repo"), "");
        using var stream = await reader.OpenReadAsync("nested/raw.bin");
        await Assert.That(stream!.ReadByte()).IsEqualTo(0);
        await Assert.That(stream.ReadByte()).IsEqualTo(255);
        await Assert.That(await reader.OpenReadAsync("nested/missing.bin")).IsNull();
        await Assert.That(reader.Context.IsImmutable).IsTrue();
        await Assert.That(reader.Context.Revision).IsEqualTo(Fixtures.Id(algorithm, "commit"));

        var incomplete = GitObjectSet.Create(algorithm,
            objects.Objects.Where(value => value.Id.ToString() != Fixtures.Id(algorithm, "binary")));
        var broken = new GitDocumentTreeReader(
            GitSnapshot.Open(incomplete, snapshot.CommitId), new Uri("https://git.example/repo"), "");
        await Assert.That(async () => await broken.OpenReadAsync("nested/raw.bin")).Throws<XRegistry.Federation.FederationException>();
    }
}
