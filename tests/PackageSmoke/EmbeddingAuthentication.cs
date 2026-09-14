using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XRegistry;
using XRegistry.Server;

namespace EmbeddingConsumer;

internal sealed class EmbeddingCredentials : IDisposable
{
    internal const string Scheme = "EmbeddingFixture";
    internal string WriterToken { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    internal string ReaderToken { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private byte[]? _writerHash;
    private byte[]? _readerHash;

    internal void Initialize()
    {
        _writerHash = SHA256.HashData(Encoding.UTF8.GetBytes(WriterToken));
        _readerHash = SHA256.HashData(Encoding.UTF8.GetBytes(ReaderToken));
    }

    internal string? Authenticate(string token)
    {
        if (token.Length is < 32 or > 128 || token.Any(static character => character is < '!' or > '~'))
        {
            return null;
        }

        Span<byte> input = stackalloc byte[128];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            var length = Encoding.UTF8.GetBytes(token, input);
            SHA256.HashData(input[..length], hash);
            var writer = CryptographicOperations.FixedTimeEquals(hash, _writerHash!);
            var reader = CryptographicOperations.FixedTimeEquals(hash, _readerHash!);
            return writer ? "writer" : reader ? "reader" : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    internal static ClaimsPrincipal Principal(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "embedding-" + role), new Claim(ClaimTypes.Role, role)], Scheme));

    public void Dispose()
    {
        if (_writerHash is not null) { CryptographicOperations.ZeroMemory(_writerHash); }
        if (_readerHash is not null) { CryptographicOperations.ZeroMemory(_readerHash); }
    }
}

internal sealed class EmbeddingAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    EmbeddingCredentials credentials) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Context.RequestAborted.ThrowIfCancellationRequested();
        var values = Request.Headers.Authorization;
        if (values.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var header = values.Count == 1 ? values[0] : null;
        var role = header is not null && header.Length <= 256 && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? credentials.Authenticate(header[7..]) : null;
        return Task.FromResult(role is null ? AuthenticateResult.Fail("Invalid fixture credential.") :
            AuthenticateResult.Success(new AuthenticationTicket(EmbeddingCredentials.Principal(role), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

internal sealed class EmbeddingAuthorizationPolicy : IRegistryAuthorizationPolicy
{
    internal ConcurrentQueue<(string Subject, RegistryAccess Access, string Path)> Observations { get; } = new();

    public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trusted = caller.Identities.Any(static identity => identity.IsAuthenticated &&
            identity.AuthenticationType == EmbeddingCredentials.Scheme);
        var subject = caller.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
        Observations.Enqueue((subject, access, path.EscapedPath));
        return ValueTask.FromResult(trusted && Enum.IsDefined(access) &&
            (caller.IsInRole("writer") || (caller.IsInRole("reader") && access == RegistryAccess.Read)));
    }
}
