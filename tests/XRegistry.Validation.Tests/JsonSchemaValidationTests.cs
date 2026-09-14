using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class JsonSchemaValidationTests
{
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
