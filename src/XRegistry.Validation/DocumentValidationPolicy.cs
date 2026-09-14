namespace XRegistry.Validation;

/// <summary>A host-selected model validation policy. This does not acquire Documents or ancestors.</summary>
public sealed record DocumentValidationPolicyOptions
{
    /// <summary>Gets whether format validation is enabled by the effective Resource model.</summary>
    public bool ValidateFormat { get; init; }
    /// <summary>Gets whether compatibility validation is enabled; it requires format validation.</summary>
    public bool ValidateCompatibility { get; init; }
    /// <summary>Gets whether unknown/indeterminate validation must reject publication.</summary>
    public bool StrictValidation { get; init; }
    /// <summary>Gets the Version format. Absent format disables both checks as specified.</summary>
    public string? Format { get; init; }
    /// <summary>Gets the Resource Meta compatibility mode. Absent mode disables compatibility checking.</summary>
    public string? Compatibility { get; init; }
}

/// <summary>A publication decision and permitted generated validation metadata.</summary>
public sealed class DocumentValidationPolicyResult
{
    internal DocumentValidationPolicyResult(bool accepted, bool? format, bool? compatibility,
        IEnumerable<DocumentDiagnostic>? diagnostics = null)
    {
        Accepted = accepted;
        FormatValidated = format;
        CompatibilityValidated = compatibility;
        Diagnostics = Array.AsReadOnly(diagnostics?.ToArray() ?? []);
    }

    /// <summary>Gets whether this validation policy permits publication; other host checks still apply.</summary>
    public bool Accepted { get; }
    /// <summary>Gets true for a successful format check, false for an unchecked non-strict outcome, or null if not applicable/rejected.</summary>
    public bool? FormatValidated { get; }
    /// <summary>Gets true for established compatibility, false for unchecked non-strict compatibility, or null if not applicable/rejected.</summary>
    public bool? CompatibilityValidated { get; }
    /// <summary>Gets explicit reasons for rejection or unchecked validation; false never represents a failed check.</summary>
    public IReadOnlyList<DocumentDiagnostic> Diagnostics { get; }
}

/// <summary>Applies core format/compatibility/strictness semantics without converting failed checks into false-success flags.</summary>
public sealed class DocumentValidationPolicy
{
    private readonly IDocumentValidator _syntax;
    private readonly IDocumentCompatibilityValidator _compatibility;

    /// <summary>Creates a policy with explicitly selected validator implementations or the built-in conservative policies.</summary>
    public DocumentValidationPolicy(IDocumentValidator? syntax = null, IDocumentCompatibilityValidator? compatibility = null)
    {
        _syntax = syntax ?? new BuiltInDocumentValidator();
        _compatibility = compatibility ?? new BuiltInDocumentCompatibilityValidator();
    }

    /// <summary>Evaluates exact candidate bytes and authoritative, nearest-first ancestor bytes.</summary>
    public async ValueTask<DocumentValidationPolicyResult> EvaluateAsync(
        DocumentValidationPolicyOptions policy, ReadOnlyMemory<byte> document,
        IReadOnlyList<ReadOnlyMemory<byte>> ancestors, DocumentValidationOptions? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ancestors);
        cancellationToken.ThrowIfCancellationRequested();
        if (policy.ValidateCompatibility && !policy.ValidateFormat)
        {
            throw new ArgumentException("Compatibility validation requires format validation.", nameof(policy));
        }

        if (!policy.ValidateFormat || string.IsNullOrEmpty(policy.Format))
        {
            return new(true, null, null);
        }

        var format = await _syntax.ValidateAsync(policy.Format, document, limits, cancellationToken).ConfigureAwait(false);
        if (format.Status == DocumentValidationStatus.Invalid)
        {
            return new(false, null, null, format.Diagnostics);
        }

        if (format.Status != DocumentValidationStatus.Valid)
        {
            return new(!policy.StrictValidation, policy.StrictValidation ? null : false,
                policy.ValidateCompatibility && policy.Compatibility is not null && !policy.StrictValidation ? false : null,
                format.Diagnostics);
        }

        if (!policy.ValidateCompatibility || string.IsNullOrEmpty(policy.Compatibility))
        {
            return new(true, true, null);
        }

        var compatibility = await _compatibility.CheckAsync(policy.Format, policy.Compatibility,
            document, ancestors, limits, cancellationToken).ConfigureAwait(false);
        return compatibility.Status switch
        {
            DocumentCompatibilityStatus.Compatible => new(true, true, true),
            DocumentCompatibilityStatus.Incompatible => new(false, true, null, compatibility.Diagnostics),
            _ => new(!policy.StrictValidation, true, policy.StrictValidation ? null : false, compatibility.Diagnostics)
        };
    }
}
