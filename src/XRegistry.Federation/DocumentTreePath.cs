// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Federation;

/// <summary>Shared portable-path validation for native File and Git document-tree readers.</summary>
public static class DocumentTreePath
{
    /// <summary>Validates an exact root-relative storage path without decoding it or treating Registry IDs as filenames.</summary>
    public static void Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        FederationSyntax.StoragePath(path);
    }
}
