// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

/// <summary>A caller-controlled metadata acquisition seam. The callback must authorize each request before accessing its source.</summary>
/// <remarks>The materializer supplies no incoming credentials and performs no network or filesystem acquisition itself.</remarks>
public delegate ValueTask<MessageDefinitionSourceResult> MessageDefinitionSource(
    MessageDefinitionReference reference, CancellationToken cancellationToken);

/// <summary>One requested base definition, retaining authored spelling and the independently resolved target.</summary>
public sealed record MessageDefinitionReference(
    MessageDefinition Source, string ReferenceText, Uri TargetUri, RegistryPath? LocalPath, int Depth)
{
    /// <summary>The maximum accepted source JSON size/shape. An acquiring callback should enforce these limits while reading.</summary>
    public RegistryJsonLimits JsonLimits { get; internal init; } = new();
}

/// <summary>The explicit outcome of a caller's authorized metadata read.</summary>
public enum MessageDefinitionSourceStatus
{
    /// <summary>An owned definition and its actual source context are available.</summary>
    Found,
    /// <summary>The referenced definition is absent.</summary>
    NotFound,
    /// <summary>The selected source cannot currently be read.</summary>
    Unavailable,
    /// <summary>The caller is not authorized to read the selected metadata.</summary>
    AccessDenied,
    /// <summary>No caller-controlled acquisition callback was supplied.</summary>
    SourceNotConfigured
}

/// <summary>An owned result from the explicitly selected source, never a fallback to another source.</summary>
public sealed class MessageDefinitionSourceResult
{
    private MessageDefinitionSourceResult(MessageDefinitionSourceStatus status, MessageDefinition? definition)
    {
        Status = status;
        Definition = definition;
    }

    /// <summary>The explicit acquisition outcome.</summary>
    public MessageDefinitionSourceStatus Status { get; }
    /// <summary>The acquired definition, present only for Found.</summary>
    public MessageDefinition? Definition { get; }

    /// <summary>Returns one acquired definition with its actual Registry/base-URI context.</summary>
    public static MessageDefinitionSourceResult Found(MessageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(MessageDefinitionSourceStatus.Found, definition);
    }

    /// <summary>Reports an unresolved reference without claiming a complete definition.</summary>
    public static MessageDefinitionSourceResult Unresolved(MessageDefinitionSourceStatus status)
    {
        if (!Enum.IsDefined(status) || status == MessageDefinitionSourceStatus.Found)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        return new(status, null);
    }
}
