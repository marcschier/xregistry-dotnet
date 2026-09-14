using System.Text.Json.Serialization;

namespace XRegistry.Federation;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class FederationSerializationContext : JsonSerializerContext;
