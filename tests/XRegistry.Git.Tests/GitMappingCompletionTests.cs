// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;
using XRegistry.Federation;
using XRegistry.Federation.Tests;

namespace XRegistry.Git.Tests;

public class GitMappingCompletionTests
{
    private const string Item = "/documents/main/assets/item";
    private static readonly Uri Repository = new("https://git.example/completion.git");

    [Test]
    [Arguments("ambiguous", FederationErrorCode.Ambiguous)]
    [Arguments("late-invalid", FederationErrorCode.InvalidPackage)]
    [Arguments("literal", null)]
    public async Task GitBackedLabelsInspectLaterMetadataAndKeepWildcardLookingValuesLiteral(
        string scenario, FederationErrorCode? expected)
    {
        var files = MappingUriFixture.Rewrite(Original(), record =>
        {
            var xid = record["entity"]?["xid"]?.GetValue<string>();
            if (xid == Item + "/versions/v1") { record["entity"]!["labels"]!["stage"] = scenario == "literal" ? "p*" : "pick"; }
            if (xid == Item + "/versions/v2")
            {
                record["entity"]!["labels"]!["stage"] = scenario == "ambiguous" ? "pick" : "production";
                if (scenario == "late-invalid") { record["entity"]!["epoch"] = "not-an-epoch"; }
            }
        });
        var snapshot = BinaryTestData.Snapshot(GitHashAlgorithm.Sha256, files);
        await using var mapping = await DirectoryMapping.OpenAsync(new GitDocumentTreeReader(snapshot, Repository,
            requestedRevision: "refs/tags/pinned"));
        var request = new FederationReadRequest(FederationOperation.Collection, Item + "/versions",
            new("stage", scenario == "literal" ? "p*" : "pick"));
        if (expected is { } code) { await Failure(() => mapping.ReadAsync(request).AsTask(), code); }
        else
        {
            var result = await mapping.ReadAsync(request);
            await Assert.That(result.SelectedXid).IsEqualTo(Item + "/versions/v1");
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("labels").GetProperty("stage").GetString()).IsEqualTo("p*");
            await Assert.That(result.Context.Source).IsEqualTo(Repository.AbsoluteUri);
            await Assert.That(result.Context.Revision).IsEqualTo(snapshot.CommitId.ToString());
            await Assert.That(result.Context.RequestedRevision).IsEqualTo("refs/tags/pinned");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GitBackedExternalDescriptorsAndOneHopAliasesRetainContextAndClosureRules(bool offline)
    {
        var files = MappingUriFixture.Rewrite(Original(), record =>
        {
            var xid = record["entity"]?["xid"]?.GetValue<string>();
            if (xid == "/") { record["snapshot"]!["completeness"] = offline ? "offline-complete" : "linked"; }
            if (xid == Item + "/versions/v2")
            {
                record["entity"]!["asseturl"] = "../uncaptured.json";
                record["document"] = new JsonObject
                {
                    ["kind"] = "external",
                    ["uri"] = "../uncaptured.json",
                    ["base"] = "https://must-not-fetch.invalid/original/",
                };
            }
        });
        var snapshot = BinaryTestData.Snapshot(GitHashAlgorithm.Sha256, files);
        await using var mapping = await DirectoryMapping.OpenAsync(new GitDocumentTreeReader(snapshot, Repository,
            requestedRevision: "refs/heads/pinned"));
        var request = new FederationReadRequest(FederationOperation.Document, "/mirrors/local/assets/copy/versions/v2");
        if (offline)
        {
            await Failure(() => mapping.ReadAsync(request).AsTask(), FederationErrorCode.InvalidPackage);
            await Failure(() => mapping.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
            return;
        }
        var result = await mapping.ReadAsync(request);
        await Assert.That(result.SelectedXid).IsEqualTo(Item + "/versions/v2");
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.GetProperty("uri").GetString()).IsEqualTo("../uncaptured.json");
        await Assert.That(result.ExternalDocument.GetProperty("base").GetString()).IsEqualTo("https://must-not-fetch.invalid/original/");
        await Assert.That(result.Context.Source).IsEqualTo(Repository.AbsoluteUri);
        await Assert.That(result.Context.Revision).IsEqualTo(snapshot.CommitId.ToString());
        await Assert.That(result.Context.RootPath).IsEqualTo("xregistry");
        await Assert.That((await mapping.ValidateAsync()).Documents).IsEqualTo(2);
        await Failure(() => mapping.ReadAsync(new(FederationOperation.Document, "/mirrors/local/assets/chain")).AsTask(),
            FederationErrorCode.NotFound);
        await Failure(() => mapping.ReadAsync(new(FederationOperation.Document, "/mirrors/local/assets/dangling")).AsTask(),
            FederationErrorCode.NotFound);
    }

    [Test]
    public async Task GitBackedFullClosureFindsAnUnvisitedMissingDocumentAfterAValidSelectiveRead()
    {
        var files = Original();
        files.Remove("documents/n0.bin");
        var snapshot = BinaryTestData.Snapshot(GitHashAlgorithm.Sha256, files);
        await using var mapping = await DirectoryMapping.OpenAsync(new GitDocumentTreeReader(snapshot, Repository));
        var selected = await mapping.ReadAsync(new(FederationOperation.Document, Item));
        using var stream = selected.Document!.OpenRead();
        using var reader = new StreamReader(stream);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("{\"hello\":\"world\"}\n");
        await Failure(() => mapping.ValidateAsync().AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(mapping.Context.Revision).IsEqualTo(snapshot.CommitId.ToString());
    }

    [Test]
    public async Task GitObjectIdentityDoesNotReplaceAnIndependentMappingDocumentDigest()
    {
        var files = Original();
        var changed = files["documents/n1.bin"].ToArray();
        changed[0] ^= 1;
        files["documents/n1.bin"] = changed;
        var snapshot = BinaryTestData.Snapshot(GitHashAlgorithm.Sha256, files);
        await using var mapping = await DirectoryMapping.OpenAsync(new GitDocumentTreeReader(snapshot, Repository));
        await Failure(() => mapping.ReadAsync(new(FederationOperation.Document, Item)).AsTask(),
            FederationErrorCode.IntegrityError);
    }

    [Test]
    [Arguments("mapping")]
    [Arguments("core")]
    public async Task UnsupportedCapturedVersionsFailBeforeGitMappingReadsBecomeSuccessful(string version)
    {
        var files = MappingUriFixture.Rewrite(Original(), record =>
        {
            if (record["entity"]?["xid"]?.GetValue<string>() != "/") { return; }
            if (version == "mapping") { record["formatversion"] = "999"; }
            else { record["entity"]!["specversion"] = "unsupported-core"; }
        });
        var snapshot = BinaryTestData.Snapshot(GitHashAlgorithm.Sha256, files);
        await Failure(() => DirectoryMapping.OpenAsync(new GitDocumentTreeReader(snapshot, Repository)).AsTask(),
            FederationErrorCode.UnsupportedVersion);
    }

    [Test]
    [Arguments("120000", FederationErrorCode.PolicyDenied)]
    [Arguments("160000", FederationErrorCode.UnsupportedOperation)]
    public async Task SelectedGitIndirectionsFailButUnrelatedIndirectionsDoNotInvalidateOrdinaryBytes(
        string mode, FederationErrorCode expected)
    {
        const GitHashAlgorithm algorithm = GitHashAlgorithm.Sha256;
        var blob = Object(algorithm, GitObjectType.Blob, "ordinary executable-mode data"u8.ToArray());
        var link = Object(algorithm, GitObjectType.Blob, "../must-not-follow"u8.ToArray());
        var target = mode == "160000" ? GitObjectId.Parse(algorithm, new string('1', 64)) : link.Id;
        var tree = Object(algorithm, GitObjectType.Tree,
            [.. "100755 good\0"u8, .. blob.Id.Bytes, .. Encoding.ASCII.GetBytes(mode + " link\0"), .. target.Bytes]);
        var commit = Commit(algorithm, tree.Id);
        var snapshot = GitSnapshot.Open(GitObjectSet.Create(algorithm, [blob, link, tree, commit]), commit.Id);
        var source = new GitDocumentTreeReader(snapshot, Repository, "");
        using var ordinary = await source.OpenReadAsync("good");
        using var reader = new StreamReader(ordinary!);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("ordinary executable-mode data");
        await Failure(async () =>
        {
            using var selected = await source.OpenReadAsync("link");
        }, expected);
    }

    [Test]
    public async Task MissingPinnedObjectsHaveAnExactErrorDistinctFromAbsentTreeNames()
    {
        const GitHashAlgorithm algorithm = GitHashAlgorithm.Sha256;
        using var pack = new MemoryStream(Fixtures.Pack(algorithm, "snapshot"));
        var all = GitObjectReader.ReadPack(pack, algorithm);
        var incomplete = GitObjectSet.Create(algorithm, all.Objects.Where(item => item.Id.ToString() != Fixtures.Id(algorithm, "binary")));
        var snapshot = GitSnapshot.Open(incomplete, GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "commit")));
        var source = new GitDocumentTreeReader(snapshot, Repository, "");
        await Assert.That(await source.OpenReadAsync("nested/absent.bin")).IsNull();
        await Failure(async () =>
        {
            using var missing = await source.OpenReadAsync("nested/raw.bin");
        }, FederationErrorCode.InconsistentSnapshot);
        await Assert.That(source.Context.Revision).IsEqualTo(Fixtures.Id(algorithm, "commit"));
    }

    [Test]
    public async Task NonCommitSelectionsAndContradictoryTagTypesNeverSubstituteAReachableCommit()
    {
        const GitHashAlgorithm algorithm = GitHashAlgorithm.Sha256;
        using var pack = new MemoryStream(Fixtures.Pack(algorithm, "snapshot"));
        var objects = GitObjectReader.ReadPack(pack, algorithm);
        await TestAssert.Fails(() => GitSnapshot.Open(objects, GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "binary"))),
            GitFailure.UnsupportedFormat);
        await TestAssert.Fails(() => GitSnapshot.Open(objects, GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "root"))),
            GitFailure.UnsupportedFormat);
        var tag = Object(algorithm, GitObjectType.Tag,
            Encoding.ASCII.GetBytes("object " + Fixtures.Id(algorithm, "binary") + "\ntype commit\ntag wrong-type\n\nfixture\n"));
        var extended = GitObjectSet.Create(algorithm, objects.Objects.Append(tag));
        await TestAssert.Fails(() => GitSnapshot.Open(extended, tag.Id), GitFailure.MalformedData);
    }

    private static Dictionary<string, byte[]> Original() =>
        MappingUriFixture.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping"));

    private static async Task Failure(Func<Task> action, FederationErrorCode expected)
    {
        var error = await Assert.That(action).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected an explicit Git-backed mapping failure.");
        await Assert.That(error.Code).IsEqualTo(expected);
    }

    private static GitObject Object(GitHashAlgorithm algorithm, GitObjectType type, byte[] bytes) =>
        GitObject.Verify(BinaryTestData.Id(algorithm, type.ToString().ToLowerInvariant(), bytes), type, bytes);

    private static GitObject Commit(GitHashAlgorithm algorithm, GitObjectId tree) => Object(algorithm, GitObjectType.Commit,
        Encoding.ASCII.GetBytes("tree " + tree + "\nauthor Fixture <fixture@example.invalid> 0 +0000\n" +
            "committer Fixture <fixture@example.invalid> 0 +0000\n\nfixture\n"));
}
