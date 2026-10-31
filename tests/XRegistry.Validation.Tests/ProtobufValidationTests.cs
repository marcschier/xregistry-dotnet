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
    [Arguments("")]
    [Arguments(";")]
    [Arguments(";;;")]
    [Arguments("; /* before */ option deprecated=true; ; // after\n ;")]
    [Arguments("option deprecated=false; ;")]
    [Arguments("; option deprecated=false;")]
    public async Task RpcMethodBodiesAcceptEmptyStatements(string body)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var input in new[] { "M", "stream M" })
            {
                foreach (var output in new[] { "M", "stream M" })
                {
                    var schema = $"syntax=\"proto{syntax}\"; message M {{}} service S {{ " +
                        $"rpc Get({input}) returns ({output}) {{ {body} }} ; rpc Other(M) returns (M); }}";
                    var result = await new BuiltInDocumentValidator().ValidateAsync(
                        $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(schema));
                    await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                    await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
                }
            }
        }
    }

    [Test]
    [Arguments("; nonsense; ;", "M", "", DocumentValidationStatus.Invalid, "protobuf.syntax")]
    [Arguments("; message Nested {} ;", "M", "", DocumentValidationStatus.Invalid, "protobuf.syntax")]
    [Arguments("; int32 value=1; ;", "M", "", DocumentValidationStatus.Invalid, "protobuf.syntax")]
    [Arguments("\";\"", "M", "", DocumentValidationStatus.Invalid, "protobuf.syntax")]
    [Arguments("; option deprecated=true ;", "Missing", "", DocumentValidationStatus.Invalid, "protobuf.type")]
    [Arguments(";;", "int32", "", DocumentValidationStatus.Invalid, "protobuf.type")]
    [Arguments(";;", "M", "rpc Get(M) returns (M);", DocumentValidationStatus.Invalid, "protobuf.duplicate")]
    [Arguments("; option deprecated=\"true\"; ;", "M", "", DocumentValidationStatus.Invalid, "protobuf.option")]
    [Arguments("; option java_package=\"example\"; ;", "M", "", DocumentValidationStatus.Invalid, "protobuf.option_scope")]
    [Arguments("; option unknown=true; ;", "M", "", DocumentValidationStatus.Unsupported, "protobuf.unsupported")]
    [Arguments("; option (custom.flag)=true; ;", "M", "", DocumentValidationStatus.Unsupported, "protobuf.unsupported")]
    public async Task RpcEmptyStatementsPreserveContentTypeAndOptionChecks(
        string body, string input, string nextMethod, DocumentValidationStatus expected, string code)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var schema = $"syntax=\"proto{syntax}\"; message M {{}} service S {{ " +
                $"rpc Get({input}) returns (M) {{ {body} }} {nextMethod} }}";
            var result = await new BuiltInDocumentValidator().ValidateAsync(
                $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(expected);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        }
    }

    [Test]
    public async Task RpcEmptyStatementsRespectTheCumulativeWorkBudget()
    {
        var schema = Encoding.UTF8.GetBytes(
            "syntax=\"proto3\"; message M {} service S { rpc Get(M) returns (M) {" + new string(';', 256) + "} }");
        var validator = new BuiltInDocumentValidator();
        var limited = await validator.ValidateAsync("Protobuf/3", schema,
            new DocumentValidationOptions { MaxWork = 500 });
        await Assert.That(limited.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(limited.Diagnostics[0].Code).IsEqualTo("limit.work");
        var sufficient = await validator.ValidateAsync("Protobuf/3", schema,
            new DocumentValidationOptions { MaxWork = 10_000 });
        await Assert.That(sufficient.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(sufficient.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("$ M {}", "message")]
    [Arguments("$ E { ZERO=0; }", "enum")]
    [Arguments("$ S {}", "service")]
    [Arguments("$ example; message M {}", "package")]
    [Arguments("$ deprecated=true; message M {}", "option")]
    [Arguments("message M { $ int32 value=1; }", "optional")]
    [Arguments("message M { $ int32 value=1; }", "repeated")]
    [Arguments("message M { optional int32 value $ 1; }", "=")]
    [Arguments("message M { optional int32 value=1 $ }", ";")]
    [Arguments("message M { optional int32 value=1 $ deprecated=true]; }", "[")]
    [Arguments("message M { optional int32 value=1 [deprecated=true $ ; }", "]")]
    [Arguments("message M { optional int32 value=1 [deprecated $ true]; }", "=")]
    [Arguments("message M { optional int32 value=1 [deprecated=true $ lazy=false]; }", ",")]
    [Arguments("message M { $ Choice { ZERO=0; } }", "enum")]
    [Arguments("message M { $ Nested {} }", "message")]
    [Arguments("message M { $ choice { string value=1; } }", "oneof")]
    [Arguments("message M { $ <string,string> values=1; }", "map")]
    [Arguments("message M { map $ string,string> values=1; }", "<")]
    [Arguments("message M { map<string $ string> values=1; }", ",")]
    [Arguments("message M { map<string,string $ values=1; }", ">")]
    [Arguments("message M { $ 1; }", "reserved")]
    [Arguments("message M { reserved 1 $ 3; }", "to")]
    [Arguments("message M { reserved 1 to $ ; }", "max")]
    [Arguments("message M { reserved 1 $ 3; }", ",")]
    [Arguments("enum E { ZERO $ 0; }", "=")]
    [Arguments("enum E { ZERO=0 $ }", ";")]
    [Arguments("enum E { ZERO=0; $ deprecated=true; }", "option")]
    [Arguments("enum E { ZERO=0; ONE= $ 1; }", "-")]
    [Arguments("enum E { ZERO=0; ONE= $ 1; }", "+")]
    [Arguments("message M {} service S { $ Get(M) returns (M); }", "rpc")]
    [Arguments("message M {} service S { rpc Get $ M) returns (M); }", "(")]
    [Arguments("message M {} service S { rpc Get(M $ returns (M); }", ")")]
    [Arguments("message M {} service S { rpc Get(M) $ (M); }", "returns")]
    [Arguments("message M {} service S { rpc Get($ M) returns (M); }", "stream")]
    [Arguments("message M {} service S { rpc Get(M) returns ($ M); }", "stream")]
    [Arguments("message M {} service S { rpc Get(M) returns (M) $ }", ";")]
    [Arguments("message M {} service S { rpc Get($ M) returns (M); }", ".")]
    [Arguments("message M {} service S { $ deprecated=true; }", "option")]
    [Arguments("message M {} service S { rpc Get(M) returns (M) { $ deprecated=true; } }", "option")]
    [Arguments("message M {} $", ";")]
    public async Task QuotedGrammarTokensNeverActAsKeywordsOrSeparators(string template, string token)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var prefix = $"syntax=\"proto{syntax}\"; ";
            var validator = new BuiltInDocumentValidator();
            var valid = await validator.ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes(prefix + template.Replace("$", token, StringComparison.Ordinal)));
            await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(valid.Diagnostics.Count).IsEqualTo(0);
            foreach (var quote in new[] { '"', '\'' })
            {
                var invalid = await validator.ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes(prefix + template.Replace("$", $"{quote}{token}{quote}", StringComparison.Ordinal)));
                await Assert.That(invalid.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            }
        }
    }

    [Test]
    [Arguments("\"syntax\"=\"proto3\"; message M {}")]
    [Arguments("syntax \"=\" \"proto3\"; message M {}")]
    [Arguments("syntax=\"proto3\" \";\" message M {}")]
    [Arguments("syntax=\"proto3\"; message M \"{\" \"}\"")]
    [Arguments("syntax=\"proto3\"; message M {} service S \"{\" \"}\"")]
    [Arguments("syntax=\"proto3\"; enum E \"{\" ZERO=0; \"}\"")]
    [Arguments("syntax=\"proto3\"; message M {} service S { rpc Get(M) returns (M) \"{\" \"}\" }")]
    [Arguments("syntax=\"proto3\"; message M { oneof choice \"{\" string value=1; \"}\" }")]
    [Arguments("syntax=\"proto3\"; option \"(\" custom.flag)=true; message M {}")]
    [Arguments("syntax=\"proto3\"; message M { string value=1 [\"(\" custom.flag)=true]; }")]
    public async Task QuotedSyntaxDelimitersAndCustomOptionMarkersAreInvalid(string schema)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
    }

    [Test]
    public async Task QuotedRequiredLabelNeverActsAsAProtoTwoFieldLabel()
    {
        var validator = new BuiltInDocumentValidator();
        var valid = await validator.ValidateAsync("Protobuf/2",
            """syntax="proto2"; message M { required int32 value=1; }"""u8.ToArray());
        await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
        foreach (var quote in new[] { '"', '\'' })
        {
            var invalid = await validator.ValidateAsync("Protobuf/2", Encoding.UTF8.GetBytes(
                $"syntax=\"proto2\"; message M {{ {quote}required{quote} int32 value=1; }}"));
            await Assert.That(invalid.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(invalid.Diagnostics[0].Code).IsEqualTo("protobuf.label");
        }
    }

    [Test]
    [Arguments("import")]
    [Arguments("public")]
    [Arguments("weak")]
    public async Task ImportKeywordTokenKindsPreserveLiteralAcquisitionPaths(string keyword)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var calls = 0;
            string? acquired = null;
            var options = new DocumentValidationOptions
            {
                ResolveReference = (name, _) =>
                {
                    calls++;
                    acquired = name;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                        $"syntax=\"proto{syntax}\"; message Imported {{}}"));
                }
            };
            var import = keyword == "import" ? "import \"other.proto\";" : $"import {keyword} \"other.proto\";";
            var validator = new BuiltInDocumentValidator();
            var valid = await validator.ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {import} message M {{}}"), options);
            await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(acquired).IsEqualTo("other.proto");
            calls = 0;
            acquired = null;
            var quoted = await validator.ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {import.Replace(keyword, $"\"{keyword}\"", StringComparison.Ordinal)} message M {{}}"),
                options);
            await Assert.That(quoted.Status).IsEqualTo(keyword == "import"
                ? DocumentValidationStatus.Invalid : DocumentValidationStatus.Valid);
            await Assert.That(calls).IsEqualTo(keyword == "import" ? 0 : 1);
            await Assert.That(acquired).IsEqualTo(keyword == "import" ? null : keyword + "other.proto");
        }
    }

    [Test]
    [Arguments(";")]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("(")]
    [Arguments(".")]
    [Arguments("-")]
    [Arguments("message")]
    [Arguments("optional")]
    [Arguments("stream")]
    [Arguments("option")]
    public async Task GrammarSpellingsRemainValidInsideStringValues(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var defaultOption = syntax == 2 ? $", default=\"{value}\"" : "";
            var schema = $"syntax=\"proto{syntax}\"; option java_package=\"{value}\"; " +
                $"message M {{ optional string value=1 [json_name=\"{value}\"{defaultOption}]; }}";
            var result = await new BuiltInDocumentValidator().ValidateAsync(
                $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task PackagePositionDoesNotChangeDeclarationScopeOrMessageSelection(int position)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var declarations = new List<string>
            {
                "message Root { optional .example.Leaf value=1; optional .example.State state=2; }",
                "message Leaf {}",
                "enum State { ZERO=0; }",
                "service S { rpc Get(.example.Root) returns (.example.Leaf); }"
            };
            declarations.Insert(position, "package example;");
            var schema = Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; " + string.Join(" ", declarations));
            var validated = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}", schema);
            await Assert.That(validated.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(validated.Diagnostics.Count).IsEqualTo(0);
            var selected = await SchemaObjectSelector.SelectAsync($"Protobuf/{syntax}", schema,
                "https://schemas.test/root.proto#example.Root");
            await Assert.That(selected.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
            await Assert.That(selected.Selection!.TypeName).IsEqualTo("example.Root");
            await Assert.That(selected.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("message Leaf {}", DocumentValidationStatus.Valid)]
    [Arguments("message Root {}", DocumentValidationStatus.Invalid)]
    [Arguments("service Root {}", DocumentValidationStatus.Invalid)]
    [Arguments("enum E { Root=0; }", DocumentValidationStatus.Invalid)]
    public async Task LateImportedPackageControlsTypeBindingAndCrossFileSymbols(
        string imported, DocumentValidationStatus expected)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var lateImportPackage in new[] { false, true })
            {
                var calls = 0;
                var schema = $"syntax=\"proto{syntax}\"; import \"other.proto\"; " +
                    "message Root { optional .example.Leaf value=1; } package example;";
                var importedBody = lateImportPackage ? imported + " package example;" : "package example; " + imported;
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes(schema),
                    new DocumentValidationOptions
                    {
                        ResolveReference = (name, _) =>
                        {
                            calls++;
                            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                                $"syntax=\"proto{syntax}\"; {importedBody}"));
                        }
                    });
                await Assert.That(calls).IsEqualTo(1);
                await Assert.That(result.Status).IsEqualTo(expected);
                if (expected == DocumentValidationStatus.Invalid)
                {
                    await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
                }
                else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
            }
        }
    }

    [Test]
    [Arguments("package first; message M {} package second;", "protobuf.package")]
    [Arguments("message M {} package same; package same;", "protobuf.package")]
    [Arguments("message M {} package .example;", "protobuf.identifier")]
    [Arguments("message M {} package example.;", "protobuf.identifier")]
    [Arguments("message M {} package \"example\";", "protobuf.identifier")]
    public async Task PackageDeclarationsRetainFullIdentifierAndUniquenessRules(string body, string code)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {body}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        }
    }

    [Test]
    public async Task PackageDiscoveryIgnoresNestedNamesAndStringContents()
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var schema = Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; " + """
                option java_package="package wrong; { }";
                message package {
                    message Nested { optional string package=1 [json_name="; package wrong;"]; }
                    optional Nested value=1;
                }
                service S { rpc package(.example.package) returns (.example.package); }
                package example;
                """);
            var selected = await SchemaObjectSelector.SelectAsync($"Protobuf/{syntax}", schema,
                "https://schemas.test/root.proto#example.package");
            await Assert.That(selected.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
            await Assert.That(selected.Selection!.TypeName).IsEqualTo("example.package");
            await Assert.That(selected.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task PackageDiscoveryUsesTheFullDottedNameAndFiniteWorkBudget()
    {
        var schema = Encoding.UTF8.GetBytes("syntax=\"proto3\";" + new string(';', 256) +
            "message Root { optional .example.nested.Leaf value=1; } message Leaf {} package example.nested;");
        var limited = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", schema,
            new DocumentValidationOptions { MaxWork = 500 });
        await Assert.That(limited.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(limited.Diagnostics[0].Code).IsEqualTo("limit.work");
        var selected = await SchemaObjectSelector.SelectAsync("Protobuf/3", schema,
            "https://schemas.test/root.proto#example.nested.Root",
            new() { Validation = new() { MaxWork = 10_000 } });
        await Assert.That(selected.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(selected.Selection!.TypeName).IsEqualTo("example.nested.Root");
        await Assert.That(selected.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("double")]
    [Arguments("float")]
    [Arguments("int32")]
    [Arguments("int64")]
    [Arguments("uint32")]
    [Arguments("uint64")]
    [Arguments("sint32")]
    [Arguments("sint64")]
    [Arguments("fixed32")]
    [Arguments("fixed64")]
    [Arguments("sfixed32")]
    [Arguments("sfixed64")]
    [Arguments("bool")]
    [Arguments("State")]
    public async Task ExplicitPackedOptionsAcceptRepeatedPackableTypes(string type)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var option in new[] { "", " [packed=true]", " [packed=false]" })
            {
                var schema = $"syntax=\"proto{syntax}\"; enum State {{ ZERO=0; }} " +
                    $"message M {{ repeated {type} values=1{option}; }}";
                var result = await new BuiltInDocumentValidator().ValidateAsync(
                    $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(schema));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
        }
    }

    [Test]
    [Arguments("optional int32 value=1")]
    [Arguments("optional bool value=1")]
    [Arguments("optional State value=1")]
    [Arguments("optional string value=1")]
    [Arguments("optional bytes value=1")]
    [Arguments("optional Child value=1")]
    [Arguments("repeated string value=1")]
    [Arguments("repeated bytes value=1")]
    [Arguments("repeated Child value=1")]
    [Arguments("map<int32,int32> value=1")]
    [Arguments("map<string,State> value=1")]
    [Arguments("map<string,Child> value=1")]
    public async Task ExplicitPackedOptionsRejectNonPackableFieldsEvenWhenFalse(string field)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var option in new[] { "", " [packed=true]", " [packed=false]" })
            {
                var schema = $"syntax=\"proto{syntax}\"; enum State {{ ZERO=0; }} message Child {{}} " +
                    $"message M {{ {field}{option}; }}";
                var result = await new BuiltInDocumentValidator().ValidateAsync(
                    $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(schema));
                await Assert.That(result.Status).IsEqualTo(option.Length == 0
                    ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
                if (option.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
                else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.packed"); }
            }
        }
    }

    [Test]
    [Arguments("int32")]
    [Arguments("State")]
    [Arguments("string")]
    [Arguments("Child")]
    public async Task ExplicitPackedOptionsRejectOneofFieldsEvenWhenFalse(string type)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var option in new[] { "", " [packed=true]", " [packed=false]" })
            {
                var schema = $"syntax=\"proto{syntax}\"; enum State {{ ZERO=0; }} message Child {{}} " +
                    $"message M {{ oneof choice {{ {type} value=1{option}; }} }}";
                var result = await new BuiltInDocumentValidator().ValidateAsync(
                    $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(schema));
                await Assert.That(result.Status).IsEqualTo(option.Length == 0
                    ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
                if (option.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
                else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.packed"); }
            }
        }
    }

    [Test]
    [Arguments("true")]
    [Arguments("false")]
    public async Task ExplicitPackedOptionsRetainImportedEnumAndMessageTypeChecks(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var type in new[] { "dep.State", "dep.Child" })
            {
                var calls = 0;
                var schema = $"syntax=\"proto{syntax}\"; import \"types.proto\"; " +
                    $"message Root {{ repeated {type} values=1 [packed={value}]; }}";
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes(schema), new DocumentValidationOptions
                    {
                        ResolveReference = (name, _) =>
                        {
                            calls++;
                            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                                $"syntax=\"proto{syntax}\"; package dep; enum State {{ ZERO=0; }} message Child {{}}"));
                        }
                    });
                await Assert.That(calls).IsEqualTo(1);
                await Assert.That(result.Status).IsEqualTo(type == "dep.State"
                    ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
                if (type == "dep.State") { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
                else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.packed"); }
            }
        }
    }

    [Test]
    [Arguments("packed=\"false\"")]
    [Arguments("packed=\"fa\" 'lse'")]
    [Arguments("packed=false, packed=true")]
    [Arguments("packed=true, packed=false")]
    [Arguments("packed=false, packed=false")]
    public async Task ExplicitPackedOptionsKeepBooleanKindsAndDuplicateChecks(string options)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; message M {{ repeated int32 values=1 [{options}]; }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
        }
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
    [Arguments("""option java_multiple_files="true"; message Root {}""")]
    [Arguments("""option optimize_for="SPEED"; message Root {}""")]
    [Arguments("""message Root { option deprecated="false"; }""")]
    [Arguments("""message Root { repeated int32 value=1 [packed="true"]; }""")]
    [Arguments("""message Root { optional int32 value=1 [deprecated="true"]; }""")]
    [Arguments("""message Root { optional int32 value=1 [lazy="false"]; }""")]
    [Arguments("""message Root { optional int32 value=1 [weak="false"]; }""")]
    [Arguments("""enum State { option allow_alias="true"; ZERO=0; }""")]
    [Arguments("""enum State { option deprecated="false"; ZERO=0; }""")]
    [Arguments("""enum State { ZERO=0 [deprecated="true"]; }""")]
    [Arguments("""message Root {} service S { option deprecated="true"; rpc Get(Root) returns (Root); }""")]
    [Arguments("""message Root {} service S { rpc Get(Root) returns (Root) { option deprecated="true"; } }""")]
    public async Task QuotedOptionValuesNeverMasqueradeAsBooleansOrEnumConstants(string declarations)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {declarations}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
            var native = declarations.Replace("\"true\"", "true", StringComparison.Ordinal)
                .Replace("\"false\"", "false", StringComparison.Ordinal)
                .Replace("\"SPEED\"", "SPEED", StringComparison.Ordinal);
            var valid = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {native}"));
            await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(valid.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("bool", "\"true\"")]
    [Arguments("bool", "\"false\"")]
    [Arguments("double", "\"1.5\"")]
    [Arguments("float", "\"inf\"")]
    [Arguments("float", "\"nan\"")]
    [Arguments("State", "\"ZERO\"")]
    [Arguments("bool", "\"tr\" 'ue'")]
    [Arguments("double", "\"1.\" '5'")]
    [Arguments("State", "\"ZE\" 'RO'")]
    public async Task QuotedDefaultsNeverMasqueradeAsScalarOrEnumConstants(string type, string value)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax=\"proto2\"; enum State {{ ZERO=0; }} " +
                $"message Root {{ optional {type} value=1 [default={value}]; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.default");
    }

    [Test]
    [Arguments("bool", "true")]
    [Arguments("bool", "false")]
    [Arguments("double", "1.5")]
    [Arguments("float", "inf")]
    [Arguments("float", "-inf")]
    [Arguments("float", "nan")]
    [Arguments("State", "ZERO")]
    [Arguments("string", "\"true\"")]
    [Arguments("bytes", "\"ZERO\"")]
    public async Task TypedDefaultsRetainValidNativeConstants(string type, string value)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax=\"proto2\"; enum State {{ ZERO=0; }} " +
                $"message Root {{ optional {type} value=1 [default={value}]; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("reserved 1 to 5, 5 to 9;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 5 to 9; reserved 1 to 5;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 1 to 9, 3 to 5;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 3 to 5, 1 to 9;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 4; reserved 4;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 1 to 5; reserved 3;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 3; reserved 1 to 5;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 1 to max; reserved 2;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 1 to 4, 5 to 9;", DocumentValidationStatus.Valid)]
    [Arguments("reserved 5 to 9; reserved 1 to 4;", DocumentValidationStatus.Valid)]
    [Arguments("reserved 1; reserved 2;", DocumentValidationStatus.Valid)]
    public async Task ReservedRangesRejectOverlapWithoutRejectingAdjacentIntervals(
        string reservations, DocumentValidationStatus expected)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var declaration in new[]
            {
                $"message Root {{ {reservations} }}",
                $"enum State {{ ZERO=0; {reservations} }}"
            })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {declaration}"));
                await Assert.That(result.Status).IsEqualTo(expected);
                if (expected == DocumentValidationStatus.Invalid)
                {
                    await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.reserved");
                }
                else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
            }
        }
    }

    [Test]
    [Arguments("reserved -2147483648 to -1; reserved -1;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved 2147483647; reserved 2147483647 to max;", DocumentValidationStatus.Invalid)]
    [Arguments("reserved -2147483648 to -2; reserved -1; reserved 2147483647;", DocumentValidationStatus.Valid)]
    public async Task EnumReservedRangesCheckBothSignedBoundaries(
        string reservations, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax=\"proto3\"; enum State {{ ZERO=0; {reservations} }}"));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Invalid)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.reserved");
        }
        else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
    }

    [Test]
    [Arguments(20000, DocumentValidationStatus.Indeterminate)]
    [Arguments(100000, DocumentValidationStatus.Valid)]
    public async Task ReservedRangeComparisonsRespectTheCumulativeWorkBudget(
        long workLimit, DocumentValidationStatus expected)
    {
        var ranges = string.Join(",", Enumerable.Range(1, 256).Select(static number =>
            number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax=\"proto3\"; message Root {{ reserved {ranges}; }}"),
            new DocumentValidationOptions { MaxWork = workLimit });
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.work");
        }
        else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
    }

    [Test]
    [Arguments("java_package=\"example\"")]
    [Arguments("java_outer_classname=\"Root\"")]
    [Arguments("go_package=\"example\"")]
    [Arguments("csharp_namespace=\"Example\"")]
    [Arguments("objc_class_prefix=\"ABC\"")]
    [Arguments("php_namespace=\"Example\"")]
    [Arguments("php_metadata_namespace=\"Example\"")]
    [Arguments("ruby_package=\"Example\"")]
    [Arguments("java_multiple_files=true")]
    [Arguments("cc_generic_services=false")]
    [Arguments("java_generic_services=false")]
    [Arguments("py_generic_services=false")]
    [Arguments("cc_enable_arenas=true")]
    [Arguments("optimize_for=SPEED")]
    public async Task FileOptionsRejectNonFilePlacement(string assignment)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var statement = $"option {assignment};";
            var valid = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {statement} message Root {{}}"));
            await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(valid.Diagnostics.Count).IsEqualTo(0);
            foreach (var declaration in new[]
            {
                $"message Root {{ {statement} }}",
                $"message Root {{ oneof choice {{ {statement} string value=1; }} }}",
                $"message Root {{}} service S {{ {statement} rpc Get(Root) returns (Root); }}",
                $"message Root {{}} service S {{ rpc Get(Root) returns (Root) {{ {statement} }} }}"
            })
            {
                var invalid = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {declaration}"));
                await Assert.That(invalid.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(invalid.Diagnostics[0].Code).IsEqualTo("protobuf.option_scope");
            }
        }
    }

    [Test]
    [Arguments("true")]
    [Arguments("false")]
    public async Task DeprecatedOptionUsesOnlyItsDeclaredTargets(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var statement = $"option deprecated={value};";
            foreach (var declaration in new[]
            {
                $"{statement} message Root {{}}",
                $"message Root {{ {statement} }}",
                $"message Root {{}} service S {{ {statement} rpc Get(Root) returns (Root); }}",
                $"message Root {{}} service S {{ rpc Get(Root) returns (Root) {{ {statement} }} }}"
            })
            {
                var valid = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; {declaration}"));
                await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
                await Assert.That(valid.Diagnostics.Count).IsEqualTo(0);
            }

            var invalid = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; message Root {{ oneof choice {{ " +
                    $"{statement} string value=1; }} }}"));
            await Assert.That(invalid.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(invalid.Diagnostics[0].Code).IsEqualTo("protobuf.option_scope");
        }
    }

    [Test]
    [Arguments("future_option=true")]
    [Arguments("(custom.flag)=true")]
    public async Task UnknownAndCustomOptionsKeepTheirUnsupportedPolicyAtEveryTarget(string assignment)
    {
        var statement = $"option {assignment};";
        foreach (var declaration in new[]
        {
            $"{statement} message Root {{}}",
            $"message Root {{ {statement} }}",
            $"message Root {{ oneof choice {{ {statement} string value=1; }} }}",
            $"message Root {{}} service S {{ {statement} rpc Get(Root) returns (Root); }}",
            $"message Root {{}} service S {{ rpc Get(Root) returns (Root) {{ {statement} }} }}"
        })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
                Encoding.UTF8.GetBytes($"syntax=\"proto3\"; {declaration}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.unsupported");
        }
    }

    [Test]
    [Arguments(".5")]
    [Arguments("-.5")]
    [Arguments("+.5")]
    [Arguments(".5e2")]
    [Arguments(".5E-2")]
    [Arguments("-.5e+2")]
    [Arguments(".0")]
    [Arguments("1.")]
    [Arguments("1.e2")]
    public async Task FloatingDefaultsAcceptLeadingDecimalAndExponentForms(string value)
    {
        foreach (var type in new[] { "float", "double" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
                Encoding.UTF8.GetBytes($"syntax=\"proto2\"; message Root {{ optional {type} value=1 [default={value}]; }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(".")]
    [Arguments(".e2")]
    [Arguments(".5e")]
    [Arguments(".5e+")]
    [Arguments(".5.2")]
    [Arguments(".5f")]
    [Arguments(".5 value")]
    public async Task FloatingDefaultsRejectMalformedDecimalNeighbors(string value)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax=\"proto2\"; message Root {{ optional double value=1 [default={value}]; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("""syntax="proto3"; message Root { int32 value=.5; }""")]
    [Arguments("""syntax="proto3"; enum State { ZERO=.0; }""")]
    [Arguments("""syntax="proto3"; message Root { reserved .1; }""")]
    [Arguments("""syntax="proto2"; message Root { optional int32 value=1 [default=.5]; }""")]
    public async Task LeadingDecimalTokensNeverSatisfyIntegerContexts(string source)
    {
        var syntax = source.Contains("proto2", StringComparison.Ordinal) ? 2 : 3;
        var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}", Encoding.UTF8.GetBytes(source));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.integer");
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
