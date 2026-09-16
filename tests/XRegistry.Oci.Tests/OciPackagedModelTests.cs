// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;
using XRegistry.Models;

namespace XRegistry.Oci.Tests;

public class OciPackagedModelTests
{
    [Test]
    [Arguments(RegistryModelKind.Core)]
    [Arguments(RegistryModelKind.Endpoint)]
    [Arguments(RegistryModelKind.Message)]
    [Arguments(RegistryModelKind.Schema)]
    [Arguments(RegistryModelKind.CloudEvents)]
    [Arguments(RegistryModelKind.Registry)]
    [Arguments(RegistryModelKind.OpenUsd)]
    public async Task EveryPackagedModelProducesACompleteNativeSnapshotWithoutRuntimeModelGeneration(RegistryModelKind kind)
    {
        var compiled = BuiltInRegistryModels.Compile(kind);
        using var original = BuiltInRegistryModels.LoadSource(kind);
        var resolved = CapturePackagedIncludes(JsonNode.Parse(original.RootElement.GetRawText())!,
            new Uri("https://packaged.example/" + kind.ToString().ToLowerInvariant() + "/model.json"), 0);
        var root = JsonNode.Parse(AuthoredCapture.Registry(RegistryJson.Parse(resolved.ToJsonString())).RootElement.GetRawText())!;
        root["entity"]!["modelsource"] = JsonNode.Parse(original.RootElement.GetRawText());
        root["entity"]!["model"] = JsonNode.Parse(compiled.EffectiveModel.RootElement.GetRawText());
        var package = await OciSnapshotWriter.CreateAsync(new([RegistryJson.Parse(root.ToJsonString())]));
        await Assert.That(package.Validation.Configs).IsEqualTo(1);
        await Assert.That(package.Validation.Documents).IsEqualTo(0);
        await using var snapshot = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest,
            new(OciWriteOptionsForModels()));
        var metadata = await snapshot.ReadAsync(new(FederationOperation.Entity, "/"));
        foreach (var group in compiled.Groups.Values)
        {
            await Check.Json(metadata.Value.GetProperty(group.Plural), "{}");
            await Assert.That(metadata.Value.GetProperty(group.Plural + "count").GetInt32()).IsEqualTo(0);
        }
        await Assert.That(snapshot.Model.Groups.Count).IsEqualTo(compiled.Groups.Count);
        if (kind == RegistryModelKind.CloudEvents)
        {
            await Assert.That(snapshot.ModelSource.GetProperty("groups").GetProperty("$includes").GetArrayLength()).IsEqualTo(3);
            await Assert.That(snapshot.ResolvedModelSource.GetProperty("groups").TryGetProperty("$includes", out _)).IsFalse();
        }
    }

    private static FederationReadLimits OciWriteOptionsForModels() => new(maxWork: 4_000_000);

    private static JsonNode CapturePackagedIncludes(JsonNode node, Uri source, int depth)
    {
        if (depth > 128) { throw new InvalidOperationException("The test-only packaged capture exceeded its finite depth."); }
        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(n => n is null ? null : CapturePackagedIncludes(n, source, depth + 1)).ToArray());
        }
        if (node is not JsonObject obj) { return node.DeepClone(); }
        var result = new JsonObject();
        foreach (var property in obj.Where(p => p.Key is not ("$include" or "$includes")))
        {
            result.Add(property.Key, property.Value is null ? null : CapturePackagedIncludes(property.Value, source, depth + 1));
        }
        var includes = obj["$includes"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray()
            ?? (obj["$include"] is JsonNode single ? [single.GetValue<string>()] : []);
        foreach (var include in includes)
        {
            var uri = new Uri(source, include);
            var kind = Enum.Parse<RegistryModelKind>(uri.AbsolutePath.Split('/')[1], ignoreCase: true);
            using var input = BuiltInRegistryModels.LoadSource(kind);
            var selected = OciMetadataTests.Resolve(input.RootElement, uri.Fragment.Length == 0 ? "#" : uri.Fragment);
            var expanded = CapturePackagedIncludes(JsonNode.Parse(selected.GetRawText())!, uri, depth + 1).AsObject();
            foreach (var property in expanded)
            {
                if (!result.ContainsKey(property.Key)) { result.Add(property.Key, property.Value?.DeepClone()); }
            }
        }
        return result;
    }
}
