// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Numerics;
using System.Text.Json;

namespace XRegistry;

internal static partial class ModelCompiler
{
    internal static RegistryModel Compile(RegistryJson source, RegistryModelCompilationOptions options)
    {
        var root = new ModelIncludes(source, options).Expand().RootElement;
        CheckObject(root, "");
        CheckMembers(root, "", ["$schema", "description", "documentation", "labels", "attributes", "groups"]);
        var groups = new Dictionary<string, RegistryGroupDefinition>(StringComparer.Ordinal);
        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        var groupNodes = OptionalObject(root, "groups", "");
        if (groupNodes.ValueKind != JsonValueKind.Undefined)
        {
            foreach (var entry in groupNodes.EnumerateObject())
            {
                var path = Diagnostics.At("/groups", entry.Name);
                var node = entry.Value;
                CheckObject(node, path);
                CheckMembers(node, path, ["plural", "singular", "description", "documentation", "icon",
                    "labels", "modelversion", "modelcompatiblewith", "attributes", "resources", "constraints", "ximportresources"]);
                var singular = CheckNames(entry.Name, node, path, groupNames, group: true);
                var resources = CompileResources(OptionalObject(node, "resources", path), path + "/resources");
                groups[entry.Name] = new RegistryGroupDefinition(entry.Name, singular)
                {
                    Annotations = Annotations(node, path),
                    Resources = resources
                };
            }
        }

        CompleteRelationships(groups, groupNodes, options);
        var registryAttributes = SystemAttributes.Registry();
        foreach (var group in groups.Values)
        {
            if (group.Plural is "model" or "modelsource" or "capabilities" or "capabilitiesoffered" or "export" ||
                registryAttributes.ContainsKey(group.Plural))
            {
                throw Diagnostics.Error("model_error", Diagnostics.At("/groups", group.Plural), "A Group type conflicts with a core Registry name.");
            }

            SystemAttributes.AddCollection(registryAttributes, group.Plural);
        }

        var model = new RegistryModel(source, Annotations(root, ""),
            CompileAttributes(OptionalObject(root, "attributes", ""), "/attributes", registryAttributes),
            groups, options.JsonLimits);
        _ = model.EffectiveModel;
        return model;
    }

    private static FrozenDictionary<string, RegistryResourceDefinition> CompileResources(JsonElement node, string path)
    {
        var resources = new Dictionary<string, RegistryResourceDefinition>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (node.ValueKind == JsonValueKind.Undefined)
        {
            return resources.ToFrozenDictionary(StringComparer.Ordinal);
        }

        foreach (var entry in node.EnumerateObject())
        {
            var at = Diagnostics.At(path, entry.Name);
            var value = entry.Value;
            CheckObject(value, at);
            CheckMembers(value, at, ["plural", "singular", "description", "documentation", "icon", "labels",
                "modelversion", "modelcompatiblewith", "maxversions", "setversionid", "hasdocument", "versionmode",
                "singleversionroot", "validateformat", "validatecompatibility", "strictvalidation", "typemap",
                "attributes", "resourceattributes", "metaattributes"]);
            var singular = CheckNames(entry.Name, value, at, names, group: false);
            var hasDocument = Boolean(value, "hasdocument", true, at);
            var versionMode = (Text(value, "versionmode", at) ?? "manual").ToLowerInvariant();
            if (versionMode is not ("manual" or "createdat" or "modifiedat" or "semver"))
            {
                throw Diagnostics.Error("model_error", at + "/versionmode", "Unknown versionmode.");
            }

            var singleRoot = Boolean(value, "singleversionroot", versionMode != "manual", at);
            if (!singleRoot && versionMode != "manual")
            {
                throw Diagnostics.Error("model_error", at + "/singleversionroot", "This versionmode requires a single root.");
            }

            var format = Boolean(value, "validateformat", false, at);
            var compatibility = Boolean(value, "validatecompatibility", false, at);
            if (compatibility && !format)
            {
                throw Diagnostics.Error("model_error", at + "/validatecompatibility", "Compatibility validation requires format validation.");
            }

            var count = BigInteger.Zero;
            var max = Member(value, "maxversions");
            if (HasValue(max))
            {
                if (max.ValueKind != JsonValueKind.Number)
                {
                    throw Diagnostics.Error("model_error", at + "/maxversions", "maxversions must be a nonnegative integer.");
                }

                var number = RegistryNumber.FromElement(max);
                if (!number.IsInteger || number.Significand.Sign < 0)
                {
                    throw Diagnostics.Error("model_error", at + "/maxversions", "maxversions must be a nonnegative integer.");
                }

                count = number.ToBigInteger();
            }

            var resourceSystem = SystemAttributes.Resource(singular);
            var resourceOverrides = OptionalObject(value, "resourceattributes", at);
            if (resourceOverrides.ValueKind != JsonValueKind.Undefined)
            {
                foreach (var property in resourceOverrides.EnumerateObject())
                {
                    if (!resourceSystem.ContainsKey(property.Name))
                    {
                        throw Diagnostics.Error("model_error", Diagnostics.At(at + "/resourceattributes", property.Name),
                            "Resource projection attributes are reserved for specification-defined navigation.");
                    }
                }
            }

            var attributes = CompileAttributes(OptionalObject(value, "attributes", at), at + "/attributes",
                SystemAttributes.Version(singular, hasDocument), versioned: true);
            CheckResourceAttributeNames(attributes, resourceSystem, at + "/attributes");

            resources[entry.Name] = new RegistryResourceDefinition(entry.Name, singular)
            {
                Annotations = Annotations(value, at),
                MaxVersions = count,
                SetVersionId = Boolean(value, "setversionid", true, at),
                HasDocument = hasDocument,
                VersionMode = versionMode,
                SingleVersionRoot = singleRoot,
                ValidateFormat = format,
                ValidateCompatibility = compatibility,
                StrictValidation = Boolean(value, "strictvalidation", false, at),
                TypeMap = TypeMap(OptionalObject(value, "typemap", at), at + "/typemap"),
                Attributes = attributes,
                ResourceAttributes = CompileAttributes(resourceOverrides, at + "/resourceattributes", resourceSystem),
                MetaAttributes = CompileAttributes(OptionalObject(value, "metaattributes", at), at + "/metaattributes",
                    SystemAttributes.Meta(singular))
            };
        }

        return resources.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void CheckResourceAttributeNames(
        IReadOnlyDictionary<string, RegistryAttributeDefinition> attributes,
        IReadOnlyDictionary<string, RegistryAttributeDefinition> resourceAttributes, string path)
    {
        foreach (var attribute in attributes.Values)
        {
            var at = Diagnostics.At(path, attribute.Name);
            if (!attribute.IsSystemDefined && resourceAttributes.ContainsKey(attribute.Name))
            {
                throw Diagnostics.Error("model_error", at,
                    "A Version extension conflicts with a Resource projection attribute.");
            }

            foreach (var branch in attribute.IfValues)
            {
                CheckResourceAttributeNames(branch.Value, resourceAttributes,
                    Diagnostics.At(at + "/ifvalues", branch.Key) + "/siblingattributes");
            }
        }
    }

    private static string CheckNames(string plural, JsonElement node, string path, HashSet<string> names, bool group)
    {
        var declaredPlural = Text(node, "plural", path);
        if (!RegistryNames.IsAttribute(plural, maxLength: 57) || (declaredPlural is not null && declaredPlural != plural))
        {
            throw Diagnostics.Error("model_error", path + "/plural", "The plural name is invalid or does not match its map key.");
        }

        var singular = Text(node, "singular", path);
        if (singular is null || !RegistryNames.IsAttribute(singular, maxLength: group ? 63 : 57))
        {
            throw Diagnostics.Error("model_error", path + "/singular", "A valid singular name is required.");
        }

        if (!names.Add(plural) || (plural != singular && !names.Add(singular)))
        {
            throw Diagnostics.Error("model_error", path, "Plural and singular names must be unique among peer types.");
        }

        return singular;
    }

    internal static IReadOnlyDictionary<string, RegistryAttributeDefinition> CompileAttributes(
        JsonElement node, string path, IReadOnlyDictionary<string, RegistryAttributeDefinition>? baseline = null,
        bool extended = false, bool versioned = false, bool inCollection = false, bool conditional = false)
    {
        var result = baseline is null
            ? new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal)
            : new Dictionary<string, RegistryAttributeDefinition>(baseline, StringComparer.Ordinal);
        if (node.ValueKind != JsonValueKind.Undefined)
        {
            foreach (var entry in node.EnumerateObject())
            {
                var at = Diagnostics.At(path, entry.Name);
                result.TryGetValue(entry.Name, out var original);
                if (entry.Name != "*" && !RegistryNames.IsAttribute(entry.Name, extended) && original is null)
                {
                    throw Diagnostics.Error("model_error", at, "An attribute name does not satisfy its character set or length limit.");
                }

                result[entry.Name] = CompileAttribute(entry.Name, entry.Value, at, original,
                    extended, versioned, inCollection, conditional, item: false);
            }
        }

        HashSet<string>? names = null;
        foreach (var definition in result.Values)
        {
            if (definition.IfValues.Count > 0)
            {
                names ??= result.Keys.ToHashSet(StringComparer.Ordinal);
                CheckConditionalNames(definition, names, path);
            }
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static RegistryAttributeDefinition CompileAttribute(string name, JsonElement node, string path,
        RegistryAttributeDefinition? baseline, bool extended, bool versioned, bool inCollection, bool conditional, bool item)
    {
        if (node.ValueKind == JsonValueKind.String && !item)
        {
            var shortType = ModelTypes.Parse(node.GetString()!, path);
            if (shortType is RegistryValueType.Array or RegistryValueType.Map)
            {
                throw Diagnostics.Error("model_error", path, "Array and map definitions require an item definition.");
            }

            if (baseline is not null && baseline.Type != shortType)
            {
                throw Diagnostics.Error("model_error", path, "A system attribute's type cannot change.");
            }

            return baseline ?? new RegistryAttributeDefinition(name, shortType);
        }

        CheckObject(node, path);
        CheckMembers(node, path, item
            ? ["type", "target", "namecharset", "attributes", "item"]
            : ["name", "type", "target", "namecharset", "description", "enum", "strict", "matchversions",
                "readonly", "immutable", "required", "default", "attributes", "item", "ifvalues"]);
        var declaredName = Text(node, "name", path);
        if (declaredName is not null && declaredName != name)
        {
            throw Diagnostics.Error("model_error", path + "/name", "The attribute name must equal its map key.");
        }

        var typeName = Text(node, "type", path) ?? baseline?.TypeName;
        if (typeName is null)
        {
            throw Diagnostics.Error("model_error", path + "/type", "An attribute or item type is required.");
        }

        var type = ModelTypes.Parse(typeName, path + "/type");
        var definition = (baseline ?? new RegistryAttributeDefinition(name, type)) with
        {
            Type = type,
            Target = Text(node, "target", path) ?? baseline?.Target,
            NameCharset = (Text(node, "namecharset", path) ?? baseline?.NameCharset ?? "strict").ToLowerInvariant(),
            Description = Text(node, "description", path) ?? baseline?.Description,
            Strict = Boolean(node, "strict", baseline?.Strict ?? true, path),
            Required = Boolean(node, "required", baseline?.Required ?? false, path),
            ReadOnly = Boolean(node, "readonly", baseline?.ReadOnly ?? false, path),
            Immutable = Boolean(node, "immutable", baseline?.Immutable ?? false, path),
            MatchVersions = Boolean(node, "matchversions", baseline?.MatchVersions ?? false, path)
        };
        if (baseline is not null && (type != baseline.Type || baseline.Required && !definition.Required ||
            baseline.ReadOnly && !definition.ReadOnly || baseline.Immutable && !definition.Immutable))
        {
            throw Diagnostics.Error("model_error", path, "A specification-defined type or constraint cannot be weakened.");
        }

        if (definition.Immutable && baseline is null)
        {
            throw Diagnostics.Error("model_error", path + "/immutable", "The immutable aspect is reserved for system-controlled attributes.");
        }

        if (name == "*" && (definition.Required || definition.ReadOnly || HasValue(Member(node, "ifvalues"))))
        {
            throw Diagnostics.Error("model_error", path, "A wildcard cannot be required, readonly or conditional.");
        }

        if (definition.MatchVersions && (!versioned || inCollection || conditional || name == "*" || !ModelTypes.IsScalar(type)))
        {
            throw Diagnostics.Error("model_error", path + "/matchversions", "matchversions requires a static scalar Version attribute outside arrays and maps.");
        }

        if (definition.Target is not null)
        {
            if (type is not (RegistryValueType.Xid or RegistryValueType.Uri or RegistryValueType.Url))
            {
                throw Diagnostics.Error("model_error", path + "/target", "target is only valid for xid, uri or url.");
            }

            ModelPaths.ParseType(definition.Target, path + "/target", target: true);
        }

        if (definition.NameCharset is not ("strict" or "extended") ||
            HasValue(Member(node, "namecharset")) && type != RegistryValueType.Object)
        {
            throw Diagnostics.Error("model_error", path + "/namecharset", "namecharset is only defined for objects as strict or extended.");
        }

        if (HasValue(Member(node, "attributes")) && type != RegistryValueType.Object)
        {
            throw Diagnostics.Error("model_error", path + "/attributes", "Only objects have nested attributes.");
        }

        if (type == RegistryValueType.Object)
        {
            definition = definition with
            {
                Attributes = CompileAttributes(OptionalObject(node, "attributes", path), path + "/attributes",
                    baseline?.Attributes, definition.NameCharset == "extended", versioned, inCollection, conditional)
            };
        }

        if (type is RegistryValueType.Array or RegistryValueType.Map)
        {
            var itemNode = Member(node, "item");
            if (!HasValue(itemNode) && baseline?.Item is null)
            {
                throw Diagnostics.Error("model_error", path + "/item", "Array and map definitions require item.");
            }

            definition = definition with
            {
                Item = HasValue(itemNode)
                    ? CompileAttribute("", itemNode, path + "/item", baseline?.Item, false, versioned, true, conditional, item: true)
                    : baseline?.Item
            };
        }
        else if (HasValue(Member(node, "item")))
        {
            throw Diagnostics.Error("model_error", path + "/item", "Only arrays and maps have item definitions.");
        }

        var enumeration = Member(node, "enum");
        if (HasValue(enumeration))
        {
            if (!ModelTypes.IsScalar(type) || enumeration.ValueKind != JsonValueKind.Array)
            {
                throw Diagnostics.Error("model_error", path + "/enum", "enum must be an array for a scalar type.");
            }

            var values = enumeration.EnumerateArray().ToArray();
            for (var i = 0; i < values.Length; i++)
            {
                CheckScalar(definition, values[i], Diagnostics.At(path + "/enum", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            definition = definition with { EnumValues = Array.AsReadOnly(values) };
        }

        if (node.TryGetProperty("default", out var defaultValue))
        {
            if (HasValue(defaultValue))
            {
                if (!ModelTypes.IsScalar(type))
                {
                    throw Diagnostics.Error("model_scalar_default", path + "/default", "Only scalar attributes have defaults.");
                }

                if (!definition.Required)
                {
                    throw Diagnostics.Error("model_required_true", path + "/required", "An attribute with a default must be required.");
                }

                CheckScalar(definition, defaultValue, path + "/default");
            }
            else if (baseline is not null && HasValue(baseline.DefaultValue))
            {
                throw Diagnostics.Error("model_error", path + "/default", "A system-defined default cannot be removed.");
            }

            definition = definition with { DefaultValue = HasValue(defaultValue) ? defaultValue : default };
        }

        if (HasValue(definition.DefaultValue) && !Allowed(definition, definition.DefaultValue))
        {
            throw Diagnostics.Error("model_error", path + "/default", "The default must be allowed by the strict enumeration.");
        }

        if (baseline is { Strict: true, EnumValues.Count: > 0 } &&
            (!definition.Strict || definition.EnumValues.Count == 0 ||
             definition.EnumValues.Any(value => !Allowed(baseline, value))))
        {
            throw Diagnostics.Error("model_error", path + "/enum", "A specification-defined enumeration cannot be widened.");
        }

        var conditions = OptionalObject(node, "ifvalues", path);
        if (conditions.ValueKind != JsonValueKind.Undefined)
        {
            if (!ModelTypes.IsScalar(type))
            {
                throw Diagnostics.Error("model_error", path + "/ifvalues", "ifvalues requires a scalar discriminator.");
            }

            var branches = new Dictionary<string, IReadOnlyDictionary<string, RegistryAttributeDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var branch in conditions.EnumerateObject())
            {
                var branchPath = Diagnostics.At(path + "/ifvalues", branch.Name);
                if (branch.Name.Length == 0 || branch.Name[0] == '^' || branches.ContainsKey(branch.Name))
                {
                    throw Diagnostics.Error("model_error", branchPath, "A conditional key is empty, reserved or duplicated ignoring case.");
                }

                if (definition.Strict && definition.EnumValues.Count > 0 &&
                    !definition.EnumValues.Any(value => string.Equals(ScalarValues.Text(value), branch.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw Diagnostics.Error("model_error", branchPath, "The conditional key is not in the strict enumeration.");
                }

                CheckObject(branch.Value, branchPath);
                CheckMembers(branch.Value, branchPath, ["siblingattributes"]);
                var siblings = Member(branch.Value, "siblingattributes");
                CheckObject(siblings, branchPath + "/siblingattributes");
                branches[branch.Name] = CompileAttributes(siblings, branchPath + "/siblingattributes",
                    extended: extended, versioned: versioned, inCollection: inCollection, conditional: true);
            }

            definition = definition with { IfValues = branches.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) };
        }

        return definition;
    }

    private static void CheckConditionalNames(RegistryAttributeDefinition attribute, HashSet<string> names, string path)
    {
        foreach (var branch in attribute.IfValues)
        {
            HashSet<string>? visible = null;
            foreach (var sibling in branch.Value.Values)
            {
                if (names.Contains(sibling.Name))
                {
                    throw Diagnostics.Error("model_error",
                        Diagnostics.At(Diagnostics.At(Diagnostics.At(path, attribute.Name) + "/ifvalues", branch.Key) + "/siblingattributes", sibling.Name),
                        "A conditional attribute conflicts with a statically visible attribute.");
                }

                if (sibling.IfValues.Count > 0)
                {
                    if (visible is null)
                    {
                        visible = new HashSet<string>(names, StringComparer.Ordinal);
                        visible.UnionWith(branch.Value.Keys);
                    }

                    CheckConditionalNames(sibling, visible, path);
                }
            }
        }
    }

    private static FrozenDictionary<string, RegistryDocumentFormat> TypeMap(JsonElement node, string path)
    {
        var result = new Dictionary<string, RegistryDocumentFormat>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/json"] = RegistryDocumentFormat.Json,
            ["*+json"] = RegistryDocumentFormat.Json,
            ["text/plain"] = RegistryDocumentFormat.String
        };
        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node.ValueKind != JsonValueKind.Undefined)
        {
            foreach (var entry in node.EnumerateObject())
            {
                var at = Diagnostics.At(path, entry.Name);
                if (entry.Name.Length == 0 || entry.Name.Count(static character => character == '*') > 1 ||
                    !explicitNames.Add(entry.Name) || entry.Value.ValueKind != JsonValueKind.String)
                {
                    throw Diagnostics.Error("model_error", at, "Invalid or case-ambiguous typemap entry.");
                }

                result[entry.Name] = entry.Value.GetString()!.ToLowerInvariant() switch
                {
                    "binary" => RegistryDocumentFormat.Binary,
                    "json" => RegistryDocumentFormat.Json,
                    "string" => RegistryDocumentFormat.String,
                    _ => throw Diagnostics.Error("model_error", at, "Unsupported document format mapping.")
                };
            }
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static RegistryModelAnnotations Annotations(JsonElement node, string path)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var labelNode = OptionalObject(node, "labels", path);
        if (labelNode.ValueKind != JsonValueKind.Undefined)
        {
            foreach (var label in labelNode.EnumerateObject())
            {
                if (label.Name.Length == 0 || label.Value.ValueKind != JsonValueKind.String)
                {
                    throw Diagnostics.Error("model_error", Diagnostics.At(path + "/labels", label.Name),
                        "Model labels require nonempty keys and string values.");
                }

                labels[label.Name] = label.Value.GetString()!;
            }
        }

        return new RegistryModelAnnotations
        {
            Description = Text(node, "description", path),
            Documentation = ReferenceText(node, "documentation", path),
            Icon = ReferenceText(node, "icon", path, nonempty: true),
            ModelVersion = Text(node, "modelversion", path),
            ModelCompatibleWith = ReferenceText(node, "modelcompatiblewith", path),
            Labels = labels.ToFrozenDictionary(StringComparer.Ordinal)
        };
    }

    private static string? ReferenceText(JsonElement node, string name, string path, bool nonempty = false)
    {
        var value = Text(node, name, path);
        if (value is not null && (nonempty && value.Length == 0 || !UriSyntax.IsReference(value, out _)))
        {
            throw Diagnostics.Error("model_error", Diagnostics.At(path, name),
                "The annotation must be a valid URI reference" + (nonempty ? " and cannot be empty." : "."));
        }

        return value;
    }

    internal static void CheckScalar(RegistryAttributeDefinition definition, JsonElement value, string path)
    {
        if (!ScalarValues.Matches(definition.Type, value))
        {
            throw Diagnostics.Error("model_error", path, "The value does not satisfy the declared scalar type.");
        }
    }

    internal static bool Allowed(RegistryAttributeDefinition definition, JsonElement value) =>
        !definition.Strict || definition.EnumValues.Count == 0 || definition.EnumValues.Any(item => ScalarValues.Equal(item, value));

    internal static bool HasValue(JsonElement value) => value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
    internal static JsonElement Member(JsonElement node, string name) => node.TryGetProperty(name, out var value) ? value : default;

    internal static JsonElement OptionalObject(JsonElement node, string name, string path)
    {
        var value = Member(node, name);
        if (!HasValue(value))
        {
            return default;
        }

        CheckObject(value, Diagnostics.At(path, name));
        return value;
    }

    internal static void CheckObject(JsonElement node, string path)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            throw Diagnostics.Error("model_error", path, "A JSON object is required.");
        }
    }

    internal static string? Text(JsonElement node, string name, string path)
    {
        var value = Member(node, name);
        if (!HasValue(value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Diagnostics.Error("model_error", Diagnostics.At(path, name), "A string is required.");
        }

        return value.GetString();
    }

    internal static bool Boolean(JsonElement node, string name, bool fallback, string path)
    {
        var value = Member(node, name);
        return value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => fallback,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Diagnostics.Error("model_error", Diagnostics.At(path, name), "A boolean is required.")
        };
    }

    internal static void CheckMembers(JsonElement node, string path, string[] allowed)
    {
        foreach (var property in node.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Diagnostics.Error(property.Name is "$include" or "$includes" ? "model_resolution_required" : "model_error",
                    Diagnostics.At(path, property.Name), "Unknown or unresolved model language member.");
            }
        }
    }
}
