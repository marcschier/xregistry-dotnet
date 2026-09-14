using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace XRegistry.Samples;

internal sealed class RegistrySampleSecurityState : IDisposable
{
    private int _securityApplied;
    private int _listenerVerified;
    private int _disposed;

    private RegistrySampleSecurityState(RegistrySampleHostOptions options, IPAddress demoAddress,
        X509Certificate2? certificate, X509Certificate2Collection certificates, byte[] adminTokenHash, byte[]? readTokenHash)
    {
        Options = options;
        DemoAddress = demoAddress;
        Certificate = certificate;
        Certificates = certificates;
        AdminTokenHash = adminTokenHash;
        ReadTokenHash = readTokenHash;
    }

    internal RegistrySampleHostOptions Options { get; }
    internal IPAddress DemoAddress { get; }
    internal X509Certificate2? Certificate { get; }
    internal X509Certificate2Collection Certificates { get; }
    internal byte[] AdminTokenHash { get; }
    internal byte[]? ReadTokenHash { get; }
    internal bool SecurityApplied => Volatile.Read(ref _securityApplied) != 0;
    internal bool ListenerVerified => Volatile.Read(ref _listenerVerified) != 0;

    internal void SetListenerVerified(bool verified) => Volatile.Write(ref _listenerVerified, verified ? 1 : 0);

    internal void MarkSecurityApplied()
    {
        if (Interlocked.Exchange(ref _securityApplied, 1) != 0)
        {
            throw new InvalidOperationException("Sample request security has already been applied.");
        }
    }

    internal static RegistrySampleSecurityState Create(RegistrySampleHostOptions options, Func<string, string?> secrets)
    {
        ArgumentNullException.ThrowIfNull(options.PublicRoot);
        var root = options.PublicRoot;
        if (!root.IsAbsoluteUri || !root.IsWellFormedOriginalString() ||
            root.OriginalString.Length > 4096 || root.UserInfo.Length != 0 ||
            root.OriginalString.IndexOfAny(['?', '#', '\\']) >= 0 ||
            root.Scheme != (options.DemoLoopback ? Uri.UriSchemeHttp : Uri.UriSchemeHttps))
        {
            throw new ArgumentException("PublicRoot must be a trusted, well-formed HTTPS metadata root without credentials, query or fragment; only explicit loopback demo permits HTTP.", nameof(options));
        }

        var authority = root.OriginalString.AsSpan(root.Scheme.Length + 3);
        var slash = authority.IndexOf('/');
        if ((slash < 0 ? authority : authority[..slash]).Contains('@'))
        {
            throw new ArgumentException("PublicRoot must not contain credentials.", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.ListenPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ListenPort, 65535);
        if (options.DemoLoopback)
        {
            var address = root.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? IPAddress.Loopback : IPAddress.TryParse(root.IdnHost, out var literal) && IsLoopback(literal)
                    ? literal : throw new ArgumentException("Demo PublicRoot must name localhost or a literal loopback address.", nameof(options));
            if (options.CertificatePath is not null || options.CertificatePasswordEnvironment is not null ||
                options.WriteTokenEnvironment is not null || options.ReadTokenEnvironment is not null)
            {
                throw new ArgumentException("Loopback demo cannot be combined with certificate or token configuration.", nameof(options));
            }

            return new(options, address, null, [], [], null);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.CertificatePath);
        ValidateEnvironmentName(options.WriteTokenEnvironment);
        if (options.ReadTokenEnvironment is not null)
        {
            ValidateEnvironmentName(options.ReadTokenEnvironment);
        }

        if (options.CertificatePasswordEnvironment is not null)
        {
            ValidateEnvironmentName(options.CertificatePasswordEnvironment);
        }

        var administrator = HashConfiguredToken(secrets(options.WriteTokenEnvironment!));
        byte[]? reader = null;
        X509Certificate2Collection certificates = [];
        var transferred = false;
        try
        {
            reader = options.ReadTokenEnvironment is { } readName ? HashConfiguredToken(secrets(readName)) : null;
            if (reader is not null && CryptographicOperations.FixedTimeEquals(administrator, reader))
            {
                throw new ArgumentException("Read and administrative bearer tokens must be distinct.", nameof(options));
            }

            var password = options.CertificatePasswordEnvironment is { } passwordName
                ? secrets(passwordName) ?? throw new ArgumentException("The configured certificate password is missing.", nameof(options))
                : null;
            // Windows Schannel cannot use ephemeral PFX keys; the normal import is released on disposal.
            certificates = X509CertificateLoader.LoadPkcs12CollectionFromFile(options.CertificatePath, password,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            if (certificates.Count > 16 || certificates.Count(static certificate => certificate.HasPrivateKey) != 1)
            {
                throw new ArgumentException("The PFX must contain one private key and at most sixteen certificates.", nameof(options));
            }

            var certificate = certificates.Single(static certificate => certificate.HasPrivateKey);
            var now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() ||
                now > certificate.NotAfter.ToUniversalTime() ||
                certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(static extension => extension.CertificateAuthority) ||
                certificate.Extensions.OfType<X509KeyUsageExtension>().Any(static extension =>
                    (extension.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0) ||
                certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(static extension =>
                    !extension.EnhancedKeyUsages.Cast<Oid>().Any(static usage => usage.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0")))
            {
                throw new ArgumentException("A currently valid server certificate with its private key is required.", nameof(options));
            }

            var state = new RegistrySampleSecurityState(options, IPAddress.Loopback, certificate, certificates, administrator, reader);
            transferred = true;
            return state;
        }
        finally
        {
            if (!transferred)
            {
                foreach (var certificate in certificates)
                {
                    certificate.Dispose();
                }
                CryptographicOperations.ZeroMemory(administrator);
                if (reader is not null)
                {
                    CryptographicOperations.ZeroMemory(reader);
                }
            }
        }
    }

    internal static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    internal static bool IsToken(ReadOnlySpan<char> token)
    {
        if (token.Length is < 32 or > RegistrySampleHosting.MaxTokenBytes)
        {
            return false;
        }

        foreach (var character in token)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '~' or '+' or '/' or '='))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] HashConfiguredToken(string? token)
    {
        if (token is null || !IsToken(token.AsSpan()))
        {
            throw new ArgumentException("A bearer token must contain 32 through 512 UTF-8 bytes using the ASCII bearer-token alphabet.");
        }

        Span<byte> bytes = stackalloc byte[RegistrySampleHosting.MaxTokenBytes];
        try
        {
            var length = Encoding.UTF8.GetBytes(token.AsSpan(), bytes);
            return SHA256.HashData(bytes[..length]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateEnvironmentName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128 ||
            !(char.IsAsciiLetter(name[0]) || name[0] == '_') ||
            name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("An explicit portable environment-variable name is required for each configured secret.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var certificate in Certificates)
        {
            certificate.Dispose();
        }
        CryptographicOperations.ZeroMemory(AdminTokenHash);
        if (ReadTokenHash is not null)
        {
            CryptographicOperations.ZeroMemory(ReadTokenHash);
        }
    }
}

internal sealed class RegistrySampleStartupGuard(RegistrySampleSecurityState state, ILogger<RegistrySampleStartupGuard> logger) :
    IStartupFilter
{
    private static readonly Action<ILogger, Exception?> s_demoWarning = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "InsecureLoopbackDemo"),
        "INSECURE LOOPBACK DEMO: every local caller receives administrative access. Do not expose or tunnel this listener to other hosts.");

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (!state.SecurityApplied)
        {
            throw new InvalidOperationException("RegistrySampleHosting.UseSecurity must be called before the sample starts.");
        }

        if (state.Options.DemoLoopback)
        {
            s_demoWarning(logger, null);
        }

        next(app);
    };
}

internal sealed class RegistrySampleListenerGuard(IServer server, RegistrySampleSecurityState state) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count != 1 ||
            !Uri.TryCreate(addresses.Single(), UriKind.Absolute, out var address) ||
            address.Scheme != (state.Options.DemoLoopback ? "http" : "https") ||
            (state.Options.ListenPort != 0 && address.Port != state.Options.ListenPort) ||
            (state.Options.DemoLoopback && (!IPAddress.TryParse(address.IdnHost, out var literal) ||
                !RegistrySampleSecurityState.IsLoopback(literal))))
        {
            throw new InvalidOperationException("The sample must have exactly its explicitly configured secure or loopback-only listener.");
        }

        state.SetListenerVerified(true);
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        state.SetListenerVerified(false);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
