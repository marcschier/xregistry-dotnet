// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;

namespace XRegistry.Oci.Tests;

internal static class AuthoredCapture
{
    internal static List<RegistryJson> Groups(int count, int idLength = 5)
    {
        var source = RegistryJson.Parse("""{"groups":{"items":{"singular":"item"}}}""");
        var result = new List<RegistryJson> { Registry(source) };
        for (var i = 0; i < count; i++)
        {
            var id = "g" + i.ToString("D4", CultureInfo.InvariantCulture);
            id = id.PadRight(idLength, 'x');
            result.Add(RegistryJson.Parse(new JsonObject
            {
                ["formatversion"] = 1,
                ["kind"] = "group",
                ["entity"] = new JsonObject
                {
                    ["xid"] = "/items/" + id,
                    ["itemid"] = id,
                    ["epoch"] = 1,
                    ["createdat"] = "2026-09-01T12:00:00Z",
                    ["modifiedat"] = "2026-09-01T12:00:00Z",
                },
            }.ToJsonString()));
        }
        return result;
    }

    internal static RegistryJson Registry(RegistryJson source)
    {
        var model = RegistryModel.Compile(source);
        return RegistryJson.Parse(new JsonObject
        {
            ["formatversion"] = 1,
            ["kind"] = "registry",
            ["snapshot"] = "offline-complete",
            ["modelresolved"] = JsonNode.Parse(source.RootElement.GetRawText()),
            ["entity"] = new JsonObject
            {
                ["registryid"] = "authored",
                ["xid"] = "/",
                ["specversion"] = "1.0-rc4",
                ["epoch"] = 1,
                ["createdat"] = "2026-09-01T12:00:00Z",
                ["modifiedat"] = "2026-09-01T12:00:00Z",
                ["modelsource"] = JsonNode.Parse(source.RootElement.GetRawText()),
                ["model"] = JsonNode.Parse(model.EffectiveModel.RootElement.GetRawText()),
                ["capabilities"] = JsonNode.Parse("""
                    {"available":{"entities":{"mutable":false},"model":{"mutable":false},"modelsource":{"mutable":false},
                      "capabilities":{"mutable":false}},"flags":[],"mutable":[],"pagination":false}
                    """),
            },
        }.ToJsonString());
    }
}
