// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

/// <summary>Identifies a built-in Registry model source.</summary>
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
    OpenUsd,

    /// <summary>The API Registry model for versioned synchronous API definitions.</summary>
    Api,

    /// <summary>Publication channels, immutable objects, and snapshots.</summary>
    Publication,

    /// <summary>WoT Thing Models and Thing Descriptions.</summary>
    Wot,

    /// <summary>The opt-in Media Endpoints working-draft model.</summary>
    Media
}
