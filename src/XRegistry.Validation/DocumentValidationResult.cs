// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Validation;

/// <summary>The outcome of checking a document's declared schema format.</summary>
public enum DocumentValidationStatus
{
    /// <summary>The implemented format rules were checked successfully.</summary>
    Valid,

    /// <summary>The document violates a checked format rule.</summary>
    Invalid,

    /// <summary>The format, version, or required construct is not implemented.</summary>
    Unsupported,

    /// <summary>A budget or unavailable reference prevented a decision.</summary>
    Indeterminate,
}

/// <summary>An owned location, stable machine-readable code, and explanatory detail.</summary>
public sealed record DocumentDiagnostic(string Path, string Code, string Detail);

/// <summary>An immutable format-validation outcome; it retains no input buffers.</summary>
public sealed class DocumentValidationResult
{
    /// <summary>Creates an outcome, copying the diagnostics into owned storage.</summary>
    public DocumentValidationResult(
        DocumentValidationStatus status,
        IEnumerable<DocumentDiagnostic>? diagnostics = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        Diagnostics = Array.AsReadOnly(diagnostics?.ToArray() ?? []);
        if (Diagnostics.Any(d => d is null || string.IsNullOrWhiteSpace(d.Path)
            || string.IsNullOrWhiteSpace(d.Code) || string.IsNullOrWhiteSpace(d.Detail)))
        {
            throw new ArgumentException("Diagnostics require a path, code, and detail.", nameof(diagnostics));
        }

        if (status != DocumentValidationStatus.Valid && Diagnostics.Count == 0)
        {
            throw new ArgumentException("An undecided or invalid outcome requires a diagnostic.", nameof(diagnostics));
        }
    }

    /// <summary>Gets the decision, never a success-shaped fallback.</summary>
    public DocumentValidationStatus Status { get; }

    /// <summary>Gets the owned diagnostics.</summary>
    public IReadOnlyList<DocumentDiagnostic> Diagnostics { get; }
}
