// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

internal sealed class RequestFlags
{
    internal bool Doc { get; private set; }
    internal bool Binary { get; private set; }
    internal bool Collections { get; private set; }
    internal List<string> Inline { get; private set; } = [];
    internal string? SetDefault { get; private set; }
    internal JsonNode? Epoch { get; private set; }
    internal List<string> Filters { get; } = [];
    internal string? Sort { get; private set; }
    internal ulong? Limit { get; private set; }
    internal string? Cursor { get; private set; }
    internal HashSet<string> Ignore { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly string[] IgnoreValues = ["capabilities", "defaultversionid", "defaultversionsticky", "epoch", "id", "modelsource", "readonly"];

    internal static RequestFlags Parse(RegistryOperation operation, RegistryLimits limits, CapabilityProfile? capabilities = null)
    {
        var flags = new RequestFlags();
        var singletons = new HashSet<string>(StringComparer.Ordinal);
        var characters = 0;
        var inlineSpecified = false;
        foreach (var parameter in operation.Parameters)
        {
            characters += parameter.Key.Length + (parameter.Value?.Length ?? 0) + 2;
            if (characters > limits.MaxQueryCharacters)
            {
                throw ServerErrors.Create("too_large", operation.Path.EscapedPath, "The query exceeds its character budget.");
            }

            if (capabilities is not null &&
                (parameter.Key is "binary" or "collections" or "doc" or "epoch" or "filter" or "ignore" or "inline" or "setdefaultversionid" or "sort" or "specversion" &&
                    !capabilities.Flags.Contains(parameter.Key) || parameter.Key == "limit" && !capabilities.Pagination))
            {
                continue;
            }

            if (parameter.Key is "doc" or "binary" or "collections" or "epoch" or "setdefaultversionid" or "specversion" or "sort" or "limit" or "cursor" &&
                !singletons.Add(parameter.Key))
            {
                throw ServerErrors.With(parameter.Key == "sort" ? "bad_sort" : parameter.Key == "cursor" ? "bad_cursor" : "bad_flag",
                    operation.Path.EscapedPath, "A single-valued flag was repeated.",
                    ("flag", parameter.Key), ("value", parameter.Value ?? ""));
            }

            switch (parameter.Key)
            {
                case "doc": flags.Doc = true; break;
                case "binary": flags.Binary = true; break;
                case "collections":
                    if (operation.Path.Kind is not (RegistryPathKind.Registry or RegistryPathKind.Group))
                    {
                        throw ServerErrors.With("bad_flag", operation.Path.EscapedPath, "collections only applies to Registry and Group entities.",
                            ("flag", "collections"));
                    }

                    flags.Collections = true;
                    flags.Inline.Add("*");
                    break;
                case "inline":
                    inlineSpecified = true;
                    flags.Inline.AddRange(string.IsNullOrEmpty(parameter.Value) ? ["*"] : parameter.Value.Split(','));
                    break;
                case "filter":
                    if (string.IsNullOrEmpty(parameter.Value))
                    {
                        throw ServerErrors.With("bad_filter", operation.Path.EscapedPath, "A filter expression is required.", ("value", parameter.Value ?? ""));
                    }

                    flags.Filters.Add(parameter.Value);
                    break;
                case "sort":
                    if (string.IsNullOrEmpty(parameter.Value))
                    {
                        throw ServerErrors.With("bad_sort", operation.Path.EscapedPath, "A sort projection is required.", ("value", parameter.Value ?? ""));
                    }

                    flags.Sort = parameter.Value;
                    break;
                case "limit":
                    if (operation.Action is not (RegistryAction.Read or RegistryAction.Head) ||
                        operation.Path.Kind is not (RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection) ||
                        !ulong.TryParse(parameter.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit == 0)
                    {
                        throw ServerErrors.With("bad_flag", operation.Path.EscapedPath, "limit requires a positive UInt64 on an initial collection read.", ("flag", "limit"));
                    }

                    flags.Limit = limit;
                    break;
                case "cursor":
                    if (operation.Action is not (RegistryAction.Read or RegistryAction.Head) ||
                        operation.Path.Kind is not (RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection) ||
                        operation.Parameters.Count != 1 || string.IsNullOrEmpty(parameter.Value))
                    {
                        throw ServerErrors.Create("bad_cursor", operation.Path.EscapedPath, "A continuation must use its opaque URL without added or modified parameters.");
                    }

                    flags.Cursor = parameter.Value;
                    break;
                case "ignore":
                    if (operation.Action is RegistryAction.Read or RegistryAction.Head or RegistryAction.Options)
                    {
                        throw ServerErrors.With("bad_ignore", operation.Path.EscapedPath, "ignore only applies to write operations.", ("value", parameter.Value ?? ""));
                    }

                    foreach (var value in string.IsNullOrEmpty(parameter.Value) ? ["*"] : parameter.Value.Split(','))
                    {
                        if (value == "*")
                        {
                            flags.Ignore.UnionWith(capabilities?.Ignores ?? (IEnumerable<string>)IgnoreValues);
                        }
                        else if (capabilities?.Ignores.Contains(value) ?? IgnoreValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                        {
                            flags.Ignore.Add(value.ToLowerInvariant());
                        }
                        else
                        {
                            throw ServerErrors.With("bad_ignore", operation.Path.EscapedPath, "The requested ignore value is not supported.", ("value", value));
                        }
                    }

                    break;
                case "epoch":
                    if (operation.Action != RegistryAction.Delete ||
                        operation.Path.Kind is not (RegistryPathKind.Group or RegistryPathKind.Resource or RegistryPathKind.Version))
                    {
                        throw ServerErrors.With("bad_flag", operation.Path.EscapedPath, "epoch is only allowed on single-entity deletion.", ("flag", "epoch"));
                    }

                    try
                    {
                        flags.Epoch = JsonNode.Parse(RegistryJson.Parse(parameter.Value ?? "", limits.Json).RootElement.GetRawText());
                        if (flags.Epoch is not null)
                        {
                            _ = ServerJson.Unsigned(flags.Epoch, operation.Path.EscapedPath);
                        }
                    }
                    catch (RegistryException exception)
                    {
                        throw ServerErrors.Wrap(exception, "bad_flag", operation.Path.EscapedPath, ("flag", "epoch"));
                    }

                    break;
                case "setdefaultversionid":
                    if (operation.Action is RegistryAction.Read or RegistryAction.Head or RegistryAction.Options ||
                        operation.Path.Kind is not (RegistryPathKind.Resource or RegistryPathKind.Meta or RegistryPathKind.Version or RegistryPathKind.VersionCollection) ||
                        parameter.Value == "request" && !(operation.Action == RegistryAction.Post && operation.Path.Kind == RegistryPathKind.Resource))
                    {
                        throw ServerErrors.With("bad_flag", operation.Path.EscapedPath, "setdefaultversionid is invalid on this operation.",
                            ("flag", "setdefaultversionid"));
                    }

                    if (string.IsNullOrEmpty(parameter.Value))
                    {
                        throw ServerErrors.With("bad_defaultversionid", operation.Path.EscapedPath,
                            "A Version identifier is required.", ("value", parameter.Value ?? ""));
                    }

                    if (parameter.Value is not ("request" or "null"))
                    {
                        try
                        {
                            _ = RegistryId.Parse(parameter.Value);
                        }
                        catch (RegistryException exception)
                        {
                            throw ServerErrors.Wrap(exception, "bad_defaultversionid", operation.Path.EscapedPath, ("value", parameter.Value));
                        }
                    }

                    flags.SetDefault = parameter.Value;
                    break;
                case "specversion":
                    var version = parameter.Value?.ToLowerInvariant();
                    var parts = version?.Split('-', 2);
                    if (parts is not { Length: 2 } || parts[1] != "rc4" ||
                        !(parts[0] == "1.0" || parts[0].StartsWith("1.0.", StringComparison.Ordinal) &&
                          parts[0][4..].Length != 0 && parts[0][4..].All(char.IsAsciiDigit)))
                    {
                        throw ServerErrors.With("unsupported_specversion", operation.Path.EscapedPath, "Only specification version 1.0-rc4 is supported.",
                            ("specversion", parameter.Value ?? ""), ("list", "1.0-rc4"));
                    }

                    break;
            }
        }

        if (operation.Path.Kind == RegistryPathKind.Export)
        {
            flags.Doc = true;
            if (!inlineSpecified)
            {
                flags.Inline = ["*", "capabilities", "modelsource"];
            }
        }

        return flags;
    }

    internal static bool Includes(IReadOnlyList<string> inline, string name, bool configuration = false) =>
        inline.Any(value => value == name || value.StartsWith(name + ".", StringComparison.Ordinal) || !configuration && value == "*");

    internal static List<string> Child(IReadOnlyList<string> inline, string name) =>
        inline.Where(value => value == "*" || value.StartsWith(name + ".", StringComparison.Ordinal))
            .Select(value => value == "*" ? "*" : value[(name.Length + 1)..]).ToList();
}
