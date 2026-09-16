// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciBootstrapTests
{
    internal const string Offline = "sha256:c937f902c54ca9c63e510bab3b4ec07ec3775ad338ac030ac93d916a736efba6";
    internal const string Linked = "sha256:dff871378d2678ee851fb7d97bae5a6b69b05c3d668f224a2f97127e8e8dee88";

    [Test]
    [Arguments("offline", Offline, "offline-complete")]
    [Arguments("linked", Linked, "linked")]
    public async Task FrozenRootsPinExactBytesAndPreserveCapturedModel(string reference, string digest, string snapshotClass)
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, reference);
        await Assert.That(snapshot.RootDigest).IsEqualTo(digest);
        await Assert.That(snapshot.Context.Revision).IsEqualTo(digest);
        await Assert.That(snapshot.Context.Source).IsEqualTo("file:///fixtures/oci/");
        await Assert.That(snapshot.Context.RequestedRevision).IsEqualTo(reference);
        await Assert.That(snapshot.Context.IsImmutable).IsTrue();
        await Assert.That(snapshot.SnapshotClass).IsEqualTo(snapshotClass);
        await Assert.That(snapshot.ModelSource.GetProperty("groups").GetProperty("imports")
            .GetProperty("ximportresources")[0].GetString()).IsEqualTo("/dirs/files");
        await Assert.That(snapshot.ResolvedModelSource.GetProperty("groups").GetProperty("imports")
            .GetProperty("ximportresources")[0].GetString()).IsEqualTo("/dirs/files");
        await Assert.That(ReferenceEquals(snapshot.Model.Groups["dirs"].Resources["files"],
            snapshot.Model.Groups["imports"].Resources["files"])).IsTrue();
        await Assert.That(snapshot.Model.Groups["dirs"].Resources["notes"].HasDocument).IsFalse();
        await Assert.That(FederationCapabilities.GetResolutionOwner(snapshot.Capabilities))
            .IsEqualTo(FederationResolutionOwner.Consumer);
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
        await Assert.That(tree.Reads.Count).IsEqualTo(5);
    }

    [Test]
    public async Task LayoutSelectionRejectsDuplicateTagsEvenWhenDigestsMatch()
    {
        var tree = MemoryLayout.Load();
        var index = JsonNode.Parse(tree.Files["index.json"])!;
        index["manifests"]!.AsArray().Add(index["manifests"]![0]!.DeepClone());
        tree.Files["index.json"] = System.Text.Encoding.UTF8.GetBytes(index.ToJsonString());
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.Ambiguous);
        await Assert.That(tree.Reads.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DigestSelectsAnUnadvertisedRootAndUnqualifiedSelectionIsAmbiguous()
    {
        var tree = MemoryLayout.Load();
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree).AsTask(), FederationErrorCode.Ambiguous);
        tree.Files["index.json"] = """{"schemaVersion":2,"manifests":[]}"""u8.ToArray();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, Offline);
        await Assert.That(snapshot.RootDigest).IsEqualTo(Offline);
    }

    [Test]
    public async Task RootIntegrityPrecedesParsing()
    {
        var tree = MemoryLayout.Load();
        tree.Files["blobs/sha256/" + Offline[7..]] = [0xff];
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.IntegrityError);
        await Assert.That(tree.Reads.Count).IsEqualTo(3);
        await Assert.That(tree.DisposedStreams).IsEqualTo(3);
    }
}
