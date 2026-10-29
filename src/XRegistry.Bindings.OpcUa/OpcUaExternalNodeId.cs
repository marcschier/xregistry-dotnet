// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>An ExpandedNodeId resolved from source table indexes to exact OPC UA URI identity.</summary>
public sealed class OpcUaExternalNodeId
{
    private readonly object identifier;

    /// <summary>Creates a URI-based external NodeId identity.</summary>
    public OpcUaExternalNodeId(string serverApplicationUri, string namespaceUri, IdType identifierType,
        object identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverApplicationUri);
        ArgumentException.ThrowIfNullOrEmpty(namespaceUri);
        ArgumentNullException.ThrowIfNull(identifier);
        var expectedType = identifierType switch
        {
            IdType.Numeric => typeof(uint),
            IdType.String => typeof(string),
            IdType.Guid => typeof(Guid),
            IdType.Opaque => typeof(byte[]),
            _ => throw new ArgumentOutOfRangeException(nameof(identifierType))
        };
        if (identifier.GetType() != expectedType)
        {
            throw new ArgumentException("The identifier value does not match its OPC UA identifier type.",
                nameof(identifier));
        }

        ServerApplicationUri = serverApplicationUri;
        NamespaceUri = namespaceUri;
        IdentifierType = identifierType;
        this.identifier = CloneIdentifier(identifier);
    }

    /// <summary>The exact Application URI selected from the source ServerArray.</summary>
    public string ServerApplicationUri { get; }

    /// <summary>The exact namespace URI selected from the source NamespaceArray or ExpandedNodeId.</summary>
    public string NamespaceUri { get; }

    /// <summary>The OPC UA identifier discriminator.</summary>
    public IdType IdentifierType { get; }

    /// <summary>The identifier value; opaque byte strings are returned as copies.</summary>
    public object Identifier => CloneIdentifier(identifier);

    /// <summary>Maps the URI identity to a verified target Session without reusing source table indexes.</summary>
    public NodeId MapToLocalNodeId(string targetServerApplicationUri, NamespaceTable namespaceUris)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetServerApplicationUri);
        ArgumentNullException.ThrowIfNull(namespaceUris);
        if (!string.Equals(ServerApplicationUri, targetServerApplicationUri, StringComparison.Ordinal))
        {
            throw new FederationException(FederationErrorCode.PolicyDenied,
                "The connected target Server Application URI does not match the external NodeId identity.");
        }

        var localIdentifier = IdentifierType switch
        {
            IdType.Numeric => new NodeId((uint)identifier, 0),
            IdType.String => new NodeId((string)identifier, 0),
            IdType.Guid => new NodeId((Guid)identifier, 0),
            IdType.Opaque => new NodeId(ByteString.From((byte[])identifier), 0),
            _ => throw new FederationException(FederationErrorCode.InvalidPackage,
                "The external OPC UA identifier type is not supported.")
        };
        var expanded = new ExpandedNodeId(localIdentifier, NamespaceUri, 0u);
        var node = ExpandedNodeId.ToNodeId(expanded, namespaceUris, updateNamespaceTable: false);
        if (node.IsNull)
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "The external namespace URI is absent from the target Server NamespaceArray.");
        }

        return node;
    }

    internal static object CloneIdentifier(object value) =>
        value is byte[] bytes ? (byte[])bytes.Clone() : value;
}
