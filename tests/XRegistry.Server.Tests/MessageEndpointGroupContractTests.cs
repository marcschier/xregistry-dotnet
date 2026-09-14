using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.MessageEndpointRuntimeTests;

namespace XRegistry.Server.Tests;

public class MessageEndpointGroupContractTests
{
    [Test]
    [Arguments(RegistryModelKind.Message, "/messagegroups/g", """{"protocol":"HTTP"}""")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/g", """{"usage":["producer"],"protocol":"HTTP"}""")]
    public async Task UnboundMessagesRemainUsableWithinProtocolBoundGroups(
        RegistryModelKind kind, string groupPath, string groupMetadata)
    {
        var engine = CreateDomain(kind);
        await Send(engine, RegistryAction.Replace, groupPath, groupMetadata);
        var message = await Send(engine, RegistryAction.Replace, groupPath + "/messages/m",
            """{"dataschemaformat":"JSONSchema/draft-07","dataschema":{"type":"object"}}""");
        await Assert.That(message.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(message.Metadata!.RootElement.TryGetProperty("protocol", out _)).IsFalse();
    }

    [Test]
    [Arguments(RegistryModelKind.Message, "/messagegroups/g", """{"protocol":"HTTP"}""")]
    [Arguments(RegistryModelKind.Endpoint, "/endpoints/g", """{"usage":["producer"],"protocol":"HTTP"}""")]
    public async Task DomainGroupContractsRejectIncompatibleChildProtocols(
        RegistryModelKind kind, string groupPath, string groupMetadata)
    {
        var engine = CreateDomain(kind);
        await Send(engine, RegistryAction.Replace, groupPath, groupMetadata);
        var before = (await Send(engine, RegistryAction.Read, groupPath)).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Replace, groupPath + "/messages/m",
            """{"protocol":"NATS","protocoloptions":{}}"""), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, groupPath)).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        var accepted = await Send(engine, RegistryAction.Replace, groupPath + "/messages/m",
            """{"protocol":"http","protocoloptions":{}}""");
        await Assert.That(accepted.Metadata!.RootElement.GetProperty("protocol").GetString()).IsEqualTo("http");
    }

    [Test]
    public async Task MessageGroupEnvelopeMustBeDeclaredByItsChildren()
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await Send(engine, RegistryAction.Replace, "/messagegroups/g", """{"envelope":"CloudEvents/1.0"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", "{}"), "invalid_attribute");
        var valid = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"envelope":"CloudEvents/1.0","envelopemetadata":{}}""");
        await Assert.That(valid.Metadata!.RootElement.GetProperty("envelope").GetString()).IsEqualTo("CloudEvents/1.0");
    }

    [Test]
    public async Task EndpointEnvelopeVersionCannotBeBroadenedByAChild()
    {
        var engine = CreateDomain(RegistryModelKind.Endpoint);
        await Send(engine, RegistryAction.Replace, "/endpoints/g", """{"usage":["producer"],"envelope":"CloudEvents/2.0"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/g/messages/m",
            """{"envelope":"CloudEvents/1.0","envelopemetadata":{}}"""), "invalid_attribute");
        await Send(engine, RegistryAction.Patch, "/endpoints/g", """{"envelope":"CloudEvents"}""");
        var valid = await Send(engine, RegistryAction.Replace, "/endpoints/g/messages/m",
            """{"envelope":"CloudEvents/1.0","envelopemetadata":{}}""");
        await Assert.That(valid.Metadata!.RootElement.GetProperty("envelope").GetString()).IsEqualTo("CloudEvents/1.0");
    }

    [Test]
    public async Task GroupSelectorChangesValidateExistingChildrenAndAtomicFinalValues()
    {
        var engine = CreateDomain(RegistryModelKind.Message);
        await Send(engine, RegistryAction.Replace, "/messagegroups/g", """{"protocol":"HTTP"}""");
        await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """{"protocol":"HTTP","protocoloptions":{}}""");
        var group = (await Send(engine, RegistryAction.Read, "/messagegroups/g")).Metadata!.RootElement.GetRawText();
        var child = (await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/messagegroups/g", """{"protocol":"NATS"}"""), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/messagegroups/g")).Metadata!.RootElement.GetRawText()).IsEqualTo(group);
        await Assert.That((await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetRawText()).IsEqualTo(child);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);

        await Send(engine, RegistryAction.Patch, "/", """
            {"messagegroups":{"g":{"protocol":"NATS","messages":{"m":{"protocol":"NATS","protocoloptions":{}}}}}}
            """);
        await Assert.That((await Send(engine, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetProperty("protocol").GetString())
            .IsEqualTo("NATS");
    }

    [Test]
    public async Task DomainGroupContractsValidateBorrowedMessagesAndReverseMutations()
    {
        var engine = CreateDomain(RegistryModelKind.Endpoint);
        await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/target", """{"protocol":"NATS","protocoloptions":{}}""");
        await Send(engine, RegistryAction.Replace, "/endpoints/e", """{"usage":["consumer"],"protocol":"HTTP"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/e/messages/alias",
            """{"meta":{"xref":"/messagegroups/g/messages/target"}}"""), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement.GetProperty("messagescount").GetInt32()).IsEqualTo(0);
        await Send(engine, RegistryAction.Patch, "/messagegroups/g/messages/target", """{"protocol":"HTTP","protocoloptions":{}}""");
        await Send(engine, RegistryAction.Replace, "/endpoints/e/messages/alias",
            """{"meta":{"xref":"/messagegroups/g/messages/target"}}""");
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        var failure = await Assert.That(async () => await Send(engine, RegistryAction.Patch, "/messagegroups/g/messages/target",
            """{"protocol":"NATS","protocoloptions":{}}""")).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the referring domain contract to reject.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/messagegroups/g/messages/target");
        await Assert.That(failure.Diagnostic.Message.Contains("/endpoints/", StringComparison.Ordinal)).IsFalse();
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Assert.That((await Send(engine, RegistryAction.Read, "/endpoints/e/messages/alias")).Metadata!.RootElement.GetProperty("protocol").GetString())
            .IsEqualTo("HTTP");

        await Send(engine, RegistryAction.Patch, "/", """
            {"messagegroups":{"g":{"messages":{"target":{"protocol":"NATS","protocoloptions":{}}}}},
             "endpoints":{"e":{"protocol":"NATS"}}}
            """);
        await Assert.That((await Send(engine, RegistryAction.Read, "/endpoints/e/messages/alias")).Metadata!.RootElement.GetProperty("protocol").GetString())
            .IsEqualTo("NATS");
    }

    [Test]
    public async Task DomainGroupOptInValidatesExistingDataAndSurvivesRestart()
    {
        const string ordinary = """
            {"groups":{"messagegroups":{"singular":"messagegroup","attributes":{"protocol":{"type":"string"}},
              "resources":{"messages":{"singular":"message","hasdocument":false,"attributes":{
                "protocol":{"type":"string"},"protocoloptions":{"type":"object","attributes":{"*":{"type":"any"}}}
              }}}}}}
            """;
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ordinary, store);
        await Send(engine, RegistryAction.Replace, "/messagegroups/g", """{"protocol":"HTTP"}""");
        await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", """{"protocol":"NATS","protocoloptions":{}}""");
        var optedIn = ordinary.Replace("\"singular\":\"messagegroup\"",
            "\"singular\":\"messagegroup\",\"modelcompatiblewith\":\"https://xregistry.io/xreg/domains/message/specs/model.json\"", StringComparison.Ordinal)
            .Replace("\"singular\":\"message\"",
                "\"singular\":\"message\",\"modelcompatiblewith\":\"https://xregistry.io/xreg/domains/message/specs/model.json\"", StringComparison.Ordinal);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", optedIn), "model_compliance_error");
        await Assert.That((await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetProperty("groups")
            .GetProperty("messagegroups").TryGetProperty("modelcompatiblewith", out _)).IsFalse();
        await Send(engine, RegistryAction.Patch, "/messagegroups/g/messages/m", """{"protocol":"HTTP"}""");
        await Send(engine, RegistryAction.Replace, "/modelsource", optedIn);
        var restarted = Create(ordinary, store);
        await ExpectCode(() => Send(restarted, RegistryAction.Patch, "/messagegroups/g/messages/m", """{"protocol":"NATS"}"""), "invalid_attribute");
        await Assert.That((await Send(restarted, RegistryAction.Read, "/messagegroups/g/messages/m")).Metadata!.RootElement.GetProperty("protocol").GetString()).IsEqualTo("HTTP");
    }

    [Test]
    [Arguments("/messagegroups/g/messages/target")]
    [Arguments("/messagegroups/g/messages/target/meta")]
    [Arguments("/messagegroups/g/messages/target/versions/1")]
    public async Task DomainAliasesRequireAuthorizedTargetReads(string denied)
    {
        var policy = new DomainReadPolicy();
        var engine = CreateDomain(RegistryModelKind.Endpoint, policy: policy);
        await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/target", """{"protocol":"HTTP","protocoloptions":{}}""");
        await Send(engine, RegistryAction.Replace, "/endpoints/e", """{"usage":["consumer"],"protocol":"HTTP"}""");
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        policy.Denied = denied;
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/e/messages/alias",
            """{"meta":{"xref":"/messagegroups/g/messages/target"}}"""), "forbidden");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Assert.That((await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement.GetProperty("messagescount").GetInt32()).IsEqualTo(0);
    }

    private sealed class DomainReadPolicy : IRegistryAuthorizationPolicy
    {
        public string? Denied { get; set; }
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(access != RegistryAccess.Read || path.EscapedPath != Denied);
    }
}
