// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace XRegistry;

internal static partial class ModelCompiler
{
    private sealed record ResourceImport(string Group, string Resource, string Path);

    private static void CompleteRelationships(
        Dictionary<string, RegistryGroupDefinition> groups, JsonElement nodes,
        RegistryModelCompilationOptions options)
    {
        var imports = new Dictionary<string, Dictionary<string, ResourceImport>>(StringComparer.Ordinal);
        foreach (var group in groups.Values)
        {
            var list = new Dictionary<string, ResourceImport>(StringComparer.Ordinal);
            imports.Add(group.Plural, list);
            var node = nodes.GetProperty(group.Plural);
            var imported = Member(node, "ximportresources");
            if (!HasValue(imported))
            {
                continue;
            }

            var path = Diagnostics.At("/groups", group.Plural) + "/ximportresources";
            if (imported.ValueKind != JsonValueKind.Array)
            {
                throw Diagnostics.Error("model_error", path, "Resource imports must be an array of local type references.");
            }

            var index = 0;
            foreach (var reference in imported.EnumerateArray())
            {
                var at = Diagnostics.At(path, (index++).ToString(CultureInfo.InvariantCulture));
                if (reference.ValueKind != JsonValueKind.String)
                {
                    throw Diagnostics.Error("model_error", at, "A Resource import must be a type reference string.");
                }

                var target = ModelPaths.ParseType(reference.GetString()!, at);
                if (target.Group is null || target.Resource is null || target.Versions is not null ||
                    target.Group == group.Plural)
                {
                    throw Diagnostics.Error("model_error", at, "An import must name another Group's Resource type.");
                }

                if (group.Resources.ContainsKey(target.Resource) ||
                    !list.TryAdd(target.Resource, new ResourceImport(target.Group, target.Resource, at)))
                {
                    throw Diagnostics.Error("model_error", at, "Imported and local Resource names must be unique.");
                }
            }
        }

        var cache = new Dictionary<(string Group, string Resource), RegistryResourceDefinition>();
        foreach (var group in groups.Values)
        {
            foreach (var resource in group.Resources)
            {
                cache[(group.Plural, resource.Key)] = resource.Value;
            }
        }

        RegistryResourceDefinition Resolve(string group, string resource, HashSet<(string, string)> active, string path)
        {
            if (cache.TryGetValue((group, resource), out var resolved))
            {
                return resolved;
            }

            if (!imports.TryGetValue(group, out var groupImports) || !groupImports.TryGetValue(resource, out var imported))
            {
                throw Diagnostics.Error("model_reference_not_found", path, "The imported Resource type does not exist.");
            }

            if (!active.Add((group, resource)))
            {
                throw Diagnostics.Error("model_import_cycle", path, "Resource imports contain a circular definition.");
            }

            if (active.Count > options.MaxResourceImportDepth)
            {
                throw Diagnostics.Error("model_expansion_limit", path, "The Resource import chain exceeds its depth budget.");
            }

            resolved = Resolve(imported.Group, imported.Resource, active, imported.Path);
            active.Remove((group, resource));
            cache.Add((group, resource), resolved);
            return resolved;
        }

        foreach (var name in groups.Keys.ToArray())
        {
            var group = groups[name];
            var resources = new Dictionary<string, RegistryResourceDefinition>(group.Resources, StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in resources.Values)
            {
                names.Add(resource.Plural);
                names.Add(resource.Singular);
            }

            foreach (var imported in imports[name].Values)
            {
                var resource = Resolve(imported.Group, imported.Resource, [(name, imported.Resource)], imported.Path);
                if (!names.Add(resource.Plural) ||
                    resource.Plural != resource.Singular && !names.Add(resource.Singular))
                {
                    throw Diagnostics.Error("model_error", imported.Path,
                        "Imported and local Resource singular/plural names must be unique.");
                }

                resources.Add(resource.Plural, resource);
            }

            var node = nodes.GetProperty(name);
            var path = Diagnostics.At("/groups", name);
            var system = SystemAttributes.Group(group.Singular);
            foreach (var resource in resources.Values)
            {
                SystemAttributes.AddCollection(system, resource.Plural);
            }

            var attributes = CompileAttributes(OptionalObject(node, "attributes", path), path + "/attributes", system);
            groups[name] = group with
            {
                Resources = resources.ToFrozenDictionary(StringComparer.Ordinal),
                Attributes = attributes,
                Constraints = CompileConstraints(OptionalObject(node, "constraints", path),
                    path + "/constraints", resources, attributes)
            };
        }
    }

    private static FrozenDictionary<string, RegistryConstraint> CompileConstraints(
        JsonElement node, string path,
        Dictionary<string, RegistryResourceDefinition> resources,
        IReadOnlyDictionary<string, RegistryAttributeDefinition> groupAttributes)
    {
        var constraints = new Dictionary<string, RegistryConstraint>(StringComparer.Ordinal);
        if (!HasValue(node))
        {
            return constraints.ToFrozenDictionary(StringComparer.Ordinal);
        }

        foreach (var entry in node.EnumerateObject())
        {
            var at = Diagnostics.At(path, entry.Name);
            var parts = ModelPaths.ParseAttributePath(entry.Name, at);
            if (parts.Count < 2 || !resources.TryGetValue(parts[0], out var resource))
            {
                throw Diagnostics.Error("model_error", at, "A constraint must begin with a defined Resource plural name.");
            }

            var attributePath = Array.AsReadOnly(parts.Skip(1).ToArray());
            var definition = ModelPaths.ResolveAttribute(resource.Attributes, attributePath, at);
            CheckObject(entry.Value, at);
            CheckMembers(entry.Value, at, ["default", "enum", "equals"]);
            var defaultValue = Member(entry.Value, "default");
            if (HasValue(defaultValue))
            {
                CheckScalar(definition, defaultValue, at + "/default");
                if (!Allowed(definition, defaultValue))
                {
                    throw Diagnostics.Error("model_error", at + "/default", "The constraint default widens the attribute enumeration.");
                }
            }

            JsonElement[] values = [];
            var enumeration = Member(entry.Value, "enum");
            if (HasValue(enumeration))
            {
                if (enumeration.ValueKind != JsonValueKind.Array)
                {
                    throw Diagnostics.Error("model_error", at + "/enum", "Constraint enum must be an array.");
                }

                values = enumeration.EnumerateArray().ToArray();
                foreach (var value in values)
                {
                    CheckScalar(definition, value, at + "/enum");
                    if (!Allowed(definition, value))
                    {
                        throw Diagnostics.Error("model_error", at + "/enum", "A constraint cannot widen allowed attribute values.");
                    }
                }
            }

            var effectiveDefault = HasValue(defaultValue) ? defaultValue : definition.DefaultValue;
            if (values.Length > 0 && HasValue(effectiveDefault) &&
                !values.Any(value => ScalarValues.Equal(value, effectiveDefault)))
            {
                throw Diagnostics.Error("model_error", at + "/enum", "The effective default must belong to the constraint enumeration.");
            }

            var equals = Text(entry.Value, "equals", at);
            IReadOnlyList<string> equalsPath = [];
            if (!string.IsNullOrEmpty(equals))
            {
                equalsPath = ModelPaths.ParseAttributePath(equals, at + "/equals");
                var groupAttribute = ModelPaths.ResolveAttribute(groupAttributes, equalsPath, at + "/equals");
                if (groupAttribute.Type != definition.Type)
                {
                    throw Diagnostics.Error("model_error", at + "/equals", "The Group and Resource scalar types must match.");
                }
            }

            constraints.Add(entry.Name, new RegistryConstraint(entry.Name, resource.Plural, attributePath)
            {
                DefaultValue = HasValue(defaultValue) ? defaultValue : default,
                EnumValues = Array.AsReadOnly(values),
                EqualsAttribute = string.IsNullOrEmpty(equals) ? null : equals,
                EqualsPath = equalsPath
            });
        }

        return constraints.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
