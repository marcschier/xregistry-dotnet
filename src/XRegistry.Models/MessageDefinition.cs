// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

/// <summary>An owned authored Message definition and the caller-established context in which its references are interpreted.</summary>
public sealed class MessageDefinition
{
    /// <summary>Creates a definition without accessing its location or deriving authority from its metadata.</summary>
    public MessageDefinition(RegistryJson metadata, Uri location, Uri? registryRoot = null, RegistryModel? model = null,
        RegistryPath? path = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(location);
        Metadata = metadata;
        Location = location;
        RegistryRoot = registryRoot;
        Model = model;
        Path = path;
    }

    /// <summary>The independently owned original metadata, not a borrowed JsonDocument.</summary>
    public RegistryJson Metadata { get; }
    /// <summary>The caller-established location of this definition, independent of untrusted self/xid fields.</summary>
    public Uri Location { get; }
    /// <summary>The owning Registry root, required to resolve catalog-root Message XIDs.</summary>
    public Uri? RegistryRoot { get; }
    /// <summary>The owning effective model, used to verify local Message Resource/Version types.</summary>
    public RegistryModel? Model { get; }
    /// <summary>The caller-established Resource/Version path when its model contains multiple distinct Message types.</summary>
    public RegistryPath? Path { get; }
}
