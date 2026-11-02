// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>A portable origin identity, independent of endpoint locators and Session table indexes.</summary>
/// <remarks>Capturing a source value does not establish trust; compare it with an independent provider binding.</remarks>
public sealed class OpcUaRegistryOrigin : IEquatable<OpcUaRegistryOrigin>
{
    private OpcUaRegistryOrigin(string? originUri, OpcUaExternalNodeId? registryRoot)
    {
        OriginUri = originUri;
        RegistryRoot = registryRoot;
    }

    /// <summary>The stable origin URI, or null for the application/Registry-root form.</summary>
    public string? OriginUri { get; }

    /// <summary>The portable Registry-root identity, or null for the stable-URI form.</summary>
    public OpcUaExternalNodeId? RegistryRoot { get; }

    /// <summary>Creates a stable-URI origin without manufacturing a native application/root pair.</summary>
    public static OpcUaRegistryOrigin Stable(string originUri)
    {
        ArgumentException.ThrowIfNullOrEmpty(originUri);
        return new(originUri, null);
    }

    /// <summary>Creates an origin from an application and its portable Registry root.</summary>
    public static OpcUaRegistryOrigin Application(OpcUaExternalNodeId registryRoot)
    {
        ArgumentNullException.ThrowIfNull(registryRoot);
        return new(null, registryRoot);
    }

    /// <summary>Resolves an untrusted source value using only that source Session's table context.</summary>
    public static async ValueTask<OpcUaRegistryOrigin> CaptureAsync(OpcUaRegistryOriginValue value,
        IOpcUaRegistrySession sourceSession, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(sourceSession);
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValidateForm())
        {
            return Stable(value.OriginUri!);
        }

        var root = await sourceSession.MapExternalNodeIdAsync(value.RegistryNodeId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(root.ServerApplicationUri, value.ServerUri, StringComparison.Ordinal))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The origin ServerUri does not match the Registry root's source-table application identity.");
        }
        return Application(root);
    }

    /// <inheritdoc />
    public bool Equals(OpcUaRegistryOrigin? other)
    {
        if (other is null || !string.Equals(OriginUri, other.OriginUri, StringComparison.Ordinal))
        {
            return false;
        }
        if (RegistryRoot is not { } root)
        {
            return other.RegistryRoot is null;
        }
        if (other.RegistryRoot is not { } otherRoot ||
            !string.Equals(root.ServerApplicationUri, otherRoot.ServerApplicationUri, StringComparison.Ordinal) ||
            !string.Equals(root.NamespaceUri, otherRoot.NamespaceUri, StringComparison.Ordinal) ||
            root.IdentifierType != otherRoot.IdentifierType)
        {
            return false;
        }
        return root.Identifier is byte[] bytes
            ? otherRoot.Identifier is byte[] otherBytes && bytes.AsSpan().SequenceEqual(otherBytes)
            : root.Identifier.Equals(otherRoot.Identifier);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is OpcUaRegistryOrigin other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(OriginUri, StringComparer.Ordinal);
        if (RegistryRoot is { } root)
        {
            hash.Add(root.ServerApplicationUri, StringComparer.Ordinal);
            hash.Add(root.NamespaceUri, StringComparer.Ordinal);
            hash.Add(root.IdentifierType);
            if (root.Identifier is byte[] bytes)
            {
                foreach (var value in bytes)
                {
                    hash.Add(value);
                }
            }
            else
            {
                hash.Add(root.Identifier);
            }
        }
        return hash.ToHashCode();
    }
}
