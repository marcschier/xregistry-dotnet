// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using XRegistry.Client;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>Native HTTPS Distribution acquisition under the shared Client origin/connection policy.</summary>
/// <remarks>No automatic redirects, token negotiation, credential forwarding, decompression or root retries are performed.</remarks>
public sealed partial class OciDistributionClient : IOciObjectReader, IOciPublisher, IDisposable
{
    private readonly HttpClient client;
    private readonly RegistryHttpConnectionPolicy policy;
    private readonly OciDistributionOptions options;
    private readonly bool ownsClient;
    private bool disposed;

    /// <summary>Creates an owned HTTP client using the shared policy's DNS and actual-connection checks.</summary>
    public OciDistributionClient(OciRepository repository, RegistryHttpConnectionPolicy policy, OciDistributionOptions? options = null)
        : this(repository, CreateClient(repository, policy, options), policy, options, true) { }

    /// <summary>Uses a caller-owned HTTP client implementing the supplied policy and exact-byte transport contract.</summary>
    /// <remarks>
    /// The caller must disable automatic redirects, decompression, ambient credentials, cookies and proxies,
    /// and enforce the supplied policy at actual connections. Prefer the policy-owned constructor outside
    /// controlled test transports. Merely supplying a policy does not retrofit an arbitrary handler.
    /// </remarks>
    public OciDistributionClient(OciRepository repository, HttpClient client, RegistryHttpConnectionPolicy policy,
        OciDistributionOptions? options = null) : this(repository, client, policy, options, false) { }

    private OciDistributionClient(OciRepository repository, HttpClient client, RegistryHttpConnectionPolicy policy,
        OciDistributionOptions? options, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        Validate(repository, policy, options);
        CheckHeaders(client);
        Repository = repository;
        Context = new("oci", repository.Endpoint);
        this.client = client;
        this.policy = policy;
        this.options = options ?? new();
        this.ownsClient = ownsClient;
    }

    /// <summary>The selected repository; it is not a Registry XID prefix.</summary>
    public OciRepository Repository { get; }
    /// <inheritdoc />
    public NativeRegistryContext Context { get; }

    private static void Validate(OciRepository repository, RegistryHttpConnectionPolicy policy, OciDistributionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(policy);
        (options ?? new()).Validate();
        if (!policy.IsOriginAllowed(repository.Origin))
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "The shared HTTP policy does not authorize this OCI origin.");
        }
    }

    private static HttpClient CreateClient(OciRepository repository, RegistryHttpConnectionPolicy policy, OciDistributionOptions? options)
    {
        Validate(repository, policy, options);
        return policy.CreateClient(options?.RequestTimeout);
    }

    /// <inheritdoc />
    public ValueTask<OciObjectResponse?> OpenManifestAsync(string reference, CancellationToken cancellationToken = default) =>
        ReadAsync(Repository.ManifestUri(reference), true, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OciObjectResponse?> OpenBlobAsync(string digest, CancellationToken cancellationToken = default) =>
        ReadAsync(Repository.BlobUri(digest), false, cancellationToken);

    private async ValueTask<OciObjectResponse?> ReadAsync(Uri uri, bool manifest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (manifest)
        {
            request.Headers.Accept.Add(new(OciFormat.Index));
            request.Headers.Accept.Add(new(OciFormat.Manifest));
        }
        HttpResponseMessage? response = null;
        var transferred = false;
        try
        {
            response = await SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
            Status(response);
            if (response.StatusCode != HttpStatusCode.OK) { throw OciJson.Invalid("A complete Distribution read requires an OK response."); }
            if (response.Content.Headers.ContentEncoding.Count != 0)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation, "OCI exact-byte transport does not accept content encodings.");
            }
            var type = response.Content.Headers.ContentType?.ToString();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (manifest && !string.Equals(mediaType, OciFormat.Index, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mediaType, OciFormat.Manifest, StringComparison.OrdinalIgnoreCase))
            {
                throw OciJson.Invalid("A Distribution manifest response requires a standard OCI Content-Type.");
            }
            var length = response.Content.Headers.ContentLength;
            if (length > options.MaxResponseBytes)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded, "The OCI response exceeds the transport byte budget.");
            }
            var digest = ResponseDigest(response);
            var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var stream = new OciHttpBodyStream(body, options.MaxResponseBytes, deadline.Token, cancellationToken);
            var result = new OciObjectResponse(stream, type, digest, length, new ResponseOwner(response, deadline));
            transferred = true;
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "The OCI HTTP request deadline expired.", innerException: exception);
        }
        finally
        {
            if (!transferred)
            {
                response?.Dispose();
                deadline.Dispose();
            }
        }
    }

    private async ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        CheckHeaders(client);
        if (!policy.IsOriginAllowed(uri) || uri.Fragment.Length != 0)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "The OCI request destination is not authorized.");
        }
        if (uri.AbsoluteUri.Length > options.MaxUriLength)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The OCI request URI exceeds its transport budget.");
        }
        if (options.AuthorizationProvider is not null)
        {
            request.Headers.Authorization = await options.AuthorizationProvider(uri, cancellationToken).AsTask()
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
            {
                response.Dispose();
                throw new FederationException(FederationErrorCode.PolicyDenied, "The supplied transport followed an unauthorized redirect.");
            }
            return response;
        }
        catch (HttpRequestException exception)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "OCI HTTPS transport failed.", innerException: exception);
        }
    }

    private static void Status(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "OCI redirects require a separately authorized destination and are not followed.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized &&
            response.Headers.WwwAuthenticate.Any(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "OCI token negotiation is unsupported. Supply authorized repository credentials; token realms are never fetched implicitly.");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "OCI repository authentication or authorization was denied.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "The selected OCI repository did not complete the request.");
        }
    }

    private static string? ResponseDigest(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Docker-Content-Digest", out var digests)) { return null; }
        var values = digests.ToArray();
        if (values.Length != 1) { throw OciObjectSession.Integrity(); }
        return values[0];
    }

    private static void CheckHeaders(HttpClient client)
    {
        if (client.DefaultRequestHeaders.Authorization is not null || client.DefaultRequestHeaders.Contains("Cookie") ||
            client.DefaultRequestHeaders.Contains("Proxy-Authorization") || client.DefaultRequestHeaders.Host is not null)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "OCI credentials and routing cannot come from ambient client headers.");
        }
    }

    /// <summary>Disposes only an internally created client. The caller owns any supplied client.</summary>
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        if (ownsClient) { client.Dispose(); }
    }

    private sealed class ResponseOwner(HttpResponseMessage response, CancellationTokenSource deadline) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose();
            deadline.Dispose();
        }
    }
}
