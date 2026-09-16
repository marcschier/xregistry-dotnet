// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace XRegistry.Bindings.Git.Protocol;

/// <summary>
/// Incrementally decodes one pkt-line at a time. Not thread-safe. Complete the decoder at EOF.
/// A failed or completed decoder cannot be reused.
/// </summary>
public sealed class GitPacketDecoder
{
    /// <summary>The Git protocol's maximum packet size, including its four-byte header.</summary>
    public const int MaximumPacketLength = 65_520;

    private readonly GitPacketKind allowedPackets;
    private readonly long maxPackets;
    private readonly long maxBytes;
    private readonly byte[] header = new byte[4];
    private byte[] payload = [];
    private GitPacketKind kind;
    private int headerLength;
    private int payloadLength;
    private long packets;
    private long bytes;
    private bool ended;
    private bool completed;
    private bool faulted;

    /// <summary>
    /// Creates a decoder with a fixed set of permitted packet kinds and cumulative wire budgets.
    /// Transport negotiation states remain the caller's responsibility.
    /// </summary>
    public GitPacketDecoder(
        GitPacketKind allowedPackets = GitPacketKind.All,
        long maxPackets = 1_000_000,
        long maxBytes = 268_435_456)
    {
        if (allowedPackets == GitPacketKind.None || (allowedPackets & ~GitPacketKind.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allowedPackets));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxPackets);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        this.allowedPackets = allowedPackets;
        this.maxPackets = maxPackets;
        this.maxBytes = maxBytes;
    }

    /// <summary>
    /// Consumes at most one packet. False means more input is required, not EOF or success with
    /// empty data. The caller retains unconsumed coalesced bytes and must call Complete at EOF.
    /// </summary>
    public bool TryRead(
        ReadOnlySpan<byte> input,
        out int bytesConsumed,
        [NotNullWhen(true)] out GitPacket? packet)
    {
        EnsureActive();
        bytesConsumed = 0;
        packet = null;
        if (headerLength < 4)
        {
            var count = Math.Min(4 - headerLength, input.Length);
            ChargeBytes(count);
            input[..count].CopyTo(header.AsSpan(headerLength));
            headerLength += count;
            bytesConsumed += count;
            input = input[count..];
            if (headerLength < 4)
            {
                return false;
            }

            ParseHeader();
        }

        var payloadCount = Math.Min(payload.Length - payloadLength, input.Length);
        ChargeBytes(payloadCount);
        input[..payloadCount].CopyTo(payload.AsSpan(payloadLength));
        payloadLength += payloadCount;
        bytesConsumed += payloadCount;
        if (payloadLength != payload.Length)
        {
            return false;
        }

        packet = new GitPacket(kind, payload);
        ended = kind == GitPacketKind.ResponseEnd;
        headerLength = 0;
        payloadLength = 0;
        payload = [];
        return true;
    }

    /// <summary>Marks EOF and rejects any partial header or payload. This method is not idempotent.</summary>
    public void Complete()
    {
        if (faulted || completed)
        {
            throw Fail(GitFailure.UnexpectedState, "The pkt-line decoder is no longer active.");
        }

        if (headerLength != 0)
        {
            throw Fail(GitFailure.TruncatedInput, "The pkt-line input is truncated.");
        }

        completed = true;
    }

    private void ParseHeader()
    {
        var length = 0;
        foreach (var value in header)
        {
            var digit = value switch
            {
                >= (byte)'0' and <= (byte)'9' => value - '0',
                >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                throw Fail(GitFailure.MalformedData, "A pkt-line header is not hexadecimal.");
            }

            length = (length << 4) | digit;
        }

        if (length == 3 || length > MaximumPacketLength)
        {
            throw Fail(GitFailure.MalformedData, "A pkt-line length is outside the protocol range.");
        }

        kind = length switch
        {
            0 => GitPacketKind.Flush,
            1 => GitPacketKind.Delimiter,
            2 => GitPacketKind.ResponseEnd,
            _ => GitPacketKind.Data,
        };
        if ((allowedPackets & kind) == 0)
        {
            throw Fail(GitFailure.UnexpectedState, "This packet kind is not permitted in the current phase.");
        }

        if (packets == maxPackets)
        {
            throw Fail(GitFailure.LimitExceeded, "The pkt-line packet budget was exceeded.");
        }

        packets++;
        payload = length > 4 ? new byte[length - 4] : [];
    }

    private void ChargeBytes(int count)
    {
        if (count > maxBytes - bytes)
        {
            throw Fail(GitFailure.LimitExceeded, "The pkt-line wire-byte budget was exceeded.");
        }

        bytes += count;
    }

    private void EnsureActive()
    {
        if (faulted || completed || ended)
        {
            throw Fail(GitFailure.UnexpectedState, "The pkt-line decoder is no longer active.");
        }
    }

    private GitDataException Fail(GitFailure failure, string message)
    {
        faulted = true;
        return new GitDataException(failure, message);
    }
}
