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
