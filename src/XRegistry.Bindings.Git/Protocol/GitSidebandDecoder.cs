// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Bindings.Git.Protocol;

/// <summary>The nonfatal sideband channels that can produce a message.</summary>
public enum GitSidebandChannel
{
    /// <summary>Exact pack data; no text decoding is applied.</summary>
    Data = 1,
    /// <summary>Untrusted progress bytes, not a trusted log message.</summary>
    Progress = 2,
}

/// <summary>A nonfatal sideband message owning its exact payload.</summary>
public sealed class GitSidebandMessage
{
    private readonly byte[] payload;

    internal GitSidebandMessage(GitSidebandChannel channel, byte[] payload)
    {
        Channel = channel;
        this.payload = payload;
    }

    /// <summary>Gets the data or progress channel.</summary>
    public GitSidebandChannel Channel { get; }

    /// <summary>Gets the bytes after the one-byte channel selector.</summary>
    public ReadOnlySpan<byte> Payload => payload;
}

/// <summary>
/// Decodes an already-selected sideband phase with cumulative budgets. Channel 3 always throws,
/// without inserting remote text in diagnostics. Failure or completion permanently ends this decoder.
/// </summary>
public sealed class GitSidebandDecoder
{
    private readonly long maxDataBytes;
    private readonly long maxProgressBytes;
    private readonly long maxErrorBytes;
    private readonly long maxMessages;
    private long dataBytes;
    private long progressBytes;
    private long messages;
    private bool ended;

    /// <summary>Creates a decoder with finite data, progress, fatal-error, and message budgets.</summary>
    public GitSidebandDecoder(
        long maxDataBytes = 134_217_728,
        long maxProgressBytes = 65_536,
        long maxErrorBytes = 4_096,
        long maxMessages = 1_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDataBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxProgressBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxErrorBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        this.maxDataBytes = maxDataBytes;
        this.maxProgressBytes = maxProgressBytes;
        this.maxErrorBytes = maxErrorBytes;
        this.maxMessages = maxMessages;
    }

    /// <summary>
    /// Reads one data packet. The caller handles flush/delimiter/response-end packets separately.
    /// Empty channel-1/channel-2 keepalives are permitted; unchannelled empty packets are not.
    /// </summary>
    public GitSidebandMessage Read(GitPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        EnsureActive();
        if (packet.Kind != GitPacketKind.Data || packet.Payload.IsEmpty)
        {
            throw Fail(GitFailure.UnexpectedState, "Expected a channelled sideband data packet.");
        }

        if (messages == maxMessages)
        {
            throw Fail(GitFailure.LimitExceeded, "The sideband message budget was exceeded.");
        }

        messages++;
        var payload = packet.Payload[1..];
        switch (packet.Payload[0])
        {
            case 1:
                Charge(ref dataBytes, payload.Length, maxDataBytes);
                return new GitSidebandMessage(GitSidebandChannel.Data, payload.ToArray());
            case 2:
                Charge(ref progressBytes, payload.Length, maxProgressBytes);
                return new GitSidebandMessage(GitSidebandChannel.Progress, payload.ToArray());
            case 3:
                if (payload.Length > maxErrorBytes)
                {
                    throw Fail(GitFailure.LimitExceeded, "The sideband error-byte budget was exceeded.");
                }

                throw Fail(GitFailure.RemoteFatal, "The remote Git sideband reported a fatal error.");
            default:
                throw Fail(GitFailure.UnexpectedState, "The sideband channel is not defined.");
        }
    }

    /// <summary>Ends the sideband phase. This method is not idempotent.</summary>
    public void Complete()
    {
        EnsureActive();
        ended = true;
    }

    private void Charge(ref long used, int count, long limit)
    {
        if (count > limit - used)
        {
            throw Fail(GitFailure.LimitExceeded, "A sideband byte budget was exceeded.");
        }

        used += count;
    }

    private void EnsureActive()
    {
        if (ended)
        {
            throw Fail(GitFailure.UnexpectedState, "The sideband decoder is no longer active.");
        }
    }

    private GitDataException Fail(GitFailure failure, string message)
    {
        ended = true;
        return new GitDataException(failure, message);
    }
}
