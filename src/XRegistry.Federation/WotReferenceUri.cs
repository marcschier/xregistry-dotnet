// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Federation;

/// <summary>A WoT reference after resolution against its captured effective base URI.</summary>
public sealed record WotReferenceUri(Uri DocumentUri, string? Fragment, bool IsDocumentLocal);

/// <summary>Resolves WoT URI-references and classifies references to the containing document.</summary>
public static class WotReferenceUriResolver
{
    /// <summary>Resolves a reference and compares its fragment-free target with the containing document URI.</summary>
    /// <param name="reference">The authored URI-reference, including any fragment.</param>
    /// <param name="effectiveBaseUri">The absolute base URI captured for the reference occurrence.</param>
    /// <param name="containingDocumentUri">The absolute URI identifying the containing document.</param>
    /// <returns>The resolved fragment-free document URI, original resolved fragment and locality classification.</returns>
    /// <exception cref="FederationException">A URI is malformed, relative without an absolute base, or embeds credentials.</exception>
    public static WotReferenceUri Resolve(string reference, Uri effectiveBaseUri, Uri containingDocumentUri)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(effectiveBaseUri);
        ArgumentNullException.ThrowIfNull(containingDocumentUri);

        FederationJson.UriReference(reference);
        var baseUri = FederationJson.AbsoluteUri(effectiveBaseUri.AbsoluteUri);
        var containingUri = FederationJson.AbsoluteUri(containingDocumentUri.AbsoluteUri);
        if (!Uri.TryCreate(baseUri, reference, out var resolved))
        {
            throw FederationJson.Invalid("A WoT URI-reference could not be resolved against its captured absolute base.");
        }

        return ResolveTarget(resolved, containingUri);
    }

    /// <summary>Classifies an absolute resolved target against the containing document URI.</summary>
    /// <param name="resolvedTarget">The absolute target URI, including any fragment.</param>
    /// <param name="containingDocumentUri">The absolute URI identifying the containing document.</param>
    /// <returns>The resolved fragment-free document URI, fragment and locality classification.</returns>
    public static WotReferenceUri ResolveTarget(Uri resolvedTarget, Uri containingDocumentUri)
    {
        ArgumentNullException.ThrowIfNull(resolvedTarget);
        ArgumentNullException.ThrowIfNull(containingDocumentUri);
        if (!resolvedTarget.IsAbsoluteUri)
        {
            throw FederationJson.Invalid("A resolved WoT reference target must be absolute.");
        }

        var absolute = FederationJson.AbsoluteUri(resolvedTarget.AbsoluteUri);
        var containingUri = FederationJson.AbsoluteUri(containingDocumentUri.AbsoluteUri);
        var fragment = absolute.Fragment.Length == 0 ? null : absolute.Fragment;
        var documentText = fragment is null
            ? absolute.AbsoluteUri
            : absolute.AbsoluteUri[..^fragment.Length];
        var documentUri = FederationJson.AbsoluteUri(documentText);
        var containingFragment = containingUri.Fragment;
        if (containingFragment.Length != 0)
        {
            containingUri = FederationJson.AbsoluteUri(containingUri.AbsoluteUri[..^containingFragment.Length]);
        }

        return new(documentUri, fragment, documentUri == containingUri);
    }

}
