// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Text;
using System.Security.Cryptography;

namespace XRegistry.Bindings.Git.Objects;

internal static class GitHash
{
    internal static IncrementalHash Create(GitHashAlgorithm algorithm)
    {
        // Git wire compatibility only. Ordinary SHA-1 is NOT collision-detecting SHA1DC or authentication.
#pragma warning disable CA5350
        return algorithm switch
        {
            GitHashAlgorithm.Sha1 => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            GitHashAlgorithm.Sha256 => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
#pragma warning restore CA5350
    }

    internal static IncrementalHash BeginObject(GitHashAlgorithm algorithm, GitObjectType type, long length)
    {
        var typeName = TypeName(type);
        Span<byte> decimalLength = stackalloc byte[20];
        if (!Utf8Formatter.TryFormat(length, decimalLength, out var written))
        {
            throw new InvalidOperationException("An Int64 Git object length could not be formatted.");
        }

        var hash = Create(algorithm);
        hash.AppendData(typeName);
        hash.AppendData(" "u8);
        hash.AppendData(decimalLength[..written]);
        hash.AppendData("\0"u8);
        return hash;
    }

    internal static ReadOnlySpan<byte> TypeName(GitObjectType type) => type switch
    {
        GitObjectType.Commit => "commit"u8,
        GitObjectType.Tree => "tree"u8,
        GitObjectType.Blob => "blob"u8,
        GitObjectType.Tag => "tag"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static GitObjectType ParseType(ReadOnlySpan<byte> name)
    {
        if (name.SequenceEqual("commit"u8))
        {
            return GitObjectType.Commit;
        }

        if (name.SequenceEqual("tree"u8))
        {
            return GitObjectType.Tree;
        }

        if (name.SequenceEqual("blob"u8))
        {
            return GitObjectType.Blob;
        }

        if (name.SequenceEqual("tag"u8))
        {
            return GitObjectType.Tag;
        }

        throw new GitDataException(GitFailure.MalformedData, "A canonical Git object type is invalid.");
    }
}
