using System.Net;
using System.Text.Json;

namespace XRegistry.Client;

public sealed partial class XRegistryHttpClient
{
    /// <summary>Reads one explicitly selected discovery endpoint without following its advertised Registry URLs.</summary>
    /// <remarks>
    /// Registry and host discovery are separate requests. An unavailable endpoint is an error, not an empty result
    /// or permission to retry another location. Host discovery invokes the credential provider for the host-wide
    /// discovery URI; the provider can decline or reject credentials for that path.
    /// </remarks>
    public async ValueTask<RegistryDiscovery> DiscoverAsync(
        RegistryDiscoveryLocation location = RegistryDiscoveryLocation.Registry,
        int maxRegistries = 1024,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRegistries);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRegistries, 65536);
        var uri = location switch
        {
            RegistryDiscoveryLocation.Registry => BuildUri(".xregistry", null),
            RegistryDiscoveryLocation.Host => new Uri(_root, "/.well-known/xregistry"),
            _ => throw new ArgumentOutOfRangeException(nameof(location))
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException("The discovery endpoint did not return HTTP 200.", null, response.StatusCode);
        }

        using var metadata = await response.ReadMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (metadata.RootElement.ValueKind != JsonValueKind.Object ||
            !metadata.RootElement.TryGetProperty("registries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > maxRegistries)
        {
            throw new InvalidDataException("Discovery requires a bounded registries array.");
        }

        var result = new List<Uri>(entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(entry.GetString(), UriKind.Absolute, out var registry) ||
                !registry.IsWellFormedOriginalString())
            {
                throw new InvalidDataException("Each discovery advertisement must be an absolute Registry URL.");
            }

            result.Add(registry);
        }

        return new RegistryDiscovery(uri, result.ToArray());
    }
}
