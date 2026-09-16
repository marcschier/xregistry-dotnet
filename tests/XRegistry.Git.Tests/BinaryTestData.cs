// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Git.Tests;

internal static class BinaryTestData
{
    internal static byte[] Zlib(ReadOnlySpan<byte> bytes, CompressionLevel level = CompressionLevel.SmallestSize)
    {
        using var output = new MemoryStream();
        using (var encoder = new ZLibStream(output, level, leaveOpen: true))
        {
            encoder.Write(bytes);
        }

        return output.ToArray();
    }

    internal static byte[] Canonical(string type, ReadOnlySpan<byte> content)
    {
        var header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"{type} {content.Length}\0"));
        return [.. header, .. content];
    }

    internal static byte[] Hash(GitHashAlgorithm algorithm, ReadOnlySpan<byte> bytes)
    {
        // Test-only independent Git-format oracle; ordinary SHA-1 is not collision hardening.
#pragma warning disable CA5350
        return algorithm == GitHashAlgorithm.Sha1 ? SHA1.HashData(bytes) : SHA256.HashData(bytes);
#pragma warning restore CA5350
    }

    internal static GitObjectId Id(GitHashAlgorithm algorithm, string type, ReadOnlySpan<byte> content)
        => GitObjectId.FromBytes(algorithm, Hash(algorithm, Canonical(type, content)));

    internal static byte[] Rechecksum(GitHashAlgorithm algorithm, byte[] pack)
    {
        var width = algorithm == GitHashAlgorithm.Sha1 ? 20 : 32;
        Hash(algorithm, pack.AsSpan(0, pack.Length - width)).CopyTo(pack.AsSpan(pack.Length - width));
        return pack;
    }

    internal static byte[] Pack(GitHashAlgorithm algorithm, ReadOnlySpan<byte> entries, uint count, uint version = 2)
    {
        var pack = new byte[12 + entries.Length + (algorithm == GitHashAlgorithm.Sha1 ? 20 : 32)];
        "PACK"u8.CopyTo(pack);
        BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32BigEndian(pack.AsSpan(8), count);
        entries.CopyTo(pack.AsSpan(12));
        return Rechecksum(algorithm, pack);
    }

    internal static byte[] ObjectHeader(int type, ulong length)
    {
        var result = new List<byte>();
        var first = (byte)((type << 4) | (int)(length & 15));
        length >>= 4;
        result.Add((byte)(first | (length == 0 ? 0 : 128)));
        while (length != 0)
        {
            var digit = (byte)(length & 127);
            length >>= 7;
            result.Add((byte)(digit | (length == 0 ? 0 : 128)));
        }

        return result.ToArray();
    }

    internal static byte[] Entry(int type, ReadOnlySpan<byte> content)
        => [.. ObjectHeader(type, (ulong)content.Length), .. Zlib(content)];

    internal static GitSnapshot Snapshot(GitHashAlgorithm algorithm,
        IReadOnlyDictionary<string, byte[]> files, string root = "xregistry")
    {
        var objects = new List<GitObject>();
        var named = files.ToDictionary(pair => root.Length == 0 ? pair.Key : root + "/" + pair.Key,
            pair => pair.Value, StringComparer.Ordinal);
        var tree = Tree("");
        var commit = Add(GitObjectType.Commit, Encoding.UTF8.GetBytes(
            $"tree {tree}\nauthor URI Fixture <fixture@example.invalid> 1700000000 +0000\n" +
            "committer URI Fixture <fixture@example.invalid> 1700000000 +0000\n\nIndependent mapping byte fixture\n"));
        return GitSnapshot.Open(GitObjectSet.Create(algorithm, objects), commit);

        GitObjectId Tree(string prefix)
        {
            var children = named.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => key[prefix.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal)
                .Select(name => (Name: name, Directory: !named.ContainsKey(prefix + name)))
                .OrderBy(child => child.Name + (child.Directory ? "/" : ""), StringComparer.Ordinal);
            using var data = new MemoryStream();
            foreach (var child in children)
            {
                var id = child.Directory ? Tree(prefix + child.Name + "/") : Add(GitObjectType.Blob, named[prefix + child.Name]);
                data.Write(Encoding.UTF8.GetBytes((child.Directory ? "40000 " : "100644 ") + child.Name + "\0"));
                data.Write(id.Bytes);
            }
            return Add(GitObjectType.Tree, data.ToArray());
        }

        GitObjectId Add(GitObjectType type, byte[] bytes)
        {
            var id = Id(algorithm, type.ToString().ToLowerInvariant(), bytes);
            objects.Add(GitObject.Verify(id, type, bytes));
            return id;
        }
    }
}
