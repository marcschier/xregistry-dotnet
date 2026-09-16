// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Validation;

/// <summary>Owned exact artifact bytes made available after a bounded integrity operation completes.</summary>
public sealed class OpenUsdArtifact
{
    private readonly byte[] _bytes;

    internal OpenUsdArtifact(byte[] bytes, bool isDigestVerified)
    {
        _bytes = bytes;
        IsDigestVerified = isDigestVerified;
    }

    /// <summary>Gets the number of exact artifact bytes.</summary>
    public long Length => _bytes.LongLength;

    /// <summary>Gets whether the bytes matched a declared digest; absence of a digest is not verification.</summary>
    public bool IsDigestVerified { get; }

    /// <summary>Opens an independent, nonwritable stream without exposing the internal byte array.</summary>
    public Stream OpenRead() => new MemoryStream(_bytes, 0, _bytes.Length, writable: false, publiclyVisible: false);
}
