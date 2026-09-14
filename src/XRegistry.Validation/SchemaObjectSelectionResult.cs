namespace XRegistry.Validation;

/// <summary>A concrete schema selection decision.</summary>
public enum SchemaObjectSelectionStatus
{
    /// <summary>Exactly one supported, concrete declaration was selected.</summary>
    Selected,

    /// <summary>The Document, selector, or selected node is invalid for this operation.</summary>
    Invalid,

    /// <summary>A valid selector did not match a declaration in the supplied Document.</summary>
    NotFound,

    /// <summary>More than one declaration matches; no first-match fallback is made.</summary>
    Ambiguous,

    /// <summary>The format, schema construct, or selector expression is not implemented.</summary>
    Unsupported,

    /// <summary>A finite budget or an unavailable explicit dependency prevented a decision.</summary>
    Indeterminate,
}

/// <summary>An immutable result with an owned selection or an explicit diagnostic.</summary>
public sealed class SchemaObjectSelectionResult
{
    internal SchemaObjectSelectionResult(SchemaObject selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Status = SchemaObjectSelectionStatus.Selected;
        Selection = selection;
        Diagnostics = Array.AsReadOnly(Array.Empty<DocumentDiagnostic>());
    }

    internal SchemaObjectSelectionResult(SchemaObjectSelectionStatus status, DocumentDiagnostic diagnostic)
    {
        Status = status;
        Diagnostics = Array.AsReadOnly(new[] { diagnostic });
    }

    /// <summary>Gets the decision; non-selection outcomes never contain a selection.</summary>
    public SchemaObjectSelectionStatus Status { get; }

    /// <summary>Gets the owned concrete selection, only when <see cref="Status"/> is Selected.</summary>
    public SchemaObject? Selection { get; }

    /// <summary>Gets stable machine-readable diagnostics; failures always have at least one.</summary>
    public IReadOnlyList<DocumentDiagnostic> Diagnostics { get; }
}
