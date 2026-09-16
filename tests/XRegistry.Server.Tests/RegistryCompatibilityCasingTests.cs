// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.BuiltInValidationAdapterTests;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryCompatibilityCasingTests
{
    [Test]
    [Arguments("backward")]
    [Arguments("BACKWARD")]
    public async Task BackwardCompatibilityCasingFlowsThroughAvroValidation(string mode)
    {
        var engine = ValidatingEngine(strict: true);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details",
            "{\"versionid\":\"v1\",\"format\":\"Avro/1.11.0\",\"schema\":\"int\",\"meta\":{\"compatibility\":\"" + mode + "\"}}");

        var accepted = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v2","format":"Avro/1.11.0","schema":"long"}
            """);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(accepted.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        await Assert.That(accepted.Metadata.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("v1");
        await Assert.That(accepted.Metadata.RootElement.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();

        await ExpectCode(() => Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v3","format":"Avro/1.11.0","schema":"int"}
            """), "compatibility_violation");
        var stored = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s$details")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("versionid").GetString()).IsEqualTo("v2");
        await Assert.That(stored.GetProperty("versionscount").GetInt32()).IsEqualTo(2);
    }

    [Test]
    [Arguments("BACKWARD", "\"int\"", "\"long\"")]
    [Arguments("BACKWARD_TRANSITIVE", "\"int\"", "\"long\"")]
    [Arguments("FORWARD", "\"long\"", "\"int\"")]
    [Arguments("FORWARD_TRANSITIVE", "\"long\"", "\"int\"")]
    [Arguments("FULL", "\"string\"", "\"bytes\"")]
    [Arguments("FULL_TRANSITIVE", "\"string\"", "\"bytes\"")]
    public async Task AllSystemCompatibilityCasingsReachTheAdvertisedRules(string mode, string ancestor, string candidate)
    {
        var engine = ValidatingEngine(strict: true);
        var capabilities = (await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement;
        var modes = capabilities.GetProperty("compatibilities").GetProperty("Avro/1.11.0")
            .EnumerateArray().Select(static value => value.GetString()!).ToArray();
        await Assert.That(modes).IsEquivalentTo(
            ["backward", "backward_transitive", "forward", "forward_transitive", "full", "full_transitive"],
            StringComparer.OrdinalIgnoreCase);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details",
            "{\"versionid\":\"v1\",\"format\":\"Avro/1.11.0\",\"schema\":" + ancestor +
            ",\"meta\":{\"compatibility\":\"" + mode + "\"}}");

        var accepted = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details",
            "{\"versionid\":\"v2\",\"format\":\"Avro/1.11.0\",\"schema\":" + candidate + "}");

        await Assert.That(accepted.Metadata!.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        await Assert.That(accepted.Metadata.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("v1");
        await Assert.That(accepted.Metadata.RootElement.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
        var meta = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta")).Metadata!.RootElement;
        await Assert.That(meta.GetProperty("compatibility").GetString()).IsEqualTo(mode);
    }

    [Test]
    public async Task AdvertisedCompatibilityCasingDoesNotEnableAnUnselectedMode()
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "compatibility-casing",
            PublicRoot = new Uri("https://registry.example"),
            AllowCapabilityUpdates = true,
            ResourceValidator = new BuiltInRegistryResourceValidator(),
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"sets":{"singular":"set","resources":{"schemas":{
                  "singular":"schema","validateformat":true,"validatecompatibility":true,"strictvalidation":true
                }}}}}
                """))
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"compatibilities":{"Avro/*":["BaCkWaRd"]}}""");
        var capabilities = (await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement;
        await Assert.That(capabilities.GetProperty("compatibilities").GetProperty("Avro/1.11.0")
            .EnumerateArray().Select(static value => value.GetString()!).ToArray())
            .IsEquivalentTo(["backward"], StringComparer.OrdinalIgnoreCase);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"v1","format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"BACKWARD"}}
            """);
        var accepted = await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v2","format":"Avro/1.11.0","schema":"long"}
            """);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();

        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta",
            """{"compatibility":"FORWARD"}"""), "compatibility_unknown");

        var meta = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta")).Metadata!.RootElement;
        await Assert.That(meta.GetProperty("compatibility").GetString()).IsEqualTo("BACKWARD");
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("v2");
    }

    [Test]
    public async Task UppercaseTransitiveModeStillChecksEveryActualAncestor()
    {
        var engine = ValidatingEngine(strict: true);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"old","format":"Avro/1.11.0","schema":{"type":"record","name":"Old","fields":[]},
             "meta":{"compatibility":"BACKWARD"}}
            """);
        await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"middle","format":"Avro/1.11.0","schema":{"type":"record","name":"Middle","aliases":["Old"],"fields":[]}}
            """);
        await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"new","format":"Avro/1.11.0","schema":{"type":"record","name":"New","aliases":["Middle"],"fields":[]}}
            """);
        var before = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta",
            """{"compatibility":"BACKWARD_TRANSITIVE"}"""), "compatibility_violation");

        await Assert.That((await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task CaseOnlyMetaPatchPreservesExistingVersionValidation()
    {
        var engine = ValidatingEngine(strict: true);
        await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details", """
            {"versionid":"v1","format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"backward"}}
            """);
        await Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details", """
            {"versionid":"v2","format":"Avro/1.11.0","schema":"long"}
            """);

        await Send(engine, RegistryAction.Patch, "/sets/g/schemas/s/meta", """{"compatibility":"BACKWARD"}""");

        var meta = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/meta")).Metadata!.RootElement;
        await Assert.That(meta.GetProperty("compatibility").GetString()).IsEqualTo("BACKWARD");
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("v2");
        foreach (var versionId in new[] { "v1", "v2" })
        {
            var version = (await Send(engine, RegistryAction.Read, "/sets/g/schemas/s/versions/" + versionId + "$details")).Metadata!.RootElement;
            await Assert.That(version.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
            await Assert.That(version.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
        }
    }

    [Test]
    public async Task GroupExtensionNamedCompatibilityKeepsCaseSensitiveEnumAndStoredValue()
    {
        var engine = Create("""
            {"groups":{"gs":{"singular":"g","attributes":{"compatibility":{"type":"string","enum":["backward"]}}}}}
            """);
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"compatibility":"backward"}""");
        var before = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement.GetRawText();

        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/gs/g", """{"compatibility":"BACKWARD"}"""), "invalid_attribute");

        await Assert.That((await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
    }
}
