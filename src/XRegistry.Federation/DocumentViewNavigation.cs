// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace XRegistry.Federation;

internal static class DocumentViewNavigation
{
    internal static void Rebase(JsonObject entity, string[] parts, RegistryModel model,
        string rootPointer, string sourcePointer, JsonObject? parentResource = null,
        FederationReadBudget? fragmentBudget = null, CancellationToken cancellationToken = default)
    {
        var defaultIncluded = parentResource?["versions"] is JsonObject versions &&
            entity["defaultversionid"] is JsonValue defaultId &&
            defaultId.TryGetValue<string>(out var id) && id is not null && versions.ContainsKey(id);
        var navigation = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["self"] = true, ["shortself"] = true,
            ["metaurl"] = entity["meta"] is JsonObject, ["defaultversionurl"] = defaultIncluded
        };
        IEnumerable<string> collections = parts.Length switch
        {
            0 => model.Groups.Keys,
            2 => model.Groups[parts[0]].Resources.Keys,
            4 => ["versions"],
            _ => []
        };
        foreach (var collection in collections)
        {
            navigation[collection + "url"] = entity[collection] is JsonObject;
            if (entity[collection] is JsonObject members)
            {
                foreach (var member in members)
                {
                    if (member.Value is not JsonObject child)
                    {
                        throw FederationJson.Invalid("An inlined collection member must be an entity.");
                    }
                    Rebase(child, [.. parts, collection, member.Key], model, rootPointer, sourcePointer,
                        fragmentBudget: fragmentBudget, cancellationToken: cancellationToken);
                }
            }
        }
        if (parts.Length == 4 && entity["meta"] is JsonObject meta)
        {
            Rebase(meta, [.. parts, "meta"], model, rootPointer, sourcePointer, entity, fragmentBudget, cancellationToken);
        }
        foreach (var (name, included) in navigation)
        {
            if (entity[name] is JsonValue value && value.TryGetValue<string>(out var link))
            {
                if (!link.StartsWith('#') && !included) { continue; }
                if (fragmentBudget is not null)
                {
                    link = "#" + DecodeFragment(link, fragmentBudget, cancellationToken);
                }
                if (!link.StartsWith('#') || (sourcePointer.Length != 0 &&
                    link != "#" + sourcePointer && !link.StartsWith("#" + sourcePointer + "/", StringComparison.Ordinal)))
                {
                    throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                        "The source did not return document-view navigation despite the requested flag.");
                }
                var pointer = rootPointer + link[(1 + sourcePointer.Length)..];
                entity[name] = "#" + (fragmentBudget is null ? pointer :
                    string.Join("/", pointer.Split('/').Select(Uri.EscapeDataString)));
            }
        }
    }

    internal static string PointerToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static string DecodeFragment(string fragment, FederationReadBudget budget, CancellationToken token)
    {
        try { return WotRegistryResolver.DecodeFragment(fragment, budget, token); }
        catch (RegistryException exception) { throw FederationJson.FromCore(exception); }
    }
}
