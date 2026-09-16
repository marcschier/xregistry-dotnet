// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Federation;

/// <summary>Representation-scoped HTTP evidence, never a Registry-wide revision or credential.</summary>
public sealed record HttpFederationObservation
{
    /// <summary>Gets the exact request URI, including view/query selection.</summary>
    public required Uri RequestUri { get; init; }
    /// <summary>Gets the original HTTP status.</summary>
    public required int StatusCode { get; init; }
    /// <summary>Gets the observation time, not an entity timestamp.</summary>
    public required DateTimeOffset RetrievedAt { get; init; }
    /// <summary>Gets the unchanged opaque ETag, including its weak marker if present.</summary>
    public string? ETag { get; init; }
    /// <summary>Gets the HTTP last-modification date when present.</summary>
    public DateTimeOffset? LastModified { get; init; }
    /// <summary>Gets the cache policy; this reader does not enable shared caching.</summary>
    public string? CacheControl { get; init; }
    /// <summary>Gets fields that distinguish cached representations.</summary>
    public string? Vary { get; init; }
    /// <summary>Gets the original ordered content-coding list.</summary>
    public string? ContentEncoding { get; init; }
    /// <summary>Gets the selected XID for this representation when applicable.</summary>
    public string? SelectedXid { get; init; }
}
