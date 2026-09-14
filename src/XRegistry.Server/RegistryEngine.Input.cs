using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private JsonObject InputMetadata()
        {
            var body = _operation.Metadata is null ? new JsonObject() : ServerJson.Object(_operation.Metadata);
            Strip(body, _operation.Path, true);
            return body;

            void Strip(JsonObject input, RegistryPath path, bool top)
            {
                Work();
                if (path.Kind is RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection)
                {
                    foreach (var property in input.ToArray())
                    {
                        _ = ParseId(property.Key);
                        var childPath = RegistryPath.Parse(ServerJson.Key(path) + "/" + Uri.EscapeDataString(property.Key));
                        if (path.Kind == RegistryPathKind.ResourceCollection && _flags.Ignore.Contains("readonly"))
                        {
                            _ = Map(property.Value, childPath.EscapedPath);
                            if (IgnoreReadonly(childPath))
                            {
                                input.Remove(property.Key);
                                continue;
                            }
                        }

                        if (property.Value is JsonObject child)
                        {
                            Strip(child, childPath, false);
                        }
                    }

                    return;
                }

                input.Remove("$schema");
                if (_flags.Ignore.Contains("epoch"))
                {
                    input.Remove("epoch");
                }

                if (top && _flags.Ignore.Contains("id"))
                {
                    switch (path.Kind)
                    {
                        case RegistryPathKind.Registry: input.Remove("registryid"); break;
                        case RegistryPathKind.Group: input.Remove(_model.Groups[path.GroupType!].Singular + "id"); break;
                        case RegistryPathKind.Resource:
                        case RegistryPathKind.Meta:
                        case RegistryPathKind.Version:
                            input.Remove(ResourceDefinition(path).Singular + "id");
                            if (path.Kind == RegistryPathKind.Version)
                            {
                                input.Remove("versionid");
                            }

                            break;
                    }
                }

                if (path.Kind == RegistryPathKind.Registry)
                {
                    foreach (var name in _flags.Ignore.Where(static name => name is "modelsource" or "capabilities"))
                    {
                        input.Remove(name);
                    }

                    foreach (var group in _model.Groups.Keys)
                    {
                        if (input[group] is JsonObject collection)
                        {
                            Strip(collection, RegistryPath.Parse("/" + group), false);
                        }
                    }
                }
                else if (path.Kind == RegistryPathKind.Group)
                {
                    foreach (var resource in _model.Groups[path.GroupType!].Resources.Keys)
                    {
                        if (input[resource] is JsonObject collection)
                        {
                            Strip(collection, RegistryPath.Parse(ServerJson.Key(path) + "/" + resource), false);
                        }
                    }
                }
                else if (path.Kind == RegistryPathKind.Resource)
                {
                    if (input["meta"] is JsonObject meta)
                    {
                        Strip(meta, RegistryPath.Parse(ResourceKey(path) + "/meta"), false);
                    }

                    if (input["versions"] is JsonObject versions)
                    {
                        Strip(versions, RegistryPath.Parse(ResourceKey(path) + "/versions"), false);
                    }
                }
                else if (path.Kind == RegistryPathKind.Meta)
                {
                    foreach (var name in _flags.Ignore.Where(static name => name is "defaultversionid" or "defaultversionsticky"))
                    {
                        input.Remove(name);
                    }
                }
            }
        }
    }
}
