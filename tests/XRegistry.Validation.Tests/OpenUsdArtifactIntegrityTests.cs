// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class OpenUsdArtifactIntegrityTests
{
    private const string Sha256Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Sha384Abc = "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7";
    private const string Sha512Abc = "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f";
    private const string Sha256Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Sha384Empty = "38b060a751ac96384cd9327eb1b1e36a21fdb71114be07434c0cc7bf63f6e1da274edebfe76f65fbd51ad2f14898b95b";
    private const string Sha512Empty = "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e";

    [Test]
    public async Task ValidateMetadataRejectsProducerDigestWithoutAlgorithm()
    {
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(Sha256Abc, null, OpenUsdDigestRole.Producer))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("Sha256", Sha256Abc, OpenUsdDigestRole.Producer)]
    [Arguments("Sha256", Sha256Abc, OpenUsdDigestRole.Consumer)]
    [Arguments("Sha384", Sha384Abc, OpenUsdDigestRole.Producer)]
    [Arguments("Sha384", Sha384Abc, OpenUsdDigestRole.Consumer)]
    [Arguments("Sha512", Sha512Abc, OpenUsdDigestRole.Producer)]
    [Arguments("Sha512", Sha512Abc, OpenUsdDigestRole.Consumer)]
    public async Task ValidateMetadataAcceptsAllThreeExactAlgorithms(string algorithm, string digest, OpenUsdDigestRole role)
    {
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(digest, algorithm, role)).ThrowsNothing();
    }

    [Test]
    public async Task ValidateMetadataUsesSha256ForConsumerFallbackWithoutLengthInference()
    {
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(Sha256Abc, null, OpenUsdDigestRole.Consumer))
            .ThrowsNothing();
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(Sha384Abc, null, OpenUsdDigestRole.Consumer))
            .Throws<ArgumentException>();
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(Sha512Abc, null, OpenUsdDigestRole.Consumer))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("Sha256", 64)]
    [Arguments("Sha384", 96)]
    [Arguments("Sha512", 128)]
    public async Task ValidateMetadataEnforcesExactLengthAndLowercaseHex(string algorithm, int length)
    {
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(new string('0', length), algorithm, OpenUsdDigestRole.Producer))
            .ThrowsNothing();
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(new string('0', length - 1), algorithm, OpenUsdDigestRole.Producer))
            .Throws<ArgumentException>();
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(new string('0', length + 1), algorithm, OpenUsdDigestRole.Producer))
            .Throws<ArgumentException>();
        foreach (var character in new[] { 'A', 'g', ' ', '\u0660' })
        {
            var digest = new string('0', length - 1) + character;
            await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(digest, algorithm, OpenUsdDigestRole.Consumer))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    [Arguments("sha256")]
    [Arguments("SHA256")]
    [Arguments("Sha256 ")]
    [Arguments("")]
    [Arguments("Sha1")]
    public async Task ValidateMetadataRejectsUnknownAlgorithmsEvenWithoutADigest(string algorithm)
    {
        foreach (var role in new[] { OpenUsdDigestRole.Producer, OpenUsdDigestRole.Consumer })
        {
            await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(Sha256Abc, algorithm, role))
                .Throws<ArgumentException>();
            await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(null, algorithm, role))
                .Throws<ArgumentException>();
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("Sha256")]
    [Arguments("Sha384")]
    [Arguments("Sha512")]
    public async Task ValidateMetadataAllowsAbsentDigestWithoutInventingAnAlgorithmRequirement(string? algorithm)
    {
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(null, algorithm, OpenUsdDigestRole.Producer))
            .ThrowsNothing();
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(null, algorithm, OpenUsdDigestRole.Consumer))
            .ThrowsNothing();
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\t")]
    public async Task ValidateMetadataDoesNotConfuseEmptyDigestWithAbsence(string digest)
    {
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(digest, "Sha256", OpenUsdDigestRole.Producer))
            .Throws<ArgumentException>();
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(digest, null, OpenUsdDigestRole.Consumer))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(2)]
    [Arguments(int.MaxValue)]
    public async Task ValidateMetadataRejectsUndefinedRolesBeforeTreatingMetadataAsOptional(int value)
    {
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(null, null, (OpenUsdDigestRole)value))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => OpenUsdArtifactIntegrity.ValidateMetadata(Sha256Abc, "Sha256", (OpenUsdDigestRole)value))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(-1, 1)]
    [Arguments(0, 0)]
    [Arguments(0, -1)]
    public async Task ReadLimitsRejectInvalidBudgets(int bytes, int operations)
    {
        await Assert.That(() => new OpenUsdArtifactReadLimits(bytes, operations))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments("Sha256", Sha256Abc, OpenUsdDigestRole.Producer)]
    [Arguments("Sha256", Sha256Abc, OpenUsdDigestRole.Consumer)]
    [Arguments("Sha384", Sha384Abc, OpenUsdDigestRole.Producer)]
    [Arguments("Sha384", Sha384Abc, OpenUsdDigestRole.Consumer)]
    [Arguments("Sha512", Sha512Abc, OpenUsdDigestRole.Producer)]
    [Arguments("Sha512", Sha512Abc, OpenUsdDigestRole.Consumer)]
    public async Task ReadAsyncVerifiesExactBytesBeforeReturningContent(string algorithm, string digest, OpenUsdDigestRole role)
    {
        using var source = Chunks("abc"u8.ToArray(), 1);
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(source, digest, algorithm, role, new(3, 4));

        await Assert.That(artifact.Length).IsEqualTo(3L);
        await Assert.That(artifact.IsDigestVerified).IsTrue();
        await Assert.That(await HexAsync(artifact)).IsEqualTo("616263");
        await Assert.That(source.ReadCalls).IsEqualTo(4);
        await Assert.That(source.CanRead).IsTrue();
    }

    [Test]
    [Arguments("Sha256", Sha256Abc)]
    [Arguments("Sha384", Sha384Abc)]
    [Arguments("Sha512", Sha512Abc)]
    public async Task ReadAsyncRejectsMismatchRatherThanHandingOutAnArtifact(string algorithm, string digest)
    {
        using var source = Chunks("abd"u8.ToArray());
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, digest, algorithm,
            OpenUsdDigestRole.Consumer, new(3, 2))).Throws<CryptographicException>();
        await Assert.That(source.ReadCalls).IsEqualTo(2);
        await Assert.That(source.BytesRead).IsEqualTo(3L);
        await Assert.That(source.CanRead).IsTrue();
    }

    [Test]
    public async Task ReadAsyncEnforcesTheByteLimitInsteadOfReturningATruncatedArtifact()
    {
        using var source = new MemoryStream("abcd"u8.ToArray());
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, null, null,
            OpenUsdDigestRole.Consumer, new(3, 3))).Throws<InvalidDataException>();
        await Assert.That(source.CanRead).IsTrue();
    }

    [Test]
    public async Task ReadAsyncCountsTheEofProbeAgainstTheOperationLimit()
    {
        using var source = new MemoryStream("abc"u8.ToArray());
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 1))).Throws<InvalidDataException>();
        await Assert.That(source.CanRead).IsTrue();
    }

    [Test]
    public async Task ReadAsyncVerifiesTheConsumerSha256Fallback()
    {
        using var source = Chunks("abc"u8.ToArray());
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 2));
        await Assert.That(artifact.IsDigestVerified).IsTrue();
        await Assert.That(artifact.Length).IsEqualTo(3L);
        await Assert.That(await HexAsync(artifact)).IsEqualTo("616263");
    }

    [Test]
    [Arguments(null)]
    [Arguments("Sha256")]
    [Arguments("Sha384")]
    [Arguments("Sha512")]
    public async Task ReadAsyncDoesNotCallMissingDigestVerified(string? algorithm)
    {
        foreach (var role in new[] { OpenUsdDigestRole.Producer, OpenUsdDigestRole.Consumer })
        {
            using var source = Chunks("abc"u8.ToArray());
            var artifact = await OpenUsdArtifactIntegrity.ReadAsync(source, null, algorithm, role, new(3, 2));
            await Assert.That(artifact.IsDigestVerified).IsFalse();
            await Assert.That(artifact.Length).IsEqualTo(3L);
            await Assert.That(await HexAsync(artifact)).IsEqualTo("616263");
            await Assert.That(source.WasDisposed).IsFalse();
        }
    }

    [Test]
    [Arguments(Sha256Abc, null, OpenUsdDigestRole.Producer)]
    [Arguments(Sha384Abc, null, OpenUsdDigestRole.Consumer)]
    [Arguments("", "Sha256", OpenUsdDigestRole.Consumer)]
    [Arguments(null, "Sha1", OpenUsdDigestRole.Consumer)]
    [Arguments("not-hex", "Sha256", OpenUsdDigestRole.Producer)]
    public async Task ReadAsyncRejectsInvalidMetadataBeforeConsumingItsSource(string? digest, string? algorithm,
        OpenUsdDigestRole role)
    {
        using var source = Chunks("abc"u8.ToArray());
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, digest, algorithm, role, new(3, 2)))
            .Throws<ArgumentException>();
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(source.BytesRead).IsEqualTo(0L);
        await Assert.That(source.WasDisposed).IsFalse();
    }

    [Test]
    public async Task ReadAsyncRejectsInvalidRoleSourceAndLimitsBeforeConsumption()
    {
        using var source = Chunks("abc"u8.ToArray());
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, null, null,
            (OpenUsdDigestRole)42, new(3, 2))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(null!, null, null,
            OpenUsdDigestRole.Consumer, new(3, 2))).Throws<ArgumentNullException>();
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, null, null,
            OpenUsdDigestRole.Consumer, null!)).Throws<ArgumentNullException>();
        using var unreadable = new ControlledStream(static (_, _) => ValueTask.FromResult(0)) { Readable = false };
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(unreadable, null, null,
            OpenUsdDigestRole.Consumer, new(0, 1))).Throws<ArgumentException>();
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(unreadable.ReadCalls).IsEqualTo(0);
        await Assert.That(source.WasDisposed).IsFalse();
        await Assert.That(unreadable.WasDisposed).IsFalse();
    }

    [Test]
    public async Task ReadAsyncPreservesBinaryBytesWithoutTextCanonicalization()
    {
        using var source = Chunks([0, 255, 128, 10, 13], 2);
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(source,
            "037e68ac6512d2c57a0d7247a76b3ab486a7f7e9c05ea50cae197782c80b63d4", "Sha256",
            OpenUsdDigestRole.Consumer, new(5, 4));
        await Assert.That(artifact.Length).IsEqualTo(5L);
        await Assert.That(artifact.IsDigestVerified).IsTrue();
        await Assert.That(await HexAsync(artifact)).IsEqualTo("00FF800A0D");
        await Assert.That(source.ReadCalls).IsEqualTo(4);
    }

    [Test]
    [Arguments("abc\n")]
    [Arguments("abc\r\n")]
    [Arguments("abc ")]
    [Arguments("package:abc")]
    public async Task ReadAsyncDoesNotVerifyDifferentTextOrContainerBytesAsTheDeclaredMember(string bytes)
    {
        using var source = Chunks(System.Text.Encoding.UTF8.GetBytes(bytes));
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(32, 3))).Throws<CryptographicException>();
        await Assert.That(source.WasDisposed).IsFalse();
    }

    [Test]
    public async Task ReadAsyncRetainsExactBytesAcrossBufferGrowth()
    {
        var bytes = Enumerable.Range(0, 131073).Select(static index => (byte)(index % 251)).ToArray();
        using var source = Chunks(bytes);
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(source,
            "dd84db969f4ff2abb79c8c2fbc06e8d8e02c46d6481c958e057f7ad7a24c58a7", "Sha256",
            OpenUsdDigestRole.Consumer, new(131073, 4));
        await Assert.That(artifact.Length).IsEqualTo(131073L);
        await Assert.That(artifact.IsDigestVerified).IsTrue();
        await Assert.That(await HexAsync(artifact)).IsEqualTo(Convert.ToHexString(bytes));
        await Assert.That(source.ReadCalls).IsEqualTo(4);
        await Assert.That(source.MaximumRequestedBytes).IsEqualTo(65536);
    }

    [Test]
    [Arguments(0L)]
    [Arguments(long.MaxValue)]
    public async Task ReadAsyncUsesActualBytesRatherThanReportedLength(long reportedLength)
    {
        using var valid = Chunks("abc"u8.ToArray(), reportedLength: reportedLength);
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(valid, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 2));
        await Assert.That(await HexAsync(artifact)).IsEqualTo("616263");
        using var over = Chunks("abcd"u8.ToArray(), reportedLength: reportedLength);
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(over, null, null,
            OpenUsdDigestRole.Consumer, new(3, 2))).Throws<InvalidDataException>();
        await Assert.That(valid.LengthReads).IsEqualTo(0);
        await Assert.That(over.LengthReads).IsEqualTo(0);
        await Assert.That(valid.SeekCalls).IsEqualTo(0);
        await Assert.That(over.SeekCalls).IsEqualTo(0);
        await Assert.That(over.BytesRead).IsEqualTo(4L);
        await Assert.That(over.WasDisposed).IsFalse();
    }

    [Test]
    public async Task ReadAsyncRejectsOversizeAfterOnlyLimitPlusOneBytes()
    {
        using var source = Chunks(new byte[1000]);
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, null, null,
            OpenUsdDigestRole.Consumer, new(3, 10))).Throws<InvalidDataException>();
        await Assert.That(source.BytesRead).IsEqualTo(4L);
        await Assert.That(source.ReadCalls).IsEqualTo(1);
        await Assert.That(source.MaximumRequestedBytes).IsEqualTo(4);
        await Assert.That(source.WasDisposed).IsFalse();
    }

    [Test]
    public async Task ReadAsyncEnforcesTheReadLimitBeforeOneMoreCall()
    {
        using var exhausted = Chunks("abc"u8.ToArray(), 1);
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(exhausted, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 3))).Throws<InvalidDataException>();
        await Assert.That(exhausted.ReadCalls).IsEqualTo(3);
        await Assert.That(exhausted.BytesRead).IsEqualTo(3L);
        await Assert.That(exhausted.WasDisposed).IsFalse();

        using var enough = Chunks("abc"u8.ToArray(), 1);
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(enough, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 4));
        await Assert.That(enough.ReadCalls).IsEqualTo(4);
        await Assert.That(artifact.IsDigestVerified).IsTrue();
        await Assert.That(await HexAsync(artifact)).IsEqualTo("616263");
    }

    [Test]
    [Arguments(null, null)]
    [Arguments("Sha256", Sha256Empty)]
    [Arguments("Sha384", Sha384Empty)]
    [Arguments("Sha512", Sha512Empty)]
    public async Task ReadAsyncChecksEmptyArtifactsAtZeroByteBudget(string? algorithm, string? digest)
    {
        var limits = new OpenUsdArtifactReadLimits(0, 1);
        await Assert.That(limits.MaxArtifactBytes).IsEqualTo(0);
        await Assert.That(limits.MaxReadOperations).IsEqualTo(1);
        using var source = Chunks([]);
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(source, digest, algorithm, OpenUsdDigestRole.Consumer, limits);
        await Assert.That(artifact.Length).IsEqualTo(0L);
        await Assert.That(artifact.IsDigestVerified).IsEqualTo(digest is not null);
        await Assert.That(await HexAsync(artifact)).IsEqualTo("");
        await Assert.That(source.ReadCalls).IsEqualTo(1);
        await Assert.That(source.MaximumRequestedBytes).IsEqualTo(1);
    }

    [Test]
    public async Task ReadAsyncRejectsTheFirstByteAtZeroBudget()
    {
        using var source = Chunks([42]);
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, null, null,
            OpenUsdDigestRole.Consumer, new(0, 1))).Throws<InvalidDataException>();
        await Assert.That(source.ReadCalls).IsEqualTo(1);
        await Assert.That(source.BytesRead).IsEqualTo(1L);
        await Assert.That(source.WasDisposed).IsFalse();
    }

    [Test]
    public async Task ReadAsyncPreservesCurrentSourcePositionAndOwnsTheReturnedBytes()
    {
        var bytes = "prefixabc"u8.ToArray();
        using var source = new MemoryStream(bytes) { Position = 6 };
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 2));
        await Assert.That(source.Position).IsEqualTo(9L);
        await Assert.That(source.CanRead).IsTrue();
        bytes[6] = (byte)'x';
        source.Dispose();
        await Assert.That(await HexAsync(artifact)).IsEqualTo("616263");
        await Assert.That(artifact.IsDigestVerified).IsTrue();
        await Assert.That(artifact.Length).IsEqualTo(3L);
    }

    [Test]
    public async Task ArtifactReadersAreIndependentNonwritableAndIndependentlyOwned()
    {
        using var source = Chunks("abc"u8.ToArray());
        var artifact = await OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 2));
        using var first = artifact.OpenRead();
        using var second = artifact.OpenRead();
        await Assert.That(first.ReadByte()).IsEqualTo(97);
        await Assert.That(second.ReadByte()).IsEqualTo(97);
        await Assert.That(first.ReadByte()).IsEqualTo(98);
        await Assert.That(first.CanWrite).IsFalse();
        await Assert.That(() => first.WriteByte(0)).Throws<NotSupportedException>();
        if (first is MemoryStream memory)
        {
            await Assert.That(memory.TryGetBuffer(out _)).IsFalse();
            await Assert.That(() => memory.GetBuffer()).Throws<UnauthorizedAccessException>();
        }
        first.Dispose();
        await Assert.That(second.ReadByte()).IsEqualTo(98);
        await Assert.That(await HexAsync(artifact)).IsEqualTo("616263");
        await Assert.That(source.ReadCalls).IsEqualTo(2);
    }

    [Test]
    public async Task ReadAsyncHonorsPreCancellationWithoutReading()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var source = Chunks("abc"u8.ToArray());
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 2), cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(source.WasDisposed).IsFalse();
    }

    [Test]
    public async Task ReadAsyncObservesCancellationAfterAReadReturns()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new ControlledStream((buffer, _) =>
        {
            "abc"u8.CopyTo(buffer.Span);
            cancellation.Cancel();
            return ValueTask.FromResult(3);
        });
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 2), cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(source.ReadCalls).IsEqualTo(1);
        await Assert.That(source.WasDisposed).IsFalse();
    }

    [Test]
    public async Task ReadAsyncCooperatesWithCancellationDuringAnActiveRead()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new ControlledStream(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return 0;
        });
        var pending = OpenUsdArtifactIntegrity.ReadAsync(source, null, null,
            OpenUsdDigestRole.Consumer, new(3, 2), cancellation.Token).AsTask();
        try
        {
            await Task.WhenAny(entered.Task, pending);
            await Assert.That(entered.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
            await cancellation.CancelAsync();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await Assert.That(source.ReadCalls).IsEqualTo(1);
            await Assert.That(source.WasDisposed).IsFalse();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task ReadAsyncDoesNotHandOutAValidPrefixBeforeEof()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var source = new ControlledStream(async (buffer, _) =>
        {
            if (reads++ == 0)
            {
                "abc"u8.CopyTo(buffer.Span);
                return 3;
            }
            entered.TrySetResult();
            await release.Task;
            return 0;
        });
        var pending = OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 2)).AsTask();
        try
        {
            await Task.WhenAny(entered.Task, pending);
            await Assert.That(entered.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
            release.TrySetResult();
            var artifact = await pending;
            await Assert.That(artifact.IsDigestVerified).IsTrue();
            await Assert.That(await HexAsync(artifact)).IsEqualTo("616263");
            await Assert.That(source.WasDisposed).IsFalse();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task ReadAsyncDoesNotAbandonAnUncooperativeSourceRead()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new ControlledStream(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return 0;
        });
        var pending = OpenUsdArtifactIntegrity.ReadAsync(source, null, null,
            OpenUsdDigestRole.Consumer, new(0, 1), cancellation.Token).AsTask();
        try
        {
            await Task.WhenAny(entered.Task, pending);
            await Assert.That(entered.Task.IsCompletedSuccessfully).IsTrue();
            await cancellation.CancelAsync();
            await Assert.That(pending.IsCompleted).IsFalse();
            release.TrySetResult();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await Assert.That(source.ReadCalls).IsEqualTo(1);
            await Assert.That(source.WasDisposed).IsFalse();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task ReadAsyncPropagatesTheOriginalIoFailureWithoutReturningAPrefix()
    {
        var failure = new IOException("source failed");
        var reads = 0;
        using var source = new ControlledStream((buffer, _) =>
        {
            if (reads++ != 0)
            {
                return ValueTask.FromException<int>(failure);
            }
            "ab"u8.CopyTo(buffer.Span);
            return ValueTask.FromResult(2);
        });
        var actual = await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, Sha256Abc, null,
            OpenUsdDigestRole.Consumer, new(3, 3))).Throws<IOException>();
        await Assert.That(ReferenceEquals(failure, actual)).IsTrue();
        await Assert.That(source.BytesRead).IsEqualTo(2L);
        await Assert.That(source.ReadCalls).IsEqualTo(2);
        await Assert.That(source.WasDisposed).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadAsyncRejectsInvalidStreamReadCounts(bool oversized)
    {
        using var source = new ControlledStream((buffer, _) => ValueTask.FromResult(oversized ? buffer.Length + 1 : -1));
        await Assert.That(async () => await OpenUsdArtifactIntegrity.ReadAsync(source, null, null,
            OpenUsdDigestRole.Consumer, new(3, 2))).Throws<InvalidDataException>();
        await Assert.That(source.ReadCalls).IsEqualTo(1);
        await Assert.That(source.WasDisposed).IsFalse();
    }

    private static ControlledStream Chunks(byte[] bytes, int chunkSize = int.MaxValue, long? reportedLength = null)
    {
        var position = 0;
        return new((buffer, token) =>
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, Math.Min(chunkSize, bytes.Length - position));
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return ValueTask.FromResult(count);
        }, reportedLength);
    }

    private static async Task<string> HexAsync(OpenUsdArtifact artifact)
    {
        using var input = artifact.OpenRead();
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return Convert.ToHexString(output.ToArray());
    }

    private sealed class ControlledStream(Func<Memory<byte>, CancellationToken, ValueTask<int>> read, long? reportedLength = null) : Stream
    {
        internal int ReadCalls { get; private set; }
        internal long BytesRead { get; private set; }
        internal int MaximumRequestedBytes { get; private set; }
        internal int LengthReads { get; private set; }
        internal int SeekCalls { get; private set; }
        internal bool WasDisposed { get; private set; }
        internal bool Readable { get; init; } = true;

        public override bool CanRead => Readable && !WasDisposed;
        public override bool CanSeek => reportedLength is not null;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                LengthReads++;
                return reportedLength ?? throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get => BytesRead;
            set
            {
                SeekCalls++;
                throw new NotSupportedException();
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            MaximumRequestedBytes = Math.Max(MaximumRequestedBytes, buffer.Length);
            var count = await read(buffer, cancellationToken);
            if (count > 0)
            {
                BytesRead += count;
            }
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            SeekCalls++;
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
