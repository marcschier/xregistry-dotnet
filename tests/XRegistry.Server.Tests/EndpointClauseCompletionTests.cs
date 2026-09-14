using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class EndpointClauseCompletionTests
{
    [Test]
    public async Task OpaqueAuthorizationNullsAreRejectedBeforeEndpointPublication()
    {
        var engine = new RegistryEngine(new RegistryEngineOptions
        {
            RegistryId = "endpoint-clauses",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = BuiltInRegistryModels.Compile(RegistryModelKind.Endpoint),
            AllowAnonymousReads = true
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/e", """
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"type":null}]}}
            """), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("endpointscount").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var accepted = await Send(engine, RegistryAction.Replace, "/endpoints/e", """
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"type":"{kind}","authorityuri":"../auth/{tenant}"}]}}
            """);
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("protocoloptions").GetProperty("authorization")[0]
            .GetProperty("type").GetString()).IsEqualTo("{kind}");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("protocoloptions").GetProperty("authorization")[0]
            .GetProperty("authorityuri").GetString()).IsEqualTo("../auth/{tenant}");
    }

    [Test]
    public async Task ExplicitEndpointMarkerEnforcesCommonEnvelopeRulesWithoutDependingOnModelEnums()
    {
        var engine = Create("""
            {"groups":{"ports":{"singular":"port",
             "modelcompatiblewith":"https://xregistry.io/xreg/domains/endpoint/specs/model.json",
             "attributes":{"usage":{"type":"array","item":{"type":"string"}},"envelope":{"type":"string"},"envelopeoptions":{"type":"any"}}}}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/ports/e", """
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"Binary"}}
            """), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("portscount").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var valid = await Send(engine, RegistryAction.Replace, "/ports/e", """
            {"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"structured","format":"application/cloudevents+json"}}
            """);
        await Assert.That(valid.Metadata!.RootElement.GetProperty("envelopeoptions").GetProperty("mode").GetString()).IsEqualTo("structured");
        await Assert.That(valid.Metadata.RootElement.GetProperty("envelopeoptions").GetProperty("format").GetString())
            .IsEqualTo("application/cloudevents+json");
    }
}
