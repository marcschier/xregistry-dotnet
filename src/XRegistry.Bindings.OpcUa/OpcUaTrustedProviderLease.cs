// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>A caller-approved lease for an already authenticated target Registry Session.</summary>
public interface IOpcUaTrustedProviderLease : IAsyncDisposable
{
    /// <summary>The exact Application URI of the connected target Server.</summary>
    string ServerApplicationUri { get; }
    /// <summary>The protocol-level reader bound to the target Session.</summary>
    OpcUaRegistryReader Reader { get; }
    /// <summary>The target Registry root validated for this Session.</summary>
    NodeId RegistryRoot { get; }
    /// <summary>The target Registry's accepted Core SpecVersion.</summary>
    string SpecVersion { get; }
    /// <summary>The target Registry context used for federation reads.</summary>
    NativeRegistryContext Context { get; }
    /// <summary>The caller-supplied mapping for the target Registry's model types.</summary>
    OpcUaRegistryTypeMap Types { get; }
}

/// <summary>Resolves an external NodeId through caller-owned endpoint and trust policy.</summary>
/// <remarks>Ownership of the returned lease transfers to the resolver operation.</remarks>
public delegate ValueTask<IOpcUaTrustedProviderLease> OpcUaTrustedProviderResolver(
    OpcUaExternalNodeId identity, CancellationToken cancellationToken);

/// <summary>Owns a trusted target connection and its model-specific type mappings.</summary>
public sealed class OpcUaTrustedProviderLease : IOpcUaTrustedProviderLease
{
    private readonly OpcUaSessionConnection connection;
    private int disposed;

    /// <summary>Creates a lease from a connected Session and the target Registry's model mapping.</summary>
    public OpcUaTrustedProviderLease(OpcUaSessionConnection connection, NativeRegistryContext context,
        OpcUaRegistryTypeMap types)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(types);
        if (!string.Equals(context.Binding, "opcua", StringComparison.Ordinal))
        {
            throw new ArgumentException("The target context binding must be 'opcua'.", nameof(context));
        }

        this.connection = connection;
        Context = context;
        Types = types;
    }

    /// <inheritdoc />
    public string ServerApplicationUri => connection.ServerApplicationUri;
    /// <inheritdoc />
    public OpcUaRegistryReader Reader => connection.Reader;
    /// <inheritdoc />
    public NodeId RegistryRoot => connection.RegistryRoot;
    /// <inheritdoc />
    public string SpecVersion => connection.SpecVersion;
    /// <inheritdoc />
    public NativeRegistryContext Context { get; }
    /// <inheritdoc />
    public OpcUaRegistryTypeMap Types { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref disposed, 1) == 0 ? connection.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>A validated external Resource and the target read source whose lifetime it requires.</summary>
public sealed class OpcUaResolvedExternalResource : IAsyncDisposable
{
    private readonly IOpcUaTrustedProviderLease lease;
    private int disposed;

    internal OpcUaResolvedExternalResource(IOpcUaTrustedProviderLease lease,
        OpcUaRegistryReadSource readSource, NodeId node, string xid, string specVersion)
    {
        this.lease = lease;
        ReadSource = readSource;
        Node = node;
        Xid = xid;
        SpecVersion = specVersion;
    }

    /// <summary>The target Registry read source; use it only while this result is alive.</summary>
    public OpcUaRegistryReadSource ReadSource { get; }
    /// <summary>The target Session-local Resource or Version NodeId.</summary>
    public NodeId Node { get; }
    /// <summary>The target Resource or Version's validated origin-relative XID.</summary>
    public string Xid { get; }
    /// <summary>The target Registry's accepted Core SpecVersion.</summary>
    public string SpecVersion { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref disposed, 1) == 0 ? lease.DisposeAsync() : ValueTask.CompletedTask;
}
