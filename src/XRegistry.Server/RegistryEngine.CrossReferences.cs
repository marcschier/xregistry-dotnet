using System.Numerics;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private bool ApplyCrossReference(RegistryPath path, JsonObject body, bool metaOnly, bool patch)
        {
            var key = ResourceKey(path);
            var meta = metaOnly ? body : body["meta"] as JsonObject;
            var current = Find(key);
            if (meta?["xref"] is { } supplied)
            {
                if (supplied is not JsonValue scalar || !scalar.TryGetValue<string>(out var value))
                {
                    throw ServerErrors.With("malformed_xref", _engine.Url(ServerJson.Key(_operation.Path)),
                        "xref must be a Resource XID string.", ("xref", ErrorValue(supplied)));
                }

                var definition = ResourceDefinition(path);
                var target = CrossReferencePath(path, value);
                var idName = definition.Singular + "id";
                var extra = !metaOnly ? body.FirstOrDefault(property => property.Key != idName && property.Key != "meta").Key : null;
                extra ??= meta.FirstOrDefault(property => property.Key != idName && property.Key != "xref" &&
                    !(property.Key == "epoch" && current is not null && current.Attributes["xref"] is null)).Key;
                if (extra is not null)
                {
                    throw ServerErrors.With("extra_xref_attribute", key,
                        "A cross-reference request permits only identifiers, xref, and an existing normal Resource's meta epoch.",
                        ("name", extra), ("singular", definition.Singular));
                }

                CheckId(body, idName, path.ResourceId!.Value, key);
                CheckId(meta, idName, path.ResourceId.Value, key);
                EnsureGroup(path);
                var resource = current;
                if (resource is null)
                {
                    resource = New(key);
                    Touch(Require(ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!))));
                }
                else if (ServerJson.Boolean(resource.Attributes, "readonly"))
                {
                    throw ServerErrors.Create("readonly", key, "This Resource is read-only.");
                }
                else if (resource.Attributes["xref"] is null)
                {
                    Guard(resource, meta, idName, path.ResourceId.Value);
                }

                foreach (var version in Children(key + "/versions"))
                {
                    DeleteEntity(version, new JsonObject());
                }

                Touch(resource);
                resource.Attributes = new JsonObject
                {
                    [idName] = path.ResourceId.Value,
                    ["xref"] = ServerJson.Key(target),
                    ["epoch"] = resource.Attributes["epoch"]?.DeepClone() ?? JsonValue.Create(0),
                    ["createdat"] = resource.Attributes["createdat"]?.DeepClone() ?? JsonValue.Create(_now),
                    ["modifiedat"] = _now
                };
                _resources.Add(key);
                return true;
            }

            if (current?.Attributes["xref"] is not null)
            {
                if (!(meta?.ContainsKey("xref") == true && meta["xref"] is null || meta is not null && !patch))
                {
                    throw ServerErrors.With("extra_xref_attribute", key, "The cross-reference must be explicitly removed before writing local metadata.",
                        ("name", body.FirstOrDefault().Key ?? "xref"), ("singular", ResourceDefinition(path).Singular));
                }

                var targetKey = ServerJson.Text(current.Attributes, "xref")!;
                var target = Find(targetKey);
                var ownEpoch = ServerJson.Unsigned(current.Attributes["epoch"]!, key);
                var targetEpoch = target?.Attributes["xref"] is null && target?.Attributes["epoch"] is { } epoch
                    ? ServerJson.Unsigned(epoch, targetKey) : BigInteger.Zero;
                current.Attributes = new JsonObject
                {
                    [ResourceDefinition(path).Singular + "id"] = path.ResourceId!.Value,
                    ["epoch"] = ServerJson.Integer(BigInteger.Max(ownEpoch, targetEpoch) + 1),
                    ["createdat"] = current.Attributes["createdat"]?.DeepClone() ?? JsonValue.Create(_now),
                    ["modifiedat"] = _now,
                    ["readonly"] = false,
                    ["defaultversionsticky"] = false
                };
                current.Dirty = true;
                _resources.Add(key);
            }

            return false;
        }

        private RegistryPath CrossReferencePath(RegistryPath source, string value)
        {
            RegistryPath target;
            try
            {
                target = RegistryPath.Parse(value);
            }
            catch (RegistryException exception)
            {
                throw ServerErrors.Wrap(new RegistryException(new("malformed_xref", ResourceKey(source),
                    "xref must be a syntactically valid Resource XID."), exception), "malformed_xref",
                    _engine.Url(ServerJson.Key(_operation.Path)), ("xref", value));
            }

            if (target.Kind != RegistryPathKind.Resource || target.IsDetails ||
                !_model.Groups.TryGetValue(target.GroupType!, out var targetGroup) ||
                !targetGroup.Resources.TryGetValue(target.ResourceType!, out var targetDefinition) ||
                !ReferenceEquals(ResourceDefinition(source), targetDefinition))
            {
                throw ServerErrors.With("malformed_xref", _engine.Url(ServerJson.Key(_operation.Path)),
                    "xref must reference the same actual compiled Resource type, not a structurally similar type.", ("xref", value));
            }

            return target;
        }

        private async ValueTask<Entity?> ResolveCrossReferenceAsync(Entity source)
        {
            if (ServerJson.Text(source.Attributes, "xref") is not { } xid || DocumentView)
            {
                return null;
            }

            var path = CrossReferencePath(RegistryPath.Parse(source.Key), xid);
            var target = Find(ServerJson.Key(path));
            if (target is null || target.Attributes["xref"] is not null ||
                !await CanReadAsync(path).ConfigureAwait(false) ||
                !await CanReadAsync(RegistryPath.Parse(target.Key + "/meta")).ConfigureAwait(false))
            {
                return null;
            }

            return target;
        }

        private async ValueTask<Entity?> VersionForReadAsync(RegistryPath path)
        {
            var source = Require(ResourceKey(path));
            if (source.Attributes["xref"] is not null && DocumentView && path.Kind == RegistryPathKind.Version)
            {
                throw ServerErrors.Create("cannot_doc_xref", ResourceKey(path), "Cross-reference Versions do not exist in document view.");
            }

            var resource = source.Attributes["xref"] is null ? source : await ResolveCrossReferenceAsync(source).ConfigureAwait(false);
            if (resource is null)
            {
                return null;
            }

            var id = path.Kind == RegistryPathKind.Version ? path.VersionId!.Value : ServerJson.Text(resource.Attributes, "defaultversionid")!;
            var versionKey = resource.Key + "/versions/" + Uri.EscapeDataString(id);
            var version = Find(versionKey);
            if (version is null || !await CanReadAsync(RegistryPath.Parse(versionKey)).ConfigureAwait(false))
            {
                return null;
            }

            return version;
        }

        private JsonObject DanglingResource(RegistryPath path, Entity source, string pointer)
        {
            var definition = ResourceDefinition(path);
            var key = ResourceKey(path);
            var meta = new JsonObject
            {
                [definition.Singular + "id"] = path.ResourceId!.Value,
                ["self"] = DocumentView ? Relative(path.Kind == RegistryPathKind.Meta ? pointer : pointer + "/meta") : _engine.Url(key + "/meta"),
                ["xid"] = key + "/meta",
                ["xref"] = source.Attributes["xref"]!.DeepClone()
            };
            if (_capabilities.ShortSelf && !DocumentView)
            {
                meta["shortself"] = ShortSelf(RegistryPath.Parse(key + "/meta"));
            }

            return path.Kind == RegistryPathKind.Meta ? meta : new JsonObject
            {
                [definition.Singular + "id"] = path.ResourceId!.Value,
                ["self"] = DocumentView ? Relative(pointer) : _engine.Url(key, definition.HasDocument),
                ["xid"] = key,
                ["metaurl"] = DocumentView ? Relative(pointer + "/meta") : _engine.Url(key + "/meta"),
                ["meta"] = meta
            };
        }
    }
}
