// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>A descriptive reference and its explicit resolution state, not an access authorization.</summary>
public sealed record CatalogDescriptiveReference
{
    internal CatalogDescriptiveReference(string originalValue, string? resolvedValue)
    {
        OriginalValue = originalValue;
        ResolvedValue = resolvedValue;
    }

    /// <summary>Gets the original, unchanged catalog attribute value.</summary>
    public string OriginalValue { get; }
    /// <summary>Gets an absolute value, or null when a relative value has no explicit catalog-root context.</summary>
    public string? ResolvedValue { get; }
}

/// <summary>Pure catalog-root-directory resolution for weburl and authority, without endpoint discovery.</summary>
public static class CatalogDescriptiveReferences
{
    /// <summary>Resolves a supplied descriptive attribute, retaining absent and unresolved states explicitly.</summary>
    /// <remarks>
    /// Only weburl and authority use these rules. Absolute values retain their exact spelling;
    /// relative values require an explicitly supplied credential-free HTTP(S) catalog Registry root.
    /// No description-Version URL, advertised endpoint or storage path is inferred. The inclusive
    /// byte limit applies independently to the original value, supplied root and resolved value.
    /// Resolving or displaying a value does not authorize its acquisition.
    /// </remarks>
    public static CatalogDescriptiveReference? Resolve(CatalogDescription description, string attribute,
        Uri? catalogRegistryRoot = null, int maxUtf8Bytes = 16_384, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (attribute is not ("weburl" or "authority"))
        {
            throw new ArgumentException("Only catalog weburl and authority are descriptive URI references.", nameof(attribute));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(maxUtf8Bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!description.Data.TryGetProperty(attribute, out var field)) { return null; }
        var value = field.GetString()!;
        CheckBytes(value, maxUtf8Bytes);
        var colon = value.IndexOf(':');
        if (colon > 0 && Uri.CheckSchemeName(value[..colon]))
        {
            return new(value, value);
        }
        if (catalogRegistryRoot is null) { return new(value, null); }
        CheckBytes(catalogRegistryRoot.OriginalString, maxUtf8Bytes);
        try
        {
            RegistryDomainRules.ValidateCatalogAdvertisement(RegistryJson.Parse(new JsonObject
            {
                ["name"] = "http",
                ["endpoint"] = catalogRegistryRoot.OriginalString,
            }.ToJsonString()).RootElement, cancellationToken);
        }
        catch (RegistryException exception) { throw FederationJson.FromCore(exception); }
        var root = catalogRegistryRoot.AbsoluteUri.EndsWith('/') ? catalogRegistryRoot :
            new Uri(catalogRegistryRoot.AbsoluteUri + "/", UriKind.Absolute);
        var resolved = new Uri(root, value).AbsoluteUri;
        cancellationToken.ThrowIfCancellationRequested();
        CheckBytes(resolved, maxUtf8Bytes);
        return new(value, resolved);
    }

    private static void CheckBytes(string value, int limit)
    {
        if (value.Length > limit || Encoding.UTF8.GetByteCount(value) > limit)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The catalog descriptive-reference byte limit was exceeded.");
        }
    }
}
