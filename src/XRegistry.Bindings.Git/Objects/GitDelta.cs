// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Bindings.Git.Objects;

internal static class GitDelta
{
    internal static byte[] Apply(ReadOnlySpan<byte> source, ReadOnlySpan<byte> program, GitReadBudget budget)
    {
        var index = 0;
        var sourceLength = ReadSize(program, ref index);
        var resultLength = ReadSize(program, ref index);
        if (sourceLength != (ulong)source.Length)
        {
            throw Malformed("The delta base length does not match its verified object.");
        }

        budget.CheckObjectSize(resultLength);
        budget.AddExpanded((long)resultLength);
        var result = new byte[(int)resultLength];
        var written = 0;
        while (index < program.Length)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            budget.AddDeltaInstruction();
            var instruction = program[index++];
            if (instruction == 0)
            {
                throw Malformed("Delta instruction zero is reserved.");
            }

            if ((instruction & 0x80) == 0)
            {
                var count = (int)instruction;
                if (count > program.Length - index)
                {
                    throw Truncated();
                }

                if (count > result.Length - written)
                {
                    throw Malformed("Delta insertion exceeds the declared result length.");
                }

                budget.AddDeltaWork(count);
                program.Slice(index, count).CopyTo(result.AsSpan(written));
                index += count;
                written += count;
            }
            else
            {
                uint offset = 0;
                uint count = 0;
                for (var byteIndex = 0; byteIndex < 4; byteIndex++)
                {
                    if ((instruction & (1 << byteIndex)) != 0)
                    {
                        offset |= (uint)ReadByte(program, ref index) << (8 * byteIndex);
                    }
                }

                for (var byteIndex = 0; byteIndex < 3; byteIndex++)
                {
                    if ((instruction & (0x10 << byteIndex)) != 0)
                    {
                        count |= (uint)ReadByte(program, ref index) << (8 * byteIndex);
                    }
                }

                if (count == 0)
                {
                    count = 0x10000;
                }

                if (offset > source.Length || count > source.Length - offset || count > result.Length - written)
                {
                    throw Malformed("Delta copy range exceeds its base or result.");
                }

                budget.AddDeltaWork(count);
                source.Slice((int)offset, (int)count).CopyTo(result.AsSpan(written));
                written += (int)count;
            }
        }

        if (written != result.Length)
        {
            throw Malformed("The delta did not produce its declared result length.");
        }

        return result;
    }

    private static ulong ReadSize(ReadOnlySpan<byte> program, ref int index)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var value = ReadByte(program, ref index);
            var payload = (uint)(value & 127);
            if (shift >= 64 || payload > (ulong.MaxValue >> shift))
            {
                throw Malformed("A delta length overflows UInt64.");
            }

            result |= (ulong)payload << shift;
            if ((value & 128) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> program, ref int index) =>
        index < program.Length ? program[index++] : throw Truncated();

    private static GitDataException Truncated() => new(GitFailure.TruncatedInput, "The delta instruction stream is truncated.");
    private static GitDataException Malformed(string message) => new(GitFailure.MalformedData, message);
}
