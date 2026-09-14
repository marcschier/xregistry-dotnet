using System.Security.Claims;

namespace XRegistry.Server;

/// <summary>A transport-independent operation corresponding to the core binding's supported verbs.</summary>
public enum RegistryAction
{
    /// <summary>Retrieves a representation.</summary>
    Read,
    /// <summary>Retrieves representation metadata without transmitting its body.</summary>
    Head,
    /// <summary>Discovers the allowed operations.</summary>
    Options,
    /// <summary>Replaces an entity's mutable attributes.</summary>
    Replace,
    /// <summary>Updates only submitted top-level attributes.</summary>
    Patch,
    /// <summary>Upserts collection members or a Resource Version.</summary>
    Post,
    /// <summary>Deletes entities atomically.</summary>
    Delete
}

/// <summary>An authorization decision over one concrete entity or administrative route.</summary>
public enum RegistryAccess
{
    /// <summary>Reads an entity or collection.</summary>
    Read,
    /// <summary>Creates an entity, including implicit parents.</summary>
    Create,
    /// <summary>Updates an existing entity, including lifecycle side effects.</summary>
    Update,
    /// <summary>Deletes an entity, including cascade/retention deletions.</summary>
    Delete,
    /// <summary>Changes model source or the effective model.</summary>
    UpdateModel,
    /// <summary>Changes server capabilities.</summary>
    UpdateCapabilities,
    /// <summary>Reads the internal committed event outbox; authentication is always required.</summary>
    ReadEvents,
    /// <summary>Acknowledges delivered event batches.</summary>
    AcknowledgeEvents
}

/// <summary>An explicit policy, supplied by the host. There is no default production allow policy.</summary>
public interface IRegistryAuthorizationPolicy
{
    /// <summary>Decides access using a trusted per-request principal, never arbitrary request headers.</summary>
    ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
        CancellationToken cancellationToken = default);
}

/// <summary>A bounded core operation. A non-null Document stream represents a present body, even at EOF.</summary>
public sealed record RegistryOperation
{
    /// <summary>Creates an operation; syntax validation belongs to RegistryPath.</summary>
    public RegistryOperation(RegistryAction action, RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        Action = action;
        Path = path;
    }

    /// <summary>Gets the action.</summary>
    public RegistryAction Action { get; }
    /// <summary>Gets the root-relative route.</summary>
    public RegistryPath Path { get; }
    /// <summary>Gets owned submitted metadata, or null for an absent metadata body.</summary>
    public RegistryJson? Metadata { get; init; }
    /// <summary>Gets the caller-owned input Document, consumed only during execution and never disposed by the engine.</summary>
    public Stream? Document { get; init; }
    /// <summary>Gets the input media type; null is distinct from application/octet-stream.</summary>
    public string? ContentType { get; init; }
    /// <summary>Gets ordered parameters, preserving repetitions and absent versus empty values.</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Parameters { get; init; } = [];
    /// <summary>Gets an optional model revision pin from DescribeAsync, preventing stale header type decoding.</summary>
    public string? ExpectedModelRevision { get; init; }
}

/// <summary>Per-request caller identity and an optional pre-publication response preparation hook.</summary>
public sealed class RegistryOperationContext
{
    /// <summary>Creates a context from the host's authenticated principal or a trusted mapper.</summary>
    public RegistryOperationContext(ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Caller = caller;
    }

    /// <summary>Gets the trusted caller. Hosts must not mutate it while the operation is in flight.</summary>
    public ClaimsPrincipal Caller { get; }
    /// <summary>
    /// Gets a hook invoked after full response/lifecycle preparation but before persistence preparation
    /// or publication. Throwing rejects a mutation. HTTP uses it for bounded serialization and headers.
    /// It must not transmit success, mutate persistence, or retain mutable request data.
    /// </summary>
    public Func<RegistryResult, CancellationToken, ValueTask>? PrepareResponseAsync { get; init; }
}

/// <summary>The semantic outcome; HTTP maps these to 200, 201, 204 and 303 respectively.</summary>
public enum RegistryResultKind
{
    /// <summary>The operation completed with a representation.</summary>
    Success,
    /// <summary>A new entity was created.</summary>
    Created,
    /// <summary>The operation completed without a representation.</summary>
    NoContent,
    /// <summary>The Document is externally located; no server-side fetching occurred.</summary>
    SeeOther
}

/// <summary>An independently owned, bounded, immutable exact Document.</summary>
public sealed class RegistryDocument
{
    private readonly byte[] _bytes;
    internal RegistryDocument(byte[] bytes) => _bytes = bytes;
    /// <summary>Gets the exact byte count, including zero for a present empty Document.</summary>
    public int Length => _bytes.Length;
    /// <summary>Opens a caller-owned read-only stream; it is independent of persistence snapshot lifetimes.</summary>
    public Stream OpenRead() => new MemoryStream(_bytes, writable: false);
    internal ReadOnlyMemory<byte> Bytes => _bytes;
}

/// <summary>A fully prepared result. A mutation result is returned only after its atomic commit succeeds.</summary>
public sealed class RegistryResult
{
    internal RegistryResult(RegistryResultKind kind, RegistryPath path, RegistryJson? metadata = null)
    {
        Kind = kind;
        Path = path;
        Metadata = metadata;
    }

    /// <summary>Gets the semantic outcome.</summary>
    public RegistryResultKind Kind { get; internal set; }
    /// <summary>Gets the actual represented entity/collection path, which can differ for POST to a Resource.</summary>
    public RegistryPath Path { get; }
    /// <summary>Gets owned metadata; in Document mode it supplies the scalar header projection.</summary>
    public RegistryJson? Metadata { get; }
    /// <summary>Gets owned Document bytes, or null for a metadata representation.</summary>
    public RegistryDocument? Document { get; internal init; }
    /// <summary>Gets whether metadata belongs in headers rather than in the response body.</summary>
    public bool IsDocument { get; internal init; }
    /// <summary>Gets the Document media type, if declared.</summary>
    public string? ContentType { get; internal init; }
    /// <summary>Gets the creation or external Document URL, if applicable.</summary>
    public Uri? Location { get; internal set; }
    /// <summary>Gets the affected Version's representation URL, if applicable.</summary>
    public Uri? ContentLocation { get; internal set; }
    /// <summary>Gets the supported actions for OPTIONS or method discovery.</summary>
    public IReadOnlyList<RegistryAction> AllowedActions { get; internal init; } = [];
    /// <summary>Gets the actual compiled Resource definition for model-aware representation encoding.</summary>
    public RegistryResourceDefinition? ResourceDefinition { get; internal init; }
    /// <summary>Gets the unique interaction ID, shared by all committed change events.</summary>
    public string? CorrelationId { get; internal set; }
    /// <summary>Gets frozen collection pagination metadata, if this is a paged collection read.</summary>
    public RegistryPageInfo? Page { get; internal init; }
}

/// <summary>The public engine operation boundary.</summary>
public interface IRegistryEngine
{
    /// <summary>Validates, authorizes, prepares and (for mutations) commits one bounded atomic operation.</summary>
    ValueTask<RegistryResult> ExecuteAsync(RegistryOperation operation, RegistryOperationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>A route's model-aware input shape and supported actions at one model revision.</summary>
public sealed record RegistryRouteDescription(
    RegistryPath Path, string ModelRevision, RegistryResourceDefinition? ResourceDefinition,
    IReadOnlyList<RegistryAction> AllowedActions);

/// <summary>Trusted configuration, independent of request Host or forwarding headers.</summary>
public sealed record RegistryEngineOptions
{
    /// <summary>Gets the required externally advertised absolute HTTP(S) Registry root.</summary>
    public required Uri PublicRoot { get; init; }
    /// <summary>Gets the immutable Registry identifier.</summary>
    public required string RegistryId { get; init; }
    /// <summary>Gets the initial effective model; persisted model source takes precedence on restart.</summary>
    public required RegistryModel Model { get; init; }
    /// <summary>Gets initial Registry attributes for a previously empty store.</summary>
    public RegistryJson InitialMetadata { get; init; } = RegistryJson.Parse("{}");
    /// <summary>Gets the only external model resolver, with bounded include work. Null prohibits egress.</summary>
    public RegistryModelCompilationOptions ModelCompilation { get; init; } = new();
    /// <summary>Gets whether unauthenticated reads may be considered by the authorization policy.</summary>
    public bool AllowAnonymousReads { get; init; }
    /// <summary>Gets whether authenticated, authorized clients may edit capabilities. Defaults to false and cannot be enabled by capability data.</summary>
    public bool AllowCapabilityUpdates { get; init; }
    /// <summary>Gets the operation budgets.</summary>
    public RegistryLimits Limits { get; init; } = new();
    /// <summary>Gets bounded query evaluation and retained-pagination limits.</summary>
    public RegistryQueryLimits QueryLimits { get; init; } = new();
    /// <summary>Gets the injected clock; one timestamp is used throughout each interaction.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    /// <summary>Gets advertised Registry-based discovery peers. Empty advertises this Registry.</summary>
    public IReadOnlyList<Uri> DiscoveryRegistries { get; init; } = [];
    /// <summary>Gets the optional format/compatibility implementation; absence is reported as unsupported, not valid.</summary>
    public IRegistryResourceValidator? ResourceValidator { get; init; }
    /// <summary>Gets the explicit external Document reference policy. Null prohibits setting external references.</summary>
    public IRegistryDocumentReferencePolicy? DocumentReferencePolicy { get; init; }
    /// <summary>Gets the explicit validator for Core's external metadata obligations. Null rejects unresolved obligations.</summary>
    public IRegistryMetadataObligationValidator? ObligationValidator { get; init; }
}
