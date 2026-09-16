// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Federation;

/// <summary>Explicit capture policy for one bounded live HTTP source session.</summary>
public sealed record HttpFederationReadOptions
{
    /// <summary>Gets shared cumulative read budgets.</summary>
    public FederationReadLimits Limits { get; init; } = new();
    /// <summary>Gets the entire session deadline, including time between dependent reads.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Gets whether an immutable producer pin is required; ordinary live HTTP cannot provide one.</summary>
    public bool RequireImmutableSnapshot { get; init; }
    /// <summary>Gets the clock used to record retrieval evidence.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Gets the explicitly authorized, caller-owned resolver for model-source includes; null prohibits acquisition.</summary>
    /// <remarks>Core's synchronous resolver contract applies. Returned documents are charged to this session's budgets; the resolver must impose its own I/O deadline.</remarks>
    public IRegistryModelResolver? ModelResolver { get; init; }
}
