using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;
using XRegistry.Federation;
using XRegistry.Federation.Tests;

namespace XRegistry.Git.Tests;

public class GitSnapshotRegressionTests
{
    [Test]
    [Arguments(GitHashAlgorithm.Sha1, false)]
    [Arguments(GitHashAlgorithm.Sha1, true)]
    [Arguments(GitHashAlgorithm.Sha256, false)]
    [Arguments(GitHashAlgorithm.Sha256, true)]
    public async Task BareLfsSignatureIsUnsupportedAtBothPublicSeams(GitHashAlgorithm algorithm, bool mapping)
    {
        var snapshot = Snapshot(algorithm, "registry.json",
            "version https://git-lfs.github.com/spec/v1"u8.ToArray());
        var reader = Reader(snapshot, "");

        await Fails(reader, mapping, FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1, false)]
    [Arguments(GitHashAlgorithm.Sha1, true)]
    [Arguments(GitHashAlgorithm.Sha256, false)]
    [Arguments(GitHashAlgorithm.Sha256, true)]
    public async Task ExistingBlobRootIsInvalidAtBothPublicSeams(GitHashAlgorithm algorithm, bool mapping)
    {
        foreach (var root in new[] { "xregistry", "catalog/xregistry" })
        {
            var snapshot = Snapshot(algorithm, root, "not a directory"u8.ToArray());
            await Fails(Reader(snapshot, root), mapping, FederationErrorCode.InvalidPackage);
        }
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1, false)]
    [Arguments(GitHashAlgorithm.Sha1, true)]
    [Arguments(GitHashAlgorithm.Sha256, false)]
    [Arguments(GitHashAlgorithm.Sha256, true)]
    public async Task TerminatedLfsSignaturesRemainUnsupportedAtBothPublicSeams(GitHashAlgorithm algorithm, bool mapping)
    {
        foreach (var ending in new[] { "\n", "\r\n" })
        {
            var content = Encoding.ASCII.GetBytes("version https://git-lfs.github.com/spec/v1" + ending);
            var reader = Reader(Snapshot(algorithm, "registry.json", content), "");
            await Fails(reader, mapping, FederationErrorCode.UnsupportedOperation);
        }
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1, false)]
    [Arguments(GitHashAlgorithm.Sha1, true)]
    [Arguments(GitHashAlgorithm.Sha256, false)]
    [Arguments(GitHashAlgorithm.Sha256, true)]
    public async Task ExistingTreeAtRegistryFilenameIsInvalidAtBothPublicSeams(GitHashAlgorithm algorithm, bool mapping)
    {
        var reader = Reader(Snapshot(algorithm, "registry.json/child", "not a registry"u8.ToArray()), "");
        await Fails(reader, mapping, FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task MissingRootPathAndRootDocumentRemainNotFound(GitHashAlgorithm algorithm)
    {
        var snapshot = Snapshot(algorithm, "xregistry/present.txt", "unrelated bytes"u8.ToArray());
        foreach (var root in new[] { "", "xregistry", "missing", "XRegistry", "xregistry/missing" })
        {
            var reader = Reader(snapshot, root);
            await Assert.That(await reader.OpenReadAsync("registry.json")).IsNull();
            await Fails(reader, true, FederationErrorCode.NotFound);
        }
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task LfsLookalikesPreserveExactBytesThroughReaderAndMapping(GitHashAlgorithm algorithm)
    {
        const string signature = "version https://git-lfs.github.com/spec/v1";
        foreach (var text in new[] { signature + "0\n", signature + "suffix", signature + " text\n", "prefix " + signature })
        {
            var expected = Encoding.ASCII.GetBytes(text);
            var original = MappingUriFixture.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping"));
            original["documents/n1.bin"] = expected;
            var files = MappingUriFixture.Rewrite(original, node =>
            {
                if (node["document"] is JsonObject document &&
                    document["href"]?.GetValue<string>() == "documents/n1.bin")
                {
                    document["size"] = expected.Length;
                    document["sha256"] = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
                }
            });
            var reader = Reader(BinaryTestData.Snapshot(algorithm, files), "xregistry");
            using var direct = await reader.OpenReadAsync("documents/n1.bin")
                ?? throw new InvalidOperationException("Expected the ordinary Git blob.");
            using var directBytes = new MemoryStream();
            await direct.CopyToAsync(directBytes);
            await Assert.That(directBytes.ToArray().SequenceEqual(expected)).IsTrue();

            await using var mapping = await DirectoryMapping.OpenAsync(reader);
            var result = await mapping.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/item/versions/v1"));
            using var stream = result.Document!.OpenRead();
            using var mappedBytes = new MemoryStream();
            await stream.CopyToAsync(mappedBytes);
            await Assert.That(result.SelectedXid).IsEqualTo("/documents/main/assets/item/versions/v1");
            await Assert.That(result.Document.Length).IsEqualTo(expected.LongLength);
            await Assert.That(mappedBytes.ToArray().SequenceEqual(expected)).IsTrue();
        }
    }

    private static GitDocumentTreeReader Reader(GitSnapshot snapshot, string root) =>
        new(snapshot, new Uri("https://git.example/regression"), root, "refs/heads/regression");

    private static async Task Fails(GitDocumentTreeReader reader, bool mapping, FederationErrorCode expected)
    {
        var exception = await Assert.That(async () =>
        {
            if (mapping)
            {
                await using var opened = await DirectoryMapping.OpenAsync(reader);
            }
            else
            {
                using var stream = await reader.OpenReadAsync("registry.json");
            }
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected a Git binding rejection.");
        await Assert.That(exception.Code).IsEqualTo(expected);
    }

    private static GitSnapshot Snapshot(GitHashAlgorithm algorithm, string path, byte[] content) =>
        BinaryTestData.Snapshot(algorithm, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [path] = content }, "");
}
