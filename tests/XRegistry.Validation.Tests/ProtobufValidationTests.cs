// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class ProtobufValidationTests
{
    [Test]
    [Arguments("string foo_bar=1; string fooBar=2;")]
    [Arguments("string first=1 [json_name='fooBar']; string foo_bar=2;")]
    [Arguments("string foo_bar=1; oneof choice {string fooBar=2;}")]
    [Arguments("map<string,int32> foo_bar=1; string fooBar=2;")]
    public async Task Proto3DerivedJsonNamesMustBeUnique(string fields)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("syntax='proto3'; message Root{" + fields + "}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.json_name");
    }

    [Test]
    public async Task JsonNameOverridesDisambiguateDerivedNamesWithoutChangingOtherScopes()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("""
                syntax='proto3';
                message Root{
                  string foo_bar=1 [json_name='first'];
                  string fooBar=2;
                  message Child{string foo_bar=1;}
                }
                message Other{string foo_bar=1;}
                """));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
    }

    [Test]
    [Arguments("optional string foo_bar=1; optional string fooBar=2;")]
    [Arguments("optional string first=1 [json_name='fooBar']; optional string foo_bar=2;")]
    public async Task Proto2DerivedJsonNameConflictsRetainBestEffortPolicy(string fields)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes("syntax='proto2'; message Root{" + fields + "}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
    }

    [Test]
    public async Task GrpcImportedProto3RetainsDerivedJsonNameChecks()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'child.proto'; message Row{} service S{rpc Get(Row)returns(Row);}"),
            new()
            {
                ResolveReference = (reference, _) =>
                {
                    if (reference != "child.proto") { throw new InvalidOperationException("An unexpected dependency was requested."); }
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                        "syntax='proto3'; message Child{string foo_bar=1;string fooBar=2;}"));
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.json_name");
    }

    [Test]
    [Arguments("a_b_c", "aBC")]
    [Arguments("foo__bar", "fooBar")]
    [Arguments("_foo_bar", "FooBar")]
    [Arguments("foo_bar_", "fooBar")]
    public async Task Proto3DerivedJsonNamesHandleUnderscoreBoundaries(string fieldName, string jsonName)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("syntax='proto3'; message Root{string " + fieldName +
                "=1; string second=2 [json_name='" + jsonName + "'];}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.json_name");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LegacyJsonConflictOptionDoesNotSilentlyDisableValidation(int syntax)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/" + syntax,
            Encoding.UTF8.GetBytes("syntax='proto" + syntax + "'; message Root{" +
                "option deprecated_legacy_json_field_conflicts=true; optional string foo_bar=1; optional string fooBar=2;}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.unsupported");
    }

    [Test]
    [Arguments("foo", "FooEntry")]
    [Arguments("map_field", "MapFieldEntry")]
    public async Task MapEntryNamesReserveSymbolsInTheirMessageScope(string mapName, string entryName)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var prefix = "syntax='proto" + syntax + "'; ";
            var label = syntax == 2 ? "optional " : "";
            var map = "map<string,int32> " + mapName + "=1;";
            foreach (var collision in new[]
            {
                "message " + entryName + "{}",
                "enum " + entryName + "{ ZERO=0; }",
                label + "string " + entryName + "=2;",
                "oneof " + entryName + "{ string value=2; }"
            })
            {
                foreach (var before in new[] { false, true })
                {
                    var body = before ? collision + map : map + collision;
                    var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/" + syntax,
                        Encoding.UTF8.GetBytes(prefix + "message Root{" + body + "}"));
                    await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                    await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
                }
            }
        }
    }

    [Test]
    public async Task MapEntryNamesDoNotReserveSymbolsInOtherScopes()
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/" + syntax,
                Encoding.UTF8.GetBytes("syntax='proto" + syntax + "'; message FooEntry{} " +
                    "message Root{ map<string,int32> foo=1; message Child{message FooEntry{} } } " +
                    "message Other{map<string,int32> foo=1;}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        }
    }

    [Test]
    [Arguments("foo", "Foo")]
    [Arguments("map_field", "MapField")]
    public async Task MapsCannotShareTheSameSyntheticEntryName(string first, string second)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/" + syntax,
                Encoding.UTF8.GetBytes("syntax='proto" + syntax + "'; message Root{" +
                    "map<string,int32> " + first + "=1; map<string,int32> " + second + "=2;}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
        }
    }

    [Test]
    public async Task GrpcSourceRejectsMapEntrySymbolCollisions()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("syntax='proto3'; message Row{map<string,int32> foo=1;message FooEntry{}} " +
                "service S{rpc Get(Row)returns(Row);}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
    }

    [Test]
    public async Task MapEntryCollisionInImportedSourceCannotBeHiddenByValidRootTypes()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'child.proto'; message Row{} service S{rpc Get(Row)returns(Row);}"),
            new()
            {
                ResolveReference = (reference, _) =>
                {
                    if (reference != "child.proto") { throw new InvalidOperationException("An unexpected dependency was requested."); }
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                        "syntax='proto3'; message Child{map<string,int32> foo=1;message FooEntry{}}"));
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RpcCompoundTypeLookupHonorsServiceMethodOwnership(bool absolute, bool ownerFirst)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var type = absolute ? ".p.Owner.Child" : "Owner.Child";
            var owner = "rpc Owner(Row) returns(Row);";
            var selected = "rpc Get(" + type + ") returns(" + type + ");";
            var source = "syntax='proto" + syntax + "'; package p; message Row {} " +
                "message Owner { message Child {} } service S { " +
                (ownerFirst ? owner + selected : selected + owner) + " }";
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/" + syntax,
                Encoding.UTF8.GetBytes(source));
            await Assert.That(result.Status).IsEqualTo(absolute ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
            if (!absolute)
            {
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type");
            }
        }
    }

    [Test]
    public async Task RpcMethodNamesAreScopedToTheirOwnService()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; package p; message Row {} message Owner { message Child {} }
                service First { rpc Owner(Row) returns(Row); }
                service Second { rpc Get(Owner.Child) returns(Owner.Child); }
                """));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("has space.proto")]
    [Arguments("bad%.proto")]
    public async Task GrpcDocumentValidationChecksImportSpellingBeforeResolvingAgainstABase(string import)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("syntax='proto3'; import '" + import + "'; service S {}"),
            new DocumentValidationOptions
            {
                DocumentUri = new Uri("https://example.test/root.proto"),
                ResolveReference = (_, _) =>
                {
                    calls++;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("dependency.uri");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GrpcDocumentValidationResolvesImportsAgainstEachSuppliedDocumentBase(bool withBase)
    {
        var calls = new List<string>();
        var bridge = withBase ? "https://example.test/apis/sub/bridge.proto" : "sub/bridge.proto";
        var types = withBase ? "https://example.test/apis/sub/types.proto" : "types.proto";
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; import 'sub/bridge.proto'; service S { rpc Get(p.Row) returns(p.Row); }
                """), new DocumentValidationOptions
            {
                DocumentUri = withBase ? new Uri("https://example.test/apis/root.proto") : null,
                ResolveReference = (name, _) =>
                {
                    calls.Add(name);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == bridge
                        ? Encoding.UTF8.GetBytes("syntax='proto3'; import public 'types.proto';")
                        : name == types ? Encoding.UTF8.GetBytes("syntax='proto3'; package p; message Row {}")
                        : null);
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual([bridge, types])).IsTrue();
    }

    [Test]
    public async Task GrpcDocumentValidationDoesNotRefetchItsConfiguredRootUri()
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'root.proto'; service S {}"),
            new DocumentValidationOptions
            {
                DocumentUri = new Uri("https://example.test/apis/root.proto"),
                ResolveReference = (_, _) =>
                {
                    calls++;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("dependency.cycle");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GrpcDocumentValidationDetectsDirectAndTransitiveImportCycles(bool transitive)
    {
        var calls = new List<string>();
        var root = Encoding.UTF8.GetBytes("syntax='proto3'; import '" +
            (transitive ? "child.proto" : "root.proto") + "'; service S {}");
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3", root,
            new DocumentValidationOptions
            {
                ResolveReference = (name, _) =>
                {
                    calls.Add(name);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == "root.proto" ? root :
                        Encoding.UTF8.GetBytes("syntax='proto3'; import 'root.proto';"));
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("dependency.cycle");
        await Assert.That(result.Diagnostics[0].Path).IsEqualTo("$/import");
        await Assert.That(calls.SequenceEqual(transitive ? ["child.proto"] : Array.Empty<string>())).IsTrue();
    }

    [Test]
    public async Task GrpcDocumentValidationAllowsSharedAcyclicDependencies()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; import 'bridge.proto'; import 'types.proto'; service S {}
                """), new DocumentValidationOptions
            {
                ResolveReference = (name, _) =>
                {
                    calls.Add(name);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                        name == "bridge.proto" ? "syntax='proto3'; import 'types.proto';" :
                        "syntax='proto3'; message Row {}"));
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["bridge.proto", "types.proto"])).IsTrue();
    }

    [Test]
    [Arguments("optional string first=1 [json_name='key']; optional string second=2 [json_name='key'];")]
    [Arguments("optional string first=1 [json_name='k' 'ey']; oneof choice { string second=2 [json_name='key']; }")]
    [Arguments("oneof first { string a=1 [json_name='key']; } oneof second { string b=2 [json_name='key']; }")]
    [Arguments("map<string,int32> first=1 [json_name='key']; optional string second=2 [json_name='key'];")]
    [Arguments("optional string first=1 [json_name='']; repeated string second=2 [json_name=''];")]
    [Arguments("optional string first=1 [json_name='\\u006bey']; optional string second=2 [json_name='key'];")]
    public async Task ExplicitJsonNamesMustBeUniqueAcrossMessageFields(string fields)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Root {{ {fields} }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.json_name");
        }
    }

    [Test]
    public async Task ExplicitJsonNameUniquenessPreservesCaseAndIndependentMessages()
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Root {{ " +
                    "optional string first=1 [json_name='key']; optional string second=2 [json_name='Key']; " +
                    "message Child { optional string value=1 [json_name='key']; } } " +
                    "message Other { optional string value=1 [json_name='key']; }"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ExplicitJsonNameCollisionsInImportedMessagesCannotBeIgnored()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            "syntax='proto3'; import 'dep.proto'; message Root {}"u8.ToArray(), new()
            {
                ResolveReference = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    "syntax='proto2'; message Imported { optional string a=1 [json_name='key']; optional string b=2 [json_name='key']; }"u8.ToArray())
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.json_name");
    }

    [Test]
    [Arguments("message Name {}")]
    [Arguments("enum Name { ZERO=0; }")]
    [Arguments("service Name {}")]
    [Arguments("enum E { Name=0; }")]
    public async Task PackagePrefixesCannotShareQualifiedNamesWithDeclarations(string declaration)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var packageFirst in new[] { false, true })
            {
                var imports = packageFirst ? "import 'package.proto'; import 'type.proto';"
                    : "import 'type.proto'; import 'package.proto';";
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {imports} message Root {{}}"),
                    new()
                    {
                        ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                            Encoding.UTF8.GetBytes(name switch
                            {
                                "package.proto" => $"syntax='proto{syntax}'; package example.Name.child; message Other {{}}",
                                "type.proto" => $"syntax='proto{syntax}'; package example; {declaration}",
                                _ => throw new InvalidOperationException("Unexpected import: " + name)
                            }))
                    });
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.duplicate");
                await Assert.That(result.Diagnostics[0].Path).IsEqualTo("$");
            }
        }
    }

    [Test]
    public async Task PackagePrefixesMayBeSharedByFilesAndRemainCaseSensitive()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            "syntax='proto3'; package example; import 'first.proto'; import 'second.proto'; message Name {} message Root { name.First first=1; name.Second second=2; }"u8.ToArray(),
            new()
            {
                ResolveReference = (name, _) =>
                {
                    calls.Add(name);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(name switch
                    {
                        "first.proto" => "syntax='proto3'; package example.name; message First {}",
                        "second.proto" => "syntax='proto3'; package example.name; message Second {}",
                        _ => throw new InvalidOperationException("Unexpected import: " + name)
                    }));
                }
            });
        await Assert.That(calls.Count).IsEqualTo(2);
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("Name.Child", false, DocumentValidationStatus.Invalid)]
    [Arguments("Name.Child", true, DocumentValidationStatus.Valid)]
    [Arguments(".example.Name.Child", false, DocumentValidationStatus.Valid)]
    [Arguments(".example.Name.Child", true, DocumentValidationStatus.Valid)]
    public async Task CompoundTypeNamesRespectNearerImportedPackagePrefixes(
        string type, bool hasChild, DocumentValidationStatus expected)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var calls = new List<string>();
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; package example.inner; " +
                    $"import 'outer.proto'; import 'near.proto'; message Root {{ optional {type} value=1 [lazy=true]; }}"),
                new()
                {
                    ResolveReference = (name, _) =>
                    {
                        calls.Add(name);
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                            Encoding.UTF8.GetBytes(name switch
                            {
                                "outer.proto" => $"syntax='proto{syntax}'; package example; message Name {{ message Child {{}} }}",
                                "near.proto" => $"syntax='proto{syntax}'; package example.inner.Name; " +
                                    (hasChild ? "message Child {}" : "message Other {}"),
                                _ => throw new InvalidOperationException("Unexpected import: " + name)
                            }));
                    }
                });
            await Assert.That(calls.Count).IsEqualTo(2);
            await Assert.That(calls[0]).IsEqualTo("outer.proto");
            await Assert.That(calls[1]).IsEqualTo("near.proto");
            await Assert.That(result.Status).IsEqualTo(expected);
            if (expected == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
            else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type"); }
        }
    }

    [Test]
    [Arguments("Name.Child.Grand", DocumentValidationStatus.Invalid)]
    [Arguments(".example.Name.Child.Grand", DocumentValidationStatus.Valid)]
    public async Task CompoundPackagePrefixesStopFallbackAtMissingIntermediateTypes(
        string type, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("syntax='proto3'; package example.inner; import 'outer.proto'; import 'near.proto'; " +
                $"message Root {{ {type} value=1; }}"), new()
            {
                ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    Encoding.UTF8.GetBytes(name switch
                    {
                        "outer.proto" => "syntax='proto3'; package example; message Name { message Child { message Grand {} } }",
                        "near.proto" => "syntax='proto3'; package example.inner.Name.Child; message Other {}",
                        _ => throw new InvalidOperationException("Unexpected import: " + name)
                    }))
            });
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type"); }
    }

    [Test]
    [Arguments("message Name {}")]
    [Arguments("enum Name { ZERO=0; }")]
    [Arguments("message Name { message Other {} }")]
    [Arguments("optional string Name=2;")]
    [Arguments("oneof Name { string text=2; }")]
    [Arguments("enum Local { Name=0; }")]
    public async Task RelativeCompoundTypeNamesDoNotBypassNearerTypeOwners(string local)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var type in new[] { "Name.Child", ".example.Name.Child" })
            {
                foreach (var field in new[]
                {
                    $"optional {type} value=1;",
                    $"map<string,{type}> value=1;",
                    $"oneof choice {{ {type} value=1; }}"
                })
                {
                    var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                        Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; package example; " +
                            "message Name { message Child {} } message Root { " + local + field + " }"));
                    await Assert.That(result.Status).IsEqualTo(type.StartsWith('.')
                        ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
                    if (type.StartsWith('.')) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
                    else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type"); }
                }
            }
        }
    }

    [Test]
    public async Task RelativeCompoundTypeNamesStillBindTheNearestDeclaredChild()
    {
        const string document = """
            syntax='proto3'; package example;
            message Name { enum Child { ZERO=0; } }
            message Root { message Name { message Child {} }
              Name.Child value=1 [lazy=true]; }
            """;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes(document));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("optional string Name=2;")]
    [Arguments("oneof Name { string text=2; }")]
    [Arguments("enum Local { Name=0; }")]
    public async Task CompoundTypeShadowingIsIndependentOfDeclarationOrder(string local)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var before in new[] { false, true })
            {
                var field = "optional Name.Child value=1;";
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; package example; " +
                        "message Name { message Child {} } message Root { " +
                        (before ? local + field : field + local) + " }"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type");
            }
        }
    }

    [Test]
    [Arguments("Name.Child.Grand", DocumentValidationStatus.Invalid)]
    [Arguments(".example.Name.Child.Grand", DocumentValidationStatus.Valid)]
    public async Task RelativeCompoundTypeOwnersDoNotFallbackAfterAMissingIntermediateType(
        string type, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("syntax='proto3'; package example; " +
                "message Name { message Child { message Grand {} } } " +
                $"message Root {{ message Name {{ message Child {{}} }} {type} value=1; }}"));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type"); }
    }

    [Test]
    [Arguments("Name.Child", DocumentValidationStatus.Invalid)]
    [Arguments(".example.Name.Child", DocumentValidationStatus.Valid)]
    public async Task RelativeCompoundTypeOwnershipIncludesImportedOuterDeclarations(
        string type, DocumentValidationStatus expected)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; package example; import 'dep.proto'; " +
                $"message Root {{ message Name {{}} {type} value=1; }}"), new()
            {
                ResolveReference = (_, _) =>
                {
                    calls++;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        "syntax='proto3'; package example; message Name { message Child {} }"u8.ToArray());
                }
            });
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type"); }
    }

    [Test]
    [Arguments("service S {}")]
    [Arguments("message Request {} message Response {} service S { rpc Get(Request) returns (Response); }")]
    [Arguments("package example.api; message M {} service S { rpc Get(stream M) returns (M); rpc Put(M) returns (stream M); rpc Both(stream M) returns (stream M); }")]
    [Arguments("service S { option deprecated=true; } service Other {}")]
    public async Task GrpcProto3ValidatesRootServicesAndStreamingDeclarations(string body)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("syntax='proto3'; " + body));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("syntax='proto3';", "grpc.service")]
    [Arguments("syntax='proto3'; message S {}", "grpc.service")]
    [Arguments("syntax='proto3'; // service S {}\n message M {}", "grpc.service")]
    [Arguments("syntax='proto3'; option java_package='service S {}';", "grpc.service")]
    [Arguments("syntax='proto2'; service S {}", "protobuf.syntax")]
    [Arguments("edition='2023'; service S {}", "protobuf.syntax")]
    [Arguments("service S {}", "protobuf.syntax")]
    [Arguments("syntax='proto3'; service S { rpc Get(Missing) returns (Missing); }", "protobuf.type")]
    [Arguments("syntax='proto3'; enum E { ZERO=0; } service S { rpc Get(E) returns (E); }", "protobuf.type")]
    [Arguments("syntax='proto3'; message M {} service S { rpc Get(M) returns(M); rpc Get(M) returns(M); }", "protobuf.duplicate")]
    public async Task GrpcProto3RejectsWrongProfileMissingServicesAndInvalidRpcTypes(string document, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes(document));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        await Assert.That(result.Diagnostics[0].Path).IsEqualTo("$");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GrpcProto3RequiresARootServiceRatherThanAnImportedService(bool rootService)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'dep.proto'; " +
                (rootService ? "service Root { rpc Get(dep.M) returns(dep.M); }" : "message Root {}")),
            new()
            {
                ResolveReference = (name, _) =>
                {
                    calls.Add(name);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        "syntax='proto2'; package dep; message M {} service Imported {}"u8.ToArray());
                }
            });
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("dep.proto");
        await Assert.That(result.Status).IsEqualTo(rootService ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
        if (rootService) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("grpc.service"); }
    }

    [Test]
    public async Task GrpcProto3KeepsUnresolvedImportsAndUnsupportedOptionsExplicit()
    {
        var missing = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            "syntax='proto3'; import 'dep.proto'; service S {}"u8.ToArray());
        await Assert.That(missing.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(missing.Diagnostics[0].Code).IsEqualTo("reference.unresolved");
        var unsupported = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            "syntax='proto3'; service S { option (custom.flag)=true; }"u8.ToArray());
        await Assert.That(unsupported.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
        await Assert.That(unsupported.Diagnostics[0].Code).IsEqualTo("protobuf.unsupported");
        var limited = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            "syntax='proto3'; service S {}"u8.ToArray(), new() { MaxWork = 1 });
        await Assert.That(limited.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(limited.Diagnostics[0].Code).IsEqualTo("limit.work");
    }

    [Test]
    [Arguments("syntax='proto3'; message M { Missing value=1; }", "protobuf.type")]
    [Arguments("edition='2023'; message M {}", "protobuf.syntax")]
    [Arguments("syntax='proto3'; service S { option (custom.flag)=true; }", "protobuf.unsupported")]
    public async Task GrpcProto3DoesNotHideInvalidOrUnsupportedImportedDeclarations(string dependency, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("gRPC/proto3",
            "syntax='proto3'; import 'dep.proto'; service Root {}"u8.ToArray(), new()
            {
                ResolveReference = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(dependency))
            });
        await Assert.That(result.Status).IsEqualTo(code == "protobuf.unsupported"
            ? DocumentValidationStatus.Unsupported : DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
    }

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
    [Arguments("message M { message Child {} optional Child value=1 [deprecated=true $ lazy=false]; }", ",")]
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
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("E")]
    [Arguments("map<string,Child>")]
    public async Task ExplicitLazyOptionsRejectNonMessageTypes(string type)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var option in new[] { "", "[lazy=false]", "[lazy=true]", "[unverified_lazy=false]", "[unverified_lazy=true]" })
            {
                var label = type.StartsWith("map", StringComparison.Ordinal) ? "" : "optional ";
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; enum E {{ ZERO=0; }} message Child {{}} " +
                        $"message Root {{ {label}{type} value=1 {option}; }}"));
                await Assert.That(result.Status).IsEqualTo(option.Length == 0
                    ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
                if (option.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
                else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.lazy"); }
            }
        }
    }

    [Test]
    [Arguments("true")]
    [Arguments("false")]
    public async Task ExplicitLazyOptionsAcceptResolvedMessageFields(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var body in new[]
            {
                $"optional Child value=1 [lazy={value}];",
                $"repeated Child value=1 [lazy={value}];",
                $"oneof choice {{ Child value=1 [lazy={value}]; }}",
                $"optional Child value=1 [unverified_lazy={value}];",
                $"repeated Child value=1 [unverified_lazy={value}];",
                $"oneof choice {{ Child value=1 [unverified_lazy={value}]; }}"
            })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Child {{}} message Root {{ {body} }}"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
        }
    }

    [Test]
    [Arguments("dep.Child", DocumentValidationStatus.Valid, "")]
    [Arguments("dep.E", DocumentValidationStatus.Invalid, "protobuf.lazy")]
    [Arguments("dep.Missing", DocumentValidationStatus.Invalid, "protobuf.type")]
    public async Task LazyOptionsRetainImportedTypeIdentityAndKind(
        string type, DocumentValidationStatus expected, string code)
    {
        foreach (var option in new[] { "lazy=true", "lazy=false", "unverified_lazy=true", "unverified_lazy=false" })
        {
            var requests = new List<string>();
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
                Encoding.UTF8.GetBytes($"syntax='proto3'; import 'dep.proto'; message Root {{ {type} value=1 [{option}]; }}"),
                new()
                {
                    ResolveReference = (name, _) =>
                    {
                        requests.Add(name);
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                            """syntax="proto3"; package dep; message Child {} enum E { ZERO=0; }"""u8.ToArray());
                    }
                });
            await Assert.That(requests.Count).IsEqualTo(1);
            await Assert.That(requests[0]).IsEqualTo("dep.proto");
            await Assert.That(result.Status).IsEqualTo(expected);
            if (expected == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
            else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code); }
        }
    }

    [Test]
    [Arguments("lazy='false'")]
    [Arguments("lazy='fa' 'lse'")]
    [Arguments("lazy=1")]
    [Arguments("lazy=false,lazy=true")]
    [Arguments("lazy=true,lazy=false")]
    [Arguments("unverified_lazy='false'")]
    [Arguments("unverified_lazy='fa' 'lse'")]
    [Arguments("unverified_lazy=1")]
    [Arguments("unverified_lazy=false,unverified_lazy=true")]
    [Arguments("unverified_lazy=true,unverified_lazy=false")]
    public async Task LazyOptionsKeepNativeBooleanAndDuplicateChecks(string options)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; message Child {{}} message Root {{ Child value=1 [{options}]; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    [Arguments(false, "lazy")]
    [Arguments(true, "lazy")]
    [Arguments(false, "unverified_lazy")]
    [Arguments(true, "unverified_lazy")]
    public async Task LazyMessageFieldsRetainPublicImportVisibility(bool forwarded, string option)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; import 'bridge.proto'; message Root {{ dep.Child value=1 [{option}=false]; }}"),
            new()
            {
                ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    Encoding.UTF8.GetBytes(name switch
                    {
                        "bridge.proto" => $"syntax='proto3'; import {(forwarded ? "public " : "")}'dep.proto';",
                        "dep.proto" => "syntax='proto3'; package dep; message Child {}",
                        _ => throw new InvalidOperationException("Unexpected import: " + name)
                    }))
            });
        await Assert.That(result.Status).IsEqualTo(forwarded
            ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
        if (forwarded) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.type"); }
    }

    [Test]
    [Arguments("true", "true")]
    [Arguments("true", "false")]
    [Arguments("false", "true")]
    [Arguments("false", "false")]
    public async Task LazyAndUnverifiedLazyRemainIndependentFieldOptions(string lazy, string unverified)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Child {{}} message Root {{ " +
                    $"optional Child first=1 [lazy={lazy},unverified_lazy={unverified}]; " +
                    $"optional Child second=2 [unverified_lazy={lazy},lazy={unverified}]; }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("int64")]
    [Arguments("uint64")]
    [Arguments("sint64")]
    [Arguments("fixed64")]
    [Arguments("sfixed64")]
    public async Task JsTypeOptionsAcceptEveryDeclared64BitIntegralType(string type)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var value in new[] { "JS_NORMAL", "JS_STRING", "JS_NUMBER" })
            {
                foreach (var body in new[]
                {
                    $"optional {type} value=1 [jstype={value}];",
                    $"repeated {type} value=1 [jstype={value},packed=false];",
                    $"oneof choice {{ {type} value=1 [jstype={value}]; }}"
                })
                {
                    var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                        Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Root {{ {body} }}"));
                    await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                    await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
                }
            }
        }
    }

    [Test]
    [Arguments("double")]
    [Arguments("float")]
    [Arguments("int32")]
    [Arguments("uint32")]
    [Arguments("sint32")]
    [Arguments("fixed32")]
    [Arguments("sfixed32")]
    [Arguments("bool")]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("Child")]
    [Arguments("E")]
    [Arguments("map<string,int64>")]
    public async Task JsTypeOptionsRejectTypesOutsideTheDeclaredIntegralSet(string type)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var value in new[] { "JS_NORMAL", "JS_STRING", "JS_NUMBER" })
            {
                var label = type.StartsWith("map", StringComparison.Ordinal) ? "" : "optional ";
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; enum E {{ ZERO=0; }} message Child {{}} " +
                        $"message Root {{ {label}{type} value=1 [jstype={value}]; }}"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.jstype");
            }
        }
    }

    [Test]
    [Arguments("'JS_STRING'")]
    [Arguments("'JS_' 'STRING'")]
    [Arguments("JS_string")]
    [Arguments("UNKNOWN")]
    [Arguments("true")]
    [Arguments("1")]
    [Arguments("-JS_STRING")]
    public async Task JsTypeOptionsRequireNativeDeclaredEnumConstants(string value)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; message Root {{ int64 value=1 [jstype={value}]; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    [Arguments("jstype=JS_STRING,jstype=JS_STRING")]
    [Arguments("jstype=JS_NUMBER,jstype=JS_NORMAL")]
    public async Task JsTypeOptionsRemainUniqueWithinAField(string options)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; message Root {{ int64 value=1 [{options}]; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    [Arguments("true", "true")]
    [Arguments("true", "false")]
    [Arguments("false", "true")]
    [Arguments("false", "false")]
    public async Task SingularDeclarationOptionsRejectRepeatedAssignments(string first, string second)
    {
        var options = $"option deprecated={first}; ; option deprecated={second};";
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var body in new[]
            {
                $"{options} message Root {{}}",
                $"message Root {{ {options} }}",
                $"enum E {{ ZERO=0; {options} }}",
                $"message Root {{}} service S {{ {options} }}",
                $"message Root {{}} service S {{ rpc Get(Root) returns (Root) {{ {options} }} }}"
            })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {body}"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
            }
        }
    }

    [Test]
    [Arguments("java_package='example'", "java_package='example'")]
    [Arguments("java_package='ex' 'ample'", "java_package='other'")]
    [Arguments("optimize_for=SPEED", "optimize_for=CODE_SIZE")]
    [Arguments("java_multiple_files=true", "java_multiple_files=false")]
    public async Task SingularFileOptionsRejectDuplicateStringsEnumsAndBooleans(string first, string second)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; option {first}; message Root {{}} option {second};"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    [Arguments("true", "true")]
    [Arguments("true", "false")]
    [Arguments("false", "true")]
    [Arguments("false", "false")]
    public async Task SingularEnumAliasOptionCannotBeOverwritten(string first, string second)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; enum E {{ option allow_alias={first}; " +
                $"ZERO=0; option deprecated=false; option allow_alias={second}; OTHER=0; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    public async Task SingularOptionsHaveIndependentContainingDeclarationsAndFiles()
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var requests = 0;
            var root = $"syntax='proto{syntax}'; option deprecated=false; option java_package='root'; import 'dep.proto'; " +
                "message Root { option deprecated=true; message Child { option deprecated=false; } " +
                "enum E { option deprecated=false; ZERO=0 [deprecated=false]; OTHER=1 [deprecated=true]; } } " +
                "message Other { option deprecated=false; } enum F { option deprecated=true; ZERO=0; } " +
                "service S { option deprecated=true; " +
                "rpc Get(Root) returns (Root) { option deprecated=true; } " +
                "rpc Set(Root) returns (Root) { option deprecated=false; } } " +
                "service T { option deprecated=false; }";
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes(root), new()
                {
                    ResolveReference = (_, _) =>
                    {
                        requests++;
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                            Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; package dep; " +
                                "option deprecated=true; option java_package='dep'; message Child { option deprecated=true; }"));
                    }
                });
            await Assert.That(requests).IsEqualTo(1);
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("message Root { option deprecated=true; message Child { option deprecated=false; } option deprecated=false; }")]
    [Arguments("message Root {} service S { option deprecated=true; rpc Get(Root) returns (Root) { option deprecated=false; } option deprecated=false; }")]
    public async Task NestedDeclarationsDoNotResetTheirParentOptionSet(string body)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; {body}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    [Arguments("IDEMPOTENCY_UNKNOWN")]
    [Arguments("NO_SIDE_EFFECTS")]
    [Arguments("IDEMPOTENT")]
    public async Task RpcIdempotencyOptionsAcceptDeclaredNativeConstants(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var streaming in new[] { ("", ""), ("stream ", ""), ("", "stream "), ("stream ", "stream ") })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Root {{}} service S {{ " +
                        $"rpc Get({streaming.Item1}Root) returns ({streaming.Item2}Root) {{ ; option deprecated=false; " +
                        $"option idempotency_level={value}; ; }} }}"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
        }
    }

    [Test]
    [Arguments("'IDEMPOTENT'")]
    [Arguments("'NO_' 'SIDE_EFFECTS'")]
    [Arguments("idempotent")]
    [Arguments("UNKNOWN")]
    [Arguments("true")]
    [Arguments("0")]
    [Arguments("1")]
    [Arguments("2")]
    [Arguments("+IDEMPOTENT")]
    [Arguments("-NO_SIDE_EFFECTS")]
    public async Task RpcIdempotencyOptionsRejectNonNativeOrUnknownConstants(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Root {{}} service S {{ " +
                    $"rpc Get(Root) returns (Root) {{ option idempotency_level={value}; }} }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
        }
    }

    [Test]
    [Arguments("option idempotency_level=IDEMPOTENT; message Root {}")]
    [Arguments("message Root { option idempotency_level=IDEMPOTENT; }")]
    [Arguments("message Root { oneof choice { option idempotency_level=IDEMPOTENT; int32 value=1; } }")]
    [Arguments("message Root {} service S { option idempotency_level=IDEMPOTENT; }")]
    [Arguments("message Root { optional int32 value=1 [idempotency_level=IDEMPOTENT]; }")]
    [Arguments("enum E { option idempotency_level=IDEMPOTENT; ZERO=0; }")]
    [Arguments("enum E { ZERO=0 [idempotency_level=IDEMPOTENT]; }")]
    public async Task RpcIdempotencyOptionsRequireMethodDeclarationScope(string body)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {body}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option_scope");
        }
    }

    [Test]
    [Arguments("IDEMPOTENT", "IDEMPOTENT")]
    [Arguments("NO_SIDE_EFFECTS", "IDEMPOTENT")]
    [Arguments("IDEMPOTENCY_UNKNOWN", "NO_SIDE_EFFECTS")]
    public async Task RpcIdempotencyOptionsRemainSingular(string first, string second)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("syntax='proto3'; message Root {} service S { rpc Get(Root) returns (Root) { " +
                $"option idempotency_level={first}; option deprecated=true; option idempotency_level={second}; }} }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    public async Task RpcIdempotencyOptionsHaveIndependentMethodAndFileScopes()
    {
        var requests = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'dep.proto'; message Root {} service S { " +
                "rpc First(Root) returns (Root) { option idempotency_level=IDEMPOTENT; } " +
                "rpc Second(Root) returns (Root) { option idempotency_level=NO_SIDE_EFFECTS; } " +
                "rpc Third(Root) returns (Root) { option idempotency_level=IDEMPOTENCY_UNKNOWN; } " +
                "rpc Omitted(Root) returns (Root); }"), new()
            {
                ResolveReference = (_, _) =>
                {
                    requests++;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        Encoding.UTF8.GetBytes("syntax='proto2'; package dep; message Root {} service S { " +
                            "rpc First(Root) returns (Root) { option idempotency_level=NO_SIDE_EFFECTS; } }"));
                }
            });
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("true")]
    [Arguments("false")]
    public async Task DebugRedactOptionsAcceptNativeBooleansOnFieldsAndEnumValues(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var label = syntax == 2 ? "optional " : "";
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; enum E {{ ZERO=0 [debug_redact={value},deprecated=false]; " +
                    $"OTHER=1 [debug_redact={value}]; }} message Root {{ " +
                    $"{label}string text=1 [debug_redact={value}]; repeated bytes data=2 [debug_redact={value}]; " +
                    $"oneof choice {{ E state=3 [debug_redact={value}]; }} " +
                    $"map<string,int32> values=4 [debug_redact={value}]; }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("'true'")]
    [Arguments("'tr' 'ue'")]
    [Arguments("TRUE")]
    [Arguments("0")]
    [Arguments("1")]
    [Arguments("+true")]
    [Arguments("-false")]
    public async Task DebugRedactOptionsRejectNonBooleanConstants(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var body in new[]
            {
                $"message Root {{ optional string text=1 [debug_redact={value}]; }}",
                $"enum E {{ ZERO=0 [debug_redact={value}]; }}"
            })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {body}"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
            }
        }
    }

    [Test]
    [Arguments("true", "true")]
    [Arguments("true", "false")]
    [Arguments("false", "true")]
    [Arguments("false", "false")]
    public async Task DebugRedactOptionsRemainSingularOnEachFieldOrEnumValue(string first, string second)
    {
        var options = $"debug_redact={first},deprecated=false,debug_redact={second}";
        foreach (var body in new[]
        {
            $"message Root {{ string text=1 [{options}]; }}",
            $"enum E {{ ZERO=0 [{options}]; }}"
        })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
                Encoding.UTF8.GetBytes($"syntax='proto3'; {body}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
        }
    }

    [Test]
    [Arguments("option debug_redact=true; message Root {}")]
    [Arguments("message Root { option debug_redact=true; }")]
    [Arguments("enum E { option debug_redact=true; ZERO=0; }")]
    [Arguments("message Root { oneof choice { option debug_redact=true; string text=1; } }")]
    [Arguments("message Root {} service S { option debug_redact=true; }")]
    [Arguments("message Root {} service S { rpc Get(Root) returns (Root) { option debug_redact=true; } }")]
    public async Task DebugRedactOptionsRejectOtherDeclarationScopes(string body)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {body}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option_scope");
        }
    }

    [Test]
    public async Task DebugRedactOptionsRetainImportedFieldAndEnumValueValidation()
    {
        foreach (var value in new[] { "true", "'true'" })
        {
            var requests = 0;
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
                Encoding.UTF8.GetBytes("syntax='proto3'; import 'dep.proto'; message Root { string value=1 [debug_redact=false]; }"), new()
                {
                    ResolveReference = (_, _) =>
                    {
                        requests++;
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                            Encoding.UTF8.GetBytes($"syntax='proto2'; package dep; enum E {{ ZERO=0 [debug_redact={value}]; }} " +
                                "message M { optional string value=1 [debug_redact=true]; }"));
                    }
                });
            await Assert.That(requests).IsEqualTo(1);
            await Assert.That(result.Status).IsEqualTo(value == "true" ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
            if (value == "true") { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
            else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option"); }
        }
    }

    [Test]
    [Arguments("true")]
    [Arguments("false")]
    public async Task AuthoredMapEntryOptionsCannotReplaceMapSyntax(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var label = syntax == 2 ? "optional " : "";
            foreach (var body in new[]
            {
                $"message Root {{ option map_entry={value}; }}",
                $"message Root {{ message Entry {{ option map_entry={value}; {label}string key=1; {label}int32 value=2; }} }}",
                $"message Root {{ map<string,int32> values=1; option map_entry={value}; }}"
            })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {body}"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.map_entry");
            }
        }
    }

    [Test]
    public async Task ImportedAuthoredMapEntryOptionsAreRejected()
    {
        var requests = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'dep.proto'; message Root {}"), new()
            {
                ResolveReference = (_, _) =>
                {
                    requests++;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        Encoding.UTF8.GetBytes("syntax='proto2'; message Entry { option map_entry=false; }"));
                }
            });
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.map_entry");
    }

    [Test]
    public async Task NativeMapSyntaxDoesNotRequireAnAuthoredMapEntryOption()
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Root {{ " +
                    "map<string,int32> numbers=1; map<int32,Child> children=2; message Child {} }"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("option map_entry=true; message Root {}")]
    [Arguments("message Root { oneof choice { option map_entry=false; string value=1; } }")]
    [Arguments("message Root {} service S { option map_entry=true; }")]
    [Arguments("message Root {} service S { rpc Get(Root) returns (Root) { option map_entry=false; } }")]
    [Arguments("message Root { string value=1 [map_entry=true]; }")]
    [Arguments("enum E { option map_entry=false; ZERO=0; }")]
    [Arguments("enum E { ZERO=0 [map_entry=true]; }")]
    public async Task MapEntryOptionsRejectNonMessageDeclarationPlacement(string body)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; {body}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option_scope");
    }

    [Test]
    [Arguments("java_package='example'", "file")]
    [Arguments("java_outer_classname='Root'", "file")]
    [Arguments("go_package='example'", "file")]
    [Arguments("csharp_namespace='Example'", "file")]
    [Arguments("objc_class_prefix='EX'", "file")]
    [Arguments("php_namespace='Example'", "file")]
    [Arguments("php_metadata_namespace='Example'", "file")]
    [Arguments("ruby_package='Example'", "file")]
    [Arguments("java_multiple_files=true", "file")]
    [Arguments("php_class_prefix='EX'", "file")]
    [Arguments("swift_prefix='EX'", "file")]
    [Arguments("java_string_check_utf8=true", "file")]
    [Arguments("java_generate_equals_and_hash=false", "file")]
    [Arguments("no_standard_descriptor_accessor=true", "message")]
    [Arguments("cc_generic_services=false", "file")]
    [Arguments("java_generic_services=false", "file")]
    [Arguments("py_generic_services=false", "file")]
    [Arguments("cc_enable_arenas=true", "file")]
    [Arguments("optimize_for=SPEED", "file")]
    [Arguments("allow_alias=true", "enum")]
    [Arguments("packed=false", "field")]
    [Arguments("lazy=false", "field")]
    [Arguments("unverified_lazy=false", "field")]
    [Arguments("jstype=JS_NORMAL", "field")]
    [Arguments("json_name='value'", "field")]
    [Arguments("weak=false", "field")]
    [Arguments("default=0", "field")]
    [Arguments("idempotency_level=IDEMPOTENT", "method")]
    [Arguments("debug_redact=true", "field,enum-value")]
    [Arguments("deprecated=false", "file,message,field,enum,enum-value,service,method")]
    public async Task SupportedOptionsUseConsistentScopeChecksAtEveryTarget(string assignment, string allowed)
    {
        foreach (var syntax in assignment.StartsWith("default=", StringComparison.Ordinal) ? new[] { 2 } : new[] { 2, 3 })
        {
            var option = $"option {assignment};";
            var label = assignment.StartsWith("packed=", StringComparison.Ordinal) ? "repeated " : "optional ";
            var type = assignment.StartsWith("lazy=", StringComparison.Ordinal) ||
                assignment.StartsWith("unverified_lazy=", StringComparison.Ordinal) ? "Child" : "int64";
            var field = $"{label}{type} value=1 [{assignment}];";
            foreach (var (target, body) in new[]
            {
                ("file", $"{option} message Root {{}}"),
                ("message", $"message Root {{ {option} }}"),
                ("oneof", $"message Root {{ oneof choice {{ {option} int64 value=1; }} }}"),
                ("field", $"message Root {{ message Child {{}} {field} }}"),
                ("enum", $"enum E {{ {option} ZERO=0; }}"),
                ("enum-value", $"enum E {{ ZERO=0 [{assignment}]; }}"),
                ("service", $"message Root {{}} service S {{ {option} }}"),
                ("method", $"message Root {{}} service S {{ rpc Get(Root) returns (Root) {{ {option} }} }}")
            })
            {
                var permitted = allowed.Split(',').Contains(target);
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {body}"));
                await Assert.That(result.Status).IsEqualTo(permitted ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
                if (permitted) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
                else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option_scope"); }
            }
        }
    }

    [Test]
    [Arguments("php_class_prefix", "true", false)]
    [Arguments("swift_prefix", "0", false)]
    [Arguments("java_string_check_utf8", "'true'", false)]
    [Arguments("java_generate_equals_and_hash", "1", false)]
    [Arguments("no_standard_descriptor_accessor", "+true", true)]
    public async Task GeneratorHintsRejectIncorrectValueKinds(string name, string value, bool message)
    {
        var statement = $"option {name}={value};";
        var body = message ? $"message Root {{ {statement} }}" : $"{statement} message Root {{}}";
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; {body}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    [Arguments("php_class_prefix", "'EX'", "'OTHER'", false)]
    [Arguments("swift_prefix", "'EX'", "'EX'", false)]
    [Arguments("java_string_check_utf8", "true", "false", false)]
    [Arguments("java_generate_equals_and_hash", "false", "false", false)]
    [Arguments("no_standard_descriptor_accessor", "false", "true", true)]
    public async Task GeneratorHintsRemainSingular(string name, string first, string second, bool message)
    {
        var statements = $"option {name}={first}; option deprecated=false; option {name}={second};";
        var body = message ? $"message Root {{ {statements} }}" : $"{statements} message Root {{}}";
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax='proto3'; {body}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.option");
    }

    [Test]
    public async Task GeneratorHintsPreserveLiteralValuesAndIndependentScopes()
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var requests = 0;
            var options = "option php_class_prefix='P' 'refix'; option swift_prefix=''; " +
                "option java_string_check_utf8=false; option java_generate_equals_and_hash=true;";
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {options} import 'dep.proto'; " +
                    "message Root { option no_standard_descriptor_accessor=false; " +
                    "message Child { option no_standard_descriptor_accessor=true; } } " +
                    "message Other { option no_standard_descriptor_accessor=true; }"), new()
                {
                    ResolveReference = (_, _) =>
                    {
                        requests++;
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                            $"syntax='proto{syntax}'; package dep; {options} " +
                            "message Root { option no_standard_descriptor_accessor=true; }"));
                    }
                });
            await Assert.That(requests).IsEqualTo(1);
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("unknown=true")]
    [Arguments("unknown=17")]
    [Arguments("unknown=\"text\"")]
    [Arguments("unknown=VALUE")]
    [Arguments("unknown.part=false")]
    [Arguments("(custom.flag)=true")]
    [Arguments("(custom.flag)=\"text\"")]
    public async Task EnumAndEnumValueUnknownOptionPoliciesRemainUnsupported(string option)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var body in new[]
            {
                $"option {option}; ZERO=0;",
                $"ZERO=0; option {option};",
                $"ZERO=0 [{option}];",
                $"ZERO=0 [deprecated=false, {option}];"
            })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; enum E {{ {body} }}"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.unsupported");
            }
        }
    }

    [Test]
    [Arguments("unknown.member", DocumentValidationStatus.Unsupported, "protobuf.unsupported")]
    [Arguments("deprecated.member", DocumentValidationStatus.Unsupported, "protobuf.unsupported")]
    [Arguments("features.(custom.flag)", DocumentValidationStatus.Unsupported, "protobuf.unsupported")]
    [Arguments("features.(.custom.flag).member", DocumentValidationStatus.Unsupported, "protobuf.unsupported")]
    [Arguments("(custom.flag).member", DocumentValidationStatus.Unsupported, "protobuf.unsupported")]
    [Arguments("(.custom.flag).member", DocumentValidationStatus.Unsupported, "protobuf.unsupported")]
    [Arguments(".deprecated", DocumentValidationStatus.Invalid, "protobuf.identifier")]
    [Arguments(".unknown.member", DocumentValidationStatus.Invalid, "protobuf.identifier")]
    [Arguments("deprecated.", DocumentValidationStatus.Invalid, "protobuf.identifier")]
    [Arguments("deprecated..member", DocumentValidationStatus.Invalid, "protobuf.identifier")]
    [Arguments("deprecated.\"member\"", DocumentValidationStatus.Invalid, "protobuf.identifier")]
    public async Task OptionNamesUseOneGrammarAtEveryDeclarationTarget(
        string name, DocumentValidationStatus status, string code)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var option = $"option {name}=true;";
            var label = syntax == 2 ? "optional " : "";
            foreach (var body in new[]
            {
                $"{option} message Root {{}}",
                $"message Root {{ {option} }}",
                $"message Root {{ oneof Choice {{ {option} int32 value=1; }} }}",
                $"message Root {{ {label}int32 value=1 [{name}=true]; }}",
                $"enum E {{ {option} ZERO=0; }}",
                $"enum E {{ ZERO=0 [{name}=true]; }}",
                $"message Root {{}} service S {{ {option} }}",
                $"message Root {{}} service S {{ rpc Get(Root) returns (Root) {{ {option} }} }}"
            })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; {body}"));
                await Assert.That(result.Status).IsEqualTo(status);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
            }
        }
    }

    [Test]
    [Arguments("option deprecated=true; ZERO=0 [deprecated=false];", DocumentValidationStatus.Valid, "")]
    [Arguments("option deprecated=false; ZERO=0 [deprecated=true];", DocumentValidationStatus.Valid, "")]
    [Arguments("option allow_alias=true; ZERO=0; OTHER=0;", DocumentValidationStatus.Valid, "")]
    [Arguments("option allow_alias=false; ZERO=0; OTHER=0;", DocumentValidationStatus.Invalid, "protobuf.enum")]
    [Arguments("option allow_alias=\"true\"; ZERO=0;", DocumentValidationStatus.Invalid, "protobuf.option")]
    [Arguments("option deprecated=1; ZERO=0;", DocumentValidationStatus.Invalid, "protobuf.option")]
    [Arguments("ZERO=0 [deprecated=\"false\"];", DocumentValidationStatus.Invalid, "protobuf.option")]
    [Arguments("ZERO=0 [deprecated=false, deprecated=true];", DocumentValidationStatus.Invalid, "protobuf.option")]
    [Arguments("ZERO=0 [deprecated=true, deprecated=false];", DocumentValidationStatus.Invalid, "protobuf.option")]
    [Arguments("ZERO=0 [deprecated=false, deprecated=false];", DocumentValidationStatus.Invalid, "protobuf.option")]
    [Arguments("option \"(\" custom.flag)=true; ZERO=0;", DocumentValidationStatus.Invalid, "protobuf.identifier")]
    [Arguments("ZERO=0 [\"(\" custom.flag)=true];", DocumentValidationStatus.Invalid, "protobuf.identifier")]
    public async Task EnumOptionPoliciesPreserveKnownBooleansAliasesAndInvalidSyntax(
        string body, DocumentValidationStatus expected, string code)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; enum E {{ {body} }}"));
            await Assert.That(result.Status).IsEqualTo(expected);
            if (expected == DocumentValidationStatus.Valid)
            {
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
            else { await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code); }
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
    [Arguments("""message Root { message Child {} optional Child value=1 [lazy="false"]; }""")]
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
    [Arguments("\\x1")]
    [Arguments("\\xf")]
    [Arguments("\\XA")]
    [Arguments("\\x1g")]
    [Arguments("\\x12")]
    [Arguments("\\X7F")]
    [Arguments("\\x123")]
    [Arguments("\\xF\" \"F")]
    public async Task HexadecimalStringEscapesAcceptOneOrTwoDigits(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var quote in new[] { '"', '\'' })
            {
                var quoted = $"{quote}{value.Replace("\"", quote.ToString(), StringComparison.Ordinal)}{quote}";
                var defaultOption = syntax == 2 ? $",default={quoted}" : "";
                var schema = $"syntax=\"proto{syntax}\"; option java_package={quoted}; " +
                    $"message M {{ optional string value=1 [json_name={quoted}{defaultOption}]; }}";
                var result = await new BuiltInDocumentValidator().ValidateAsync(
                    $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(schema));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
        }
    }

    [Test]
    [Arguments("'\\0'")]
    [Arguments("'\\00'")]
    [Arguments("'\\000'")]
    [Arguments("'\\x0'")]
    [Arguments("'\\x0g'")]
    [Arguments("'\\X00'")]
    [Arguments("'\\u0000'")]
    [Arguments("'\\U00000000'")]
    [Arguments("'prefix' \"\\x00\" 'suffix'")]
    [Arguments("'\\u0000' 'suffix'")]
    [Arguments("'prefix' '\\000'")]
    public async Task JsonNamesRejectDecodedNulInEveryFieldKind(string literal)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var label = syntax == 2 ? "optional " : "";
            foreach (var field in new[]
            {
                $"{label}string value=1 [json_name={literal}];",
                $"repeated int32 value=1 [json_name={literal}];",
                $"oneof choice {{ string value=1 [json_name={literal}]; }}",
                $"map<string,int32> value=1 [json_name={literal}];"
            })
            {
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Root {{ {field} }}"));
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.json_name");
            }
        }
    }

    [Test]
    [Arguments("''")]
    [Arguments("'\\x01'")]
    [Arguments("'\\n'")]
    [Arguments("'\\uFFFF'")]
    [Arguments("'\\U0010FFFF'")]
    [Arguments("'literal\\\\0'")]
    [Arguments("'literal\\\\u0000'")]
    public async Task JsonNameNulCheckDoesNotRestrictOtherDecodedValues(string literal)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='proto{syntax}'; message Root {{ optional string value=1 [json_name={literal}]; }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("'\\0'")]
    [Arguments("'\\x0'")]
    [Arguments("'\\x0g'")]
    [Arguments("'\\u0000'")]
    [Arguments("'a' '\\000' 'b'")]
    public async Task NulRemainsLegalInStringOptionsAndStringOrBytesDefaults(string literal)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax='proto2'; option java_package={literal}; message Root {{ " +
                $"optional string text=1 [default={literal}]; optional bytes data=2 [default={literal}]; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ImportedJsonNamesRetainDecodedNulValidation()
    {
        var requests = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'dep.proto'; message Root {}"), new()
            {
                ResolveReference = (name, _) =>
                {
                    requests++;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        name == "dep.proto" ? Encoding.UTF8.GetBytes(
                            "syntax='proto2'; message Imported { optional string text=1 [json_name='a' '\\u0000']; }")
                            : (ReadOnlyMemory<byte>?)null);
                }
            });
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.json_name");
    }

    [Test]
    [Arguments("\\x")]
    [Arguments("\\X")]
    [Arguments("\\xg")]
    [Arguments("\\Xz")]
    [Arguments("\\x\" \"1")]
    [Arguments("\\u123")]
    [Arguments("\\U0000012")]
    [Arguments("\\u12z4")]
    [Arguments("\\U0000000z")]
    public async Task HexadecimalAndUnicodeEscapesRejectMissingOrInvalidRequiredDigits(string value)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; option java_package=\"{value}\"; message M {{}}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.string");
        }
    }

    [Test]
    [Arguments("\\a", "\a")]
    [Arguments("\\b", "\b")]
    [Arguments("\\f", "\f")]
    [Arguments("\\n", "\n")]
    [Arguments("\\r", "\r")]
    [Arguments("\\t", "\t")]
    [Arguments("\\v", "\v")]
    [Arguments("\\'", "'")]
    [Arguments("\\\"", "\"")]
    public async Task CharacterEscapesResolveTheirDecodedImportPath(string escaped, string decoded)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var quote in new[] { '"', '\'' })
            {
                foreach (var adjacent in new[] { false, true })
                {
                    var requests = new List<string>();
                    var expected = $"child{decoded}.proto";
                    var path = adjacent
                        ? $"{quote}child{escaped}{quote} /* join */ {quote}.proto{quote}"
                        : $"{quote}child{escaped}.proto{quote}";
                    var source = $"syntax=\"proto{syntax}\"; import {path}; " +
                        $"message Root {{ optional Child value=1; }}";
                    var result = await new BuiltInDocumentValidator().ValidateAsync(
                        $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(source), new()
                        {
                            ResolveReference = (name, _) =>
                            {
                                requests.Add(name);
                                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == expected
                                    ? Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; message Child {{}}")
                                    : (ReadOnlyMemory<byte>?)null);
                            }
                        });
                    await Assert.That(requests.Count).IsEqualTo(1);
                    await Assert.That(requests[0]).IsEqualTo(expected);
                    await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                    await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
                }
            }
        }
    }

    [Test]
    [Arguments("\\a", "a")]
    [Arguments("\\b", "b")]
    [Arguments("\\f", "f")]
    [Arguments("\\n", "n")]
    [Arguments("\\r", "r")]
    [Arguments("\\t", "t")]
    [Arguments("\\v", "v")]
    public async Task CharacterEscapesNeverAliasTheirUnescapedLetterImport(string escaped, string letter)
    {
        var requests = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax=\"proto3\"; import \"child{escaped}.proto\"; message Root {{}}"),
            new()
            {
                ResolveReference = (name, _) =>
                {
                    requests.Add(name);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == $"child{letter}.proto"
                        ? """syntax="proto3"; message Child {}"""u8.ToArray() : (ReadOnlyMemory<byte>?)null);
                }
            });
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0]).IsNotEqualTo($"child{letter}.proto");
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("reference.unresolved");
    }

    [Test]
    [Arguments("\n", "protobuf.string")]
    [Arguments("\r", "protobuf.string")]
    [Arguments("\0", "protobuf.string")]
    [Arguments("\\q", "protobuf.string")]
    [Arguments("\\?", "protobuf.string")]
    [Arguments("\\", "protobuf.string")]
    [Arguments("\\\\", "protobuf.import")]
    public async Task CharacterEscapeDecodingPreservesLiteralAndImportFailures(string value, string code)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var requests = 0;
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; import \"{value}\"; message Root {{}}"),
                new()
                {
                    ResolveReference = (_, _) =>
                    {
                        requests++;
                        throw new InvalidOperationException("Invalid literals must fail before acquisition.");
                    }
                });
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
            await Assert.That(requests).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("\\u0041", "A")]
    [Arguments("\\u00e9", "\u00e9")]
    [Arguments("\\uD7FF", "\uD7FF")]
    [Arguments("\\uE000", "\uE000")]
    [Arguments("\\uFFFF", "\uFFFF")]
    [Arguments("\\U00000041", "A")]
    [Arguments("\\U0000d7ff", "\uD7FF")]
    [Arguments("\\U0000e000", "\uE000")]
    [Arguments("\\U00010000", "\U00010000")]
    [Arguments("\\U0001F600", "\U0001F600")]
    [Arguments("\\U000FFFFF", "\U000FFFFF")]
    [Arguments("\\U00100000", "\U00100000")]
    [Arguments("\\U0010FFFF", "\U0010FFFF")]
    public async Task UnicodeEscapesResolveExactScalarImportPaths(string escaped, string decoded)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var quote in new[] { '"', '\'' })
            {
                var requests = new List<string>();
                var expected = $"child{decoded}.proto";
                var source = $"syntax=\"proto{syntax}\"; import {quote}child{escaped}{quote} {quote}.proto{quote}; " +
                    $"message Root {{ optional Child value=1; }}";
                var result = await new BuiltInDocumentValidator().ValidateAsync(
                    $"Protobuf/{syntax}", Encoding.UTF8.GetBytes(source), new()
                    {
                        ResolveReference = (name, _) =>
                        {
                            requests.Add(name);
                            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == expected
                                ? Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; message Child {{}}")
                                : (ReadOnlyMemory<byte>?)null);
                        }
                    });
                await Assert.That(requests.Count).IsEqualTo(1);
                await Assert.That(requests[0]).IsEqualTo(expected);
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
        }
    }

    [Test]
    [Arguments("\\uD800")]
    [Arguments("\\uDBFF")]
    [Arguments("\\uDC00")]
    [Arguments("\\uDFFF")]
    [Arguments("\\uD83D\\uDE00")]
    [Arguments("\\U0000D800")]
    [Arguments("\\U0000DFFF")]
    [Arguments("\\U00110000")]
    [Arguments("\\U01000000")]
    [Arguments("\\UFFFFFFFF")]
    public async Task UnicodeEscapesRejectNonScalarValuesBeforeAcquisition(string escaped)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var quote in new[] { '"', '\'' })
            {
                var requests = 0;
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; import {quote}{escaped}.proto{quote}; message Root {{}}"),
                    new()
                    {
                        ResolveReference = (_, _) =>
                        {
                            requests++;
                            throw new InvalidOperationException("A non-scalar escape must fail before acquisition.");
                        }
                    });
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
                await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.string");
                await Assert.That(requests).IsEqualTo(0);
            }
        }
    }

    [Test]
    [Arguments("\\u006f", "oldName")]
    [Arguments("\\U0000006F", "oldName")]
    [Arguments("e\\u0301", "\u00e9ldName")]
    public async Task UnicodeReservedNamesUseDecodedOrdinalValues(string prefix, string name)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; message Root {{ reserved \"{prefix}\" \"ldName\", \"{name}\"; }}"));
            var equivalent = prefix != "e\\u0301";
            await Assert.That(result.Status).IsEqualTo(equivalent
                ? DocumentValidationStatus.Invalid : DocumentValidationStatus.Valid);
            if (equivalent) { await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.reserved"); }
            else { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        }
    }

    [Test]
    [Arguments("\\u0000")]
    [Arguments("\\u000A")]
    [Arguments("\\U0000000D")]
    [Arguments("\\uFFFF")]
    [Arguments("\\U0010FFFF")]
    public async Task UnicodeEscapesRemainQuotedStringAndBytesDefaults(string escaped)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax=\"proto2\"; message Root {{ " +
                $"optional string text=1 [default=\"{escaped}\"]; optional bytes data=2 [default='{escaped}']; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("\\u0070roto")]
    [Arguments("\\U00000070roto")]
    [Arguments("proto\\u003")]
    public async Task UnicodeEscapesCannotReplaceLiteralSyntaxDiscriminator(string prefix)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax=\"{prefix}{syntax}\"; message Root {{}}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.syntax");
        }
    }

    [Test]
    [Arguments("\\u002Fchild.proto")]
    [Arguments("\\u005Cchild.proto")]
    [Arguments("\\u002E/child.proto")]
    [Arguments("\\u002E\\u002E/child.proto")]
    [Arguments("child\\U0000002F\\U0000002Fother.proto")]
    public async Task UnicodeImportEscapesRetainDecodedPathConstraints(string path)
    {
        var requests = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax=\"proto3\"; import \"{path}\"; message Root {{}}"), new()
            {
                ResolveReference = (_, _) =>
                {
                    requests++;
                    throw new InvalidOperationException("Invalid decoded paths must fail before acquisition.");
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.import");
        await Assert.That(requests).IsEqualTo(0);
    }

    [Test]
    [Arguments("option deprecated=\"\\u0074rue\"; message Root {}", "protobuf.option")]
    [Arguments("message Root { reserved \"\\u006F\" \"ldName\"; optional string oldName=1; }", "protobuf.reserved")]
    [Arguments("message Root { optional bool flag=1 [default=\"\\U00000074rue\"]; }", "protobuf.default")]
    [Arguments("enum E { ZERO=0; } message Root { optional E value=1 [default=\"\\u005AERO\"]; }", "protobuf.default")]
    public async Task UnicodeDecodedValuesPreserveTokenKindsAndReservedFields(string body, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax=\"proto2\"; {body}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("\\x41", "A", "")]
    [Arguments("\\X61", "a", "")]
    [Arguments("\\x1g", "\u0001g", "")]
    [Arguments("\\x413", "A3", "")]
    [Arguments("\\101", "A", "")]
    [Arguments("\\1g", "\u0001g", "")]
    [Arguments("\\12g", "\ng", "")]
    [Arguments("\\1234", "S4", "")]
    [Arguments("\\108", "\b8", "")]
    [Arguments("\\xC3\\xA9", "\u00e9", "")]
    [Arguments("\\303\\251", "\u00e9", "")]
    [Arguments("\\xC3' '", "\u00e9", "\\251")]
    [Arguments("\\xF0' '", "\U0001F600", "\\237\\x98\\200")]
    public async Task ByteEscapesResolveDecodedAndConcatenatedUtf8ImportPaths(
        string escaped, string decoded, string suffix)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            foreach (var quote in new[] { '"', '\'' })
            {
                var spelling = (escaped + suffix).Replace("'", quote.ToString(), StringComparison.Ordinal);
                var expected = $"child{decoded}.proto";
                var requests = new List<string>();
                var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                    Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; import {quote}child{spelling}.proto{quote}; " +
                        "message Root { optional Child value=1; }"), new()
                    {
                        ResolveReference = (name, _) =>
                        {
                            requests.Add(name);
                            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == expected
                                ? Encoding.UTF8.GetBytes($"syntax=\"proto{syntax}\"; message Child {{}}")
                                : (ReadOnlyMemory<byte>?)null);
                        }
                    });
                await Assert.That(requests.Count).IsEqualTo(1);
                await Assert.That(requests[0]).IsEqualTo(expected);
                await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
        }
    }

    [Test]
    [Arguments("\\x80")]
    [Arguments("\\xFF")]
    [Arguments("\\377")]
    [Arguments("\\xC3")]
    [Arguments("\\xC0\\xAF")]
    [Arguments("\\xED\\xA0\\x80")]
    [Arguments("\\xF4\\x90\\x80\\x80")]
    [Arguments("\\xF5\\x80\\x80\\x80")]
    public async Task ArbitraryByteDefaultsDoNotBecomeInvalidUtf8StringDefaults(string escaped)
    {
        var validator = new BuiltInDocumentValidator();
        var bytes = await validator.ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax=\"proto2\"; message Root {{ optional bytes value=1 [default=\"{escaped}\"]; }}"));
        await Assert.That(bytes.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(bytes.Diagnostics.Count).IsEqualTo(0);
        var text = await validator.ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax=\"proto2\"; message Root {{ optional string value=1 [default=\"{escaped}\"]; }}"));
        await Assert.That(text.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(text.Diagnostics[0].Code).IsEqualTo("protobuf.string");
    }

    [Test]
    [Arguments("\\x80")]
    [Arguments("\\377")]
    [Arguments("\\xC3' 'x")]
    public async Task InvalidUtf8ByteEscapesFailEveryTextConsumer(string escaped)
    {
        foreach (var body in new[]
        {
            $"import '{escaped}.proto'; message Root {{}}",
            $"option java_package='{escaped}'; message Root {{}}",
            $"message Root {{ reserved '{escaped}'; }}",
            $"message Root {{ string value=1 [json_name='{escaped}']; }}"
        })
        {
            var requests = 0;
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
                Encoding.UTF8.GetBytes($"syntax=\"proto3\"; {body}"), new()
                {
                    ResolveReference = (_, _) =>
                    {
                        requests++;
                        throw new InvalidOperationException("Invalid UTF-8 must fail before acquisition.");
                    }
                });
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.string");
            await Assert.That(requests).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("\\400")]
    [Arguments("\\777")]
    public async Task OctalEscapesCannotExceedOneByte(string escaped)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
            Encoding.UTF8.GetBytes($"syntax=\"proto2\"; message Root {{ optional bytes value=1 [default='{escaped}']; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.string");
    }

    [Test]
    [Arguments("\\x6F")]
    [Arguments("\\157")]
    public async Task ByteEscapedReservedNamesCompareDecodedValues(string spelling)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax=\"proto3\"; message Root {{ reserved \"{spelling}\" \"ldName\"; string oldName=1; }}"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.reserved");
    }

    [Test]
    [Arguments("\\x2Fchild.proto")]
    [Arguments("\\057child.proto")]
    [Arguments("\\134child.proto")]
    [Arguments("\\x2E\\056/child.proto")]
    public async Task ByteEscapedImportPathsCannotBypassRelativePathChecks(string spelling)
    {
        var requests = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3",
            Encoding.UTF8.GetBytes($"syntax=\"proto3\"; import '{spelling}'; message Root {{}}"), new()
            {
                ResolveReference = (_, _) =>
                {
                    requests++;
                    throw new InvalidOperationException("Invalid decoded paths must not be acquired.");
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.import");
        await Assert.That(requests).IsEqualTo(0);
    }

    [Test]
    [Arguments("\\160roto")]
    [Arguments("\\x70roto")]
    public async Task ByteEscapesCannotReplaceLiteralSyntaxDiscriminator(string prefix)
    {
        foreach (var syntax in new[] { 2, 3 })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync($"Protobuf/{syntax}",
                Encoding.UTF8.GetBytes($"syntax='{prefix}{syntax}'; message Root {{}}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("protobuf.syntax");
        }
    }

    [Test]
    [Arguments("\\x41")]
    [Arguments("\\101")]
    [Arguments("\\u0041")]
    [Arguments("\\U00000041")]
    public async Task EscapedLiteralDecodingConsumesTheSharedWorkBudget(string escape)
    {
        var repeated = string.Concat(Enumerable.Repeat(escape, 2048));
        var document = Encoding.UTF8.GetBytes($"syntax='proto2'; message Root {{ optional bytes value=1 [default='{repeated}']; }}");
        var validator = new BuiltInDocumentValidator();
        var limited = await validator.ValidateAsync("Protobuf/2", document, new() { MaxWork = 1000 });
        await Assert.That(limited.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(limited.Diagnostics[0].Code).IsEqualTo("limit.work");
        var sufficient = await validator.ValidateAsync("Protobuf/2", document, new() { MaxWork = 50_000 });
        await Assert.That(sufficient.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(sufficient.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("0")]
    [Arguments("-0")]
    [Arguments("+0")]
    [Arguments("123")]
    [Arguments("-123")]
    [Arguments("+123")]
    [Arguments("077")]
    [Arguments("-077")]
    [Arguments("+077")]
    [Arguments("0x1f")]
    [Arguments("0X1E")]
    [Arguments("-0x1f")]
    [Arguments("+0X1E")]
    [Arguments("inf")]
    [Arguments("-inf")]
    [Arguments("+inf")]
    [Arguments("nan")]
    [Arguments("-nan")]
    [Arguments("+nan")]
    [Arguments("09.0")]
    [Arguments("09e1")]
    [Arguments("+09e-1")]
    public async Task FloatingDefaultsAcceptIntegerRadicesAndSignedSpecials(string value)
    {
        foreach (var type in new[] { "float", "double" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
                Encoding.UTF8.GetBytes($"syntax=\"proto2\"; message M {{ optional {type} value=1 [default={value}]; }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("09")]
    [Arguments("-09")]
    [Arguments("+09")]
    [Arguments("078")]
    [Arguments("0x")]
    [Arguments("0x1g")]
    [Arguments("0x1.0")]
    [Arguments("0x1p2")]
    [Arguments("0x1e+2")]
    [Arguments("INF")]
    [Arguments("NaN")]
    [Arguments("Infinity")]
    [Arguments("\"0x1f\"")]
    [Arguments("\"+inf\"")]
    [Arguments("'nan'")]
    public async Task FloatingDefaultsRejectMalformedRadicesAndNonNativeSpecials(string value)
    {
        foreach (var type in new[] { "float", "double" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/2",
                Encoding.UTF8.GetBytes($"syntax=\"proto2\"; message M {{ optional {type} value=1 [default={value}]; }}"));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
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
