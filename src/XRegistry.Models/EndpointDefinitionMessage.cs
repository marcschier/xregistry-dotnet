// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

/// <summary>A selected Message definition, not a materialized inheritance chain or a validated runtime message.</summary>
public sealed class EndpointDefinitionMessage
{
    internal EndpointDefinitionMessage(string messageId, string groupReference, RegistryJson metadata)
    {
        MessageId = messageId;
        GroupReference = groupReference;
        Metadata = metadata;
    }

    /// <summary>Gets the exact collection key, without inferring a runtime event discriminator.</summary>
    public string MessageId { get; }

    /// <summary>Gets the exact referenced Group URI, or the empty string for the inline collection.</summary>
    public string GroupReference { get; }

    /// <summary>Gets independently owned metadata without Endpoint substitution inside Message definitions.</summary>
    public RegistryJson Metadata { get; }
}
