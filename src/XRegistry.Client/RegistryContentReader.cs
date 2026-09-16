// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace XRegistry.Client;

internal abstract class RegistryContentReader(long limit) : IDisposable
{
    private long _bytesRead;

    internal async ValueTask<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = limit - _bytesRead;
        var length = remaining >= count ? count : checked((int)remaining + 1);
        var read = await ReadCoreAsync(buffer, offset, length, cancellationToken).ConfigureAwait(false);
        if (read > remaining)
        {
            throw new InvalidDataException("The response exceeds its body byte limit.");
        }

        _bytesRead += read;
        return read;
    }

    protected abstract ValueTask<int> ReadCoreAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

internal sealed class RegistryRawContentReader(Stream source, long limit) : RegistryContentReader(limit)
{
    protected override ValueTask<int> ReadCoreAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        source.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
}

internal abstract class RegistryBufferedContentReader(RegistryContentReader source, long limit) :
    RegistryContentReader(limit)
{
    private bool _disposed;

    protected byte[] Input { get; } = ArrayPool<byte>.Shared.Rent(16 * 1024);

    protected int InputOffset { get; set; }

    protected int InputLength { get; set; }

    protected async ValueTask<bool> EnsureInputAsync(CancellationToken cancellationToken) =>
        InputOffset < InputLength || await ReadMoreInputAsync(cancellationToken).ConfigureAwait(false);

    protected async ValueTask<bool> ReadMoreInputAsync(CancellationToken cancellationToken)
    {
        var remaining = InputLength - InputOffset;
        if (remaining == Input.Length)
        {
            throw new InvalidDataException("The content decoder did not make progress.");
        }

        Input.AsSpan(InputOffset, remaining).CopyTo(Input);
        InputOffset = 0;
        var read = await source.ReadAsync(Input, remaining, Input.Length - remaining, cancellationToken)
            .ConfigureAwait(false);
        InputLength = remaining + read;
        return read != 0;
    }

    protected async ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await EnsureInputAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The compressed response is truncated.");
        }

        return Input[InputOffset++];
    }

    protected async ValueTask RequireEndAsync(CancellationToken cancellationToken)
    {
        if (await EnsureInputAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Unexpected data follows the completed content coding.");
        }
    }

    protected virtual void DisposeDecoder()
    {
    }

    protected sealed override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                DisposeDecoder();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(Input, clearArray: true);
                source.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
