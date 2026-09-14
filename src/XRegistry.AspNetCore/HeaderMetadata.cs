using Microsoft.AspNetCore.Http;
using XRegistry.Http;
using XRegistry.Server;

namespace XRegistry.AspNetCore;

internal static class HeaderMetadata
{
    internal static RegistryJson Decode(IHeaderDictionary headers, RegistryResourceDefinition resource, RegistryLimits limits, string path)
    {
        var contentTypeDiscriminator = resource.Attributes.TryGetValue("contenttype", out var definition) && definition.IfValues.Count != 0;
        return RegistryHeaderMetadata.Decode(
            headers.Where(header => header.Key.StartsWith("xRegistry-", StringComparison.OrdinalIgnoreCase) ||
                    contentTypeDiscriminator && header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                .Select(static header => new KeyValuePair<string, IEnumerable<string?>>(header.Key, header.Value)),
            resource, RegistryHeaderMetadataDirection.ClientInput,
            new() { MaxHeaderBytes = limits.MaxHeaderBytes, MaxHeaderCount = limits.MaxHeaderBytes, Json = limits.Json }, path);
    }

    internal static void Encode(RegistryResult result, Dictionary<string, string> headers, int maxBytes)
    {
        var metadata = RegistryHeaderMetadata.Encode(result.Metadata!, result.ResourceDefinition!,
            RegistryHeaderMetadataDirection.Response,
            new() { MaxHeaderBytes = maxBytes, MaxHeaderCount = maxBytes }, result.Path.EscapedPath);
        foreach (var header in metadata)
        {
            headers.Add(header.Key, header.Value);
        }
    }
}
