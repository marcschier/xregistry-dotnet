using System.Net;
using System.Net.Sockets;

namespace XRegistry.Client;

/// <summary>Constrains outbound registry HTTP connections to an explicitly configured origin.</summary>
public sealed class RegistryHttpConnectionPolicy
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _scheme;
    private readonly bool _loopbackOnly;
    private readonly bool _allowPrivateOrigin;

    /// <summary>Creates a policy for one registry or explicitly authorized service origin.</summary>
    /// <param name="origin">An absolute HTTPS URI without credentials, query, or fragment.</param>
    /// <param name="allowLoopbackHttp">Allows HTTP only for literal loopback or localhost origins.</param>
    /// <param name="allowPrivateOrigin">Allows RFC1918/ULA addresses for this HTTPS origin, not all origins.</param>
    /// <exception cref="ArgumentException">The configured origin is not safe for this policy.</exception>
    public RegistryHttpConnectionPolicy(
        Uri origin, bool allowLoopbackHttp = false, bool allowPrivateOrigin = false)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0)
        {
            throw new ArgumentException("An absolute credential-free HTTP origin is required.", nameof(origin));
        }

        _host = origin.IdnHost;
        _port = origin.Port;
        _scheme = origin.Scheme;
        _allowPrivateOrigin = allowPrivateOrigin;
        if (_scheme == Uri.UriSchemeHttp)
        {
            var literalLoopback = IPAddress.TryParse(_host, out var address) && IPAddress.IsLoopback(address);
            if (!allowLoopbackHttp ||
                (!literalLoopback && !StringComparer.OrdinalIgnoreCase.Equals(_host, "localhost")))
            {
                throw new ArgumentException("Plaintext HTTP is allowed only for an explicit loopback origin.", nameof(origin));
            }

            _loopbackOnly = true;
        }
        else if (_scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Only HTTPS and explicit loopback HTTP origins are supported.", nameof(origin));
        }
    }

    /// <summary>Checks origin identity without authorizing a different destination or credentials.</summary>
    /// <param name="uri">The request or proposed redirect URI.</param>
    /// <returns>Whether its scheme, IDN host, and port match the configured origin.</returns>
    public bool IsOriginAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri && uri.UserInfo.Length == 0 &&
            StringComparer.Ordinal.Equals(uri.Scheme, _scheme) &&
            StringComparer.OrdinalIgnoreCase.Equals(uri.IdnHost, _host) &&
            uri.Port == _port;
    }

    /// <summary>Checks an address under this origin's explicit network policy.</summary>
    /// <param name="address">The actual resolved address, including IPv4-mapped IPv6 addresses.</param>
    /// <returns>Whether the address is permitted for an actual connection.</returns>
    public bool IsAddressAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (_loopbackOnly)
        {
            return IPAddress.IsLoopback(address);
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var privateAddress = bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
            if (privateAddress)
            {
                return _allowPrivateOrigin;
            }

            return bytes[0] is not (0 or 127) && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) &&
                !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
                !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if ((bytes[0] & 0xfe) == 0xfc)
        {
            return _allowPrivateOrigin;
        }

        // Restrict to global-unicast allocation, excluding protocol-assignment,
        // documentation and 6to4 ranges rather than trusting translated targets.
        return address.ScopeId == 0 &&
            (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }

    /// <summary>Creates an owned client with redirects, cookies, ambient credentials, and proxies disabled.</summary>
    /// <param name="timeout">A finite operation timeout, or the default thirty seconds.</param>
    /// <returns>A client whose caller owns its disposal. It has no automatic mutation retry handler.</returns>
    public HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var requestTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "A finite timeout up to ten minutes is required.");
        }

        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, requestTimeout.TotalSeconds)),
            MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 64,
            ConnectCallback = ConnectAsync
        };
        return new HttpClient(new OriginHandler(this, sockets), disposeHandler: true)
        {
            Timeout = requestTimeout
        };
    }

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (context.InitialRequestMessage.RequestUri is null ||
            !IsOriginAllowed(context.InitialRequestMessage.RequestUri) ||
            !StringComparer.OrdinalIgnoreCase.Equals(context.DnsEndPoint.Host, _host) ||
            context.DnsEndPoint.Port != _port)
        {
            throw new HttpRequestException("The connection destination is outside the configured origin.");
        }

        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsAddressAllowed(address)))
        {
            throw new HttpRequestException("The configured origin resolves to an unauthorized destination.");
        }

        SocketException? failure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var connected = false;
            try
            {
                await socket.ConnectAsync(address, _port, cancellationToken).ConfigureAwait(false);
                connected = true;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                failure = exception;
            }
            finally
            {
                if (!connected)
                {
                    socket.Dispose();
                }
            }
        }

        throw new HttpRequestException("No authorized origin address accepted the connection.", failure);
    }

    private sealed class OriginHandler(RegistryHttpConnectionPolicy policy, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ValidateOrigin(request);
            return base.Send(request, cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ValidateOrigin(request);
            return base.SendAsync(request, cancellationToken);
        }

        private void ValidateOrigin(HttpRequestMessage request)
        {
            if (request.RequestUri is null || !policy.IsOriginAllowed(request.RequestUri) ||
                request.Headers.Host is not null)
            {
                throw new HttpRequestException("The request origin or Host override is not authorized.");
            }
        }
    }
}
