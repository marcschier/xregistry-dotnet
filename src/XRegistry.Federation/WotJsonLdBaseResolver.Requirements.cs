// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Newtonsoft.Json.Linq;
using XRegistry.Models;

namespace XRegistry.Federation;

public static partial class WotJsonLdBaseResolver
{
    /// <summary>Derives a SemVer resolution request from an authored ordinary reference or dependency link.</summary>
    /// <remarks>
    /// Member meanings come from captured contexts. A TD type link also carries the authored version.model
    /// and the complete type-link floor inventory. Syntax and floor compatibility are checked by Version
    /// selection, after its ambiguity rule. Projection requirements and incremental compatibility lines
    /// are not interpreted by this method. An exact-Version URI association bypasses floor derivation.
    /// </remarks>
    public static async ValueTask<WotVersionResolutionRequest> ReadSemVerReferenceRequirementsAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        WotCapturedDependencyOccurrence dependency, IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(dependency);
        ArgumentNullException.ThrowIfNull(dependency.Reference);
        ArgumentNullException.ThrowIfNull(dependency.Target);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        if (!Enum.IsDefined(sourceKind)) { throw new ArgumentOutOfRangeException(nameof(sourceKind)); }
        cancellationToken.ThrowIfCancellationRequested();
        var readBudget = budget ?? new();
        var contexts = ContextSnapshot(CaptureContextDocuments(capturedContexts, readBudget, cancellationToken));
        var reference = dependency.Reference;
        var operation = reference.Operator switch
        {
            "tm:ref" or "tm:extends" or "tm:submodel" => WotReferenceOperator.ThingModel,
            "td:ref" or "td:extends" or "td:subthing" => WotReferenceOperator.ThingDescription,
            "type" when sourceKind == WotReferenceTargetKind.ThingDescription => WotReferenceOperator.ThingModelTypeLink,
            _ => throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "This request reader requires an ordinary reference or a supported dependency link.")
        };
        var targetKind = operation == WotReferenceOperator.ThingDescription
            ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel;
        if (dependency.Target.ResourceKind != (targetKind == WotReferenceTargetKind.ThingModel ? "thingmodel" : "thingdescription") ||
            dependency.Target.WotId is null)
        {
            throw FederationJson.Invalid("The authored operator and captured target kind disagree.");
        }
        var authoredReferences = reference.Operator is "tm:ref" or "td:ref"
            ? await DiscoverOrdinaryReferencesAsync(document, documentUri, contexts, readBudget, cancellationToken).ConfigureAwait(false)
            : await DiscoverLinkReferencesAsync(document, documentUri, sourceKind, contexts, readBudget, cancellationToken).ConfigureAwait(false);
        if (!authoredReferences.Contains(reference))
        {
            throw FederationJson.Invalid("The supplied reference does not match an authored occurrence under the captured contexts.");
        }
        if (dependency.Target.VersionId is { } exactVersion)
        {
            return new(dependency.Target.WotId, targetKind, operation, VersionIdPin: exactVersion);
        }
        var root = await ParseDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        var occurrence = ObjectAtPointer(root, reference.SourcePointer, readBudget, cancellationToken);
        if (occurrence[reference.PropertyName] is not JValue { Type: JTokenType.String } href ||
            href.Value<string>() != reference.Href)
        {
            throw FederationJson.Invalid("The reference requirement does not match the captured authored occurrence.");
        }

        async ValueTask<List<(WotMinimumVersionKind Kind, JToken Value)>> FloorsAsync(string pointer)
        {
            var value = ObjectAtPointer(root, pointer, readBudget, cancellationToken);
            var floors = new List<(WotMinimumVersionKind Kind, JToken Value)>();
            foreach (var member in value.Properties())
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (member.Name.StartsWith('@')) { continue; }
                var iri = await ExpandMemberIriAsync(document, pointer, member.Name, documentUri, contexts,
                    readBudget, cancellationToken).ConfigureAwait(false);
                if (iri == "https://www.w3.org/2019/wot/tm#minVersion")
                {
                    floors.Add((WotMinimumVersionKind.ThingModel, member.Value));
                }
                else if (iri == "https://www.w3.org/2019/wot/td#minVersion")
                {
                    floors.Add((WotMinimumVersionKind.ThingDescription, member.Value));
                }
            }
            return floors;
        }

        var selectedFloors = await FloorsAsync(reference.SourcePointer).ConfigureAwait(false);
        var typeLinkCount = 1;
        var allTypeLinksHaveFloor = true;
        string? implicitFloor = null;
        if (operation == WotReferenceOperator.ThingModelTypeLink)
        {
            JToken? authoredModel = null;
            if (root["version"] is JObject version && version.TryGetValue("model", out var model))
            {
                authoredModel = model;
            }
            var links = await DiscoverLinkReferencesAsync(document, documentUri, sourceKind, contexts,
                readBudget, cancellationToken).ConfigureAwait(false);
            typeLinkCount = 0;
            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (link.Operator != "type") { continue; }
                typeLinkCount++;
                var floors = link.SourcePointer == reference.SourcePointer
                    ? selectedFloors : await FloorsAsync(link.SourcePointer).ConfigureAwait(false);
                allTypeLinksHaveFloor &= floors.Any(floor => floor.Kind == WotMinimumVersionKind.ThingModel);
            }
            if (typeLinkCount == 0) { throw FederationJson.Invalid("The supplied occurrence is not an authored TD type link."); }
            if (authoredModel is not null && typeLinkCount > 1 && !allTypeLinksHaveFloor)
            {
                throw new RegistryException(new("ambiguous-floor", reference.SourcePointer,
                    "Every type link requires its own TM floor when the TD has version.model and several type links."));
            }
            if (authoredModel is not null)
            {
                if (authoredModel.Type != JTokenType.String)
                {
                    throw new RegistryException(new("invalid-version", "/version/model",
                        "A TD's implicit model floor must be an authored string."));
                }
                implicitFloor = authoredModel.Value<string>()!;
            }
        }
        if (selectedFloors.Count > 1)
        {
            throw new RegistryException(new("invalid-floor", reference.SourcePointer,
                "One reference cannot carry competing minimum-version members."));
        }
        string? minimum = null;
        WotMinimumVersionKind? minimumKind = null;
        if (selectedFloors.Count == 1)
        {
            var floor = selectedFloors[0];
            minimumKind = floor.Kind;
            if (floor.Value.Type != JTokenType.String)
            {
                throw new RegistryException(new("invalid-version", reference.SourcePointer,
                    "A minimum version must be an authored string."));
            }
            minimum = floor.Value.Value<string>()!;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(dependency.Target.WotId, targetKind, operation, minimum, minimumKind, implicitFloor,
            typeLinkCount, allTypeLinksHaveFloor);
    }
}
