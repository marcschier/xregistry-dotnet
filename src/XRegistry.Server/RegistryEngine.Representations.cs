// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private RegistryJson DefaultCapabilities()
    {
        var mutable = !IsReadOnly;
        var available = new JsonObject
        {
            ["capabilities"] = new JsonObject { ["mutable"] = mutable && _options.AllowCapabilityUpdates },
            ["capabilitiesoffered"] = new JsonObject { ["mutable"] = false },
            ["entities"] = new JsonObject { ["mutable"] = mutable },
            ["export"] = new JsonObject { ["mutable"] = false },
            ["model"] = new JsonObject { ["mutable"] = false },
            ["modelsource"] = new JsonObject { ["mutable"] = mutable }
        };
        return ServerJson.Own(new JsonObject
        {
            ["available"] = available,
            ["flags"] = JsonNode.Parse("""["binary","collections","doc","epoch","filter","ignore","inline","setdefaultversionid","sort","specversion"]"""),
            ["formats"] = new JsonArray((_options.ResourceValidator?.Formats ?? []).Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["compatibilities"] = CompatibilityCapabilities(),
            ["ignores"] = new JsonArray(RequestFlags.IgnoreValues.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["mutable"] = new JsonArray(),
            ["pagination"] = true,
            ["shortself"] = false,
            ["specversions"] = JsonNode.Parse("""["1.0-rc4"]"""),
            ["versionmodes"] = JsonNode.Parse("""["manual","createdat","modifiedat","semver"]""")
        }, Limits.Json);
    }

    private JsonObject CompatibilityCapabilities()
    {
        var result = new JsonObject();
        if (_options.ResourceValidator is { } validator)
        {
            foreach (var format in validator.Compatibilities)
            {
                result[format.Key] = new JsonArray(format.Value.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());
            }
        }

        return result;
    }

    private RegistryJson Offered()
    {
        var result = new JsonObject();
        foreach (var property in ServerJson.Object(DefaultCapabilities()))
        {
            result[property.Key] = Offer(property.Value!);
        }

        result["pagination"]!["enum"] = JsonNode.Parse("[false,true]");
        result["shortself"]!["enum"] = JsonNode.Parse(SupportsShortSelf ? "[false,true]" : "[false]");
        if (!IsReadOnly)
        {
            result["available"]!["attributes"]!["entities"]!["attributes"]!["mutable"]!["enum"] = JsonNode.Parse("[false,true]");
            result["available"]!["attributes"]!["modelsource"]!["attributes"]!["mutable"]!["enum"] = JsonNode.Parse("[false,true]");
        }

        return ServerJson.Own(result, Limits.Json);
        static JsonObject Offer(JsonNode value)
        {
            if (value is JsonObject obj)
            {
                var attributes = new JsonObject();
                foreach (var property in obj)
                {
                    attributes[property.Key] = Offer(property.Value!);
                }

                return new JsonObject { ["type"] = "object", ["attributes"] = attributes };
            }

            if (value is JsonArray array)
            {
                return new JsonObject { ["type"] = "array", ["item"] = new JsonObject { ["type"] = "string" }, ["enum"] = array.DeepClone() };
            }

            return new JsonObject { ["type"] = "boolean", ["enum"] = new JsonArray(value.DeepClone()) };
        }
    }

    private sealed partial class Request
    {
        private readonly Dictionary<string, List<Entity>> _visible = new(StringComparer.Ordinal);
        private readonly Dictionary<JsonNode, long> _responseSizes = new(ReferenceEqualityComparer.Instance);

        private async ValueTask<RegistryResult> RenderAsync()
        {
            if (_operation.Action == RegistryAction.Delete)
            {
                return new(RegistryResultKind.NoContent, _responsePath);
            }

            var path = _responsePath.Kind == RegistryPathKind.Export ? RegistryPath.Parse("/") : _responsePath;
            ValidateInline(path, _flags.Inline);
            await InitializeQueryAsync(path).ConfigureAwait(false);
            JsonObject output;
            RegistryResourceDefinition? resource = path.ResourceType is null ? null : ResourceDefinition(path);
            switch (path.Kind)
            {
                case RegistryPathKind.Model:
                    output = ServerJson.Object(_model.EffectiveModel);
                    break;
                case RegistryPathKind.ModelSource:
                    output = ServerJson.Object(_modelSource);
                    break;
                case RegistryPathKind.Capabilities:
                    output = ServerJson.Object(_capabilities.Metadata);
                    break;
                case RegistryPathKind.CapabilitiesOffered:
                    output = ServerJson.Object(_engine.Offered());
                    break;
                case RegistryPathKind.Discovery:
                    var peers = _engine._options.DiscoveryRegistries.Count == 0 ? [_engine.PublicRoot] : _engine._options.DiscoveryRegistries;
                    output = new JsonObject
                    {
                        ["registries"] = new JsonArray(peers.Select(static peer =>
                            (JsonNode?)JsonValue.Create(peer.AbsoluteUri.TrimEnd('/'))).ToArray())
                    };
                    break;
                case RegistryPathKind.GroupCollection:
                case RegistryPathKind.ResourceCollection:
                case RegistryPathKind.VersionCollection:
                    EnsureCollectionExists(path);
                    if (_capabilities.Pagination && _operation.Action is RegistryAction.Read or RegistryAction.Head)
                    {
                        return await PreparePageAsync(path).ConfigureAwait(false);
                    }

                    output = await RenderCollectionAsync(path, _flags.Inline, "", Selection()).ConfigureAwait(false);
                    break;
                default:
                    output = await RenderEntityAsync(path, _flags.Inline, "").ConfigureAwait(false);
                    break;
            }

            if (_operation.Action == RegistryAction.Post && path.Kind is RegistryPathKind.Registry or RegistryPathKind.Group)
            {
                output = new JsonObject();
                var input = _ownerPostCollections ??
                    throw new InvalidOperationException("The processed owner POST collections are unavailable.");
                foreach (var property in input)
                {
                    var nestedPath = RegistryPath.Parse((path.Kind == RegistryPathKind.Registry ? "" : ServerJson.Key(path)) + "/" + property.Key);
                    output[property.Key] = await RenderCollectionAsync(nestedPath, RequestFlags.Child(_flags.Inline, property.Key),
                        "/" + Pointer(property.Key), Map(property.Value, nestedPath.EscapedPath).Select(static entry => entry.Key).ToHashSet(StringComparer.Ordinal))
                        .ConfigureAwait(false);
                }
            }

            var isDocument = path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version && resource!.HasDocument &&
                !path.IsDetails && !_flags.Doc;
            RegistryDocument? document = null;
            Uri? redirect = null;
            if (isDocument)
            {
                var version = await VersionForReadAsync(path).ConfigureAwait(false);
                var reference = version is null ? null : ServerJson.Text(version.Attributes, resource!.Singular + "url");
                if (reference is not null)
                {
                    redirect = DocumentReference(version!.Key, reference);
                    await AuthorizeDocumentReferenceAsync(redirect).ConfigureAwait(false);
                }
                else
                {
                    document = new(version is null ? [] : await ReadDocumentAsync(version).ConfigureAwait(false));
                }

                output["self"] = _engine.Url(ServerJson.Key(path));
            }

            var metadata = ServerJson.Own(output, _engine.Limits.Json with { MaxBytes = _engine.Limits.MaxResponseBytes });
            if (document?.Length > _engine.Limits.MaxResponseBytes)
            {
                throw ServerErrors.Create("too_large", path.EscapedPath, "The response Document exceeds its byte budget.");
            }

            return new(redirect is null ? RegistryResultKind.Success : RegistryResultKind.SeeOther, _responsePath, metadata)
            {
                Location = redirect,
                Document = document,
                IsDocument = isDocument,
                ContentType = isDocument ? ServerJson.Text(output, "contenttype") : "application/json; charset=utf-8",
                ResourceDefinition = resource
            };
        }

        private HashSet<string>? Selection() =>
            _operation.Action is RegistryAction.Post or RegistryAction.Patch && _operation.Metadata is not null
                ? ServerJson.Object(_operation.Metadata).Select(static property => property.Key).ToHashSet(StringComparer.Ordinal) : null;

        private void EnsureCollectionExists(RegistryPath path)
        {
            if (path.Kind == RegistryPathKind.ResourceCollection)
            {
                Require(ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!)));
            }
            else if (path.Kind == RegistryPathKind.VersionCollection)
            {
                var resource = Require(ResourceKey(path));
                if (DocumentView && resource.Attributes["xref"] is not null)
                {
                    throw ServerErrors.Create("cannot_doc_xref", ResourceKey(path), "Cross-reference Versions do not exist in document view.");
                }
            }
        }

        private async ValueTask<List<Entity>> AuthorizedChildrenAsync(string key)
        {
            if (_visible.TryGetValue(key, out var known))
            {
                return known;
            }

            var actualKey = key;
            var collectionPath = RegistryPath.Parse(key);
            if (collectionPath.Kind == RegistryPathKind.VersionCollection)
            {
                var source = Require(ResourceKey(collectionPath));
                if (source.Attributes["xref"] is not null)
                {
                    if (DocumentView)
                    {
                        throw ServerErrors.Create("cannot_doc_xref", ResourceKey(collectionPath), "Cross-reference Versions do not exist in document view.");
                    }

                    var target = await ResolveCrossReferenceAsync(source).ConfigureAwait(false);
                    if (target is null)
                    {
                        return [];
                    }

                    actualKey = target.Key + "/versions";
                }
            }

            var result = new List<Entity>();
            if (_queryBudget is not null && !await CanReadAsync(collectionPath).ConfigureAwait(false))
            {
                return result;
            }

            foreach (var entity in Children(actualKey))
            {
                if (await CanReadAsync(RegistryPath.Parse(entity.Key)).ConfigureAwait(false) &&
                    (actualKey == key || await CanReadAsync(
                        RegistryPath.Parse(key + "/" + entity.Key[(entity.Key.LastIndexOf('/') + 1)..])).ConfigureAwait(false)))
                {
                    if (_queryBudget is not null && RegistryPath.Parse(entity.Key).Kind == RegistryPathKind.Resource &&
                        entity.Attributes["xref"] is null && await VersionForReadAsync(RegistryPath.Parse(entity.Key)).ConfigureAwait(false) is null)
                    {
                        continue;
                    }

                    result.Add(entity);
                }
            }

            _visible.Add(key, result);
            return result;
        }

        private async ValueTask<JsonObject> RenderCollectionAsync(RegistryPath path, IReadOnlyList<string> inline,
            string pointer, HashSet<string>? selected = null)
        {
            var output = new JsonObject();
            long size = 2;
            foreach (var entity in await OrderedChildrenAsync(path, pointer).ConfigureAwait(false))
            {
                Work();
                var childPath = RegistryPath.Parse(entity.Key);
                var id = childPath.Kind switch
                {
                    RegistryPathKind.Group => childPath.GroupId!.Value,
                    RegistryPathKind.Resource => childPath.ResourceId!.Value,
                    _ => childPath.VersionId!.Value
                };
                if (selected is null || selected.Contains(id))
                {
                    childPath = RegistryPath.Parse(ServerJson.Key(path) + "/" + Uri.EscapeDataString(id));
                    if (selected is not null && _ignoredReadonly.Contains(ServerJson.Key(childPath)))
                    {
                        continue;
                    }

                    var child = await RenderEntityAsync(childPath, inline, pointer + "/" + Pointer(id)).ConfigureAwait(false);
                    size += PropertyNameSize(id) + ResponseSize(child) + (output.Count == 0 ? 0 : 1);
                    CheckResponseSize(size);
                    output[id] = child;
                }
            }

            _responseSizes[output] = size;
            return output;
        }

        private async ValueTask<JsonObject> RenderEntityAsync(RegistryPath path, IReadOnlyList<string> inline, string pointer)
        {
            Work();
            var key = ServerJson.Key(path);
            var entity = path.Kind == RegistryPathKind.Version ? await VersionForReadAsync(path).ConfigureAwait(false) ??
                throw ServerErrors.Create("not_found", key, "The requested Version is not accessible.") :
                Require(path.Kind == RegistryPathKind.Meta ? ResourceKey(path) : key);
            JsonObject output;
            if (path.Kind is RegistryPathKind.Resource or RegistryPathKind.Meta or RegistryPathKind.Version)
            {
                output = await RenderResourcePartAsync(path, entity, inline, pointer).ConfigureAwait(false);
            }
            else
            {
                output = (JsonObject)entity.Attributes.DeepClone();
                var root = path.Kind == RegistryPathKind.Registry;
                var collections = root ? _model.Groups.Keys : _model.Groups[path.GroupType!].Resources.Keys;
                output["self"] = DocumentView ? Relative(pointer) : _engine.Url(key);
                output["xid"] = key;
                foreach (var name in collections)
                {
                    var collectionKey = (root ? "" : key) + "/" + name;
                    var included = RequestFlags.Includes(inline, name);
                    var count = (await VisibleChildrenAsync(collectionKey).ConfigureAwait(false)).Count;
                    output[name + "url"] = DocumentView && included ? Relative(pointer + "/" + Pointer(name)) : CollectionUrl(collectionKey, count);
                    output[name + "count"] = count;
                    if (included)
                    {
                        output[name] = await RenderCollectionAsync(RegistryPath.Parse(collectionKey), RequestFlags.Child(inline, name),
                            pointer + "/" + Pointer(name)).ConfigureAwait(false);
                    }
                    else
                    {
                        output.Remove(name);
                    }

                    _responseSizes.Remove(output);
                    _ = ResponseSize(output);
                }

                if (root)
                {
                    foreach (var name in new[] { "model", "modelsource", "capabilities" })
                    {
                        output.Remove(name);
                        if (RequestFlags.Includes(inline, name, configuration: true))
                        {
                            CheckAvailability(RegistryPath.Parse("/" + name), RegistryAction.Read, _capabilities);
                            await RequireReadAsync(RegistryPath.Parse("/" + name)).ConfigureAwait(false);
                            output[name] = JsonNode.Parse((name == "model" ? _model.EffectiveModel : name == "modelsource" ?
                                _modelSource : _capabilities.Metadata).RootElement.GetRawText());
                        }
                    }
                }

                if (_flags.Collections && pointer.Length == 0)
                {
                    var onlyCollections = new JsonObject();
                    foreach (var name in collections)
                    {
                        onlyCollections[name] = output[name]?.DeepClone();
                    }

                    output = onlyCollections;
                }
            }

            output.Remove("shortself");
            if (_capabilities.ShortSelf && !DocumentView && !(_flags.Collections && pointer.Length == 0))
            {
                output["shortself"] = ShortSelf(path);
            }

            _responseSizes.Remove(output);
            _ = ResponseSize(output);

            return output;
        }

        private async ValueTask<JsonObject> RenderResourcePartAsync(RegistryPath path, Entity entity,
            IReadOnlyList<string> inline, string pointer)
        {
            var resourceKey = ResourceKey(path);
            var source = Require(resourceKey);
            var resource = source;
            var definition = ResourceDefinition(path);
            if (source.Attributes["xref"] is not null)
            {
                if (DocumentView && path.Kind == RegistryPathKind.Version)
                {
                    throw ServerErrors.Create("cannot_doc_xref", resourceKey, "Cross-reference Versions do not exist in document view.");
                }

                var target = await ResolveCrossReferenceAsync(source).ConfigureAwait(false);
                if (target is null)
                {
                    return DanglingResource(path, source, pointer);
                }

                resource = target;
            }

            var defaultId = ServerJson.Text(resource.Attributes, "defaultversionid")!;
            var versionsInline = RequestFlags.Includes(inline, "versions");
            if (path.Kind == RegistryPathKind.Meta)
            {
                var meta = (JsonObject)resource.Attributes.DeepClone();
                meta[definition.Singular + "id"] = path.ResourceId!.Value;
                meta["self"] = DocumentView ? Relative(pointer) : _engine.Url(resourceKey + "/meta");
                meta["xid"] = resourceKey + "/meta";
                if (source.Attributes["xref"] is not null)
                {
                    meta["xref"] = source.Attributes["xref"]!.DeepClone();
                }

                meta["defaultversionurl"] = _engine.Url(resourceKey + "/versions/" + Uri.EscapeDataString(defaultId), definition.HasDocument);
                meta.Remove("shortself");
                if (_capabilities.ShortSelf && !DocumentView)
                {
                    meta["shortself"] = ShortSelf(path);
                }

                return meta;
            }

            var version = path.Kind == RegistryPathKind.Version ? entity : await VersionForReadAsync(path).ConfigureAwait(false) ??
                throw ServerErrors.Create("forbidden", resourceKey, "The default Version is not accessible.");
            var output = DocumentView && path.Kind == RegistryPathKind.Resource ? new JsonObject() : (JsonObject)version.Attributes.DeepClone();
            var key = ServerJson.Key(path);
            output[definition.Singular + "id"] = path.ResourceId!.Value;
            output["self"] = DocumentView ? Relative(pointer) : _engine.Url(key, definition.HasDocument);
            output["xid"] = key;
            if (!DocumentView || path.Kind == RegistryPathKind.Version)
            {
                output["isdefault"] = Id(version) == defaultId;
                if (DocumentView)
                {
                    output.Remove("formatvalidated");
                    output.Remove("formatvalidatedreason");
                    output.Remove("compatibilityvalidated");
                    output.Remove("compatibilityvalidatedreason");
                }

                if (definition.HasDocument)
                {
                    output.Remove(definition.Singular);
                    output.Remove(definition.Singular + "base64");
                    if (RequestFlags.Includes(inline, definition.Singular))
                    {
                        await InlineDocumentAsync(output, version, definition).ConfigureAwait(false);
                    }
                }
            }

            if (path.Kind == RegistryPathKind.Resource)
            {
                var metaInline = RequestFlags.Includes(inline, "meta");
                output["metaurl"] = DocumentView && metaInline ? Relative(pointer + "/meta") : _engine.Url(resourceKey + "/meta");
                var visibleVersions = await VisibleChildrenAsync(resourceKey + "/versions").ConfigureAwait(false);
                var count = visibleVersions.Count;
                output["versionsurl"] = DocumentView && versionsInline ? Relative(pointer + "/versions") : CollectionUrl(resourceKey + "/versions", count);
                output["versionscount"] = count;
                if (metaInline)
                {
                    await RequireReadAsync(RegistryPath.Parse(resourceKey + "/meta")).ConfigureAwait(false);
                    var meta = await RenderResourcePartAsync(RegistryPath.Parse(resourceKey + "/meta"), resource, [], pointer + "/meta")
                        .ConfigureAwait(false);
                    if (DocumentView && versionsInline && visibleVersions.Any(version => Id(version) == defaultId))
                    {
                        meta["defaultversionurl"] = Relative(pointer + "/versions/" + Pointer(defaultId));
                    }

                    output["meta"] = meta;
                }

                if (versionsInline)
                {
                    output["versions"] = await RenderCollectionAsync(RegistryPath.Parse(resourceKey + "/versions"),
                        RequestFlags.Child(inline, "versions"), pointer + "/versions").ConfigureAwait(false);
                }
            }

            return output;
        }

        private async ValueTask<byte[]> ReadDocumentAsync(Entity version, CancellationToken? cancellationToken = null)
        {
            var token = cancellationToken ?? _queryBudget?.Shared.CancellationToken ?? _ct;
            try
            {
                token.ThrowIfCancellationRequested();
                if (version.Document is not null)
                {
                    return version.Document;
                }

                if (!version.HadDocument)
                {
                    throw new InvalidDataException("The stored document-bearing Version has no Document.");
                }

                using var stream = _snapshot.OpenDocument(version.Key, token);
                version.Document = await BoundedContent.ReadAsync(stream, _engine.Limits.MaxDocumentBytes, token).ConfigureAwait(false);
                WorkingBytes(version.Document.Length);
                return version.Document;
            }
            catch (OperationCanceledException exception) when (!_ct.IsCancellationRequested &&
                _queryBudget?.Shared.CancellationToken.IsCancellationRequested == true)
            {
                throw new RegistryException(new("server_busy", version.Key, "The query Document-read deadline is exhausted."), exception);
            }
        }

        private async ValueTask InlineDocumentAsync(JsonObject output, Entity version, RegistryResourceDefinition definition)
        {
            if (version.Attributes[definition.Singular + "url"] is not null)
            {
                return;
            }

            var bytes = await ReadDocumentAsync(version).ConfigureAwait(false);
            var format = _flags.Binary ? RegistryDocumentFormat.Binary :
                definition.ResolveDocumentFormat(ServerJson.Text(version.Attributes, "contenttype") ?? "application/octet-stream");
            if (bytes.Length != 0 && format == RegistryDocumentFormat.Json)
            {
                try
                {
                    var json = RegistryJson.Parse(bytes, _engine.Limits.Json with { MaxBytes = _engine.Limits.MaxResponseBytes });
                    output[definition.Singular] = JsonNode.Parse(json.RootElement.GetRawText());
                    return;
                }
                catch (RegistryException exception) when (exception.Diagnostic.Code is "invalid_json" or "invalid_utf8" or "duplicate_member")
                {
                    // The binding explicitly requires binary fallback for a document that is not valid inline JSON.
                }
            }
            else if (bytes.Length != 0 && format == RegistryDocumentFormat.String)
            {
                try
                {
                    output[definition.Singular] = new UTF8Encoding(false, true).GetString(bytes);
                    return;
                }
                catch (DecoderFallbackException)
                {
                    // Invalid UTF-8 must remain exact bytes, never replacement characters.
                }
            }

            output[definition.Singular + "base64"] = Convert.ToBase64String(bytes);
        }

        private void ValidateInline(RegistryPath path, IReadOnlyList<string> inline)
        {
            foreach (var expression in inline)
            {
                var kind = path.Kind;
                var group = path.GroupType is null ? null : _model.Groups[path.GroupType];
                var resource = path.ResourceType is null ? null : group!.Resources[path.ResourceType];
                var parts = expression.Split('.');
                for (var index = 0; index < parts.Length; index++)
                {
                    var name = parts[index];
                    var leaf = index == parts.Length - 1;
                    if (name == "*" && leaf)
                    {
                        break;
                    }

                    if (kind == RegistryPathKind.Registry && _model.Groups.TryGetValue(name, out group))
                    {
                        kind = RegistryPathKind.Group;
                        continue;
                    }

                    if (kind is RegistryPathKind.Group or RegistryPathKind.GroupCollection && group!.Resources.TryGetValue(name, out resource))
                    {
                        kind = RegistryPathKind.Resource;
                        continue;
                    }

                    if (kind is RegistryPathKind.Resource or RegistryPathKind.ResourceCollection && name == "versions")
                    {
                        kind = RegistryPathKind.Version;
                        continue;
                    }

                    if (leaf && (kind == RegistryPathKind.Registry && name is "model" or "modelsource" or "capabilities" ||
                        kind is RegistryPathKind.Resource or RegistryPathKind.ResourceCollection && name == "meta" ||
                        kind is RegistryPathKind.Resource or RegistryPathKind.ResourceCollection or RegistryPathKind.Version or RegistryPathKind.VersionCollection &&
                        resource!.HasDocument && name == resource.Singular))
                    {
                        continue;
                    }

                    var definitions = kind switch
                    {
                        RegistryPathKind.Registry => _model.Attributes,
                        RegistryPathKind.Group or RegistryPathKind.GroupCollection => group!.Attributes,
                        RegistryPathKind.Meta => resource!.MetaAttributes,
                        RegistryPathKind.Resource or RegistryPathKind.ResourceCollection or RegistryPathKind.Version or RegistryPathKind.VersionCollection => resource!.Attributes,
                        _ => null
                    };
                    if (leaf && definitions?.ContainsKey(name) == true)
                    {
                        throw ServerErrors.With("inline_noninlineable", path.EscapedPath, "The named attribute is not inlineable.", ("name", expression));
                    }

                    throw ServerErrors.With("bad_inline", path.EscapedPath, "An inline path is unknown or is not an inlineable attribute.", ("value", expression));
                }
            }
        }

        private static string Pointer(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        private static string Relative(string pointer) => pointer.Length == 0 ? "#/" : "#" + pointer;

        private long ResponseSize(JsonNode? node)
        {
            _ct.ThrowIfCancellationRequested();
            if (node is null)
            {
                return 4;
            }

            if (_responseSizes.TryGetValue(node, out var cached))
            {
                return cached;
            }

            var size = node switch
            {
                JsonObject obj => 2L + Math.Max(0, obj.Count - 1) + obj.Sum(property => PropertyNameSize(property.Key) + ResponseSize(property.Value)),
                JsonArray array => 2L + Math.Max(0, array.Count - 1) + array.Sum(ResponseSize),
                _ => Encoding.UTF8.GetByteCount(node.ToJsonString())
            };
            CheckResponseSize(size);
            _responseSizes[node] = size;
            return size;
        }

        private void CheckResponseSize(long size)
        {
            if (size > _engine.Limits.MaxResponseBytes)
            {
                throw ServerErrors.Create("too_large", _operation.Path.EscapedPath, "The response exceeds its encoded byte budget.");
            }
        }

        private static long PropertyNameSize(string name) => JsonEncodedText.Encode(name).EncodedUtf8Bytes.Length + 3L;
    }
}
