using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Queries;

namespace XRegistry.Federation;

public sealed partial class ProducerRegistryView
{
    private sealed class Presentation(ProducerRegistryView view, ProducerViewOptions options, RegistryQuerySelection? selection = null)
    {
        private readonly List<(JsonObject Entity, RegistryPath Path, string Pointer)> _entities = [];
        private readonly Dictionary<string, string> _entityPointers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _collectionPointers = new(StringComparer.Ordinal);

        internal async ValueTask<ProducerRegistryResult> ReadAsync(RegistryPath path, CancellationToken token)
        {
            var resource = view.Resource(path);
            if (path.GroupType is null && path.Kind != RegistryPathKind.Registry ||
                !options.DocumentView && resource?.HasDocument == true && !path.IsDetails &&
                path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version)
            {
                return await view.ReadBaseAsync(path, token).ConfigureAwait(false);
            }
            if (selection is null && IsCollection(path))
            {
                await view.EnsureCollectionOwnerAsync(path, token).ConfigureAwait(false);
            }
            var value = IsCollection(path)
                ? await CollectionAsync(path, options.Inline, "", token).ConfigureAwait(false)
                : await EntityAsync(path, options.Inline, "", token).ConfigureAwait(false);
            if (options.DocumentView) { Pointers(); }
            return view.Result(LogicalPath(path), view.Element(value));
        }

        private async ValueTask<JsonObject> CollectionAsync(RegistryPath path, IReadOnlyList<string> inline, string pointer, CancellationToken token)
        {
            view._budget.ChargeWork();
            var target = LogicalPath(path);
            IEnumerable<string> ids;
            if (selection is not null)
            {
                var selected = await SelectedChildrenAsync(path, token).ConfigureAwait(false);
                ids = selected.Select(child => child.VersionId?.Value ?? child.ResourceId?.Value ?? child.GroupId!.Value);
                if (options.DocumentView && path.Kind == RegistryPathKind.VersionCollection)
                {
                    var source = await SourceAsync(Owner(target), token).ConfigureAwait(false);
                    if (await DocumentAliasAsync(source, Owner(target), token).ConfigureAwait(false) is not null) { throw CannotDocAlias(); }
                }
            }
            else if (options.DocumentView)
            {
                var first = await view._slots[0].GetAsync(token).ConfigureAwait(false);
                SortedDictionary<string, Member> members;
                if (path.Kind == RegistryPathKind.VersionCollection)
                {
                    var source = await SourceAsync(Owner(target), token).ConfigureAwait(false);
                    if (await DocumentAliasAsync(source, Owner(target), token).ConfigureAwait(false) is not null) { throw CannotDocAlias(); }
                    members = await view.VersionsAsync(source, Owner(target), token).ConfigureAwait(false);
                }
                else if (first.Owner == FederationResolutionOwner.Producer)
                {
                    members = view.Complete(first, target,
                        await first.ReadAsync(new(FederationOperation.Collection, target), token).ConfigureAwait(false));
                }
                else { members = await view.CollectionAsync(target, token).ConfigureAwait(false); }
                ids = members.Keys;
            }
            else
            {
                var response = await view.ReadBaseAsync(path, token).ConfigureAwait(false);
                ids = response.Metadata.EnumerateObject().Select(property => property.Name).ToArray();
            }
            _collectionPointers[target] = pointer;
            var result = new JsonObject();
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                view._budget.ChargeWork();
                result[id] = await EntityAsync(PathFor(target + "/" + id, true), inline, pointer + "/" + PointerToken(id), token).ConfigureAwait(false);
            }
            return result;
        }

        private async ValueTask<JsonObject> EntityAsync(RegistryPath path, IReadOnlyList<string> inline, string pointer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            view._budget.ChargeWork();
            var target = LogicalPath(path);
            var resource = view.Resource(path);
            if (options.DocumentView && path.ResourceId is not null)
            {
                var source = await SourceAsync(Owner(target), token).ConfigureAwait(false);
                var alias = await DocumentAliasAsync(source, Owner(target), token).ConfigureAwait(false);
                if (alias is not null)
                {
                    if (path.Kind == RegistryPathKind.Version) { throw CannotDocAlias(); }
                    if (path.Kind == RegistryPathKind.Meta) { await view.ValidateAliasMetaAsync(alias, token).ConfigureAwait(false); }
                    var dangling = view.Dangling(alias, path.Kind == RegistryPathKind.Meta);
                    Register(dangling, path, pointer);
                    if (path.Kind == RegistryPathKind.Resource && dangling["meta"] is JsonObject meta)
                    {
                        Register(meta, PathFor(target + "/meta"), pointer + "/meta");
                    }
                    return dangling;
                }
            }
            var metadataPath = path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version ? PathFor(target, true) : path;
            var response = await view.ReadBaseAsync(metadataPath, token).ConfigureAwait(false);
            var value = JsonNode.Parse(response.Metadata.GetRawText())!.AsObject();
            if (options.DocumentView && path.Kind == RegistryPathKind.Resource)
            {
                var allowed = resource!.ResourceAttributes.Keys.ToHashSet(StringComparer.Ordinal);
                foreach (var name in value.Select(property => property.Key).Where(name => !allowed.Contains(name)).ToArray()) { value.Remove(name); }
            }
            if (path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version && resource!.HasDocument)
            {
                value.Remove(resource.Singular);
                value.Remove(resource.Singular + "base64");
                if ((!options.DocumentView || path.Kind == RegistryPathKind.Version) && ProducerViewOptions.Includes(inline, resource.Singular))
                {
                    await InlineDocumentAsync(value, path, resource, token).ConfigureAwait(false);
                }
            }
            if (options.DocumentView && path.Kind == RegistryPathKind.Version)
            {
                value.Remove("formatvalidated");
                value.Remove("formatvalidatedreason");
                value.Remove("compatibilityvalidated");
                value.Remove("compatibilityvalidatedreason");
            }
            if (path.Kind == RegistryPathKind.Registry)
            {
                foreach (var name in new[] { "model", "modelsource", "capabilities" })
                {
                    value.Remove(name);
                    if (ProducerViewOptions.Includes(inline, name, configuration: true))
                    {
                        value[name] = JsonNode.Parse((name == "model" ? view._model.EffectiveModel.RootElement :
                            name == "modelsource" ? view._model.Source.RootElement : view._capabilities).GetRawText());
                    }
                }
            }
            var topCollections = options.Collections && pointer.Length == 0;
            if (!topCollections) { Register(value, path, pointer); }
            if (path.Kind == RegistryPathKind.Resource)
            {
                if (ProducerViewOptions.Includes(inline, "meta"))
                {
                    value["meta"] = await EntityAsync(PathFor(target + "/meta"), [], pointer + "/meta", token).ConfigureAwait(false);
                }
                else if (value["meta"] is JsonObject meta && meta["xref"] is not null)
                {
                    Register(meta, PathFor(target + "/meta"), pointer + "/meta");
                }
                else { value.Remove("meta"); }
            }
            var output = topCollections ? new JsonObject() : value;
            foreach (var name in Collections(path))
            {
                var collection = (target == "/" ? "" : target) + "/" + name;
                value.Remove(name);
                if (selection is not null)
                {
                    var collectionPath = PathFor(collection);
                    var children = await SelectedChildrenAsync(collectionPath, token).ConfigureAwait(false);
                    value[name + "count"] = children.Count;
                    value[name + "url"] = selection.CollectionUrl(collectionPath, children.Count);
                }
                if (ProducerViewOptions.Includes(inline, name))
                {
                    output[name] = await CollectionAsync(PathFor(collection), ProducerViewOptions.Child(inline, name),
                        pointer + "/" + PointerToken(name), token).ConfigureAwait(false);
                }
            }
            return output;
        }

        private async ValueTask<IReadOnlyList<RegistryPath>> SelectedChildrenAsync(RegistryPath collection, CancellationToken token)
        {
            if (LogicalPath(selection!.Target) == LogicalPath(collection)) { return selection.RootPaths; }
            var children = await view.QueryChildrenAsync(collection, token).ConfigureAwait(false);
            return children.Where(selection.Includes).ToArray();
        }

        private async ValueTask InlineDocumentAsync(JsonObject value, RegistryPath path, RegistryResourceDefinition resource, CancellationToken token)
        {
            if (value[resource.Singular + "url"] is not null) { return; }
            var read = await view.ReadBaseAsync(PathFor(LogicalPath(path)), token).ConfigureAwait(false);
            if (read.ExternalDocument.ValueKind == JsonValueKind.Object)
            {
                if (!read.ExternalDocument.TryGetProperty("kind", out var kind) || kind.GetString() != "external" ||
                    !read.ExternalDocument.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String)
                {
                    throw Invalid("The external Document descriptor is malformed.");
                }
                value[resource.Singular + "url"] = uri.GetString();
                return;
            }
            var document = read.Document ?? throw Invalid("No Document bytes were supplied for inline output.");
            view._budget.CheckObjectBytes(document.Length);
            view._budget.CheckResultBytes(document.Length);
            var bytes = new byte[checked((int)document.Length)];
            using (var stream = document.OpenRead()) { await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false); }
            var format = options.Binary ? RegistryDocumentFormat.Binary :
                resource.ResolveDocumentFormat(document.ContentType ?? "application/octet-stream");
            if (bytes.Length != 0 && format == RegistryDocumentFormat.Json)
            {
                try
                {
                    var json = RegistryJson.Parse(bytes, view.JsonLimits());
                    value[resource.Singular] = JsonNode.Parse(json.RootElement.GetRawText());
                    return;
                }
                catch (RegistryException exception) when (exception.Diagnostic.Code is "invalid_json" or "invalid_utf8" or "duplicate_member")
                {
                    // Invalid JSON remains exact bytes rather than becoming a lossy inline value.
                }
            }
            else if (bytes.Length != 0 && format == RegistryDocumentFormat.String)
            {
                try
                {
                    value[resource.Singular] = new UTF8Encoding(false, true).GetString(bytes);
                    return;
                }
                catch (DecoderFallbackException)
                {
                    // Invalid UTF-8 remains exact bytes.
                }
            }
            value[resource.Singular + "base64"] = Convert.ToBase64String(bytes);
        }

        private async ValueTask<Slot> SourceAsync(string owner, CancellationToken token)
        {
            var first = await view._slots[0].GetAsync(token).ConfigureAwait(false);
            return first.Owner == FederationResolutionOwner.Producer ? first : await view.SelectAsync(owner, token).ConfigureAwait(false);
        }

        private async ValueTask<AliasState?> DocumentAliasAsync(Slot source, string owner, CancellationToken token)
        {
            var entity = Entity(await source.ReadAsync(new(FederationOperation.Entity, owner), token).ConfigureAwait(false));
            var meta = entity["meta"] is JsonObject included ? included :
                Entity(await source.ReadAsync(new(FederationOperation.Entity, owner + "/meta"), token).ConfigureAwait(false));
            if (meta["xref"] is null) { return null; }
            var target = view.ValidateAliasTarget(source, owner, meta);
            if (view._slots.Length > 1 && view._slots[0].Owner != FederationResolutionOwner.Producer)
            {
                // Multiple sources require the same explicit target-ownership compatibility check as API projection.
                await view.AliasAsync(source, owner, token).ConfigureAwait(false);
            }
            return new AliasState(source, owner, target, RequiredString(meta, "xref"), null, null);
        }

        private void Register(JsonObject entity, RegistryPath path, string pointer)
        {
            _entities.Add((entity, path, pointer));
            _entityPointers[LogicalPath(path)] = pointer;
        }

        private void Pointers()
        {
            foreach (var (entity, path, pointer) in _entities)
            {
                view._budget.ChargeWork();
                var target = LogicalPath(path);
                entity["self"] = Relative(pointer);
                entity.Remove("shortself");
                foreach (var name in Collections(path))
                {
                    var collection = (target == "/" ? "" : target) + "/" + name;
                    if (entity.ContainsKey(name + "url") && _collectionPointers.TryGetValue(collection, out var collectionPointer))
                    {
                        entity[name + "url"] = Relative(collectionPointer);
                    }
                }
                if (path.Kind == RegistryPathKind.Resource && entity.ContainsKey("metaurl") &&
                    _entityPointers.TryGetValue(target + "/meta", out var metaPointer))
                {
                    entity["metaurl"] = Relative(metaPointer);
                }
                if (path.Kind == RegistryPathKind.Meta && entity["defaultversionid"] is JsonValue id && id.TryGetValue<string>(out var version))
                {
                    var selected = Owner(target) + "/versions/" + version;
                    entity["defaultversionurl"] = _entityPointers.TryGetValue(selected, out var versionPointer)
                        ? Relative(versionPointer) : view.Url(selected, view.Resource(path)!.HasDocument);
                }
            }
        }

        private IEnumerable<string> Collections(RegistryPath path) => path.Kind switch
        {
            RegistryPathKind.Registry => view._model.Groups.Keys,
            RegistryPathKind.Group => view._model.Groups[path.GroupType!].Resources.Keys,
            RegistryPathKind.Resource => ["versions"],
            _ => []
        };

        private static bool IsCollection(RegistryPath path) =>
            path.Kind is RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection;
        private static string Relative(string pointer) => pointer.Length == 0 ? "#/" : "#" + pointer;
        private static string PointerToken(string value) => Uri.EscapeDataString(value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
        private static FederationException CannotDocAlias() => new(FederationErrorCode.UnsupportedOperation,
            "An alias has no local Version hierarchy in Core document view.", "cannot_doc_xref");
    }
}
