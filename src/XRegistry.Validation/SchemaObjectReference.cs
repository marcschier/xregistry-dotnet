// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Validation;

internal sealed record SchemaObjectReference(string DocumentReference, string Selector)
{
    internal static SchemaObjectReference Parse(string schemaUri, SchemaObjectSelectionOptions options,
        bool named, ValidationContext context)
    {
        var reference = options.DocumentReference;
        if (schemaUri.Length > options.MaxUriLength || reference?.Length > options.MaxUriLength)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/selector", "limit.uri",
                "The schema URI or Document reference exceeds its character limit.");
        }
        context.Work(schemaUri.Length + (reference?.Length ?? 0), "$/selector");
        var hash = schemaUri.IndexOf('#');
        if (hash >= 0 && schemaUri.IndexOf('#', hash + 1) >= 0)
        {
            throw Invalid("selection.uri", "A URI can contain only one raw fragment delimiter.");
        }
        if (reference is null)
        {
            if (hash == 0)
            {
                throw Invalid("selection.document_reference_required",
                    "A fragment-only URI requires the exact acquired Document reference; Registry IDs and selectors cannot be guessed.");
            }
            reference = hash < 0 ? schemaUri : schemaUri[..hash];
        }

        var referenceHash = reference.IndexOf('#');
        if (referenceHash >= 0)
        {
            if (reference.IndexOf('#', referenceHash + 1) >= 0)
            {
                throw Invalid("selection.uri", "The Document reference has more than one fragment delimiter.");
            }
            SchemaJson.DecodeFragment(reference[(referenceHash + 1)..], "$/selector", "selection.uri", context);
        }

        string rawSelector;
        if (schemaUri.Equals(reference, StringComparison.Ordinal))
        {
            rawSelector = "";
        }
        else if (referenceHash >= 0 && schemaUri.StartsWith(reference, StringComparison.Ordinal))
        {
            rawSelector = schemaUri[reference.Length..];
        }
        else if (referenceHash < 0 && schemaUri.StartsWith(reference + "#", StringComparison.Ordinal))
        {
            rawSelector = schemaUri[(reference.Length + 1)..];
        }
        else
        {
            throw Invalid("selection.document_reference_mismatch",
                "The URI must extend the exact raw acquired Document reference; equivalent or normalized spellings are not interchangeable.");
        }

        if (rawSelector.Length > options.MaxSelectorLength)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, "$/selector", "limit.selector",
                "The selector exceeds its character limit.");
        }
        var selector = SchemaJson.DecodeFragment(rawSelector, "$/selector", "selection.uri", context);
        if (referenceHash >= 0 && selector.Length != 0)
        {
            if (named && selector[0] == ':')
            {
                selector = selector[1..];
                if (selector.Length == 0)
                {
                    throw Invalid("selection.name", "An explicit ':Name' suffix cannot have an empty name.");
                }
            }
            else if (named || selector[0] != '/')
            {
                throw Invalid("selection.document_reference_mismatch",
                    "An existing fragment requires ':Name' for a named type, or an appended structural pointer.");
            }
        }
        return new(reference, selector);
    }

    private static SchemaObjectSelectionFailure Invalid(string code, string detail)
        => new(SchemaObjectSelectionStatus.Invalid, new("$/selector", code, detail));
}

internal sealed class SchemaObjectSelectionFailure(
    SchemaObjectSelectionStatus status, DocumentDiagnostic diagnostic) : Exception(diagnostic.Detail)
{
    internal SchemaObjectSelectionStatus Status { get; } = status;
    internal DocumentDiagnostic Diagnostic { get; } = diagnostic;

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void Fail(SchemaObjectSelectionStatus status, string code, string detail, string path = "$/selector")
        => throw new SchemaObjectSelectionFailure(status, new(path, code, detail));
}
