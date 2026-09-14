using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace XRegistry.Client;

internal sealed class RegistryDeflateContentReader(
    RegistryContentReader source, long limit, bool gzip, RegistryGzipWorkBudget work) :
    RegistryBufferedContentReader(source, limit)
{
    private readonly IChecksum _checksum = gzip ? new Crc32() : new Adler32();
    private readonly Crc32 _headerChecksum = new();
    private Inflater _inflater = new(noHeader: true);
    private bool _started;
    private bool _finished;
    private uint _memberSize;

    protected override async ValueTask<int> ReadCoreAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (!_finished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_started)
            {
                if (gzip)
                {
                    await ReadGzipHeaderAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReadZlibHeaderAsync(cancellationToken).ConfigureAwait(false);
                }

                _started = true;
            }

            var read = Inflate(buffer, offset, count);
            if (read != 0)
            {
                _checksum.Update(new ArraySegment<byte>(buffer, offset, read));
                _memberSize = unchecked(_memberSize + (uint)read);
                return read;
            }

            if (_inflater.IsFinished)
            {
                // The inflater may read ahead into the footer or the next member.
                InputOffset = InputLength - _inflater.RemainingInput;
                await ReadTrailerAsync(cancellationToken).ConfigureAwait(false);
                if (!gzip)
                {
                    await RequireEndAsync(cancellationToken).ConfigureAwait(false);
                    _finished = true;
                }
                else if (!await EnsureInputAsync(cancellationToken).ConfigureAwait(false))
                {
                    _finished = true;
                }
                else
                {
                    _inflater = new Inflater(noHeader: true);
                    _checksum.Reset();
                    _memberSize = 0;
                    _started = false;
                }

                continue;
            }

            if (!_inflater.IsNeedingInput)
            {
                throw new InvalidDataException("The deflate decoder did not make progress.");
            }

            if (!await EnsureInputAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The deflate response is truncated.");
            }

            _inflater.SetInput(Input, InputOffset, InputLength - InputOffset);
            InputOffset = InputLength;
        }

        return 0;
    }

    private int Inflate(byte[] buffer, int offset, int count)
    {
        try
        {
            return _inflater.Inflate(buffer, offset, count);
        }
        catch (SharpZipBaseException exception)
        {
            throw new InvalidDataException("The deflate response is malformed.", exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The deflate response is malformed.", exception);
        }
    }

    private async ValueTask ReadGzipHeaderAsync(CancellationToken cancellationToken)
    {
        work.BeginMember();
        _headerChecksum.Reset();
        var first = await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false);
        var second = await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false);
        var method = await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false);
        var flags = await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false);
        if (first != 0x1f || second != 0x8b || method != 8 || (flags & 0xe0) != 0)
        {
            throw new InvalidDataException("The gzip member header is malformed.");
        }

        for (var index = 0; index < 6; index++)
        {
            _ = await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((flags & 4) != 0)
        {
            var low = await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false);
            var high = await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false);
            var length = low | (high << 8);
            for (var index = 0; index < length; index++)
            {
                _ = await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if ((flags & 8) != 0)
        {
            await ReadGzipStringAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((flags & 16) != 0)
        {
            await ReadGzipStringAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((flags & 2) != 0)
        {
            var expected = _headerChecksum.Value & 0xffff;
            work.ReadHeaderByte();
            var low = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            work.ReadHeaderByte();
            var high = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if ((low | (high << 8)) != expected)
            {
                throw new InvalidDataException("The gzip header CRC16 does not match.");
            }
        }
    }

    private async ValueTask<byte> ReadGzipHeaderByteAsync(CancellationToken cancellationToken)
    {
        work.ReadHeaderByte();
        var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        _headerChecksum.Update(value);
        return value;
    }

    private async ValueTask ReadGzipStringAsync(CancellationToken cancellationToken)
    {
        while (await ReadGzipHeaderByteAsync(cancellationToken).ConfigureAwait(false) != 0)
        {
        }
    }

    private async ValueTask ReadZlibHeaderAsync(CancellationToken cancellationToken)
    {
        var method = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        var flags = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if ((method & 15) != 8 || (method >> 4) > 7 || ((method << 8) | flags) % 31 != 0)
        {
            throw new InvalidDataException("The zlib header is malformed.");
        }

        if ((flags & 32) != 0)
        {
            throw new NotSupportedException("Preset zlib dictionaries are not supported for HTTP content decoding.");
        }
    }

    private async ValueTask ReadTrailerAsync(CancellationToken cancellationToken)
    {
        var checksum = await ReadUInt32Async(littleEndian: gzip, cancellationToken).ConfigureAwait(false);
        if (checksum != _checksum.Value)
        {
            throw new InvalidDataException("The compressed response checksum does not match.");
        }

        if (gzip && await ReadUInt32Async(littleEndian: true, cancellationToken).ConfigureAwait(false) != _memberSize)
        {
            throw new InvalidDataException("The gzip member size does not match.");
        }
    }

    private async ValueTask<uint> ReadUInt32Async(bool littleEndian, CancellationToken cancellationToken)
    {
        uint result = 0;
        for (var index = 0; index < 4; index++)
        {
            var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            result |= (uint)value << (littleEndian ? index * 8 : (3 - index) * 8);
        }

        return result;
    }
}
