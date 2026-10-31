// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class ProtobufValidationTests
{
    [Test]
    [Arguments("""syntax = "proto3"; package telemetry; message Sample { int64 id = 1; optional string name = 2; repeated double values = 3 [packed = true]; }""", DocumentValidationStatus.Valid)]
    [Arguments("""syntax="proto3"; message M { message Child { string name=1; } Child child=1; map<string,Child> children=2; oneof value { string text=3; int32 count=4; } reserved 5 to 8, 10; reserved "old"; }""", DocumentValidationStatus.Valid)]
    [Arguments("""syntax="proto3"; enum State { UNKNOWN=0; OPEN=1; } message M { State state=1; } service S { rpc Get(M) returns (stream M); }""", DocumentValidationStatus.Valid)]
    [Arguments("""
        syntax="proto3"; // comment
        message M { string value=1; /* block */ }
        """, DocumentValidationStatus.Valid)]
    [Arguments("""syntax="proto3"; message M { nonsense tokens; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { int32 x=1; int32 y=1; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { int32 x=1; int32 x=2; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { int32 x=0; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { int32 x=19000; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { int32 x=536870911; }""", DocumentValidationStatus.Valid)]
    [Arguments("""syntax="proto3"; message M { int32 x=536870912; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { required int32 x=1; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { map<float,string> values=1; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { oneof x { repeated int32 y=1; } }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { reserved 2 to 4; int32 x=3; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { reserved "x"; string x=1; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; enum E { X=1; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; enum E { X=0; Y=0; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; enum E { option allow_alias=true; X=0; Y=0; }""", DocumentValidationStatus.Valid)]
    [Arguments("""syntax="proto3"; message M { Missing x=1; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { string x=1 [default="x"]; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { string x=1 [packed=true]; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; message M { int32 x=1; } }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; import other.proto; message M {}""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto3"; import "other.proto"; message M {}""", DocumentValidationStatus.Indeterminate)]
    [Arguments("""syntax="proto3"; option (custom.flag)=true; message M {}""", DocumentValidationStatus.Unsupported)]
    [Arguments("""syntax="proto3"; /* unterminated""", DocumentValidationStatus.Invalid)]
    public async Task LexerDeclarationsAndFieldRulesAreChecked(string schema, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""syntax="proto2"; message M { required int32 id=1; optional string name=2 [default="anon"]; }""", DocumentValidationStatus.Valid)]
    [Arguments("""message M { optional int32 count=1 [default=2147483647]; }""", DocumentValidationStatus.Valid)]
    [Arguments("""syntax="proto2"; message M { int32 id=1; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto2"; message M { optional int32 id=1 [default=2147483648]; }""", DocumentValidationStatus.Invalid)]
    [Arguments("""syntax="proto2"; message M { extensions 100 to 200; }""", DocumentValidationStatus.Unsupported)]
    [Arguments("""syntax="proto3"; message M {}""", DocumentValidationStatus.Invalid)]
    public async Task ProtoTwoLabelsDefaultsAndSyntaxDiscriminatorAreChecked(string schema, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2", Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(expected);
    }

    [Test]
    [Arguments("optional string foo=1; message foo {}")]
    [Arguments("message foo {} optional string foo=1;")]
    [Arguments("optional string foo=1; enum foo { ZERO=0; }")]
    [Arguments("enum foo { ZERO=0; } optional string foo=1;")]
    [Arguments("optional string foo=1; enum E { foo=0; }")]
    [Arguments("enum E { foo=0; } optional string foo=1;")]
    [Arguments("oneof foo { string bar=1; } message foo {}")]
    [Arguments("message foo {} oneof foo { string bar=1; }")]
    [Arguments("oneof foo { string bar=1; } enum foo { ZERO=0; }")]
    [Arguments("enum foo { ZERO=0; } oneof foo { string bar=1; }")]
    [Arguments("oneof foo { string bar=1; } enum E { foo=0; }")]
    [Arguments("enum E { foo=0; } oneof foo { string bar=1; }")]
    [Arguments("message foo {} enum E { foo=0; }")]
    [Arguments("enum E { foo=0; } message foo {}")]
    [Arguments("enum E { foo=0; } enum Other { foo=0; }")]
    [Arguments("oneof first { string foo=1; } enum E { foo=0; }")]
    [Arguments("map<string,string> foo=1; message foo {}")]
    [Arguments("enum E { foo=0; } map<string,string> foo=1;")]
    [Arguments("enum E { option allow_alias=true; foo=0; bar=0; } enum Other { foo=0; }")]
    public async Task MessageSymbolsRejectCrossKindNameCollisionsInBothSyntaxProfiles(string body)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var schema = $"syntax=\"proto{syntax}\"; message M {{ {body} }}";
            var result = await new BuiltInDocumentValidator().ValidateAsync(
                $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
        }
    }

    [Test]
    [Arguments("service S {} service S {}")]
    [Arguments("service S {} message S {}")]
    [Arguments("message S {} service S {}")]
    [Arguments("service S {} enum S { ZERO=0; }")]
    [Arguments("enum S { ZERO=0; } service S {}")]
    [Arguments("enum E { S=0; } service S {}")]
    [Arguments("service S {} enum E { S=0; }")]
    public async Task PackageSymbolsRejectServiceTypeAndEnumValueNameCollisions(string declarations)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; package example; {declarations}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
        }
    }

    [Test]
    public async Task SymbolsPreserveContainingScopeCaseAndMessageTypeResolution()
    {
        const string schema = """
            syntax="proto3"; package example;
            message First { message Value {} Value value=1; enum E { ZERO=0; } }
            message Second { message Value {} Value value=1; enum E { ZERO=0; } }
            service S { rpc Get(First) returns (Second); }
            service s { rpc Get(Second) returns (First); }
            """;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("message M { S value=1; }")]
    [Arguments("message M {} service Other { rpc Get(S) returns (M); }")]
    public async Task ServiceSymbolsDoNotBindAsMessageTypes(string declarations)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax=\"proto3\"; service S {{}} {declarations}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type");
    }

    [Test]
    [Arguments("example", DocumentValidationStatus.Invalid)]
    [Arguments("other", DocumentValidationStatus.Valid)]
    public async Task ImportedSymbolsUseTheDeclaredPackageScope(string importedPackage, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            """syntax="proto3"; package example; import "other.proto"; message M {} service S {}"""u8.ToArray(),
            new DocumentValidationOptions
            {
                ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == "other.proto"
                    ? Encoding.UTF8.GetBytes($"syntax=\"proto3\"; package {importedPackage}; service S {{}}") : null)
            });
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Invalid)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
        }
        else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
    }

    [Test]
    [Arguments(false, false, DocumentValidationStatus.Invalid)]
    [Arguments(false, true, DocumentValidationStatus.Valid)]
    [Arguments(true, false, DocumentValidationStatus.Valid)]
    [Arguments(true, true, DocumentValidationStatus.Valid)]
    public async Task ImportVisibilityRequiresDirectImportsOrPublicForwarding(
        bool publicImport, bool directImport, DocumentValidationStatus expected)
    {
        foreach (var declaration in new[]
        {
            "message Root { .leaf.Child child=1; }",
            "message Root { .leaf.State state=1; }",
            "message Root { map<string,.leaf.Child> values=1; }",
            "message Root { oneof value { .leaf.Child child=1; } }",
            "message Root {} service S { rpc Get(.leaf.Child) returns (Root); }",
            "message Root {} service S { rpc Get(Root) returns (.leaf.Child); }"
        })
        {
            var requests = new List<string>();
            var schema = "syntax=\"proto3\"; import \"bridge.proto\"; " +
                (directImport ? "import \"leaf.proto\"; " : "") + declaration;
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", Encoding.UTF8.GetBytes(schema),
                new DocumentValidationOptions
                {
                    ResolveReference = (name, _) =>
                    {
                        requests.Add(name);
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(name switch
                        {
                            "bridge.proto" => "syntax=\"proto3\"; import " +
                                (publicImport ? "public " : "") +
                                "\"leaf.proto\"; message Bridge { .leaf.Child child=1; }",
                            "leaf.proto" => "syntax=\"proto3\"; package leaf; message Child {} enum State { ZERO=0; }",
                            _ => throw new InvalidOperationException("Unexpected import: " + name)
                        }));
                    }
                });
            await Assert.That(result.Status).IsEqualTo(expected);
            await Assert.That(requests.Count).IsEqualTo(2);
            await Assert.That(requests.Count(name => name == "bridge.proto")).IsEqualTo(1);
            await Assert.That(requests.Count(name => name == "leaf.proto")).IsEqualTo(1);
            if (expected == DocumentValidationStatus.Invalid)
            {
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type");
            }
            else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        }
    }

    [Test]
    [Arguments(false, false, DocumentValidationStatus.Invalid)]
    [Arguments(false, true, DocumentValidationStatus.Invalid)]
    [Arguments(true, false, DocumentValidationStatus.Invalid)]
    [Arguments(true, true, DocumentValidationStatus.Valid)]
    public async Task PublicImportVisibilityStopsAtPrivateEdges(
        bool firstPublic, bool secondPublic, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            """syntax="proto3"; import "bridge.proto"; message Root { Leaf value=1; }"""u8.ToArray(),
            new DocumentValidationOptions
            {
                ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    Encoding.UTF8.GetBytes(name switch
                    {
                        "bridge.proto" => $"syntax=\"proto3\"; import {(firstPublic ? "public " : "")}\"middle.proto\";",
                        "middle.proto" => $"syntax=\"proto3\"; import {(secondPublic ? "public " : "")}\"leaf.proto\";",
                        "leaf.proto" => "syntax=\"proto3\"; message Leaf {}",
                        _ => throw new InvalidOperationException("Unexpected import: " + name)
                    }))
            });
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Invalid)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type");
        }
        else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
    }

    [Test]
    [Arguments(false, DocumentValidationStatus.Invalid)]
    [Arguments(true, DocumentValidationStatus.Valid)]
    public async Task SiblingImportsDoNotGrantEachOtherVisibility(bool importsSibling, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            """syntax="proto3"; import "left.proto"; import "right.proto"; message Root { Left value=1; }"""u8.ToArray(),
            new DocumentValidationOptions
            {
                ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    Encoding.UTF8.GetBytes(name switch
                    {
                        "left.proto" => "syntax=\"proto3\"; " +
                            (importsSibling ? "import \"right.proto\"; " : "") + "message Left { Right value=1; }",
                        "right.proto" => "syntax=\"proto3\"; message Right {}",
                        _ => throw new InvalidOperationException("Unexpected import: " + name)
                    }))
            });
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Invalid)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type");
        }
        else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
    }

    [Test]
    [Arguments(false, SchemaObjectSelectionStatus.Invalid)]
    [Arguments(true, SchemaObjectSelectionStatus.Selected)]
    public async Task SelectionAppliesImportVisibilityAfterResolvingDependencyDocumentUris(
        bool publicImport, SchemaObjectSelectionStatus expected)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var requests = new List<string>();
            var result = await SchemaObjectSelector.SelectAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; import \"nested/bridge.proto\"; " +
                    "message Root { optional Leaf value=1; }"), "https://schemas.test/root.proto#Root",
                new()
                {
                    Validation = new()
                    {
                        DocumentUri = new("https://schemas.test/root.proto"),
                        ResolveReference = (name, _) =>
                        {
                            requests.Add(name);
                            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(name switch
                            {
                                "https://schemas.test/nested/bridge.proto" =>
                                    $"syntax=\"proto{syntax}\"; import {(publicImport ? "public " : "")}\"leaf.proto\";",
                                "https://schemas.test/nested/leaf.proto" => $"syntax=\"proto{syntax}\"; message Leaf {{}}",
                                _ => throw new InvalidOperationException("Unexpected import: " + name)
                            }));
                        }
                    }
                });
            await Assert.That(result.Status).IsEqualTo(expected);
            await Assert.That(requests.Count).IsEqualTo(2);
            await Assert.That(requests[0]).IsEqualTo("https://schemas.test/nested/bridge.proto");
            await Assert.That(requests[1]).IsEqualTo("https://schemas.test/nested/leaf.proto");
            if (expected == SchemaObjectSelectionStatus.Selected)
            {
                await Assert.That(result.Selection!.TypeName).IsEqualTo("Root");
                await Assert.That(result.Selection.References.Count).IsEqualTo(2);
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
            else
            {
                await Assert.That(result.Selection).IsNull();
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type");
            }
        }
    }

    [Test]
    [Arguments(2, 2, DocumentValidationStatus.Valid)]
    [Arguments(2, 3, DocumentValidationStatus.Valid)]
    [Arguments(3, 2, DocumentValidationStatus.Invalid)]
    [Arguments(3, 3, DocumentValidationStatus.Valid)]
    public async Task ImportedEnumSyntaxIsCheckedForEveryFieldForm(
        int referringSyntax, int declaringSyntax, DocumentValidationStatus expected)
    {
        foreach (var field in new[]
        {
            "optional .dep.State state=1;",
            "repeated .dep.State states=1 [packed=true];",
            "map<string,.dep.State> states=1;",
            "oneof choice { .dep.State state=1; }"
        })
        {
            var requests = 0;
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{referringSyntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{referringSyntax}\"; import \"dep.proto\"; message Root {{ {field} }}"),
                new DocumentValidationOptions
                {
                    ResolveReference = (name, _) =>
                    {
                        requests++;
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == "dep.proto"
                            ? Encoding.UTF8.GetBytes($"syntax=\"proto{declaringSyntax}\"; package dep; enum State {{ ZERO=0; }}")
                            : null);
                    }
                });
            await Assert.That(result.Status).IsEqualTo(expected);
            await Assert.That(requests).IsEqualTo(1);
            if (expected == DocumentValidationStatus.Invalid)
            {
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.enum_syntax");
            }
            else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        }
    }

    [Test]
    [Arguments(2, 3)]
    [Arguments(3, 2)]
    public async Task ImportedMessagesMayContainEnumsFromTheirOwnSyntax(int referringSyntax, int declaringSyntax)
    {
        var result = await SchemaObjectSelector.SelectAsync($"Protobuf/{referringSyntax}",
            Encoding.UTF8.GetBytes($"syntax=\"proto{referringSyntax}\"; import \"dep.proto\"; " +
                "message Root { optional .dep.Child child=1; } service S { rpc Get(.dep.Child) returns (Root); }"),
            "root.proto#Root", new()
            {
                Validation = new()
                {
                    ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == "dep.proto"
                        ? Encoding.UTF8.GetBytes($"syntax=\"proto{declaringSyntax}\"; package dep; " +
                            "enum State { ZERO=0; } message Child { optional State state=1; }") : null)
                }
            });
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo("Root");
        await Assert.That(result.Selection.References.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PubliclyForwardedEnumsRetainExplicitOrImplicitProtoTwoSyntax(bool explicitSyntax)
    {
        var result = await SchemaObjectSelector.SelectAsync("Protobuf/3",
            """syntax="proto3"; import "bridge.proto"; message Root { .dep.State state=1; }"""u8.ToArray(),
            "root.proto#Root", new()
            {
                Validation = new()
                {
                    ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        Encoding.UTF8.GetBytes(name switch
                        {
                            "bridge.proto" => "syntax=\"proto3\"; import public \"dep.proto\";",
                            "dep.proto" => (explicitSyntax ? "syntax=\"proto2\"; " : "") +
                                "package dep; enum State { ZERO=0; }",
                            _ => throw new InvalidOperationException("Unexpected import: " + name)
                        }))
                }
            });
        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.enum_syntax");
    }

    [Test]
    [Arguments("""option java_package="com." 'example'; message Root {}""")]
    [Arguments("""option java_package="" 'example' ""; message Root {}""")]
    [Arguments("""message Root { optional string value=1 [json_name="my" 'Value']; }""")]
    [Arguments("""message Root { reserved "old" 'Name'; optional string value=1; }""")]
    public async Task AdjacentStringLiteralsAreAcceptedInBothSyntaxProfiles(string declarations)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {declarations}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AdjacentImportLiteralsResolveTheCombinedAuthoredPathOnce()
    {
        var requests = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            """
            syntax="proto3"; import "child" /* concatenation */ '.proto';
            message Root { Child value=1; }
            """u8.ToArray(), new DocumentValidationOptions
            {
                ResolveReference = (name, _) =>
                {
                    requests.Add(name);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == "child.proto"
                        ? """syntax="proto3"; message Child {}"""u8.ToArray() : null);
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0]).IsEqualTo("child.proto");
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AdjacentReservedNameLiteralsRejectTheCombinedFieldName()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            """syntax="proto3"; message Root { reserved "old" 'Name'; string oldName=1; }"""u8.ToArray());
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.reserved");
    }

    [Test]
    [Arguments("""syntax="proto3"; import "." "./child.proto"; message Root {}""", "protobuf.import")]
    [Arguments("""syntax="proto3"; message Root { reserved "old" 'Name', "oldName"; }""", "protobuf.reserved")]
    [Arguments("""syntax="proto3"; option java_package=+"a" 'b'; message Root {}""", "protobuf.constant")]
    public async Task AdjacentStringLiteralsKeepCombinedValueConstraints(string source, string code)
    {
        var requests = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", Encoding.UTF8.GetBytes(source),
            new DocumentValidationOptions
            {
                ResolveReference = (_, _) =>
                {
                    requests++;
                    throw new InvalidOperationException("Invalid import must fail before acquisition.");
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        await Assert.That(requests).IsEqualTo(0);
    }

    [Test]
    public async Task ProtoTwoStringDefaultsAcceptAdjacentLiterals()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            """syntax="proto2"; message Root { optional string value=1 [default="a" 'b']; }"""u8.ToArray());
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""syntax="proto" '3'; message Root {}""")]
    [Arguments("""syntax="proto3" ''; message Root {}""")]
    public async Task SyntaxDiscriminatorDoesNotAcceptAdjacentLiterals(string source)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", Encoding.UTF8.GetBytes(source));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.syntax");
    }

    [Test]
    public async Task SuppliedImportBindsTheDeclaredMessageNotArbitraryText()
    {
        var source = Encoding.UTF8.GetBytes("""syntax="proto3"; import "child.proto"; message Parent { Child child=1; }""");
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", source,
            new DocumentValidationOptions
            {
                ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    name == "child.proto" ? Encoding.UTF8.GetBytes("""syntax="proto3"; message Child { string name=1; }""") : null),
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
    }
}
