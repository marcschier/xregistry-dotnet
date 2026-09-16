// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;

namespace XRegistry.Models;

/// <summary>A materialized definition, its original input, and explicit traversal/completeness evidence.</summary>
public sealed class MessageMaterializationResult
{
    internal MessageMaterializationResult(MessageDefinition original, RegistryJson metadata,
        IReadOnlyList<MessageDefinition> definitions, MessageDefinitionReference? unresolvedBase = null,
        MessageDefinitionSourceStatus? unresolvedStatus = null,
        IReadOnlyDictionary<string, MessageDefinition>? propertySources = null,
        IReadOnlyList<RegistryValidationObligation>? obligations = null,
        IReadOnlyList<MessageDefinitionReference>? references = null)
    {
        Original = original;
        Metadata = metadata;
        Definitions = definitions;
        UnresolvedBase = unresolvedBase;
        UnresolvedStatus = unresolvedStatus;
        Constraints = new(metadata);
        PropertySources = propertySources ?? new ReadOnlyDictionary<string, MessageDefinition>(
            new Dictionary<string, MessageDefinition>(StringComparer.Ordinal));
        Obligations = obligations ?? Array.Empty<RegistryValidationObligation>();
        References = references ?? Array.Empty<MessageDefinitionReference>();
    }

    /// <summary>The original authored definition and source identity, never replaced by its base.</summary>
    public MessageDefinition Original { get; }
    /// <summary>The owned composed metadata; an incomplete result includes only the available chain.</summary>
    public RegistryJson Metadata { get; }
    /// <summary>The effective envelope/protocol/payload views, not a transport implementation.</summary>
    public MessageDefinitionConstraints Constraints { get; }
    /// <summary>Source context for each materialized JSON pointer; relative URI values keep their declaring source's base.</summary>
    public IReadOnlyDictionary<string, MessageDefinition> PropertySources { get; }
    /// <summary>Explicit schema or external-identity obligations that this metadata-only materializer does not acquire.</summary>
    public IReadOnlyList<RegistryValidationObligation> Obligations { get; }
    /// <summary>The actual definitions traversed, ordered from the authored root toward its oldest available base.</summary>
    public IReadOnlyList<MessageDefinition> Definitions { get; }
    /// <summary>The requested edges, including original reference spelling and resolved request URI; actual sources are retained separately.</summary>
    public IReadOnlyList<MessageDefinitionReference> References { get; }
    /// <summary>Whether the complete base chain was available and materialized.</summary>
    public bool IsComplete => UnresolvedBase is null;
    /// <summary>The first unresolved base, if any; no alternative source was attempted.</summary>
    public MessageDefinitionReference? UnresolvedBase { get; }
    /// <summary>The explicit reason why the base chain is incomplete.</summary>
    public MessageDefinitionSourceStatus? UnresolvedStatus { get; }
}
