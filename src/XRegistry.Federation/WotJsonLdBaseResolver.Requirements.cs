// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Newtonsoft.Json.Linq;
using XRegistry.Models;

namespace XRegistry.Federation;

public static partial class WotJsonLdBaseResolver
{
    internal static async ValueTask<WotVersionResolutionRequest> ReadLocalReferenceRequirementsAsync(
        WotResolvedDocument containing, WotAuthoredReferenceOccurrence reference, Uri documentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts, FederationReadBudget budget,
        CancellationToken cancellationToken)
    {
        if (!reference.Target.IsDocumentLocal || reference.Operator is not ("tm:ref" or "td:ref"))
        {
            throw FederationJson.Invalid("A captured local request requires an ordinary document-local reference.");
        }
        var contexts = ContextSnapshot(CaptureContextDocuments(capturedContexts, budget, cancellationToken));
        var references = await DiscoverOrdinaryReferencesAsync(containing.Document, documentUri, contexts,
            budget, cancellationToken).ConfigureAwait(false);
        if (!references.Contains(reference))
        {
            throw FederationJson.Invalid("The local reference does not match an authored occurrence under captured contexts.");
        }
        var kind = reference.Operator == "tm:ref"
            ? WotReferenceTargetKind.ThingModel : WotReferenceTargetKind.ThingDescription;
        if (kind != containing.Kind)
        {
            throw new RegistryException(new("invalid-floor", reference.SourcePointer,
                "A local reference must have the containing document's WoT kind."));
        }
        var root = await ParseDocumentAsync(containing.Document, budget, cancellationToken).ConfigureAwait(false);
        var floors = await ReadAuthoredMinimumVersionsAsync(containing.Document, documentUri, root,
            reference.SourcePointer, kind, contexts, budget, cancellationToken).ConfigureAwait(false);
        var (minimum, minimumKind) = ReadMinimumVersion(floors, reference.SourcePointer);
        var (line, _) = await ReadAuthoredSelectionMembersAsync(containing.Document, documentUri, root,
            reference.SourcePointer, contexts, false, budget, cancellationToken).ConfigureAwait(false);
        return new(containing.Version.WotId, kind, kind == WotReferenceTargetKind.ThingModel
            ? WotReferenceOperator.ThingModel : WotReferenceOperator.ThingDescription, minimum, minimumKind,
            IsDocumentLocal: true) { CompatibilityLine = line };
    }

    /// <summary>Derives a SemVer resolution request from an authored ordinary reference or dependency link.</summary>
    /// <remarks>
    /// Member meanings come from captured contexts. A TD type link also carries the authored version.model
    /// and the complete type-link floor inventory. Syntax and floor compatibility are checked by Version
    /// selection, after its ambiguity rule. This entry point assumes a native SemVer type-link inventory;
    /// use ReadReferenceRequirementsAsync for mixed-profile targets. Deployed line selectors are retained
    /// for rejection by SemVer selection. An exact-Version URI association bypasses floor derivation.
    /// </remarks>
    public static async ValueTask<WotVersionResolutionRequest> ReadSemVerReferenceRequirementsAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        WotCapturedDependencyOccurrence dependency, IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        return await ReadReferenceRequirementsCoreAsync(document, documentUri, sourceKind, dependency,
            capturedContexts, null, true, null, budget, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Derives deployed and legacy reference requirements using independently captured target profiles.</summary>
    /// <remarks>
    /// Incremental references carry only their own explicit line and optional numeric floor.
    /// Only SemVer-target type links participate in a TD's implicit model floor and ambiguity rule.
    /// Exact-Version URI associations bypass selection requirements. Projection entries are not accepted.
    /// </remarks>
    public static ValueTask<WotVersionResolutionRequest> ReadReferenceRequirementsAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        WotCapturedDependencyOccurrence dependency, IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        WotCapturedUriIdentityIndex capturedIdentities, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capturedIdentities);
        return ReadReferenceRequirementsCoreAsync(document, documentUri, sourceKind, dependency,
            capturedContexts, capturedIdentities, false, null, budget, cancellationToken);
    }

    /// <summary>Reads a projection source entry's profile-aware requirements and optional exact-byte digest pin.</summary>
    /// <remarks>The returned digest pin uses the Registry digest spelling. It does not waive source requirements.</remarks>
    public static async ValueTask<WotVersionResolutionRequest> ReadProjectionSourceRequirementsAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        WotProjectionSourceOccurrence source, IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        WotCapturedUriIdentityIndex capturedIdentities, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capturedIdentities);
        var readBudget = budget ?? new();
        var sources = await DiscoverProjectionSourceReferencesAsync(document, documentUri, sourceKind,
            capturedContexts, readBudget, cancellationToken).ConfigureAwait(false);
        if (!sources.Contains(source))
        {
            throw FederationJson.Invalid("The supplied source does not match an authored projection manifest entry.");
        }
        var identity = capturedIdentities.ClassifyProjectionSource(source, readBudget, cancellationToken) ??
            throw FederationJson.Invalid("A projection source cannot identify its containing plan.");
        return await ReadReferenceRequirementsCoreAsync(document, documentUri, sourceKind,
            new(source.Reference, identity), capturedContexts, capturedIdentities, false, source,
            readBudget, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WotVersionResolutionRequest> ReadReferenceRequirementsCoreAsync(
        FederationDocument document, Uri documentUri, WotReferenceTargetKind sourceKind,
        WotCapturedDependencyOccurrence dependency, IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        WotCapturedUriIdentityIndex? capturedIdentities, bool semVerOnly, WotProjectionSourceOccurrence? projectionSource,
        FederationReadBudget? budget, CancellationToken cancellationToken)
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
            ProjectionVocabulary + "projects" when projectionSource is not null =>
                projectionSource.TargetKind == WotReferenceTargetKind.ThingModel
                    ? WotReferenceOperator.ThingModel : WotReferenceOperator.ThingDescription,
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
        WotCapturedResourceIdentity? Classify(WotCapturedUriIdentityIndex index, WotAuthoredReferenceOccurrence occurrence) =>
            projectionSource is not null && occurrence == projectionSource.Reference
                ? index.ClassifyProjectionSource(projectionSource, readBudget, cancellationToken)
                : occurrence.Operator is "tm:ref" or "td:ref"
                ? index.ClassifyOrdinaryReference(occurrence, readBudget, cancellationToken)
                : index.ClassifyDependencyLink(occurrence, sourceKind, readBudget, cancellationToken);
        if (capturedIdentities is not null && Classify(capturedIdentities, reference) != dependency.Target)
        {
            throw FederationJson.Invalid("The supplied dependency target does not match the captured URI inventory.");
        }
        var profile = TargetProfile(dependency.Target);
        if (semVerOnly && profile != WotVersionProfile.SemVer)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The SemVer requirement reader does not accept an incremental target.");
        }
        if (projectionSource is null)
        {
            var authoredReferences = reference.Operator is "tm:ref" or "td:ref"
                ? await DiscoverOrdinaryReferencesAsync(document, documentUri, contexts, readBudget, cancellationToken).ConfigureAwait(false)
                : await DiscoverLinkReferencesAsync(document, documentUri, sourceKind, contexts, readBudget, cancellationToken).ConfigureAwait(false);
            if (!authoredReferences.Contains(reference))
            {
                throw FederationJson.Invalid("The supplied reference does not match an authored occurrence under the captured contexts.");
            }
        }
        if (projectionSource is null && dependency.Target.VersionId is { } exactVersion)
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

        ValueTask<List<(WotMinimumVersionKind Kind, JToken Value)>> FloorsAsync(string pointer) =>
            ReadAuthoredMinimumVersionsAsync(document, documentUri, root, pointer, targetKind,
                contexts, readBudget, cancellationToken);

        var selectedFloors = await FloorsAsync(reference.SourcePointer).ConfigureAwait(false);
        var typeLinkCount = 1;
        var allTypeLinksHaveFloor = true;
        string? implicitFloor = null;
        if (operation == WotReferenceOperator.ThingModelTypeLink)
        {
            JToken? authoredModel = null;
            if (profile == WotVersionProfile.SemVer &&
                root["version"] is JObject version && version.TryGetValue("model", out var model))
            {
                authoredModel = model;
            }
            var links = await DiscoverLinkReferencesAsync(document, documentUri, sourceKind, contexts,
                readBudget, cancellationToken).ConfigureAwait(false);
            typeLinkCount = 0;
            var semVerLinks = new List<WotAuthoredReferenceOccurrence>();
            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readBudget.ChargeWork();
                if (link.Operator != "type") { continue; }
                if (capturedIdentities is not null)
                {
                    var target = Classify(capturedIdentities, link) ??
                        throw FederationJson.Invalid("A TD type link cannot identify its containing TD as a Thing Model.");
                    if (TargetProfile(target) != WotVersionProfile.SemVer) { continue; }
                }
                semVerLinks.Add(link);
            }
            foreach (var link in semVerLinks)
            {
                typeLinkCount++;
                var floors = link.SourcePointer == reference.SourcePointer
                    ? selectedFloors : await FloorsAsync(link.SourcePointer).ConfigureAwait(false);
                allTypeLinksHaveFloor &= floors.Any(floor => floor.Kind == WotMinimumVersionKind.ThingModel);
            }
            if (profile == WotVersionProfile.SemVer && typeLinkCount == 0)
            {
                throw FederationJson.Invalid("The supplied occurrence is not an authored SemVer TD type link.");
            }
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
        var (minimum, minimumKind) = ReadMinimumVersion(selectedFloors, reference.SourcePointer);
        var (compatibilityLine, sourceDigest) = await ReadAuthoredSelectionMembersAsync(document, documentUri, root,
            reference.SourcePointer, contexts, projectionSource is not null, readBudget, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(dependency.Target.WotId, targetKind, operation, minimum, minimumKind, implicitFloor,
            typeLinkCount, allTypeLinksHaveFloor, DigestPin: sourceDigest) { CompatibilityLine = compatibilityLine };
    }

    private static async ValueTask<List<(WotMinimumVersionKind Kind, JToken Value)>> ReadAuthoredMinimumVersionsAsync(
        FederationDocument document, Uri documentUri, JObject root, string pointer, WotReferenceTargetKind targetKind,
        IReadOnlyDictionary<Uri, FederationDocument> contexts, FederationReadBudget budget, CancellationToken token)
    {
        var value = ObjectAtPointer(root, pointer, budget, token);
        var floors = new List<(WotMinimumVersionKind Kind, JToken Value)>();
        foreach (var member in value.Properties())
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (member.Name.StartsWith('@')) { continue; }
            var iri = await ExpandMemberIriAsync(document, pointer, member.Name, documentUri, contexts,
                budget, token).ConfigureAwait(false);
            if (iri == "https://www.w3.org/2019/wot/tm#minVersion")
            {
                floors.Add((WotMinimumVersionKind.ThingModel, member.Value));
            }
            else if (iri == "https://www.w3.org/2019/wot/td#minVersion")
            {
                floors.Add((WotMinimumVersionKind.ThingDescription, member.Value));
            }
            else if (iri == "http://opcfoundation.org/UA/WoT-Binding/minVersion")
            {
                floors.Add((targetKind == WotReferenceTargetKind.ThingModel
                    ? WotMinimumVersionKind.ThingModel : WotMinimumVersionKind.ThingDescription, member.Value));
            }
        }
        return floors;
    }

    private static (string? Minimum, WotMinimumVersionKind? Kind) ReadMinimumVersion(
        List<(WotMinimumVersionKind Kind, JToken Value)> floors, string pointer)
    {
        if (floors.Count > 1)
        {
            throw new RegistryException(new("invalid-floor", pointer,
                "One reference cannot carry competing minimum-version members."));
        }
        if (floors.Count == 0) { return (null, null); }
        var floor = floors[0];
        if (floor.Value.Type != JTokenType.String)
        {
            throw new RegistryException(new("invalid-version", pointer, "A minimum version must be an authored string."));
        }
        return (floor.Value.Value<string>()!, floor.Kind);
    }

    private static async ValueTask<(string? Line, string? Digest)> ReadAuthoredSelectionMembersAsync(FederationDocument document,
        Uri documentUri, JObject root, string pointer, IReadOnlyDictionary<Uri, FederationDocument> contexts,
        bool projectionSource, FederationReadBudget budget, CancellationToken token)
    {
        string? line = null;
        string? sourceDigest = null;
        var count = 0;
        foreach (var member in ObjectAtPointer(root, pointer, budget, token).Properties())
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (member.Name.StartsWith('@')) { continue; }
            var iri = await ExpandMemberIriAsync(document, pointer, member.Name, documentUri,
                contexts, budget, token).ConfigureAwait(false);
            if (projectionSource && iri == ProjectionVocabulary + "sourceDigest")
            {
                if (sourceDigest is not null || member.Value.Type != JTokenType.String ||
                    member.Value.Value<string>() is not { Length: 72 } digest ||
                    !digest.StartsWith("sha-256:", StringComparison.Ordinal) ||
                    digest.AsSpan(8).ContainsAnyExcept("0123456789abcdef".AsSpan()))
                {
                    throw FederationJson.Invalid("A projection source requires one sha-256 digest with 64 lowercase hexadecimal digits.");
                }
                sourceDigest = "sha256:" + digest[8..];
                continue;
            }
            if (iri != "http://opcfoundation.org/UA/WoT-Binding/compatibilityLine" || pointer.Length == 0) { continue; }
            count++;
            if (count > 1 || member.Value.Type != JTokenType.String)
            {
                throw new RegistryException(new("invalid-line", pointer,
                    "One reference can carry only one string-valued target compatibility line."));
            }
            line = member.Value.Value<string>()!;
        }
        return (line, sourceDigest);
    }

    private static WotVersionProfile TargetProfile(WotCapturedResourceIdentity identity) =>
        identity.Source.Model is { } model ? WotRegistryProfiles.Read(model) :
            throw new RegistryException(new("unknown-profile", "",
                "Reference requirements need an available captured target profile."));
}
