// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using Opc.Ua;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>A portable source key and remote Node identity for caller-owned durable storage.</summary>
/// <remarks>The local JSON format is not a standard wire representation and does not establish provider trust.</remarks>
public sealed class OpcUaFederationSourcePin
{
    private const int MaxBytes = 64 * 1024;

    /// <summary>Creates a pin without retaining endpoints or Session indexes.</summary>
    public OpcUaFederationSourcePin(OpcUaRegistryOrigin origin, string xid, OpcUaExternalTargetRole role,
        OpcUaExternalNodeId remoteNode)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(remoteNode);
        if (role is not (OpcUaExternalTargetRole.LogicalResource or OpcUaExternalTargetRole.ExactVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        _ = new FederationReadRequest(FederationOperation.Document, xid);
        var parts = xid[1..].Split('/');
        if ((role == OpcUaExternalTargetRole.LogicalResource) != (parts.Length == 4))
        {
            throw Invalid("The pinned XID does not match the declared source role.");
        }
        if (origin.RegistryRoot is { } root &&
            !string.Equals(root.ServerApplicationUri, remoteNode.ServerApplicationUri, StringComparison.Ordinal))
        {
            throw Invalid("The pinned remote Node belongs to a different origin application.");
        }
        Origin = origin;
        Xid = xid;
        Role = role;
        RemoteNode = remoteNode;
    }

    /// <summary>The independently admitted origin when this pin was created.</summary>
    public OpcUaRegistryOrigin Origin { get; }
    /// <summary>The concrete origin-relative Resource or exact Version XID.</summary>
    public string Xid { get; }
    /// <summary>The source's logical or exact role.</summary>
    public OpcUaExternalTargetRole Role { get; }
    /// <summary>The portable remote Node identity, not a local NodeId or endpoint.</summary>
    public OpcUaExternalNodeId RemoteNode { get; }

    /// <summary>Compares the origin/XID/role source key, independently of document bytes and locator spelling.</summary>
    public bool HasSameSource(OpcUaFederationSourcePin other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Origin.Equals(other.Origin) && Role == other.Role &&
            Xid[1..].Split('/').Select(Uri.UnescapeDataString).SequenceEqual(
                other.Xid[1..].Split('/').Select(Uri.UnescapeDataString), StringComparer.Ordinal);
    }

    /// <summary>Rejects replacement of any pinned source or remote Node identity.</summary>
    public void ValidateReconnect(OpcUaFederationSourcePin candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!HasSameSource(candidate) ||
            !OpcUaRegistryOrigin.Application(RemoteNode).Equals(OpcUaRegistryOrigin.Application(candidate.RemoteNode)))
        {
            throw new FederationException(FederationErrorCode.PolicyDenied,
                "The reconnect changes the pinned origin, XID, role or remote Node identity.");
        }
    }

    /// <summary>Writes the bounded, versioned local receipt for caller-owned persistence.</summary>
    public byte[] Serialize()
    {
        var json = new JsonObject
        {
            ["format"] = 1,
            ["originUri"] = Origin.OriginUri,
            ["originRoot"] = Origin.RegistryRoot is { } root ? NodeJson(root) : null,
            ["xid"] = Xid,
            ["role"] = Role == OpcUaExternalTargetRole.LogicalResource ? "resource" : "version",
            ["remoteNode"] = NodeJson(RemoteNode)
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        if (bytes.Length > MaxBytes)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The source pin exceeds its byte budget.");
        }
        return bytes;
    }

    /// <summary>Reads a local receipt without acquiring a provider or treating stored values as new trust configuration.</summary>
    public static OpcUaFederationSourcePin Parse(ReadOnlyMemory<byte> bytes)
    {
        RegistryJson document;
        try
        {
            document = RegistryJson.Parse(bytes.Span, new RegistryJsonLimits { MaxBytes = MaxBytes });
        }
        catch (RegistryException failure)
        {
            throw new FederationException(failure.Diagnostic.Code.EndsWith("_limit", StringComparison.Ordinal)
                ? FederationErrorCode.LimitExceeded : FederationErrorCode.InvalidPackage,
                "The stored source pin is not valid bounded JSON.", failure.Diagnostic.Code, failure);
        }
        var json = document.RootElement;
        Fields(json, "format", "originUri", "originRoot", "xid", "role", "remoteNode");
        if (json.GetProperty("format").ValueKind != JsonValueKind.Number ||
            !json.GetProperty("format").TryGetInt32(out var version) || version != 1)
        {
            throw Invalid("The stored source pin format is unsupported.");
        }
        var uri = json.GetProperty("originUri");
        var root = json.GetProperty("originRoot");
        OpcUaRegistryOrigin origin;
        if (uri.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(uri.GetString()) &&
            root.ValueKind == JsonValueKind.Null)
        {
            origin = OpcUaRegistryOrigin.Stable(uri.GetString()!);
        }
        else if (uri.ValueKind == JsonValueKind.Null && root.ValueKind == JsonValueKind.Object)
        {
            origin = OpcUaRegistryOrigin.Application(ReadNode(root));
        }
        else
        {
            throw Invalid("The stored source pin must contain one origin form.");
        }
        var role = Text(json, "role") switch
        {
            "resource" => OpcUaExternalTargetRole.LogicalResource,
            "version" => OpcUaExternalTargetRole.ExactVersion,
            _ => throw Invalid("The stored source pin role is unsupported.")
        };
        return new(origin, Text(json, "xid"), role, ReadNode(json.GetProperty("remoteNode")));
    }

    private static JsonObject NodeJson(OpcUaExternalNodeId node) => new()
    {
        ["application"] = node.ServerApplicationUri,
        ["namespace"] = node.NamespaceUri,
        ["kind"] = node.IdentifierType.ToString(),
        ["identifier"] = node.IdentifierType switch
        {
            IdType.Numeric => JsonValue.Create((uint)node.Identifier),
            IdType.String => JsonValue.Create((string)node.Identifier),
            IdType.Guid => JsonValue.Create(((Guid)node.Identifier).ToString("D")),
            IdType.Opaque => JsonValue.Create(Convert.ToBase64String((byte[])node.Identifier)),
            _ => throw Invalid("The stored Node identifier kind is unsupported.")
        }
    };

    private static OpcUaExternalNodeId ReadNode(JsonElement json)
    {
        Fields(json, "application", "namespace", "kind", "identifier");
        var kind = Text(json, "kind");
        var value = json.GetProperty("identifier");
        IdType type;
        object identifier;
        switch (kind)
        {
            case "Numeric" when value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number):
                type = IdType.Numeric;
                identifier = number;
                break;
            case "String" when value.ValueKind == JsonValueKind.String:
                type = IdType.String;
                identifier = value.GetString()!;
                break;
            case "Guid" when value.ValueKind == JsonValueKind.String &&
                Guid.TryParseExact(value.GetString(), "D", out var guid):
                type = IdType.Guid;
                identifier = guid;
                break;
            case "Opaque" when value.ValueKind == JsonValueKind.String && value.TryGetBytesFromBase64(out var opaque):
                type = IdType.Opaque;
                identifier = opaque;
                break;
            default:
                throw Invalid("The stored Node identifier does not match its kind.");
        }
        return new(Text(json, "application"), Text(json, "namespace"), type, identifier);
    }

    private static void Fields(JsonElement json, params string[] names)
    {
        if (json.ValueKind != JsonValueKind.Object ||
            !json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw Invalid("The stored source pin has missing, duplicate or unknown fields.");
        }
    }

    private static string Text(JsonElement json, string name) =>
        json.GetProperty(name) is { ValueKind: JsonValueKind.String } value &&
        !string.IsNullOrEmpty(value.GetString()) ? value.GetString()! :
            throw Invalid("A stored source pin identity field is not a nonempty string.");

    private static FederationException Invalid(string message) => new(FederationErrorCode.InvalidPackage, message);
}
