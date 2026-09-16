// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Validation;

/// <summary>Checks source-format documents, not xRegistry metadata or data instances.</summary>
public interface IDocumentValidator
{
    /// <summary>
    /// Validates borrowed exact bytes. The caller keeps them unchanged until completion.
    /// Cancellation and resolver failures propagate; no input storage is retained.
    /// </summary>
    ValueTask<DocumentValidationResult> ValidateAsync(
        string format,
        ReadOnlyMemory<byte> document,
        DocumentValidationOptions? options = null,
        CancellationToken cancellationToken = default);
}
