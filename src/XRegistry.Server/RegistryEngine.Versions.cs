// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Numerics;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private void FinalizeResource(Entity resource)
        {
            if (resource.Attributes["xref"] is not null)
            {
                return;
            }

            var path = RegistryPath.Parse(resource.Key);
            var definition = ResourceDefinition(path);
            var versions = Children(resource.Key + "/versions").ToList();
            if (versions.Count == 0)
            {
                DeleteEntity(resource, new JsonObject());
                return;
            }

            foreach (var version in versions)
            {
                Complete(version);
            }

            RebuildAncestry(versions, definition);
            var requestedDefault = _flags.SetDefault;
            if (requestedDefault == "request")
            {
                if (_newVersions.Count != 1)
                {
                    throw ServerErrors.Create(_newVersions.Count == 0 ? "defaultversionid_request" : "too_many_versions",
                        resource.Key, "The request must create exactly one Version for this default selection.");
                }

                requestedDefault = Id(Require(_newVersions[0]));
            }

            if (requestedDefault is not null)
            {
                resource.Attributes["defaultversionsticky"] = requestedDefault != "null";
                if (requestedDefault != "null")
                {
                    resource.Attributes["defaultversionid"] = requestedDefault;
                }
            }

            var selected = ServerJson.Text(resource.Attributes, "defaultversionid");
            var sticky = ServerJson.Boolean(resource.Attributes, "defaultversionsticky");
            if (sticky && definition.MaxVersions == BigInteger.One)
            {
                throw ServerErrors.Create("setdefaultversionsticky_false", resource.Key, "A one-Version Resource cannot have a sticky default.");
            }

            if (selected is not null && versions.All(version => Id(version) != selected))
            {
                var deleted = WasDeleted(resource.Key + "/versions/" + Uri.EscapeDataString(selected));
                if (!deleted || requestedDefault is not null and not "null")
                {
                    throw ServerErrors.With("unknown_id", resource.Key, "The requested default Version does not exist.",
                        ("singular", "version"), ("id", selected));
                }

                sticky = false;
                resource.Attributes["defaultversionsticky"] = false;
            }

            if (!sticky || selected is null)
            {
                selected = Id(Newest(versions, definition));
            }

            if (ServerJson.Text(resource.Attributes, "defaultversionid") != selected ||
                !JsonNode.DeepEquals(resource.Original["defaultversionsticky"], resource.Attributes["defaultversionsticky"]))
            {
                Touch(resource);
            }

            resource.Attributes["defaultversionid"] = selected;
            resource.Attributes["defaultversionsticky"] = sticky;
            Complete(resource);
            CheckMatchVersions(versions, definition.Attributes, includeDefaultMembership: false);
            _validationResources.Add(new(resource, ServerJson.Own(resource.Attributes, _engine.Limits.Json),
                versions.Select(version => new ValidationVersion(version, ServerJson.Own(version.Attributes, _engine.Limits.Json))).ToList()));
            while (definition.MaxVersions > 0 && versions.Count > definition.MaxVersions)
            {
                var ordered = OldestFirst(versions, definition);
                var oldest = definition.MaxVersions == BigInteger.One
                    ? ordered[0] : ordered.First(version => Id(version) != selected);
                DeleteEntity(oldest, new JsonObject());
                versions.Remove(oldest);
                RebuildAncestry(versions, definition);
                if (Id(oldest) == selected)
                {
                    selected = Id(Newest(versions, definition));
                    resource.Attributes["defaultversionid"] = selected;
                }
            }

            if (!sticky)
            {
                var newest = Id(Newest(versions, definition));
                if (ServerJson.Text(resource.Attributes, "defaultversionid") != newest)
                {
                    Touch(resource);
                    resource.Attributes["defaultversionid"] = newest;
                }
            }
        }

        private void ReconcileValidatedOrdering()
        {
            foreach (var key in _resources)
            {
                var resource = Find(key);
                if (resource is null || resource.Attributes["xref"] is not null)
                {
                    continue;
                }

                var definition = ResourceDefinition(RegistryPath.Parse(key));
                var versions = Children(key + "/versions");
                RebuildAncestry(versions, definition);
                if (!ServerJson.Boolean(resource.Attributes, "defaultversionsticky"))
                {
                    var newest = Id(Newest(versions, definition));
                    if (ServerJson.Text(resource.Attributes, "defaultversionid") != newest)
                    {
                        Touch(resource);
                        resource.Attributes["defaultversionid"] = newest;
                    }
                }

                Complete(resource);
                CheckMatchVersions(versions, definition.Attributes);
            }
        }

        private void RebuildAncestry(IReadOnlyList<Entity> versions, RegistryResourceDefinition definition)
        {
            if (definition.VersionMode != "manual")
            {
                var repeat = true;
                // Repairing ancestry can itself update modifiedat and change the next ordering.
                while (repeat)
                {
                    repeat = false;
                    var ordered = OldestFirst(versions, definition);
                    for (var index = 0; index < ordered.Count; index++)
                    {
                        Work();
                        var ancestor = Id(ordered[Math.Max(0, index - 1)]);
                        if (ServerJson.Text(ordered[index].Attributes, "ancestorid") != ancestor)
                        {
                            SetAncestor(ordered[index], ancestor);
                            repeat |= definition.VersionMode == "modifiedat";
                        }
                    }
                }
            }
            else
            {
                var ids = versions.ToDictionary(Id, StringComparer.Ordinal);
                foreach (var version in versions)
                {
                    Work();
                    var ancestor = ServerJson.Text(version.Attributes, "ancestorid");
                    if (ancestor is null)
                    {
                        SetAncestor(version, Id(version));
                    }
                    else if (!ids.ContainsKey(ancestor))
                    {
                        if (WasDeleted(Parent(version.Key) + "/" + Uri.EscapeDataString(ancestor)))
                        {
                            SetAncestor(version, Id(version));
                        }
                        else
                        {
                            throw ServerErrors.With("unknown_id", ResourceKey(RegistryPath.Parse(version.Key)), "The ancestor Version does not exist.",
                                ("singular", "version"), ("id", ancestor));
                        }
                    }
                }

                var visited = new Dictionary<string, bool>(StringComparer.Ordinal);
                var trail = new List<string>();
                foreach (var version in versions)
                {
                    trail.Clear();
                    var current = version;
                    while (true)
                    {
                        Work();
                        var currentId = Id(current);
                        if (visited.TryGetValue(currentId, out var complete))
                        {
                            if (!complete)
                            {
                                throw ServerErrors.With("ancestor_circular_reference", ResourceKey(RegistryPath.Parse(version.Key)),
                                    "The Version ancestry contains a cycle.", ("list", string.Join(", ", trail.Append(currentId))));
                            }

                            break;
                        }

                        visited.Add(currentId, false);
                        trail.Add(currentId);
                        var parent = ServerJson.Text(current.Attributes, "ancestorid")!;
                        if (parent == currentId)
                        {
                            break;
                        }

                        current = ids[parent];
                    }

                    foreach (var id in trail)
                    {
                        visited[id] = true;
                    }
                }
            }

            if (definition.SingleVersionRoot && versions.Count(version => Id(version) == ServerJson.Text(version.Attributes, "ancestorid")) > 1)
            {
                throw ServerErrors.With("multiple_roots", ResourceKey(RegistryPath.Parse(versions[0].Key)),
                    "This Resource type permits only one ancestry root.", ("plural", definition.Plural));
            }
        }

        private void SetAncestor(Entity entity, string ancestor)
        {
            if (ServerJson.Text(entity.Attributes, "ancestorid") != ancestor)
            {
                Touch(entity);
                entity.Attributes["ancestorid"] = ancestor;
            }
        }

        private static string Id(Entity version) => ServerJson.Text(version.Attributes, "versionid") ??
            Uri.UnescapeDataString(version.Key[(version.Key.LastIndexOf('/') + 1)..]);

        private static Entity Newest(IReadOnlyList<Entity> versions, RegistryResourceDefinition definition)
        {
            if (definition.VersionMode != "manual")
            {
                return OldestFirst(versions, definition)[^1];
            }

            var ancestors = versions.Where(version => Id(version) != ServerJson.Text(version.Attributes, "ancestorid"))
                .Select(version => ServerJson.Text(version.Attributes, "ancestorid")).ToHashSet(StringComparer.Ordinal);
            var leaves = versions.Where(version => !ancestors.Contains(Id(version))).ToList();
            if (leaves.Count == 0)
            {
                throw ServerErrors.With("ancestor_circular_reference", ResourceKey(RegistryPath.Parse(versions[0].Key)),
                    "The Version ancestry has no leaf.", ("list", string.Join(", ", versions.Select(Id))));
            }

            leaves.Sort((left, right) => CompareTime(left, right, "createdat"));
            return leaves[^1];
        }

        private static List<Entity> OldestFirst(IReadOnlyList<Entity> versions, RegistryResourceDefinition definition)
        {
            var ordered = versions.ToList();
            if (definition.VersionMode == "manual")
            {
                var roots = ordered.Where(version => Id(version) == ServerJson.Text(version.Attributes, "ancestorid")).ToList();
                if (roots.Count == 0)
                {
                    throw ServerErrors.With("ancestor_circular_reference", ResourceKey(RegistryPath.Parse(versions[0].Key)),
                        "The Version ancestry has no root.", ("list", string.Join(", ", versions.Select(Id))));
                }

                roots.Sort((left, right) => CompareTime(left, right, "createdat"));
                var nonRoots = ordered.Except(roots).OrderBy(Id, StringComparer.OrdinalIgnoreCase);
                return [.. roots, .. nonRoots];
            }

            if (definition.VersionMode == "semver")
            {
                var parsed = ordered.ToDictionary(Id, version => SemanticVersion.Parse(Id(version), version.Key), StringComparer.Ordinal);
                ordered.Sort((left, right) =>
                {
                    var comparison = parsed[Id(left)].CompareTo(parsed[Id(right)]);
                    return comparison == 0 ? StringComparer.OrdinalIgnoreCase.Compare(Id(left), Id(right)) : comparison;
                });
            }
            else
            {
                ordered.Sort((left, right) => CompareTime(left, right, definition.VersionMode));
            }

            return ordered;
        }

        private static int CompareTime(Entity left, Entity right, string field)
        {
            var a = ServerJson.Text(left.Attributes, field)!;
            var b = ServerJson.Text(right.Attributes, field)!;
            var comparison = ServerJson.CompareTimestamps(a, b);

            return comparison == 0 ? StringComparer.OrdinalIgnoreCase.Compare(Id(left), Id(right)) : comparison;
        }

        private void CheckMatchVersions(IReadOnlyList<Entity> versions, IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions,
            bool includeDefaultMembership = true)
        {
            if (versions.Count < 2)
            {
                return;
            }

            var defaultId = ServerJson.Text(Require(ResourceKey(RegistryPath.Parse(versions[0].Key))).Attributes, "defaultversionid");
            CheckObject(versions.Select(static version => version.Attributes).ToList(), definitions, "");
            void CheckObject(List<JsonObject> values, IReadOnlyDictionary<string, RegistryAttributeDefinition> shape, string pointer)
            {
                foreach (var definition in shape.Values)
                {
                    Work();
                    var first = values[0][definition.Name];
                    var defaultMembership = pointer.Length == 0 && definition.Name == "isdefault";
                    if (definition.MatchVersions && (!defaultMembership || includeDefaultMembership))
                    {
                        for (var index = 1; index < values.Count; index++)
                        {
                            Work();
                            // Default membership is derived after final ordering/retention, not stored in the Version record.
                            var equal = defaultMembership
                                ? (Id(versions[0]) == defaultId) == (Id(versions[index]) == defaultId)
                                : ServerJson.Equal(first, values[index][definition.Name]);
                            if (!equal)
                            {
                                if (_modelChanged)
                                {
                                    throw ServerErrors.Create("model_compliance_error", "/model",
                                        "Existing Versions do not satisfy the proposed model's cross-Version equality rule.");
                                }

                                throw ServerErrors.With("mismatched_version_attribute", ResourceKey(RegistryPath.Parse(versions[0].Key)),
                                    "A matchversions attribute differs between Versions.",
                                    ("name", (pointer + "/" + definition.Name).TrimStart('/').Replace('/', '.')));
                            }
                        }
                    }

                    if (definition.Type == RegistryValueType.Object)
                    {
                        var nested = values.Select(value => value[definition.Name] as JsonObject ?? new JsonObject()).ToList();
                        CheckObject(nested, definition.Attributes, pointer + "/" + definition.Name);
                    }

                    foreach (var conditional in definition.IfValues.Values)
                    {
                        CheckObject(values, conditional, pointer);
                    }
                }
            }
        }
    }
}
