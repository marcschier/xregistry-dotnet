// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>The three scalar fields of a source Session's RegistryOriginDataType value.</summary>
/// <remarks>This is an untrusted claim, not a provider binding. Resolve table indexes in the source Session before retaining it.</remarks>
public sealed record OpcUaRegistryOriginValue(string? OriginUri, string? ServerUri, ExpandedNodeId RegistryNodeId)
{
    internal bool ValidateForm()
    {
        var stable = !string.IsNullOrEmpty(OriginUri);
        if (stable
            ? !string.IsNullOrEmpty(ServerUri) || !RegistryNodeId.IsNull
            : string.IsNullOrEmpty(ServerUri) || RegistryNodeId.IsNull)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "OriginRegistry must use exactly one authoritative origin form.");
        }
        return stable;
    }
}
