// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Validation;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class BuiltInValidationAdapterTests
{
    private const string ValidatedModel = """
        {"groups":{"sets":{"singular":"set","resources":{"schemas":{
          "singular":"schema","validateformat":true,"validatecompatibility":true,"strictvalidation":false
        }}}}}
        """;

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    public async Task DisabledCompatibilityAvoidsDependencyRechecksWhileFormatValidationRemainsEnabled(
        bool validateCompatibility, bool strict, bool omitted)
    {
        const string reference = "https://schemas.example/item";
        var calls = 0;
        var model = omitted
            ? ValidatedModel.Replace("\"validatecompatibility\":true,", "", StringComparison.Ordinal)
            : ValidatedModel.Replace("\"validatecompatibility\":true", "\"validatecompatibility\":" +
                (validateCompatibility ? "true" : "false"), StringComparison.Ordinal);
        var compiled = RegistryModel.Compile(RegistryJson.Parse(model));
        await Assert.That(compiled.Groups["sets"].Resources["schemas"].ValidateFormat).IsTrue();
        await Assert.That(compiled.Groups["sets"].Resources["schemas"].ValidateCompatibility).IsEqualTo(validateCompatibility);
        var engine = ValidatingEngine(strict: strict, validateCompatibility: omitted ? null : validateCompatibility, validationOptions: new()
        {
            ResolveReference = (uri, _) =>
            {
                if (uri != reference) { throw new InvalidOperationException("An unexpected dependency was requested."); }
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""{"type":"string"}"""));
            }
        });
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"v1","format":"JsonSchema/draft-07","schema":{"$ref":"https://schemas.example/item"},
             "meta":{"compatibility":"full"}}
            """);
        await Assert.That(calls).IsEqualTo(validateCompatibility ? 2 : 1);
        await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v2","format":"JsonSchema/draft-07","schema":{"$ref":"https://schemas.example/item"}}
            """);
        await Assert.That(calls).IsEqualTo(validateCompatibility ? 6 : 3);
        await Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/versions/v1$details", """
            {"schema":{"$ref":"https://schemas.example/item"}}
            """);
        await Assert.That(calls).IsEqualTo(validateCompatibility ? 10 : 5);
        foreach (var version in new[] { "v1", "v2" })
        {
            var result = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/" + version + "$details");
            await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
            await Assert.That(result.Metadata.RootElement.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
            await Assert.That(result.Metadata.RootElement.TryGetProperty("compatibilityvalidated", out var flag))
                .IsEqualTo(validateCompatibility);
            if (validateCompatibility) { await Assert.That(flag.GetBoolean()).IsTrue(); }
            await Assert.That(result.Metadata.RootElement.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
            var stored = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/" + version);
            using var bytes = stored.Document!.OpenRead();
            using var reader = new StreamReader(bytes, Encoding.UTF8);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("""{"$ref":"https://schemas.example/item"}""");
        }
        var meta = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("compatibility").GetString()).IsEqualTo("full");
        await Assert.That(calls).IsEqualTo(validateCompatibility ? 10 : 5);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AbsentCompatibilityModeSkipsRechecksAndRemovingItClearsPriorResults(bool strict)
    {
        var calls = 0;
        var engine = ValidatingEngine(strict: strict, validationOptions: new()
        {
            ResolveReference = (reference, _) =>
            {
                if (reference != "https://schemas.example/item") { throw new InvalidOperationException("An unexpected dependency was requested."); }
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""{"type":"string"}"""));
            }
        });
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"v1","format":"JsonSchema/draft-07","schema":{"$ref":"https://schemas.example/item"}}
            """);
        await Assert.That(calls).IsEqualTo(1);
        await AssertVersionsAsync(false, "v1");
        await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v2","format":"JsonSchema/draft-07","schema":{"$ref":"https://schemas.example/item"}}
            """);
        await Assert.That(calls).IsEqualTo(3);
        await AssertVersionsAsync(false, "v1", "v2");
        await Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta", """{"compatibility":"full"}""");
        await Assert.That(calls).IsEqualTo(7);
        await AssertVersionsAsync(true, "v1", "v2");
        await Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta", """{"compatibility":null}""");
        await Assert.That(calls).IsEqualTo(9);
        await AssertVersionsAsync(false, "v1", "v2");
        await Assert.That(calls).IsEqualTo(9);

        async Task AssertVersionsAsync(bool compatibilityRequested, params string[] versions)
        {
            var meta = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta");
            await Assert.That(meta.Metadata!.RootElement.TryGetProperty("compatibility", out var mode))
                .IsEqualTo(compatibilityRequested);
            if (compatibilityRequested) { await Assert.That(mode.GetString()).IsEqualTo("full"); }
            foreach (var version in versions)
            {
                var result = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/" + version + "$details");
                await Assert.That(result.Metadata!.RootElement.GetProperty("format").GetString()).IsEqualTo("JsonSchema/draft-07");
                await Assert.That(result.Metadata.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
                await Assert.That(result.Metadata.RootElement.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
                await Assert.That(result.Metadata.RootElement.TryGetProperty("compatibilityvalidated", out var flag))
                    .IsEqualTo(compatibilityRequested);
                if (compatibilityRequested) { await Assert.That(flag.GetBoolean()).IsTrue(); }
                await Assert.That(result.Metadata.RootElement.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
                var stored = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/" + version);
                using var bytes = stored.Document!.OpenRead();
                using var reader = new StreamReader(bytes, Encoding.UTF8);
                await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("""{"$ref":"https://schemas.example/item"}""");
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnsupportedCompatibilityModeIsUnknownRatherThanAnEstablishedViolation(bool strict)
    {
        var engine = ValidatingEngine(strict: strict, allowCapabilityUpdates: true);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"v1","format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"full"}}
            """);
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"compatibilities":{"Avro/1.11.0":["full"]}}""");
        if (strict)
        {
            await ExpectCode(() => Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta",
                """{"compatibility":"backward"}"""), "compatibility_unknown");
        }
        else
        {
            await Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta", """{"compatibility":"backward"}""");
        }
        var meta = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("compatibility").GetString())
            .IsEqualTo(strict ? "full" : "backward");
        var details = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/v1$details");
        await Assert.That(details.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(details.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsEqualTo(strict);
        await Assert.That(details.Metadata.RootElement.TryGetProperty("compatibilityvalidatedreason", out var reason)).IsEqualTo(!strict);
        if (!strict) { await Assert.That(reason.GetString()).IsNotNullOrEmpty(); }
        var stored = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/v1");
        using var bytes = stored.Document!.OpenRead();
        using var reader = new StreamReader(bytes, Encoding.UTF8);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("\"int\"");
    }

    [Test]
    [Arguments("valid", false)]
    [Arguments("valid", true)]
    [Arguments("unsupported", false)]
    [Arguments("unsupported", true)]
    [Arguments("absent", false)]
    [Arguments("absent", true)]
    public async Task ClientValidationOverridesCannotChangeAuthoritativeResults(string state, bool exactVersion)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = ValidatingEngine(persistence: store);
        var format = state switch
        {
            "valid" => ",\"format\":\"Avro/1.11.0\"",
            "unsupported" => ",\"format\":\"unknown/v1\"",
            _ => ""
        };
        var path = "/sets/g/schemas/s" + (exactVersion ? "/versions/v1" : "") + "$details";
        await Send(engine, RegistryAction.Replace, path,
            "{\"versionid\":\"v1\",\"schema\":\"int\"" + (exactVersion ? "" : ",\"meta\":{\"compatibility\":\"full\"}") + format + """
            ,"formatvalidated":{"forged":true},"compatibilityvalidated":"forged",
            "formatvalidatedreason":false,"compatibilityvalidatedreason":[]}
            """);
        if (exactVersion)
        {
            var created = (await Send(engine, RegistryAction.Read, path)).Metadata!.RootElement;
            await Assert.That(created.TryGetProperty("formatvalidated", out var formatFlag)).IsEqualTo(state != "absent");
            await Assert.That(created.TryGetProperty("formatvalidatedreason", out var formatReason)).IsEqualTo(state == "unsupported");
            if (state != "absent") { await Assert.That(formatFlag.GetBoolean()).IsEqualTo(state == "valid"); }
            if (state == "unsupported") { await Assert.That(formatReason.GetString()).IsNotNullOrEmpty(); }
            await Assert.That(created.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
            await Assert.That(created.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
            await Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta", """{"compatibility":"full"}""");
        }
        var before = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/v1$details")).Metadata!.RootElement;
        foreach (var aspect in new[] { "format", "compatibility" })
        {
            await Assert.That(before.TryGetProperty(aspect + "validated", out var flag)).IsEqualTo(state != "absent");
            await Assert.That(before.TryGetProperty(aspect + "validatedreason", out var reason)).IsEqualTo(state == "unsupported");
            if (state != "absent") { await Assert.That(flag.GetBoolean()).IsEqualTo(state == "valid"); }
            if (state == "unsupported") { await Assert.That(reason.GetString()).IsNotNullOrEmpty(); }
        }
        var fields = new[] { "formatvalidated", "compatibilityvalidated", "formatvalidatedreason", "compatibilityvalidatedreason" };
        foreach (var field in fields)
        {
            foreach (var forged in new[] { "null", """{"forged":true}""", field.EndsWith("reason", StringComparison.Ordinal) ? "\"forged\"" : state == "valid" ? "false" : "true" })
            {
                await Send(engine, RegistryAction.Patch, path, "{\"" + field + "\":" + forged + "}");
                var result = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/v1$details")).Metadata!.RootElement;
                foreach (var output in fields)
                {
                    var present = before.TryGetProperty(output, out var original);
                    await Assert.That(result.TryGetProperty(output, out var current)).IsEqualTo(present);
                    if (present) { await Assert.That(current.GetRawText()).IsEqualTo(original.GetRawText()); }
                }
            }
        }
        var restarted = ValidatingEngine(persistence: store);
        var persisted = (await Send(restarted, RegistryAction.Read, "/sets/g/schemas/s/versions/v1$details")).Metadata!.RootElement;
        foreach (var field in fields)
        {
            var present = before.TryGetProperty(field, out var original);
            await Assert.That(persisted.TryGetProperty(field, out var current)).IsEqualTo(present);
            if (present) { await Assert.That(current.GetRawText()).IsEqualTo(original.GetRawText()); }
        }
        var stored = await Send(restarted, RegistryAction.Read, "/sets/g/schemas/s/versions/v1");
        using var bytes = stored.Document!.OpenRead();
        using var reader = new StreamReader(bytes, Encoding.UTF8);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("\"int\"");
    }

    [Test]
    public async Task InvalidSyntaxRejectsPublicationInsteadOfBecomingAValidationFlag()
    {
        var engine = ValidatingEngine();
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"format":"JsonSchema/draft-07","schema":{"type":"not-a-type"}}
            """), "format_violation");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("setscount").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var accepted = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"format":"JsonSchema/draft-07","schema":{"type":"string"}}
            """);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(accepted.Metadata.RootElement.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/sets/g/schemas/s$details", """
            {"schema":{"type":7}}
            """), "format_violation");
        var unchanged = await Send(engine, RegistryAction.Read, "/sets/g/schemas/s$details", null,
            new KeyValuePair<string, string?>("inline", "schema"));
        await Assert.That(unchanged.Metadata!.RootElement.GetProperty("schema").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(unchanged.Metadata.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments("backward", "\"int\"", "\"long\"")]
    [Arguments("backward_transitive", "\"int\"", "\"long\"")]
    [Arguments("forward", "\"long\"", "\"int\"")]
    [Arguments("forward_transitive", "\"long\"", "\"int\"")]
    [Arguments("full", "\"string\"", "\"bytes\"")]
    [Arguments("full_transitive", "\"string\"", "\"bytes\"")]
    public async Task AvroCompatibilityUsesTheDeclaredDirectionAndAncestry(string mode, string ancestor, string candidate)
    {
        var engine = ValidatingEngine(strict: true);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details",
            "{\"versionid\":\"v1\",\"format\":\"Avro/1.11.0\",\"schema\":" + ancestor +
            ",\"meta\":{\"compatibility\":\"" + mode + "\"}}");
        var accepted = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details",
            "{\"versionid\":\"v2\",\"format\":\"Avro/1.11.0\",\"schema\":" + candidate + "}");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(accepted.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        await Assert.That(accepted.Metadata.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("v1");
        await Assert.That(accepted.Metadata.RootElement.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
    }

    [Test]
    public async Task IncompatibleAvroRejectsWithoutAddingVersionsEvenWhenStrictValidationIsFalse()
    {
        var engine = ValidatingEngine();
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"v1","format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"forward"}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v2","format":"Avro/1.11.0","schema":"long"}
            """), "compatibility_violation");
        var resource = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s$details")).Metadata!.RootElement;
        await Assert.That(resource.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(resource.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
    }

    [Test]
    public async Task RetentionCannotEraseAnAncestorBeforeCompatibilityIsChecked()
    {
        var engine = ValidatingEngine(maxVersions: 1);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"forward"}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"format":"Avro/1.11.0","schema":"long"}
            """), "compatibility_violation");
        var retained = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s$details")).Metadata!.RootElement;
        await Assert.That(retained.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(retained.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        var next = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"format":"Avro/1.11.0","schema":"int"}
            """);
        await Assert.That(next.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("2");
        await Assert.That(next.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/1$details"), "not_found");
    }

    [Test]
    [Arguments("JsonSchema/draft-07", """{"type":"string"}""")]
    [Arguments("JsonSchema/draft/2019-09", """{"type":"string"}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"type":"string"}""")]
    [Arguments("XSD/1.0", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="name" type="xs:string"/></xs:schema>""")]
    [Arguments("Avro/1.8.2", "\"int\"")]
    [Arguments("Avro/1.11.0", "\"int\"")]
    [Arguments("Protobuf/2", """syntax = "proto2"; message Row { optional int32 id = 1; }""")]
    [Arguments("Protobuf/3", """syntax = "proto3"; message Row { int32 id = 1; }""")]
    [Arguments("gRPC/proto3", """syntax = "proto3"; message Row {} service S { rpc Get(Row) returns(Row); }""")]
    [Arguments("JsonStructure", """{"$schema":"https://json-structure.org/meta/core/v0/#","$id":"urn:test:row","name":"Row","type":"string"}""")]
    [Arguments("JsonStructure/draft-04", """{"$schema":"https://json-structure.org/meta/core/v0/#","$id":"urn:test:row","name":"Row","type":"string"}""")]
    public async Task AllQualifiedSyntaxFamiliesProduceValidatedMetadata(string format, string document)
    {
        var engine = ValidatingEngine(strict: true);
        var result = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details",
            "{\"format\":\"" + format + "\",\"schemabase64\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(document)) + "\"}");
        await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(result.Metadata.RootElement.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(result.Metadata.RootElement.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
    }

    [Test]
    public async Task GrpcSourceValidationIsAdvertisedByTheOptInAdapter()
    {
        var adapter = new BuiltInRegistryResourceValidator();
        await Assert.That(adapter.Formats.Contains("gRPC/proto3")).IsTrue();
        await Assert.That(adapter.Compatibilities.ContainsKey("gRPC/proto3")).IsTrue();
        await Assert.That(adapter.Compatibilities["gRPC/proto3"].Contains("full")).IsTrue();
        await Assert.That(adapter.Formats.Contains("gRPC/descriptor-set")).IsFalse();
        await Assert.That(adapter.Formats.Contains("gRPC/proto2")).IsFalse();
        await Assert.That(adapter.Formats.Contains("gRPC/editions")).IsFalse();
    }

    [Test]
    public async Task GrpcPublicationUsesTheConfiguredSourceBaseForAuthorizedImports()
    {
        var calls = new List<string>();
        var engine = ValidatingEngine(strict: true, validationOptions: new()
        {
            DocumentUri = new Uri("https://example.test/apis/root.proto"),
            ResolveReference = (name, _) =>
            {
                calls.Add(name);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(name == "https://example.test/apis/types.proto"
                    ? Encoding.UTF8.GetBytes("syntax='proto3'; package p; message Row {}") : null);
            }
        });
        var metadata = "{\"format\":\"gRPC/proto3\",\"schemabase64\":\"" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "syntax='proto3'; import 'types.proto'; service S { rpc Get(p.Row) returns(p.Row); }")) + "\"}";
        var result = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", metadata);
        await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(result.Metadata.RootElement.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(calls.SequenceEqual(["https://example.test/apis/types.proto"])).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GrpcImportCyclesRemainUncheckedOrRejectStrictPublication(bool strict)
    {
        var engine = ValidatingEngine(strict: strict, validationOptions: new()
        {
            ResolveReference = (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                Encoding.UTF8.GetBytes("syntax='proto3'; import 'root.proto';"))
        });
        var metadata = "{\"format\":\"gRPC/proto3\",\"schemabase64\":\"" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes("syntax='proto3'; import 'child.proto'; service S {}")) + "\"}";
        if (strict)
        {
            await ExpectCode(() => Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", metadata),
                "format_unknown");
            await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement
                .GetProperty("setscount").GetInt32()).IsEqualTo(0);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        }
        else
        {
            var result = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", metadata);
            await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsFalse();
            await Assert.That(result.Metadata.RootElement.GetProperty("formatvalidatedreason").GetString()!)
                .Contains("dependency.cycle");
        }
    }

    [Test]
    [Arguments("""syntax='proto3'; message Row {}""")]
    [Arguments("""syntax='proto2'; service S {}""")]
    [Arguments("""syntax='proto3'; service S { rpc Get(Missing) returns(Missing); }""")]
    public async Task InvalidGrpcSourceRejectsWithoutPublishingMetadataOrEvents(string document)
    {
        foreach (var strict in new[] { false, true })
        {
            var engine = ValidatingEngine(strict: strict);
            var metadata = "{\"format\":\"gRPC/proto3\",\"schemabase64\":\"" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(document)) + "\"}";
            await ExpectCode(() => Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", metadata),
                "format_violation");
            await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement
                .GetProperty("setscount").GetInt32()).IsEqualTo(0);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GrpcCompatibilityProvesOnlyExactBytesAndPreservesFailedPublication(bool strict)
    {
        const string original = "syntax='proto3'; service S {}";
        const string changed = "syntax='proto3'; service S { }";
        var engine = ValidatingEngine(strict: strict);
        string Metadata(string version, string source, bool meta = false) =>
            "{\"versionid\":\"" + version + "\",\"format\":\"gRPC/proto3\",\"schemabase64\":\"" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(source)) + "\"" +
            (meta ? ",\"meta\":{\"compatibility\":\"full\"}" : "") + "}";
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", Metadata("v1", original, true));
        var same = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", Metadata("v2", original));
        await Assert.That(same.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(same.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        if (strict)
        {
            await ExpectCode(() => Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details",
                Metadata("v3", changed)), "compatibility_unknown");
            var retained = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s$details")).Metadata!.RootElement;
            await Assert.That(retained.GetProperty("versionid").GetString()).IsEqualTo("v2");
            await Assert.That(retained.GetProperty("versionscount").GetInt32()).IsEqualTo(2);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(2);
        }
        else
        {
            var accepted = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", Metadata("v3", changed));
            await Assert.That(accepted.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
            await Assert.That(accepted.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
            await Assert.That(accepted.Metadata.RootElement.GetProperty("compatibilityvalidatedreason").GetString()!)
                .Contains("compatibility.policy");
        }
    }

    [Test]
    [Arguments("JsonSchema/draft-04", "{}")]
    [Arguments("Avro/1.9", "\"int\"")]
    [Arguments("XSD/1.1", "<schema/>")]
    public async Task UnsupportedVariantsRemainUncheckedWithReasonsAndStrictModeRejects(string format, string document)
    {
        var metadata = "{\"format\":\"" + format + "\",\"schemabase64\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(document)) + "\"}";
        var accepted = await Send(ValidatingEngine(), RegistryAction.Replace, "/sets/g/schemas/s$details", metadata);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(accepted.Metadata.RootElement.GetProperty("formatvalidatedreason").GetString()!).Contains("format.unsupported");
        var strict = ValidatingEngine(strict: true);
        await ExpectCode(() => Send(strict, RegistryAction.Replace, "/sets/g/schemas/s$details", metadata), "format_unknown");
        await Assert.That((await Send(strict, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("setscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task TransitiveCompatibilityFollowsActualAncestorsAndRejectsAnOlderAliasMismatch()
    {
        var engine = ValidatingEngine(strict: true);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"z-old","format":"Avro/1.11.0","schema":{"type":"record","name":"Old","fields":[]},"meta":{"compatibility":"backward"}}
            """);
        await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"a-mid","format":"Avro/1.11.0","schema":{"type":"record","name":"Mid","aliases":["Old"],"fields":[]}}
            """);
        var latest = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"m-new","format":"Avro/1.11.0","schema":{"type":"record","name":"New","aliases":["Mid"],"fields":[]}}
            """);
        await Assert.That(latest.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("a-mid");
        await Assert.That(latest.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta",
            """{"compatibility":"backward_transitive"}"""), "compatibility_violation");
        var meta = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta")).Metadata!.RootElement;
        await Assert.That(meta.GetProperty("compatibility").GetString()).IsEqualTo("backward");
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("m-new");
    }

    [Test]
    public async Task NonAvroCompatibilityEstablishesOnlyValidatedByteIdentity()
    {
        var engine = ValidatingEngine();
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"v1","format":"JsonSchema/draft-07","schemabase64":"eyJ0eXBlIjoic3RyaW5nIn0=","meta":{"compatibility":"full"}}
            """);
        var same = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v2","format":"JsonSchema/draft-07","schemabase64":"eyJ0eXBlIjoic3RyaW5nIn0="}
            """);
        await Assert.That(same.Metadata!.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        var reformatted = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v3","format":"JsonSchema/draft-07","schemabase64":"eyJ0eXBlIjogInN0cmluZyJ9"}
            """);
        await Assert.That(reformatted.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(reformatted.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(reformatted.Metadata.RootElement.GetProperty("compatibilityvalidatedreason").GetString()!).Contains("compatibility.policy");
    }

    [Test]
    public async Task StrictNonAvroChangesAreUnsupportedRatherThanReportedAsCompatible()
    {
        var engine = ValidatingEngine(strict: true);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"format":"JsonSchema/draft-07","schema":{"type":"string"},"meta":{"compatibility":"full"}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"format":"JsonSchema/draft-07","schema":{"type":"integer"}}
            """), "compatibility_unknown");
        var existing = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s$details")).Metadata!.RootElement;
        await Assert.That(existing.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(existing.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        await Assert.That(existing.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task RequiredHistoryIsNeverSilentlyTruncatedAtTheConfiguredLimit()
    {
        var engine = ValidatingEngine(validationOptions: new() { MaxHistory = 1 });
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"backward_transitive"}}
            """);
        var atLimit = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"format":"Avro/1.11.0","schema":"long"}
            """);
        await Assert.That(atLimit.Metadata!.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        var beyondLimit = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"format":"Avro/1.11.0","schema":"double"}
            """);
        await Assert.That(beyondLimit.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(beyondLimit.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(beyondLimit.Metadata.RootElement.GetProperty("compatibilityvalidatedreason").GetString()!).Contains("limit.history");
    }

    [Test]
    public async Task MissingSchemaReferencesRemainUncheckedWithoutAnyAutomaticAcquisition()
    {
        var engine = ValidatingEngine();
        var result = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"format":"JsonSchema/draft-07","schema":{"$ref":"https://schemas.invalid/unavailable"}}
            """);
        await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(result.Metadata.RootElement.GetProperty("formatvalidatedreason").GetString()!).Contains("reference.unresolved");
    }

    [Test]
    public async Task AbsoluteSelfReferencesUseTheDocumentUrlRatherThanTheMetadataUrl()
    {
        var engine = ValidatingEngine(strict: true);
        var result = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"format":"JsonSchema/draft-07","schema":{
              "$ref":"https://registry.example/sets/g/schemas/s/versions/1#/definitions/item",
              "definitions":{"item":{"type":"string"}}
            }}
            """);
        await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(result.Metadata.RootElement.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
    }

    [Test]
    public async Task CancelingExplicitSchemaReferenceResolutionLeavesNoPublishedEntities()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var engine = ValidatingEngine(validationOptions: new()
        {
            ResolveReference = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            }
        });
        var operation = engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/sets/g/schemas/s$details"))
        {
            Metadata = RegistryJson.Parse("""
                {"format":"JsonSchema/draft-07","schema":{"$ref":"https://schemas.invalid/explicit-callback"}}
                """)
        }, Writer(), cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await Assert.That(async () => await operation).Throws<OperationCanceledException>();
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("setscount").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    public async Task MissingEmptyAndMetadataOnlyDocumentsUseTheSameSyntaxSemantics(bool hasDocument, bool explicitEmpty)
    {
        var engine = ValidatingEngine(hasDocument: hasDocument);
        var input = explicitEmpty
            ? """{"format":"Avro/1.11.0","schemabase64":""}"""
            : """{"format":"Avro/1.11.0"}""";
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", input), "format_violation");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("setscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments(16, false)]
    [Arguments(17, true)]
    public async Task SyntaxByteBudgetIsInclusiveAndNeverMisreportedAsValid(int limit, bool expected)
    {
        var engine = ValidatingEngine(validationOptions: new() { MaxDocumentBytes = limit });
        var result = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"format":"JsonSchema/draft-07","schema":{"type":"string"}}
            """);
        await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsEqualTo(expected);
        if (!expected)
        {
            await Assert.That(result.Metadata.RootElement.GetProperty("formatvalidatedreason").GetString()!).Contains("limit.bytes");
        }
    }

    [Test]
    public async Task EngineSchemaWorkBudgetIsSharedAcrossResourcesInOneNestedWrite()
    {
        var limits = new RegistryLimits { MaxSchemaSteps = 6 };
        var single = await Send(ValidatingEngine(limits: limits), RegistryAction.Replace, "/sets/g/schemas/s$details",
            """{"format":"Avro/1.11.0","schema":"int"}""");
        await Assert.That(single.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        var engine = ValidatingEngine(limits: limits);
        await Send(engine, RegistryAction.Replace, "/", """
            {"sets":{"g":{"schemas":{
              "a":{"format":"Avro/1.11.0","schema":"int"},
              "b":{"format":"Avro/1.11.0","schema":"int"}
            }}}}
            """);
        foreach (var id in new[] { "a", "b" })
        {
            var resource = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/" + id + "$details")).Metadata!.RootElement;
            await Assert.That(resource.GetProperty("formatvalidated").GetBoolean()).IsFalse();
            await Assert.That(resource.GetProperty("formatvalidatedreason").GetString()!).Contains("limit.work");
        }
    }

    internal static RegistryEngine ValidatingEngine(bool strict = false, RegistryLimits? limits = null,
        DocumentValidationOptions? validationOptions = null, IRegistryPersistence? persistence = null,
        bool hasDocument = true, int maxVersions = 0, bool? validateCompatibility = true, bool allowCapabilityUpdates = false)
    {
        var model = strict ? ValidatedModel.Replace("\"strictvalidation\":false", "\"strictvalidation\":true", StringComparison.Ordinal) : ValidatedModel;
        if (validateCompatibility is null)
        {
            model = model.Replace("\"validatecompatibility\":true,", "", StringComparison.Ordinal);
        }
        else if (!validateCompatibility.Value)
        {
            model = model.Replace("\"validatecompatibility\":true", "\"validatecompatibility\":false", StringComparison.Ordinal);
        }

        if (!hasDocument)
        {
            model = model.Replace("\"singular\":\"schema\"", "\"singular\":\"schema\",\"hasdocument\":false", StringComparison.Ordinal);
        }

        if (maxVersions != 0)
        {
            model = model.Replace("\"singular\":\"schema\"", "\"singular\":\"schema\",\"maxversions\":" +
                maxVersions.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return new(new()
        {
            RegistryId = "validated",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(model)),
            ResourceValidator = new BuiltInRegistryResourceValidator(validationOptions),
            AllowCapabilityUpdates = allowCapabilityUpdates,
            Limits = limits ?? new()
        }, persistence ?? new InMemoryRegistryPersistence(), new PermitPolicy());
    }
}
