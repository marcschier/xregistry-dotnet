// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Models;

/// <summary>Explicit, separately provisioned WoT endpoint profiles.</summary>
public enum WotVersionProfile
{
    /// <summary>The draft's native SemVer profile.</summary>
    SemVer,
    /// <summary>The selected D1-D9 incremental profile.</summary>
    Incremental
}

/// <summary>Builds explicit WoT profile models without modifying the packaged specification source.</summary>
public static class WotRegistryProfiles
{
    /// <summary>Compiles a separate profile preset. Existing built-in model entry points remain unchanged.</summary>
    public static RegistryModel Compile(WotVersionProfile profile)
    {
        if (!Enum.IsDefined(profile)) { throw new ArgumentOutOfRangeException(nameof(profile)); }
        using var packaged = BuiltInRegistryModels.LoadSource(RegistryModelKind.Wot);
        var source = JsonNode.Parse(packaged.RootElement.GetRawText())!.AsObject();
        var name = profile == WotVersionProfile.Incremental ? "incremental" : "semver";
        source["attributes"] ??= new JsonObject();
        source["attributes"]!["wotversionprofile"] = new JsonObject
        {
            ["type"] = "string", ["required"] = true, ["readonly"] = true,
            ["strict"] = true, ["enum"] = new JsonArray(JsonValue.Create(name)), ["default"] = name
        };
        if (profile == WotVersionProfile.Incremental)
        {
            foreach (var group in source["groups"]!.AsObject())
            {
                foreach (var resource in group.Value!["resources"]!.AsObject())
                {
                    var definition = resource.Value!.AsObject();
                    definition["versionmode"] = "manual";
                    definition["singleversionroot"] = false;
                    definition["validatecompatibility"] = false;
                    definition["metaattributes"] = new JsonObject();
                    definition["attributes"]!["compatibilityline"] = new JsonObject { ["type"] = "uriabsolute" };
                }
            }
        }
        return RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()));
    }

    /// <summary>Reads the fixed profile declaration; its absence means legacy SemVer.</summary>
    /// <exception cref="RegistryException">The declaration is unknown, mutable or does not fix a single value.</exception>
    public static WotVersionProfile Read(RegistryModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Attributes.TryGetValue("wotversionprofile", out var attribute)) { return WotVersionProfile.SemVer; }
        if (attribute.Type != RegistryValueType.String || !attribute.ReadOnly || !attribute.Required ||
            !attribute.Strict || attribute.DefaultValue.ValueKind != JsonValueKind.String ||
            attribute.EnumValues.Count != 1 || attribute.EnumValues[0].ValueKind != JsonValueKind.String ||
            attribute.EnumValues[0].GetString() != attribute.DefaultValue.GetString())
        {
            throw new RegistryException(new("unknown-profile", "/wotversionprofile",
                "The WoT endpoint profile must have one required read-only fixed value."));
        }
        var profile = attribute.DefaultValue.GetString() switch
        {
            "semver" => WotVersionProfile.SemVer,
            "incremental" => WotVersionProfile.Incremental,
            _ => throw new RegistryException(new("unknown-profile", "/wotversionprofile",
                "The WoT endpoint profile is not supported."))
        };
        if (profile == WotVersionProfile.Incremental && model.Groups.Values.SelectMany(group => group.Resources.Values)
            .Any(resource => resource.Annotations.ModelCompatibleWith == RegistryDomainRules.WotModelUri &&
                resource.Singular is "thingmodel" or "thingdescription" &&
                (resource.VersionMode != "manual" || resource.SingleVersionRoot || resource.ValidateCompatibility ||
                 !resource.HasDocument ||
                 !resource.Attributes.TryGetValue("compatibilityline", out var line) ||
                 line.Type != RegistryValueType.UriAbsolute || line.ReadOnly)))
        {
            throw new RegistryException(new("unknown-profile", "/wotversionprofile",
                "Incremental WoT Resources require complete Documents, manual multiple-root versioning and writable absolute compatibility lines."));
        }
        return profile;
    }
}
