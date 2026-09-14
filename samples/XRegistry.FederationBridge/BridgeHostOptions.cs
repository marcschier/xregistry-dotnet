using System.Security.Claims;
using XRegistry.Federation;
using XRegistry.Queries;

namespace XRegistry.Samples.Bridge;

public sealed record BridgeHostOptions
{
    public required RegistryModel Model { get; init; }
    public required Uri PublicRoot { get; init; }
    public string RegistryId { get; init; } = "bridge";
    public string AggregatePath { get; init; } = "/registry";
    public string MountPrefix { get; init; } = "/registries";
    public bool AllowAnonymousReads { get; init; }
    public int MaxConcurrentRequests { get; init; } = 4;
    public int MaxRequestBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxHeaderBytes { get; init; } = 16 * 1024;
    public int MaxQueryCharacters { get; init; } = 8192;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public RegistryQueryEvaluationLimits QueryLimits { get; init; } = new();
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public BridgePagingLimits PagingLimits { get; init; } = new();
    public FederationReadLimits ReadLimits { get; init; } = new(
        maxObjectBytes: 2 * 1024 * 1024, maxTotalBytes: 16 * 1024 * 1024,
        maxRequests: 256, maxObjects: 1024, maxWork: 200_000, maxSources: 32,
        maxHops: 4, maxDepth: 32, maxJsonDepth: 64, maxResultBytes: 2 * 1024 * 1024);
    public IReadOnlyList<Uri> ExternalDocumentOrigins { get; init; } = [];
    public required Func<ClaimsPrincipal, string, CancellationToken, ValueTask<bool>> AuthorizeSource { get; init; }
    public required Func<ClaimsPrincipal, string, HttpMethod, CancellationToken, ValueTask<bool>> AuthorizeMount { get; init; }
    public Func<ClaimsPrincipal, string, RegistryPath, CancellationToken, ValueTask<bool>>? AuthorizeRetainedRead { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Model);
        ArgumentNullException.ThrowIfNull(PublicRoot);
        ArgumentNullException.ThrowIfNull(AuthorizeSource);
        ArgumentNullException.ThrowIfNull(AuthorizeMount);
        ArgumentNullException.ThrowIfNull(ReadLimits);
        ArgumentNullException.ThrowIfNull(QueryLimits);
        ArgumentNullException.ThrowIfNull(TimeProvider);
        ArgumentNullException.ThrowIfNull(PagingLimits);
        PagingLimits.Validate();
        XRegistry.RegistryId.Parse(RegistryId);
        CheckMount(AggregatePath);
        CheckMount(MountPrefix);
        if (AggregatePath == MountPrefix || AggregatePath.StartsWith(MountPrefix + "/", StringComparison.Ordinal) ||
            MountPrefix.StartsWith(AggregatePath + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Aggregate and write-through paths must not overlap.");
        }
        if (!PublicRoot.IsAbsoluteUri || PublicRoot.Scheme is not ("https" or "http") ||
            PublicRoot.UserInfo.Length != 0 || PublicRoot.Query.Length != 0 || PublicRoot.Fragment.Length != 0 ||
            !PublicRoot.AbsolutePath.TrimEnd('/').EndsWith(AggregatePath, StringComparison.Ordinal))
        {
            throw new ArgumentException("PublicRoot must be the trusted aggregate URL, with its configured mount suffix.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentRequests, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConcurrentRequests, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRequestBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRequestBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxResponseBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHeaderBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxHeaderBytes, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxQueryCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxQueryCharacters, 16 * 1024);
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentException("A bridge request needs a finite deadline up to two minutes.");
        }
        if (ExternalDocumentOrigins.Count > 16 || ExternalDocumentOrigins.Any(uri =>
            !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"))
        {
            throw new ArgumentException("External Document redirects require at most sixteen explicit HTTP(S) origins.");
        }
    }

    internal static void CheckMount(string path)
    {
        if (path.Length is < 2 or > 256 || !path.StartsWith('/') || path.EndsWith('/') ||
            path[1..].Split('/').Any(segment => !XRegistry.RegistryId.IsValid(segment)))
        {
            throw new ArgumentException("A mount must have literal, nonempty Core ID path components.");
        }
    }
}

public sealed record BridgeWriteMount
{
    public required string Name { get; init; }
    public required Uri UpstreamRoot { get; init; }
    public required RegistryModel Model { get; init; }
    public bool AllowLoopbackHttp { get; init; }
    public bool AllowPrivateOrigin { get; init; }
    public Func<Uri, CancellationToken, ValueTask<System.Net.Http.Headers.AuthenticationHeaderValue?>>? AuthorizationProvider { get; init; }
}
