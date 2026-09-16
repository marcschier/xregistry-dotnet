// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Numerics;
using System.Text.Json;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>A caller's decision about an advertisement before endpoint access.</summary>
public enum AdvertisementAccess
{
    /// <summary>The candidate may participate in selection.</summary>
    Allow,
    /// <summary>Exclude this candidate as permitted by the caller's selection policy.</summary>
    Exclude,
    /// <summary>Reject the operation; do not fall back.</summary>
    Deny,
}

/// <summary>Explicit caller choices applied before advertisement priority.</summary>
public sealed class AdvertisementSelectionOptions
{
    /// <summary>Snapshots supported names and optional caller choices. Names are case-sensitive.</summary>
    public AdvertisementSelectionOptions(IEnumerable<string> supportedProfiles, string? profileName = null,
        int? originalIndex = null, Func<CatalogAdvertisement, AdvertisementAccess>? accessPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(supportedProfiles);
        ArgumentOutOfRangeException.ThrowIfNegative(originalIndex ?? 0);
        SupportedProfiles = supportedProfiles.ToFrozenSet(StringComparer.Ordinal);
        ProfileName = profileName;
        OriginalIndex = originalIndex;
        AccessPolicy = accessPolicy;
    }

    /// <summary>The exact implemented profile names, not names inferred from URI schemes.</summary>
    public IReadOnlySet<string> SupportedProfiles { get; }
    /// <summary>An optional exact profile choice.</summary>
    public string? ProfileName { get; }
    /// <summary>An optional original array position; the implicit HTTP position is the array length.</summary>
    public int? OriginalIndex { get; }
    /// <summary>Caller policy, evaluated before endpoint access.</summary>
    public Func<CatalogAdvertisement, AdvertisementAccess>? AccessPolicy { get; }
}

/// <summary>A detached access advertisement, preserving spelling, order, and explicit parameters.</summary>
public sealed class CatalogAdvertisement
{
    internal CatalogAdvertisement(JsonElement data, int index, bool isImplicit)
    {
        Name = data.GetProperty("name").GetString()!;
        Endpoint = data.GetProperty("endpoint").GetString()!;
        Priority = data.TryGetProperty("priority", out _) ? FederationJson.Priority(data) : 0;
        if (data.TryGetProperty("parameters", out var parameters))
        {
            Parameters = parameters.Clone();
        }
        else
        {
            Parameters = JsonSerializer.SerializeToElement(new Dictionary<string, string>(),
                FederationSerializationContext.Default.DictionaryStringString);
        }
        OriginalIndex = index;
        IsImplicit = isImplicit;
        Data = data.Clone();
    }

    /// <summary>Exact, case-sensitive discriminator.</summary>
    public string Name { get; }
    /// <summary>Original endpoint text, not URI-normalized identity.</summary>
    public string Endpoint { get; }
    /// <summary>Effective priority; an absent serialized priority is zero.</summary>
    public BigInteger Priority { get; }
    /// <summary>Original array position (or appended implicit position).</summary>
    public int OriginalIndex { get; }
    /// <summary>Whether this candidate came from xregurl.</summary>
    public bool IsImplicit { get; }
    /// <summary>Detached parameters. An absent parameters field yields an empty object.</summary>
    public JsonElement Parameters { get; }
    /// <summary>Detached advertisement metadata, without injected defaults.</summary>
    public JsonElement Data { get; }

    /// <summary>Validates the selected built-in binding's syntax, without accessing an endpoint.</summary>
    public void ValidateBinding()
    {
        var allowed = Name switch
        {
            "http" => Array.Empty<string>(),
            "git" => ["revision", "path"],
            "oci" => ["reference"],
            "file" => ["layout", "reference"],
            _ => null,
        };
        if (allowed is null)
        {
            return;
        }
        if (Parameters.EnumerateObject().Any(parameter => !allowed.Contains(parameter.Name, StringComparer.Ordinal)))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "Unknown selected profile parameter.");
        }
        var uri = FederationJson.AbsoluteUri(Endpoint);
        if (Endpoint.Contains('?', StringComparison.Ordinal) || Endpoint.Contains('#', StringComparison.Ordinal))
        {
            throw FederationJson.Invalid("A binding endpoint must not contain a query or fragment.");
        }
        if (Name == "git")
        {
            if (uri.Scheme != "https" || uri.Host.Length == 0)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "Only HTTPS Git repository advertisements are supported.");
            }
            GitBindingSyntax.ValidateRevision(FederationJson.String(Parameters, "revision"));
            if (Parameters.TryGetProperty("path", out _))
            {
                GitBindingSyntax.ValidateRootPath(FederationJson.String(Parameters, "path", true));
            }
        }
        else if (Name == "file" && Parameters.TryGetProperty("layout", out var layout) &&
            layout.ValueKind == JsonValueKind.String && layout.GetString() == "document-tree" &&
            Parameters.TryGetProperty("reference", out _))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "A document tree has no tag selector.");
        }
        try
        {
            RegistryDomainRules.ValidateCatalogAdvertisement(Data);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
    }
}

/// <summary>One selected catalog-description Version, never a merge of Versions.</summary>
public sealed class CatalogDescription
{
    private CatalogDescription(JsonElement data, CancellationToken cancellationToken = default)
    {
        Data = data;
        try
        {
            RegistryDomainRules.ValidateCatalogDescription(data, cancellationToken);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
        var advertisements = new List<CatalogAdvertisement>();
        if (data.TryGetProperty("federationprofiles", out var profiles))
        {
            foreach (var profile in profiles.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                advertisements.Add(new CatalogAdvertisement(profile, advertisements.Count, false));
            }
        }
        Advertisements = advertisements.AsReadOnly();
        if (data.TryGetProperty("xregurl", out var xregurl))
        {
            XregUrl = xregurl.GetString();
        }
    }

    /// <summary>Detached original Version metadata.</summary>
    public JsonElement Data { get; }
    /// <summary>The original explicit array, including duplicates and unsupported names.</summary>
    public IReadOnlyList<CatalogAdvertisement> Advertisements { get; }
    /// <summary>Original HTTP root text, if supplied.</summary>
    public string? XregUrl { get; }

    /// <summary>Parses common advertisement fields before filtering or binding interpretation.</summary>
    public static CatalogDescription Parse(ReadOnlyMemory<byte> utf8Json, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default) =>
        new(FederationJson.Parse(utf8Json, budget, cancellationToken), cancellationToken);

    /// <summary>Selects exactly the explicit or Meta-default description from a materialized Resource.</summary>
    public static CatalogDescription FromResource(JsonElement resource, string? descriptionVersion = null)
    {
        FederationJson.RequireObject(resource);
        FederationJson.ValidateKeys(resource);
        var meta = FederationJson.Required(resource, "meta");
        FederationJson.RequireObject(meta);
        if (meta.TryGetProperty("xref", out _))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "Resolve a catalog alias through its owning Registry before selecting its description.",
                "cannot_doc_xref");
        }
        var version = descriptionVersion ?? FederationJson.String(meta, "defaultversionid");
        if (!FederationSyntax.Id(version) || version is "null" or "request")
        {
            throw FederationJson.Invalid("Invalid catalog-description Version ID.");
        }
        var versions = FederationJson.Required(resource, "versions");
        FederationJson.RequireObject(versions);
        if (!versions.TryGetProperty(version, out var selected))
        {
            throw new FederationException(FederationErrorCode.NotFound, "Catalog-description Version not found.");
        }
        if (FederationJson.String(selected, "versionid") != version)
        {
            throw FederationJson.Invalid("Catalog-description Version identity mismatch.");
        }
        return new CatalogDescription(selected.Clone());
    }

    /// <summary>Selects one candidate. A selected error never retries another candidate.</summary>
    public CatalogAdvertisement Select(AdvertisementSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var candidates = Advertisements.ToList();
        if (XregUrl is not null)
        {
            var data = JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["name"] = "http", ["endpoint"] = XregUrl },
                FederationSerializationContext.Default.DictionaryStringString);
            candidates.Add(new CatalogAdvertisement(data, candidates.Count, true));
        }
        var eligible = new List<CatalogAdvertisement>();
        var excluded = false;
        foreach (var candidate in candidates)
        {
            if ((options.ProfileName is not null && options.ProfileName != candidate.Name) ||
                (options.OriginalIndex is not null && options.OriginalIndex != candidate.OriginalIndex))
            {
                continue;
            }
            var access = options.AccessPolicy?.Invoke(candidate) ?? AdvertisementAccess.Allow;
            if (access == AdvertisementAccess.Deny)
            {
                throw new FederationException(FederationErrorCode.PolicyDenied, "Advertisement access denied.");
            }
            if (access == AdvertisementAccess.Exclude)
            {
                excluded = true;
                continue;
            }
            if (access != AdvertisementAccess.Allow)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Unknown access-policy decision.");
            }
            if (options.SupportedProfiles.Contains(candidate.Name))
            {
                eligible.Add(candidate);
            }
        }
        var selected = eligible.OrderBy(candidate => candidate.Priority).FirstOrDefault()
            ?? throw new FederationException(excluded ? FederationErrorCode.PolicyDenied :
                FederationErrorCode.UnsupportedBinding, "No eligible supported advertisement.");
        selected.ValidateBinding();
        return selected;
    }
}
