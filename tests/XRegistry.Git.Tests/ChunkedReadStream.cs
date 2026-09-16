// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Git.Tests;

internal sealed class ChunkedReadStream(byte[] bytes, int chunkSize = 1, Action? afterRead = null) : Stream
{
    private int offset;

    internal int BytesRead => offset;

    internal int LargestRequest { get; private set; }

    internal bool Disposed { get; private set; }

    public override bool CanRead => !Disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        LargestRequest = Math.Max(LargestRequest, buffer.Length);
        var count = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - offset);
        bytes.AsSpan(offset, count).CopyTo(buffer);
        offset += count;
        afterRead?.Invoke();
        return count;
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
