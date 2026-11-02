// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;

namespace XRegistry.Bindings.OpcUa;

/// <summary>The three scalar fields of a source Session's RegistryOriginDataType value.</summary>
/// <remarks>This is an untrusted claim, not a provider binding. Resolve table indexes in the source Session before retaining it.</remarks>
public sealed record OpcUaRegistryOriginValue(string? OriginUri, string? ServerUri, ExpandedNodeId RegistryNodeId);
