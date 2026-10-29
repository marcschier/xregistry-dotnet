// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    /// <summary>Checks an API Group's optional format restriction.</summary>
    public static void ValidateApiGroupMetadata(JsonElement group, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireMetadataObject(group);
        if (group.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
        {
            ValidateApiFormat(format.GetString()!, "/format");
        }
    }

    /// <summary>Checks an API Version format and its optional owning Group restriction.</summary>
    public static void ValidateApiVersionMetadata(
        JsonElement version, JsonElement group, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireMetadataObject(version);
        RequireMetadataObject(group);
        if (!version.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String)
        {
            throw Invalid("An API Version must declare a format identifier.", "/format");
        }

        var value = format.GetString()!;
        ValidateApiFormat(value, "/format");
        if (group.TryGetProperty("format", out var groupFormat) && groupFormat.ValueKind == JsonValueKind.String &&
            !string.Equals(value, groupFormat.GetString(), StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("The API Version format differs from its owning Group's format.", "/format");
        }
    }

    private static void ValidateApiFormat(string value, string path)
    {
        var separator = value.IndexOf('/');
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) ||
            separator == 0 || separator == value.Length - 1 ||
            separator >= 0 && value.IndexOf('/', separator + 1) >= 0)
        {
            throw Invalid("An API format must be a non-empty identifier of the form <SPEC>[/<VERSION>].", path);
        }
    }
}
