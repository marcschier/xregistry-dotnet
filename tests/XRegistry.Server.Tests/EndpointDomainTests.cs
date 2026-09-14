using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class EndpointDomainTests
{
    private const string GenericEndpoints = """
        {"groups":{"endpoints":{"singular":"endpoint","attributes":{
          "usage":{"type":"array","item":{"type":"string"}},"protocol":{"type":"string"}
        }}}}
        """;

    [Test]
    public async Task PackagedEndpointRejectsInvalidUsageBeforePublishing()
    {
        var engine = CreateEndpoint(RegistryModelKind.Endpoint);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/rejected",
            """{"usage":[]}"""), "invalid_attribute");
        var root = await Send(engine, RegistryAction.Read, "/");
        await Assert.That(root.Metadata!.RootElement.GetProperty("endpointscount").GetInt32()).IsEqualTo(0);
        await Assert.That(root.Metadata.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var created = await Send(engine, RegistryAction.Replace, "/endpoints/accepted",
            """{"usage":["consumer"],"protocol":"HTTP"}""");
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(created.Metadata!.RootElement.GetProperty("usage")[0].GetString()).IsEqualTo("consumer");
    }

    [Test]
    [Arguments(RegistryModelKind.Endpoint)]
    [Arguments(RegistryModelKind.CloudEvents)]
    public async Task DomainRuleChecksMergedPatchesAndRollsBackNestedFailures(RegistryModelKind kind)
    {
        var engine = CreateEndpoint(kind);
        await Send(engine, RegistryAction.Replace, "/endpoints/existing",
            """{"usage":["subscriber","consumer"],"protocol":"MQTT/5.0"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/endpoints/existing",
            """{"protocol":"HTTP"}"""), "invalid_attribute");
        var unchanged = (await Send(engine, RegistryAction.Read, "/endpoints/existing")).Metadata!.RootElement;
        await Assert.That(unchanged.GetProperty("protocol").GetString()).IsEqualTo("MQTT/5.0");
        await Assert.That(unchanged.GetProperty("usage").GetArrayLength()).IsEqualTo(2);
        await Assert.That(unchanged.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/", """
            {"endpoints":{"a-valid":{"usage":["producer"],"protocol":"HTTP"},
              "z-invalid":{"usage":["not-an-endpoint-role"]}}}
            """), "invalid_attribute");
        var root = (await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement;
        await Assert.That(root.GetProperty("endpointscount").GetInt32()).IsEqualTo(1);
        await Assert.That(root.GetProperty("epoch").GetInt32()).IsEqualTo(1);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        var updated = await Send(engine, RegistryAction.Patch, "/endpoints/existing", """{"usage":["consumer"]}""");
        await Assert.That(updated.Metadata!.RootElement.GetProperty("usage").GetArrayLength()).IsEqualTo(1);
        await Assert.That(updated.Metadata.RootElement.GetProperty("usage")[0].GetString()).IsEqualTo("consumer");
    }

    [Test]
    [Arguments("")]
    [Arguments("https://xregistry.io/xreg/domains/message/specs/model.json")]
    public async Task AnArbitraryGroupNamedEndpointsDoesNotActivateEndpointRules(string compatibleWith)
    {
        var model = compatibleWith.Length == 0 ? GenericEndpoints : GenericEndpoints.Replace("\"singular\":\"endpoint\"",
            "\"singular\":\"endpoint\",\"modelcompatiblewith\":\"" + compatibleWith + "\"", StringComparison.Ordinal);
        var engine = Create(model);
        var withoutUsage = await Send(engine, RegistryAction.Replace, "/endpoints/no-usage", "{}");
        await Assert.That(withoutUsage.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(withoutUsage.Metadata!.RootElement.TryGetProperty("usage", out _)).IsFalse();
        var customRoles = await Send(engine, RegistryAction.Replace, "/endpoints/custom",
            """{"usage":["arbitrary","roles"],"protocol":"HTTP"}""");
        await Assert.That(customRoles.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(customRoles.Metadata!.RootElement.GetProperty("usage")[0].GetString()).IsEqualTo("arbitrary");
        await Assert.That(customRoles.Metadata.RootElement.GetProperty("usage")[1].GetString()).IsEqualTo("roles");
    }

    [Test]
    public async Task AnExplicitEndpointDomainDeclarationWorksUnderADifferentGroupName()
    {
        var engine = Create("""
            {"groups":{"ports":{"singular":"port",
              "modelcompatiblewith":"https://xregistry.io/xreg/domains/endpoint/specs/model.json",
              "attributes":{"usage":{"type":"array","required":true,"item":{"type":"string"}},"protocol":{"type":"string"}}
            }}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/ports/rejected",
            """{"usage":["producer","consumer"],"protocol":"MQTT/5.0"}"""), "invalid_attribute");
        var accepted = await Send(engine, RegistryAction.Replace, "/ports/accepted",
            """{"usage":["subscriber","consumer"],"protocol":"NATS"}""");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("portid").GetString()).IsEqualTo("accepted");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("usage").GetArrayLength()).IsEqualTo(2);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("portscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task AddingEndpointDomainToAnExistingModelValidatesExistingGroupsAtomically()
    {
        var engine = Create(GenericEndpoints);
        await Send(engine, RegistryAction.Replace, "/endpoints/custom", """{"usage":["arbitrary"]}""");
        var optedIn = GenericEndpoints.Replace("\"singular\":\"endpoint\"",
            "\"singular\":\"endpoint\",\"modelcompatiblewith\":\"https://xregistry.io/xreg/domains/endpoint/specs/model.json\"",
            StringComparison.Ordinal);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", optedIn), "model_compliance_error");
        var model = (await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement;
        await Assert.That(model.GetProperty("groups").GetProperty("endpoints").TryGetProperty("modelcompatiblewith", out _)).IsFalse();
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(1);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        await Send(engine, RegistryAction.Patch, "/endpoints/custom", """{"usage":["consumer"]}""");
        await Send(engine, RegistryAction.Replace, "/modelsource", optedIn);
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/endpoints/custom", """{"usage":["arbitrary"]}"""), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/endpoints/custom")).Metadata!.RootElement.GetProperty("usage")[0].GetString())
            .IsEqualTo("consumer");
    }

    [Test]
    public async Task EndpointDomainOptInSurvivesFrozenModelRestart()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = CreateEndpoint(RegistryModelKind.CloudEvents, store);
        await Send(engine, RegistryAction.Replace, "/endpoints/existing", """{"usage":["producer"],"protocol":"HTTP"}""");
        var restarted = CreateEndpoint(RegistryModelKind.CloudEvents, store);
        await ExpectCode(() => Send(restarted, RegistryAction.Patch, "/endpoints/existing",
            """{"usage":["producer","subscriber"],"protocol":"AMQP/1.0"}"""), "invalid_attribute");
        var existing = (await Send(restarted, RegistryAction.Read, "/endpoints/existing")).Metadata!.RootElement;
        await Assert.That(existing.GetProperty("usage").GetArrayLength()).IsEqualTo(1);
        await Assert.That(existing.GetProperty("usage")[0].GetString()).IsEqualTo("producer");
        await Assert.That(existing.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    private static RegistryEngine CreateEndpoint(RegistryModelKind kind, IRegistryPersistence? persistence = null) =>
        new(new RegistryEngineOptions
        {
            RegistryId = "endpoint-tests",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = BuiltInRegistryModels.Compile(kind),
            AllowAnonymousReads = true
        }, persistence ?? new InMemoryRegistryPersistence(), new PermitPolicy());
}
