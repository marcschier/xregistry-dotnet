// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciDocumentTests
{
    internal const string Sample = "/dirs/main/files/sample";

    [Test]
    public async Task FrozenVersionLookupReadsOnlyItsRoutingPathAndPayload()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, Sample + "/versions/v2"));
        await Assert.That(result.Target).IsEqualTo(Sample + "/versions/v2");
        await Assert.That(result.SelectedXid).IsEqualTo(Sample + "/versions/v2");
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"number\"}\n");
        await Assert.That(result.Document!.ContentType).IsEqualTo("application/schema+json");
        await Assert.That(tree.Reads[^1]).IsEqualTo(
            "blobs/sha256/e562cc019db9f5cb554d77b7cdd6b0e0640c1fd7a045e36b1b164879a24ac87d");
        await Assert.That(tree.Reads.Contains(
            "blobs/sha256/85803e087e684bdab3e5d6c2dd1af627da83382db625be9a42aea3d4d06539be")).IsFalse();
        await Assert.That(tree.Reads.Contains(
            "blobs/sha256/47a8404dd5bb287e70354f4aa0f7bf250e4fefe66a72cf2ac5475a802fd45968")).IsFalse();
        await Assert.That(string.Join(",", tree.Reads.Select(p => p.Replace("blobs/sha256/", "", StringComparison.Ordinal))))
            .IsEqualTo("oci-layout,index.json," +
                "c937f902c54ca9c63e510bab3b4ec07ec3775ad338ac030ac93d916a736efba6," +
                "8b12b7aede7c57cede44697e325f2936960583fa02ef734194a493749872df06," +
                "bbb8431c731d4be5db0dda92683801758a2dc25a2fc7e9908630eccdcd4d5adf," +
                "eebb063319204754b9460f9a59f70e5a188108910f4d311b4bca6b5def0c2754," +
                "7321cbe88ee2dca9c70fb3f4020cb6811676547a5f688befdc6f7e153a4b91c6," +
                "792822629fffa09fc45efd1281aadb88d502d9cfd596407be3fcd16a4f729030," +
                "b8786ba073b0998562c0d16fc74cf55856c2ddc2587bdfc864dd2a5ed7828fc9," +
                "277207f15b1951090e9ee7ee999b81c4bbcab5bebfe624abba1e99bc43e310c1," +
                "295a081643d334f73d00466295e37aa2c92c0347a8932529828a568df7e6b4a3," +
                "bd619a454c1060773adbd6a0613b1d26fc451d1f5720e8345d0b96c47af23c37," +
                "c1812842e586a27ae9d1037349bcd4c5ae4a819bdb07017dc3d0bb8b5187d119," +
                "af6422ebdc770ab37ab3bbaaa1e7469146dbc3acc2523cb87cc554aa00cb8713," +
                "3311d4b1bb8e790703c637d1cd0d12f482b34f94f927e97b97d1a1978491ab22," +
                "7c9437a2acb55dfd866aa6198d70be51021b12744d71e505d332c1ab8f79ab60," +
                "f86a54fd29327feca1af033dbaf76ce7191573da03fe163c6896dcff449adc72," +
                "00e22b0a556fdea2f7f40d5e739069bd3b4d1ccc5838c7d0645deddd0b15dc34," +
                "e2b7f1dfdb131ccd547802766a7dbbc86162e188ec1ad281668021e23173a928," +
                "dab7faf03ac8e7ea11f6ffa3db448d742432fc9ed09dcbc7ca3a5f97c5475bfa," +
                "643cfb6ca41afe6c0440ce8b210987e7a3b90955b3596f2a1635c1a75f0d6768," +
                "63495a05486a49c7877f949dcd602eddc40e848329a64d00d6bdc11b76ae7d79," +
                "e562cc019db9f5cb554d77b7cdd6b0e0640c1fd7a045e36b1b164879a24ac87d");
        await Assert.That(tree.DisposedStreams).IsEqualTo(tree.Reads.Count);
        await snapshot.DisposeAsync();
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document))).IsEqualTo("{\"type\":\"number\"}\n");
    }

    [Test]
    [Arguments(Sample)]
    [Arguments("/dirs/main/files/alias")]
    [Arguments("/imports/shared/files/alias")]
    public async Task FrozenDefaultUsesV1NotNewerV2AndImportedAliasesKeepSourceIdentity(string target)
    {
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline");
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, target));
        await Assert.That(result.Target).IsEqualTo(target);
        await Assert.That(result.SelectedXid).IsEqualTo(Sample + "/versions/v1");
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
        await Assert.That(result.Context.Revision).IsEqualTo(OciBootstrapTests.Offline);
    }

    [Test]
    public async Task MetadataOnlyAndZeroByteDocumentsAreDistinct()
    {
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document,
            "/dirs/main/notes/info")).AsTask(), FederationErrorCode.UnsupportedOperation);
        var empty = await snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/empty"));
        await Assert.That(empty.Document!.Length).IsEqualTo(0L);
        await Assert.That(empty.Document.ContentType).IsEqualTo("text/plain; charset=utf-8");
        await Assert.That((await Check.Bytes(empty.Document)).Length).IsEqualTo(0);
        var binary = await snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/binary"));
        await Assert.That(Convert.ToHexString(await Check.Bytes(binary.Document!))).IsEqualTo("00017F80FF0D0A");
    }

    [Test]
    [Arguments("/dirs/main/files/dangling")]
    [Arguments("/dirs/main/files/chain")]
    public async Task AliasChainsAndDanglingAliasesDoNotRetrieveDocuments(string target)
    {
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(MemoryLayout.Load(), "offline");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, target)).AsTask(), FederationErrorCode.NotFound);
    }

    [Test]
    public async Task LinkedExternalContentIsNeverReplacedByTheUnusedLayer()
    {
        var tree = MemoryLayout.Load();
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "linked");
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document,
            Sample + "/versions/v2")).AsTask(), FederationErrorCode.Unavailable);
        await Assert.That(tree.Reads.Contains(
            "blobs/sha256/44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a")).IsFalse();
    }
}
