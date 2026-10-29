// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using Opc.Ua.Client;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>Owns a discovered, caller-approved OPC UA Session and its selected Registry root.</summary>
public sealed class OpcUaSessionConnection : IAsyncDisposable
{
    private OpcUaSessionConnection(ManagedSession session, OpcUaSessionAdapter adapter,
        OpcUaRegistryReader reader, NodeId registryRoot, string specVersion, string serverApplicationUri)
    {
        Session = session;
        Adapter = adapter;
        Reader = reader;
        RegistryRoot = registryRoot;
        SpecVersion = specVersion;
        ServerApplicationUri = serverApplicationUri;
    }

    /// <summary>The connected OPC UA client Session.</summary>
    public ManagedSession Session { get; }
    /// <summary>The OPC UA 2.0.1 service adapter.</summary>
    public OpcUaSessionAdapter Adapter { get; }
    /// <summary>The bounded protocol-level Registry reader.</summary>
    public OpcUaRegistryReader Reader { get; }
    /// <summary>The advertisement's Registry root mapped to this Session's NamespaceArray.</summary>
    public NodeId RegistryRoot { get; }
    /// <summary>The validated Core SpecVersion reported by the selected Registry.</summary>
    public string SpecVersion { get; }
    /// <summary>The exact Application URI of the connected Server.</summary>
    public string ServerApplicationUri { get; }

    /// <summary>Discovers endpoints, applies caller policy, connects, maps the root, and checks Core version.</summary>
    public static async ValueTask<OpcUaSessionConnection> OpenAsync(OpcUaFederationTarget target,
        ApplicationConfiguration configuration, ITelemetryContext telemetry, IUserIdentity userIdentity,
        NodeId registryType, Func<EndpointDescription, bool> isPermitted,
        Func<string, bool> isSupportedSpecVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(userIdentity);
        ArgumentNullException.ThrowIfNull(isPermitted);
        ArgumentNullException.ThrowIfNull(isSupportedSpecVersion);

        using var discovery = await DiscoveryClient.CreateAsync(configuration, new Uri(target.Endpoint),
            DiagnosticsMasks.None, cancellationToken).ConfigureAwait(false);
        var endpoints = await discovery.GetEndpointsAsync([], cancellationToken).ConfigureAwait(false);
        var selected = target.SelectEndpoint(endpoints.ToArray() ?? [], isPermitted);
        var serverApplicationUri = selected.Server?.ApplicationUri;
        if (string.IsNullOrEmpty(serverApplicationUri))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The selected OPC UA endpoint has no Server Application URI.");
        }
        var configuredEndpoint = new ConfiguredEndpoint(null, selected,
            EndpointConfiguration.Create(configuration));
        var session = await new ManagedSessionBuilder(configuration, telemetry)
            .UseEndpoint(configuredEndpoint)
            .WithUserIdentity(userIdentity)
            .ConnectAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var adapter = new OpcUaSessionAdapter(session, registryType);
            var serverArray = await adapter.ReadServerArrayAsync(cancellationToken).ConfigureAwait(false);
            if (serverArray.Count == 0 ||
                !string.Equals(serverArray[0], serverApplicationUri, StringComparison.Ordinal))
            {
                throw new FederationException(FederationErrorCode.PolicyDenied,
                    "The connected Session ServerArray does not match the selected endpoint's Server Application URI.");
            }
            var reader = new OpcUaRegistryReader(adapter);
            var root = await reader.VerifyRootAsync(target, cancellationToken).ConfigureAwait(false);
            var specVersion = await reader.ReadSpecVersionAsync(root, isSupportedSpecVersion, cancellationToken)
                .ConfigureAwait(false);
            return new(session, adapter, reader, root, specVersion, serverApplicationUri);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
