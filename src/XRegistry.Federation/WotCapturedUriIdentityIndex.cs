// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

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

    /// <summary>Captures the URI identities supplied by the already selected Registry views.</summary>
    public WotCapturedUriIdentityIndex(IEnumerable<WotCapturedUriIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        _identities = identities.ToArray();
        if (_identities.Any(static identity => identity is null))
        {
            throw new ArgumentException("Captured URI identities cannot contain null entries.", nameof(identities));
        }
    }

    /// <summary>Returns the unique captured Resource or exact Version identified by an absolute URI.</summary>
    /// <exception cref="FederationException">No identity matches, or distinct targets share the URI.</exception>
    public WotCapturedResourceIdentity Resolve(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Fragment.Length != 0)
        {
            throw FederationJson.Invalid("A captured identity lookup requires an absolute fragment-free URI.");
        }

        var matches = _identities.Where(identity => identity.Uri == uri).ToArray();
        if (matches.Length == 0)
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "No captured Registry identity matches the resolved document URI.");
        }

        var targets = new Dictionary<TargetKey, WotCapturedResourceIdentity>();
        foreach (var match in matches)
        {
            var target = match.Target;
            var key = new TargetKey(FederationJson.AbsoluteUri(target.Source.Context.Source).AbsoluteUri,
                target.GroupXid, target.ResourceXid, target.VersionId);
            if (targets.TryGetValue(key, out var prior))
            {
                if (prior.ResourceKind != target.ResourceKind || prior.WotId != target.WotId ||
                    prior.ResourceCollection != target.ResourceCollection)
                {
                    throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                        "Duplicate captured records disagree about one Resource or Version identity.");
                }
                continue;
            }
            targets.Add(key, target);
        }

        if (targets.Count != 1)
        {
            throw new FederationException(FederationErrorCode.Ambiguous,
                "Several distinct captured Resources or Versions match the resolved document URI.");
        }
        return targets.Values.Single();
    }

    private readonly record struct TargetKey(string Source, string GroupXid, string ResourceXid, string? VersionId);
}
