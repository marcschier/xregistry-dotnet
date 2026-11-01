// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class SchemaObjectEmbeddedSelectionTests
{
    private const string Avro = """{"type":"record","name":"TelemetryEvent","fields":[]}""";
    private const string Proto = """syntax="proto3"; message TelemetryEvent { string value=1; }""";
    private const string Pointer = "/schemagroups/g:1/schemas/s~1one/versions/v:1/schema";
    private const string Reference = "https://example.test/registry.json#" + Pointer;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmbeddedSchemasUsePointerExtractionBeforeNamedDeclarationSelection(bool protobuf)
    {
        var schema = protobuf ? Proto : Avro;
        var container = Container(schema, protobuf);
        var result = await SchemaObjectSelector.SelectEmbeddedAsync(
            protobuf ? "Protobuf/3" : "Avro/1.11.0", container, Reference + ":TelemetryEvent",
            new() { DocumentReference = Reference });
        Array.Fill(container, (byte)'!');
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo("TelemetryEvent");
        await Assert.That(result.Selection.Kind).IsEqualTo(protobuf ? SchemaTypeKind.ProtobufMessage : SchemaTypeKind.AvroRecord);
        await Assert.That(result.Selection.DocumentReference).IsEqualTo(Reference);
        await Assert.That(result.Selection.SchemaUri).IsEqualTo(Reference + ":TelemetryEvent");
        await Assert.That(Encoding.UTF8.GetString(result.Selection.Document.Span)).IsEqualTo(schema);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DirectRegistryRootReferencesUseHashNamesRatherThanCompoundColons(bool protobuf)
    {
        var format = protobuf ? "Protobuf/3" : "Avro/1.11.0";
        var schema = Encoding.UTF8.GetBytes(protobuf ? Proto : Avro);
        const string direct = "/schemagroups/g:1/schemas/s:1";
        var selected = await SchemaObjectSelector.SelectAsync(format, schema, direct + "#TelemetryEvent");
        await Assert.That(selected.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(selected.Selection!.DocumentReference).IsEqualTo(direct);
        await Assert.That(selected.Selection.TypeName).IsEqualTo("TelemetryEvent");
        var wrong = await SchemaObjectSelector.SelectAsync(format, schema, direct + "#:TelemetryEvent");
        await Assert.That(wrong.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(wrong.Diagnostics[0].Code).IsEqualTo("selection.name");
        await Assert.That(wrong.Selection).IsNull();
    }

    [Test]
    [Arguments("", SchemaObjectSelectionStatus.Selected, null)]
    [Arguments(":TelemetryEvent", SchemaObjectSelectionStatus.Selected, null)]
    [Arguments("%3ATelemetryEvent", SchemaObjectSelectionStatus.Selected, null)]
    [Arguments(":", SchemaObjectSelectionStatus.Invalid, "selection.name")]
    [Arguments(":Missing", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments(":telemetryevent", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("%253ATelemetryEvent", SchemaObjectSelectionStatus.Invalid, "selection.document_reference_mismatch")]
    public async Task EmbeddedAvroOptionalSelectorPreservesExactSuffixAndOnePassDecoding(
        string suffix, SchemaObjectSelectionStatus status, string? diagnostic)
    {
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", Container(Avro),
            Reference + suffix, new() { DocumentReference = Reference });
        await Assert.That(result.Status).IsEqualTo(status);
        if (diagnostic is null)
        {
            await Assert.That(result.Selection!.TypeName).IsEqualTo("TelemetryEvent");
        }
        else
        {
            await Assert.That(result.Selection).IsNull();
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo(diagnostic);
        }
    }

    [Test]
    [Arguments("", "selection.name_required")]
    [Arguments(":", "selection.name")]
    [Arguments("TelemetryEvent", "selection.document_reference_mismatch")]
    public async Task EmbeddedProtobufRequiresANamedSuffixEvenForOneMessage(string suffix, string diagnostic)
    {
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Protobuf/3", Container(Proto, true),
            Reference + suffix, new() { DocumentReference = Reference });
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(diagnostic);
    }

    [Test]
    [Arguments("#/schema")]
    [Arguments("#%2Fschema")]
    [Arguments("#/a~1b")]
    [Arguments("#/a%252Fb")]
    public async Task EmbeddedPointersDecodeOnceAndRetainTheExactAcquisitionReference(string reference)
    {
        var container = Encoding.UTF8.GetBytes("""{"schema":""" + Avro + ""","a/b":""" + Avro + ""","a%2Fb":""" + Avro + "}");
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", container,
            reference + ":TelemetryEvent", new() { DocumentReference = reference });
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.DocumentReference).IsEqualTo(reference);
        await Assert.That(result.Selection.TypeName).IsEqualTo("TelemetryEvent");
        await Assert.That(result.Selection.Document.Length).IsEqualTo(Encoding.UTF8.GetByteCount(Avro));
        await Assert.That(Encoding.UTF8.GetString(result.Selection.Document.Span)).IsEqualTo(Avro);
    }

    [Test]
    [Arguments("#/absent", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("#/Schema", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("#/items/01", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("#/items/-", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("#/bad~2", SchemaObjectSelectionStatus.Invalid, "selection.pointer")]
    [Arguments("#anchor", SchemaObjectSelectionStatus.Invalid, "selection.pointer")]
    [Arguments("#/%GG", SchemaObjectSelectionStatus.Invalid, "selection.uri")]
    [Arguments("#/%C3%28", SchemaObjectSelectionStatus.Invalid, "selection.uri")]
    public async Task EmbeddedPointerFailuresCannotSelectAnotherSchema(
        string reference, SchemaObjectSelectionStatus status, string code)
    {
        var bytes = Encoding.UTF8.GetBytes("""{"schema":""" + Avro + ""","items":[""" + Avro + "]}");
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", bytes,
            reference + ":TelemetryEvent", new() { DocumentReference = reference });
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        await Assert.That(result.Selection).IsNull();
    }

    [Test]
    [Arguments("Avro/1.11.0", "\"text\"")]
    [Arguments("Avro/1.11.0", "null")]
    [Arguments("Avro/1.11.0", "[]")]
    [Arguments("Protobuf/3", "{}")]
    [Arguments("Protobuf/3", "null")]
    public async Task EmbeddedSchemaRepresentationMustMatchTheDeclaredFormat(string format, string value)
    {
        var result = await SchemaObjectSelector.SelectEmbeddedAsync(format,
            Encoding.UTF8.GetBytes("""{"schema":""" + value + "}"), "#/schema:TelemetryEvent",
            new() { DocumentReference = "#/schema" });
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_schema");
        await Assert.That(result.Selection).IsNull();
    }

    [Test]
    [Arguments("{", "json.syntax")]
    [Arguments("""{"schema":null,"schema":null}""", "json.duplicate_property")]
    public async Task EmbeddedSelectionValidatesTheCompleteContainer(string json, string code)
    {
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", Encoding.UTF8.GetBytes(json),
            "#/schema:TelemetryEvent", new() { DocumentReference = "#/schema" });
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        await Assert.That(result.Selection).IsNull();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task EmbeddedInputByteLimitsApplyToTheWholeContainer(int delta)
    {
        var container = Container(Avro);
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", container,
            Reference + ":TelemetryEvent", new()
            {
                DocumentReference = Reference,
                Validation = new() { MaxDocumentBytes = container.Length + delta }
            });
        await Assert.That(result.Status).IsEqualTo(delta < 0
            ? SchemaObjectSelectionStatus.Indeterminate : SchemaObjectSelectionStatus.Selected);
        if (delta < 0) { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.bytes"); }
    }

    [Test]
    public async Task EmbeddedExtractionAndTypeParsingDoNotResetTotalBudgets()
    {
        var container = Container(Avro);
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", container,
            Reference + ":TelemetryEvent", new()
            {
                DocumentReference = Reference,
                Validation = new() { MaxTotalBytes = container.Length }
            });
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.total_bytes");
        await Assert.That(result.Selection).IsNull();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task EmbeddedTotalByteLimitIncludesContainerAndExtractedSchema(int delta)
    {
        var container = Container(Avro);
        var total = container.Length + Encoding.UTF8.GetByteCount(Avro);
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", container,
            Reference + ":TelemetryEvent", new()
            {
                DocumentReference = Reference,
                Validation = new() { MaxTotalBytes = total + delta }
            });
        await Assert.That(result.Status).IsEqualTo(delta < 0
            ? SchemaObjectSelectionStatus.Indeterminate : SchemaObjectSelectionStatus.Selected);
        if (delta < 0) { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.total_bytes"); }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task EmbeddedNodeLimitsIncludeBothParsingStages(int delta)
    {
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", Container(Avro),
            Reference + ":TelemetryEvent", new()
            {
                DocumentReference = Reference,
                Validation = new() { MaxNodes = 15 + delta }
            });
        await Assert.That(result.Status).IsEqualTo(delta < 0
            ? SchemaObjectSelectionStatus.Indeterminate : SchemaObjectSelectionStatus.Selected);
        if (delta < 0) { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.nodes"); }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task EmbeddedPointerStepLimitHasAnInclusiveBoundary(int delta)
    {
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", Container(Avro),
            Reference + ":TelemetryEvent", new()
            {
                DocumentReference = Reference, MaxSelectorSegments = 7 + delta
            });
        await Assert.That(result.Status).IsEqualTo(delta < 0
            ? SchemaObjectSelectionStatus.Indeterminate : SchemaObjectSelectionStatus.Selected);
        if (delta < 0) { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.selector"); }
    }

    [Test]
    public async Task EmbeddedWorkAndDepthLimitsCannotBeBypassedByASmallSchema()
    {
        var container = Container(Avro);
        var work = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", container,
            Reference + ":TelemetryEvent", new()
            {
                DocumentReference = Reference, Validation = new() { MaxWork = container.Length }
            });
        await Assert.That(work.Status).IsEqualTo(SchemaObjectSelectionStatus.Indeterminate);
        await Assert.That(work.Diagnostics[0].Code).IsEqualTo("limit.work");
        var depth = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", container,
            Reference + ":TelemetryEvent", new()
            {
                DocumentReference = Reference, Validation = new() { MaxDepth = 2 }
            });
        await Assert.That(depth.Status).IsEqualTo(SchemaObjectSelectionStatus.Indeterminate);
        await Assert.That(depth.Diagnostics[0].Code).IsEqualTo("limit.depth");
    }

    [Test]
    public async Task EmbeddedProtobufUsesOnlyTheExplicitDependencyResolver()
    {
        const string schema = """syntax="proto3"; import "part.proto"; message TelemetryEvent { Part part=1; }""";
        var calls = new List<string>();
        var container = Container(schema, true);
        var missing = await SchemaObjectSelector.SelectEmbeddedAsync("Protobuf/3", container,
            Reference + ":TelemetryEvent", new() { DocumentReference = Reference });
        await Assert.That(missing.Status).IsEqualTo(SchemaObjectSelectionStatus.Indeterminate);
        await Assert.That(missing.Selection).IsNull();
        var result = await SchemaObjectSelector.SelectEmbeddedAsync("Protobuf/3", container,
            Reference + ":TelemetryEvent", new()
            {
                DocumentReference = Reference,
                Validation = new()
                {
                    ResolveReference = (name, _) =>
                    {
                        calls.Add(name);
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                            """syntax="proto3"; message Part {}"""u8.ToArray());
                    }
                }
            });
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo("TelemetryEvent");
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("part.proto");
        await Assert.That(result.Selection.References.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EmbeddedSelectionRequiresExplicitPointerContextAndPropagatesCancellation()
    {
        await Assert.That(async () => await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0",
            Container(Avro), "schema.avsc#TelemetryEvent", new() { DocumentReference = "schema.avsc" }))
            .Throws<ArgumentException>();
        await Assert.That(async () => await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0",
            Container(Avro), Reference + ":TelemetryEvent", new() { DocumentReference = Reference },
            new CancellationToken(true))).Throws<OperationCanceledException>();
        var mismatch = await SchemaObjectSelector.SelectEmbeddedAsync("Avro/1.11.0", Container(Avro),
            "https://other.test/registry.json#" + Pointer + ":TelemetryEvent",
            new() { DocumentReference = Reference });
        await Assert.That(mismatch.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(mismatch.Diagnostics[0].Code).IsEqualTo("selection.document_reference_mismatch");
        await Assert.That(mismatch.Selection).IsNull();
    }

    private static byte[] Container(string schema, bool protobuf = false) =>
        Encoding.UTF8.GetBytes("""{"schemagroups":{"g:1":{"schemas":{"s/one":{"versions":{"v:1":{"schema":"""
            + (protobuf ? "\"" + JsonEncodedText.Encode(schema).ToString() + "\"" : schema) + "}}}}}}}");
}
