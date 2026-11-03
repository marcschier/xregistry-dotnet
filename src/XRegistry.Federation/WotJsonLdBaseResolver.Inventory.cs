// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>One external dependency and its captured identity, before exact-Version selection.</summary>
public sealed record WotCapturedDependencyOccurrence(WotAuthoredReferenceOccurrence Reference,
    WotCapturedResourceIdentity Target);

/// <summary>A source-derived inventory, not complete projection admission or verified publication closure.</summary>
public sealed class WotCapturedReferenceInventory
{
    internal WotCapturedReferenceInventory(List<WotCapturedDependencyOccurrence> dependencies,
        IReadOnlyList<WotProjectionSelectorOccurrence> selectors, List<WotCapturedDependencyOccurrence> descriptions)
    {
        Dependencies = dependencies.AsReadOnly();
        ProjectionSelectors = selectors;
        SchemaDescriptions = descriptions.AsReadOnly();
    }

    /// <summary>Gets external canonical dependencies, excluding local references and ordinary Schema targets.</summary>
    public IReadOnlyList<WotCapturedDependencyOccurrence> Dependencies { get; }
    /// <summary>Gets selectors that reuse their manifest source's eventual binding.</summary>
    public IReadOnlyList<WotProjectionSelectorOccurrence> ProjectionSelectors { get; }
    /// <summary>Gets captured Schema describedby links excluded from WoT closure dependencies.</summary>
    public IReadOnlyList<WotCapturedDependencyOccurrence> SchemaDescriptions { get; }
}

public static partial class WotJsonLdBaseResolver
{
    /// <summary>Combines authored discovery and captured target classification without consulting closure bindings.</summary>
    /// <remarks>
    /// Rejects competing external dependency members at one authored object pointer after local and Schema
    /// exclusions. Projection selectors reuse source-entry dependencies rather than creating duplicate bindings.
    /// Captured Schema describedby links are excluded; source requirements, pins, annotations, cycles,
    /// exact-Version selection and closure completeness remain unverified.
    /// </remarks>
    public static async ValueTask<WotCapturedReferenceInventory> DiscoverCapturedReferenceInventoryAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts, WotCapturedUriIdentityIndex capturedIdentities,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        ArgumentNullException.ThrowIfNull(capturedIdentities);
        if (!Enum.IsDefined(sourceKind)) { throw new ArgumentOutOfRangeException(nameof(sourceKind)); }
        cancellationToken.ThrowIfCancellationRequested();
        var readBudget = budget ?? new();
        var contexts = ContextSnapshot(CaptureContextDocuments(capturedContexts, readBudget, cancellationToken));
        var references = await DiscoverOrdinaryReferencesAsync(document, documentUri, contexts,
            readBudget, cancellationToken).ConfigureAwait(false);
        var links = await DiscoverLinkReferencesAsync(document, documentUri, sourceKind, contexts,
            readBudget, cancellationToken).ConfigureAwait(false);
        var sources = await DiscoverProjectionSourceReferencesAsync(document, documentUri, sourceKind, contexts,
            readBudget, cancellationToken).ConfigureAwait(false);
        var selectors = await AssociateProjectionSelectorsAsync(document, documentUri, contexts, sources,
            references, readBudget, cancellationToken).ConfigureAwait(false);
        var selectorKeys = new HashSet<(string Pointer, string Property)>();
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            selectorKeys.Add((selector.Reference.SourcePointer, selector.Reference.PropertyName));
        }
        var pointers = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new List<WotCapturedDependencyOccurrence>();
        var descriptions = new List<WotCapturedDependencyOccurrence>();

        void Add(WotAuthoredReferenceOccurrence reference, WotCapturedResourceIdentity? target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (target is null) { return; }
            if (!pointers.Add(reference.SourcePointer))
            {
                throw FederationJson.Invalid("One authored object contains competing external dependency occurrences.");
            }
            dependencies.Add(new(reference, target));
        }

        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (selectorKeys.Contains((reference.SourcePointer, reference.PropertyName))) { continue; }
            var target = capturedIdentities.ClassifyOrdinaryReference(reference, readBudget, cancellationToken);
            if (target?.ResourceKind == "schema") { continue; }
            Add(reference, target);
        }
        foreach (var reference in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (reference.Operator == "describedby")
            {
                var schema = capturedIdentities.ClassifySchemaDescription(reference, readBudget, cancellationToken);
                if (schema is not null) { descriptions.Add(new(reference, schema)); }
                continue;
            }
            Add(reference, capturedIdentities.ClassifyDependencyLink(reference, sourceKind, readBudget, cancellationToken));
        }
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            Add(source.Reference, capturedIdentities.ClassifyProjectionSource(source, readBudget, cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(dependencies, selectors, descriptions);
    }
}
