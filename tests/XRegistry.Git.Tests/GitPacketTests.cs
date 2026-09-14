using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Protocol;

namespace XRegistry.Git.Tests;

public class GitPacketTests
{
    [Test]
    [Arguments("0000", GitPacketKind.Flush)]
    [Arguments("0001", GitPacketKind.Delimiter)]
    [Arguments("0002", GitPacketKind.ResponseEnd)]
    [Arguments("0004", GitPacketKind.Data)]
    public async Task ControlAndEmptyPacketsRemainDistinct(string wire, GitPacketKind kind)
    {
        var decoder = new GitPacketDecoder();
        await Assert.That(decoder.TryRead(System.Text.Encoding.ASCII.GetBytes(wire), out var count, out var packet)).IsTrue();
        await Assert.That(count).IsEqualTo(4);
        await Assert.That(packet!.Kind).IsEqualTo(kind);
        await Assert.That(packet.Payload.Length).IsEqualTo(0);
        await Assert.That(System.Text.Encoding.ASCII.GetString(GitPacket.Encode(kind))).IsEqualTo(wire);
        decoder.Complete();
    }

    [Test]
    public async Task FragmentedPacketOwnsExactPayloadAndLeavesCoalescedPacketUnconsumed()
    {
        var decoder = new GitPacketDecoder();
        await Assert.That(decoder.TryRead("00"u8, out var consumed, out _)).IsFalse();
        await Assert.That(consumed).IsEqualTo(2);
        await Assert.That(decoder.TryRead("08ab"u8, out consumed, out _)).IsFalse();
        await Assert.That(consumed).IsEqualTo(4);
        var tail = "cd0000"u8.ToArray();
        await Assert.That(decoder.TryRead(tail, out consumed, out var packet)).IsTrue();
        await Assert.That(consumed).IsEqualTo(2);
        await Assert.That(packet!.Kind).IsEqualTo(GitPacketKind.Data);
        await Assert.That(Convert.ToHexString(packet.Payload)).IsEqualTo("61626364");
        tail[0] = 0;
        await Assert.That(Convert.ToHexString(packet.Payload)).IsEqualTo("61626364");
        await Assert.That(decoder.TryRead(tail.AsSpan(2), out consumed, out packet)).IsTrue();
        await Assert.That(packet!.Kind).IsEqualTo(GitPacketKind.Flush);
        await Assert.That(consumed).IsEqualTo(4);
        decoder.Complete();
    }

    [Test]
    public async Task EveryFragmentBoundaryAndBytewiseInputPreservesFraming()
    {
        var bytes = "000cabc\0def\n"u8.ToArray();
        for (var split = 0; split < bytes.Length; split++)
        {
            var decoder = new GitPacketDecoder();
            await Assert.That(decoder.TryRead(bytes.AsSpan(0, split), out var count, out _)).IsFalse();
            await Assert.That(count).IsEqualTo(split);
            await Assert.That(decoder.TryRead(bytes.AsSpan(split), out count, out var packet)).IsTrue();
            await Assert.That(count).IsEqualTo(bytes.Length - split);
            await Assert.That(Convert.ToHexString(packet!.Payload)).IsEqualTo("616263006465660A");
            decoder.Complete();
        }

        var bytewise = new GitPacketDecoder();
        for (var index = 0; index < bytes.Length; index++)
        {
            var complete = bytewise.TryRead(bytes.AsSpan(index, 1), out var count, out var packet);
            await Assert.That(count).IsEqualTo(1);
            await Assert.That(complete).IsEqualTo(index == bytes.Length - 1);
            if (complete)
            {
                await Assert.That(Convert.ToHexString(packet!.Payload)).IsEqualTo("616263006465660A");
            }
        }

        bytewise.Complete();
    }

    [Test]
    public async Task MaximumPacketIsExactly65520BytesIncludingHeader()
    {
        var payload = new byte[65_516];
        Array.Fill(payload, (byte)0xA5);
        var bytes = GitPacket.Encode(GitPacketKind.Data, payload);
        await Assert.That(bytes.Length).IsEqualTo(65_520);
        await Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4)).IsEqualTo("fff0");
        var decoder = new GitPacketDecoder(maxBytes: 65_520, maxPackets: 1);
        await Assert.That(decoder.TryRead(bytes, out var count, out var packet)).IsTrue();
        await Assert.That(count).IsEqualTo(65_520);
        await Assert.That(packet!.Payload.SequenceEqual(payload)).IsTrue();
        decoder.Complete();
        await Assert.That(() => GitPacket.Encode(GitPacketKind.Data, new byte[65_517])).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments("0003")]
    [Arguments("fff1")]
    [Arguments("FFFF")]
    [Arguments("00g4")]
    [Arguments(" 004")]
    [Arguments("-004")]
    [Arguments("0\0x4")]
    public async Task InvalidHeaderPoisonsDecoder(string header)
    {
        var decoder = new GitPacketDecoder();
        await TestAssert.Fails(() => decoder.TryRead(System.Text.Encoding.ASCII.GetBytes(header), out _, out _), GitFailure.MalformedData);
        await TestAssert.Fails(() => decoder.TryRead("0004"u8, out _, out _), GitFailure.UnexpectedState);
        await TestAssert.Fails(decoder.Complete, GitFailure.UnexpectedState);
    }

    [Test]
    public async Task EofRejectsEveryPartialHeaderAndPayload()
    {
        var bytes = "0008test"u8.ToArray();
        for (var length = 1; length < bytes.Length; length++)
        {
            var decoder = new GitPacketDecoder();
            await Assert.That(decoder.TryRead(bytes.AsSpan(0, length), out _, out _)).IsFalse();
            await TestAssert.Fails(decoder.Complete, GitFailure.TruncatedInput);
            await TestAssert.Fails(() => decoder.TryRead("0004"u8, out _, out _), GitFailure.UnexpectedState);
        }
    }

    [Test]
    public async Task AllowedKindsResponseEndAndCompletionEnforceState()
    {
        var legacy = new GitPacketDecoder(GitPacketKind.Data | GitPacketKind.Flush);
        await TestAssert.Fails(() => legacy.TryRead("0001"u8, out _, out _), GitFailure.UnexpectedState);
        var ended = new GitPacketDecoder();
        ended.TryRead("00020004"u8, out var consumed, out _);
        await Assert.That(consumed).IsEqualTo(4);
        await TestAssert.Fails(() => ended.TryRead("0004"u8, out _, out _), GitFailure.UnexpectedState);
        var completed = new GitPacketDecoder();
        completed.Complete();
        await TestAssert.Fails(() => completed.TryRead([], out _, out _), GitFailure.UnexpectedState);
        await TestAssert.Fails(completed.Complete, GitFailure.UnexpectedState);
    }

    [Test]
    public async Task CumulativeBudgetsDistinguishZeroLimitAndLimitPlusOne()
    {
        var empty = new GitPacketDecoder(maxPackets: 0, maxBytes: 0);
        empty.Complete();
        var noPackets = new GitPacketDecoder(maxPackets: 0);
        await TestAssert.Fails(() => noPackets.TryRead("0004"u8, out _, out _), GitFailure.LimitExceeded);
        var noBytes = new GitPacketDecoder(maxBytes: 0);
        await TestAssert.Fails(() => noBytes.TryRead("0"u8, out _, out _), GitFailure.LimitExceeded);
        var decoder = new GitPacketDecoder(maxPackets: 2, maxBytes: 8);
        decoder.TryRead("0004"u8, out _, out _);
        decoder.TryRead("0000"u8, out _, out _);
        await TestAssert.Fails(() => decoder.TryRead("0"u8, out _, out _), GitFailure.LimitExceeded);
        var onePacket = new GitPacketDecoder(maxPackets: 1);
        onePacket.TryRead("0004"u8, out _, out _);
        await TestAssert.Fails(() => onePacket.TryRead("0000"u8, out _, out _), GitFailure.LimitExceeded);
    }

    [Test]
    public async Task InvalidConfigurationAndControlPayloadAreArgumentErrors()
    {
        await Assert.That(() => new GitPacketDecoder(GitPacketKind.None)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GitPacketDecoder((GitPacketKind)16)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GitPacketDecoder(maxPackets: -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new GitPacketDecoder(maxBytes: -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => GitPacket.Encode(GitPacketKind.Flush, "x"u8)).Throws<ArgumentException>();
        await Assert.That(() => GitPacket.Encode(GitPacketKind.All)).Throws<ArgumentOutOfRangeException>();
    }
}
