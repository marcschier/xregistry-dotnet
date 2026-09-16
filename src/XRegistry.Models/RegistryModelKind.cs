// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

/// <summary>Identifies a model source included in the pinned specification baseline.</summary>
public enum RegistryModelKind
{
    /// <summary>The core registry attributes.</summary>
    Core,

    /// <summary>Application endpoint definitions.</summary>
    Endpoint,

    /// <summary>Message definitions.</summary>
    Message,

    /// <summary>Schema document registries.</summary>
    Schema,

    /// <summary>The composite CloudEvents model source.</summary>
    CloudEvents,

    /// <summary>The Registry-of-Registries catalog model.</summary>
    Registry,

    /// <summary>OpenUSD asset and codeless schema-plugin metadata.</summary>
    OpenUsd
}
