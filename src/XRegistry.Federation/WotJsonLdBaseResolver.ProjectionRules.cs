// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Newtonsoft.Json.Linq;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>One conjunctive filter; filters on the same source are disjunctive.</summary>
public sealed record WotProjectionSelectionFilter(string? AffordanceKind, IReadOnlyList<string> TypeIris);

/// <summary>An authored source's selection rules, before source acquisition or composition.</summary>
public sealed record WotProjectionSourceSelectionRule(WotProjectionSourceOccurrence Source,
    string Routing, bool SelectAll, string? NamePrefix, IReadOnlyList<WotProjectionSelectionFilter> Filters)
{
    /// <summary>Tests exact expanded class membership without inference or implicit whole-source selection.</summary>
    public bool Matches(string affordanceKind, IReadOnlySet<string> expandedTypes,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expandedTypes);
        if (affordanceKind is not ("property" or "action" or "event"))
        {
            throw new ArgumentException("Expected a property, action or event.", nameof(affordanceKind));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var readBudget = budget ?? new();
        readBudget.ChargeWork();
        if (SelectAll) { return true; }
        var typeSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in expandedTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            typeSet.Add(type);
        }
        foreach (var filter in Filters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (filter.AffordanceKind is not null && filter.AffordanceKind != affordanceKind) { continue; }
            var matches = true;
            foreach (var type in filter.TypeIris)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (!typeSet.Contains(type))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) { return true; }
        }
        return false;
    }
}

public static partial class WotJsonLdBaseResolver
{
    /// <summary>Reads bounded bulk predicates and routing from a captured projection manifest.</summary>
    /// <remarks>
    /// Expanded type IRIs are exact membership constraints, not subclass queries. This validates the
    /// selection-rule grammar, not source requirements, selector annotations, the complete envelope,
    /// nested plans, routing security or a resolved ordinary document.
    /// </remarks>
    public static async ValueTask<IReadOnlyList<WotProjectionSourceSelectionRule>> ReadProjectionSelectionRulesAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        var readBudget = budget ?? new();
        var contexts = ContextSnapshot(CaptureContextDocuments(capturedContexts, readBudget, cancellationToken));
        var sources = await DiscoverProjectionSourceReferencesAsync(document, documentUri, sourceKind,
            contexts, readBudget, cancellationToken).ConfigureAwait(false);
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        var result = new List<WotProjectionSourceSelectionRule>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            var pointer = source.Reference.SourcePointer;
            var entry = ObjectAtPointer(root, pointer, readBudget, cancellationToken);
            var rules = new Dictionary<string, JProperty>(StringComparer.Ordinal);
            foreach (var member in entry.Properties())
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (member.Name.StartsWith('@')) { continue; }
                var iri = await ExpandMemberIriAsync(document, pointer, member.Name, documentUri, contexts,
                    readBudget, cancellationToken).ConfigureAwait(false);
                if (iri != ProjectionVocabulary + "routing" && iri != ProjectionVocabulary + "namePrefix" &&
                    iri != ProjectionVocabulary + "selectAll" && iri != ProjectionVocabulary + "select") { continue; }
                if (!rules.TryAdd(iri, member))
                {
                    throw FederationJson.Invalid("A projection source has competing selection-rule aliases.");
                }
            }
            var routing = "source";
            if (rules.TryGetValue(ProjectionVocabulary + "routing", out var routingMember))
            {
                if (routingMember.Value is not JValue { Type: JTokenType.String } route ||
                    route.Value<string>() is not ("source" or "projection"))
                {
                    throw FederationJson.Invalid("Projection routing must be source or projection.");
                }
                routing = route.Value<string>()!;
            }
            string? prefix = null;
            if (rules.TryGetValue(ProjectionVocabulary + "namePrefix", out var prefixMember))
            {
                if (prefixMember.Value is not JValue { Type: JTokenType.String } prefixValue)
                {
                    throw FederationJson.Invalid("A projection bulk namePrefix must be a string.");
                }
                prefix = prefixValue.Value<string>();
            }
            var selectAll = false;
            if (rules.TryGetValue(ProjectionVocabulary + "selectAll", out var allMember))
            {
                if (allMember.Value is not JValue { Type: JTokenType.Boolean } allValue)
                {
                    throw FederationJson.Invalid("Projection selectAll must be a boolean.");
                }
                selectAll = allValue.Value<bool>();
            }
            var filters = new List<WotProjectionSelectionFilter>();
            if (rules.TryGetValue(ProjectionVocabulary + "select", out var selectMember))
            {
                if (selectMember.Value is not JArray values)
                {
                    throw FederationJson.Invalid("Projection select must be an array of closed filter objects.");
                }
                for (var index = 0; index < values.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    readBudget.ChargeWork();
                    if (values[index] is not JObject filter)
                    {
                        throw FederationJson.Invalid("A projection selection filter must be an object.");
                    }
                    var filterPointer = pointer + "/" + EscapeProjectionPointer(selectMember.Name) + "/" +
                        index.ToString(CultureInfo.InvariantCulture);
                    string? category = null;
                    JProperty? types = null;
                    foreach (var member in filter.Properties())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        readBudget.ChargeWork();
                        if (member.Name == "@type")
                        {
                            if (types is not null)
                            {
                                throw FederationJson.Invalid("A projection filter cannot repeat its type constraint.");
                            }
                            types = member;
                            continue;
                        }
                        if (member.Name.StartsWith('@'))
                        {
                            throw FederationJson.Invalid("A projection filter admits only affordanceKind and @type.");
                        }
                        var iri = await ExpandMemberIriAsync(document, filterPointer, member.Name, documentUri,
                            contexts, readBudget, cancellationToken).ConfigureAwait(false);
                        if (iri == "@type")
                        {
                            if (types is not null)
                            {
                                throw FederationJson.Invalid("A projection filter cannot repeat its type constraint.");
                            }
                            types = member;
                            continue;
                        }
                        if (iri != ProjectionVocabulary + "affordanceKind" || category is not null ||
                            member.Value is not JValue { Type: JTokenType.String } kind ||
                            kind.Value<string>() is not ("property" or "action" or "event"))
                        {
                            throw FederationJson.Invalid("A projection filter admits one bounded affordanceKind and @type.");
                        }
                        category = kind.Value<string>();
                    }
                    IReadOnlyList<string> typeIris = Array.Empty<string>();
                    if (types is not null)
                    {
                        if (types.Value.Type != JTokenType.String &&
                            (types.Value is not JArray typeValues ||
                             typeValues.Any(value => value.Type != JTokenType.String)))
                        {
                            throw FederationJson.Invalid("A projection type filter must be a string or an array of strings.");
                        }
                        var contextDocuments = CaptureContextDocuments(contexts, readBudget, cancellationToken);
                        var documentBase = FederationJson.AbsoluteUri(document.Base ?? documentUri.AbsoluteUri);
                        var loaded = new Dictionary<string, JToken>(StringComparer.Ordinal);
                        var baseline = ExpandCapturedDocument(root, documentBase, contextDocuments, loaded,
                            readBudget, cancellationToken);
                        var expanded = ExpandProjectionObject(root, filterPointer, baseline, documentBase,
                            contextDocuments, loaded, readBudget, cancellationToken);
                        var expandedTypes = expanded["@type"] as JArray;
                        var names = new List<string>();
                        foreach (var value in expandedTypes ?? [])
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            readBudget.ChargeWork();
                            if (value is not JValue { Type: JTokenType.String, Value: string iri })
                            {
                                throw FederationJson.Invalid("A projection type predicate did not expand to an IRI.");
                            }
                            _ = FederationJson.AbsoluteUri(iri);
                            names.Add(iri);
                        }
                        if (types.Value is JValue && names.Count != 1 ||
                            types.Value is JArray { Count: > 0 } && names.Count == 0)
                        {
                            throw FederationJson.Invalid("A projection type predicate has no expanded IRI.");
                        }
                        typeIris = names.AsReadOnly();
                    }
                    filters.Add(new(category, typeIris));
                }
            }
            result.Add(new(source, routing, selectAll, prefix, filters.AsReadOnly()));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result.AsReadOnly();
    }

    private static string EscapeProjectionPointer(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static IReadOnlyDictionary<Uri, FederationDocument> SnapshotProjectionContexts(
        IReadOnlyDictionary<Uri, FederationDocument> contexts, FederationReadBudget budget, CancellationToken token) =>
        ContextSnapshot(CaptureContextDocuments(contexts, budget, token));

    internal static async ValueTask<HashSet<string>> ReadProjectionObjectTypesAsync(FederationDocument document,
        string pointer, Uri documentUri, IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget budget, CancellationToken token)
    {
        var root = await ParseDocumentAsync(document, budget, token).ConfigureAwait(false);
        var contexts = CaptureContextDocuments(capturedContexts, budget, token);
        var loaded = new Dictionary<string, JToken>(StringComparer.Ordinal);
        var documentBase = FederationJson.AbsoluteUri(document.Base ?? documentUri.AbsoluteUri);
        var baseline = ExpandCapturedDocument(root, documentBase, contexts, loaded, budget, token);
        var expanded = ExpandProjectionObject(root, pointer, baseline, documentBase, contexts, loaded, budget, token);
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in expanded["@type"] as JArray ?? [])
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (value is not JValue { Type: JTokenType.String, Value: string iri })
            {
                throw FederationJson.Invalid("A projection type did not expand to an IRI.");
            }
            _ = FederationJson.AbsoluteUri(iri);
            types.Add(iri);
        }
        return types;
    }
}
