// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Client;

/// <summary>The two discovery locations defined by the core specification.</summary>
public enum RegistryDiscoveryLocation
{
    /// <summary>The configured Registry's .xregistry endpoint, preserving its mount prefix.</summary>
    Registry,

    /// <summary>The hosting origin's /.well-known/xregistry endpoint, outside any Registry mount prefix.</summary>
    Host
}

/// <summary>Discovery advertisements, not authorization to fetch their destinations.</summary>
public sealed class RegistryDiscovery
{
    internal RegistryDiscovery(Uri requestUri, Uri[] registries)
    {
        RequestUri = requestUri;
        Registries = Array.AsReadOnly(registries);
    }

    /// <summary>Gets the selected discovery endpoint.</summary>
    public Uri RequestUri { get; }

    /// <summary>Gets advertised absolute URLs in source order, retaining duplicates and original URI strings.</summary>
    public IReadOnlyList<Uri> Registries { get; }
}
