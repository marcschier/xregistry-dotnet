// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

internal sealed class CapabilityProfile
{
    internal CapabilityProfile(RegistryJson metadata, string revision)
    {
        Metadata = metadata;
        Revision = revision;
        var root = metadata.RootElement;
        Available = root.GetProperty("available").EnumerateObject().ToFrozenDictionary(
            static property => property.Name, static property => property.Value.GetProperty("mutable").GetBoolean(),
            StringComparer.OrdinalIgnoreCase);
        Flags = Strings(root, "flags");
        Ignores = Strings(root, "ignores");
        Formats = Strings(root, "formats");
        VersionModes = Strings(root, "versionmodes");
        Pagination = root.GetProperty("pagination").GetBoolean();
        ShortSelf = root.GetProperty("shortself").GetBoolean();
        Compatibilities = root.GetProperty("compatibilities").EnumerateObject().ToFrozenDictionary(
            static property => property.Name,
            static property => (IReadOnlySet<string>)property.Value.EnumerateArray().Select(static value => value.GetString()!)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    internal RegistryJson Metadata { get; }
    internal string Revision { get; }
    internal FrozenDictionary<string, bool> Available { get; }
    internal FrozenSet<string> Flags { get; }
    internal FrozenSet<string> Ignores { get; }
    internal FrozenSet<string> Formats { get; }
    internal FrozenSet<string> VersionModes { get; }
    internal FrozenDictionary<string, IReadOnlySet<string>> Compatibilities { get; }
    internal bool Pagination { get; }
    internal bool ShortSelf { get; }
    internal bool Mutable(string name) => Available.TryGetValue(name, out var mutable) && mutable;
    internal bool Has(string name) => Available.ContainsKey(name);
    internal bool Compatibility(string format, string mode) =>
        Compatibilities.Any(entry => FileSystemName.MatchesSimpleExpression(entry.Key, format, true) && entry.Value.Contains(mode));

    internal static CapabilityProfile Update(CapabilityProfile previous, RegistryJson defaults, JsonObject? input,
        bool patch, bool shortSelfSupported, RegistryJsonLimits limits, int maxWork, CancellationToken cancellationToken)
    {
        var work = 0L;
        void Spend(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > maxWork - work)
            {
                throw Error("operation_limit", "The capability-validation work budget is exhausted.");
            }

            work += count;
        }

        var baseValues = ServerJson.Object(defaults);
        var result = patch && input is not null ? ServerJson.Object(previous.Metadata) : (JsonObject)baseValues.DeepClone();
        if (input is not null)
        {
            foreach (var property in input)
            {
                Spend(1);
                if (!baseValues.ContainsKey(property.Key))
                {
                    throw Error("capability_unknown", "The capability is not offered.", ("field", property.Key));
                }

                result[property.Key] = (property.Value ?? baseValues[property.Key])?.DeepClone();
            }
        }

        var offered = defaults.RootElement;
        foreach (var name in new[] { "flags", "formats", "ignores", "mutable", "specversions", "versionmodes" })
        {
            result[name] = NormalizeList(result[name], offered.GetProperty(name), name, Spend);
        }

        RequireMember(result["specversions"]!.AsArray(), "1.0-rc4", "specversions");
        RequireMember(result["versionmodes"]!.AsArray(), "manual", "versionmodes");
        foreach (var name in new[] { "pagination", "shortself" })
        {
            if (result[name] is not JsonValue value || !value.TryGetValue<bool>(out var enabled))
            {
                throw ValueError(name, result[name], "false, true", "The capability requires a JSON boolean.");
            }

            if (name == "shortself" && enabled && !shortSelfSupported)
            {
                throw Error("capability_error", "The configured PublicRoot cannot support a dollar-free shortself URL.");
            }
        }

        var available = result["available"] as JsonObject ?? throw ValueError("available", result["available"], "object", "available must be an object.");
        var normalized = new JsonObject();
        foreach (var entry in available)
        {
            Spend(baseValues["available"]!.AsObject().Count + 1);
            var name = baseValues["available"]!.AsObject().Select(static pair => pair.Key)
                .FirstOrDefault(name => string.Equals(name, entry.Key, StringComparison.OrdinalIgnoreCase));
            if (name is null || normalized.ContainsKey(name))
            {
                throw ValueError("available", JsonValue.Create(entry.Key), string.Join(", ", baseValues["available"]!.AsObject().Select(static item => item.Key)),
                    "An available metadata type is unknown or duplicated.");
            }

            if (entry.Value is not JsonObject fields || fields.Count != 1 ||
                fields["mutable"] is not JsonValue mutableValue || !mutableValue.TryGetValue<bool>(out var mutable))
            {
                throw ValueError("available." + entry.Key, entry.Value, "{\"mutable\":false}, {\"mutable\":true}",
                    "Each available entry must contain exactly a mutable boolean.");
            }

            var upper = baseValues["available"]![name]!["mutable"]!.GetValue<bool>();
            if (name is "capabilities" or "capabilitiesoffered" or "model" or "export" ? mutable != upper : mutable && !upper)
            {
                throw Error("capability_error", "The requested metadata mutability is not offered: " + name);
            }

            normalized[name] = new JsonObject { ["mutable"] = mutable };
        }

        foreach (var required in new[] { "capabilities", "entities", "model" })
        {
            if (!normalized.ContainsKey(required))
            {
                throw Error("capability_missing_value", "available must retain its mandatory metadata types.", ("name", "available"), ("value", required));
            }
        }

        result["available"] = normalized;
        var formatNames = result["formats"]!.AsArray().Select(static value => value!.GetValue<string>()).ToArray();
        var combinations = result["compatibilities"] as JsonObject ??
            throw ValueError("compatibilities", result["compatibilities"], "object", "compatibilities must be an object.");
        var compatibility = new JsonObject();
        foreach (var entry in combinations)
        {
            Spend(formatNames.Length + 1);
            if (entry.Key.Length == 0 || entry.Key.Contains('?', StringComparison.Ordinal))
            {
                throw ValueError("compatibilities", JsonValue.Create(entry.Key), string.Join(", ", formatNames), "A compatibility format key is invalid.");
            }

            var matches = formatNames.Where(format =>
                FileSystemName.MatchesSimpleExpression(entry.Key, format, ignoreCase: true)).ToArray();
            if (matches.Length == 0)
            {
                throw Error("capability_error", "A compatibility format is not enabled: " + entry.Key);
            }

            foreach (var format in matches)
            {
                if (!offered.GetProperty("compatibilities").TryGetProperty(format, out var choices))
                {
                    throw Error("capability_error", "No compatibility policy is offered for " + format + ".");
                }

                var modes = NormalizeList(entry.Value, choices, "compatibilities", Spend);
                if (compatibility.ContainsKey(format) && !ServerJson.Equal(compatibility[format], modes))
                {
                    throw Error("capability_error", "Overlapping compatibility format keys must agree.");
                }

                compatibility[format] = modes;
            }
        }

        result["compatibilities"] = compatibility;
        return new(ServerJson.Own(result, limits), Guid.NewGuid().ToString("N"));
    }

    private static JsonArray NormalizeList(JsonNode? value, JsonElement offered, string name, Action<int> spend)
    {
        spend((value as JsonArray)?.Count ?? 1);
        if (value is not JsonArray array || array.Any(static item => item is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
        {
            throw ValueError(name, value, string.Join(", ", offered.EnumerateArray().Select(static item => item.GetString())),
                "The capability must be an array of strings.");
        }

        var supplied = array.Select(static item => item!.GetValue<string>()).ToArray();
        if (supplied.Contains("*", StringComparer.Ordinal))
        {
            spend(offered.GetArrayLength());
            if (supplied.Length != 1)
            {
                throw Error("capability_wildcard", "A capability wildcard cannot be combined with other values.", ("field", name));
            }

            return JsonNode.Parse(offered.GetRawText())!.AsArray();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new JsonArray();
        foreach (var item in supplied)
        {
            spend(offered.GetArrayLength() + 1);
            var canonical = offered.EnumerateArray().Select(static choice => choice.GetString()!)
                .FirstOrDefault(choice => string.Equals(choice, item, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                throw ValueError(name, JsonValue.Create(item), string.Join(", ", offered.EnumerateArray().Select(static choice => choice.GetString())),
                    "The capability value is not offered.");
            }

            if (!seen.Add(canonical))
            {
                throw ValueError(name, JsonValue.Create(item), string.Join(", ", offered.EnumerateArray().Select(static choice => choice.GetString())),
                    "Capability values must be case-insensitively unique.");
            }

            result.Add((JsonNode?)JsonValue.Create(canonical));
        }

        return result;
    }

    private static void RequireMember(JsonArray values, string required, string name)
    {
        if (!values.Any(value => string.Equals(value!.GetValue<string>(), required, StringComparison.OrdinalIgnoreCase)))
        {
            throw Error("capability_missing_value", "The capability is missing a mandatory value.", ("name", name), ("value", required));
        }
    }

    private static FrozenSet<string> Strings(JsonElement root, string name) =>
        root.GetProperty(name).EnumerateArray().Select(static value => value.GetString()!).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static RegistryException Error(string code, string message, params (string Name, string Value)[] arguments) =>
        ServerErrors.With(code, "/capabilities", message, arguments);
    private static RegistryException ValueError(string name, JsonNode? value, string choices, string message) =>
        Error("capability_value", message, ("field", name),
            ("value", value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "null"),
            ("list", choices));
}
