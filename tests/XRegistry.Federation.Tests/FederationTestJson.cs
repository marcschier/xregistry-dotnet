// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace XRegistry.Federation.Tests;

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class FederationTestJsonSerializerContext : JsonSerializerContext
{
}

internal static class FederationTestJson
{
    public static string Serialize(string value) =>
        JsonSerializer.Serialize(value, FederationTestJsonSerializerContext.Default.String);

    public static string Serialize(string[] value) =>
        JsonSerializer.Serialize(value, FederationTestJsonSerializerContext.Default.StringArray);

    public static string[] DeserializeStringArray(string json) =>
        JsonSerializer.Deserialize(json, FederationTestJsonSerializerContext.Default.StringArray)
        ?? throw new JsonException("Expected a JSON string array.");
}
