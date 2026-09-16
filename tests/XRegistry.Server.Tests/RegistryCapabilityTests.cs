// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryCapabilityTests
{
    [Test]
    public async Task CapabilityChangesArePersistedAndDrivePaginationRatherThanOnlyAdvertisingIt()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = MutableEngine(store);
        await Send(engine, RegistryAction.Post, "/teams", """{"a":{},"b":{}}""");
        var changed = await Send(engine, RegistryAction.Patch, "/capabilities", """{"pagination":false}""");
        await Assert.That(changed.Metadata!.RootElement.GetProperty("pagination").GetBoolean()).IsFalse();
        var all = await Send(engine, RegistryAction.Read, "/teams", null, new KeyValuePair<string, string?>("limit", "1"));
        await Assert.That(all.Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(2);
        await Assert.That(all.Page).IsNull();
        var restarted = MutableEngine(store);
        await Assert.That((await Send(restarted, RegistryAction.Read, "/capabilities")).Metadata!.RootElement.GetProperty("pagination").GetBoolean()).IsFalse();
        await Send(restarted, RegistryAction.Patch, "/capabilities", """{"pagination":true}""");
        var first = await Send(restarted, RegistryAction.Read, "/teams", null, new KeyValuePair<string, string?>("limit", "1"));
        await Assert.That(first.Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(first.Page!.TotalCount).IsEqualTo(2UL);
    }

    [Test]
    public async Task CapabilityPatchReplacesWholeMembersAndPutResetsOmittedDefaults()
    {
        var engine = MutableEngine();
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"flags":["filter"],"pagination":false}""");
        var caps = (await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement;
        await Assert.That(string.Join(',', caps.GetProperty("flags").EnumerateArray().Select(static item => item.GetString()))).IsEqualTo("filter");
        await Send(engine, RegistryAction.Replace, "/capabilities", """{"flags":["*"]}""");
        caps = (await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement;
        await Assert.That(caps.GetProperty("pagination").GetBoolean()).IsTrue();
        await Assert.That(caps.GetProperty("flags").EnumerateArray().Any(static item => item.GetString() == "sort")).IsTrue();
        await Assert.That(caps.GetProperty("flags").EnumerateArray().Any(static item => item.GetString() == "*")).IsFalse();
    }

    [Test]
    [Arguments("""{"flags":["filter","*"]}""", "capability_wildcard")]
    [Arguments("""{"flags":["invented"]}""", "capability_value")]
    [Arguments("""{"flags":["filter","FILTER"]}""", "capability_value")]
    [Arguments("""{"pagination":"false"}""", "capability_value")]
    [Arguments("""{"available":{"entities":{"mutable":true}}}""", "capability_missing_value")]
    [Arguments("""{"specversions":[]}""", "capability_missing_value")]
    [Arguments("""{"versionmodes":["semver"]}""", "capability_missing_value")]
    [Arguments("""{"authentication":false}""", "capability_unknown")]
    public async Task InvalidCapabilityChangesDoNotPublishConfigurationOrEvents(string body, string code)
    {
        var engine = MutableEngine();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/capabilities", body), code);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    [Test]
    public async Task RootCapabilityAndNestedEntityChangesRollBackTogetherAndEmitOneInteraction()
    {
        var engine = MutableEngine();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """
            {"capabilities":{"pagination":false},"teams":{"a":{},"b":{"notdefined":1}}}
            """), "unknown_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement.GetProperty("pagination").GetBoolean()).IsTrue();
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        var accepted = await Send(engine, RegistryAction.Patch, "/", """{"capabilities":{"pagination":false},"teams":{"a":{}}}""");
        var events = (await engine.ReadEventBatchesAsync(Writer())).Single().Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Count(static item => item.GetProperty("type").GetString() == "io.xregistry.capabilities.updated")).IsEqualTo(1);
        await Assert.That(events.Count(static item => item.GetProperty("type").GetString() == "io.xregistry.registry.updated")).IsEqualTo(1);
        await Assert.That(events.All(item => item.GetProperty("xregcorrelationid").GetString() == accepted.CorrelationId)).IsTrue();
        await Assert.That(events.Single(static item => item.GetProperty("type").GetString() == "io.xregistry.capabilities.updated")
            .GetProperty("data").GetProperty("changed").EnumerateArray().Select(static item => item.GetString()!).ToArray())
            .IsEquivalentTo(["pagination"], StringComparer.Ordinal);
        await Assert.That(events.Single(static item => item.GetProperty("type").GetString() == "io.xregistry.registry.updated")
            .GetProperty("data").GetProperty("changed").EnumerateArray().Select(static item => item.GetString()!).ToArray())
            .IsEquivalentTo(["epoch", "modifiedat", "capabilities", "teams", "teamscount"], StringComparer.Ordinal);
    }

    [Test]
    public async Task DisabledFlagsAreIgnoredAndUnavailableMetadataRejectsAllRetrievalForms()
    {
        var engine = MutableEngine();
        await Send(engine, RegistryAction.Post, "/teams", """{"a":{},"b":{}}""");
        await Send(engine, RegistryAction.Patch, "/capabilities", """
            {"flags":["inline"],"available":{
              "capabilities":{"mutable":true},"capabilitiesoffered":{"mutable":false},"entities":{"mutable":true},
              "model":{"mutable":false},"modelsource":{"mutable":true}
            }}
            """);
        var all = await Send(engine, RegistryAction.Read, "/teams", null, new("filter", "invalid-filter!="), new("sort", "invalid-sort"));
        await Assert.That(all.Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(2);
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/export"), "not_available");
        await Send(engine, RegistryAction.Patch, "/capabilities", """
            {"available":{"capabilities":{"mutable":true},"entities":{"mutable":true},"model":{"mutable":false}}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/modelsource"), "not_available");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/", null, new KeyValuePair<string, string?>("inline", "modelsource")), "not_available");
    }

    [Test]
    public async Task OfferedChoicesPreserveImmutableModelAndHostControlledCapabilityMutability()
    {
        var engine = MutableEngine();
        var offered = (await Send(engine, RegistryAction.Read, "/capabilitiesoffered")).Metadata!.RootElement;
        await Assert.That(offered.GetProperty("pagination").GetProperty("enum").GetArrayLength()).IsEqualTo(2);
        await Assert.That(offered.GetProperty("shortself").GetProperty("enum").GetArrayLength()).IsEqualTo(2);
        await Assert.That(offered.GetProperty("available").GetProperty("attributes").GetProperty("model")
            .GetProperty("attributes").GetProperty("mutable").GetProperty("enum")[0].GetBoolean()).IsFalse();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/capabilities", """
            {"available":{"capabilities":{"mutable":true},"entities":{"mutable":true},"model":{"mutable":true}}}
            """), "capability_error");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/capabilities", """
            {"available":{"capabilities":{"mutable":false},"entities":{"mutable":true},"model":{"mutable":false}}}
            """), "capability_error");
    }

    [Test]
    public async Task CapabilityMutabilityRestrictsWritesButCannotDisableHostAuthentication()
    {
        var engine = MutableEngine();
        await Send(engine, RegistryAction.Patch, "/capabilities", """
            {"available":{"capabilities":{"mutable":true},"entities":{"mutable":false},"model":{"mutable":false},"modelsource":{"mutable":false}}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g", "{}"), "readonly");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", "{}"), "readonly");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """{"name":"forbidden"}"""), "readonly");
        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Patch, RegistryPath.Parse("/capabilities"))
        {
            Metadata = RegistryJson.Parse("""{"pagination":false}""")
        }, Reader()), "unauthorized");
        await Send(engine, RegistryAction.Patch, "/", """{"capabilities":{"pagination":false}}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement.GetProperty("pagination").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task NewCapabilityValuesControlNestedDataButModelWritePermissionUsesPreviousState()
    {
        var engine = MutableEngine();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """
            {"capabilities":{"available":{"capabilities":{"mutable":true},"entities":{"mutable":false},"model":{"mutable":false}}},
             "teams":{"g":{}}}
            """), "readonly");
        await Assert.That((await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement.GetProperty("available")
            .GetProperty("entities").GetProperty("mutable").GetBoolean()).IsTrue();
        await Send(engine, RegistryAction.Patch, "/", """
            {"capabilities":{"available":{"capabilities":{"mutable":true},"entities":{"mutable":true},"model":{"mutable":false},"modelsource":{"mutable":false}}},
             "modelsource":{}}
            """);
        await Assert.That((await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetRawText()).IsEqualTo("{}");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", Model), "readonly");
    }

    [Test]
    public async Task DisabledFormatsAndCompatibilityModesDoNotBecomeSuccessfulValidation()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"sets":{"singular":"set","resources":{"schemas":{"singular":"schema","validateformat":true,"validatecompatibility":true}}}}}
            """));
        var engine = new RegistryEngine(new()
        {
            RegistryId = "format-capabilities",
            PublicRoot = new Uri("https://registry.example"),
            Model = model,
            AllowCapabilityUpdates = true,
            ResourceValidator = new BuiltInRegistryResourceValidator()
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"formats":[],"compatibilities":{}}""");
        var uncheckedDocument = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details",
            """{"format":"Avro/1.11.0","schema":{}}""");
        await Assert.That(uncheckedDocument.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(uncheckedDocument.Metadata.RootElement.GetProperty("formatvalidatedreason").GetString()!).Contains("not enabled");
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"formats":["*"],"compatibilities":{}}""");
        var checkedDocument = await Send(engine, RegistryAction.Replace, "/sets/g/schemas/s$details",
            """{"format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"forward"}}""");
        await Assert.That(checkedDocument.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(checkedDocument.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"compatibilities":{"Avro/*":["forward"]}}""");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/sets/g/schemas/s$details",
            """{"format":"Avro/1.11.0","schema":"long"}"""), "compatibility_violation");
    }

    [Test]
    public async Task CapabilityAuthorizationFailureHasNoConfigurationEffects()
    {
        var engine = MutableEngine(authorization: new DenyCapabilities());
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """{"capabilities":{"pagination":false},"name":"not written"}"""), "forbidden");
        await Assert.That((await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement.GetProperty("pagination").GetBoolean()).IsTrue();
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task VersionModeRestrictionsValidateTheCompleteCombinedModelCandidate()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,"versionmode":"semver"}}}}}
            """));
        var engine = MutableEngine(model: model);
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/capabilities", """{"versionmodes":["manual"]}"""), "capability_error");
        await Send(engine, RegistryAction.Patch, "/", """
            {"capabilities":{"versionmodes":["manual"]},"modelsource":{
              "groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false}}}}
            }}
            """);
        await Assert.That((await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement.GetProperty("versionmodes").GetArrayLength()).IsEqualTo(1);
        var created = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        await Assert.That(created.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
    }

    [Test]
    public async Task CapabilityValidationWorkExhaustionRejectsWithoutPersistingConfiguration()
    {
        var engine = MutableEngine(limits: new() { MaxEntityOperations = 50 });
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/capabilities", """{"pagination":false}"""), "operation_limit");
        await Assert.That((await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement.GetProperty("pagination").GetBoolean()).IsTrue();
        await Assert.That((await engine.ReadEventBatchesAsync(Writer(), limit: 1)).Count).IsEqualTo(0);
    }
    internal static RegistryEngine MutableEngine(IRegistryPersistence? persistence = null, RegistryModel? model = null,
        IRegistryAuthorizationPolicy? authorization = null, RegistryLimits? limits = null) => new(new()
        {
            RegistryId = "mutable",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = model ?? RegistryModel.Compile(RegistryJson.Parse(Model)),
            AllowCapabilityUpdates = true,
            Limits = limits ?? new()
        }, persistence ?? new InMemoryRegistryPersistence(), authorization ?? new PermitPolicy());

    private sealed class DenyCapabilities : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(access != RegistryAccess.UpdateCapabilities);
    }
}
