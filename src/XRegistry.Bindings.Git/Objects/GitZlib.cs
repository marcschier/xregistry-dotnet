// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.IO.Compression;

namespace XRegistry.Bindings.Git.Objects;

internal static class GitZlib
{
    internal static byte[] Read(GitInput input, GitReadBudget budget, long maximumOutput, long? expectedLength = null)
    {
        using var encoded = new MemoryStream();
        var scanner = new Scanner(input, budget, encoded, maximumOutput, expectedLength);
        var adler = scanner.Scan();
        var output = new byte[checked((int)scanner.OutputLength)];
        using var source = new MemoryStream(encoded.GetBuffer(), 2, checked((int)encoded.Length) - 6, writable: false);
        try
        {
            // The scanner proved the raw DEFLATE boundary. Read-ahead cannot eat Adler-32 or the next object.
            using var inflater = new DeflateStream(source, CompressionMode.Decompress);
            var offset = 0;
            while (offset != output.Length)
            {
                budget.CancellationToken.ThrowIfCancellationRequested();
                var read = inflater.Read(output.AsSpan(offset, Math.Min(8192, output.Length - offset)));
                if (read == 0)
                {
                    throw new GitDataException(GitFailure.TruncatedInput, "A Git zlib member ended before its decoded size.");
                }

                offset += read;
            }

            if (inflater.ReadByte() != -1)
            {
                throw new GitDataException(GitFailure.MalformedData, "A Git zlib member exceeds its decoded size.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new GitDataException(GitFailure.MalformedData, "The bounded Git zlib member is invalid.", exception);
        }

        if (Adler32(output, budget.CancellationToken) != adler)
        {
            throw new GitDataException(GitFailure.IntegrityMismatch, "The Git zlib Adler-32 checksum does not match.");
        }

        return output;
    }

    private static uint Adler32(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        uint first = 1;
        uint second = 0;
        while (!bytes.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(bytes.Length, 5552);
            foreach (var value in bytes[..count])
            {
                first += value;
                second += first;
            }

            first %= 65521;
            second %= 65521;
            bytes = bytes[count..];
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (second << 16) | first;
    }

    private sealed class Scanner(
        GitInput input,
        GitReadBudget budget,
        MemoryStream encoded,
        long maximumOutput,
        long? expectedLength)
    {
        private static readonly Huffman FixedLiterals = CreateFixedLiterals();
        private static readonly Huffman FixedDistances = CreateFixedDistances();
        private uint bits;
        private int bitCount;
        private int windowSize;

        internal long OutputLength { get; private set; }

        internal uint Scan()
        {
            var method = ReadByte();
            var flags = ReadByte();
            if ((method & 15) != 8 || (method >> 4) > 7 || (((method << 8) | flags) % 31) != 0)
            {
                throw Invalid("The Git zlib header is invalid.");
            }

            if ((flags & 32) != 0)
            {
                throw new GitDataException(GitFailure.UnsupportedFormat, "Git zlib preset dictionaries are unsupported.");
            }

            windowSize = 1 << ((method >> 4) + 8);
            bool final;
            do
            {
                budget.AddBlock();
                final = ReadBits(1) != 0;
                switch (ReadBits(2))
                {
                    case 0:
                        StoredBlock();
                        break;
                    case 1:
                        CompressedBlock(FixedLiterals, FixedDistances);
                        break;
                    case 2:
                        DynamicBlock();
                        break;
                    default:
                        throw Invalid("A DEFLATE block type is reserved.");
                }
            }
            while (!final);

            Align();
            uint adler = 0;
            for (var index = 0; index < 4; index++)
            {
                adler = (adler << 8) | (uint)ReadByte();
            }

            if (expectedLength is { } expected && OutputLength != expected)
            {
                throw Invalid("The Git zlib member does not match its declared decoded size.");
            }

            return adler;
        }

        internal int ReadBits(int count)
        {
            while (bitCount < count)
            {
                bits |= (uint)ReadByte() << bitCount;
                bitCount += 8;
            }

            var value = (int)(bits & ((1u << count) - 1));
            bits >>= count;
            bitCount -= count;
            return value;
        }

        private int ReadByte()
        {
            if (encoded.Length == budget.Limits.MaxCompressedObjectBytes)
            {
                throw new GitDataException(GitFailure.LimitExceeded, "The Git compressed-member byte budget was exceeded.");
            }

            var value = input.ReadByte();
            encoded.WriteByte((byte)value);
            return value;
        }

        private void Align()
        {
            bits = 0;
            bitCount = 0;
        }

        private void AddOutput(int count)
        {
            if (expectedLength is { } expected && count > expected - OutputLength)
            {
                throw Invalid("The Git zlib member exceeds its declared decoded size.");
            }

            if (count > maximumOutput - OutputLength)
            {
                throw new GitDataException(GitFailure.LimitExceeded, "The Git inflated object byte budget was exceeded.");
            }

            budget.AddExpanded(count);
            OutputLength += count;
        }

        private void StoredBlock()
        {
            Align();
            var length = ReadByte() | (ReadByte() << 8);
            var complement = ReadByte() | (ReadByte() << 8);
            if ((length ^ complement) != 65535)
            {
                throw Invalid("A stored DEFLATE block has an invalid length complement.");
            }

            AddOutput(length);
            budget.AddSymbols(length);
            for (var index = 0; index < length; index++)
            {
                ReadByte();
            }
        }

        private int Symbol(Huffman table)
        {
            budget.AddSymbols(1);
            return table.Decode(this);
        }

        private void CompressedBlock(Huffman literals, Huffman distances)
        {
            ReadOnlySpan<int> lengths = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
            ReadOnlySpan<byte> lengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
            ReadOnlySpan<int> offsets = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
            ReadOnlySpan<byte> offsetBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
            while (true)
            {
                var symbol = Symbol(literals);
                if (symbol < 256)
                {
                    AddOutput(1);
                }
                else if (symbol == 256)
                {
                    return;
                }
                else
                {
                    var lengthIndex = symbol - 257;
                    if ((uint)lengthIndex >= lengths.Length)
                    {
                        throw Invalid("A DEFLATE length symbol is reserved.");
                    }

                    var count = lengths[lengthIndex] + ReadBits(lengthBits[lengthIndex]);
                    var distanceIndex = Symbol(distances);
                    if ((uint)distanceIndex >= offsets.Length)
                    {
                        throw Invalid("A DEFLATE distance symbol is reserved.");
                    }

                    var distance = offsets[distanceIndex] + ReadBits(offsetBits[distanceIndex]);
                    if (distance > OutputLength || distance > windowSize)
                    {
                        throw Invalid("A DEFLATE copy refers outside its decoded window.");
                    }

                    AddOutput(count);
                }
            }
        }

        private void DynamicBlock()
        {
            var literalCount = ReadBits(5) + 257;
            var distanceCount = ReadBits(5) + 1;
            var codeCount = ReadBits(4) + 4;
            if (literalCount > 286)
            {
                throw Invalid("A DEFLATE literal alphabet is oversized.");
            }

            ReadOnlySpan<byte> order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
            Span<byte> codeLengths = stackalloc byte[19];
            codeLengths.Clear();
            for (var index = 0; index < codeCount; index++)
            {
                codeLengths[order[index]] = (byte)ReadBits(3);
            }

            var codes = new Huffman(codeLengths, allowSingle: false, allowEmpty: false);
            Span<byte> lengths = stackalloc byte[literalCount + distanceCount];
            var offset = 0;
            while (offset < lengths.Length)
            {
                var symbol = Symbol(codes);
                if (symbol <= 15)
                {
                    lengths[offset++] = (byte)symbol;
                    continue;
                }

                int repeat;
                byte repeated;
                if (symbol == 16)
                {
                    if (offset == 0)
                    {
                        throw Invalid("A DEFLATE repeat has no preceding code length.");
                    }

                    repeat = ReadBits(2) + 3;
                    repeated = lengths[offset - 1];
                }
                else
                {
                    repeat = symbol == 17 ? ReadBits(3) + 3 : ReadBits(7) + 11;
                    repeated = 0;
                }

                if (repeat > lengths.Length - offset)
                {
                    throw Invalid("A DEFLATE repeat exceeds its alphabet.");
                }

                lengths.Slice(offset, repeat).Fill(repeated);
                offset += repeat;
            }

            if (lengths[256] == 0)
            {
                throw Invalid("A DEFLATE literal alphabet has no end-of-block symbol.");
            }

            var literals = new Huffman(lengths[..literalCount], allowSingle: true, allowEmpty: false);
            var distances = new Huffman(lengths[literalCount..], allowSingle: true, allowEmpty: true);
            CompressedBlock(literals, distances);
        }

        private static Huffman CreateFixedLiterals()
        {
            Span<byte> lengths = stackalloc byte[288];
            lengths[..144].Fill(8);
            lengths[144..256].Fill(9);
            lengths[256..280].Fill(7);
            lengths[280..].Fill(8);
            return new Huffman(lengths, allowSingle: false, allowEmpty: false);
        }

        private static Huffman CreateFixedDistances()
        {
            Span<byte> lengths = stackalloc byte[32];
            lengths.Fill(5);
            return new Huffman(lengths, allowSingle: false, allowEmpty: false);
        }
    }

    private sealed class Huffman
    {
        private readonly int[] counts = new int[16];
        private readonly int[] firstCodes = new int[16];
        private readonly int[] firstSymbols = new int[16];
        private readonly int[] symbols;
        private readonly int maximumLength;

        internal Huffman(ReadOnlySpan<byte> lengths, bool allowSingle, bool allowEmpty)
        {
            var symbolCount = 0;
            foreach (var length in lengths)
            {
                if (length > 15)
                {
                    throw Invalid("A DEFLATE Huffman code is too long.");
                }

                if (length != 0)
                {
                    counts[length]++;
                    symbolCount++;
                    maximumLength = Math.Max(maximumLength, length);
                }
            }

            var remaining = 1;
            var code = 0;
            var start = 0;
            for (var length = 1; length <= 15; length++)
            {
                remaining = (remaining << 1) - counts[length];
                if (remaining < 0)
                {
                    throw Invalid("A DEFLATE Huffman alphabet is oversubscribed.");
                }

                code = (code + counts[length - 1]) << 1;
                firstCodes[length] = code;
                firstSymbols[length] = start;
                start += counts[length];
            }

            if (remaining != 0
                && !(allowEmpty && symbolCount == 0)
                && !(allowSingle && symbolCount == 1 && maximumLength == 1))
            {
                throw Invalid("A DEFLATE Huffman alphabet is incomplete.");
            }

            symbols = new int[symbolCount];
            var next = (int[])firstSymbols.Clone();
            for (var index = 0; index < lengths.Length; index++)
            {
                var length = lengths[index];
                if (length != 0)
                {
                    symbols[next[length]++] = index;
                }
            }
        }

        internal int Decode(Scanner scanner)
        {
            var code = 0;
            for (var length = 1; length <= maximumLength; length++)
            {
                code = (code << 1) | scanner.ReadBits(1);
                var offset = code - firstCodes[length];
                if ((uint)offset < counts[length])
                {
                    return symbols[firstSymbols[length] + offset];
                }
            }

            throw Invalid("A DEFLATE symbol has no Huffman code.");
        }
    }

    private static GitDataException Invalid(string message) => new(GitFailure.MalformedData, message);
}
