// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private static void ValidateDeprecationRange(RegistryJson metadata, string subject)
        {
            if (metadata.RootElement.TryGetProperty("deprecated", out var deprecated) &&
                deprecated.ValueKind == JsonValueKind.Object &&
                deprecated.TryGetProperty("effective", out var effective) &&
                deprecated.TryGetProperty("removal", out var removal) &&
                ServerJson.CompareTimestamps(removal.GetString()!, effective.GetString()!) < 0)
            {
                throw ServerErrors.With("invalid_attribute", subject,
                    "The deprecation removal time cannot precede its effective time.", ("name", "deprecated.removal"));
            }
        }

        private void CheckRemovalPromise(Entity entity)
        {
            if (entity.Attributes["deprecated"] is JsonObject deprecated &&
                ServerJson.Text(deprecated, "removal") is { } removal &&
                ServerJson.CompareTimestamps(_now, removal) < 0)
            {
                throw ServerErrors.Create("bad_request", entity.Key,
                    "The entity cannot be deleted before its declared deprecation removal time.");
            }
        }
    }
}
