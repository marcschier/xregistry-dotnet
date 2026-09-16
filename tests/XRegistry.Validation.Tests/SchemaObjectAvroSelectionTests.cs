// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class SchemaObjectAvroSelectionTests
{
    private const string NamedSchema = """
        {"type":"record","name":"Envelope","namespace":"root","fields":[
          {"name":"a","type":{"type":"record","name":"a.Child","fields":[]}},
          {"name":"b","type":{"type":"record","name":"b.Child","fields":[]}},
          {"name":"state","type":{"type":"enum","name":"a.State","symbols":["OFF"]}},
          {"name":"blob","type":{"type":"fixed","name":"a.Blob","size":2}}]}
        """;

    [Test]
    public async Task AvroExactGlobalFullNameTakesPrecedenceOverAmbiguousShortNames()
    {
        var result = await SchemaObjectSelector.SelectAsync("Avro/1.11.0", """
            {"type":"record","name":"Root","fields":[
              {"name":"a","type":{"type":"record","name":"a.Child","fields":[]}},
              {"name":"b","type":{"type":"record","name":"b.Child","fields":[]}},
              {"name":"global","type":{"type":"record","name":"Child","fields":[]}}]}
            """u8.ToArray(), "schema.avsc#Child");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo("Child");
        await Assert.That(result.Selection.JsonPointer).IsEqualTo("/fields/2/type");
    }

    [Test]
    public async Task AvroExactEnumNameDoesNotFallbackToANamespacedRecord()
    {
        var result = await SchemaObjectSelector.SelectAsync("Avro/1.11.0", """
            {"type":"record","name":"Root","fields":[
              {"name":"r","type":{"type":"record","name":"ns.State","fields":[]}},
              {"name":"e","type":{"type":"enum","name":"State","symbols":["OFF"]}}]}
            """u8.ToArray(), "schema.avsc#State");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_type");
        await Assert.That(result.Selection).IsNull();
    }

    [Test]
    [Arguments("", "root.Envelope", "")]
    [Arguments("#", "root.Envelope", "")]
    [Arguments("#a.Child", "a.Child", "/fields/0/type")]
    [Arguments("#b.Child", "b.Child", "/fields/1/type")]
    [Arguments("#Envelope", "root.Envelope", "")]
    public async Task AvroOptionalRootAndQualifiedRecordsHaveLiteralIdentities(string suffix, string name, string expectedPointer)
    {
        var result = await SchemaObjectSelector.SelectAsync("Avro/1.8.2", Encoding.UTF8.GetBytes(NamedSchema), "events.avsc" + suffix);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo(name);
        await Assert.That(result.Selection.JsonPointer).IsEqualTo(expectedPointer);
        await Assert.That(result.Selection.Json!.Value.GetProperty("type").GetString()).IsEqualTo("record");
    }

    [Test]
    [Arguments("Child", SchemaObjectSelectionStatus.Ambiguous, "selection.ambiguous")]
    [Arguments("a.State", SchemaObjectSelectionStatus.Invalid, "selection.not_type")]
    [Arguments("Blob", SchemaObjectSelectionStatus.Invalid, "selection.not_type")]
    [Arguments("string", SchemaObjectSelectionStatus.Invalid, "selection.not_type")]
    [Arguments("Absent", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("child", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("a..Child", SchemaObjectSelectionStatus.Invalid, "selection.name")]
    [Arguments(".a.Child", SchemaObjectSelectionStatus.Invalid, "selection.name")]
    [Arguments("a.Child:other", SchemaObjectSelectionStatus.Invalid, "selection.name")]
    public async Task AvroSelectionDistinguishesAmbiguousWrongKindMissingAndInvalidNames(
        string name, SchemaObjectSelectionStatus expected, string code)
    {
        var result = await SchemaObjectSelector.SelectAsync("Avro/1.11.0", Encoding.UTF8.GetBytes(NamedSchema), "events.avsc#" + name);

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("""{"type":"string"}""")]
    [Arguments("\"int\"")]
    [Arguments("""{"type":"enum","name":"State","symbols":["OFF"]}""")]
    [Arguments("""{"type":"fixed","name":"Blob","size":1}""")]
    [Arguments("""{"type":"array","items":{"type":"record","name":"Nested","fields":[]}}""")]
    public async Task AvroWithoutSuffixNeverFallsBackToANestedRecord(string schema)
    {
        var result = await SchemaObjectSelector.SelectAsync("Avro/1.11.0", Encoding.UTF8.GetBytes(schema), "events.avsc");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_type");
        await Assert.That(result.Selection).IsNull();
    }

    [Test]
    [Arguments("", "root.Envelope")]
    [Arguments(":a.Child", "a.Child")]
    [Arguments("%3Aa.Child", "a.Child")]
    public async Task AvroLocalFragmentSuffixDoesNotConsumeColonContainingIds(string suffix, string name)
    {
        const string reference = "#/schemagroups/g:prod/schemas/event:avro/versions/v:1";
        var result = await SchemaObjectSelector.SelectAsync("Avro/1.11.0", Encoding.UTF8.GetBytes(NamedSchema),
            reference + suffix, new() { DocumentReference = reference });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.DocumentReference).IsEqualTo(reference);
        await Assert.That(result.Selection.TypeName).IsEqualTo(name);
    }

    [Test]
    public async Task AvroExplicitEmptyLocalSuffixIsNotAnOmittedSuffix()
    {
        const string reference = "#/schemagroups/g/schemas/event";
        var result = await SchemaObjectSelector.SelectAsync("Avro/1.11.0", Encoding.UTF8.GetBytes(NamedSchema),
            reference + ":", new() { DocumentReference = reference });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.name");
    }

    [Test]
    public async Task AvroNestedRecordUsesItsFullNameAndRetainsRecursiveDocumentContext()
    {
        const string schema = """
            {"type":"record","name":"Envelope","namespace":"telemetry","fields":[
              {"name":"reading","type":{"type":"record","name":"Reading","fields":[
                {"name":"value","type":"double"},
                {"name":"parent","type":["null","Envelope"],"default":null}]}}]}
            """;
        var result = await SchemaObjectSelector.SelectAsync("Avro/1.11.0", Encoding.UTF8.GetBytes(schema),
            "https://schemas.test/events.avsc#telemetry.Reading");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.Kind).IsEqualTo(SchemaTypeKind.AvroRecord);
        await Assert.That(result.Selection.TypeName).IsEqualTo("telemetry.Reading");
        await Assert.That(result.Selection.JsonPointer).IsEqualTo("/fields/0/type");
        await Assert.That(result.Selection.Json!.Value.GetProperty("name").GetString()).IsEqualTo("Reading");
        await Assert.That(result.Selection.Json.Value.GetProperty("fields")[1].GetProperty("type")[1].GetString())
            .IsEqualTo("Envelope");
        await Assert.That(Encoding.UTF8.GetString(result.Selection.Document.Span)).IsEqualTo(schema);
    }
}
