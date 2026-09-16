// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;

namespace XRegistry.Server;

/// <summary>A validator outcome, deliberately distinguishing unsupported or indeterminate work from validity.</summary>
public enum RegistryValidationStatus
{
    /// <summary>The model did not request this check.</summary>
    NotRequested,
    /// <summary>The check completed successfully.</summary>
    Valid,
    /// <summary>The content violates the requested rule.</summary>
    Invalid,
    /// <summary>The format or compatibility rule is unsupported.</summary>
    Unsupported,
    /// <summary>The available evidence cannot establish validity.</summary>
    Indeterminate
}

/// <summary>Independent format and compatibility outcomes for one Version.</summary>
public sealed record RegistryVersionValidation(RegistryValidationStatus Format, RegistryValidationStatus Compatibility,
    string? FormatReason = null, string? CompatibilityReason = null);

/// <summary>A pre-retention candidate Version and its exact, owned Document (empty for metadata-only types).</summary>
public sealed record RegistryVersionCandidate(string VersionId, RegistryJson Metadata, RegistryDocument Document,
    Uri? ExternalDocument);

/// <summary>An immutable, budgeted Resource validation request. Eligible Versions are supplied before retention pruning.</summary>
/// <remarks>Domain rules may prohibit format inspection of specific artifacts; OpenUSD Opaque Versions are excluded before invoking the collaborator.</remarks>
public sealed record RegistryResourceValidationContext(RegistryResourceDefinition Resource, RegistryGroupDefinition Group,
    RegistryJson GroupMetadata, RegistryJson Meta, IReadOnlyList<RegistryVersionCandidate> Versions, RegistryLimits Limits)
{
    /// <summary>Gets enabled format choices, or null when the caller imposes no capability restriction.</summary>
    public IReadOnlySet<string>? EnabledFormats { get; init; }
    /// <summary>Gets enabled compatibility choices by format, or null when unrestricted.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>>? EnabledCompatibilities { get; init; }
}

/// <summary>An optional format/compatibility implementation. Core metadata and lifecycle validation remain in the engine.</summary>
public interface IRegistryResourceValidator
{
    /// <summary>Gets the truthful supported format identifiers for capability discovery.</summary>
    IReadOnlyList<string> Formats { get; }
    /// <summary>Gets supported compatibility algorithms by format identifier.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> Compatibilities { get; }
    /// <summary>
    /// Checks every supplied Version and returns exactly one outcome per Version ID. Must honor cancellation,
    /// MaxSchemaSteps and document limits. It must not fetch ExternalDocument references implicitly.
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, RegistryVersionValidation>> ValidateAsync(RegistryResourceValidationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>An explicit authorization policy for external Document references. Approval never itself causes fetching.</summary>
public interface IRegistryDocumentReferencePolicy
{
    /// <summary>Authorizes an absolute resolved reference for this caller or throws a RegistryException. No default policy is installed.</summary>
    ValueTask AuthorizeAsync(ClaimsPrincipal caller, Uri reference, CancellationToken cancellationToken = default);
}

/// <summary>A completed metadata entity with unresolved external obligations from Core.</summary>
public sealed record RegistryMetadataObligationContext(RegistryPath Path, RegistryJson Metadata, RegistryModel Model,
    Uri PublicRoot, ClaimsPrincipal Caller, IReadOnlyList<RegistryValidationObligation> Obligations, RegistryLimits Limits);

/// <summary>A host policy that explicitly discharges URI target and other external metadata obligations.</summary>
public interface IRegistryMetadataObligationValidator
{
    /// <summary>Returns only after every supplied obligation is satisfied; rejects unsupported or invalid obligations explicitly.</summary>
    ValueTask ValidateAsync(RegistryMetadataObligationContext context, CancellationToken cancellationToken = default);
}
