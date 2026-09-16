// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Storage.File.Tests;

public class MetadataCommitTests
{
    [Test]
    public async Task MultipleChangesCommitAsOneGeneration()
    {
        using var directory = new TestDirectory();
        using (var store = LocalFileStore.Initialize(directory.Path))
        {
            using var first = await store.PrepareAsync(0,
            [
                StorageMutation.Put("one", "{\"epoch\":9007199254740993,\"missing\":null}"u8.ToArray()),
                StorageMutation.Put("two", "{\"epoch\":18446744073709551616}"u8.ToArray()),
            ]);
            await Assert.That(store.Commit(first)).IsEqualTo(1L);
            using var snapshot = store.ReadSnapshot();
            await Assert.That(snapshot.Generation).IsEqualTo(1L);
            await Assert.That(snapshot.Records.Count).IsEqualTo(2);
            await Assert.That(snapshot.Records[0].Key).IsEqualTo("one");
            await Assert.That(snapshot.Records[0].Metadata.GetProperty("epoch").GetRawText()).IsEqualTo("9007199254740993");
            await Assert.That(snapshot.Records[1].Metadata.GetProperty("epoch").GetRawText()).IsEqualTo("18446744073709551616");

            using var second = await store.PrepareAsync(1,
            [
                StorageMutation.Delete("one"),
                StorageMutation.Put("two", "{\"value\":\"changed\"}"u8.ToArray()),
            ]);
            await Assert.That(store.Commit(second)).IsEqualTo(2L);
            await Assert.That(snapshot.Records[0].Metadata.GetProperty("epoch").GetRawText()).IsEqualTo("9007199254740993");
        }

        using var reopened = LocalFileStore.Open(directory.Path);
        using var state = reopened.ReadSnapshot();
        await Assert.That(state.Generation).IsEqualTo(2L);
        await Assert.That(state.Records.Count).IsEqualTo(1);
        await Assert.That(state.Records[0].Key).IsEqualTo("two");
        await Assert.That(state.Records[0].Metadata.GetProperty("value").GetString()).IsEqualTo("changed");
    }

    [Test]
    public async Task PreparedMetadataOwnsItsInput()
    {
        using var directory = new TestDirectory();
        using var store = LocalFileStore.Initialize(directory.Path);
        var json = Encoding.UTF8.GetBytes("{\"n\":1}");
        using var candidate = await store.PrepareAsync(0, [StorageMutation.Put("key", json)]);
        json[5] = (byte)'9';
        store.Commit(candidate);

        using var snapshot = store.ReadSnapshot();
        await Assert.That(snapshot.Records[0].Metadata.GetProperty("n").GetInt32()).IsEqualTo(1);
    }
}
