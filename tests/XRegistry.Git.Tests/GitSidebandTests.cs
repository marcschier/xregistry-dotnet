using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Protocol;

namespace XRegistry.Git.Tests;

public class GitSidebandTests
{
    [Test]
    public async Task DataAndProgressRemainSeparateAndFatalNeverReturnsSuccess()
    {
        var decoder = new GitSidebandDecoder();
        var data = decoder.Read(Packet("000a\x01PACK!"u8));
        await Assert.That(data.Channel).IsEqualTo(GitSidebandChannel.Data);
        await Assert.That(Convert.ToHexString(data.Payload)).IsEqualTo("5041434B21");
        var progress = decoder.Read(Packet("0008\x02ok\n"u8));
        await Assert.That(progress.Channel).IsEqualTo(GitSidebandChannel.Progress);
        await Assert.That(Convert.ToHexString(progress.Payload)).IsEqualTo("6F6B0A");
        await TestAssert.Fails(() => decoder.Read(Packet("0008\u0003bad"u8)), GitFailure.RemoteFatal);
        await TestAssert.Fails(() => decoder.Read(Packet("0005\x01"u8)), GitFailure.UnexpectedState);
    }

    [Test]
    [Arguments("0004")]
    [Arguments("0000")]
    [Arguments("0001")]
    [Arguments("0002")]
    [Arguments("0005\u0000")]
    [Arguments("0005\u0004")]
    public async Task NonSidebandPacketsAreExplicitFailures(string wire)
    {
        var decoder = new GitSidebandDecoder();
        await TestAssert.Fails(
            () => decoder.Read(Packet(System.Text.Encoding.ASCII.GetBytes(wire))),
            GitFailure.UnexpectedState);
    }

    [Test]
    public async Task EmptyKeepalivesAndCumulativeChannelLimitsAreBounded()
    {
        var decoder = new GitSidebandDecoder(maxDataBytes: 2, maxProgressBytes: 2, maxErrorBytes: 2, maxMessages: 5);
        await Assert.That(decoder.Read(Packet("0005\x02"u8)).Payload.Length).IsEqualTo(0);
        decoder.Read(Packet("0006\x01x"u8));
        decoder.Read(Packet("0006\x01y"u8));
        decoder.Read(Packet("0006\x02x"u8));
        decoder.Read(Packet("0006\x02y"u8));
        await TestAssert.Fails(() => decoder.Read(Packet("0005\x02"u8)), GitFailure.LimitExceeded);
        await TestAssert.Fails(() => new GitSidebandDecoder(maxDataBytes: 0).Read(Packet("0006\x01x"u8)), GitFailure.LimitExceeded);
        await TestAssert.Fails(() => new GitSidebandDecoder(maxProgressBytes: 0).Read(Packet("0006\x02x"u8)), GitFailure.LimitExceeded);
        await TestAssert.Fails(() => new GitSidebandDecoder(maxErrorBytes: 2).Read(Packet("0008\u0003bad"u8)), GitFailure.LimitExceeded);
        await TestAssert.Fails(() => new GitSidebandDecoder(maxErrorBytes: 0).Read(Packet("0005\x03"u8)), GitFailure.RemoteFatal);
        var done = new GitSidebandDecoder(maxMessages: 0);
        done.Complete();
        await TestAssert.Fails(() => done.Read(Packet("0005\x01"u8)), GitFailure.UnexpectedState);
    }

    private static GitPacket Packet(ReadOnlySpan<byte> wire)
    {
        var decoder = new GitPacketDecoder();
        if (!decoder.TryRead(wire, out var count, out var packet) || count != wire.Length)
        {
            throw new InvalidOperationException("The independent test packet is incomplete.");
        }

        return packet;
    }
}
