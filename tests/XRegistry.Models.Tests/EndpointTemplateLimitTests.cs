using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class EndpointTemplateLimitTests
{
    private static readonly RegistryJson Input = RegistryJson.Parse(
        """{"usage":["producer"],"protocoloptions":{"x":"{v}{v}"}}""");
    private static readonly RegistryJson Arguments = RegistryJson.Parse("""{"v":"/"}""");

    [Test]
    public async Task ExpansionCountIsInclusiveAndCountsRepeatedOccurrences()
    {
        var result = EndpointDefinition.Materialize(Input, Arguments, new EndpointTemplateOptions { MaxExpansions = 2 });
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("x").GetString())
            .IsEqualTo("%2F%2F");
        await Rejects(Input, Arguments, new EndpointTemplateOptions { MaxExpansions = 1 }, "template_expansion_limit");
    }

    [Test]
    public async Task ExpansionByteBudgetCountsEveryUseEvenWhenTheVariableIsRepeated()
    {
        var result = EndpointDefinition.Materialize(Input, Arguments, new EndpointTemplateOptions { MaxExpansionBytes = 6 });
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("x").GetString())
            .IsEqualTo("%2F%2F");
        await Rejects(Input, Arguments, new EndpointTemplateOptions { MaxExpansionBytes = 5 }, "template_expansion_byte_limit");
    }

    [Test]
    public async Task ExpandedStringBudgetIncludesLiteralBytesBeforeAllocation()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"protocoloptions":{"x":"a{v}"}}""");
        var result = EndpointDefinition.Materialize(input, Arguments, new EndpointTemplateOptions { MaxExpandedStringBytes = 4 });
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("x").GetString()).IsEqualTo("a%2F");
        await Rejects(input, Arguments, new EndpointTemplateOptions { MaxExpandedStringBytes = 3 }, "template_string_limit");
    }

    [Test]
    public async Task ExpandedMapKeysShareTheStringAndExpansionBudgets()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"query":{"{v}":"x"}}}""");
        var arguments = RegistryJson.Parse("""{"v":"abcdef"}""");
        var result = EndpointDefinition.Materialize(input, arguments, new EndpointTemplateOptions { MaxExpandedStringBytes = 6 });
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("query")
            .GetProperty("abcdef").GetString()).IsEqualTo("x");
        await Rejects(input, arguments, new EndpointTemplateOptions { MaxExpandedStringBytes = 5 }, "template_string_limit");
    }

    [Test]
    public async Task VariableCountAlsoBoundsUnusedBindings()
    {
        var arguments = RegistryJson.Parse("""{"v":"/","unused":"x"}""");
        var result = EndpointDefinition.Materialize(Input, arguments, new EndpointTemplateOptions { MaxVariables = 2 });
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("x").GetString()).IsEqualTo("%2F%2F");
        await Rejects(Input, arguments, new EndpointTemplateOptions { MaxVariables = 1 }, "template_variable_limit");
    }

    [Test]
    public async Task SerializedOutputByteLimitHasAnExactInclusiveBoundary()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"protocoloptions":{"x":"{v}"}}""");
        var arguments = RegistryJson.Parse("""{"v":"0123456789"}""");
        const string expected = """{"usage":["producer"],"protocoloptions":{"x":"0123456789"}}""";
        var limit = Encoding.UTF8.GetByteCount(expected);
        var result = EndpointDefinition.Materialize(input, arguments,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxBytes = limit } });
        await Assert.That(result.Resolved.RootElement.GetRawText()).IsEqualTo(expected);
        await Rejects(input, arguments,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxBytes = limit - 1 } }, "byte_limit");
    }

    [Test]
    public async Task AuthoredAndArgumentBytesAreBoundedEvenIfExpansionWouldShrinkThem()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"protocoloptions":{"x":"{averylongvariablename}"}}""");
        var arguments = RegistryJson.Parse("""{"averylongvariablename":""}""");
        await Rejects(input, arguments, new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxBytes = 60 } },
            "byte_limit");
        var shortInput = RegistryJson.Parse("""{"usage":["producer"]}""");
        var unused = RegistryJson.Parse("""{"unused":"abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz"}""");
        await Rejects(shortInput, unused, new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxBytes = 60 } },
            "byte_limit");
    }

    [Test]
    public async Task NodeAndDepthLimitsCountContainersAndNotOnlyExpandedValues()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"],"protocoloptions":{"x":"{v}"}}""");
        var result = EndpointDefinition.Materialize(input, Arguments,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxNodes = 5, MaxDepth = 2 } });
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("x").GetString()).IsEqualTo("%2F");
        await Rejects(input, Arguments,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxNodes = 4 } }, "node_limit");
        await Rejects(input, Arguments,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxDepth = 1 } }, "depth_limit");
    }

    [Test]
    public async Task EmptyBindingsStillConsumeOccurrencesButNotExpansionBytes()
    {
        var empty = RegistryJson.Parse("""{"v":""}""");
        var result = EndpointDefinition.Materialize(Input, empty,
            new EndpointTemplateOptions { MaxExpansionBytes = 0, MaxExpansions = 2 });
        await Assert.That(result.Resolved.RootElement.GetProperty("protocoloptions").GetProperty("x").GetString()).IsEqualTo("");
        await Rejects(Input, empty, new EndpointTemplateOptions { MaxExpansions = 0 }, "template_expansion_limit");
    }

    [Test]
    public async Task DeferredMetadataHasIndependentFiniteCountAndByteBudgets()
    {
        var input = RegistryJson.Parse("""{"usage":["producer"]}""");
        var empty = RegistryJson.Parse("{}");
        var result = EndpointDefinition.Materialize(input, empty, new EndpointTemplateOptions { MaxDeferredChecks = 2 });
        await Assert.That(result.DeferredChecks.Count).IsEqualTo(2);
        await Rejects(input, empty, new EndpointTemplateOptions { MaxDeferredChecks = 1 }, "endpoint_deferred_limit");
        await Rejects(input, empty, new EndpointTemplateOptions { MaxDeferredBytes = 0 }, "endpoint_deferred_limit");
        var concrete = EndpointDefinition.Materialize(RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{"endpoints":[{"uri":"https://example.com"}]}}
            """), options: new EndpointTemplateOptions { MaxDeferredChecks = 0, MaxDeferredBytes = 0 });
        await Assert.That(concrete.DeferredChecks.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExactNumberBudgetsAlsoApplyToUnexpandedMetadata()
    {
        var empty = RegistryJson.Parse("{}");
        await Rejects(RegistryJson.Parse("""{"usage":["producer"],"protocoloptions":{"x":1234}}"""), empty,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxNumberCharacters = 3 } }, "number_limit");
        await Rejects(RegistryJson.Parse("""{"usage":["producer"],"protocoloptions":{"x":1e3}}"""), empty,
            new EndpointTemplateOptions { JsonLimits = new RegistryJsonLimits { MaxNumberExponent = 2 } }, "number_limit");
    }

    [Test]
    public async Task DerivedSharedSubscriptionIsBoundedBeforeItCanBeReturned()
    {
        var input = RegistryJson.Parse("""
            {"usage":["subscriber"],"protocol":"MQTT","protocoloptions":{"topicfilter":"orders/+/events","sharedsubscriptiongroup":"workers"}}
            """);
        var result = EndpointDefinition.Materialize(input, options: new EndpointTemplateOptions { MaxExpandedStringBytes = 30 });
        await Assert.That(result.MqttSubscriptionFilter).IsEqualTo("$share/workers/orders/+/events");
        await Rejects(input, RegistryJson.Parse("{}"), new EndpointTemplateOptions { MaxExpandedStringBytes = 29 }, "template_string_limit");
    }

    private static async Task Rejects(RegistryJson input, RegistryJson arguments, EndpointTemplateOptions options, string code)
    {
        var failure = await Assert.That(() => EndpointDefinition.Materialize(input, arguments, options))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a finite-budget diagnostic.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
    }
}
