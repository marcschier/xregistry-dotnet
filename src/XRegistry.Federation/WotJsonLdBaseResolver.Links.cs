// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Newtonsoft.Json.Linq;
using XRegistry.Models;

namespace XRegistry.Federation;

public static partial class WotJsonLdBaseResolver
{
    private const string HasLinkIri = "https://www.w3.org/2019/wot/td#hasLink";
    private const string RelationTypeIri = "https://www.w3.org/2019/wot/hypermedia#hasRelationType";
    private const string LinkTargetIri = "https://www.w3.org/2019/wot/hypermedia#hasTarget";

    /// <summary>Discovers reference links using captured member meanings and literal relation values.</summary>
    /// <remarks>
    /// The caller supplies the source kind established by its captured model. Only TDs contribute type links.
    /// Results also include describedby candidates for subsequent Schema exclusion; describedby does not
    /// authorize a closure binding. This method does not classify targets or apply reference floors.
    /// </remarks>
    public static async ValueTask<IReadOnlyList<WotAuthoredReferenceOccurrence>> DiscoverLinkReferencesAsync(
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
        ExpandCapturedDocument(root, documentBase, contextDocuments, new(), readBudget, cancellationToken);
        var candidates = new List<(string Pointer, JProperty Property)>();
        CollectAuthoredMembers(root, "", candidates, readBudget, cancellationToken);
        var occurrences = new List<WotAuthoredReferenceOccurrence>();
        foreach (var (pointer, property) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            var iri = await ExpandMemberIriAsync(document, pointer, property.Name, documentUri,
                contexts, readBudget, cancellationToken).ConfigureAwait(false);
            if (iri != HasLinkIri) { continue; }
            if (property.Value is not JArray links)
            {
                throw FederationJson.Invalid("A WoT links member must contain an authored array.");
            }
            var collectionPointer = pointer + "/" +
                property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            for (var index = 0; index < links.Count; index++)
            {
                readBudget.ChargeWork();
                if (links[index] is not JObject link)
                {
                    throw FederationJson.Invalid("A WoT links array must contain link objects.");
                }
                var linkPointer = collectionPointer + "/" + index.ToString(CultureInfo.InvariantCulture);
                var relations = new List<JProperty>();
                var targets = new List<JProperty>();
                foreach (var member in link.Properties())
                {
                    if (member.Name.StartsWith('@')) { continue; }
                    var meaning = await ExpandMemberIriAsync(document, linkPointer, member.Name, documentUri,
                        contexts, readBudget, cancellationToken).ConfigureAwait(false);
                    if (meaning == RelationTypeIri) { relations.Add(member); }
                    else if (meaning == LinkTargetIri) { targets.Add(member); }
                }
                if (relations.Any(relation => relation.Value.Type != JTokenType.String))
                {
                    throw FederationJson.Invalid("A WoT link relation must be an authored string.");
                }
                var selectedRelations = relations.Where(relation =>
                    IsReferenceRelation(relation.Value.Value<string>()!, sourceKind)).ToArray();
                if (selectedRelations.Length == 0) { continue; }
                if (relations.Count != 1 || targets.Count != 1)
                {
                    throw FederationJson.Invalid("A reference link must have one relation member and one href member.");
                }
                var target = targets[0];
                if (target.Value.Type != JTokenType.String)
                {
                    throw FederationJson.Invalid("A reference link href must be an authored string.");
                }
                var href = target.Value.Value<string>()!;
                var resolved = await ResolveReferenceUriAsync(document, linkPointer, target.Name, href,
                    documentUri, contexts, readBudget, cancellationToken).ConfigureAwait(false);
                occurrences.Add(new(linkPointer, target.Name, selectedRelations[0].Value.Value<string>()!, href, resolved));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return occurrences.AsReadOnly();
    }

    private static bool IsReferenceRelation(string relation, WotReferenceTargetKind sourceKind) =>
        relation is "tm:extends" or "tm:submodel" or "td:extends" or "td:subthing" or "describedby" ||
        relation == "type" && sourceKind == WotReferenceTargetKind.ThingDescription;
}
