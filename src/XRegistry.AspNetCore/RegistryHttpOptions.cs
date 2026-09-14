using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace XRegistry.AspNetCore;

/// <summary>Explicit mount and trusted caller mapping for the bounded Minimal API adapter.</summary>
public sealed record RegistryHttpOptions
{
    /// <summary>Gets the local path prefix. Empty mounts at the application root; this never changes PublicRoot.</summary>
    public string MountPath { get; init; } = "";
    /// <summary>Gets an optional trusted mapper. By default HttpContext.User is used; no identity headers are recognized.</summary>
    public Func<HttpContext, ClaimsPrincipal>? CallerMapper { get; init; }
    /// <summary>Gets the optional challenge field for 401 responses; configure it to match the host authentication middleware.</summary>
    public string? AuthenticationChallenge { get; init; }
    /// <summary>Gets the separate bounded problem-details response budget, at least 1024 bytes.</summary>
    public int MaxErrorBytes { get; init; } = 32 * 1024;
    /// <summary>Gets the complete request/body/response lifetime budget, independent of client disconnect cancellation.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
