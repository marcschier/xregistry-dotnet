// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Git.Tests;

public class GitLooseObjectTests
{
    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task LooseObjectsVerifyExactGitRepresentationAndOwnDecodedBytes(GitHashAlgorithm algorithm)
    {
        foreach (var name in new[] { "empty", "hello", "binary", "root", "commit", "tag" })
        {
            var compressed = Fixtures.Loose(algorithm, name);
            using var stream = new MemoryStream(compressed);
            var expected = GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, name));
            var value = GitObjectReader.ReadLoose(stream, expected);
            await Assert.That(value.Id).IsEqualTo(expected);
            await Assert.That(Convert.ToHexString(value.Content)).IsEqualTo(Convert.ToHexString(Fixtures.Content(algorithm, name)));
            await Assert.That(value.Length).IsEqualTo((long)Fixtures.Content(algorithm, name).Length);
            await Assert.That(stream.CanRead).IsTrue();
            Array.Fill(compressed, (byte)0);
            await Assert.That(Convert.ToHexString(value.Content)).IsEqualTo(Convert.ToHexString(Fixtures.Content(algorithm, name)));
        }
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task EveryTruncationAndAnyTrailingByteAreRejected(GitHashAlgorithm algorithm)
    {
        var compressed = Fixtures.Loose(algorithm, "hello");
        var expected = GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "hello"));
        for (var length = 0; length < compressed.Length; length++)
        {
            using var prefix = new MemoryStream(compressed, 0, length);
            await TestAssert.Fails(() => GitObjectReader.ReadLoose(prefix, expected), GitFailure.TruncatedInput);
        }

        using var trailing = new MemoryStream([.. compressed, 0]);
        await TestAssert.Fails(() => GitObjectReader.ReadLoose(trailing, expected), GitFailure.MalformedData);
        using var concatenated = new MemoryStream([.. compressed, .. compressed]);
        await TestAssert.Fails(() => GitObjectReader.ReadLoose(concatenated, expected), GitFailure.MalformedData);
    }

    [Test]
    [Arguments("blob 06\0hello\n")]
    [Arguments("blob +6\0hello\n")]
    [Arguments("blob -6\0hello\n")]
    [Arguments("blob \0hello\n")]
    [Arguments("blob 18446744073709551616\0hello\n")]
    [Arguments("blob 7\0hello\n")]
    [Arguments("blob 5\0hello\n")]
    [Arguments("blob 6 \0hello\n")]
    [Arguments("Blob 6\0hello\n")]
    [Arguments("unknown 6\0hello\n")]
    [Arguments("blob6\0hello\n")]
    [Arguments("blob 6hello\n")]
    public async Task CanonicalHeadersAreRequiredBeforeObjectPublication(string canonical)
    {
        using var source = new MemoryStream(BinaryTestData.Zlib(Encoding.ASCII.GetBytes(canonical)));
        var expected = GitObjectId.Parse(GitHashAlgorithm.Sha1, Fixtures.Id(GitHashAlgorithm.Sha1, "hello"));
        await TestAssert.Fails(() => GitObjectReader.ReadLoose(source, expected), GitFailure.MalformedData);
    }

    [Test]
    public async Task ObjectIdentityAndZlibChecksumAreSeparateIntegrityChecks()
    {
        var expected = GitObjectId.Parse(GitHashAlgorithm.Sha256, Fixtures.Id(GitHashAlgorithm.Sha256, "hello"));
        using var wrongObject = new MemoryStream(Fixtures.Loose(GitHashAlgorithm.Sha256, "base"));
        await TestAssert.Fails(() => GitObjectReader.ReadLoose(wrongObject, expected), GitFailure.IntegrityMismatch);
        var corrupt = Fixtures.Loose(GitHashAlgorithm.Sha256, "hello");
        corrupt[^1] ^= 1;
        using var wrongAdler = new MemoryStream(corrupt);
        await TestAssert.Fails(() => GitObjectReader.ReadLoose(wrongAdler, expected), GitFailure.IntegrityMismatch);
    }

    [Test]
    [Arguments("771c030000000001", GitFailure.MalformedData)]
    [Arguments("7800030000000001", GitFailure.MalformedData)]
    [Arguments("782000000000030000000001", GitFailure.UnsupportedFormat)]
    [Arguments("78010700000001", GitFailure.MalformedData)]
    [Arguments("780101010000004100420042", GitFailure.MalformedData)]
    public async Task InvalidZlibHeadersDictionaryAndReservedBlocksFail(string hex, GitFailure failure)
    {
        using var source = new MemoryStream(Convert.FromHexString(hex));
        var expected = GitObjectId.Parse(GitHashAlgorithm.Sha1, Fixtures.Id(GitHashAlgorithm.Sha1, "empty"));
        await TestAssert.Fails(() => GitObjectReader.ReadLoose(source, expected), failure);
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(8192)]
    public async Task NonseekableFragmentedSourcesNeedNoLengthOrPosition(int chunkSize)
    {
        using var source = new ChunkedReadStream(Fixtures.Loose(GitHashAlgorithm.Sha1, "binary"), chunkSize);
        var expected = GitObjectId.Parse(GitHashAlgorithm.Sha1, Fixtures.Id(GitHashAlgorithm.Sha1, "binary"));
        var value = GitObjectReader.ReadLoose(source, expected);
        await Assert.That(Convert.ToHexString(value.Content)).IsEqualTo("00FF0D0A62696E6172790A");
        await Assert.That(source.Disposed).IsFalse();
        await Assert.That(source.LargestRequest <= 8192).IsTrue();
    }

    [Test]
    public async Task CompressedObjectAndTotalBudgetsAcceptTheLimitAndRejectLimitPlusOne()
    {
        var bytes = Fixtures.Loose(GitHashAlgorithm.Sha1, "hello");
        var expected = GitObjectId.Parse(GitHashAlgorithm.Sha1, Fixtures.Id(GitHashAlgorithm.Sha1, "hello"));
        var exact = new GitReadLimits
        {
            MaxEncodedBytes = bytes.Length,
            MaxCompressedObjectBytes = bytes.Length,
            MaxObjectBytes = 6,
            MaxTotalDecompressedBytes = 13,
        };
        using var source = new MemoryStream(bytes);
        await Assert.That(GitObjectReader.ReadLoose(source, expected, exact).Length).IsEqualTo(6L);
        foreach (var over in new[]
        {
                exact with { MaxEncodedBytes = bytes.Length - 1 },
                exact with { MaxCompressedObjectBytes = bytes.Length - 1 },
                exact with { MaxObjectBytes = 5 },
                exact with { MaxTotalDecompressedBytes = 12 },
                exact with { MaxEncodedBytes = 0 },
                exact with { MaxCompressedObjectBytes = 0 },
                exact with { MaxTotalDecompressedBytes = 0 },
            })
        {
            using var limited = new ChunkedReadStream(bytes, 8192);
            await TestAssert.Fails(() => GitObjectReader.ReadLoose(limited, expected, over), GitFailure.LimitExceeded);
            await Assert.That((long)limited.BytesRead <= over.MaxEncodedBytes + 1).IsTrue();
        }

        using var empty = new MemoryStream(Fixtures.Loose(GitHashAlgorithm.Sha1, "empty"));
        var emptyId = GitObjectId.Parse(GitHashAlgorithm.Sha1, Fixtures.Id(GitHashAlgorithm.Sha1, "empty"));
        await Assert.That(GitObjectReader.ReadLoose(empty, emptyId, new GitReadLimits { MaxObjectBytes = 0 }).Length).IsEqualTo(0L);
    }

    [Test]
    public async Task StoredAndDynamicHuffmanMembersProduceExactIndependentlyHashedContent()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
            "aaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbcccccddddefghijklmnopqrstuvwxyz\n", 1000)));
        var expected = BinaryTestData.Id(GitHashAlgorithm.Sha256, "blob", content);
        var canonical = BinaryTestData.Canonical("blob", content);
        foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.SmallestSize })
        {
            var bytes = BinaryTestData.Zlib(canonical, level);
            await Assert.That((bytes[2] >> 1) & 3).IsEqualTo(level == CompressionLevel.NoCompression ? 0 : 2);
            using var source = new ChunkedReadStream(bytes, 13);
            var value = GitObjectReader.ReadLoose(source, expected);
            await Assert.That(value.Content.SequenceEqual(content)).IsTrue();
        }
    }

    [Test]
    public async Task DeflateWorkBudgetsAlsoBoundStoredAndEmptyBlocks()
    {
        var canonical = "blob 6\0hello\n"u8.ToArray();
        var bytes = BinaryTestData.Zlib(canonical, CompressionLevel.NoCompression);
        var expected = GitObjectId.Parse(GitHashAlgorithm.Sha1, Fixtures.Id(GitHashAlgorithm.Sha1, "hello"));
        using var exact = new MemoryStream(bytes);
        await Assert.That(GitObjectReader.ReadLoose(exact, expected,
            new GitReadLimits { MaxInflateSymbols = 13, MaxDeflateBlocks = 1 }).Length).IsEqualTo(6L);
        using var symbols = new MemoryStream(bytes);
        await TestAssert.Fails(() => GitObjectReader.ReadLoose(symbols, expected,
            new GitReadLimits { MaxInflateSymbols = 12 }), GitFailure.LimitExceeded);
        using var blocks = new MemoryStream(bytes);
        await TestAssert.Fails(() => GitObjectReader.ReadLoose(blocks, expected,
            new GitReadLimits { MaxDeflateBlocks = 0 }), GitFailure.LimitExceeded);
    }

    [Test]
    public async Task CancellationAndInvalidLimitsDoNotTransferStreamOwnership()
    {
        var bytes = Fixtures.Loose(GitHashAlgorithm.Sha1, "hello");
        var expected = GitObjectId.Parse(GitHashAlgorithm.Sha1, Fixtures.Id(GitHashAlgorithm.Sha1, "hello"));
        using var cancellation = new CancellationTokenSource();
        using var source = new ChunkedReadStream(bytes, afterRead: cancellation.Cancel);
        await Assert.That(() => GitObjectReader.ReadLoose(source, expected, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(source.Disposed).IsFalse();
        await Assert.That(source.BytesRead).IsEqualTo(1);
        using var invalid = new MemoryStream(bytes);
        await Assert.That(() => GitObjectReader.ReadLoose(invalid, expected,
            new GitReadLimits { MaxObjectBytes = int.MaxValue })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(invalid.Position).IsEqualTo(0L);
    }
}
