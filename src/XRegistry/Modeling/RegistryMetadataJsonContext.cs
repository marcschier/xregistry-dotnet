using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace XRegistry;

[JsonSerializable(typeof(JsonNode))]
internal sealed partial class RegistryMetadataJsonContext : JsonSerializerContext;
