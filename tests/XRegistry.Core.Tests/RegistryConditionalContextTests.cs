// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryConditionalContextTests
{
    [Test]
    public async Task ClientInputReturnsOnlySubmittedValuesWithRetainedConditionalContext()
    {
        var definitions = Shape("""
            {"kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra":{"type":"integer"}}}}},
             "count":{"type":"integer","required":true,"default":7},
             "managed":{"type":"boolean","readonly":true}}
            """);
        var retained = RegistryJson.Parse("""{"kind":"on","extra":1,"count":9,"managed":true}""");
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"extra":2,"managed":{"invalid":true}}"""), definitions,
            new() { Mode = RegistryMetadataMode.ClientInput, RetainedMetadata = retained.RootElement });

        await Assert.That(result.Metadata.RootElement.GetRawText()).IsEqualTo("""{"extra":2}""");
        await Assert.That(result.Obligations.Count).IsEqualTo(0);
        await Assert.That(retained.RootElement.GetRawText()).IsEqualTo("""{"kind":"on","extra":1,"count":9,"managed":true}""");
    }

    [Test]
    public async Task RetainedContextResolvesChainedConditionalDefinitions()
    {
        var definitions = Shape("""
            {"kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{
              "phase":{"type":"string","ifvalues":{"ready":{"siblingattributes":{"extra":{"type":"integer"}}}}}
            }}}}}
            """);
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"extra":2}"""), definitions,
            new()
            {
                Mode = RegistryMetadataMode.ClientInput,
                RetainedMetadata = RegistryJson.Parse("""{"kind":"on","phase":"ready","extra":1}""").RootElement
            });

        await Assert.That(result.Metadata.RootElement.GetRawText()).IsEqualTo("""{"extra":2}""");
    }

    [Test]
    public async Task ClientInputWithoutRetainedContextDoesNotGuessConditionalBranch()
    {
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"extra":2}"""), ConditionalShape(),
            new() { Mode = RegistryMetadataMode.ClientInput }));

        await Assert.That(diagnostic.Code).IsEqualTo("unknown_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/extra");
    }

    [Test]
    public async Task CompleteEntityDoesNotBorrowRetainedConditionalContext()
    {
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"kind":"off","extra":2}"""), ConditionalShape(),
            new() { RetainedMetadata = RegistryJson.Parse("""{"kind":"on","extra":1}""").RootElement }));

        await Assert.That(diagnostic.Code).IsEqualTo("unknown_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/extra");
    }

    [Test]
    public async Task NullDiscriminatorSelectsDefaultWithoutReplacingSubmittedDeletion()
    {
        var definitions = Shape("""
            {"kind":{"type":"string","required":true,"default":"on","ifvalues":{
              "on":{"siblingattributes":{"extra":{"type":"integer"}}}
            }}}
            """);
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"kind":null,"extra":2}"""), definitions,
            new()
            {
                Mode = RegistryMetadataMode.ClientInput,
                RetainedMetadata = RegistryJson.Parse("""{"kind":"off"}""").RootElement
            });

        await Assert.That(result.Metadata.RootElement.GetProperty("kind").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(result.Metadata.RootElement.GetProperty("extra").GetInt32()).IsEqualTo(2);
    }

    [Test]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("1")]
    [Arguments("\"not an object\"")]
    public async Task RetainedContextMustBeAnObject(string retained)
    {
        await Assert.That(() => RegistryMetadataValidator.Validate(RegistryJson.Parse("{}"), ConditionalShape(),
            new()
            {
                Mode = RegistryMetadataMode.ClientInput,
                RetainedMetadata = RegistryJson.Parse(retained).RootElement
            })).Throws<ArgumentException>();
    }

    [Test]
    public async Task RetainedContextRespectsTheMetadataNodeBudget()
    {
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{}"), ConditionalShape(),
            new()
            {
                Mode = RegistryMetadataMode.ClientInput,
                RetainedMetadata = RegistryJson.Parse("""{"kind":"on","extra":1,"other":2}""").RootElement,
                Limits = new RegistryJsonLimits { MaxNodes = 3 }
            }));

        await Assert.That(diagnostic.Code).IsEqualTo("node_limit");
    }

    [Test]
    public async Task RetainedContextAtTheNodeBudgetBoundaryPreservesPartialOutput()
    {
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"extra":2}"""), ConditionalShape(),
            new()
            {
                Mode = RegistryMetadataMode.ClientInput,
                RetainedMetadata = RegistryJson.Parse("""{"kind":"on","extra":1,"other":2}""").RootElement,
                Limits = new RegistryJsonLimits { MaxNodes = 4 }
            });

        await Assert.That(result.Metadata.RootElement.GetRawText()).IsEqualTo("""{"extra":2}""");
    }

    private static IReadOnlyDictionary<string, RegistryAttributeDefinition> ConditionalShape() => Shape("""
        {"kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra":{"type":"integer"}}}}}}
        """);

    private static IReadOnlyDictionary<string, RegistryAttributeDefinition> Shape(string attributes) =>
        RegistryModel.Compile(RegistryJson.Parse(
            "{\"attributes\":{\"body\":{\"type\":\"object\",\"attributes\":" + attributes + "}}}"))
            .Attributes["body"].Attributes;
}
