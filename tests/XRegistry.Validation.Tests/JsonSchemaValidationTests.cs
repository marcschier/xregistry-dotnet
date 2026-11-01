// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class JsonSchemaValidationTests
{
    [Test]
    [Arguments("JsonSchema/draft-07", "$defs")]
    [Arguments("JsonSchema/draft-07", "$anchor")]
    [Arguments("JsonSchema/draft-07", "$vocabulary")]
    [Arguments("JsonSchema/draft-07", "$dynamicRef")]
    [Arguments("JsonSchema/draft-07", "$dynamicAnchor")]
    [Arguments("JsonSchema/draft-07", "$recursiveRef")]
    [Arguments("JsonSchema/draft-07", "$recursiveAnchor")]
    [Arguments("JsonSchema/draft-07", "dependentSchemas")]
    [Arguments("JsonSchema/draft-07", "dependentRequired")]
    [Arguments("JsonSchema/draft-07", "minContains")]
    [Arguments("JsonSchema/draft-07", "maxContains")]
    [Arguments("JsonSchema/draft-07", "unevaluatedItems")]
    [Arguments("JsonSchema/draft-07", "unevaluatedProperties")]
    [Arguments("JsonSchema/draft-07", "contentSchema")]
    [Arguments("JsonSchema/draft-07", "prefixItems")]
    [Arguments("JsonSchema/draft-07", "deprecated")]
    [Arguments("JsonSchema/draft/2019-09", "$dynamicRef")]
    [Arguments("JsonSchema/draft/2019-09", "$dynamicAnchor")]
    [Arguments("JsonSchema/draft/2019-09", "prefixItems")]
    [Arguments("JsonSchema/draft/2020-12", "additionalItems")]
    public async Task KeywordsAbsentFromSelectedDialectDoNotValidateTheirAnnotationValues(string format, string keyword)
    {
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteNull(keyword); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", "$defs", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "$anchor", "schema.string")]
    [Arguments("JsonSchema/draft/2019-09", "$vocabulary", "schema.object")]
    [Arguments("JsonSchema/draft/2020-12", "$dynamicRef", "schema.string")]
    [Arguments("JsonSchema/draft/2020-12", "$dynamicAnchor", "schema.string")]
    [Arguments("JsonSchema/draft/2019-09", "$recursiveRef", "schema.string")]
    [Arguments("JsonSchema/draft/2019-09", "$recursiveAnchor", "schema.boolean")]
    [Arguments("JsonSchema/draft/2019-09", "dependentSchemas", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "dependentRequired", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "minContains", "schema.number")]
    [Arguments("JsonSchema/draft/2019-09", "maxContains", "schema.number")]
    [Arguments("JsonSchema/draft/2019-09", "unevaluatedItems", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "unevaluatedProperties", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "contentSchema", "schema.object")]
    [Arguments("JsonSchema/draft/2020-12", "prefixItems", "schema.array")]
    [Arguments("JsonSchema/draft/2019-09", "deprecated", "schema.boolean")]
    [Arguments("JsonSchema/draft-07", "additionalItems", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "additionalItems", "schema.object")]
    public async Task SelectedDialectStillValidatesRecognizedKeywordShapes(string format, string keyword, string code)
    {
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteNull(keyword); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/" + keyword);
    }

    [Test]
    [Arguments("JsonSchema/draft-07", """{"$defs":{"ignored":{"type":"invalid","$ref":"never"}}}""")]
    [Arguments("JsonSchema/draft-07", """{"dependentSchemas":{"ignored":{"$ref":"never"}}}""")]
    [Arguments("JsonSchema/draft-07", """{"contentSchema":{"$ref":"never"},"unevaluatedItems":{"$ref":"never"},"unevaluatedProperties":{"$ref":"never"}}""")]
    [Arguments("JsonSchema/draft-07", """{"$dynamicRef":"never","$recursiveRef":"never"}""")]
    [Arguments("JsonSchema/draft/2019-09", """{"prefixItems":[{"$anchor":"ignored","$ref":"never"}],"$dynamicRef":"never"}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"additionalItems":{"$anchor":"ignored","$ref":"never"}}""")]
    public async Task UnknownDialectSubschemasNeverAcquireDependencies(string format, string schema)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema), new()
        {
            MaxReferences = 0,
            ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(4, 64, DocumentValidationStatus.Indeterminate, "limit.nodes")]
    [Arguments(5, 64, DocumentValidationStatus.Valid, "")]
    [Arguments(6, 64, DocumentValidationStatus.Valid, "")]
    [Arguments(100, 3, DocumentValidationStatus.Indeterminate, "limit.depth")]
    [Arguments(100, 4, DocumentValidationStatus.Valid, "")]
    [Arguments(100, 5, DocumentValidationStatus.Valid, "")]
    public async Task UnknownDialectContentStillConsumesWholeDocumentBudgets(
        int nodes, int depth, DocumentValidationStatus status, string code)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07",
            """{"$defs":{"ignored":{"type":"invalid","$ref":"never"}}}"""u8.ToArray(), new()
            {
                MaxNodes = nodes,
                MaxDepth = depth,
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(status);
        if (code.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code); }
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", """{"prefixItems":[{"$anchor":"ignored"}],"$ref":"#ignored"}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"additionalItems":{"$anchor":"ignored"},"$ref":"#ignored"}""")]
    public async Task AnchorsInsideUnknownDialectKeywordsAreNotRegistered(string format, string schema)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("reference.not_found");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/$ref");
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", """{"$recursiveRef":"#"}""")]
    [Arguments("JsonSchema/draft/2019-09", """{"$recursiveAnchor":true}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"$dynamicRef":"#node"}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"$dynamicAnchor":"node"}""")]
    public async Task RecognizedDynamicKeywordsRemainExplicitlyUnsupported(string format, string schema)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.dynamic_reference");
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task DeprecatedReservedDefinitionsAndDependenciesRetainMetaSchemaChecks(string format)
    {
        foreach (var schema in new[] { """{"definitions":null}""", """{"dependencies":null}""" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.object");
        }
    }

    [Test]
    [Arguments("""{"$ref":"target","type":"invalid","$id":42,"pattern":"[","$schema":false}""")]
    [Arguments("""{"$id":"https://wrong.test/","$ref":"target","properties":{"ignored":{"$ref":"never"}}}""")]
    [Arguments("""{"$ref":"target","$defs":{"ignored":{"$dynamicRef":"#never"}},"required":42}""")]
    public async Task DraftSevenReferenceObjectsIgnoreSiblingsWithoutAcquiringTheirReferences(string schema)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes(schema), new()
            {
                DocumentUri = new("https://schemas.test/root"),
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(reference == "https://schemas.test/target"
                        ? """{"type":"string"}"""u8.ToArray() : null);
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/target"])).IsTrue();
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task ModernReferenceObjectsContinueToValidateSiblingKeywords(string format)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            """{"$ref":"https://schemas.test/target","type":"invalid"}"""u8.ToArray(), new()
            {
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.type");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/type");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"$ref":"#/definitions/target","definitions":{"target":{"type":"string"},"ignored":{"type":"invalid"}}}""",
        DocumentValidationStatus.Valid, "")]
    [Arguments("""{"$ref":"#/definitions/target","definitions":{"target":{"type":"invalid"}}}""",
        DocumentValidationStatus.Invalid, "schema.type")]
    [Arguments("""{"$ref":42,"type":"string"}""", DocumentValidationStatus.Invalid, "schema.string")]
    [Arguments("""{"$ref":"missing","type":"string"}""", DocumentValidationStatus.Indeterminate, "reference.unresolved")]
    [Arguments("""{"$ref":"#","type":"invalid"}""", DocumentValidationStatus.Valid, "")]
    public async Task DraftSevenReferenceTargetsRemainCheckedEvenWhenContainingSiblingsAreIgnored(
        string schema, DocumentValidationStatus status, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(status);
        if (code.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code); }
    }

    [Test]
    [Arguments("#node")]
    [Arguments("#%6Eode")]
    [Arguments("#n%6Fde")]
    public async Task EncodedAnchorReferencesDecodeOnceBeforeChoosingTheirReferenceKind(string reference)
    {
        foreach (var format in new[] { "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12" })
        {
            var calls = 0;
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes($$$$"""
                {"$ref":"{{{{reference}}}}","$defs":{"target":{"$anchor":"node","type":"string"}}}
                """), new()
                {
                    ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
                });
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            await Assert.That(calls).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("#%2F$defs%2Ftarget", DocumentValidationStatus.Valid, "")]
    [Arguments("#/$defs/a%252Fb", DocumentValidationStatus.Valid, "")]
    [Arguments("#%256Eode", DocumentValidationStatus.Invalid, "reference.not_found")]
    [Arguments("#%C0%AF", DocumentValidationStatus.Invalid, "reference.pointer")]
    [Arguments("#%FF", DocumentValidationStatus.Invalid, "reference.pointer")]
    [Arguments("#missing", DocumentValidationStatus.Invalid, "reference.not_found")]
    public async Task ReferenceFragmentDecodingNeverDoubleDecodesOrAcceptsInvalidUtf8(
        string reference, DocumentValidationStatus status, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12",
            Encoding.UTF8.GetBytes($$$"""
                {"$ref":"{{{reference}}}","$defs":{"target":{"$anchor":"node"},"a%2Fb":true}}
                """));
        await Assert.That(result.Status).IsEqualTo(status);
        if (code.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code); }
    }

    [Test]
    [Arguments("https://schemas.test/part#%GG")]
    [Arguments("part%GG")]
    [Arguments("part\\name")]
    [Arguments("part#node#other")]
    public async Task MalformedSchemaReferencesRejectBeforeCallbackNormalization(string reference)
    {
        var calls = 0;
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("$ref", reference); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12",
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()), new()
            {
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.uri");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ExternalEncodedAnchorReferencesKeepFragmentFreeSharedAcquisition()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12", """
            {"allOf":[{"$ref":"part#%6Eode"},{"$ref":"part#node"},{"$ref":"part#%2F$defs%2Ftarget"}]}
            """u8.ToArray(), new()
            {
                DocumentUri = new("https://schemas.test/root"),
                MaxReferences = 1,
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        """{"$defs":{"target":{"$anchor":"node","type":"string"}}}"""u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/part"])).IsTrue();
    }

    [Test]
    [Arguments("""{"type":"string","minLength":2.0,"maxLength":10,"pattern":"^[a-z]+$"}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"type":["null","string"],"anyOf":[true,{"const":"x"}]}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"type":["string","string"]}""", DocumentValidationStatus.Invalid, "schema.duplicate")]
    [Arguments("""{"type":42}""", DocumentValidationStatus.Invalid, "schema.string")]
    [Arguments("""{"minimum":"0"}""", DocumentValidationStatus.Invalid, "schema.number")]
    [Arguments("""{"multipleOf":0}""", DocumentValidationStatus.Invalid, "schema.positive")]
    [Arguments("""{"minItems":-1}""", DocumentValidationStatus.Invalid, "schema.nonnegative_integer")]
    [Arguments("""{"minItems":1.5}""", DocumentValidationStatus.Invalid, "schema.nonnegative_integer")]
    [Arguments("""{"minItems":1e40}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"required":["id","id"]}""", DocumentValidationStatus.Invalid, "schema.duplicate")]
    [Arguments("""{"allOf":[]}""", DocumentValidationStatus.Invalid, "schema.empty")]
    [Arguments("""{"items":[{"type":"string"}]}""", DocumentValidationStatus.Invalid, "schema.object")]
    [Arguments("""{"prefixItems":[{"type":"string"}],"items":false}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"$vocabulary":{"https://example.test/vocabulary":true}}""", DocumentValidationStatus.Unsupported, "schema.vocabulary_unsupported")]
    [Arguments("""{"$vocabulary":{"https://example.test/vocabulary":"true"}}""", DocumentValidationStatus.Invalid, "schema.boolean")]
    [Arguments("""{"$schema":"http://json-schema.org/draft-07/schema#"}""", DocumentValidationStatus.Invalid, "schema.dialect_mismatch")]
    [Arguments("""{"$schema":"https://example.test/unknown"}""", DocumentValidationStatus.Unsupported, "schema.dialect_unsupported")]
    [Arguments("""{"$dynamicRef":"#node"}""", DocumentValidationStatus.Unsupported, "schema.dynamic_reference")]
    [Arguments("""{"pattern":"["}""", DocumentValidationStatus.Invalid, "schema.pattern")]
    [Arguments("""{"pattern":"(?<=x)y"}""", DocumentValidationStatus.Unsupported, "schema.pattern_unsupported")]
    [Arguments("""{"type":"object","unevaluatedProperties":false,"dependentRequired":{"a":["b"]}}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"examples":42}""", DocumentValidationStatus.Invalid, "schema.array")]
    [Arguments("""{"x-note":{"arbitrary":"annotation"}}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"enum":[]}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"type":"string","type":"number"}""", DocumentValidationStatus.Invalid, "json.duplicate_property")]
    [Arguments("""{"type":""", DocumentValidationStatus.Invalid, "json.syntax")]
    public async Task MetaVocabularyChecksAreNotJustJsonParsing(string schema, DocumentValidationStatus expected, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(
            "JsonSchema/draft/2020-12", Encoding.UTF8.GetBytes(schema));

        await Assert.That(result.Status).IsEqualTo(expected);
        if (code.Length > 0)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        }
    }

    [Test]
    public async Task DraftSevenTupleItemsAndEnumRulesDifferFrom2020()
    {
        var validator = new BuiltInDocumentValidator();
        var tuple = await validator.ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes("""{"items":[{"type":"string"}],"additionalItems":false}"""));
        var emptyEnum = await validator.ValidateAsync("JsonSchema/draft-07", Encoding.UTF8.GetBytes("""{"enum":[]}"""));
        var duplicateEnum = await validator.ValidateAsync("JsonSchema/draft-07", Encoding.UTF8.GetBytes("""{"enum":[1,1.0]}"""));

        await Assert.That(tuple.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(emptyEnum.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(duplicateEnum.Status).IsEqualTo(DocumentValidationStatus.Invalid);
    }

    [Test]
    public async Task ReferencesUseOnlyExplicitDocumentsAndResolveRealTargets()
    {
        var validator = new BuiltInDocumentValidator();
        var schema = Encoding.UTF8.GetBytes("""{"$ref":"https://schemas.test/age#/$defs/age"}""");
        var unresolved = await validator.ValidateAsync("JsonSchema/draft/2020-12", schema);
        var resolved = await validator.ValidateAsync("JsonSchema/draft/2020-12", schema,
            new DocumentValidationOptions
            {
                ResolveReference = (reference, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    reference == "https://schemas.test/age"
                        ? Encoding.UTF8.GetBytes("""{"$defs":{"age":{"type":"integer"}}}""")
                        : null),
            });
        var missing = await validator.ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes("""{"$ref":"#/definitions/missing","definitions":{"present":{"type":"string"}}}"""));
        var recursive = await validator.ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes("""{"type":"object","properties":{"next":{"$ref":"#"}}}"""));

        await Assert.That(unresolved.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(unresolved.Diagnostics[0].Code).IsEqualTo("reference.unresolved");
        await Assert.That(resolved.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(missing.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(missing.Diagnostics[0].Code).IsEqualTo("reference.not_found");
        await Assert.That(recursive.Status).IsEqualTo(DocumentValidationStatus.Valid);
    }

    [Test]
    [Arguments("JsonSchema/draft-07")]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("jSoNsChEmA/dRaFt/2020-12")]
    public async Task DeclaredObjectAndPropertyTypesAreChecked(string format)
    {
        var validator = new BuiltInDocumentValidator();
        var valid = await validator.ValidateAsync(format,
            Encoding.UTF8.GetBytes("""{"type":"object","properties":{"age":{"type":"integer","minimum":0}},"required":["age"]}"""));
        var invalid = await validator.ValidateAsync(format,
            Encoding.UTF8.GetBytes("""{"type":"object","properties":{"age":{"type":"int32"}}}"""));

        await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(invalid.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(invalid.Diagnostics[0].Path).IsEqualTo("$/properties/age/type");
        await Assert.That(invalid.Diagnostics[0].Code).IsEqualTo("schema.type");
    }
}
