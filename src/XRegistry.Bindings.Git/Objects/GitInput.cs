// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace XRegistry.Bindings.Git.Objects;

internal sealed class GitInput(Stream source, GitReadBudget budget, IncrementalHash? hash = null)
{
    private readonly byte[] buffer = new byte[8192];
    private IncrementalHash? hash = hash;
    private int offset;
    private int length;
    private int hashStart;
    private long sourceBytes;

    internal long Position { get; private set; }

    internal int ReadByte()
    {
        var value = TryReadByte();
        return value < 0
            ? throw new GitDataException(GitFailure.TruncatedInput, "The Git binary input is truncated.")
            : value;
    }

    internal int TryReadByte()
    {
        budget.CancellationToken.ThrowIfCancellationRequested();
        if (offset == length)
        {
            FlushHash();
            var remaining = budget.Limits.MaxEncodedBytes - sourceBytes;
            var requested = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
            length = source.Read(buffer, 0, requested);
            if (length > remaining)
            {
                throw new GitDataException(GitFailure.LimitExceeded, "The Git encoded-byte budget was exceeded.");
            }

            sourceBytes += length;
            offset = 0;
            hashStart = 0;
            if (length == 0)
            {
                return -1;
            }
        }

        Position++;
        return buffer[offset++];
    }

    internal void ReadExactly(Span<byte> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = (byte)ReadByte();
        }
    }

    internal byte[] FinishHash()
    {
        FlushHash();
        var active = hash ?? throw new InvalidOperationException("The Git input hash is not active.");
        hash = null;
        return active.GetHashAndReset();
    }

    internal void RequireEnd()
    {
        if (TryReadByte() != -1)
        {
            throw new GitDataException(GitFailure.MalformedData, "The Git binary input has trailing bytes.");
        }

        budget.CancellationToken.ThrowIfCancellationRequested();
    }

    private void FlushHash()
    {
        hash?.AppendData(buffer.AsSpan(hashStart, offset - hashStart));
        hashStart = offset;
    }
}
