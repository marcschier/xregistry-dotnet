// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Server;

/// <summary>A transport-neutral Registry backend that supplies the operations required by a protocol host.</summary>
/// <remarks>
/// Implementations authorize requests using the supplied trusted caller, describe routes before input decoding,
/// execute each operation once, and prepare mutations so failures occur before publication. The host owns only
/// transport parsing and representation encoding.
/// </remarks>
public interface IRegistryHostBackend : IRegistryEngine
{
    /// <summary>Gets the trusted externally advertised Registry root.</summary>
    Uri PublicRoot { get; }

    /// <summary>Gets the finite operation and representation limits used by the host.</summary>
    RegistryLimits Limits { get; }

    /// <summary>Authorizes access before a protocol host consumes a request body.</summary>
    ValueTask AuthorizeRequestAsync(RegistryAction action, RegistryPath path, RegistryOperationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a protocol path to its canonical Registry path.</summary>
    ValueTask<RegistryPath> ResolvePathAsync(RegistryAction action, string escapedPath, RegistryOperationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Describes a route against the current model before protocol input is decoded.</summary>
    ValueTask<RegistryRouteDescription> DescribeAsync(RegistryAction action, RegistryPath path,
        RegistryOperationContext context, IReadOnlyList<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken = default);
}
