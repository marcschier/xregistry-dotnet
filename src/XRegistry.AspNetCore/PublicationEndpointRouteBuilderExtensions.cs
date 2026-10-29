// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using XRegistry.Publication;
using XRegistry.Server;

namespace XRegistry.AspNetCore;

/// <summary>Maps the bounded HTTP projection for one already verified Publication snapshot.</summary>
public static class PublicationEndpointRouteBuilderExtensions
{
    /// <summary>The reserved path, relative to the local Registry mount, that serves the open-record context.</summary>
    public const string ContextPath = "/.well-known/xregistry-open";

    /// <summary>The Publication relation URI used to link the projected Registry root to its open record.</summary>
    public const string ContextRelation = "https://xregistry.io/workingdrafts/publication/context";

    /// <summary>
    /// Maps the Registry and serves the exact xregistry-open record retained by a verified snapshot.
    /// The backend must use read-only persistence; the caller must have constructed it from the snapshot's
    /// captured Registry bytes and matching model.
    /// </summary>
    public static IEndpointConventionBuilder MapPublicationSnapshot(this IEndpointRouteBuilder endpoints,
        IReadOnlyRegistryHostBackend backend, PublicationOpenedSnapshot snapshot, RegistryHttpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!backend.IsReadOnly)
        {
            throw new ArgumentException("A Publication snapshot projection requires a read-only backend.", nameof(backend));
        }

        var exactContextBytes = snapshot.OpenRecord.GetExactBytes();
        if (exactContextBytes.Length > backend.Limits.MaxResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot),
                "The open-record context exceeds the backend response byte budget.");
        }

        var publicRoot = backend.PublicRoot;
        if (!publicRoot.IsAbsoluteUri || publicRoot.Scheme is not ("http" or "https") ||
            publicRoot.UserInfo.Length != 0 || publicRoot.Query.Length != 0 || publicRoot.Fragment.Length != 0)
        {
            throw new ArgumentException("The read-only backend must provide a valid public HTTP(S) root.", nameof(backend));
        }

        options ??= new RegistryHttpOptions();
        return endpoints.MapXRegistry(backend, options with
        {
            PublicationContextUri = new Uri(publicRoot.AbsoluteUri.TrimEnd('/') + ContextPath),
            PublicationContextBytes = exactContextBytes
        });
    }
}
