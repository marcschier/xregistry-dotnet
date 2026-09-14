using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Git.Tests;

public class GitDeltaTests
{
    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task BothDeltaTypesAndForwardBaseChainsMatchIndependentGitObjects(GitHashAlgorithm algorithm)
    {
        foreach (var name in new[] { "ofs", "refBackward", "refForward", "ofsChain", "refForwardChain" })
        {
            using var source = new ChunkedReadStream(Fixtures.Pack(algorithm, name), 1);
            var objects = GitObjectReader.ReadPack(source, algorithm);
            var chain = name.EndsWith("Chain", StringComparison.Ordinal);
            await Assert.That(objects.Count).IsEqualTo(chain ? 3 : 2);
            var changed = objects.Get(GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "changed")));
            await Assert.That(changed.Type).IsEqualTo(GitObjectType.Blob);
            await Assert.That(Convert.ToHexString(changed.Content)).IsEqualTo("68656C6C6F206D616E6167656420776F726C640A");
            if (chain)
            {
                var final = objects.Get(GitObjectId.Parse(algorithm, Fixtures.Id(algorithm, "final")));
                await Assert.That(Convert.ToHexString(final.Content)).IsEqualTo("68656C6C6F206D616E6167656420776F726C64210A");
            }
        }
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task DeltaLimitsChargeProgramsResultsCopiesInstructionsDepthAndDeferredEntries(GitHashAlgorithm algorithm)
    {
        var exact = new GitReadLimits
        {
            MaxObjectBytes = 20,
            MaxTotalDecompressedBytes = 48,
            MaxDeltaWorkBytes = 20,
            MaxDeltaInstructions = 3,
            MaxDeltaDepth = 1,
            MaxDeltaObjects = 1,
        };
        using var source = new MemoryStream(Fixtures.Pack(algorithm, "refForward"));
        await Assert.That(GitObjectReader.ReadPack(source, algorithm, exact).Count).IsEqualTo(2);
        foreach (var limits in new[]
        {
            exact with { MaxObjectBytes = 19 },
            exact with { MaxTotalDecompressedBytes = 47 },
            exact with { MaxDeltaWorkBytes = 19 },
            exact with { MaxDeltaInstructions = 2 },
            exact with { MaxDeltaDepth = 0 },
            exact with { MaxDeltaObjects = 0 },
        })
        {
            using var limited = new MemoryStream(Fixtures.Pack(algorithm, "refForward"));
            await TestAssert.Fails(() => GitObjectReader.ReadPack(limited, algorithm, limits), GitFailure.LimitExceeded);
        }

        using var chain = new MemoryStream(Fixtures.Pack(algorithm, "ofsChain"));
        await Assert.That(GitObjectReader.ReadPack(chain, algorithm,
            new GitReadLimits { MaxDeltaDepth = 2, MaxDeltaObjects = 2 }).Count).IsEqualTo(3);
        using var tooDeep = new MemoryStream(Fixtures.Pack(algorithm, "refForwardChain"));
        await TestAssert.Fails(() => GitObjectReader.ReadPack(tooDeep, algorithm,
            new GitReadLimits { MaxDeltaDepth = 1 }), GitFailure.LimitExceeded);
    }

    [Test]
    [Arguments("0c0100", GitFailure.MalformedData)]
    [Arguments("0c01910c01", GitFailure.MalformedData)]
    [Arguments("0c01910b02", GitFailure.MalformedData)]
    [Arguments("0c01900d", GitFailure.MalformedData)]
    [Arguments("0c01026162", GitFailure.MalformedData)]
    [Arguments("0c020161", GitFailure.MalformedData)]
    [Arguments("0d010161", GitFailure.MalformedData)]
    [Arguments("0c0102ff", GitFailure.TruncatedInput)]
    [Arguments("0c0181", GitFailure.TruncatedInput)]
    [Arguments("0c80", GitFailure.TruncatedInput)]
    [Arguments("8080808080808080808000", GitFailure.MalformedData)]
    [Arguments("0c8080808080808080808000", GitFailure.MalformedData)]
    [Arguments("0c8080808010", GitFailure.LimitExceeded)]
    public async Task MalformedDeltaInstructionsFailBeforeAnyResult(string program, GitFailure failure)
    {
        var algorithm = GitHashAlgorithm.Sha256;
        var delta = Convert.FromHexString(program);
        var baseEntry = BinaryTestData.Entry(3, "hello world\n"u8);
        var reference = Convert.FromHexString(Fixtures.Id(algorithm, "base"));
        byte[] deltaEntry =
        [
            .. BinaryTestData.ObjectHeader(7, (ulong)delta.Length),
            .. reference,
            .. BinaryTestData.Zlib(delta),
        ];
        using var source = new MemoryStream(BinaryTestData.Pack(algorithm, [.. baseEntry, .. deltaEntry], 2));
        await TestAssert.Fails(() => GitObjectReader.ReadPack(source, algorithm), failure);
    }

    [Test]
    [Arguments(GitHashAlgorithm.Sha1)]
    [Arguments(GitHashAlgorithm.Sha256)]
    public async Task MissingThinBasesAndUnresolvableCyclesAreNotPartialSuccess(GitHashAlgorithm algorithm)
    {
        var delta = Fixtures.Delta(algorithm);
        var reference = Convert.FromHexString(Fixtures.Id(algorithm, "base"));
        byte[] entry =
        [
            .. BinaryTestData.ObjectHeader(7, (ulong)delta.Length),
            .. reference, .. BinaryTestData.Zlib(delta),
        ];
        using var thin = new MemoryStream(BinaryTestData.Pack(algorithm, entry, 1));
        await TestAssert.Fails(() => GitObjectReader.ReadPack(thin, algorithm), GitFailure.UnresolvedDeltaBase);

        // Neither claimed REF base can be established without the other; do not loop or fabricate a base.
        byte[] cyclic =
        [
            .. BinaryTestData.ObjectHeader(7, (ulong)delta.Length),
            .. Convert.FromHexString(Fixtures.Id(algorithm, "final")),
            .. BinaryTestData.Zlib(delta),
            .. BinaryTestData.ObjectHeader(7, (ulong)Fixtures.Delta(algorithm, "delta2").Length),
            .. Convert.FromHexString(Fixtures.Id(algorithm, "changed")),
            .. BinaryTestData.Zlib(Fixtures.Delta(algorithm, "delta2")),
        ];
        using var cycle = new MemoryStream(BinaryTestData.Pack(algorithm, cyclic, 2));
        await TestAssert.Fails(() => GitObjectReader.ReadPack(cycle, algorithm), GitFailure.UnresolvedDeltaBase);
    }

    [Test]
    [Arguments("00")]
    [Arguments("01")]
    [Arguments("0d")]
    [Arguments("ffffffffffffffffffff00")]
    public async Task OfsBasesMustBeExactPriorObjectStartsWithoutOffsetOverflow(string offsetHex)
    {
        var delta = Fixtures.Delta(GitHashAlgorithm.Sha256);
        byte[] entry =
        [
            .. BinaryTestData.ObjectHeader(6, (ulong)delta.Length),
            .. Convert.FromHexString(offsetHex),
            .. BinaryTestData.Zlib(delta),
        ];
        using var source = new MemoryStream(BinaryTestData.Pack(GitHashAlgorithm.Sha256, entry, 1));
        await TestAssert.Fails(() => GitObjectReader.ReadPack(source, GitHashAlgorithm.Sha256), GitFailure.MalformedData);
    }

    [Test]
    public async Task OmittedCopySizeMeans65536AndHighOffsetBytesAreUnsigned()
    {
        var baseContent = new byte[65_536];
        Array.Fill(baseContent, (byte)'x');
        var baseId = BinaryTestData.Id(GitHashAlgorithm.Sha256, "blob", baseContent);
        byte[] program = [0x80, 0x80, 0x04, 0x80, 0x80, 0x04, 0x80];
        byte[] entries =
        [
            .. BinaryTestData.Entry(3, baseContent),
            .. BinaryTestData.ObjectHeader(7, (ulong)program.Length), .. baseId.Bytes,
            .. BinaryTestData.Zlib(program),
        ];
        using var source = new MemoryStream(BinaryTestData.Pack(GitHashAlgorithm.Sha256, entries, 2));
        var objects = GitObjectReader.ReadPack(source, GitHashAlgorithm.Sha256);
        await Assert.That(objects.Count).IsEqualTo(1);
        await Assert.That(objects.Get(baseId).Content.SequenceEqual(baseContent)).IsTrue();

        byte[] unsignedOffset = [12, 1, 0x98, 0x80, 1];
        byte[] invalid =
        [
            .. BinaryTestData.Entry(3, "hello world\n"u8),
            .. BinaryTestData.ObjectHeader(7, (ulong)unsignedOffset.Length),
            .. Convert.FromHexString(Fixtures.Id(GitHashAlgorithm.Sha256, "base")),
            .. BinaryTestData.Zlib(unsignedOffset),
        ];
        using var highOffset = new MemoryStream(BinaryTestData.Pack(GitHashAlgorithm.Sha256, invalid, 2));
        await TestAssert.Fails(() => GitObjectReader.ReadPack(highOffset, GitHashAlgorithm.Sha256), GitFailure.MalformedData);
    }
}
