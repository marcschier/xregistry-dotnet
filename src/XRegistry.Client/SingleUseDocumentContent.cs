// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;

namespace XRegistry.Client;

internal sealed class SingleUseDocumentContent : HttpContent
{
    private readonly Stream _source;
    private readonly long _limit;
    private readonly long? _length;
    private int _serialized;

    internal SingleUseDocumentContent(Stream source, long limit)
    {
        _source = source;
        _limit = limit;
        if (source.CanSeek)
        {
            _length = source.Length - source.Position;
            if (_length < 0 || _length > limit)
            {
                throw new ArgumentException("The Document stream exceeds its byte budget.", nameof(source));
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length ?? 0;
        return _length.HasValue;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        CopyOnceAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        CopyOnceAsync(stream, cancellationToken);

    private async Task CopyOnceAsync(Stream destination, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _serialized, 1) != 0)
        {
            throw new InvalidOperationException("A Document upload cannot be automatically replayed.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long bytes = 0;
            while (true)
            {
                var count = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) { break; }
                if (count > _limit - bytes || _length.HasValue && count > _length.Value - bytes)
                {
                    throw new InvalidDataException("The Document exceeded its declared length or byte budget during upload.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                bytes += count;
            }
            if (_length.HasValue && bytes != _length.Value)
            {
                throw new InvalidDataException("The Document stream ended before its declared length.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
