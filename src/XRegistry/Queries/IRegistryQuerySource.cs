// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Queries;

/// <summary>A stable, complete, authorized logical view after whole-Resource shadowing.</summary>
/// <remarks>
/// The adapter owns source selection, authorization and leases. All Meta, Version and Document reads
/// for a Resource must retain its selected origin, including source-local one-hop aliases. It must
/// never recover a filtered-out Resource from another origin or independently merge Versions.
/// Metadata must be model-valid and normalized. No method may silently truncate a collection.
/// Completeness is relative to the query target and requested facts, not an obligation to load the
/// whole Registry. The host establishes target existence and access before evaluation; excludeall
/// can short-circuit without reading a source. Unneeded ancestor/configuration planes need not load.
/// </remarks>
public interface IRegistryQuerySource
{
    /// <summary>Reads an entity's raw data plane; null means authorization-hidden, never incomplete data.</summary>
    /// <remarks>
    /// Registry/Group/Version paths return their own attributes. Resource AND Meta paths return
    /// Resource Meta attributes, including defaultversionid, not the default-Version projection.
    /// The evaluator follows that ID through this same adapter. Administrative model/modelsource/
    /// capabilities paths return their actual JSON objects when requested. Missing required entities,
    /// unavailable semantic inputs and incomplete source responses must throw explicitly.
    /// includeDocument is requested only for a Version, and requires exact bytes (including empty)
    /// unless its modeled Document URL is present. The adapter must not fetch that URL implicitly.
    /// Cancellation must be honored; the caller owns the source and its lifetime.
    /// </remarks>
    ValueTask<RegistryQueryEntity?> ReadEntityAsync(RegistryPath path, bool includeDocument,
        CancellationToken cancellationToken = default);

    /// <summary>Enumerates every visible direct entity in a model-defined collection, with no duplicates.</summary>
    /// <remarks>
    /// Paths are logical Registry-relative identities, not source URLs. Hidden parents/collections
    /// yield no members; an unavailable or partial collection must throw, not masquerade as empty.
    /// Resource visibility includes its default-Version projection. Results must remain stable for
    /// the evaluation and subsequent projection. Current authorization for retained pages remains
    /// the host's responsibility; this interface confers no permission to reuse old authorization.
    /// </remarks>
    IAsyncEnumerable<RegistryPath> GetChildrenAsync(RegistryPath collection,
        CancellationToken cancellationToken = default);
}

/// <summary>Owned raw metadata and optional borrowed, stable Document bytes from one selected entity.</summary>
public sealed class RegistryQueryEntity
{
    /// <summary>Creates a raw entity plane. Metadata is immutable and independently owned.</summary>
    public RegistryQueryEntity(RegistryJson metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new ArgumentException("Query entity metadata must be a JSON object.", nameof(metadata));
        }

        Metadata = metadata;
    }

    /// <summary>Gets the raw plane described by <see cref="IRegistryQuerySource.ReadEntityAsync"/>.</summary>
    public RegistryJson Metadata { get; }
    /// <summary>Gets exact bytes, stable until evaluation ends. Null is absent; empty memory is a present Document.</summary>
    public ReadOnlyMemory<byte>? Document { get; init; }
    /// <summary>Gets an optional trusted, enabled host-generated short URL; domain URL attributes are not rewritten.</summary>
    public string? ShortSelf { get; init; }
    /// <summary>Gets optional raw selected-unit default context for a Version, avoiding a separate Resource read permission.</summary>
    /// <remarks>
    /// This is not a Version attribute. The evaluator derives isdefault without exposing this ID.
    /// It must agree with the same pinned Resource Meta. Without it, Version facts require reading
    /// the Resource plane through the source. Supplying it never authorizes a Resource or Meta query.
    /// </remarks>
    public string? DefaultVersionId { get; init; }
    /// <summary>Gets whether a Resource/Meta is an intentionally dangling source-local alias, not a missing default.</summary>
    public bool IsDanglingCrossReference { get; init; }
}
