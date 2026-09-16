// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XRegistry.Bindings.Git.Objects;

internal static class GitPack
{
    private sealed class Entry(long offset, int type, byte[] data, Entry? offsetBase, GitObjectId? referenceBase)
    {
        internal long Offset { get; } = offset;
        internal int Type { get; } = type;
        internal byte[] Data { get; } = data;
        internal Entry? OffsetBase { get; } = offsetBase;
        internal GitObjectId? ReferenceBase { get; } = referenceBase;
        internal GitObject? Object { get; set; }
        internal int Depth { get; set; }
    }

    internal static GitObjectSet Read(Stream source, GitHashAlgorithm algorithm, GitReadLimits limits, CancellationToken cancellationToken)
    {
        using var hash = GitHash.Create(algorithm);
        var budget = new GitReadBudget(limits, cancellationToken);
        var input = new GitInput(source, budget, hash);
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("PACK"u8))
        {
            throw new GitDataException(GitFailure.MalformedData, "The Git pack signature is invalid.");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (version is not (2 or 3))
        {
            throw new GitDataException(GitFailure.UnsupportedFormat, "Only Git pack versions 2 and 3 are supported.");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (count > limits.MaxObjects)
        {
            throw new GitDataException(GitFailure.LimitExceeded, "The Git pack object-count budget was exceeded.");
        }

        var objects = new Dictionary<GitObjectId, GitObject>();
        var entries = new List<Entry>();
        var offsets = new Dictionary<long, Entry>();
        var deltas = 0;
        for (uint index = 0; index < count; index++)
        {
            var offset = input.Position;
            var (type, size) = ReadObjectHeader(input);
            budget.CheckObjectSize(size);
            Entry? offsetBase = null;
            GitObjectId? referenceBase = null;
            if (type is 6 or 7)
            {
                if (++deltas > limits.MaxDeltaObjects)
                {
                    throw new GitDataException(GitFailure.LimitExceeded, "The delta entry budget was exceeded.");
                }

                if (type == 6)
                {
                    var distance = ReadOffset(input);
                    if (distance == 0 || distance > (ulong)offset ||
                        !offsets.TryGetValue(offset - (long)distance, out offsetBase))
                    {
                        throw new GitDataException(GitFailure.MalformedData, "An OFS_DELTA base must be an exact preceding entry.");
                    }
                }
                else
                {
                    var reference = new byte[GitObjectId.GetByteLength(algorithm)];
                    input.ReadExactly(reference);
                    referenceBase = GitObjectId.FromBytes(algorithm, reference);
                }
            }

            var content = GitZlib.Read(input, budget, limits.MaxObjectBytes, (long)size);
            var entry = new Entry(offset, type, content, offsetBase, referenceBase);
            offsets.Add(offset, entry);
            entries.Add(entry);
            if (type is not (6 or 7))
            {
                var objectType = (GitObjectType)type;
                var id = GitObjectId.Compute(algorithm, objectType, content);
                entry.Object = new GitObject(id, objectType, content);
                GitObjectSet.Add(objects, entry.Object);
            }
        }

        var actualChecksum = input.FinishHash();
        Span<byte> checksum = stackalloc byte[GitObjectId.GetByteLength(algorithm)];
        input.ReadExactly(checksum);
        if (!CryptographicOperations.FixedTimeEquals(actualChecksum, checksum))
        {
            throw new GitDataException(GitFailure.IntegrityMismatch, "The Git pack checksum does not match.");
        }

        input.RequireEnd();
        ResolveDeltas(entries, objects, algorithm, budget, deltas);
        return new GitObjectSet(algorithm, objects);
    }

    private static void ResolveDeltas(
        List<Entry> entries, Dictionary<GitObjectId, GitObject> objects, GitHashAlgorithm algorithm,
        GitReadBudget budget, int unresolved)
    {
        var byOffset = new Dictionary<long, List<Entry>>();
        var byId = new Dictionary<GitObjectId, List<Entry>>();
        var ready = new Queue<Entry>();
        foreach (var entry in entries)
        {
            if (entry.Object is not null)
            {
                ready.Enqueue(entry);
            }
            else if (entry.OffsetBase is not null)
            {
                if (!byOffset.TryGetValue(entry.OffsetBase.Offset, out var list))
                {
                    byOffset.Add(entry.OffsetBase.Offset, list = []);
                }

                list.Add(entry);
            }
            else
            {
                var id = entry.ReferenceBase!;
                if (!byId.TryGetValue(id, out var list))
                {
                    byId.Add(id, list = []);
                }

                list.Add(entry);
            }
        }

        while (ready.TryDequeue(out var resolved))
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            var dependents = new List<Entry>();
            if (byOffset.Remove(resolved.Offset, out var offsets))
            {
                dependents.AddRange(offsets);
            }

            if (byId.Remove(resolved.Object!.Id, out var references))
            {
                dependents.AddRange(references);
            }

            foreach (var entry in dependents)
            {
                if (resolved.Depth >= budget.Limits.MaxDeltaDepth)
                {
                    throw new GitDataException(GitFailure.LimitExceeded, "The reconstructed delta chain is too deep.");
                }

                var content = GitDelta.Apply(resolved.Object.Content, entry.Data, budget);
                var id = GitObjectId.Compute(algorithm, resolved.Object.Type, content);
                entry.Object = new GitObject(id, resolved.Object.Type, content);
                entry.Depth = resolved.Depth + 1;
                GitObjectSet.Add(objects, entry.Object);
                ready.Enqueue(entry);
                unresolved--;
            }
        }

        if (unresolved != 0)
        {
            throw new GitDataException(GitFailure.UnresolvedDeltaBase,
                "The pack has missing, thin, or cyclic delta bases; no partial object set was published.");
        }
    }

    private static ulong ReadOffset(GitInput input)
    {
        var value = input.ReadByte();
        ulong offset = (uint)(value & 127);
        while ((value & 128) != 0)
        {
            value = input.ReadByte();
            if (offset >= (ulong.MaxValue >> 7))
            {
                throw new GitDataException(GitFailure.MalformedData, "An OFS_DELTA distance overflows.");
            }

            offset = ((offset + 1) << 7) + (uint)(value & 127);
        }

        return offset;
    }

    private static (int Type, ulong Size) ReadObjectHeader(GitInput input)
    {
        var value = input.ReadByte();
        var type = (value >> 4) & 7;
        if (type is 0 or 5)
        {
            throw new GitDataException(GitFailure.MalformedData, "The Git pack object type is reserved.");
        }

        ulong size = (uint)(value & 15);
        var shift = 4;
        while ((value & 128) != 0)
        {
            value = input.ReadByte();
            var chunk = (uint)(value & 127);
            if (shift >= 64 || chunk > (ulong.MaxValue >> shift))
            {
                throw new GitDataException(GitFailure.MalformedData, "A Git pack object length overflows UInt64.");
            }

            size |= (ulong)chunk << shift;
            shift += 7;
        }

        return (type, size);
    }
}
