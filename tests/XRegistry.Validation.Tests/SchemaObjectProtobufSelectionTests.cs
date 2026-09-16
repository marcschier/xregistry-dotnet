// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class SchemaObjectProtobufSelectionTests
{
    private const string NamedSchema = """
        syntax="proto3"; package p;
        // message Ghost {}
        message A { message Node {} }
        message B { message Node {} }
        enum State { UNKNOWN=0; }
        """;

    [Test]
    public async Task ProtobufExactGlobalMessageTakesPrecedenceOverNestedShortNames()
    {
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3", """
            syntax="proto3"; message A { message Node {} } message B { message Node {} } message Node {}
            """u8.ToArray(), "schema.proto#Node");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo("Node");
    }

    [Test]
    public async Task ProtobufExactEnumNameDoesNotFallbackToANestedMessage()
    {
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3", """
            syntax="proto3"; message A { message State {} } enum State { UNKNOWN=0; }
            """u8.ToArray(), "schema.proto#State");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_type");
        await Assert.That(result.Selection).IsNull();
    }

    [Test]
    [Arguments("p.A.Node", "p.A.Node")]
    [Arguments(".p.B.Node", "p.B.Node")]
    [Arguments("A", "p.A")]
    [Arguments("%70.A.Node", "p.A.Node")]
    public async Task ProtobufPackageAndNestedMessagesHaveFullyQualifiedIdentities(string suffix, string name)
    {
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3", Encoding.UTF8.GetBytes(NamedSchema), "events.proto#" + suffix);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo(name);
        await Assert.That(result.Selection.Kind).IsEqualTo(SchemaTypeKind.ProtobufMessage);
    }

    [Test]
    [Arguments("Node", SchemaObjectSelectionStatus.Ambiguous, "selection.ambiguous")]
    [Arguments("State", SchemaObjectSelectionStatus.Invalid, "selection.not_type")]
    [Arguments("Ghost", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("Missing", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("a", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("p..A", SchemaObjectSelectionStatus.Invalid, "selection.name")]
    [Arguments("A[]", SchemaObjectSelectionStatus.Invalid, "selection.name")]
    [Arguments("%2570.A.Node", SchemaObjectSelectionStatus.Invalid, "selection.name")]
    public async Task ProtobufNeverSelectsEnumsCommentsOrAnAmbiguousFirstMessage(
        string suffix, SchemaObjectSelectionStatus expected, string code)
    {
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3", Encoding.UTF8.GetBytes(NamedSchema), "events.proto#" + suffix);

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("events.proto")]
    [Arguments("events.proto#")]
    public async Task ProtobufRequiresAMessageSuffixEvenWithOnlyOneMessage(string schemaUri)
    {
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/2",
            """syntax="proto2"; message Only { required string value=1; }"""u8.ToArray(), schemaUri);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.name_required");
    }

    [Test]
    public async Task ProtobufTwoSelectsTheDeclaredMessage()
    {
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/2",
            """syntax="proto2"; message Only { required string value=1; }"""u8.ToArray(), "events.proto#Only");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo("Only");
    }

    [Test]
    public async Task ProtobufImportsProvideContextButAreNotContainedRootDeclarations()
    {
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3",
            """syntax="proto3"; import "dep.proto"; message Root { dep.Part value=1; }"""u8.ToArray(),
            "root.proto#dep.Part", new()
            {
                Validation = new()
                {
                    ResolveReference = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        """syntax="proto3"; package dep; message Part {}"""u8.ToArray()),
                },
            });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.NotFound);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_found");
    }

    [Test]
    public async Task ProtobufLocalSuffixUsesTheExactContextInsteadOfTheLastColon()
    {
        const string reference = "#/schemagroups/g:prod/schemas/event:proto/versions/v:1";
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3", Encoding.UTF8.GetBytes(NamedSchema),
            reference + ":p.A.Node", new() { DocumentReference = reference });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.DocumentReference).IsEqualTo(reference);
        await Assert.That(result.Selection.TypeName).IsEqualTo("p.A.Node");
    }

    [Test]
    public async Task ProtobufExplicitBaseUriClosesImportCyclesAndCoalescesRepeatedImports()
    {
        var calls = new List<string>();
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3", """
            syntax="proto3"; import "child.proto"; import "child.proto";
            message Root { Child child=1; }
            """u8.ToArray(), "https://SCHEMAS.test/root.proto#Root", new()
        {
            Validation = new()
            {
                DocumentUri = new("https://schemas.test/root.proto"),
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        reference == "https://schemas.test/child.proto"
                            ? """syntax="proto3"; import "root.proto"; message Child { Root parent=1; }"""u8.ToArray() : null);
                },
            },
        });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.DocumentReference).IsEqualTo("https://SCHEMAS.test/root.proto");
        await Assert.That(result.Selection.DocumentUri!.OriginalString).IsEqualTo("https://schemas.test/root.proto");
        await Assert.That(result.Selection.References.ContainsKey("https://schemas.test/child.proto")).IsTrue();
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("https://schemas.test/child.proto");
    }

    [Test]
    public async Task ProtobufNestedMessagePreservesPackagesImportsAndCycleContext()
    {
        const string source = """
            syntax="proto3"; package telemetry; import "dep.proto";
            message Parent {
              message Child { .dep.Part part=1; }
              Child child=1; enum State { UNKNOWN=0; }
            }
            """;
        const string dependency = """
            syntax="proto3"; package dep; import "root.proto";
            message Part { .telemetry.Parent parent=1; }
            """;
        var rootBytes = Encoding.UTF8.GetBytes(source);
        var dependencyBytes = Encoding.UTF8.GetBytes(dependency);
        var calls = new List<string>();
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3", rootBytes, "root.proto#telemetry.Parent.Child",
            new()
            {
                Validation = new()
                {
                    ResolveReference = (name, _) =>
                    {
                        calls.Add(name);
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == "dep.proto" ? dependencyBytes : null);
                    },
                },
            });
        Array.Fill(rootBytes, (byte)'!');
        Array.Fill(dependencyBytes, (byte)'!');

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.Kind).IsEqualTo(SchemaTypeKind.ProtobufMessage);
        await Assert.That(result.Selection.TypeName).IsEqualTo("telemetry.Parent.Child");
        await Assert.That(result.Selection.Json).IsNull();
        await Assert.That(result.Selection.JsonPointer).IsNull();
        await Assert.That(result.Selection.DocumentReference).IsEqualTo("root.proto");
        await Assert.That(Encoding.UTF8.GetString(result.Selection.Document.Span)).IsEqualTo(source);
        await Assert.That(result.Selection.References.Count).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetString(result.Selection.References["dep.proto"].Span)).IsEqualTo(dependency);
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("dep.proto");
    }
}
