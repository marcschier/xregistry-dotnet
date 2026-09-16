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
        bool hasDocument = true, int maxVersions = 0)
    {
        var model = strict ? ValidatedModel.Replace("\"strictvalidation\":false", "\"strictvalidation\":true", StringComparison.Ordinal) : ValidatedModel;
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
            Limits = limits ?? new()
        }, persistence ?? new InMemoryRegistryPersistence(), new PermitPolicy());
    }
}
