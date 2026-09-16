// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryMetadataTests
{
    [Test]
    [Arguments("integer", "184467440737095516160000", "184467440737095516160000")]
    [Arguments("uinteger", "0", "0")]
    [Arguments("decimal", "1.2345678901234567890123456789", "1.2345678901234567890123456789")]
    [Arguments("boolean", "true", "true")]
    [Arguments("string", "\"\"", "\"\"")]
    [Arguments("timestamp", "\"2026-09-11T14:00:00.123456789+02:00\"", "\"2026-09-11T12:00:00.123456789Z\"")]
    public async Task ScalarValuesPreserveTypePrecisionAndNormalizeTimestamps(string type, string value, string expected)
    {
        var definitions = Shape("{\"value\":{\"type\":\"" + type + "\"}}");
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("{\"value\":" + value + "}"), definitions);
        await Assert.That(result.Metadata.RootElement.GetProperty("value").GetRawText()).IsEqualTo(expected);
        await Assert.That(result.Obligations.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("integer", "1.1")]
    [Arguments("uinteger", "-1")]
    [Arguments("boolean", "\"true\"")]
    [Arguments("string", "1")]
    [Arguments("timestamp", "\"2024-04-31T12:00:00Z\"")]
    [Arguments("timestamp", "\"not-a-date\"")]
    [Arguments("uriabsolute", "\"relative/path\"")]
    public async Task InvalidScalarsReturnExactFieldDiagnostics(string type, string value)
    {
        var definitions = Shape("{\"value\":{\"type\":\"" + type + "\"}}");
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"value\":" + value + "}"), definitions));
        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/value");
    }

    [Test]
    public async Task DefaultsApplyWithinPresentObjectsButNeverCreateAnOptionalParent()
    {
        var definitions = Shape("""
            {"nested":{"type":"object","attributes":{"count":{"type":"integer","required":true,"default":42}}}}
            """);
        var absent = RegistryMetadataValidator.Validate(RegistryJson.Parse("{}"), definitions);
        var present = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"nested":{}}"""), definitions);
        var explicitNull = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"nested":{"count":null}}"""), definitions);

        await Assert.That(absent.Metadata.RootElement.TryGetProperty("nested", out _)).IsFalse();
        await Assert.That(present.Metadata.RootElement.GetProperty("nested").GetProperty("count").GetInt32()).IsEqualTo(42);
        await Assert.That(explicitNull.Metadata.RootElement.GetProperty("nested").GetProperty("count").GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task SubmittedReadonlyValuesAreIgnoredWithoutResettingDefaultsOrNullDeletions()
    {
        var definitions = Shape("""
            {"readonlyvalue":{"type":"boolean","readonly":true},
             "count":{"type":"integer","required":true,"default":42}}
            """);
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"readonlyvalue":{"not":"a boolean"},"count":null}"""), definitions,
            new() { Mode = RegistryMetadataMode.ClientInput });
        var omitted = RegistryMetadataValidator.Validate(RegistryJson.Parse("{}"), definitions,
            new() { Mode = RegistryMetadataMode.ClientInput });

        await Assert.That(result.Metadata.RootElement.TryGetProperty("readonlyvalue", out _)).IsFalse();
        await Assert.That(result.Metadata.RootElement.GetProperty("count").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(omitted.Metadata.RootElement.TryGetProperty("count", out _)).IsFalse();
    }

    [Test]
    [Arguments("""{"items":[1,null]}""", "/items/1")]
    [Arguments("""{"items":[1,"2"]}""", "/items/1")]
    [Arguments("""{"labels":{"Bad":"value"}}""", "/labels/Bad")]
    [Arguments("""{"labels":{"valid":null}}""", "/labels/valid")]
    [Arguments("""{"nested":{"unknown":1}}""", "/nested/unknown")]
    public async Task CollectionAndNestedErrorsRetainPrecisePointers(string metadata, string expectedPath)
    {
        var definitions = Shape("""
            {"items":{"type":"array","item":{"type":"integer"}},
             "labels":{"type":"map","item":{"type":"string"}},
             "nested":{"type":"object","attributes":{"known":"integer"}}}
            """);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(RegistryJson.Parse(metadata), definitions));
        await Assert.That(diagnostic.Path).IsEqualTo(expectedPath);
    }

    [Test]
    public async Task ConditionalSiblingDefinitionsActivateFromCaseInsensitiveValuesAndDefaults()
    {
        var definitions = Shape("""
            {"kind":{"type":"string","required":true,"default":"enabled","ifvalues":{
              "ENABLED":{"siblingattributes":{"extra":{"type":"integer","required":true}}}}}}
            """);
        var valid = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"extra":17}"""), definitions);
        await Assert.That(valid.Metadata.RootElement.GetProperty("kind").GetString()).IsEqualTo("enabled");
        await Assert.That(valid.Metadata.RootElement.GetProperty("extra").GetInt32()).IsEqualTo(17);
        await Assert.That(TestErrors.Capture(() => RegistryMetadataValidator.Validate(RegistryJson.Parse("{}"), definitions)).Path)
            .IsEqualTo("/extra");
        await Assert.That(TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"kind":"disabled","extra":17}"""), definitions)).Code).IsEqualTo("unknown_attribute");
    }

    [Test]
    public async Task WildcardsDoNotPermitInvalidNamesOrBypassDeclaredTypes()
    {
        var definitions = Shape("""{"known":"integer","*":{"type":"string"}}""");
        var valid = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"known":3,"other":"value"}"""), definitions);
        await Assert.That(valid.Metadata.RootElement.GetProperty("other").GetString()).IsEqualTo("value");
        await Assert.That(TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"Bad":"value"}"""), definitions)).Code).IsEqualTo("unknown_attribute");
        await Assert.That(TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"other":3}"""), definitions)).Code).IsEqualTo("invalid_attribute");
    }

    [Test]
    public async Task XidTargetsUseModelTypesButAllowDanglingEntityIds()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"body":{"type":"object","attributes":{"ref":{"type":"xid","target":"/groups/docs[/versions]"}}}},
             "groups":{"groups":{"singular":"group","resources":{"docs":{"singular":"doc"}}}}}
            """));
        var definitions = model.Attributes["body"].Attributes;
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"ref":"/groups/not-created/docs/missing/versions/v1"}"""), definitions, new() { Model = model });
        await Assert.That(result.Obligations.Count).IsEqualTo(0);
        await Assert.That(TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"ref":"/other/id/docs/missing"}"""), definitions, new() { Model = model })).Code)
            .IsEqualTo("invalid_attribute");
        var unresolved = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"ref":"/groups/id/docs/missing"}"""), definitions);
        await Assert.That(unresolved.Obligations[0].Kind).IsEqualTo("target");
    }

    [Test]
    public async Task RequiredMissingValuesAndExhaustedLimitsAreNotSuccessfulValidation()
    {
        var definitions = Shape("""{"value":{"type":"integer","required":true}}""");
        await Assert.That(TestErrors.Capture(() => RegistryMetadataValidator.Validate(RegistryJson.Parse("{}"), definitions)).Path)
            .IsEqualTo("/value");
        await Assert.That(TestErrors.Capture(() => RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"value":1}"""),
            definitions, new() { Limits = new RegistryJsonLimits { MaxNodes = 1 } })).Code).IsEqualTo("node_limit");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.That(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"value":1}"""), definitions, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    private static IReadOnlyDictionary<string, RegistryAttributeDefinition> Shape(string attributes) =>
        RegistryModel.Compile(RegistryJson.Parse("{\"attributes\":{\"body\":{\"type\":\"object\",\"attributes\":" +
            attributes + "}}}")).Attributes["body"].Attributes;
}
