// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class JsonStructureValidationTests
{
    private const string Header = """
        "$schema":"https://json-structure.org/meta/core/v0/#","$id":"https://example.test/Schema","name":"Schema"
        """;

    [Test]
    [Arguments(""" "type":"string","enum":["a","b"] """)]
    [Arguments(""" "type":"object","properties":{"Id":{"type":"int64"},"Name":{"type":"string"}},"required":["Id"] """)]
    [Arguments(""" "type":"array","items":{"type":"string"} """)]
    [Arguments(""" "type":"map","values":{"type":"uint32"} """)]
    [Arguments(""" "type":"tuple","properties":{"A":{"type":"int32"},"B":{"type":"string"}},"tuple":["B","A"] """)]
    [Arguments(""" "type":"choice","choices":{"Text":{"type":"string"},"Number":{"type":"int32"}} """)]
    [Arguments(""" "$root":"/definitions/Node","definitions":{"Node":{"type":"object","properties":{"Next":{"type":["null",{"$ref":"#/definitions/Node"}]}}}} """)]
    [Arguments(""" "definitions":{"Namespace":{"Counter":{"type":"uint64","const":"18446744073709551615"}}} """)]
    [Arguments(""" "type":"object","properties":{"A":{"type":"string"},"B":{"type":"int32"}},"required":[["A"],["B"]] """)]
    [Arguments(""" "type":"decimal","precision":10,"scale":2 """)]
    public async Task CoreGrammarAndRecursiveNamedReferencesAreAccepted(string body)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(
            "JsonStructure/draft-04", Encoding.UTF8.GetBytes("{" + Header + "," + body + "}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(""" "type":"object","properties":{} """)]
    [Arguments(""" "type":"object","properties":{"bad-name":{"type":"string"}} """)]
    [Arguments(""" "type":"object","properties":{"A":{"type":"string"}},"required":["Missing"] """)]
    [Arguments(""" "type":["string","null"] """)]
    [Arguments(""" "type":{"$ref":"#/definitions/Value"},"definitions":{"Value":{"type":"string"}} """)]
    [Arguments(""" "type":"array" """)]
    [Arguments(""" "type":"string","items":{"type":"string"} """)]
    [Arguments(""" "type":"tuple","properties":{"A":{"type":"string"}},"tuple":[] """)]
    [Arguments(""" "type":"string","enum":["a","a"] """)]
    [Arguments(""" "type":"integer","const":2147483648 """)]
    [Arguments(""" "type":"int64","const":123 """)]
    [Arguments(""" "type":"uint64","const":"18446744073709551616" """)]
    [Arguments(""" "type":"object","properties":{"Ref":{"type":{"$ref":"#/definitions/Missing"}}} """)]
    [Arguments(""" "$root":"/definitions/Value","type":"string","definitions":{"Value":{"type":"string"}} """)]
    [Arguments(""" "type":"boolean","maxLength":3 """)]
    [Arguments(""" "type":"decimal","precision":3,"scale":4 """)]
    [Arguments(""" "type":"object","properties":{"A":{"$ref":"#/definitions/X"}},"definitions":{"X":{"type":"string"}} """)]
    public async Task StructuralAndPrimitiveTypeViolationsAreInvalid(string body)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(
            "JsonStructure/draft-04", Encoding.UTF8.GetBytes("{" + Header + "," + body + "}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
    }

    [Test]
    [Arguments(""" "type":"string","$uses":["JSONStructureAlternateNames"] """)]
    [Arguments(""" "type":"object","properties":{"A":{"type":"string"}},"abstract":true """)]
    [Arguments(""" "type":"datetime","const":"2026-09-11T12:00:00Z" """)]
    public async Task UnqualifiedExtensionsAndLexicalPoliciesAreNotReportedAsValid(string body)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(
            "JsonStructure/draft-04", Encoding.UTF8.GetBytes("{" + Header + "," + body + "}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
    }

    [Test]
    public async Task BareJsonIsNotAJsonStructureSchema()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonStructure", "{}"u8.ToArray());
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
    }
}
