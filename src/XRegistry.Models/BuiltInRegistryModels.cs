// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Models;

/// <summary>Provides the original, byte-preserved model sources from the pinned specifications.</summary>
public static class BuiltInRegistryModels
{
    /// <summary>Compiles a trusted built-in server preset, explicitly opting Registry descriptions into catalog publication rules.</summary>
    /// <remarks>Other model kinds retain <see cref="Compile"/> semantics. Packaged source bytes are never changed.</remarks>
    public static RegistryModel CompileForServer(RegistryModelKind kind) => kind == RegistryModelKind.Registry
        ? CompileSource(RegistryJson.Parse(CatalogServerSource().ToJsonString()), SourceUri(kind))
        : Compile(kind);

    /// <summary>Compiles the composite server preset with catalog publication rules, preserving packaged includes and Resource imports.</summary>
    public static RegistryModel CompileAllForServer()
    {
        var source = JsonNode.Parse(AllSource().RootElement.GetRawText())!.AsObject();
        source["groups"]!["categories"] = CatalogServerSource()["groups"]!["categories"]!.DeepClone();
        return CompileSource(RegistryJson.Parse(source.ToJsonString()), new Uri("https://xregistry.invalid/spec/all-models.json"));
    }

    /// <summary>Compiles the general-purpose Group models without network acquisition; the opt-in Media Endpoint model is excluded.</summary>
    public static RegistryModel CompileAll() =>
        CompileSource(AllSource(), new Uri("https://xregistry.invalid/spec/all-models.json"));

    /// <summary>Compiles a built-in model using only the packaged model-source resolver, never the network.</summary>
    public static RegistryModel Compile(RegistryModelKind kind)
    {
        using var source = LoadSource(kind);
        return CompileSource(WithHttpProtocolBranches(
            RegistryJson.FromElement(source.RootElement), HttpSelectorOverlayName(kind)), SourceUri(kind));
    }

    /// <summary>Opens an independently owned stream containing the original model-source bytes.</summary>
    /// <param name="kind">The included model to open.</param>
    /// <returns>A stream that the caller must dispose.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The model kind is not defined.</exception>
    /// <exception cref="InvalidOperationException">The package is missing a required model resource.</exception>
    public static Stream OpenSource(RegistryModelKind kind)
    {
        var name = kind switch
        {
            RegistryModelKind.Core => "core",
            RegistryModelKind.Endpoint => "endpoint",
            RegistryModelKind.Message => "message",
            RegistryModelKind.Schema => "schema",
            RegistryModelKind.CloudEvents => "cloudevents",
            RegistryModelKind.Registry => "registry",
            RegistryModelKind.OpenUsd => "openusd",
            RegistryModelKind.Api => "api",
            RegistryModelKind.Publication => "publication",
            RegistryModelKind.Wot => "wot",
            RegistryModelKind.Media => "media-endpoints",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown built-in model.")
        };

        return OpenPackagedSource(name);
    }

    private static Stream OpenPackagedSource(string name) =>
        typeof(BuiltInRegistryModels).Assembly.GetManifestResourceStream(
            $"XRegistry.Models.BuiltIn.{name}.json")
            ?? throw new InvalidOperationException($"The built-in model resource '{name}' is missing.");

    /// <summary>Parses an independently owned original model source without resolving includes.</summary>
    /// <param name="kind">The included model to load.</param>
    /// <returns>A JSON document that the caller must dispose.</returns>
    /// <remarks>
    /// This is a model source, not an effective model. In particular, the CloudEvents
    /// source retains its relative includes for the model compiler's explicit resolver.
    /// No network access, file access, or application runtime is invoked.
    /// </remarks>
    public static JsonDocument LoadSource(RegistryModelKind kind)
    {
        using var stream = OpenSource(kind);
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 128 });
    }

    private static RegistryModel CompileSource(RegistryJson source, Uri sourceUri) =>
        RegistryModel.Compile(source, new RegistryModelCompilationOptions
        {
            SourceUri = sourceUri,
            Resolver = new PackagedResolver()
        });

    private static RegistryJson AllSource() => RegistryJson.Parse("""
        {"$include":"https://xregistry.invalid/spec/core/model.json",
         "groups":{"$includes":[
          "https://xregistry.invalid/spec/cloudevents/model.json#/groups",
          "https://xregistry.invalid/spec/workingdrafts/models/registry/model.json#/groups",
          "https://xregistry.invalid/spec/workingdrafts/models/openusd/model.json#/groups",
          "https://xregistry.invalid/spec/workingdrafts/models/api/model.json#/groups",
          "https://xregistry.invalid/spec/workingdrafts/models/publication/model.json#/groups",
          "https://xregistry.invalid/spec/workingdrafts/models/wot/model.json#/groups"
        ]}}
        """);

    private static JsonObject CatalogServerSource()
    {
        using var packaged = LoadSource(RegistryModelKind.Registry);
        var source = JsonNode.Parse(packaged.RootElement.GetRawText())!.AsObject();
        source["groups"]!["categories"]!["resources"]!["registries"]!["modelcompatiblewith"] = RegistryDomainRules.CatalogModelUri;
        return source;
    }

    private static Uri SourceUri(RegistryModelKind kind)
    {
        var path = kind switch
        {
            RegistryModelKind.Core => "core",
            RegistryModelKind.Endpoint => "endpoint",
            RegistryModelKind.Message => "message",
            RegistryModelKind.Schema => "schema",
            RegistryModelKind.CloudEvents => "cloudevents",
            RegistryModelKind.Registry => "workingdrafts/models/registry",
            RegistryModelKind.OpenUsd => "workingdrafts/models/openusd",
            RegistryModelKind.Api => "workingdrafts/models/api",
            RegistryModelKind.Publication => "workingdrafts/models/publication",
            RegistryModelKind.Wot => "workingdrafts/models/wot",
            RegistryModelKind.Media => "workingdrafts/models/media-endpoints",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new Uri("https://xregistry.invalid/spec/" + path + "/model.json");
    }

    private sealed class PackagedResolver : IRegistryModelResolver
    {
        public RegistryJson Resolve(Uri documentUri)
        {
            var mediaDependency = documentUri.AbsoluteUri switch
            {
                "https://xregistry.invalid/spec/workingdrafts/models/media-endpoints/protocol-options.json" =>
                    "media-endpoints-options",
                "https://xregistry.invalid/spec/workingdrafts/models/media-endpoints/sources/endpoint-model.source.json" =>
                    "media-endpoints-endpoint-source",
                // The Endpoint snapshot's relative Message include must resolve to its matching snapshot.
                "https://xregistry.invalid/spec/workingdrafts/models/media-endpoints/sources/message-model.source.json" or
                "https://xregistry.invalid/spec/workingdrafts/models/media-endpoints/message/model.json" =>
                    "media-endpoints-message-source",
                _ => null
            };
            if (mediaDependency is not null)
            {
                using var stream = OpenPackagedSource(mediaDependency);
                using var source = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 128 });
                var overlay = mediaDependency switch
                {
                    "media-endpoints-endpoint-source" => "http-media-endpoint-source",
                    "media-endpoints-message-source" => "http-media-message-source",
                    _ => null
                };
                return WithHttpProtocolBranches(RegistryJson.FromElement(source.RootElement), overlay);
            }
            foreach (var kind in Enum.GetValues<RegistryModelKind>())
            {
                if (documentUri == SourceUri(kind))
                {
                    using var source = LoadSource(kind);
                    return WithHttpProtocolBranches(
                        RegistryJson.FromElement(source.RootElement), HttpSelectorOverlayName(kind));
                }
            }

            throw new RegistryException(new("model_resolution_required", "",
                "The requested include is not an explicitly packaged model source."));
        }
    }

    private static RegistryJson WithHttpProtocolBranches(RegistryJson source, string? overlayName)
    {
        if (overlayName is null)
        {
            return source;
        }

        using var overlayStream = OpenPackagedSource(overlayName);
        using var overlayDocument = JsonDocument.Parse(overlayStream, new JsonDocumentOptions { MaxDepth = 128 });
        var root = JsonNode.Parse(source.RootElement.GetRawText())!.AsObject();
        var overlay = JsonNode.Parse(overlayDocument.RootElement.GetRawText())!.AsObject();
        var groups = root["groups"] as JsonObject;
        var overlayGroups = overlay["groups"] as JsonObject;
        var changed = AddHttpProtocolBranches(
            groups?["endpoints"]?["attributes"]?["protocol"],
            overlayGroups?["endpoints"]?["attributes"]?["protocol"]);
        changed |= AddHttpProtocolBranches(
            groups?["messagegroups"]?["resources"]?["messages"]?["attributes"]?["protocol"],
            overlayGroups?["messagegroups"]?["resources"]?["messages"]?["attributes"]?["protocol"]);
        return changed ? RegistryJson.Parse(root.ToJsonString()) : source;
    }

    private static bool AddHttpProtocolBranches(JsonNode? protocol, JsonNode? overlayProtocol)
    {
        if (protocol?["ifvalues"] is not JsonObject values || values["HTTP"] is not JsonObject http ||
            overlayProtocol?["ifvalues"] is not JsonObject overlayValues)
        {
            return false;
        }

        var changed = false;
        foreach (var selector in overlayValues.Select(static pair => pair.Key)
            .Where(static selector => selector is "HTTP/1.1" or "HTTP/2" or "HTTP/3"))
        {
            if (!values.ContainsKey(selector))
            {
                values[selector] = http.DeepClone();
                changed = true;
            }
        }
        return changed;
    }

    private static string? HttpSelectorOverlayName(RegistryModelKind kind) => kind switch
    {
        RegistryModelKind.Endpoint => "http-endpoint-model",
        RegistryModelKind.Message => "http-message-model",
        _ => null
    };
}
