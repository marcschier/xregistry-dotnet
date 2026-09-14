using System.Buffers;
using System.IO.Compression;

namespace XRegistry.Client;

internal sealed class RegistryBrotliContentReader(RegistryContentReader source, long limit) :
    RegistryBufferedContentReader(source, limit)
{
    private BrotliDecoder _decoder;
    private bool _finished;

    protected override async ValueTask<int> ReadCoreAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (!_finished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = _decoder.Decompress(Input.AsSpan(InputOffset, InputLength - InputOffset),
                buffer.AsSpan(offset, count), out var consumed, out var written);
            InputOffset += consumed;
            switch (status)
            {
                case OperationStatus.Done:
                    await RequireEndAsync(cancellationToken).ConfigureAwait(false);
                    _finished = true;
                    return written;
                case OperationStatus.InvalidData:
                    throw new InvalidDataException("The Brotli response is malformed.");
                case OperationStatus.NeedMoreData:
                    if (written != 0)
                    {
                        return written;
                    }

                    if (!await ReadMoreInputAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidDataException("The Brotli response is truncated.");
                    }

                    break;
                case OperationStatus.DestinationTooSmall:
                    if (written == 0)
                    {
                        throw new InvalidDataException("The Brotli decoder did not make progress.");
                    }

                    return written;
                default:
                    throw new InvalidDataException("The Brotli decoder returned an unexpected status.");
            }
        }

        return 0;
    }

    protected override void DisposeDecoder() => _decoder.Dispose();
}
