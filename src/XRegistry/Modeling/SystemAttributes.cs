// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Text.Json;

namespace XRegistry;

internal static class SystemAttributes
{
    private static readonly JsonElement s_false = RegistryJson.Parse("false").RootElement;
    private static readonly JsonElement s_specVersion = RegistryJson.Parse("\"1.0-rc4\"").RootElement;

    internal static bool RequiresNonEmptyString(RegistryAttributeDefinition definition) =>
        definition.IsSystemDefined && definition.Name is "name" or "format" or "documentation" or "icon";

    internal static Dictionary<string, RegistryAttributeDefinition> Registry()
    {
        var result = Common("registryid");
        result["registryid"] = result["registryid"] with { ReadOnly = true };
        result["specversion"] = Attribute("specversion", RegistryValueType.String, required: true, readOnly: true)
            with
        { DefaultValue = s_specVersion };
        result["capabilities"] = OpenObject("capabilities");
        result["model"] = OpenObject("model") with { ReadOnly = true };
        result["modelsource"] = OpenObject("modelsource");
        return result;
    }

    internal static Dictionary<string, RegistryAttributeDefinition> Group(string singular)
    {
        var result = Common(singular + "id");
        result["deprecated"] = Deprecated();
        result["constraints"] = Attribute("constraints", RegistryValueType.Map) with
        {
            IsConstraints = true,
            Item = Attribute("", RegistryValueType.Object) with
            {
                Attributes = new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["default"] = Attribute("default", RegistryValueType.Any),
                    ["enum"] = Attribute("enum", RegistryValueType.Array) with { Item = Attribute("", RegistryValueType.Any) },
                    ["equals"] = Attribute("equals", RegistryValueType.String)
                }.ToFrozenDictionary(StringComparer.Ordinal)
            }
        };
        return result;
    }

    internal static Dictionary<string, RegistryAttributeDefinition> Version(string singular, bool hasDocument)
    {
        var result = Common("versionid");
        Add(result, Attribute(singular + "id", RegistryValueType.String, required: true)
            with
        { Immutable = true, IsIdentifier = true });
        result["isdefault"] = Attribute("isdefault", RegistryValueType.Boolean, required: true, readOnly: true)
            with
        { DefaultValue = s_false };
        result["ancestorid"] = Attribute("ancestorid", RegistryValueType.String, required: true) with { IsIdentifier = true };
        result["contenttype"] = Attribute("contenttype", RegistryValueType.String);
        result["format"] = Attribute("format", RegistryValueType.String);
        result["formatvalidated"] = Attribute("formatvalidated", RegistryValueType.Boolean, readOnly: true);
        result["compatibilityvalidated"] = Attribute("compatibilityvalidated", RegistryValueType.Boolean, readOnly: true);
        result["formatvalidatedreason"] = Attribute("formatvalidatedreason", RegistryValueType.String, readOnly: true);
        result["compatibilityvalidatedreason"] = Attribute("compatibilityvalidatedreason", RegistryValueType.String, readOnly: true);
        if (hasDocument)
        {
            Add(result, Attribute(singular, RegistryValueType.Any) with { IsDocument = true });
            Add(result, Attribute(singular + "url", RegistryValueType.Url) with { IsDocument = true });
            Add(result, Attribute(singular + "base64", RegistryValueType.String) with { IsDocument = true });
        }

        return result;
    }

    internal static Dictionary<string, RegistryAttributeDefinition> Resource(string singular)
    {
        var common = Common(singular + "id");
        var result = new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal);
        foreach (var name in new[] { singular + "id", "self", "shortself", "xid" })
        {
            result[name] = common[name];
        }

        result["metaurl"] = Attribute("metaurl", RegistryValueType.Url, required: true, readOnly: true) with { Immutable = true };
        result["meta"] = OpenObject("meta");
        AddCollection(result, "versions");
        return result;
    }

    internal static Dictionary<string, RegistryAttributeDefinition> Meta(string singular)
    {
        var result = Common(singular + "id");
        foreach (var name in new[] { "name", "description", "documentation", "icon" })
        {
            result.Remove(name);
        }

        result["xref"] = Attribute("xref", RegistryValueType.Url);
        result["readonly"] = Attribute("readonly", RegistryValueType.Boolean, required: true, readOnly: true)
            with
        { DefaultValue = s_false };
        result["compatibility"] = Attribute("compatibility", RegistryValueType.String) with
        {
            EnumValues = Array.AsReadOnly(RegistryJson.Parse(
                """["backward","backward_transitive","forward","forward_transitive","full","full_transitive"]""")
                .RootElement.EnumerateArray().ToArray())
        };
        result["deprecated"] = Deprecated();
        result["defaultversionid"] = Attribute("defaultversionid", RegistryValueType.String, required: true) with { IsIdentifier = true };
        result["defaultversionurl"] = Attribute("defaultversionurl", RegistryValueType.Url, required: true, readOnly: true);
        result["defaultversionsticky"] = Attribute("defaultversionsticky", RegistryValueType.Boolean, required: true)
            with
        { DefaultValue = s_false };
        return result;
    }

    internal static void AddCollection(Dictionary<string, RegistryAttributeDefinition> attributes, string plural)
    {
        Add(attributes, Attribute(plural + "url", RegistryValueType.Url, required: true, readOnly: true) with { Immutable = true });
        Add(attributes, Attribute(plural + "count", RegistryValueType.UInteger, required: true, readOnly: true));
        Add(attributes, Attribute(plural, RegistryValueType.Map) with { IsCollection = true, Item = OpenObject("") });
    }

    private static Dictionary<string, RegistryAttributeDefinition> Common(string id)
    {
        var result = new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal);
        Add(result, Attribute(id, RegistryValueType.String, required: true) with { Immutable = true, IsIdentifier = true });
        Add(result, Attribute("self", RegistryValueType.Url, required: true, readOnly: true) with { Immutable = true });
        Add(result, Attribute("shortself", RegistryValueType.Url, readOnly: true) with { Immutable = true });
        Add(result, Attribute("xid", RegistryValueType.Xid, required: true, readOnly: true) with { Immutable = true });
        Add(result, Attribute("epoch", RegistryValueType.UInteger, required: true, readOnly: true));
        foreach (var name in new[] { "name", "description" })
        {
            Add(result, Attribute(name, RegistryValueType.String));
        }

        foreach (var name in new[] { "documentation", "icon" })
        {
            Add(result, Attribute(name, RegistryValueType.Url));
        }

        Add(result, Attribute("labels", RegistryValueType.Map) with { Item = Attribute("", RegistryValueType.String) });
        Add(result, Attribute("createdat", RegistryValueType.Timestamp, required: true));
        Add(result, Attribute("modifiedat", RegistryValueType.Timestamp, required: true));
        return result;
    }

    private static RegistryAttributeDefinition Deprecated() => Attribute("deprecated", RegistryValueType.Object) with
    {
        Attributes = new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal)
        {
            ["alternative"] = Attribute("alternative", RegistryValueType.Url),
            ["documentation"] = Attribute("documentation", RegistryValueType.Url),
            ["effective"] = Attribute("effective", RegistryValueType.Timestamp),
            ["removal"] = Attribute("removal", RegistryValueType.Timestamp),
            ["*"] = Attribute("*", RegistryValueType.Any)
        }.ToFrozenDictionary(StringComparer.Ordinal)
    };

    private static RegistryAttributeDefinition OpenObject(string name) => Attribute(name, RegistryValueType.Object) with
    {
        Attributes = new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal)
        {
            ["*"] = Attribute("*", RegistryValueType.Any)
        }.ToFrozenDictionary(StringComparer.Ordinal)
    };

    private static RegistryAttributeDefinition Attribute(string name, RegistryValueType type,
        bool required = false, bool readOnly = false) => new(name, type)
        {
            Required = required,
            ReadOnly = readOnly,
            IsSystemDefined = true
        };

    private static void Add(Dictionary<string, RegistryAttributeDefinition> attributes, RegistryAttributeDefinition attribute)
    {
        if (!attributes.TryAdd(attribute.Name, attribute))
        {
            throw Diagnostics.Error("model_error", "", "A type name conflicts with a specification-defined attribute.");
        }
    }
}
