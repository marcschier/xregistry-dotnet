using System.Text;
using System.Text.Json.Nodes;

namespace XRegistry.Queries;

public static partial class RegistryQuery
{
    private sealed partial class Evaluation
    {
        private async ValueTask<JsonObject?> FactsAsync(RegistryPath path, QueryExpression expression)
        {
            var first = expression.Attributes[0].Name;
            var key = path.EscapedPath + "|" + first;
            if (_facts.TryGetValue(key, out var known))
            {
                return known;
            }

            budget.Fact();
            var entity = await EntityAsync(path).ConfigureAwait(false);
            JsonObject? facts = null;
            if (entity is not null)
            {
                facts = path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version or RegistryPathKind.Meta
                    ? await ResourceFactsAsync(path, entity, first).ConfigureAwait(false)
                    : await ContainerFactsAsync(path, entity, first).ConfigureAwait(false);
                if (facts is not null)
                {
                    if (expression.Shape.Child(first) is not null && expression.Attributes.Length == 1)
                    {
                        // Logical collection existence does not depend on an HTTP inline projection.
                        facts[first] = new JsonObject();
                    }

                    SetShortSelf(facts, entity.ShortSelf);
                    budget.FactBytes(QueryJson.Encode(facts, budget.Limits.MaxFactBytes).Length);
                }
            }

            _facts.Add(key, facts);
            return facts;
        }

        private async ValueTask<JsonObject?> ContainerFactsAsync(RegistryPath path, RegistryQueryEntity entity, string first)
        {
            var output = QueryJson.Object(entity.Metadata);
            output["self"] = Url(path.EscapedPath);
            output["xid"] = path.EscapedPath;
            var root = path.Kind == RegistryPathKind.Registry;
            var collections = root ? model.Groups.Keys : model.Groups[path.GroupType!].Resources.Keys;
            if (!root)
            {
                output[model.Groups[path.GroupType!].Singular + "id"] = path.GroupId!.Value;
            }

            foreach (var name in collections)
            {
                var key = (root ? "" : path.EscapedPath) + "/" + name;
                output.Remove(name);
                output[name + "url"] = Url(key);
                output[name + "count"] = (await ChildrenAsync(RegistryPath.Parse(key)).ConfigureAwait(false)).Count;
            }

            if (root)
            {
                output.Remove("model");
                output.Remove("modelsource");
                output.Remove("capabilities");
                if (first is "model" or "modelsource" or "capabilities")
                {
                    var configuration = await EntityAsync(RegistryPath.Parse("/" + first)).ConfigureAwait(false);
                    if (configuration is null)
                    {
                        return null;
                    }

                    output[first] = QueryJson.Object(configuration.Metadata);
                }
            }

            return output;
        }

        private async ValueTask<JsonObject?> ResourceFactsAsync(RegistryPath path, RegistryQueryEntity entity, string first)
        {
            var definition = model.Groups[path.GroupType!].Resources[path.ResourceType!];
            var resourcePath = RegistryPath.ForResource(path.GroupType!, path.GroupId!, path.ResourceType!, path.ResourceId!);
            var resourceKey = resourcePath.EscapedPath;
            var resource = path.Kind is RegistryPathKind.Resource or RegistryPathKind.Meta
                ? entity : entity.DefaultVersionId is null ? await EntityAsync(resourcePath).ConfigureAwait(false) :
                    _entities.GetValueOrDefault(resourceKey);
            if (resource is null && entity.DefaultVersionId is null)
            {
                return null;
            }

            if (resource?.IsDanglingCrossReference == true)
            {
                if (path.Kind == RegistryPathKind.Version)
                {
                    throw Diagnostics.Error("invalid_query_source", path.EscapedPath, "A dangling alias cannot provide a Version.");
                }

                if (path.Kind == RegistryPathKind.Resource && first == "meta")
                {
                    var selectedMeta = await EntityAsync(RegistryPath.Parse(resourceKey + "/meta")).ConfigureAwait(false);
                    if (selectedMeta is null)
                    {
                        return null;
                    }

                    if (!selectedMeta.IsDanglingCrossReference ||
                        QueryJson.Text(QueryJson.Object(selectedMeta.Metadata), "xref") != QueryJson.Text(QueryJson.Object(resource.Metadata), "xref"))
                    {
                        throw Diagnostics.Error("invalid_query_source", resourceKey, "The Resource and Meta alias facts do not describe the same selected unit.");
                    }
                }

                return DanglingFacts(path, resource, definition);
            }

            var defaultId = resource is null ? ValidId(entity.DefaultVersionId!, resourceKey) :
                RequiredId(QueryJson.Object(resource.Metadata), "defaultversionid", resourceKey);
            if (path.Kind == RegistryPathKind.Version && entity.DefaultVersionId is { } suppliedDefault &&
                ValidId(suppliedDefault, path.EscapedPath) != defaultId)
            {
                throw Diagnostics.Error("invalid_query_source", path.EscapedPath, "The Version default context contradicts its selected Resource.");
            }

            if (path.Kind == RegistryPathKind.Meta)
            {
                return MetaFacts(path, entity, definition, defaultId);
            }

            var document = definition.HasDocument && (first == definition.Singular || first == definition.Singular + "base64");
            var versionPath = path.Kind == RegistryPathKind.Version ? path : RegistryPath.ForVersion(path.GroupType!, path.GroupId!,
                path.ResourceType!, path.ResourceId!, RegistryId.Parse(defaultId));
            var version = await EntityAsync(versionPath, document).ConfigureAwait(false);
            if (version is null)
            {
                return null;
            }

            if (version.DefaultVersionId is { } documentDefault && ValidId(documentDefault, versionPath.EscapedPath) != defaultId)
            {
                throw Diagnostics.Error("invalid_query_source", versionPath.EscapedPath, "The Version default context changed between source projections.");
            }

            var output = QueryJson.Object(version.Metadata);
            var versionId = RequiredId(output, "versionid", versionPath.EscapedPath);
            if (versionId != versionPath.VersionId!.Value)
            {
                throw Diagnostics.Error("invalid_query_source", versionPath.EscapedPath, "The selected Version identity does not match its logical path.");
            }

            output[definition.Singular + "id"] = path.ResourceId!.Value;
            output["self"] = Url(path.EscapedPath, definition.HasDocument);
            output["xid"] = path.EscapedPath;
            output["isdefault"] = versionId == defaultId;
            output.Remove(definition.Singular);
            output.Remove(definition.Singular + "base64");
            if (document)
            {
                InlineDocument(output, version, definition, first == definition.Singular + "base64");
            }

            if (path.Kind == RegistryPathKind.Resource)
            {
                output["metaurl"] = Url(resourceKey + "/meta");
                output["versionsurl"] = Url(resourceKey + "/versions");
                output["versionscount"] = (await ChildrenAsync(RegistryPath.Parse(resourceKey + "/versions")).ConfigureAwait(false)).Count;
                output.Remove("meta");
                output.Remove("versions");
                if (first == "meta")
                {
                    var metaPath = RegistryPath.Parse(resourceKey + "/meta");
                    var selectedMeta = await EntityAsync(metaPath).ConfigureAwait(false);
                    if (selectedMeta is null)
                    {
                        return null;
                    }

                    var selectedDefault = RequiredId(QueryJson.Object(selectedMeta.Metadata), "defaultversionid", metaPath.EscapedPath);
                    if (selectedMeta.IsDanglingCrossReference || selectedDefault != defaultId)
                    {
                        throw Diagnostics.Error("invalid_query_source", metaPath.EscapedPath,
                            "The Resource and Meta default facts do not describe the same selected unit.");
                    }

                    output["meta"] = MetaFacts(metaPath, selectedMeta, definition, selectedDefault);
                }
            }

            return output;
        }

        private JsonObject MetaFacts(RegistryPath path, RegistryQueryEntity entity, RegistryResourceDefinition definition, string defaultId)
        {
            var key = RegistryPath.ForResource(path.GroupType!, path.GroupId!, path.ResourceType!, path.ResourceId!).EscapedPath;
            var meta = QueryJson.Object(entity.Metadata);
            meta[definition.Singular + "id"] = path.ResourceId!.Value;
            meta["self"] = Url(key + "/meta");
            meta["xid"] = key + "/meta";
            meta["defaultversionurl"] = Url(key + "/versions/" + Uri.EscapeDataString(defaultId), definition.HasDocument);
            SetShortSelf(meta, entity.ShortSelf);
            return meta;
        }

        private JsonObject DanglingFacts(RegistryPath path, RegistryQueryEntity entity, RegistryResourceDefinition definition)
        {
            var attributes = QueryJson.Object(entity.Metadata);
            var xref = QueryJson.Text(attributes, "xref") ??
                throw Diagnostics.Error("invalid_query_source", path.EscapedPath, "A dangling alias requires its original xref.");
            var key = RegistryPath.ForResource(path.GroupType!, path.GroupId!, path.ResourceType!, path.ResourceId!).EscapedPath;
            var meta = new JsonObject
            {
                [definition.Singular + "id"] = path.ResourceId!.Value,
                ["self"] = Url(key + "/meta"),
                ["xid"] = key + "/meta",
                ["xref"] = xref
            };
            SetShortSelf(meta, path.Kind == RegistryPathKind.Meta ? entity.ShortSelf : entity.ShortSelf is null ? null : entity.ShortSelf + "/meta");
            return path.Kind == RegistryPathKind.Meta ? meta : new JsonObject
            {
                [definition.Singular + "id"] = path.ResourceId!.Value,
                ["self"] = Url(key, definition.HasDocument),
                ["xid"] = key,
                ["metaurl"] = Url(key + "/meta"),
                ["meta"] = meta
            };
        }

        private void InlineDocument(JsonObject output, RegistryQueryEntity entity, RegistryResourceDefinition definition, bool binary)
        {
            if (output[definition.Singular + "url"] is not null)
            {
                return;
            }

            var bytes = entity.Document ??
                throw Diagnostics.Error("query_source_incomplete", "", "Document facts require exact bytes or an explicit modeled Document URL.");
            budget.Spend();
            var format = binary ? RegistryDocumentFormat.Binary :
                definition.ResolveDocumentFormat(QueryJson.Text(output, "contenttype") ?? "application/octet-stream");
            if (bytes.Length != 0 && format == RegistryDocumentFormat.Json)
            {
                try
                {
                    var json = RegistryJson.Parse(bytes.Span, budget.Limits.Json with { MaxBytes = budget.Limits.MaxDocumentBytes });
                    output[definition.Singular] = JsonNode.Parse(json.RootElement.GetRawText(), documentOptions: new() { MaxDepth = 256 });
                    return;
                }
                catch (RegistryException exception) when (exception.Diagnostic.Code is "invalid_json" or "invalid_utf8" or "duplicate_member")
                {
                    // Core inline rules require exact binary fallback, not a lossy or guessed JSON value.
                }
            }
            else if (bytes.Length != 0 && format == RegistryDocumentFormat.String)
            {
                try
                {
                    output[definition.Singular] = new UTF8Encoding(false, true).GetString(bytes.Span);
                    return;
                }
                catch (DecoderFallbackException)
                {
                    // Invalid UTF-8 remains exact bytes.
                }
            }

            output[definition.Singular + "base64"] = Convert.ToBase64String(bytes.Span);
        }

        private static string RequiredId(JsonObject metadata, string name, string path)
        {
            var value = QueryJson.Text(metadata, name);
            if (value is null)
            {
                throw Diagnostics.Error("query_source_incomplete", path, "The selected raw data plane is missing a required identity or default-Version fact.");
            }

            return ValidId(value, path);
        }

        private static string ValidId(string value, string path)
        {
            try
            {
                return RegistryId.Parse(value).Value;
            }
            catch (RegistryException exception)
            {
                throw new RegistryException(new("invalid_query_source", path, "The selected data plane contains an invalid identity."), exception);
            }
        }

        private static void SetShortSelf(JsonObject output, string? shortSelf)
        {
            output.Remove("shortself");
            if (shortSelf is not null)
            {
                output["shortself"] = shortSelf;
            }
        }

        private string Url(string key, bool details = false) => publicRoot + (key == "/" ? "" : key) + (details ? "$details" : "");
    }
}
