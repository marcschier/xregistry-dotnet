// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;

namespace XRegistry;

/// <summary>A core HTTP path shape relative to a configured Registry root.</summary>
public enum RegistryPathKind
{
    /// <summary>The Registry root.</summary>
    Registry,
    /// <summary>The effective model.</summary>
    Model,
    /// <summary>The model source.</summary>
    ModelSource,
    /// <summary>The enabled capabilities.</summary>
    Capabilities,
    /// <summary>The offered capabilities.</summary>
    CapabilitiesOffered,
    /// <summary>The export operation.</summary>
    Export,
    /// <summary>The .xregistry discovery document.</summary>
    Discovery,
    /// <summary>A model-defined Groups collection.</summary>
    GroupCollection,
    /// <summary>A Group entity.</summary>
    Group,
    /// <summary>A model-defined Resources collection.</summary>
    ResourceCollection,
    /// <summary>A Resource's default Version projection.</summary>
    Resource,
    /// <summary>A Resource Meta entity.</summary>
    Meta,
    /// <summary>A Resource's Versions collection.</summary>
    VersionCollection,
    /// <summary>A specific Version.</summary>
    Version
}

/// <summary>
/// An immutable escaped protocol path. Parsing never uses URI/filesystem normalization,
/// decodes each segment once, and preserves the original escaped spelling.
/// </summary>
public sealed record RegistryPath
{
    private RegistryPath(string escapedPath, RegistryPathKind kind, string? groupType = null,
        RegistryId? groupId = null, string? resourceType = null, RegistryId? resourceId = null,
        RegistryId? versionId = null, bool details = false)
    {
        EscapedPath = escapedPath;
        Kind = kind;
        GroupType = groupType;
        GroupId = groupId;
        ResourceType = resourceType;
        ResourceId = resourceId;
        VersionId = versionId;
        IsDetails = details;
    }

    /// <summary>Gets the original escaped path, including any literal $details suffix.</summary>
    public string EscapedPath { get; }
    /// <summary>Gets the syntactic route kind; model membership is a separate check.</summary>
    public RegistryPathKind Kind { get; }
    /// <summary>Gets the decoded plural Group type name, if present.</summary>
    public string? GroupType { get; }
    /// <summary>Gets the decoded Group identifier, if present.</summary>
    public RegistryId? GroupId { get; }
    /// <summary>Gets the decoded plural Resource type name, if present.</summary>
    public string? ResourceType { get; }
    /// <summary>Gets the decoded Resource identifier, if present.</summary>
    public RegistryId? ResourceId { get; }
    /// <summary>Gets the decoded Version identifier, if present.</summary>
    public RegistryId? VersionId { get; }
    /// <summary>Gets whether the escaped input ends in literal $details (not an encoded dollar sign).</summary>
    public bool IsDetails { get; }

    /// <summary>Parses a root-relative escaped core HTTP path without a query, fragment, trailing slash or mount prefix.</summary>
    /// <exception cref="RegistryException">The path, ID, encoding or suffix placement is invalid.</exception>
    public static RegistryPath Parse(string escapedPath)
    {
        ArgumentNullException.ThrowIfNull(escapedPath);
        if (escapedPath.Length > 8192 || !escapedPath.StartsWith('/') ||
            escapedPath.Contains('?', StringComparison.Ordinal) || escapedPath.Contains('#', StringComparison.Ordinal))
        {
            throw Diagnostics.Error("malformed_path", "", "A bounded root-relative protocol path without a query or fragment is required.");
        }

        if (escapedPath == "/")
        {
            return new RegistryPath(escapedPath, RegistryPathKind.Registry);
        }

        var details = escapedPath.EndsWith("$details", StringComparison.Ordinal);
        var path = details ? escapedPath[..^8] : escapedPath;
        var rawSegments = path[1..].Split('/');
        if (rawSegments.Length > 6 || rawSegments.Any(static segment => segment.Length == 0))
        {
            throw Diagnostics.Error("malformed_path", "", "The path has an unsupported shape or an empty segment.");
        }

        if (details && rawSegments.Length is not (4 or 6))
        {
            throw Diagnostics.Error("bad_details", "", "The details suffix is only valid for Resources and Versions.");
        }

        var segments = rawSegments.Select(DecodeSegment).ToArray();
        if (segments.Length == 1)
        {
            var special = segments[0] switch
            {
                "model" => RegistryPathKind.Model,
                "modelsource" => RegistryPathKind.ModelSource,
                "capabilities" => RegistryPathKind.Capabilities,
                "capabilitiesoffered" => RegistryPathKind.CapabilitiesOffered,
                "export" => RegistryPathKind.Export,
                ".xregistry" => RegistryPathKind.Discovery,
                _ => RegistryPathKind.GroupCollection
            };
            if (special != RegistryPathKind.GroupCollection)
            {
                return new RegistryPath(escapedPath, special);
            }
        }

        CheckTypeName(segments[0]);
        var groupId = segments.Length >= 2 ? RegistryId.Parse(segments[1]) : null;
        if (segments.Length >= 3)
        {
            CheckTypeName(segments[2]);
        }

        var resourceId = segments.Length >= 4 ? RegistryId.Parse(segments[3]) : null;
        var kind = segments.Length switch
        {
            1 => RegistryPathKind.GroupCollection,
            2 => RegistryPathKind.Group,
            3 => RegistryPathKind.ResourceCollection,
            4 => RegistryPathKind.Resource,
            5 when segments[4] == "meta" => RegistryPathKind.Meta,
            5 when segments[4] == "versions" => RegistryPathKind.VersionCollection,
            6 when segments[4] == "versions" => RegistryPathKind.Version,
            _ => throw Diagnostics.Error("malformed_path", "", "The path does not match a core route.")
        };
        return new RegistryPath(escapedPath, kind, segments[0], groupId,
            segments.Length >= 3 ? segments[2] : null, resourceId,
            segments.Length == 6 ? RegistryId.Parse(segments[5]) : null, details);
    }

    /// <summary>Builds a Group path, escaping identifiers without converting them to filesystem names.</summary>
    public static RegistryPath ForGroup(string groupType, RegistryId groupId)
    {
        ArgumentNullException.ThrowIfNull(groupType);
        ArgumentNullException.ThrowIfNull(groupId);
        CheckTypeName(groupType);
        return Parse("/" + groupType + "/" + Uri.EscapeDataString(groupId.Value));
    }

    /// <summary>Builds a Resource path. The caller decides whether a document-bearing type needs $details.</summary>
    public static RegistryPath ForResource(string groupType, RegistryId groupId,
        string resourceType, RegistryId resourceId, bool details = false)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(resourceId);
        CheckTypeName(resourceType);
        return Parse(ForGroup(groupType, groupId).EscapedPath + "/" + resourceType + "/" +
            Uri.EscapeDataString(resourceId.Value) + (details ? "$details" : ""));
    }

    /// <summary>Builds a Version path, escaping the Version ID exactly once.</summary>
    public static RegistryPath ForVersion(string groupType, RegistryId groupId,
        string resourceType, RegistryId resourceId, RegistryId versionId, bool details = false)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        return Parse(ForResource(groupType, groupId, resourceType, resourceId).EscapedPath +
            "/versions/" + Uri.EscapeDataString(versionId.Value) + (details ? "$details" : ""));
    }

    /// <summary>Gets the preserved escaped entity XID without $details. Collections and administrative routes have no XID.</summary>
    public string ToXid()
    {
        if (Kind is not (RegistryPathKind.Registry or RegistryPathKind.Group or RegistryPathKind.Resource
            or RegistryPathKind.Meta or RegistryPathKind.Version))
        {
            throw Diagnostics.Error("malformed_xid", "", "Only an entity path has an XID.");
        }

        return IsDetails ? EscapedPath[..^8] : EscapedPath;
    }

    /// <inheritdoc />
    public override string ToString() => EscapedPath;

    private static void CheckTypeName(string name)
    {
        if (!RegistryNames.IsAttribute(name, maxLength: 57))
        {
            throw Diagnostics.Error("malformed_path", "", "A plural type name must satisfy the model naming grammar.");
        }
    }

    internal static string DecodeSegment(string segment)
    {
        var bytes = new byte[segment.Length];
        var count = 0;
        for (var index = 0; index < segment.Length; index++)
        {
            var character = segment[index];
            if (character == '%')
            {
                if (index + 2 >= segment.Length || Hex(segment[index + 1]) < 0 || Hex(segment[index + 2]) < 0)
                {
                    throw Diagnostics.Error("malformed_path", "", "A path segment has an invalid percent escape.");
                }

                bytes[count++] = (byte)(Hex(segment[index + 1]) * 16 + Hex(segment[index + 2]));
                index += 2;
            }
            else if (character is <= '\x7f' and not '\\')
            {
                bytes[count++] = (byte)character;
            }
            else
            {
                throw Diagnostics.Error("malformed_path", "", "Protocol path segments must use ASCII URI syntax.");
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException exception)
        {
            throw new RegistryException(new("malformed_path", "", "A path segment contains invalid UTF-8."), exception);
        }
    }

    private static int Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };
}
