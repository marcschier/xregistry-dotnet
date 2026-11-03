// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>Identifies which captured Registry value supplied a URI identity.</summary>
public enum WotCapturedUriIdentityKind
{
    /// <summary>The Resource's applicable WoT identifier.</summary>
    WotId,
    /// <summary>The original absolute base recorded for a captured document.</summary>
    DocumentBase,
    /// <summary>A URI that the captured Registry view assigns to a Resource or Version.</summary>
    DocumentView,
}

/// <summary>A captured Resource identity and its already selected, caller-authorized Registry source.</summary>
public sealed record WotCapturedResourceIdentity
{
    /// <summary>Creates a Resource-level or exact-Version identity from captured Registry metadata.</summary>
    public WotCapturedResourceIdentity(IFederationReadSource source, string groupXid,
        string resourceCollection, string resourceXid, string resourceKind, string? wotId,
        string? versionId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(groupXid);
        ArgumentNullException.ThrowIfNull(resourceCollection);
        ArgumentNullException.ThrowIfNull(resourceXid);
        ArgumentException.ThrowIfNullOrEmpty(resourceKind);

        var groupParts = FederationSyntax.Xid(groupXid);
        var collectionParts = FederationSyntax.Xid(resourceCollection, collection: true);
        var resourceParts = FederationSyntax.Xid(resourceXid);
        if (groupParts.Length != 2 || collectionParts.Length != 3 || resourceParts.Length != 4 ||
            !groupParts.SequenceEqual(collectionParts.Take(2), StringComparer.Ordinal) ||
            !collectionParts.SequenceEqual(resourceParts.Take(3), StringComparer.Ordinal))
        {
            throw new ArgumentException("The Group, Resource collection and Resource XIDs must identify one captured path.");
        }
        if (resourceKind is "thingmodel" or "thingdescription" && string.IsNullOrEmpty(wotId))
        {
            throw new ArgumentException("A WoT Resource identity requires its captured wotid.", nameof(wotId));
        }
        if (versionId is { Length: 0 })
        {
            throw new ArgumentException("An exact Version identity requires a nonempty Version id.", nameof(versionId));
        }
        if (versionId is not null)
        {
            _ = new FederationReadRequest(FederationOperation.Entity,
                resourceXid + "/versions/" + Uri.EscapeDataString(versionId));
        }

        Source = source;
        GroupXid = groupXid;
        ResourceCollection = resourceCollection;
        ResourceXid = resourceXid;
        ResourceKind = resourceKind;
        WotId = wotId;
        VersionId = versionId;
    }

    /// <summary>Gets the authorized captured source that owns the Resource.</summary>
    public IFederationReadSource Source { get; }
    /// <summary>Gets the captured containing Group XID.</summary>
    public string GroupXid { get; }
    /// <summary>Gets the captured Resource collection XID.</summary>
    public string ResourceCollection { get; }
    /// <summary>Gets the captured Resource XID.</summary>
    public string ResourceXid { get; }
    /// <summary>Gets the captured Resource type's singular name.</summary>
    public string ResourceKind { get; }
    /// <summary>Gets the applicable captured WoT identity, when the Resource has one.</summary>
    public string? WotId { get; }
    /// <summary>Gets the exact Version id, or null when the URI identifies the Resource.</summary>
    public string? VersionId { get; }
}

/// <summary>Associates one fragment-free URI with a captured Resource or exact Version identity.</summary>
public sealed record WotCapturedUriIdentity
{
    /// <summary>Creates one identity association from captured Registry metadata.</summary>
    public WotCapturedUriIdentity(Uri uri, WotCapturedUriIdentityKind kind,
        WotCapturedResourceIdentity target)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("A captured URI identity must be absolute.", nameof(uri));
        }
        var absolute = FederationJson.AbsoluteUri(uri.AbsoluteUri);
        if (absolute.Fragment.Length != 0)
        {
            throw new ArgumentException("A captured URI identity must not contain a fragment.", nameof(uri));
        }

        Uri = absolute;
        Kind = kind;
        Target = target;
    }

    /// <summary>Gets the absolute, fragment-free URI recorded by the Registry view.</summary>
    public Uri Uri { get; }
    /// <summary>Gets the captured metadata field that supplied the URI.</summary>
    public WotCapturedUriIdentityKind Kind { get; }
    /// <summary>Gets the captured target identity.</summary>
    public WotCapturedResourceIdentity Target { get; }
}

/// <summary>A URI association retained by an authorized captured Registry view.</summary>
public sealed record WotCapturedUriAssociation
{
    /// <summary>Records the URI's declared Resource or exact-Version scope without inferring target kind.</summary>
    public WotCapturedUriAssociation(IFederationReadSource source, Uri uri,
        WotCapturedUriIdentityKind kind, string targetXid)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(targetXid);
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        if (!uri.IsAbsoluteUri || FederationJson.AbsoluteUri(uri.AbsoluteUri).Fragment.Length != 0)
        {
            throw new ArgumentException("A captured URI association must be absolute and fragment-free.", nameof(uri));
        }
        var path = RegistryPath.Parse(targetXid);
        if (path.Kind is not (RegistryPathKind.Resource or RegistryPathKind.Version) || path.IsDetails)
        {
            throw new ArgumentException("A captured URI association must identify a Resource or exact Version.", nameof(targetXid));
        }
        Source = source;
        Uri = uri;
        Kind = kind;
        TargetXid = targetXid;
    }

    /// <summary>The already authorized source retaining the selected Registry view.</summary>
    public IFederationReadSource Source { get; }
    /// <summary>The URI recorded by the view, never an inferred source locator.</summary>
    public Uri Uri { get; }
    /// <summary>The captured field that supplied the URI.</summary>
    public WotCapturedUriIdentityKind Kind { get; }
    /// <summary>The Resource or exact Version identified by the captured URI.</summary>
    public string TargetXid { get; }
}

/// <summary>Resolves external URI identities without consulting bindings or live Registry state.</summary>
public sealed class WotCapturedUriIdentityIndex
{
    private readonly WotCapturedUriIdentity[] _identities;
    private readonly Dictionary<IFederationReadSource, CapturedSource> _sources =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Builds associations from an owned captured graph's applicable wotid values and retained document bases.</summary>
    /// <remarks>
    /// WoT identifiers retain Resource scope; a document base identifies its exact associated Version.
    /// Missing bases remain absent. Storage locators and provenance are not used to invent associations.
    /// Extra associations must be supplied from independently captured URI metadata, not closure bindings.
    /// </remarks>
    public static async ValueTask<WotCapturedUriIdentityIndex> CaptureRegistryAsync(
        CapturedRegistryReadSource source, IEnumerable<WotCapturedUriAssociation>? additionalAssociations = null,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        budget ??= new();
        var associations = new List<WotCapturedUriAssociation>();
        Uri IdentityUri(string value)
        {
            budget.CheckObjectBytes(value.Length);
            budget.ChargeWork(value.Length);
            var length = System.Text.Encoding.UTF8.GetByteCount(value);
            budget.CheckObjectBytes(length);
            budget.ChargeBytes(length);
            budget.ChargeObject();
            var uri = FederationJson.AbsoluteUri(value);
            if (uri.Fragment.Length != 0) { throw FederationJson.Invalid("A captured URI identity must be fragment-free."); }
            return uri;
        }
        foreach (var (xid, kind, version) in source.ResourceMetadata(budget, cancellationToken))
        {
            if (kind is "thingmodel" or "thingdescription")
            {
                associations.Add(new(source, IdentityUri(FederationJson.String(version, "wotid")),
                    WotCapturedUriIdentityKind.WotId, xid));
            }
        }
        foreach (var (xid, document) in source.DocumentAssociations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (document.Base is { } recordedBase)
            {
                associations.Add(new(source, IdentityUri(recordedBase), WotCapturedUriIdentityKind.DocumentBase, xid));
            }
        }
        foreach (var association in additionalAssociations ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            ArgumentNullException.ThrowIfNull(association);
            associations.Add(association);
        }
        return await CaptureAsync(associations, budget, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Derives target kinds and applicable WoT identifiers from captured models and metadata.</summary>
    /// <remarks>
    /// Associations must come from the captured view's URI and document metadata, not closure bindings.
    /// Resource associations use captured Meta and default-Version metadata; exact-Version associations
    /// do not require a default. Only the supplied sources are read, and document bytes are not acquired.
    /// </remarks>
    public static async ValueTask<WotCapturedUriIdentityIndex> CaptureAsync(
        IEnumerable<WotCapturedUriAssociation> associations, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(associations);
        cancellationToken.ThrowIfCancellationRequested();
        var readBudget = budget ?? new();
        var reads = new Dictionary<IFederationReadSource, CapturedFederationReadScope>(
            ReferenceEqualityComparer.Instance);
        var metadata = new Dictionary<(CapturedFederationReadScope Scope, string Xid), JsonElement>();
        var identities = new List<WotCapturedUriIdentity>();

        async ValueTask<JsonElement> EntityAsync(CapturedFederationReadScope scope, string xid)
        {
            if (metadata.TryGetValue((scope, xid), out var retained)) { return retained; }
            var envelope = await scope.MetadataAsync(new(FederationOperation.Entity, xid)).ConfigureAwait(false);
            var entity = envelope.TryGetProperty("entity", out var value) ? value : envelope;
            if (!FederationSyntax.SameXid(FederationJson.String(entity, "xid"), xid))
            {
                throw FederationJson.Invalid("A captured URI association returned different entity metadata.");
            }
            metadata.Add((scope, xid), entity);
            return entity;
        }

        foreach (var association in associations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (association is null)
            {
                throw new ArgumentException("Captured URI associations cannot contain null entries.", nameof(associations));
            }
            if (!reads.TryGetValue(association.Source, out var scope))
            {
                readBudget.ChargeSource();
                scope = new(association.Source, readBudget, cancellationToken);
                reads.Add(association.Source, scope);
            }
            scope.Check();
            var parts = FederationSyntax.Xid(association.TargetXid);
            if (!scope.Model.Groups.TryGetValue(parts[0], out var group) ||
                !group.Resources.TryGetValue(parts[2], out var resource) || !resource.HasDocument)
            {
                throw FederationJson.Invalid("A captured URI association must identify a document-bearing type in the captured model.");
            }
            var groupXid = DocumentTreeFormat.XidPath(parts[..2]);
            var collectionXid = DocumentTreeFormat.XidPath(parts[..3]);
            var resourceXid = DocumentTreeFormat.XidPath(parts[..4]);
            var selectedResourceXid = resourceXid;
            var versionId = parts.Length == 6 ? parts[5] : null;
            var selectedId = versionId;
            if (selectedId is null)
            {
                var meta = await EntityAsync(scope, resourceXid + "/meta").ConfigureAwait(false);
                if (meta.TryGetProperty("xref", out var xref))
                {
                    readBudget.CheckHops(1);
                    if (xref.ValueKind != JsonValueKind.String)
                    {
                        throw FederationJson.Invalid("A captured Resource alias must name a Resource XID.");
                    }
                    var targetParts = FederationSyntax.Xid(xref.GetString()!);
                    if (targetParts.Length != 4 ||
                        !ReferenceEquals(resource, DocumentTreeFormat.Resource(scope.Model, targetParts)))
                    {
                        throw FederationJson.Invalid("A captured Resource alias must name the same Resource model type.");
                    }
                    selectedResourceXid = DocumentTreeFormat.XidPath(targetParts);
                    meta = await EntityAsync(scope, selectedResourceXid + "/meta").ConfigureAwait(false);
                    if (meta.TryGetProperty("xref", out _))
                    {
                        throw new FederationException(FederationErrorCode.UnsupportedOperation,
                            "A captured Resource alias target cannot itself be an alias.");
                    }
                }
                selectedId = FederationJson.String(meta, "defaultversionid");
            }
            var versionXid = selectedResourceXid + "/versions/" + Uri.EscapeDataString(selectedId);
            var version = await EntityAsync(scope, versionXid).ConfigureAwait(false);
            if (version.TryGetProperty("versionid", out _) && FederationJson.String(version, "versionid") != selectedId)
            {
                throw FederationJson.Invalid("Captured Version metadata disagrees with its Version identity.");
            }
            var wotId = resource.Singular is "thingmodel" or "thingdescription"
                ? FederationJson.String(version, "wotid") : null;
            if (wotId is not null) { _ = FederationJson.AbsoluteUri(wotId); }
            if (association.Kind == WotCapturedUriIdentityKind.WotId &&
                (wotId is null || FederationJson.AbsoluteUri(wotId) != association.Uri || versionId is not null))
            {
                throw FederationJson.Invalid("A wotid association must identify its captured WoT Resource and applicable wotid.");
            }
            identities.Add(new(association.Uri, association.Kind,
                new(association.Source, groupXid, collectionXid, resourceXid, resource.Singular, wotId, versionId)));
        }

        foreach (var scope in reads.Values)
        {
            readBudget.ChargeWork();
            scope.Check();
        }
        foreach (var ((scope, xid), retained) in metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (scope.Context.IsImmutable) { continue; }
            var envelope = await scope.MetadataAsync(new(FederationOperation.Entity, xid)).ConfigureAwait(false);
            var entity = envelope.TryGetProperty("entity", out var value) ? value : envelope;
            if (!EqualJson(retained, entity, readBudget, cancellationToken))
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "URI identity metadata changed during capture.");
            }
        }
        var result = new WotCapturedUriIdentityIndex(identities, readBudget, cancellationToken);
        foreach (var scope in reads.Values) { scope.Check(); }
        return result;
    }

    /// <summary>Captures the URI identities supplied by the already selected Registry views.</summary>
    public WotCapturedUriIdentityIndex(IEnumerable<WotCapturedUriIdentity> identities,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var readBudget = budget ?? new();
        cancellationToken.ThrowIfCancellationRequested();
        var captured = new List<WotCapturedUriIdentity>();
        var immutableContexts = new HashSet<NativeRegistryContext>();
        var sourceCount = 0;
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (identity is null)
            {
                throw new ArgumentException("Captured URI identities cannot contain null entries.", nameof(identities));
            }
            if (!_sources.TryGetValue(identity.Target.Source, out var source))
            {
                source = new(identity.Target.Source, _sources.Count);
                if (!source.Context.IsImmutable || immutableContexts.Add(source.Context))
                {
                    if (sourceCount >= readBudget.Limits.MaxSources)
                    {
                        throw new FederationException(FederationErrorCode.LimitExceeded,
                            "The captured URI index exceeds the configured source limit.");
                    }
                    sourceCount++;
                }
                _sources.Add(identity.Target.Source, source);
            }
            source.Check(cancellationToken);
            captured.Add(identity);
        }
        foreach (var source in _sources.Values)
        {
            readBudget.ChargeWork();
            source.Check(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _identities = captured.ToArray();
    }

    /// <summary>Classifies an ordinary reference against captured identities before closure binding lookup.</summary>
    /// <returns>
    /// Null for a document-local reference. An external tm:ref can identify a Thing Model or an excluded
    /// Schema Resource; an external td:ref must identify a Thing Description. Schema results never authorize
    /// a WoT closure binding. Resource-level results do not yet select a Version.
    /// </returns>
    /// <remarks>Projection selectors must first be associated with their manifest entries, not passed here as ordinary references.</remarks>
    public WotCapturedResourceIdentity? ClassifyOrdinaryReference(WotAuthoredReferenceOccurrence reference,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(reference.Target);
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.Operator is not ("tm:ref" or "td:ref"))
        {
            throw FederationJson.Invalid("Ordinary reference classification requires a canonical tm:ref or td:ref operator.");
        }
        return ClassifyTarget(reference, reference.Operator == "tm:ref" ? "thingmodel" : "thingdescription",
            reference.Operator == "tm:ref", budget, cancellationToken);
    }

    /// <summary>Classifies a canonical dependency link using captured target identities and the source's model-derived kind.</summary>
    /// <returns>Null for a document-local link; otherwise the unique, correctly typed captured Resource or exact Version.</returns>
    /// <remarks>
    /// Accepts tm:extends, tm:submodel, td:extends, td:subthing and TD type links.
    /// Describedby candidates are not dependency links. This method neither selects Versions nor acquires documents.
    /// </remarks>
    public WotCapturedResourceIdentity? ClassifyDependencyLink(WotAuthoredReferenceOccurrence reference,
        WotReferenceTargetKind sourceKind, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(reference.Target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(sourceKind)) { throw new ArgumentOutOfRangeException(nameof(sourceKind)); }
        var targetKind = reference.Operator switch
        {
            "tm:extends" or "tm:submodel" => "thingmodel",
            "td:extends" or "td:subthing" => "thingdescription",
            "type" when sourceKind == WotReferenceTargetKind.ThingDescription => "thingmodel",
            _ => throw FederationJson.Invalid("Dependency link classification requires a canonical relation, with type restricted to TD sources.")
        };
        return ClassifyTarget(reference, targetKind, false, budget, cancellationToken);
    }

    /// <summary>Classifies a describedby link as an excluded Schema reference using captured identities.</summary>
    /// <returns>Null for a document-local link; otherwise the unique captured Schema identity.</returns>
    public WotCapturedResourceIdentity? ClassifySchemaDescription(WotAuthoredReferenceOccurrence reference,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(reference.Target);
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.Operator != "describedby")
        {
            throw FederationJson.Invalid("Schema description classification requires a literal describedby relation.");
        }
        return ClassifyTarget(reference, "schema", false, budget, cancellationToken);
    }

    /// <summary>Checks a discovered projection source's declared media kind against its unique captured URI identity.</summary>
    /// <returns>Null for a document-local source; otherwise its correctly typed captured Resource or exact Version.</returns>
    /// <remarks>Local sources still require cycle validation. Source requirements, pins and complete projection admission remain separate.</remarks>
    public WotCapturedResourceIdentity? ClassifyProjectionSource(WotProjectionSourceOccurrence source,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Reference);
        ArgumentNullException.ThrowIfNull(source.Reference.Target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(source.TargetKind)) { throw new ArgumentOutOfRangeException(nameof(source)); }
        if (source.Reference.Operator != WotJsonLdBaseResolver.ProjectionVocabulary + "projects" ||
            source.Reference.Target.Fragment is not null)
        {
            throw FederationJson.Invalid("Projection source classification requires its canonical operator and a fragment-free href.");
        }
        return ClassifyTarget(source.Reference, source.TargetKind == WotReferenceTargetKind.ThingModel
            ? "thingmodel" : "thingdescription", false, budget, cancellationToken);
    }

    private WotCapturedResourceIdentity? ClassifyTarget(WotAuthoredReferenceOccurrence reference,
        string targetKind, bool allowSchema, FederationReadBudget? budget, CancellationToken cancellationToken)
    {
        var readBudget = budget ?? new();
        readBudget.ChargeWork();
        if (reference.Target.IsDocumentLocal) { return null; }
        var target = Resolve(reference.Target.DocumentUri, readBudget, cancellationToken);
        if (target.ResourceKind != targetKind && !(allowSchema && target.ResourceKind == "schema"))
        {
            throw FederationJson.Invalid("The reference identifies an unsupported captured Resource kind.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return target;
    }

    /// <summary>Returns the unique captured Resource or exact Version identified by an absolute URI.</summary>
    /// <exception cref="FederationException">No identity matches, or distinct targets share the URI.</exception>
    public WotCapturedResourceIdentity Resolve(Uri uri, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Fragment.Length != 0)
        {
            throw FederationJson.Invalid("A captured identity lookup requires an absolute fragment-free URI.");
        }

        var readBudget = budget ?? new();
        cancellationToken.ThrowIfCancellationRequested();
        var targets = new Dictionary<TargetKey, WotCapturedResourceIdentity>();
        foreach (var match in _identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBudget.ChargeWork();
            if (match.Uri != uri) { continue; }
            var target = match.Target;
            var source = _sources[target.Source];
            source.Check(cancellationToken);
            var key = new TargetKey(source.Context, source.Context.IsImmutable ? null : source.Id,
                DocumentTreeFormat.XidPath(FederationSyntax.Xid(target.GroupXid)),
                DocumentTreeFormat.XidPath(FederationSyntax.Xid(target.ResourceXid)), target.VersionId);
            if (targets.TryGetValue(key, out var prior))
            {
                if (prior.ResourceKind != target.ResourceKind || prior.WotId != target.WotId ||
                    !FederationSyntax.SameXid(prior.ResourceCollection, target.ResourceCollection, collection: true) ||
                    _sources[prior.Source].Owner != source.Owner ||
                    !ModelsEqual(_sources[prior.Source].Model, source.Model, readBudget, cancellationToken))
                {
                    throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                        "Duplicate captured records disagree about one Resource or Version identity.");
                }
                continue;
            }
            targets.Add(key, target);
        }

        if (targets.Count == 0)
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "No captured Registry identity matches the resolved document URI.");
        }
        if (targets.Count != 1)
        {
            throw new FederationException(FederationErrorCode.Ambiguous,
                "Several distinct captured Resources or Versions match the resolved document URI.");
        }
        readBudget.ChargeSource();
        var selected = targets.Values.Single();
        _sources[selected.Source].Check(cancellationToken);
        return selected;
    }

    internal void CheckSource(IFederationReadSource source, CancellationToken cancellationToken) =>
        _sources[source].Check(cancellationToken);

    private static bool ModelsEqual(RegistryModel? left, RegistryModel? right,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(left, right)) { return true; }
        return left is not null && right is not null &&
            EqualJson(left.Source.RootElement, right.Source.RootElement, budget, cancellationToken) &&
            EqualJson(left.EffectiveModel.RootElement, right.EffectiveModel.RootElement, budget, cancellationToken);
    }

    private static bool EqualJson(JsonElement left, JsonElement right, FederationReadBudget budget,
        CancellationToken cancellationToken) =>
        JsonNode.DeepEquals(ModelNode(left, budget, cancellationToken), ModelNode(right, budget, cancellationToken));

    private static JsonNode? ModelNode(JsonElement value, FederationReadBudget budget,
        CancellationToken cancellationToken)
    {
        FederationJson.CountWork(value, budget, cancellationToken);
        RegistryJson bounded;
        try
        {
            bounded = RegistryJson.FromElement(value, new RegistryJsonLimits
            {
                MaxBytes = budget.Limits.MaxObjectBytes,
                MaxDepth = budget.Limits.MaxJsonDepth,
                MaxNodes = (int)Math.Min(int.MaxValue, budget.Limits.MaxWork)
            });
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return JsonNode.Parse(bounded.RootElement.GetRawText(),
            documentOptions: new() { MaxDepth = budget.Limits.MaxJsonDepth });
    }

    private readonly record struct TargetKey(NativeRegistryContext Context, int? MutableSourceId,
        string GroupXid, string ResourceXid, string? VersionId);

    private sealed class CapturedSource
    {
        private readonly IFederationReadSource _source;

        internal CapturedSource(IFederationReadSource source, int id)
        {
            _source = source;
            Id = id;
            Context = source.Context;
            Model = source.Model;
            Owner = FederationCapabilities.GetResolutionOwner(source.Capabilities);
        }

        internal int Id { get; }
        internal NativeRegistryContext Context { get; }
        internal RegistryModel? Model { get; }
        internal FederationResolutionOwner Owner { get; }

        internal void Check(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_source.Context != Context || !ReferenceEquals(_source.Model, Model) ||
                FederationCapabilities.GetResolutionOwner(_source.Capabilities) != Owner)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "A captured URI identity's source context, model or resolution owner changed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
