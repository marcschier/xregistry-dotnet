// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class AvroValidationTests
{
    [Test]
    [Arguments("""{"type":"int","logicalType":"date"}""")]
    [Arguments("""{"type":"int","logicalType":"time-millis"}""")]
    [Arguments("""{"type":"long","logicalType":"time-micros"}""")]
    [Arguments("""{"type":"long","logicalType":"timestamp-millis"}""")]
    [Arguments("""{"type":"long","logicalType":"timestamp-micros"}""")]
    [Arguments("""{"type":"long","logicalType":"local-timestamp-millis"}""")]
    [Arguments("""{"type":"long","logicalType":"local-timestamp-micros"}""")]
    [Arguments("""{"type":"string","logicalType":"uuid"}""")]
    [Arguments("""{"type":"fixed","name":"Duration","size":12,"logicalType":"duration"}""")]
    [Arguments("""{"type":"fixed","name":"Duration","size":11,"logicalType":"duration"}""")]
    [Arguments("""{"type":"int","logicalType":"uuid"}""")]
    [Arguments("""{"type":"string","logicalType":"future-type","precision":{"extension":true}}""")]
    [Arguments("""{"type":"bytes","logicalType":"Decimal","precision":4,"scale":2}""")]
    [Arguments("""{"type":"string","logicalType":""}""")]
    [Arguments("""{"type":"array","items":{"type":"long","logicalType":"timestamp-micros"},"logicalType":"future-array"}""")]
    [Arguments("""{"type":"record","name":"R","logicalType":"future-record","fields":[{"name":"next","type":["null","R"],"default":null}]}""")]
    public async Task NonDecimalLogicalAnnotationsUseTheirUnderlyingWireSchemaInBothDataProfiles(string schema)
    {
        foreach (var format in new[] { "Avro/1.8.2", "Avro/1.11.0" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("""{"type":"Missing","logicalType":"future"}""", "avro.unknown_name", "$/type")]
    [Arguments("""{"type":"string","logicalType":3}""", "schema.string", "$/logicalType")]
    [Arguments("""{"type":"fixed","name":"F","size":-1,"logicalType":"duration"}""", "avro.fixed_size", "$/size")]
    [Arguments("""{"type":"array","logicalType":"future"}""", "schema.required", "$/items")]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"v","type":{"type":"int","logicalType":"date"},"default":2147483648}]}""",
        "avro.default", "$/fields/0/default")]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"v","type":{"type":"string","logicalType":"uuid"},"default":0}]}""",
        "avro.default", "$/fields/0/default")]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"v","type":{"type":"fixed","name":"D","size":12,"logicalType":"duration"},"default":"short"}]}""",
        "avro.default", "$/fields/0/default")]
    public async Task LogicalAnnotationsNeverBypassUnderlyingTypeAndDefaultChecks(string schema, string code, string path)
    {
        foreach (var format in new[] { "Avro/1.8.2", "Avro/1.11.0" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
            await Assert.That(result.Diagnostics.Single().Path).IsEqualTo(path);
        }
    }

    [Test]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""")]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":4,"scale":5}""")]
    [Arguments("""{"type":"fixed","name":"D","size":2,"logicalType":"decimal","precision":4}""")]
    public async Task DecimalAnnotationsUseUnderlyingWireSchemasWithoutConvertingValues(string schema)
    {
        foreach (var format in new[] { "Avro/1.8.2", "Avro/1.11.0" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(1000)]
    [Arguments(int.MaxValue)]
    public async Task FixedDecimalCapacityCannotReachUnreservedIntegerExpansion(int size)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Avro/1.11.0",
            Encoding.UTF8.GetBytes($$"""{"type":"fixed","name":"D","size":{{size}},"logicalType":"decimal","precision":1}"""));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("limit.work");
    }

    [Test]
    public async Task DecimalDefaultsRemainUnderlyingBytesRatherThanHostDecimalValues()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Avro/1.11.0", """
            {"type":"record","name":"R","fields":[
              {"name":"amount","type":{"type":"bytes","logicalType":"decimal","precision":1000,"scale":999},"default":"\u00ff"},
              {"name":"fixed","type":{"type":"fixed","name":"D","size":2,"logicalType":"decimal","precision":4},"default":"\u0000\u0001"}]}
            """u8.ToArray());
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(-1, DocumentValidationStatus.Indeterminate)]
    [Arguments(0, DocumentValidationStatus.Valid)]
    [Arguments(1, DocumentValidationStatus.Valid)]
    public async Task FixedDecimalIntegerReservationsHonorTheExactCumulativeWorkBoundary(
        int delta, DocumentValidationStatus status)
    {
        var bytes = """{"type":"fixed","name":"D","size":2,"logicalType":"decimal","precision":4}"""u8.ToArray();
        var result = await new BuiltInDocumentValidator().ValidateAsync("Avro/1.11.0", bytes,
            new() { MaxWork = bytes.Length + 1 + 4 + 9 + 25 + delta });
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("limit.work"); }
    }

    [Test]
    [Arguments("\"long\"", DocumentValidationStatus.Valid)]
    [Arguments("""{"type":"record","name":"events.Order","fields":[{"name":"id","type":"long"},{"name":"next","type":["null","events.Order"],"default":null}]}""", DocumentValidationStatus.Valid)]
    [Arguments("""[{"type":"record","name":"First","fields":[]},{"type":"record","name":"Second","fields":[]}]""", DocumentValidationStatus.Valid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"a","type":{"type":"array","items":"int"},"default":[1,2]},{"name":"m","type":{"type":"map","values":"boolean"},"default":{"k":true}}]}""", DocumentValidationStatus.Valid)]
    [Arguments("""{"type":"fixed","name":"Digest","size":16}""", DocumentValidationStatus.Valid)]
    [Arguments("""{"type":"enum","name":"State","symbols":["OPEN","CLOSED"],"default":"OPEN"}""", DocumentValidationStatus.Valid)]
    [Arguments("""{"type":"record","name":"9bad","fields":[]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"record","name":"R","namespace":"bad..namespace","fields":[]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"x","type":"int"},{"name":"x","type":"string"}]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"x","type":"Missing"}]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""["int","int"]""", DocumentValidationStatus.Invalid)]
    [Arguments("""["null",["int","string"]]""", DocumentValidationStatus.Invalid)]
    [Arguments("""[{"type":"array","items":"int"},{"type":"array","items":"string"}]""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"x","type":["null","string"],"default":"wrong-first-branch"}]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"x","type":"int","default":2147483647}]}""", DocumentValidationStatus.Valid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"x","type":"int","default":2147483648}]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"x","type":"long","default":9223372036854775807}]}""", DocumentValidationStatus.Valid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"x","type":"long","default":9223372036854775808}]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"enum","name":"State","symbols":["OPEN","OPEN"]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"enum","name":"State","symbols":["OPEN"],"default":"MISSING"}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"fixed","name":"Bad","size":-1}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"array"}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"map","values":42}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"record","name":"R","fields":[{"name":"x","type":"bytes","default":"\u0100"}]}""", DocumentValidationStatus.Invalid)]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":10,"scale":2}""", DocumentValidationStatus.Valid)]
    public async Task AvroSchemaGrammarNamesUnionsAndDefaultsAreChecked(string schema, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Avro/1.11.0", Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(expected);
    }

    [Test]
    public async Task Release182DoesNotInventEnumDefaultResolution()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("avro/1.8.2",
            Encoding.UTF8.GetBytes("""{"type":"record","name":"R","fields":[{"name":"id","type":"long"}]}"""));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
    }

    [Test]
    [Arguments("Avro/1.8.2")]
    [Arguments("Avro/1.11.0")]
    public async Task OlderDataSchemaProfilesRetainFirstBranchUnionDefaultValidation(string format)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, """
            {"type":"record","name":"R","fields":[
              {"name":"id","type":["null","string"],"default":"later-branch"}]}
            """u8.ToArray());
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("avro.default");
    }

    [Test]
    [Arguments("Avro/1.8.2")]
    [Arguments("Avro/1.11.0")]
    public async Task ProtocolErrorDefinitionsDoNotBecomeDataSchemaTypes(string format)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            """{"type":"error","name":"Denied","fields":[]}"""u8.ToArray());
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("avro.unknown_name");
    }
}
