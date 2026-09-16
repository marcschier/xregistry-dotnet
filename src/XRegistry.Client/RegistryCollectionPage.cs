// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Client;

/// <summary>An independently owned collection page; the response connection is already released.</summary>
public sealed class RegistryCollectionPage
{
    internal RegistryCollectionPage(
        Uri requestUri, JsonElement records, IReadOnlyList<RegistryHttpLink> links, ulong? totalCount)
    {
        RequestUri = requestUri;
        Records = records.Clone();
        Links = links;
        TotalCount = totalCount;
    }

    /// <summary>Gets the actual page request URI, including its unchanged continuation query.</summary>
    public Uri RequestUri { get; }

    /// <summary>Gets the owned object keyed by record IDs; no response/document disposal is required.</summary>
    public JsonElement Records { get; }

    /// <summary>Gets parsed links from this page, without granting permission to fetch them.</summary>
    public IReadOnlyList<RegistryHttpLink> Links { get; }

    /// <summary>Gets the consistent aggregate count advertised so far, or null when not supplied.</summary>
    public ulong? TotalCount { get; }
}
