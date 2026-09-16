// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;
using XRegistry.Federation;
using XRegistry.Federation.Tests;

namespace XRegistry.Git.Tests;

public class GitDirectoryMappingXidTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PinnedGitMappingComposesEscapedEntityCollectionAndDocumentSelection(bool rewritten)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping");
        var files = rewritten ? MappingUriFixture.Renamed(fixture, true) : MappingUriFixture.Load(fixture);
        var snapshot = BinaryTestData.Snapshot(GitHashAlgorithm.Sha256, files);
        var reader = new GitDocumentTreeReader(snapshot, new Uri("https://git.example/uri-fixture"),
            "xregistry", "refs/heads/fixture");
        await using var source = await DirectoryMapping.OpenAsync(reader);
        var target = rewritten ? "/documents/group%3aone/assets/item%40stable/%76ersions/v%3a1" :
            "/%64ocuments/%6dain/assets/CON/%76ersions/a%3ab%40c.";
        var stored = rewritten ? "/%64ocuments/group%3Aone/%61ssets/item%40stable/%76ersions/v%3A1" :
            "/documents/main/assets/CON/versions/a:b@c.";
        var metadata = await source.ReadAsync(new(FederationOperation.Entity, target));
        await Assert.That(metadata.SelectedXid).IsEqualTo(stored);
        await Assert.That(metadata.Metadata.GetProperty("entity").GetProperty("xid").GetString()).IsEqualTo(stored);
        var collection = await source.ReadAsync(new(FederationOperation.Collection, target[..target.LastIndexOf('/')]));
        var id = rewritten ? "v:1" : "a:b@c.";
        await Assert.That(collection.Metadata.GetProperty("entities").GetProperty(id).GetProperty("versionid").GetString()).IsEqualTo(id);
        var document = await source.ReadAsync(new(FederationOperation.Document, target));
        await Assert.That(document.SelectedXid).IsEqualTo(stored);
        await Assert.That(document.Context.Binding).IsEqualTo("git");
        await Assert.That(document.Context.Revision).IsEqualTo(snapshot.CommitId.ToString());
        await Assert.That(document.Context.IsImmutable).IsTrue();
        await Assert.That(document.Context.RootPath).IsEqualTo("xregistry");
        await Assert.That(document.Context.RequestedRevision).IsEqualTo("refs/heads/fixture");
        await source.DisposeAsync();
        using var stream = document.Document!.OpenRead();
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray()))
            .IsEqualTo(rewritten ? "7B2268656C6C6F223A22776F726C64227D0A" : "0001FF7F0A");
    }

}
