// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Publication;

/// <summary>Validates one supported extension against a publication's pinned context.</summary>
public interface IPublicationExtensionValidator
{
    /// <summary>Gets the exact extension URI supported by this validator.</summary>
    string ExtensionUri { get; }

    /// <summary>Validates the extension's verified bytes and required snapshot semantics.</summary>
    /// <remarks>Throw if validation cannot complete. The reader does not accept the snapshot until validation succeeds.</remarks>
    ValueTask ValidateAsync(PublicationOpenedSnapshot snapshot, ReadOnlyMemory<byte> exactExtensionBytes,
        CancellationToken cancellationToken = default);
}
