// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class AvroValidationTests
{
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
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":10,"scale":2}""", DocumentValidationStatus.Unsupported)]
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
}
