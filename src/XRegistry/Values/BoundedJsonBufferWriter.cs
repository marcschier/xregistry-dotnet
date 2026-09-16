// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;

namespace XRegistry;

internal sealed class BoundedJsonBufferWriter(int maxBytes) : IBufferWriter<byte>
{
    private byte[] _scratch = [];
    private byte[] _output = [];
    private int _written;

    internal ReadOnlySpan<byte> WrittenSpan => _output.AsSpan(0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _scratch.Length);
        if (count > maxBytes - _written)
        {
            throw Diagnostics.Error("byte_limit", "", "The JSON exceeds its encoded byte budget.");
        }

        var required = _written + count;
        if (required > _output.Length)
        {
            var capacity = (int)Math.Min(maxBytes, Math.Max(required, Math.Max(256L, 2L * _output.Length)));
            Array.Resize(ref _output, capacity);
        }

        _scratch.AsSpan(0, count).CopyTo(_output.AsSpan(_written));
        _written = required;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        // UTF-8 writer hints reserve worst-case escaping space, not actual encoded bytes.
        // Keep that reservation separate from the budget-capped committed output.
        var required = Math.Max(4096, sizeHint);
        if (required > _scratch.Length)
        {
            _scratch = new byte[required];
        }

        return _scratch;
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
}
