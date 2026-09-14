using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointDefinitionMessageTests
{
    [Test]
    public async Task InlineAndExplicitReferencedGroupsAreSelectedWithoutFetchingOrExpandingMessages()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","messages":{"inline":{"messageid":"inline","marker":"local"}},
             "messagegroups":["/messagegroups/orders","https://192.0.2.1:1/messagegroups/external"],
             "protocoloptions":{"query":{"tenant":"{tenant}"}}}
            """);
        var groups = RegistryJson.Parse("""
            {"/messagegroups/orders":{"protocol":"http","messages":{"order":{"messageid":"order","marker":"{tenant}"}}},
             "https://192.0.2.1:1/messagegroups/external":{"messages":{"external":{"marker":"remote supplied"}}}}
            """);
        var result = EndpointDefinition.Materialize(input, RegistryJson.Parse("""{"tenant":"west"}"""),
            new EndpointTemplateOptions { MessageGroups = groups });

        await Assert.That(result.Messages.Select(message => message.MessageId).ToArray())
            .IsEquivalentTo(["inline", "order", "external"], StringComparer.Ordinal);
        await Assert.That(result.SelectMessage("order").Metadata.RootElement.GetProperty("marker").GetString()).IsEqualTo("{tenant}");
        await Assert.That(result.SelectMessage("order").GroupReference).IsEqualTo("/messagegroups/orders");
        await Assert.That(result.SelectMessage("inline").GroupReference).IsEqualTo("");
        await Assert.That(result.SelectMessage("external").Metadata.RootElement.GetProperty("marker").GetString()).IsEqualTo("remote supplied");
        await Assert.That(result.DeferredChecks.Any(check => check.Code == "message_group")).IsFalse();
    }

    [Test]
    public async Task DuplicateIdsAreLegalButAnAmbiguousSelectionNeverChoosesTheFirst()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"messages":{"order":{"format":"json"}},"messagegroups":["/messagegroups/xml"]}
            """);
        var groups = RegistryJson.Parse("""{"/messagegroups/xml":{"messages":{"order":{"format":"xml"}}}}""");
        var result = EndpointDefinition.Materialize(input, options: new EndpointTemplateOptions { MessageGroups = groups });
        await Assert.That(result.Messages.Count).IsEqualTo(2);
        var failure = await Assert.That(() => result.SelectMessage("order")).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected an ambiguous selection diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("ambiguous_message");
        await Assert.That(result.SelectMessage("order", "").Metadata.RootElement.GetProperty("format").GetString()).IsEqualTo("json");
        await Assert.That(result.SelectMessage("order", "/messagegroups/xml").Metadata.RootElement.GetProperty("format").GetString()).IsEqualTo("xml");
        var duplicate = await Assert.That(() => EndpointDefinition.Materialize(input, options:
            new EndpointTemplateOptions { MessageGroups = groups, RejectDuplicateMessageIds = true })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the explicit duplicate policy to reject.");
        await Assert.That(duplicate.Diagnostic.Code).IsEqualTo("duplicate_message_id");
    }

    [Test]
    public async Task MissingAndUnexpandedGroupsBlockAggregateSelectionButNotAnExplicitInlineScope()
    {
        var input = RegistryJson.Parse("""
            {"usage":["producer"],"messages":{"inline":{"marker":"known"}},"messagegroups":["https://192.0.2.1:1/group","/messagegroups/unexpanded"]}
            """);
        var result = EndpointDefinition.Materialize(input, options: new EndpointTemplateOptions
        {
            MessageGroups = RegistryJson.Parse("""{"/messagegroups/unexpanded":{"messagesurl":"https://192.0.2.1:1/messages"}}""")
        });
        await Assert.That(result.DeferredChecks.Count(check => check.Code == "message_group")).IsEqualTo(2);
        var failure = await Assert.That(() => result.SelectMessage("inline")).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected incomplete-scope selection to fail.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("message_selection_incomplete");
        await Assert.That(result.SelectMessage("inline", "").Metadata.RootElement.GetProperty("marker").GetString()).IsEqualTo("known");
        var unresolved = await Assert.That(() => result.SelectMessage("missing", "/messagegroups/unexpanded")).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the referenced scope to remain incomplete.");
        await Assert.That(unresolved.Diagnostic.Code).IsEqualTo("message_selection_incomplete");
    }

    [Test]
    public async Task ExplicitEmptyCollectionsAreNotConfusedWithMissingOrUnreachableGroups()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"messages":{},"messagegroups":["/messagegroups/empty"]}""");
        var result = EndpointDefinition.Materialize(input, options: new EndpointTemplateOptions
        {
            MessageGroups = RegistryJson.Parse("""{"/messagegroups/empty":{"messages":{}}}""")
        });
        await Assert.That(result.Messages.Count).IsEqualTo(0);
        await Assert.That(result.DeferredChecks.Any(check => check.Code == "message_group")).IsFalse();
        var missing = await Assert.That(() => result.SelectMessage("missing")).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a missing message diagnostic.");
        await Assert.That(missing.Diagnostic.Code).IsEqualTo("message_not_found");
    }

    [Test]
    public async Task RepeatedGroupReferencesDoNotDuplicateTheSameMessageCandidate()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"messagegroups":["/messagegroups/g","/messagegroups/g"]}""");
        var result = EndpointDefinition.Materialize(input, options: new EndpointTemplateOptions
        {
            MessageGroups = RegistryJson.Parse("""{"/messagegroups/g":{"messages":{"m":{"marker":"one"}}}}""")
        });
        await Assert.That(result.Messages.Count).IsEqualTo(1);
        await Assert.That(result.SelectMessage("m").Metadata.RootElement.GetProperty("marker").GetString()).IsEqualTo("one");
    }

    [Test]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP","messages":{"m":{"protocol":"NATS"}}}""", "/messages/m/protocol")]
    [Arguments("""{"usage":["producer"],"envelope":"myspec/1.0","messages":{"m":{"envelope":"myspec"}}}""", "/messages/m/envelope")]
    [Arguments("""{"usage":["producer"],"envelope":"myspec/1.0","messages":{"m":{"envelope":"myspec/1.01"}}}""", "/messages/m/envelope")]
    [Arguments("""{"usage":["producer"],"messages":{"m":{"messageid":"other"}}}""", "/messages/m/messageid")]
    [Arguments("""{"usage":["producer"],"messages":{"m":false}}""", "/messages/m")]
    [Arguments("""{"usage":["producer"],"messagegroups":["relative/group"]}""", "/messagegroups/0")]
    [Arguments("""{"usage":["producer"],"messagegroups":["/messagegroups/g/messages/m"]}""", "/messagegroups/0")]
    public async Task EndpointMessageMembershipValidatesExplicitSelectorAndReferenceContracts(string json, string path)
    {
        var failure = await Assert.That(() => EndpointDefinition.Materialize(RegistryJson.Parse(json))).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a group contract diagnostic.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    public async Task EndpointEnvelopeRefinementAndProtocolShorthandsUseExistingDomainContracts()
    {
        var result = EndpointDefinition.Materialize(RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"AMQP","envelope":"myspec/1.0",
             "messages":{"m":{"protocol":"amqp/1.0","envelope":"myspec/1.0.1"}}}
            """));
        await Assert.That(result.SelectMessage("m").Metadata.RootElement.GetProperty("envelope").GetString()).IsEqualTo("myspec/1.0.1");
    }

    [Test]
    public async Task ReferencedGroupProtocolCannotBeBypassedByAnUnboundMessage()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"protocol":"HTTP","messagegroups":["/messagegroups/g"]}""");
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input, options: new EndpointTemplateOptions
        {
            MessageGroups = RegistryJson.Parse("""{"/messagegroups/g":{"protocol":"NATS","messages":{"m":{}}}}""")
        })).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an effective group protocol conflict.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/suppliedmessagegroups/~1messagegroups~1g/messages/m/protocol");
    }

    [Test]
    public async Task MessageInheritanceIsPreservedAndExplicitlyDeferredRatherThanFetchedOrInvented()
    {
        var result = EndpointDefinition.Materialize(RegistryJson.Parse("""
            {"usage":["producer"],"envelope":"myspec/1.0","messages":{"m":{"basemessage":"https://192.0.2.1:1/messages/base"}}}
            """));
        await Assert.That(result.SelectMessage("m").Metadata.RootElement.GetProperty("basemessage").GetString())
            .IsEqualTo("https://192.0.2.1:1/messages/base");
        await Assert.That(result.DeferredChecks.Any(check => check.Code == "message_materialization" && check.Path == "/messages/m")).IsTrue();
    }

    [Test]
    public async Task MessageCountAndAggregateBytesAreInclusiveAcrossSelectedGroups()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"messages":{"m":{"marker":"one"}}}""");
        var bytes = Encoding.UTF8.GetByteCount("""{"marker":"one"}""") + 1;
        var result = EndpointDefinition.Materialize(input, options: new EndpointTemplateOptions { MaxMessages = 1, MaxMessageBytes = bytes });
        await Assert.That(result.SelectMessage("m").Metadata.RootElement.GetProperty("marker").GetString()).IsEqualTo("one");
        var count = await Assert.That(() => EndpointDefinition.Materialize(input, options:
            new EndpointTemplateOptions { MaxMessages = 0 })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a message count limit.");
        await Assert.That(count.Diagnostic.Code).IsEqualTo("endpoint_message_limit");
        var size = await Assert.That(() => EndpointDefinition.Materialize(input, options:
            new EndpointTemplateOptions { MaxMessageBytes = bytes - 1 })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a message byte limit.");
        await Assert.That(size.Diagnostic.Code).IsEqualTo("endpoint_message_limit");
    }

    [Test]
    public async Task ReturnedMessageAndDeferredCollectionsCannotBeMutatedAndSelectionHonorsCancellation()
    {
        var result = EndpointDefinition.Materialize(RegistryJson.Parse("""{"usage":["producer"],"messages":{"m":{"marker":"one"}}}"""));
        await Assert.That(() => ((IList<EndpointDefinitionMessage>)result.Messages).Clear()).Throws<NotSupportedException>();
        await Assert.That(() => ((IList<RegistryDiagnostic>)result.DeferredChecks).Clear()).Throws<NotSupportedException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => result.SelectMessage("m", cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(result.SelectMessage("m").Metadata.RootElement.GetProperty("marker").GetString()).IsEqualTo("one");
    }

    [Test]
    public async Task ExactIntegerCollectionCountsArePreservedAndPartialCollectionsAreExplicit()
    {
        var exact = EndpointDefinition.Materialize(RegistryJson.Parse("""
            {"usage":["producer"],"messages":{"m":{"marker":"one"}},"messagescount":1e0}
            """));
        await Assert.That(exact.Resolved.RootElement.GetProperty("messagescount").GetRawText()).IsEqualTo("1e0");
        await Assert.That(exact.SelectMessage("m").MessageId).IsEqualTo("m");
        var partial = EndpointDefinition.Materialize(RegistryJson.Parse("""
            {"usage":["producer"],"messages":{"m":{}},"messagescount":2}
            """));
        var failure = await Assert.That(() => partial.SelectMessage("m")).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected an incomplete collection diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("message_selection_incomplete");
    }

    [Test]
    public async Task GroupBudgetsCountBothSuppliedAndDeclaredReferencesBeforeDeduplication()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"messagegroups":["/messagegroups/g","/messagegroups/g"]}""");
        var groups = RegistryJson.Parse("""{"/messagegroups/g":{"messages":{}},"/messagegroups/unused":{"messages":{}}}""");
        var complete = EndpointDefinition.Materialize(input, options:
            new EndpointTemplateOptions { MessageGroups = groups, MaxMessageGroups = 2 });
        await Assert.That(complete.Messages.Count).IsEqualTo(0);
        var supplied = await Assert.That(() => EndpointDefinition.Materialize(input, options:
            new EndpointTemplateOptions { MessageGroups = groups, MaxMessageGroups = 1 })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a supplied Group limit.");
        await Assert.That(supplied.Diagnostic.Path).IsEqualTo("/suppliedmessagegroups");
        var declared = await Assert.That(() => EndpointDefinition.Materialize(input, options:
            new EndpointTemplateOptions { MaxMessageGroups = 1 })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a declared reference limit.");
        await Assert.That(declared.Diagnostic.Path).IsEqualTo("/messagegroups");
    }
}
