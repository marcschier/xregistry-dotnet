// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

internal sealed class OciHttpBodyStream(Stream inner, long limit, CancellationToken deadline, CancellationToken operation) : Stream
{
    private long received;
    private bool disposed;
    public override bool CanRead => !disposed && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => received; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var remaining = limit - received;
            var available = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
            var read = await inner.ReadAsync(buffer[..available], linked.Token).ConfigureAwait(false);
            if (read > limit - received)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded, "The OCI response exceeded its exact transport byte budget.");
            }
            received += read;
            return read;
        }
        catch (OperationCanceledException exception) when (!operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "The OCI response-body deadline expired.", innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "OCI response-body transport failed.", innerException: exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
