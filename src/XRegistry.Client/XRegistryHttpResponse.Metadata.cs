using XRegistry.Http;

namespace XRegistry.Client;

public sealed partial class XRegistryHttpResponse
{
    /// <summary>Reads typed Document metadata from raw response headers without consuming the body.</summary>
    /// <param name="resource">The explicit effective Resource definition for this response.</param>
    /// <returns>Independently owned metadata, including Content-Type and read-only attributes when present.</returns>
    /// <remarks>
    /// This can be called before or after body consumption, repeatedly until response disposal.
    /// It does not infer success from the status or decode a metadata-body response.
    /// Raw repeated field values are preserved for validation; unrelated headers still count toward the budget.
    /// </remarks>
    public RegistryJson ReadHeaderMetadata(RegistryResourceDefinition resource)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(resource);
        return RegistryHeaderMetadata.Decode(
            _response.Headers.NonValidated.Concat(_response.Content.Headers.NonValidated)
                .Select(static header => new KeyValuePair<string, IEnumerable<string?>>(header.Key, header.Value)),
            resource, RegistryHeaderMetadataDirection.Response, _options.HeaderMetadataOptions());
    }
}
