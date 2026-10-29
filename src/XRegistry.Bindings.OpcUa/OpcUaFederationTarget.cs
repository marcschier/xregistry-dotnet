// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Text.Json;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>A validated OPC UA federation advertisement with a portable Registry root NodeId.</summary>
public sealed class OpcUaFederationTarget
{
    private readonly ExpandedNodeId registryRoot;
    private readonly string registryRootText;

    private OpcUaFederationTarget(string endpoint, ExpandedNodeId registryRoot, string registryRootText,
        string? applicationUri, string? transportProfileUri)
    {
        Endpoint = endpoint;
        this.registryRoot = registryRoot;
        this.registryRootText = registryRootText;
        ApplicationUri = applicationUri;
        TransportProfileUri = transportProfileUri;
    }

    /// <summary>The native OPC UA endpoint URL, kept separate from application identity.</summary>
    public string Endpoint { get; }
    /// <summary>The expected Server Application URI, if the catalog supplied one.</summary>
    public string? ApplicationUri { get; }
    /// <summary>The expected OPC UA transport profile URI, if supplied.</summary>
    public string? TransportProfileUri { get; }
    /// <summary>The portable NodeId string from the advertisement.</summary>
    public string RegistryRoot => registryRootText;

    /// <summary>Parses and validates the selected <c>opcua</c> advertisement.</summary>
    public static OpcUaFederationTarget FromAdvertisement(CatalogAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        if (advertisement.Name != "opcua")
        {
            throw new ArgumentException("The selected advertisement is not the opcua profile.", nameof(advertisement));
        }

        var endpoint = AbsoluteUri(advertisement.Endpoint);
        if (advertisement.Endpoint.Contains('?', StringComparison.Ordinal) ||
            advertisement.Endpoint.Contains('#', StringComparison.Ordinal) ||
            endpoint.UserInfo.Length != 0 || endpoint.Host.Length == 0)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The OPC UA endpoint must be a credential-free native endpoint URL.");
        }

        var parameters = advertisement.Parameters;
        RequireObject(parameters);
        foreach (var parameter in parameters.EnumerateObject())
        {
            if (parameter.Name is not ("registryroot" or "applicationuri" or "transportprofileuri"))
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "Unknown OPC UA federation parameter.");
            }
        }

        if (!parameters.TryGetProperty("registryroot", out var rootValue) ||
            rootValue.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(rootValue.GetString()))
        {
            throw Invalid("The OPC UA advertisement requires registryroot.");
        }

        var rootText = rootValue.GetString()!;
        if (rootText.StartsWith("svr=", StringComparison.Ordinal) ||
            rootText.StartsWith("svu=", StringComparison.Ordinal) ||
            rootText.StartsWith("ns=", StringComparison.Ordinal) ||
            rootText.Contains(";ns=", StringComparison.Ordinal))
        {
            throw Invalid(
                "registryroot must use the portable NodeId form without a server prefix or namespace index.");
        }

        ExpandedNodeId root;
        try
        {
            root = ExpandedNodeId.Parse(rootText);
        }
        catch (ServiceResultException exception)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The OPC UA registryroot is not a valid portable NodeId.", innerException: exception);
        }

        if (root.IsNull || root.ServerIndex != 0 ||
            (root.NamespaceUri is null && root.NamespaceIndex != 0))
        {
            throw Invalid(
                "registryroot must identify a non-null node in namespace zero or by namespace URI.");
        }

        var applicationUri = OptionalAbsoluteUri(parameters, "applicationuri");
        var transportProfileUri = OptionalAbsoluteUri(parameters, "transportprofileuri");
        return new(advertisement.Endpoint, root, rootText, applicationUri, transportProfileUri);
    }

    /// <summary>Maps the portable namespace URI to this connected Server's current NamespaceArray.</summary>
    /// <remarks>A NodeId index from another Session is never reused.</remarks>
    public NodeId ResolveRegistryRoot(NamespaceTable namespaceUris)
    {
        ArgumentNullException.ThrowIfNull(namespaceUris);
        NodeId? resolved;
        try
        {
            resolved = ExpandedNodeId.ToNodeId(registryRoot, namespaceUris, updateNamespaceTable: false);
        }
        catch (ServiceResultException exception)
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "The OPC UA registryroot namespace is absent from the connected Server NamespaceArray.",
                innerException: exception);
        }

        if (resolved is not { } node || node.IsNull)
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "The OPC UA registryroot namespace is absent from the connected Server NamespaceArray.");
        }
        return node;
    }

    /// <summary>Selects the unique discovered endpoint matching this advertisement and caller policy.</summary>
    public EndpointDescription SelectEndpoint(IEnumerable<EndpointDescription> candidates,
        Func<EndpointDescription, bool> isPermitted)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(isPermitted);

        var identityMatches = new List<EndpointDescription>();
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (!string.Equals(candidate.EndpointUrl, Endpoint, StringComparison.Ordinal) ||
                (ApplicationUri is not null &&
                    !string.Equals(candidate.Server?.ApplicationUri, ApplicationUri, StringComparison.Ordinal)) ||
                (TransportProfileUri is not null &&
                    !string.Equals(candidate.TransportProfileUri, TransportProfileUri, StringComparison.Ordinal)))
            {
                continue;
            }
            identityMatches.Add(candidate);
        }

        if (identityMatches.Count == 0)
        {
            throw new FederationException(FederationErrorCode.NotFound,
                "No discovered OPC UA endpoint matches the selected advertisement.");
        }

        var permitted = identityMatches.Where(isPermitted).Take(2).ToArray();
        if (permitted.Length == 0)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied,
                "Caller policy denied every matching OPC UA endpoint.");
        }
        if (permitted.Length > 1)
        {
            throw new FederationException(FederationErrorCode.Ambiguous,
                "More than one matching OPC UA endpoint is permitted.");
        }
        return permitted[0];
    }

    private static string? OptionalAbsoluteUri(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
            !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
        {
            throw Invalid($"The OPC UA {name} parameter must be an absolute URI.");
        }
        return value.GetString();
    }

    private static FederationException Invalid(string message) =>
        new(FederationErrorCode.InvalidPackage, message);

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Expected an OPC UA federation parameter object.");
        }
    }

    private static Uri AbsoluteUri(string value)
    {
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !Uri.IsWellFormedUriString(value, UriKind.Absolute))
        {
            throw Invalid("Expected an absolute OPC UA endpoint URI.");
        }
        if (uri.UserInfo.Length != 0)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied,
                "Embedded endpoint credentials are prohibited.");
        }
        return uri;
    }
}
