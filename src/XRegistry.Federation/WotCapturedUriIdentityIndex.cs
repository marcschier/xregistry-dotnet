// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

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

/// <summary>Resolves external URI identities without consulting bindings or live Registry state.</summary>
public sealed class WotCapturedUriIdentityIndex
{
    private readonly WotCapturedUriIdentity[] _identities;
    private readonly Dictionary<IFederationReadSource, CapturedSource> _sources =
        new(ReferenceEqualityComparer.Instance);

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
