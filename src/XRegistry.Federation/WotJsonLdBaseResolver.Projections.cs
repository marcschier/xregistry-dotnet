// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>One ordered projection source entry, before target selection or requirement validation.</summary>
public sealed record WotProjectionSourceOccurrence(WotAuthoredReferenceOccurrence Reference,
    string SourceName, WotReferenceTargetKind TargetKind);

public static partial class WotJsonLdBaseResolver
{
    /// <summary>The selected experimental projection vocabulary; earlier placeholder IRIs are not aliases.</summary>
    public const string ProjectionVocabulary =
        "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#";

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

    private static JObject ExpandProjectionRoot(JObject root, JArray baseline, Uri documentBase,
        Dictionary<string, FederationDocument> contexts, Dictionary<string, JToken> loaded,
        FederationReadBudget budget, CancellationToken cancellationToken)
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
        // A root-only property distinguishes the authored envelope from nodes lifted out of @graph.
        root[predicate] = "root";
        budget.CheckResultBytes(Encoding.UTF8.GetByteCount(root.ToString(Formatting.None)));
        var expanded = ExpandCapturedDocument(root, documentBase, contexts, loaded, budget, cancellationToken);
        root.Remove(predicate);
        JObject? selected = null;
        foreach (var node in expanded.OfType<JObject>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (node.Property(predicate, StringComparison.Ordinal) is null) { continue; }
            if (selected is not null)
            {
                throw FederationJson.Invalid("The authored projection root has competing expanded identities.");
            }
            selected = node;
        }
        return selected ?? throw FederationJson.Invalid("The authored projection root could not be identified.");
    }
}
