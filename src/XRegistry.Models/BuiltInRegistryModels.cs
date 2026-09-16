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

    /// <summary>Compiles one composite Registry containing every scoped built-in Group model, without network acquisition.</summary>
    public static RegistryModel CompileAll() =>
        CompileSource(AllSource(), new Uri("https://xregistry.invalid/spec/all-models.json"));

    /// <summary>Compiles a built-in model using only the packaged model-source resolver, never the network.</summary>
    public static RegistryModel Compile(RegistryModelKind kind)
    {
        using var source = LoadSource(kind);
        return CompileSource(RegistryJson.FromElement(source.RootElement), SourceUri(kind));
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
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown built-in model.")
        };

        return typeof(BuiltInRegistryModels).Assembly.GetManifestResourceStream(
            $"XRegistry.Models.BuiltIn.{name}.json")
            ?? throw new InvalidOperationException($"The built-in model resource '{name}' is missing.");
    }

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
          "https://xregistry.invalid/spec/workingdrafts/models/openusd/model.json#/groups"
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
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new Uri("https://xregistry.invalid/spec/" + path + "/model.json");
    }

    private sealed class PackagedResolver : IRegistryModelResolver
    {
        public RegistryJson Resolve(Uri documentUri)
        {
            foreach (var kind in Enum.GetValues<RegistryModelKind>())
            {
                if (documentUri == SourceUri(kind))
                {
                    using var source = LoadSource(kind);
                    return RegistryJson.FromElement(source.RootElement);
                }
            }

            throw new RegistryException(new("model_resolution_required", "",
                "The requested include is not an explicitly packaged model source."));
        }
    }
}
