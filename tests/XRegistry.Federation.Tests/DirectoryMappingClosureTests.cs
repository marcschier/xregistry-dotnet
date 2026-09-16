// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class DirectoryMappingClosureTests
{
    private const string Item = "/documents/main/assets/item";
    private const string Version = Item + "/versions/v1";

    [Test]
    [Arguments(FederationOperation.Entity, Version)]
    [Arguments(FederationOperation.Entity, "/")]
    [Arguments(FederationOperation.Collection, Item + "/versions")]
    public async Task VisitedExternalDescriptorsContradictOfflineCompletenessEvenForMetadata(
        FederationOperation operation, string xid)
    {
        var tree = External(false);
        await using var mapping = await DirectoryMapping.OpenAsync(tree);

        await Check.Error(() => mapping.ReadAsync(new(operation, xid)).AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task LinkedMetadataRetainsTheExactExternalLocatorWithoutFetchingIt()
    {
        var tree = External(true);
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ReadAsync(new(FederationOperation.Entity, Version));

        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("asseturl").GetString())
            .IsEqualTo("https://never-fetch.invalid/exact%2Fbytes?part=%25");
        await Assert.That(tree.Reads.Any(path => path.StartsWith("documents/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task FullValidationChecksTheFrozenClosureWithoutReadingUnreferencedFilesOrBuildingAResponse()
    {
        var tree = MemoryTreeReader.Load();
        tree.Files["unreferenced.json"] = [0xff];
        var budget = new FederationReadBudget(new(maxObjects: 40, maxResultBytes: 1));
        await using var mapping = await DirectoryMapping.OpenAsync(tree, budget);
        var result = await mapping.ValidateAsync();

        await Assert.That(result.RootSha256).IsEqualTo("7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf");
        await Assert.That(result.SnapshotClass).IsEqualTo("offline-complete");
        await Assert.That(result.MetadataObjects).IsEqualTo(37);
        await Assert.That(result.Documents).IsEqualTo(3);
        await Assert.That(budget.ObjectsRead).IsEqualTo(40L);
        await Assert.That(tree.Reads.Count(path => path == "registry.json")).IsEqualTo(1);
        await Assert.That(tree.Reads.Contains("unreferenced.json")).IsFalse();
        await Assert.That(tree.DisposedStreams).IsEqualTo(40);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AValidSelectiveReadDoesNotProveAnUnvisitedDocumentsIntegrity(bool missing)
    {
        var tree = MemoryTreeReader.Load();
        if (missing) { tree.Files.Remove("documents/n0.bin"); }
        else { tree.Files["documents/n0.bin"] = [0, 1, 0, 0x7f, 0x0a]; }
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var selected = await mapping.ReadAsync(new(FederationOperation.Document, Item));
        await Assert.That(await DirectoryMappingTests.ReadHex(selected.Document!)).IsEqualTo("7B2268656C6C6F223A22776F726C64227D0A");
        await Assert.That(tree.Reads.Contains("documents/n0.bin")).IsFalse();

        await Check.Error(() => mapping.ValidateAsync().AsTask(),
            missing ? FederationErrorCode.InvalidPackage : FederationErrorCode.IntegrityError);
        await Assert.That(tree.Reads.Contains("documents/n0.bin")).IsTrue();
    }

    [Test]
    public async Task FullLinkedValidationDoesNotFollowExternalReferencesOrUnusedDocumentFiles()
    {
        var tree = External(true);
        tree.Files["documents/n1.bin"] = [0xff];
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var result = await mapping.ValidateAsync();

        await Assert.That(result.SnapshotClass).IsEqualTo("linked");
        await Assert.That(result.MetadataObjects).IsEqualTo(37);
        await Assert.That(result.Documents).IsEqualTo(2);
        await Assert.That(tree.Reads.Contains("documents/n1.bin")).IsFalse();
        await Assert.That(tree.Reads.All(path => path == "registry.json" || path.StartsWith("records/", StringComparison.Ordinal) ||
            path.StartsWith("indexes/", StringComparison.Ordinal) || path.StartsWith("documents/", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ExhaustedClosureBudgetCannotBecomeSuccessOnRetryWithPartiallyCachedObjects()
    {
        var tree = MemoryTreeReader.Load();
        var budget = new FederationReadBudget(new(maxObjects: 39));
        await using var mapping = await DirectoryMapping.OpenAsync(tree, budget);

        await Check.Error(() => mapping.ValidateAsync().AsTask(), FederationErrorCode.LimitExceeded);
        await Assert.That(budget.ObjectsRead).IsEqualTo(39L);
        await Check.Error(() => mapping.ValidateAsync().AsTask(), FederationErrorCode.LimitExceeded);
        await Assert.That(budget.ObjectsRead).IsEqualTo(39L);
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
    }

    [Test]
    public async Task CancelledValidationDoesNotReadAndDoesNotPoisonTheNextOperation()
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(async () => await mapping.ValidateAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(string.Join(",", tree.Reads)).IsEqualTo("registry.json");
        await Assert.That((await mapping.ValidateAsync()).MetadataObjects).IsEqualTo(37);
        await mapping.DisposeAsync();
        await Assert.That(async () => await mapping.ValidateAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ActiveValidationRejectsConcurrentReadsValidationAndDisposal()
    {
        var tree = MemoryTreeReader.Load();
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Open = path => path == "indexes/n0.json"
            ? new GatedStream(tree.Files[path], entered, release)
            : new MemoryStream(tree.Files[path], false);
        var validation = mapping.ValidateAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(async () => await mapping.ReadAsync(new(FederationOperation.Model, "/")))
                .Throws<InvalidOperationException>();
            await Assert.That(async () => await mapping.ValidateAsync()).Throws<InvalidOperationException>();
            await Assert.That(async () => await mapping.DisposeAsync()).Throws<InvalidOperationException>();
        }
        finally
        {
            release.TrySetResult();
            await validation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Assert.That((await validation).MetadataObjects).IsEqualTo(37);
        await Assert.That((await mapping.ValidateAsync()).Documents).IsEqualTo(3);
    }

    private static MemoryTreeReader External(bool linked) => Changed(record =>
    {
        var xid = record["entity"]?["xid"]?.GetValue<string>();
        if (xid == "/" && linked) { record["snapshot"]!["completeness"] = "linked"; }
        if (xid == Version)
        {
            const string uri = "https://never-fetch.invalid/exact%2Fbytes?part=%25";
            record["entity"]!["asseturl"] = uri;
            record["document"] = new JsonObject { ["kind"] = "external", ["uri"] = uri };
        }
    });

    private static MemoryTreeReader Changed(Action<JsonObject> change)
    {
        var tree = new MemoryTreeReader();
        foreach (var pair in MappingUriFixture.Rewrite(MemoryTreeReader.Load().Files, change))
        {
            tree.Files.Add(pair.Key, pair.Value);
        }
        return tree;
    }

    private sealed class GatedStream(byte[] bytes, TaskCompletionSource entered, TaskCompletionSource release)
        : MemoryStream(bytes, false)
    {
        private bool _waiting = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_waiting)
            {
                _waiting = false;
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
