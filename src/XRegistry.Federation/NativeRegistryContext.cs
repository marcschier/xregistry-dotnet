// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Federation;

/// <summary>
/// Immutable, credential-free selected binding context. Revision and root-byte evidence are not entity IDs.
/// A root hash alone does not imply snapshot isolation or publisher authenticity.
/// </summary>
public sealed record NativeRegistryContext
{
    /// <summary>Creates exact source context. An immutable view requires an actual resolved revision pin.</summary>
    public NativeRegistryContext(string binding, string source, string? revision = null,
        bool isImmutable = false, string? rootSha256 = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(binding);
        ArgumentException.ThrowIfNullOrEmpty(source);
        FederationJson.AbsoluteUri(source);
        if (revision is { Length: 0 } || (isImmutable && revision is null))
        {
            throw new ArgumentException("An immutable view requires a nonempty resolved revision.", nameof(revision));
        }
        if (rootSha256 is not null && (rootSha256.Length != 64 ||
            !rootSha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A root SHA-256 must have 64 lowercase hexadecimal digits.", nameof(rootSha256));
        }
        Binding = binding;
        Source = source;
        Revision = revision;
        IsImmutable = isImmutable;
        RootSha256 = rootSha256;
    }

    /// <summary>The exact binding discriminator.</summary>
    public string Binding { get; }
    /// <summary>The exact selected source locator; credentials belong to the supplied reader, not here.</summary>
    public string Source { get; }
    /// <summary>Binding-specific resolved revision evidence, or no pin.</summary>
    public string? Revision { get; }
    /// <summary>Whether the binding guarantees immutable revision reads.</summary>
    public bool IsImmutable { get; }
    /// <summary>SHA-256 of the exact captured registry.json bytes, when applicable.</summary>
    public string? RootSha256 { get; }
    /// <summary>The selected binding-relative storage root, when distinct from the repository locator.</summary>
    public string? RootPath { get; init; }
    /// <summary>The original mutable revision selector, retained separately from the resolved immutable revision.</summary>
    public string? RequestedRevision { get; init; }
    /// <summary>The selected catalog-description Version and advertisement, when acquisition was explicitly catalog-directed.</summary>
    public FederationCatalogOrigin? CatalogOrigin { get; init; }
}
