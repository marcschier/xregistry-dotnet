// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Text.Json;
using XRegistry.Validation;

namespace XRegistry.Server;

/// <summary>Adapts bounded schema syntax, Avro resolution and conservative identity compatibility to the engine.</summary>
/// <remarks>
/// Configure this explicitly through RegistryEngineOptions.ResourceValidator. Avro uses reader/writer resolution;
/// other formats establish only validated byte identity and report changed schemas as unsupported.
/// No references are fetched by default.
/// </remarks>
public sealed class BuiltInRegistryResourceValidator : IRegistryResourceValidator
{
    private static readonly IReadOnlyList<string> s_formats = Array.AsReadOnly(new[]
    {
        "JsonSchema/draft-07", "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12",
        "XSD/1.0", "Avro/1.8.2", "Avro/1.11.0", "Protobuf/2", "Protobuf/3",
        "JsonStructure", "JsonStructure/draft-04"
    });
    private static readonly IReadOnlyList<string> s_modes = Array.AsReadOnly(new[]
    {
        "backward", "backward_transitive", "forward", "forward_transitive", "full", "full_transitive"
    });
    private static readonly FrozenDictionary<string, IReadOnlyList<string>> s_compatibilities =
        s_formats.ToFrozenDictionary(static format => format, static _ => s_modes, StringComparer.OrdinalIgnoreCase);
    private readonly BuiltInDocumentValidator _syntax = new();
    private readonly BuiltInDocumentCompatibilityValidator _compatibility = new();
    private readonly DocumentValidationOptions _options;

    /// <summary>Creates an opt-in adapter. Supplied limits are further restricted by the engine's per-operation budgets.</summary>
    /// <remarks>
    /// Work is reserved equally across requested syntax and compatibility checks; unused shares are not reassigned.
    /// A reference resolver, when supplied, must authorize egress and honor cancellation itself.
    /// </remarks>
    public BuiltInRegistryResourceValidator(DocumentValidationOptions? options = null)
    {
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxDocumentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxTotalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxNodes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxWork, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxDepth, 128);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxHistory);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxReferences);
        if (_options.DocumentUri is { IsAbsoluteUri: false })
        {
            throw new ArgumentException("The schema document URI must be absolute.", nameof(options));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Formats => s_formats;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Compatibilities => s_compatibilities;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, RegistryVersionValidation>> ValidateAsync(
        RegistryResourceValidationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Versions);
        ArgumentNullException.ThrowIfNull(context.Resource);
        ArgumentNullException.ThrowIfNull(context.Limits);
        context.Limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Versions.Count > context.Limits.MaxEntityOperations)
        {
            throw ServerErrors.Create("operation_limit", "", "The candidate Version set exceeds the operation budget.");
        }

        var compatibility = context.Resource.ValidateCompatibility ? Text(context.Meta.RootElement, "compatibility") : null;
        var eligible = context.Versions.Where(version => context.Resource.ValidateFormat &&
            Text(version.Metadata.RootElement, "format") is { } format && FormatEnabled(context, format) && version.ExternalDocument is null).ToArray();
        var checks = (long)eligible.Length + (compatibility is null ? 0 :
            eligible.Count(version => CompatibilityEnabled(context, Text(version.Metadata.RootElement, "format")!, compatibility)));
        var perCallWork = Math.Min(_options.MaxWork, context.Limits.MaxSchemaSteps) / Math.Max(1, checks);
        var byId = context.Versions.ToDictionary(static version => version.VersionId, StringComparer.Ordinal);
        var results = new Dictionary<string, RegistryVersionValidation>(StringComparer.Ordinal);
        foreach (var version in context.Versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var format = context.Resource.ValidateFormat ? Text(version.Metadata.RootElement, "format") : null;
            if (format is null)
            {
                results.Add(version.VersionId, new(RegistryValidationStatus.NotRequested, RegistryValidationStatus.NotRequested));
                continue;
            }

            var compatibilityStatus = compatibility is null ? RegistryValidationStatus.NotRequested : RegistryValidationStatus.Unsupported;
            if (!FormatEnabled(context, format))
            {
                results.Add(version.VersionId, new(RegistryValidationStatus.Unsupported, compatibilityStatus,
                    "format.disabled: The format is not enabled by the Registry capability profile.",
                    compatibility is null ? null : "format.disabled: Compatibility cannot be checked for a disabled format."));
                continue;
            }

            if (version.ExternalDocument is not null)
            {
                results.Add(version.VersionId, new(RegistryValidationStatus.Unsupported, compatibilityStatus,
                    "reference.external: The externally stored Document has not been fetched.",
                    compatibility is null ? null : "reference.external: External Documents cannot be compared without explicit acquisition."));
                continue;
            }

            if (perCallWork == 0)
            {
                results.Add(version.VersionId, new(RegistryValidationStatus.Indeterminate, compatibilityStatus,
                    "limit.work: The resource validation work budget cannot fund every requested check."));
                continue;
            }

            var syntax = await _syntax.ValidateAsync(format, version.Document.Bytes, LimitsFor(context, version, perCallWork),
                cancellationToken).ConfigureAwait(false);
            var status = syntax.Status switch
            {
                DocumentValidationStatus.Valid => RegistryValidationStatus.Valid,
                DocumentValidationStatus.Invalid => RegistryValidationStatus.Invalid,
                DocumentValidationStatus.Unsupported => RegistryValidationStatus.Unsupported,
                DocumentValidationStatus.Indeterminate => RegistryValidationStatus.Indeterminate,
                _ => throw new InvalidOperationException("The syntax validator returned an unknown status.")
            };
            results.Add(version.VersionId, new(status, compatibilityStatus, Reason(syntax.Diagnostics),
                compatibility is null ? null : "The Version's format has not been validated."));
        }

        if (compatibility is not null)
        {
            foreach (var version in context.Versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var syntax = results[version.VersionId];
                if (syntax.Format != RegistryValidationStatus.Valid)
                {
                    continue;
                }

                var format = Text(version.Metadata.RootElement, "format")!;
                if (!CompatibilityEnabled(context, format, compatibility))
                {
                    results[version.VersionId] = syntax with
                    {
                        Compatibility = RegistryValidationStatus.Unsupported,
                        CompatibilityReason = "compatibility.disabled: The mode is not enabled for this format."
                    };
                    continue;
                }

                var limits = LimitsFor(context, version, perCallWork);
                var history = Ancestors(version, byId, results, format, compatibility, limits.MaxHistory, cancellationToken);
                if (history.Status is { } failure)
                {
                    results[version.VersionId] = syntax with { Compatibility = failure, CompatibilityReason = history.Reason };
                    continue;
                }

                var compared = await _compatibility.CheckAsync(format, compatibility, version.Document.Bytes, history.Documents,
                    limits, cancellationToken).ConfigureAwait(false);
                var status = compared.Status switch
                {
                    DocumentCompatibilityStatus.Compatible => RegistryValidationStatus.Valid,
                    DocumentCompatibilityStatus.Incompatible => RegistryValidationStatus.Invalid,
                    DocumentCompatibilityStatus.Unsupported => RegistryValidationStatus.Unsupported,
                    DocumentCompatibilityStatus.Indeterminate => RegistryValidationStatus.Indeterminate,
                    _ => throw new InvalidOperationException("The compatibility validator returned an unknown status.")
                };
                results[version.VersionId] = syntax with { Compatibility = status, CompatibilityReason = Reason(compared.Diagnostics) };
            }
        }

        return results.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static History Ancestors(RegistryVersionCandidate candidate,
        Dictionary<string, RegistryVersionCandidate> versions, Dictionary<string, RegistryVersionValidation> validated,
        string format, string mode, int limit, CancellationToken cancellationToken)
    {
        var documents = new List<ReadOnlyMemory<byte>>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { candidate.VersionId };
        var current = candidate;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ancestorId = Text(current.Metadata.RootElement, "ancestorid") ??
                throw new InvalidOperationException("The engine candidate has no ancestor identifier.");
            if (ancestorId == current.VersionId)
            {
                return new(documents, null, null);
            }

            if (documents.Count >= limit)
            {
                return new(documents, RegistryValidationStatus.Indeterminate, "limit.history: The complete required ancestor chain exceeds its budget.");
            }

            if (!visited.Add(ancestorId))
            {
                throw new InvalidOperationException("The engine candidate ancestry contains a cycle.");
            }

            if (!versions.TryGetValue(ancestorId, out var ancestor))
            {
                return new(documents, RegistryValidationStatus.Indeterminate, "history.missing: A required ancestor is not in the authoritative candidate snapshot.");
            }

            if (ancestor.ExternalDocument is not null)
            {
                return new(documents, RegistryValidationStatus.Indeterminate, "reference.external: A required ancestor Document is external and has not been fetched.");
            }

            if (!string.Equals(format, Text(ancestor.Metadata.RootElement, "format"), StringComparison.OrdinalIgnoreCase))
            {
                return new(documents, RegistryValidationStatus.Unsupported, "compatibility.format: Cross-format schema evolution is not qualified.");
            }

            if (validated[ancestorId].Format != RegistryValidationStatus.Valid)
            {
                return new(documents, RegistryValidationStatus.Indeterminate, "history.unchecked: A required ancestor has not passed its own format check.");
            }

            documents.Add(ancestor.Document.Bytes);
            if (!mode.EndsWith("_transitive", StringComparison.OrdinalIgnoreCase))
            {
                return new(documents, null, null);
            }

            current = ancestor;
        }
    }

    private DocumentValidationOptions LimitsFor(RegistryResourceValidationContext context, RegistryVersionCandidate version, long work)
    {
        var documentUri = _options.DocumentUri;
        if (documentUri is null && Text(version.Metadata.RootElement, "self") is { } self)
        {
            documentUri = new Uri(self.EndsWith("$details", StringComparison.Ordinal) ? self[..^8] : self, UriKind.Absolute);
        }

        return _options with
        {
            MaxWork = work,
            MaxNodes = Math.Min(_options.MaxNodes, (int)Math.Min(context.Limits.Json.MaxNodes, work)),
            MaxDocumentBytes = Math.Min(_options.MaxDocumentBytes, context.Limits.MaxDocumentBytes),
            MaxTotalBytes = Math.Min(_options.MaxTotalBytes, context.Limits.MaxWorkingSetBytes),
            MaxDepth = Math.Min(_options.MaxDepth, context.Limits.Json.MaxDepth),
            MaxHistory = Math.Min(_options.MaxHistory, context.Limits.MaxEntityOperations),
            DocumentUri = documentUri
        };
    }

    private static string? Text(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static bool FormatEnabled(RegistryResourceValidationContext context, string format) =>
        context.EnabledFormats is null || context.EnabledFormats.Contains(format) ||
        !s_formats.Contains(format, StringComparer.OrdinalIgnoreCase);
    private static bool CompatibilityEnabled(RegistryResourceValidationContext context, string format, string mode) =>
        context.EnabledCompatibilities is null ||
        context.EnabledCompatibilities.Any(entry => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(entry.Key, format, true) && entry.Value.Contains(mode));

    private static string? Reason(IReadOnlyList<DocumentDiagnostic> diagnostics) => diagnostics.Count == 0 ? null :
        string.Join("; ", diagnostics.Select(static diagnostic => diagnostic.Code + " at " + diagnostic.Path + ": " + diagnostic.Detail));

    private sealed record History(List<ReadOnlyMemory<byte>> Documents, RegistryValidationStatus? Status, string? Reason);
}
