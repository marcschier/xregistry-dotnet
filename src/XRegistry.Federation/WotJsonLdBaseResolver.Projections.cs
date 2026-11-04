// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>One ordered projection source entry, before target selection or requirement validation.</summary>
public sealed record WotProjectionSourceOccurrence(WotAuthoredReferenceOccurrence Reference,
    string SourceName, WotReferenceTargetKind TargetKind);

/// <summary>An authored selector associated with its manifest entry, before target-affordance validation.</summary>
public sealed record WotProjectionSelectorOccurrence(WotAuthoredReferenceOccurrence Reference,
    WotProjectionSourceOccurrence Source, string TargetPointer);

/// <summary>Validated immutable facts about a marked projection-plan envelope.</summary>
public sealed record WotProjectionPlanEnvelope(WotReferenceTargetKind Kind, string ScenarioIri);

/// <summary>A permitted selector annotation retained with its authored key and value.</summary>
public sealed record WotProjectionSelectorAnnotation(string MemberName, string Iri, JsonElement Value)
{
    /// <summary>The plan's prefix namespace for a vendor annotation, or null for a standard annotation.</summary>
    public string? VendorPrefixIri { get; init; }
}

public static partial class WotJsonLdBaseResolver
{
    /// <summary>The selected experimental projection vocabulary; earlier placeholder IRIs are not aliases.</summary>
    public const string ProjectionVocabulary =
        "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#";

    /// <summary>Validates the marked envelope's absolute identity, scenario and single ordinary root kind.</summary>
    public static async ValueTask<WotProjectionPlanEnvelope> ReadProjectionPlanEnvelopeAsync(
        FederationDocument document, Uri documentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        var readBudget = budget ?? new();
        var contexts = CaptureContextDocuments(capturedContexts, readBudget, cancellationToken);
        var contextSnapshot = ContextSnapshot(contexts);
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        var documentBase = FederationJson.AbsoluteUri(document.Base ?? documentUri.AbsoluteUri);
        _ = await ReadAbsoluteRootIriAsync(document, root, "@id", documentUri, contextSnapshot, readBudget,
            cancellationToken).ConfigureAwait(false);
        var scenarioIri = await ReadAbsoluteRootIriAsync(document, root, ProjectionVocabulary + "scenario",
            documentUri, contextSnapshot, readBudget, cancellationToken).ConfigureAwait(false);
        var loaded = new Dictionary<string, JToken>(StringComparer.Ordinal);
        var baseline = ExpandCapturedDocument(root, documentBase, contexts, loaded, readBudget, cancellationToken);
        var expandedRoot = ExpandProjectionRoot(root, baseline, documentBase, contexts, loaded,
            readBudget, cancellationToken);
        var envelope = ValidateProjectionEnvelope(expandedRoot);
        if (!string.Equals(envelope.ScenarioIri, scenarioIri, StringComparison.Ordinal))
        {
            throw FederationJson.Invalid("A projection scenario must retain its authored absolute IRI.");
        }
        return envelope;
    }

    /// <summary>Reads permitted annotations and checks that forms and security follow projection routing.</summary>
    public static async ValueTask<IReadOnlyList<WotProjectionSelectorAnnotation>> ReadProjectionSelectorAnnotationsAsync(
        FederationDocument document, Uri documentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        WotProjectionSelectorOccurrence selector, string routing,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        ArgumentNullException.ThrowIfNull(selector);
        if (routing is not ("source" or "projection"))
        {
            throw new ArgumentOutOfRangeException(nameof(routing));
        }

        var readBudget = budget ?? new();
        var contexts = ContextSnapshot(CaptureContextDocuments(capturedContexts, readBudget, cancellationToken));
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        var occurrence = ObjectAtPointer(root, selector.Reference.SourcePointer, readBudget, cancellationToken);
        var annotations = new List<WotProjectionSelectorAnnotation>();
        var meanings = new HashSet<string>(StringComparer.Ordinal);
        var forms = 0;
        var security = 0;
        foreach (var member in occurrence.Properties().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (member.Name == selector.Reference.PropertyName) { continue; }
            string? vendorPrefixIri = null;
            var iri = member.Name == "@type" ? "@type" :
                member.Name.StartsWith('@')
                    ? throw FederationJson.Invalid("A projection selector cannot contain other JSON-LD keywords.")
                    : await ExpandMemberIriAsync(document, selector.Reference.SourcePointer, member.Name,
                        documentUri, contexts, readBudget, cancellationToken).ConfigureAwait(false);
            if (iri is null || !meanings.Add(iri))
            {
                throw FederationJson.Invalid("A projection selector annotation must have one unique expanded meaning.");
            }

            if (iri == "@type")
            {
                if (member.Value.Type != JTokenType.String &&
                    (member.Value is not JArray { Count: > 0 } types ||
                     types.Any(type => type.Type != JTokenType.String)))
                {
                    throw FederationJson.Invalid("A projection selector @type annotation must be an IRI string or nonempty array of strings.");
                }
                var expandedTypes = await ReadProjectionObjectTypesAsync(document,
                    selector.Reference.SourcePointer, documentUri, contexts, readBudget, cancellationToken)
                    .ConfigureAwait(false);
                if (expandedTypes.Count == 0)
                {
                    throw FederationJson.Invalid("A projection selector @type annotation must expand to at least one IRI.");
                }
            }
            else if (IsProjectionStandardAnnotation(iri))
            {
                if (iri == "https://www.w3.org/2019/wot/td#hasForm") { forms++; }
                if (iri == "https://www.w3.org/2019/wot/td#hasSecurityConfiguration") { security++; }
            }
            else if (IsProjectionVendorAnnotation(member.Name, iri))
            {
                var separator = member.Name.IndexOf(':');
                vendorPrefixIri = await ReadProjectionCompactPrefixBindingAsync(root, occurrence,
                    selector.Reference.SourcePointer, member.Name[..separator], document, documentUri, contexts,
                    readBudget, cancellationToken).ConfigureAwait(false);
                if (vendorPrefixIri is null)
                {
                    throw FederationJson.Invalid("A projection vendor annotation requires a bound compact-IRI prefix.");
                }
                _ = FederationJson.AbsoluteUri(iri);
            }
            else
            {
                throw FederationJson.Invalid("A projection selector cannot override a standardized TD, TM or projection member.");
            }

            using var value = JsonDocument.Parse(member.Value.ToString(Formatting.None));
            annotations.Add(new(member.Name, iri, value.RootElement.Clone()) { VendorPrefixIri = vendorPrefixIri });
        }

        if (forms > 1 || security > 1 ||
            routing == "source" && (forms != 0 || security != 0) ||
            routing == "projection" && (forms != 1 || security != 1))
        {
            throw FederationJson.Invalid(routing == "source"
                ? "Source-routed projection selectors cannot override forms or security."
                : "Projection-routed selectors must provide exactly one forms and security annotation.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return annotations.AsReadOnly();
    }

    private static bool IsProjectionStandardAnnotation(string iri) =>
        iri is "https://www.w3.org/2019/wot/td#title" or
            "https://www.w3.org/2019/wot/td#titles" or
            "https://www.w3.org/2019/wot/td#description" or
            "https://www.w3.org/2019/wot/td#descriptions" or
            "https://www.w3.org/2019/wot/td#hasForm" or
            "https://www.w3.org/2019/wot/td#hasSecurityConfiguration";

    private static bool IsProjectionVendorAnnotation(string memberName, string iri)
    {
        var separator = memberName.IndexOf(':');
        return separator > 0 && separator < memberName.Length - 1 &&
            !iri.StartsWith("https://www.w3.org/2019/wot/td#", StringComparison.Ordinal) &&
            !iri.StartsWith("https://www.w3.org/2019/wot/tm#", StringComparison.Ordinal) &&
            !iri.StartsWith(ProjectionVocabulary, StringComparison.Ordinal);
    }

    internal static async ValueTask<string?> ReadProjectionCompactPrefixBindingAsync(FederationDocument document,
        string pointer, string prefix, Uri documentUri, IReadOnlyDictionary<Uri, FederationDocument> contexts,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        var root = await ParseDocumentAsync(document, budget, cancellationToken).ConfigureAwait(false);
        var occurrence = ObjectAtPointer(root, pointer, budget, cancellationToken);
        return await ReadProjectionCompactPrefixBindingAsync(root, occurrence, pointer, prefix, document,
            documentUri, contexts, budget, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string?> ReadProjectionCompactPrefixBindingAsync(JObject root, JObject occurrence,
        string pointer, string prefix, FederationDocument document, Uri documentUri,
        IReadOnlyDictionary<Uri, FederationDocument> contexts, FederationReadBudget budget,
        CancellationToken cancellationToken)
    {
        var contextDocuments = CaptureContextDocuments(contexts, budget, cancellationToken);
        var loaded = new Dictionary<string, JToken>(StringComparer.Ordinal);
        var documentBase = FederationJson.AbsoluteUri(document.Base ?? documentUri.AbsoluteUri);
        var baseline = ExpandCapturedDocument(root, documentBase, contextDocuments, loaded, budget,
            cancellationToken);
        var prefixAlreadyAuthored = occurrence.Property(prefix, StringComparison.Ordinal) is not null;
        var prefixProbeValue = "urn:xregistry:projection-prefix:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pointer + "\n" + prefix)))
                .ToLowerInvariant();
        while (ContainsProjectionProbeValue(root, prefixProbeValue, budget, cancellationToken) ||
            ContainsProjectionProbeValue(root, prefixProbeValue + ":compact", budget, cancellationToken) ||
            ContainsProjectionProbeValue(baseline, prefixProbeValue, budget, cancellationToken) ||
            ContainsProjectionProbeValue(baseline, prefixProbeValue + ":compact", budget, cancellationToken) ||
            loaded.Values.Any(context => ContainsProjectionProbeValue(context, prefixProbeValue, budget, cancellationToken) ||
                ContainsProjectionProbeValue(context, prefixProbeValue + ":compact", budget, cancellationToken)))
        {
            prefixProbeValue += ":probe";
        }
        var compactProbeValue = prefixProbeValue + ":compact";
        var prefixProbeName = prefix + ":__xregistry_prefix_probe__";
        for (var suffix = 0; ContainsProperty(root, prefixProbeName, budget, cancellationToken) ||
            ContainsProperty(baseline, prefixProbeName, budget, cancellationToken) ||
            loaded.Values.Any(context => ContainsProperty(context, prefixProbeName, budget, cancellationToken)); suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            prefixProbeName = prefix + ":__xregistry_prefix_probe_" +
                suffix.ToString(CultureInfo.InvariantCulture) + "__";
        }

        if (!prefixAlreadyAuthored) { occurrence[prefix] = prefixProbeValue; }
        occurrence[prefixProbeName] = compactProbeValue;
        try
        {
            var probed = ExpandCapturedDocument(root, documentBase, contextDocuments, loaded, budget,
                cancellationToken);
            var expanded = ExpandProjectionObject(root, pointer, probed, documentBase, contextDocuments, loaded,
                budget, cancellationToken);
            var prefixIri = prefixAlreadyAuthored
                ? await ExpandMemberIriAsync(document, pointer, prefix, documentUri, contexts, budget,
                    cancellationToken).ConfigureAwait(false)
                : ExpandedProbeProperty(expanded, prefixProbeValue, budget, cancellationToken);
            var compactIri = ExpandedProbeProperty(expanded, compactProbeValue, budget, cancellationToken);
            var suffix = prefixProbeName[(prefix.Length + 1)..];
            if (prefixIri is null || !string.Equals(compactIri, prefixIri + suffix, StringComparison.Ordinal))
            {
                return null;
            }
            _ = FederationJson.AbsoluteUri(prefixIri);
            if (!string.Equals(prefixIri, prefix + ":", StringComparison.Ordinal) ||
                ProbeAmbiguousProjectionPrefix(root, pointer, prefix, probed, documentBase, contextDocuments,
                    loaded, budget, cancellationToken))
            {
                return prefixIri;
            }
            return null;
        }
        finally
        {
            occurrence.Property(prefixProbeName, StringComparison.Ordinal)?.Remove();
            if (!prefixAlreadyAuthored) { occurrence.Property(prefix, StringComparison.Ordinal)?.Remove(); }
        }
    }

    private static bool ProbeAmbiguousProjectionPrefix(JObject root, string pointer, string prefix,
        JArray baseline, Uri documentBase,
        Dictionary<string, FederationDocument> contexts, Dictionary<string, JToken> loaded,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pointer + "\n" + prefix)))
            .ToLowerInvariant();
        var markerTerm = "__xregistry_prefix_scope_" + digest + "__";
        while (ContainsProperty(root, markerTerm, budget, cancellationToken) ||
            ContainsProjectionProbeValue(root, markerTerm, budget, cancellationToken) ||
            ContainsProperty(baseline, markerTerm, budget, cancellationToken) ||
            ContainsProjectionProbeValue(baseline, markerTerm, budget, cancellationToken) ||
            loaded.Values.Any(context => ContainsProperty(context, markerTerm, budget, cancellationToken) ||
                ContainsProjectionProbeValue(context, markerTerm, budget, cancellationToken)))
        {
            markerTerm += "_";
        }
        var markerValue = "urn:xregistry:prefix-scope:" + digest;
        while (ContainsProjectionProbeValue(root, markerValue, budget, cancellationToken) ||
            ContainsProjectionProbeValue(baseline, markerValue, budget, cancellationToken) ||
            loaded.Values.Any(context => ContainsProjectionProbeValue(context, markerValue, budget, cancellationToken)))
        {
            markerValue += ":probe";
        }
        var markerBase = "https://xregistry.invalid/.well-known/jsonld-probe/prefix-scope/" + digest + "/";

        budget.CheckResultBytes(Encoding.UTF8.GetByteCount(root.ToString(Formatting.None)));
        var probeRoot = (JObject)root.DeepClone();
        ObjectAtPointer(probeRoot, pointer, budget, cancellationToken)[markerTerm] = markerValue;
        void Mark(JToken context)
        {
            if (ContainsProperty(context, markerTerm, budget, cancellationToken) ||
                ContainsProjectionProbeValue(context, markerTerm, budget, cancellationToken) ||
                ContainsProjectionProbeValue(context, markerValue, budget, cancellationToken))
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "A captured context conflicts with a projection prefix-scope probe.");
            }
            MarkProjectionPrefixContexts(context, prefix, markerTerm, markerBase, budget, cancellationToken);
        }
        MarkProjectionPrefixContexts(probeRoot, prefix, markerTerm, markerBase, budget, cancellationToken);
        budget.CheckResultBytes(Encoding.UTF8.GetByteCount(probeRoot.ToString(Formatting.None)));
        try
        {
            // A scope marker follows prefix definitions through the processor without rebinding them.
            var marked = ExpandCapturedDocument(probeRoot, documentBase, contexts, loaded, budget,
                cancellationToken, Mark);
            var expanded = ExpandProjectionObject(probeRoot, pointer, marked, documentBase, contexts, loaded,
                budget, cancellationToken, Mark);
            var scope = ExpandedProbeProperty(expanded, markerValue, budget, cancellationToken);
            if (string.Equals(scope, markerBase + "bound", StringComparison.Ordinal)) { return true; }
            if (string.Equals(scope, markerBase + "unbound", StringComparison.Ordinal)) { return false; }
        }
        catch (FederationException exception) when (exception.Code == FederationErrorCode.InvalidPackage)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The effective JSON-LD prefix flag could not be inspected without changing its context.",
                innerException: exception);
        }
        throw new FederationException(FederationErrorCode.UnsupportedOperation,
            "The effective JSON-LD prefix flag could not be determined.");
    }

    private static void MarkProjectionPrefixContexts(JToken token, string prefix, string markerTerm,
        string markerBase, FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties().ToArray())
            {
                if (property.Name == "@context")
                {
                    MarkProjectionPrefixContext(property.Value, prefix, markerTerm, markerBase, budget,
                        cancellationToken);
                }
                else
                {
                    MarkProjectionPrefixContexts(property.Value, prefix, markerTerm, markerBase, budget,
                        cancellationToken);
                }
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
            {
                MarkProjectionPrefixContexts(item, prefix, markerTerm, markerBase, budget, cancellationToken);
            }
        }
    }

    private static void MarkProjectionPrefixContext(JToken context, string prefix, string markerTerm,
        string markerBase, FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (context is JObject obj)
        {
            var definition = obj.Property(prefix, StringComparison.Ordinal);
            if (definition is not null)
            {
                if (obj.Property(markerTerm, StringComparison.Ordinal) is not null)
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation,
                        "A captured context conflicts with a projection prefix-scope probe.");
                }
                var bound = definition.Value switch
                {
                    JValue { Type: JTokenType.String } => !prefix.Contains('/'),
                    JObject term => term["@prefix"] is JValue { Type: JTokenType.Boolean } flag &&
                        flag.Value<bool>(),
                    _ => false
                };
                obj.Add(markerTerm, new JObject
                {
                    ["@id"] = markerBase + (bound ? "bound" : "unbound"),
                    ["@protected"] = false
                });
            }
            MarkProjectionPrefixContexts(obj, prefix, markerTerm, markerBase, budget, cancellationToken);
        }
        else if (context is JArray array)
        {
            foreach (var item in array)
            {
                MarkProjectionPrefixContext(item, prefix, markerTerm, markerBase, budget, cancellationToken);
            }
        }
    }

    private static string? ExpandedProbeProperty(JObject expanded, string expectedValue,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        var matches = expanded.Properties().Where(property =>
            ContainsProjectionProbeValue(property.Value, expectedValue, budget, cancellationToken)).ToArray();
        return matches.Length == 1 ? matches[0].Name : null;
    }

    private static bool ContainsProjectionProbeValue(JToken token, string expectedValue,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (token is JValue value)
        {
            return value.Type == JTokenType.String &&
                string.Equals(value.Value<string>(), expectedValue, StringComparison.Ordinal);
        }
        return token is JContainer container &&
            container.Children().Any(child =>
                ContainsProjectionProbeValue(child, expectedValue, budget, cancellationToken));
    }

    /// <summary>Discovers ordered source entries only when the root has the selected expanded projection marker.</summary>
    /// <remarks>
    /// This validates root-kind and manifest shape, not complete projection admission, target metadata,
    /// version requirements, digest pins or affordance selection. It never acquires source documents.
    /// </remarks>
    public static async ValueTask<IReadOnlyList<WotProjectionSourceOccurrence>> DiscoverProjectionSourceReferencesAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        if (!Enum.IsDefined(sourceKind)) { throw new ArgumentOutOfRangeException(nameof(sourceKind)); }
        var readBudget = budget ?? new();
        var contextDocuments = CaptureContextDocuments(capturedContexts, readBudget, cancellationToken);
        var contexts = ContextSnapshot(contextDocuments);
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        var documentBase = FederationJson.AbsoluteUri(document.Base ?? documentUri.AbsoluteUri);
        var loaded = new Dictionary<string, JToken>(StringComparer.Ordinal);
        var baseline = ExpandCapturedDocument(root, documentBase, contextDocuments, loaded, readBudget, cancellationToken);
        var expandedRoot = ExpandProjectionRoot(root, baseline, documentBase, contextDocuments, loaded,
            readBudget, cancellationToken);
        var types = expandedRoot["@type"] is JArray rootTypes
            ? rootTypes.Values<string>().ToHashSet(StringComparer.Ordinal) : [];
        if (!types.Contains(ProjectionVocabulary + "projection"))
        {
            return Array.Empty<WotProjectionSourceOccurrence>();
        }
        var thing = types.Contains("https://www.w3.org/2019/wot/td#Thing");
        var model = types.Contains("https://www.w3.org/2019/wot/tm#ThingModel");
        if (thing == model || thing != (sourceKind == WotReferenceTargetKind.ThingDescription))
        {
            throw FederationJson.Invalid("A projection root must declare exactly its captured TD or TM kind.");
        }

        JProperty? projects = null;
        foreach (var member in root.Properties())
        {
            if (member.Name.StartsWith('@')) { continue; }
            var iri = await ExpandMemberIriAsync(document, "", member.Name, documentUri,
                contexts, readBudget, cancellationToken).ConfigureAwait(false);
            if (iri == ProjectionVocabulary + "projectionKind")
            {
                throw FederationJson.Invalid("A projection root cannot declare a separate projectionKind.");
            }
            if (iri != ProjectionVocabulary + "projects") { continue; }
            if (projects is not null)
            {
                throw FederationJson.Invalid("A projection root cannot declare competing source manifest members.");
            }
            projects = member;
        }
        if (projects?.Value is not JArray { Count: > 0 } entries)
        {
            throw FederationJson.Invalid("A projection requires a nonempty authored source manifest array.");
        }
        var pointer = "/" + projects.Name.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var uris = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new List<WotProjectionSourceOccurrence>();
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (entries[index] is not JObject entry)
            {
                throw FederationJson.Invalid("A projection source manifest must contain source objects.");
            }
            var entryPointer = pointer + "/" + index.ToString(CultureInfo.InvariantCulture);
            JProperty? sourceName = null;
            foreach (var member in entry.Properties())
            {
                if (member.Name.StartsWith('@')) { continue; }
                var iri = await ExpandMemberIriAsync(document, entryPointer, member.Name, documentUri,
                    contexts, readBudget, cancellationToken).ConfigureAwait(false);
                if (iri != ProjectionVocabulary + "sourceName") { continue; }
                if (sourceName is not null)
                {
                    throw FederationJson.Invalid("A projection source has competing sourceName members.");
                }
                sourceName = member;
            }
            if (sourceName?.Value is not JValue { Type: JTokenType.String } alias ||
                string.IsNullOrEmpty(alias.Value<string>()) || !names.Add(alias.Value<string>()!))
            {
                throw FederationJson.Invalid("Projection source names must be nonempty and unique.");
            }
            if (entry["href"] is not JValue { Type: JTokenType.String } hrefValue)
            {
                throw FederationJson.Invalid("A projection source must have an authored href string.");
            }
            WotReferenceTargetKind? targetKind = null;
            if (entry["type"] is JValue { Type: JTokenType.String } mediaType &&
                MediaTypeHeaderValue.TryParse(mediaType.Value<string>(), out var parsedType))
            {
                if (string.Equals(parsedType.MediaType, "application/td+json", StringComparison.OrdinalIgnoreCase))
                {
                    targetKind = WotReferenceTargetKind.ThingDescription;
                }
                else if (string.Equals(parsedType.MediaType, "application/tm+json", StringComparison.OrdinalIgnoreCase))
                {
                    targetKind = WotReferenceTargetKind.ThingModel;
                }
            }
            if (targetKind is null)
            {
                throw FederationJson.Invalid("A projection source must declare its expected TD or TM media type.");
            }
            var href = hrefValue.Value<string>()!;
            var target = await ResolveReferenceUriAsync(document, entryPointer, "href", href, documentUri,
                contexts, readBudget, cancellationToken).ConfigureAwait(false);
            if (target.Fragment is not null || !uris.Add(target.DocumentUri.AbsoluteUri))
            {
                throw FederationJson.Invalid("Projection source URIs must be fragment-free and unique.");
            }
            occurrences.Add(new(new(entryPointer, "href", ProjectionVocabulary + "projects", href, target),
                alias.Value<string>()!, targetKind.Value));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return occurrences.AsReadOnly();
    }

    private static async ValueTask<string> ReadAbsoluteRootIriAsync(FederationDocument document, JObject root,
        string semanticIri, Uri documentUri, IReadOnlyDictionary<Uri, FederationDocument> contexts,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        JToken? value = null;
        if (semanticIri == "@id" && root.Property("@id", StringComparison.Ordinal) is { } keywordId)
        {
            value = keywordId.Value;
        }
        foreach (var member in root.Properties())
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (member.Name.StartsWith('@')) { continue; }
            var iri = await ExpandMemberIriAsync(document, "", member.Name, documentUri, contexts, budget,
                cancellationToken).ConfigureAwait(false);
            if (iri != semanticIri) { continue; }
            if (value is not null)
            {
                throw FederationJson.Invalid("A projection root cannot declare competing identifier or scenario members.");
            }
            value = member.Value;
        }
        if (value is not JValue { Type: JTokenType.String } authored)
        {
            throw FederationJson.Invalid(semanticIri == "@id"
                ? "A projection root must have an absolute id."
                : "A projection root must have exactly one IRI-valued scenario.");
        }
        var valueText = authored.Value<string>()!;
        _ = FederationJson.AbsoluteUri(valueText);
        return valueText;
    }

    private static WotProjectionPlanEnvelope ValidateProjectionEnvelope(JObject expandedRoot)
    {
        var types = expandedRoot["@type"] is JArray rootTypes
            ? rootTypes.Values<string>().ToHashSet(StringComparer.Ordinal) : [];
        if (!types.Contains(ProjectionVocabulary + "projection"))
        {
            throw FederationJson.Invalid("The root is not marked as a projection plan.");
        }
        var thing = types.Contains("https://www.w3.org/2019/wot/td#Thing");
        var model = types.Contains("https://www.w3.org/2019/wot/tm#ThingModel");
        if (thing == model)
        {
            throw FederationJson.Invalid("A projection root must declare exactly one ordinary TD or TM kind.");
        }
        if (expandedRoot["@id"] is not JValue { Type: JTokenType.String } id)
        {
            throw FederationJson.Invalid("A projection root must have an absolute id.");
        }
        _ = FederationJson.AbsoluteUri(id.Value<string>()!);
        if (expandedRoot[ProjectionVocabulary + "scenario"] is not JArray scenarioValues ||
            scenarioValues.Count != 1 || scenarioValues[0] is not JObject scenarioNode ||
            scenarioNode["@id"] is not JValue { Type: JTokenType.String } scenario)
        {
            throw FederationJson.Invalid("A projection root must have exactly one IRI-valued scenario.");
        }
        var scenarioIri = scenario.Value<string>()!;
        _ = FederationJson.AbsoluteUri(scenarioIri);
        return new(thing ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel,
            scenarioIri);
    }

    /// <summary>Associates explicit affordance selectors with their declared source entries without acquiring targets.</summary>
    /// <remarks>
    /// Only authored tm:ref members of root affordance-map entries can be selectors.
    /// Each result uses its source entry's eventual binding rather than creating another dependency.
    /// Actual target kind, affordance existence, annotations, source requirements and composition remain unverified.
    /// </remarks>
    public static async ValueTask<IReadOnlyList<WotProjectionSelectorOccurrence>> DiscoverProjectionSelectorReferencesAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        var readBudget = budget ?? new();
        var sources = await DiscoverProjectionSourceReferencesAsync(document, documentUri, sourceKind,
            capturedContexts, readBudget, cancellationToken).ConfigureAwait(false);
        if (sources.Count == 0) { return Array.Empty<WotProjectionSelectorOccurrence>(); }
        var references = await DiscoverOrdinaryReferencesAsync(document, documentUri, capturedContexts,
            readBudget, cancellationToken).ConfigureAwait(false);
        return await AssociateProjectionSelectorsAsync(document, documentUri, capturedContexts, sources,
            references, readBudget, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<WotProjectionSelectorOccurrence>> AssociateProjectionSelectorsAsync(
        FederationDocument document, Uri documentUri, IReadOnlyDictionary<Uri, FederationDocument> contexts,
        IReadOnlyList<WotProjectionSourceOccurrence> sources, IReadOnlyList<WotAuthoredReferenceOccurrence> references,
        FederationReadBudget readBudget, CancellationToken cancellationToken)
    {
        if (sources.Count == 0) { return Array.Empty<WotProjectionSelectorOccurrence>(); }
        var byUri = sources.ToDictionary(source => source.Reference.Target.DocumentUri.AbsoluteUri,
            StringComparer.Ordinal);
        var categories = new Dictionary<string, string?>(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var selectors = new List<WotProjectionSelectorOccurrence>();
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (reference.Operator != "tm:ref" || reference.Target.Fragment is null) { continue; }
            var authoredPath = reference.SourcePointer.Split('/');
            if (authoredPath.Length != 3) { continue; }
            var member = DecodePointerToken(authoredPath[1]);
            if (!categories.TryGetValue(member, out var category))
            {
                var iri = await ExpandMemberIriAsync(document, "", member, documentUri, contexts,
                    readBudget, cancellationToken).ConfigureAwait(false);
                category = iri switch
                {
                    "https://www.w3.org/2019/wot/td#hasPropertyAffordance" => "properties",
                    "https://www.w3.org/2019/wot/td#hasActionAffordance" => "actions",
                    "https://www.w3.org/2019/wot/td#hasEventAffordance" => "events",
                    _ => null
                };
                categories.Add(member, category);
            }
            if (category is null) { continue; }
            string pointer;
            try
            {
                pointer = WotRegistryResolver.DecodeFragment(reference.Target.Fragment, readBudget,
                    cancellationToken, requirePointer: false);
            }
            catch (RegistryException exception)
            {
                throw FederationJson.FromCore(exception);
            }
            if (pointer.Length == 0 || pointer[0] != '/') { continue; }
            readBudget.CheckDepth(pointer.Count(character => character == '/'));
            var targetPath = pointer[1..].Split('/');
            if (targetPath.Length != 2) { continue; }
            var targetCategory = DecodePointerToken(targetPath[0]);
            if (targetCategory is not ("properties" or "actions" or "events")) { continue; }
            _ = DecodePointerToken(targetPath[1]);
            if (targetCategory != category)
            {
                throw FederationJson.Invalid("A projection selector must target its own affordance category.");
            }
            if (!byUri.TryGetValue(reference.Target.DocumentUri.AbsoluteUri, out var source))
            {
                throw FederationJson.Invalid("A projection selector must identify a declared source document.");
            }
            if (!selected.Add(reference.SourcePointer))
            {
                throw FederationJson.Invalid("One authored affordance cannot contain competing projection selectors.");
            }
            var authored = ObjectAtPointer(root, reference.SourcePointer, readBudget, cancellationToken);
            foreach (var memberValue in authored.Properties())
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (memberValue.Name.StartsWith('@')) { continue; }
                var meaning = await ExpandMemberIriAsync(document, reference.SourcePointer, memberValue.Name,
                    documentUri, contexts, readBudget, cancellationToken).ConfigureAwait(false);
                if (meaning is "https://www.w3.org/2019/wot/tm#minVersion" or
                    "https://www.w3.org/2019/wot/td#minVersion" or
                    "http://opcfoundation.org/UA/WoT-Binding/minVersion")
                {
                    throw new RegistryException(new("invalid-floor", reference.SourcePointer,
                        "A projection source floor belongs on its manifest entry, not an affordance selector."));
                }
                if (meaning == "http://opcfoundation.org/UA/WoT-Binding/compatibilityLine")
                {
                    throw new RegistryException(new("invalid-line", reference.SourcePointer,
                        "A projection source line belongs on its manifest entry, not an affordance selector."));
                }
                if (meaning == ProjectionVocabulary + "sourceDigest")
                {
                    throw FederationJson.Invalid("A projection source digest belongs on its manifest entry, not an affordance selector.");
                }
            }
            selectors.Add(new(reference, source, pointer));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return selectors.AsReadOnly();
    }

    private static JObject ExpandProjectionRoot(JObject root, JArray baseline, Uri documentBase,
        Dictionary<string, FederationDocument> contexts, Dictionary<string, JToken> loaded,
        FederationReadBudget budget, CancellationToken cancellationToken) =>
        ExpandProjectionObject(root, "", baseline, documentBase, contexts, loaded, budget, cancellationToken);

    private static JObject ExpandProjectionObject(JObject root, string pointer, JArray baseline, Uri documentBase,
        Dictionary<string, FederationDocument> contexts, Dictionary<string, JToken> loaded,
        FederationReadBudget budget, CancellationToken cancellationToken, Action<JToken>? transform = null)
    {
        var predicate = "https://xregistry.invalid/.well-known/jsonld-probe/root/" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToString(Formatting.None))))
                .ToLowerInvariant();
        while (ContainsProperty(root, predicate, budget, cancellationToken) ||
            ContainsProperty(baseline, predicate, budget, cancellationToken) ||
            loaded.Values.Any(context => ContainsProperty(context, predicate, budget, cancellationToken)))
        {
            predicate += ":probe";
        }
        var occurrence = ObjectAtPointer(root, pointer, budget, cancellationToken);
        // Mark only the authored object, rather than a node lifted out of another graph or list.
        occurrence[predicate] = "object";
        budget.CheckResultBytes(Encoding.UTF8.GetByteCount(root.ToString(Formatting.None)));
        var expanded = ExpandCapturedDocument(root, documentBase, contexts, loaded, budget, cancellationToken,
            transform);
        occurrence.Remove(predicate);
        JObject? selected = null;
        void Find(JToken token)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (token is JObject node)
            {
                if (node.Property("@value", StringComparison.Ordinal) is not null) { return; }
                if (node.Property(predicate, StringComparison.Ordinal) is not null)
                {
                    if (selected is not null)
                    {
                        throw FederationJson.Invalid("The authored projection object has competing expanded identities.");
                    }
                    selected = node;
                }
                foreach (var member in node.Properties()) { Find(member.Value); }
            }
            else if (token is JArray array)
            {
                foreach (var item in array) { Find(item); }
            }
        }
        Find(expanded);
        return selected ?? throw FederationJson.Invalid("The authored projection object could not be identified.");
    }
}
