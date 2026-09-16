// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Validation;

/// <summary>An explicit schema-evolution decision, separate from format syntax validity.</summary>
public enum DocumentCompatibilityStatus
{
    /// <summary>The documented policy establishes compatibility.</summary>
    Compatible,
    /// <summary>The documented policy establishes incompatibility.</summary>
    Incompatible,
    /// <summary>The format, mode or required evolution rule has no implemented policy.</summary>
    Unsupported,
    /// <summary>Missing history, invalid inputs, unavailable references or a budget prevented a decision.</summary>
    Indeterminate
}

/// <summary>A compatibility result and owned diagnostics. Unsupported is never equivalent to compatible.</summary>
public sealed class DocumentCompatibilityResult
{
    /// <summary>Creates a decision with explicit diagnostics for every negative or undecided outcome.</summary>
    public DocumentCompatibilityResult(DocumentCompatibilityStatus status, IEnumerable<DocumentDiagnostic>? diagnostics = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        Diagnostics = Array.AsReadOnly(diagnostics?.ToArray() ?? []);
        if (status != DocumentCompatibilityStatus.Compatible && Diagnostics.Count == 0 ||
            Diagnostics.Any(static diagnostic => diagnostic is null ||
                string.IsNullOrWhiteSpace(diagnostic.Path) || string.IsNullOrWhiteSpace(diagnostic.Code) ||
                string.IsNullOrWhiteSpace(diagnostic.Detail)))
        {
            throw new ArgumentException("A negative or undecided compatibility result needs valid diagnostics.", nameof(diagnostics));
        }
    }

    /// <summary>Gets the explicit policy outcome.</summary>
    public DocumentCompatibilityStatus Status { get; }
    /// <summary>Gets owned reasons for negative or indeterminate decisions.</summary>
    public IReadOnlyList<DocumentDiagnostic> Diagnostics { get; }
}

/// <summary>Checks a candidate against ordered nearest-first ancestors supplied by the authoritative host.</summary>
public interface IDocumentCompatibilityValidator
{
    /// <summary>Checks one documented format/mode policy; no history or schema is implicitly fetched.</summary>
    ValueTask<DocumentCompatibilityResult> CheckAsync(string format, string mode, ReadOnlyMemory<byte> candidate,
        IReadOnlyList<ReadOnlyMemory<byte>> ancestors, DocumentValidationOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Avro reader/writer resolution plus validated exact-schema identity for other supported formats.</summary>
/// <remarks>Non-identical schemas outside the Avro policy remain explicitly unsupported.</remarks>
public sealed class BuiltInDocumentCompatibilityValidator : IDocumentCompatibilityValidator
{
    /// <inheritdoc />
    public async ValueTask<DocumentCompatibilityResult> CheckAsync(
        string format, string mode, ReadOnlyMemory<byte> candidate, IReadOnlyList<ReadOnlyMemory<byte>> ancestors,
        DocumentValidationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(ancestors);
        options ??= new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = mode.ToLowerInvariant();
        if (normalized is not ("backward" or "backward_transitive" or "forward" or "forward_transitive" or "full" or "full_transitive"))
        {
            return Failure(DocumentCompatibilityStatus.Unsupported, "compatibility.mode", "The compatibility mode is not implemented.");
        }

        if (ancestors.Count > options.MaxHistory)
        {
            return Failure(DocumentCompatibilityStatus.Indeterminate, "limit.history", "The supplied ancestor history exceeds its budget.");
        }

        var count = normalized.EndsWith("_transitive", StringComparison.Ordinal) ? ancestors.Count : Math.Min(1, ancestors.Count);
        long bytes = candidate.Length;
        for (var index = 0; index < count; index++)
        {
            if (ancestors[index].Length > options.MaxDocumentBytes ||
                ancestors[index].Length > options.MaxTotalBytes - bytes)
            {
                return Failure(DocumentCompatibilityStatus.Indeterminate, "limit.total_bytes", "The schema history exceeds the input byte budget.");
            }

            bytes += ancestors[index].Length;
        }

        try
        {
            var context = new ValidationContext(options, cancellationToken);
            if (AvroSyntax.IsFormat(format))
            {
                var current = new AvroSyntax(format, context).Parse(candidate);
                for (var index = 0; index < count; index++)
                {
                    var previous = new AvroSyntax(format, context).Parse(ancestors[index]);
                    var backward = normalized.StartsWith("backward", StringComparison.Ordinal) ||
                        normalized.StartsWith("full", StringComparison.Ordinal);
                    var forward = normalized.StartsWith("forward", StringComparison.Ordinal) ||
                        normalized.StartsWith("full", StringComparison.Ordinal);
                    if (backward && !AvroResolution.CanRead(previous, current, context) ||
                        forward && !AvroResolution.CanRead(current, previous, context))
                    {
                        return Failure(DocumentCompatibilityStatus.Incompatible, "compatibility.avro",
                            $"Avro reader/writer resolution fails for ancestor {index}.");
                    }
                }

                return new(DocumentCompatibilityStatus.Compatible);
            }

            var validator = new BuiltInDocumentValidator();
            var valid = await validator.ValidateAsync(format, candidate, options, cancellationToken).ConfigureAwait(false);
            if (valid.Status != DocumentValidationStatus.Valid)
            {
                return new(valid.Status == DocumentValidationStatus.Unsupported ?
                    DocumentCompatibilityStatus.Unsupported : DocumentCompatibilityStatus.Indeterminate, valid.Diagnostics);
            }

            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidate.Span.SequenceEqual(ancestors[index].Span))
                {
                    return Failure(DocumentCompatibilityStatus.Unsupported, "compatibility.policy",
                        "This format has only a validated exact-schema identity policy; changes need a separately qualified policy.");
                }
            }

            return new(DocumentCompatibilityStatus.Compatible);
        }
        catch (ValidationFailure failure)
        {
            return new(failure.Status == DocumentValidationStatus.Unsupported ?
                DocumentCompatibilityStatus.Unsupported : DocumentCompatibilityStatus.Indeterminate, [failure.Diagnostic]);
        }
    }

    private static DocumentCompatibilityResult Failure(DocumentCompatibilityStatus status, string code, string detail) =>
        new(status, [new("$", code, detail)]);
}

internal static class AvroResolution
{
    internal static bool CanRead(AvroType writer, AvroType reader, ValidationContext context) =>
        Visit(writer, reader, context, [], 1);

    private static bool Visit(AvroType writer, AvroType reader, ValidationContext context,
        HashSet<(AvroType Writer, AvroType Reader)> active, int depth)
    {
        context.Node(depth);
        context.Work();
        if (!active.Add((writer, reader)))
        {
            return true;
        }

        try
        {
            if (writer.Kind == "union")
            {
                return writer.Branches.All(branch => Visit(branch, reader, context, active, depth + 1));
            }

            if (reader.Kind == "union")
            {
                return reader.Branches.Any(branch => Visit(writer, branch, context, active, depth + 1));
            }

            if (writer.Kind != reader.Kind)
            {
                return (writer.Kind, reader.Kind) is ("int", "long" or "float" or "double") or
                    ("long", "float" or "double") or ("float", "double") or ("string", "bytes") or ("bytes", "string");
            }

            if (writer.Kind is "record" or "enum" or "fixed")
            {
                var ns = reader.Name!.Contains('.', StringComparison.Ordinal)
                    ? reader.Name[..(reader.Name.LastIndexOf('.') + 1)] : "";
                if (writer.Name != reader.Name && !reader.Aliases.Any(alias =>
                    (alias.Contains('.', StringComparison.Ordinal) ? alias : ns + alias) == writer.Name))
                {
                    return false;
                }
            }

            switch (writer.Kind)
            {
                case "record":
                    foreach (var field in reader.Fields.Values)
                    {
                        context.Work(writer.Fields.Count + 1);
                        var matches = writer.Fields.Values.Where(candidate =>
                            candidate.Name == field.Name || field.Aliases.Contains(candidate.Name, StringComparer.Ordinal)).ToArray();
                        if (matches.Length > 1 || matches.Length == 0 && !field.HasDefault ||
                            matches.Length == 1 && !Visit(matches[0].Type, field.Type, context, active, depth + 1))
                        {
                            return false;
                        }
                    }

                    return true;
                case "enum":
                    return reader.EnumDefault is not null || writer.Symbols.IsSubsetOf(reader.Symbols);
                case "fixed":
                    return writer.Size == reader.Size;
                case "array":
                case "map":
                    return Visit(writer.Item!, reader.Item!, context, active, depth + 1);
                default:
                    return true;
            }
        }
        finally
        {
            active.Remove((writer, reader));
        }
    }
}
