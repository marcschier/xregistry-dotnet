// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>One winning definition with its original owner, before annotations, relocation or final validation.</summary>
public sealed record WotProjectionAffordanceSelection(WotResolvedDocument Owner, string SourcePointer,
    string Category, string Name, JsonElement Definition, WotProjectionSourceSelectionRule? Source,
    WotProjectionSelectorOccurrence? Selector, IReadOnlyList<WotProjectionSelectorAnnotation> Annotations);

public static partial class WotRegistryResolver
{
    /// <summary>Acquires constrained captured sources and determines explicit, bulk and local membership.</summary>
    /// <remarks>
    /// This returns a selection inventory, not a resolved TD/TM. Nested source plans are explicitly
    /// unsupported here. Annotations, envelope admission, dependency closure, contexts, forms, security,
    /// provenance serialization and final ordinary validation must precede exposing a resolved view.
    /// </remarks>
    public static async ValueTask<IReadOnlyList<WotProjectionAffordanceSelection>> SelectCapturedProjectionAffordancesAsync(
        WotResolvedDocument containingRevision, Uri documentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts, WotCapturedUriIdentityIndex capturedIdentities,
        bool allowPrerelease = false, FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        ArgumentNullException.ThrowIfNull(capturedIdentities);
        var readBudget = budget ?? new();
        var contexts = WotJsonLdBaseResolver.SnapshotProjectionContexts(capturedContexts, readBudget, cancellationToken);
        var envelope = await WotJsonLdBaseResolver.ReadProjectionPlanEnvelopeAsync(containingRevision.Document,
            documentUri, contexts, readBudget, cancellationToken).ConfigureAwait(false);
        if (envelope.Kind != containingRevision.Kind)
        {
            throw FederationJson.Invalid("A projection root kind must match its captured Registry model kind.");
        }
        var rules = await WotJsonLdBaseResolver.ReadProjectionSelectionRulesAsync(containingRevision.Document,
            documentUri, containingRevision.Kind, contexts, readBudget, cancellationToken).ConfigureAwait(false);
        if (rules.Count == 0)
        {
            throw FederationJson.Invalid("A projection selection inventory requires a marked source manifest.");
        }
        var selectors = await WotJsonLdBaseResolver.DiscoverProjectionSelectorReferencesAsync(
            containingRevision.Document, documentUri, containingRevision.Kind, contexts,
            readBudget, cancellationToken).ConfigureAwait(false);
        var selectorAnnotations = new Dictionary<string, IReadOnlyList<WotProjectionSelectorAnnotation>>(
            StringComparer.Ordinal);
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            WotProjectionSourceSelectionRule? matchingRule = null;
            var matches = 0;
            foreach (var rule in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (rule.Source != selector.Source) { continue; }
                matchingRule = rule;
                matches++;
            }
            if (matches != 1 || matchingRule is null)
            {
                throw FederationJson.Invalid("A projection selector must belong to exactly one source rule.");
            }
            var annotations = await WotJsonLdBaseResolver.ReadProjectionSelectorAnnotationsAsync(containingRevision.Document,
                documentUri, contexts, selector, matchingRule.Routing, readBudget, cancellationToken)
                .ConfigureAwait(false);
            selectorAnnotations.Add(selector.Reference.SourcePointer, annotations);
        }
        var root = await ParseAcquiredDocumentAsync(containingRevision, readBudget, cancellationToken).ConfigureAwait(false);
        var planMaps = await ProjectionMapsAsync(containingRevision, root, documentUri, contexts, readBudget,
            cancellationToken).ConfigureAwait(false);
        var nameComparer = Comparer<string>.Create((left, right) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            return CompareProjectionNames(left, right);
        });
        var rankComparer = Comparer<(int Category, string Name, string Original)>.Create((left, right) =>
        {
            var category = left.Category.CompareTo(right.Category);
            if (category != 0) { return category; }
            var name = nameComparer.Compare(left.Name, right.Name);
            return name != 0 ? name : nameComparer.Compare(left.Original, right.Original);
        });
        var winners = new Dictionary<(string Category, string Name), WotProjectionAffordanceSelection>();
        var selectorPointers = selectors.Select(selector => selector.Reference.SourcePointer)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            var selected = await ResolveCapturedProjectionSourceAsync(containingRevision, rule.Source,
                documentUri, contexts, capturedIdentities, allowPrerelease, readBudget,
                cancellationToken).ConfigureAwait(false);
            var sourceUri = rule.Source.Reference.Target.DocumentUri;
            var nested = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(selected.Document,
                sourceUri, selected.Kind, contexts, readBudget, cancellationToken).ConfigureAwait(false);
            if (nested.Count != 0)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "Nested projection plans must be resolved before source affordance membership is selected.");
            }
            var sourceRoot = await ParseAcquiredDocumentAsync(selected, readBudget, cancellationToken).ConfigureAwait(false);
            var sourceMaps = await ProjectionMapsAsync(selected, sourceRoot, sourceUri, contexts, readBudget,
                cancellationToken).ConfigureAwait(false);
            var explicitSelections = new SortedDictionary<(int Category, string Name, string Original),
                WotProjectionAffordanceSelection>(rankComparer);
            var bulk = new SortedDictionary<(int Category, string Name, string Original),
                WotProjectionAffordanceSelection>(rankComparer);
            foreach (var selector in selectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (selector.Source != rule.Source) { continue; }
                var path = selector.Reference.SourcePointer.Split('/');
                var name = DecodePointerToken(path[2], readBudget, cancellationToken);
                var target = selector.TargetPointer.Split('/');
                var category = target[1];
                var original = DecodePointerToken(target[2], readBudget, cancellationToken);
                // Every selector is checked, even if an earlier source will displace it.
                var definition = SelectProjectionMapAffordance(sourceRoot, category, original, readBudget,
                    cancellationToken);
                explicitSelections.Add((ProjectionCategory(category), name, original),
                    new(selected, selector.TargetPointer, category, name, definition, rule, selector,
                        selectorAnnotations[selector.Reference.SourcePointer]));
            }
            foreach (var (category, map) in sourceMaps)
            {
                foreach (var affordance in map.Value.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    readBudget.ChargeWork();
                    var pointer = "/" + ProjectionPointer(map.Name) + "/" + ProjectionPointer(affordance.Name);
                    var types = rule.SelectAll || rule.Filters.Count == 0
                        ? new HashSet<string>(StringComparer.Ordinal)
                        : await WotJsonLdBaseResolver.ReadProjectionObjectTypesAsync(selected.Document, pointer,
                            sourceUri, contexts, readBudget, cancellationToken).ConfigureAwait(false);
                    var kind = category switch { "properties" => "property", "actions" => "action", _ => "event" };
                    if (!rule.Matches(kind, types, readBudget, cancellationToken)) { continue; }
                    var name = ProjectionBulkName(rule.NamePrefix, affordance.Name);
                    readBudget.CheckResultBytes(Encoding.UTF8.GetByteCount(name));
                    bulk.Add((ProjectionCategory(category), name, affordance.Name),
                        new(selected, pointer, category, name, affordance.Value, rule, null,
                            Array.Empty<WotProjectionSelectorAnnotation>()));
                }
            }
            foreach (var selection in explicitSelections.Values.Concat(bulk.Values))
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                winners.TryAdd((selection.Category, selection.Name), selection);
            }
        }
        foreach (var (category, map) in planMaps)
        {
            foreach (var local in map.Value.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                var pointer = "/" + ProjectionPointer(map.Name) + "/" + ProjectionPointer(local.Name);
                if (selectorPointers.Contains(pointer)) { continue; }
                winners[(category, local.Name)] = new(containingRevision, pointer, category, local.Name,
                    local.Value, null, null, Array.Empty<WotProjectionSelectorAnnotation>());
            }
        }
        var ordered = new SortedDictionary<(int Category, string Name, string Original),
            WotProjectionAffordanceSelection>(rankComparer);
        long resultBytes = 0;
        foreach (var selection in winners.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (selection.Selector is not null)
            {
                var source = selection.Source ??
                    throw new InvalidOperationException("A projection selector must retain its source rule.");
                var sourcePrefixes = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var annotation in selection.Annotations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    readBudget.ChargeWork();
                    if (annotation.VendorPrefixIri is not { } expected) { continue; }
                    var prefix = annotation.MemberName[..annotation.MemberName.IndexOf(':')];
                    if (!sourcePrefixes.TryGetValue(prefix, out var actual))
                    {
                        actual = await WotJsonLdBaseResolver.ReadProjectionCompactPrefixBindingAsync(
                            selection.Owner.Document, selection.SourcePointer, prefix,
                            source.Source.Reference.Target.DocumentUri, contexts, readBudget, cancellationToken)
                            .ConfigureAwait(false);
                        sourcePrefixes.Add(prefix, actual);
                    }
                    if (actual is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
                    {
                        throw FederationJson.Invalid(
                            $"Projection annotation prefix '{prefix}' conflicts with the selected source's context.");
                    }
                }
            }
            resultBytes += (long)Encoding.UTF8.GetByteCount(selection.Definition.GetRawText()) +
                Encoding.UTF8.GetByteCount(selection.Name) + Encoding.UTF8.GetByteCount(selection.SourcePointer);
            foreach (var annotation in selection.Annotations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                resultBytes += (long)Encoding.UTF8.GetByteCount(annotation.MemberName) +
                    Encoding.UTF8.GetByteCount(annotation.Iri) +
                    Encoding.UTF8.GetByteCount(annotation.Value.GetRawText()) +
                    Encoding.UTF8.GetByteCount(annotation.VendorPrefixIri ?? "");
            }
            readBudget.CheckResultBytes(resultBytes);
            ordered.Add((ProjectionCategory(selection.Category), selection.Name, ""), selection);
        }
        if (ordered.Count == 0)
        {
            throw FederationJson.Invalid("A projection plan must resolve at least one imported or local affordance.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ordered.Values.ToList().AsReadOnly();
    }

    private static async ValueTask<Dictionary<string, JsonProperty>> ProjectionMapsAsync(WotResolvedDocument owner,
        JsonElement root, Uri documentUri, IReadOnlyDictionary<Uri, FederationDocument> contexts,
        FederationReadBudget budget, CancellationToken token)
    {
        var maps = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        foreach (var member in root.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (member.Name.StartsWith('@')) { continue; }
            var iri = await WotJsonLdBaseResolver.ExpandMemberIriAsync(owner.Document, "", member.Name,
                documentUri, contexts, budget, token).ConfigureAwait(false);
            var category = iri switch
            {
                "https://www.w3.org/2019/wot/td#hasPropertyAffordance" => "properties",
                "https://www.w3.org/2019/wot/td#hasActionAffordance" => "actions",
                "https://www.w3.org/2019/wot/td#hasEventAffordance" => "events",
                _ => null
            };
            if (category is null) { continue; }
            if (member.Value.ValueKind != JsonValueKind.Object || !maps.TryAdd(category, member))
            {
                throw FederationJson.Invalid("A projection owner must have at most one object-valued map per affordance category.");
            }
            foreach (var affordance in member.Value.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                budget.ChargeWork();
                if (affordance.Value.ValueKind != JsonValueKind.Object)
                {
                    throw FederationJson.Invalid("A projection affordance map must contain object-valued definitions.");
                }
            }
        }
        return maps;
    }

    private static int ProjectionCategory(string category) =>
        category switch { "properties" => 0, "actions" => 1, "events" => 2, _ => throw new ArgumentException("Unknown affordance map.") };

    private static string ProjectionPointer(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string ProjectionBulkName(string? prefix, string name)
    {
        if (prefix is null || name.Length == 0) { return prefix is null ? name : prefix; }
        if (!Rune.TryGetRuneAt(name, 0, out var first))
        {
            throw FederationJson.Invalid("A projection affordance name contains an invalid Unicode code point.");
        }
        return prefix + Rune.ToUpperInvariant(first) + name[first.Utf16SequenceLength..];
    }

    private static int CompareProjectionNames(string left, string right)
    {
        var leftRunes = left.EnumerateRunes();
        var rightRunes = right.EnumerateRunes();
        while (true)
        {
            var hasLeft = leftRunes.MoveNext();
            var hasRight = rightRunes.MoveNext();
            if (!hasLeft || !hasRight) { return hasLeft.CompareTo(hasRight); }
            var comparison = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
            if (comparison != 0) { return comparison; }
        }
    }
}
