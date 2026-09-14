using System.Security.Claims;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class EndpointAuthoredStorageTests
{
    [Test]
    public async Task MetadataRoundTripPreservesValuesWithoutRequiringJsonEscapeSpelling()
    {
        var engine = Create("""
            {"groups":{"controls":{"singular":"control","attributes":{"payload":{"type":"any"}}}}}
            """);
        const string expected = """
            {"key+>":{"value":"{+unrestricted}","topic":"orders/+","subject":"orders.>",
             "number":9007199254740993,"exponent":1.2300e+100,"flag":false,"items":["first","second"],"nothing":null}}
            """;
        await Send(engine, RegistryAction.Replace, "/controls/metadata", "{\"payload\":" + expected + "}");
        var read = (await Send(engine, RegistryAction.Read, "/controls/metadata")).Metadata!;
        await Assert.That(MetadataEqual(read.RootElement.GetProperty("payload"), RegistryJson.Parse(expected).RootElement)).IsTrue();
    }

    [Test]
    public async Task MetadataComparisonAcceptsEscapedKeysValuesWhitespaceAndObjectOrdering()
    {
        var expected = RegistryJson.Parse("""{"key+>":"orders.>","items":[1e3,false,null,"{+unrestricted}"]}""");
        var received = RegistryJson.Parse("""
            {
              "items" : [ 1e3, false, null, "{\u002Bunrestricted}" ],
              "key\u002B\u003E" : "orders.\u003E"
            }
            """);
        await Assert.That(MetadataEqual(received.RootElement, expected.RootElement)).IsTrue();
    }

    [Test]
    [Arguments("""{"Key+>":[9007199254740993,false,null,"orders.>"]}""")]
    [Arguments("""{"other":[9007199254740993,false,null,"orders.>"]}""")]
    [Arguments("""{"key+>":[9007199254740993,false,null,"orders.>"],"extra":0}""")]
    [Arguments("""{"key+>":["9007199254740993",false,null,"orders.>"]}""")]
    [Arguments("""{"key+>":[9007199254740992,false,null,"orders.>"]}""")]
    [Arguments("""{"key+>":[9007199254740993,true,null,"orders.>"]}""")]
    [Arguments("""{"key+>":[9007199254740993,false,"null","orders.>"]}""")]
    [Arguments("""{"key+>":[9007199254740993,false,null,"orders.\\u003E"]}""")]
    [Arguments("""{"key+>":[false,9007199254740993,null,"orders.>"]}""")]
    [Arguments("""{"key+>":[9007199254740993,false,null]}""")]
    public async Task MetadataComparisonRejectsChangedKeysKindsLiteralsNumbersAndArrayOrder(string actual)
    {
        var expected = RegistryJson.Parse("""{"key+>":[9007199254740993,false,null,"orders.>"]}""");
        await Assert.That(MetadataEqual(RegistryJson.Parse(actual).RootElement, expected.RootElement)).IsFalse();
    }

    [Test]
    public async Task MetadataCredentialCheckExaminesDecodedKeysAndValues()
    {
        var nestedValue = RegistryJson.Parse("""{"items":[{"value":"N\u0045VER-FORWARD-THIS"}]}""");
        var escapedKey = RegistryJson.Parse("""{"N\u0045VER-FORWARD-THIS":null}""");
        await Assert.That(ContainsMetadataText(nestedValue.RootElement, "NEVER-FORWARD-THIS")).IsTrue();
        await Assert.That(ContainsMetadataText(escapedKey.RootElement, "NEVER-FORWARD-THIS")).IsTrue();
        await Assert.That(ContainsMetadataText(RegistryJson.Parse("""{"items":["safe",false,null]}""").RootElement,
            "NEVER-FORWARD-THIS")).IsFalse();
    }

    [Test]
    [Arguments(RegistryModelKind.Endpoint)]
    [Arguments(RegistryModelKind.CloudEvents)]
    public async Task PackagedEndpointRoundTripsUnresolvedUriMapKeyAndEnumWithoutAcquisition(RegistryModelKind kind)
    {
        var engine = Domain(kind);
        var authored = RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP","protocoloptions":{
             "endpoints":[{"uri":"https://192.0.2.1:1/{tenant}/events"}],
             "query":{"{parameter}":"{tenant}"},"apikeyin":"{placement}",
             "authorization":[{"type":"OAuth2","authorityuri":"https://192.0.2.1:1/auth/{tenant}"}]}}
            """);
        var caller = new RegistryOperationContext(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "template-author"), new Claim("access_token", "NEVER-FORWARD-THIS")], "test")));
        var result = await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/endpoints/e"))
        {
            Metadata = authored
        }, caller);
        await Assert.That(result.Kind).IsEqualTo(RegistryResultKind.Created);
        var stored = (await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!;
        var options = stored.RootElement.GetProperty("protocoloptions");
        await Assert.That(options.GetProperty("endpoints")[0].GetProperty("uri").GetString())
            .IsEqualTo("https://192.0.2.1:1/{tenant}/events");
        await Assert.That(options.GetProperty("query").GetProperty("{parameter}").GetString()).IsEqualTo("{tenant}");
        await Assert.That(options.GetProperty("apikeyin").GetString()).IsEqualTo("{placement}");
        await Assert.That(options.GetProperty("authorization")[0].GetProperty("authorityuri").GetString())
            .IsEqualTo("https://192.0.2.1:1/auth/{tenant}");
        await Assert.That(ContainsMetadataText(stored.RootElement, "NEVER-FORWARD-THIS")).IsFalse();
        await Assert.That(options.TryGetProperty("deployed", out _)).IsFalse();
        var resolved = EndpointDefinition.Materialize(stored,
            RegistryJson.Parse("""{"tenant":"north/west","parameter":"filter","placement":"header"}"""));
        await Assert.That(resolved.GetProtocolOption("endpoints")[0].GetProperty("uri").GetString())
            .IsEqualTo("https://192.0.2.1:1/north%2Fwest/events");
        await Assert.That(resolved.GetProtocolOption("query").GetProperty("filter").GetString()).IsEqualTo("north%2Fwest");
        await Assert.That(resolved.GetProtocolOption("apikeyin").GetString()).IsEqualTo("header");
        await Assert.That(resolved.DeferredChecks.Any(check => check.Code == "authorization_configuration")).IsTrue();
    }

    [Test]
    public async Task AmqpStringEnumsAndExactNativeValuesSurvivePersistenceRestart()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Domain(RegistryModelKind.Endpoint, store);
        await Send(engine, RegistryAction.Replace, "/endpoints/e", """
            {"usage":["consumer"],"protocol":"AMQP/1.0","protocoloptions":{
             "distribution-mode":"{mode}","durable":false,"timeout":1e3,
             "source-filters":{"{filter}":{"large":123456789012345678901234567890,"flag":true,"null":null}}}}
            """);
        var restarted = Domain(RegistryModelKind.Endpoint, store);
        var stored = (await Send(restarted, RegistryAction.Read, "/endpoints/e")).Metadata!;
        var options = stored.RootElement.GetProperty("protocoloptions");
        await Assert.That(options.GetProperty("distribution-mode").GetString()).IsEqualTo("{mode}");
        await Assert.That(options.GetProperty("durable").GetBoolean()).IsFalse();
        await Assert.That(options.GetProperty("timeout").GetRawText()).IsEqualTo("1e3");
        await Assert.That(MetadataEqual(options.GetProperty("source-filters").GetProperty("{filter}"),
            RegistryJson.Parse("""{"large":123456789012345678901234567890,"flag":true,"null":null}""").RootElement)).IsTrue();
        var resolved = EndpointDefinition.Materialize(stored, RegistryJson.Parse("""{"mode":"copy","filter":"selector"}"""));
        await Assert.That(resolved.GetProtocolOption("distribution-mode").GetString()).IsEqualTo("copy");
        await Assert.That(resolved.GetProtocolOption("source-filters").GetProperty("selector").GetProperty("large").GetRawText())
            .IsEqualTo("123456789012345678901234567890");
        var before = stored.RootElement;
        await ExpectCode(() => Send(restarted, RegistryAction.Patch, "/endpoints/e",
            """{"protocoloptions":{"durable":"{flag}"}}"""), "invalid_attribute");
        await Assert.That(MetadataEqual((await Send(restarted, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement, before)).IsTrue();
        await Assert.That((await restarted.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("HTTP", "[]")]
    [Arguments("HTTP", """{"query":{"{parameter}":17}}""")]
    [Arguments("HTTP", """{"endpoints":[{"uri":true}]}""")]
    [Arguments("HTTP", """{"headers":[{"name":"X-A","value":false}]}""")]
    [Arguments("AMQP/1.0", """{"durable":"{flag}"}""")]
    [Arguments("MQTT/5.0", """{"qos":"{qos}"}""")]
    [Arguments("KAFKA", """{"endpoints":[{"bootstrap.servers":"{servers}"}]}""")]
    [Arguments("NATS", """{"subject":42}""")]
    public async Task InvalidAuthoredShapeRollsBackRegistryAndOutbox(string protocol, string options)
    {
        var engine = Domain(RegistryModelKind.Endpoint);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/e",
            Definition(protocol, options)), "invalid_attribute");
        await Empty(engine);
    }

    [Test]
    [Arguments("""{"query":{"{+parameter}":"v"}}""")]
    [Arguments("""{"endpoints":[{"uri":"https://example.com/{x,y}"}]}""")]
    [Arguments("""{"headers":[{"{field}":"X-A","value":"v"}]}""")]
    [Arguments("""{"authorization":[{"{field}":"SASL"}]}""")]
    public async Task InvalidAuthoredGrammarUsesTheExistingServerAttributeFailure(string options)
    {
        var engine = Domain(RegistryModelKind.Endpoint);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/e", Definition("HTTP", options)), "invalid_attribute");
        await Empty(engine);
    }

    [Test]
    public async Task InvalidAuthoredPatchAndNestedBatchCannotPublishPartialState()
    {
        var engine = Domain(RegistryModelKind.Endpoint);
        await Send(engine, RegistryAction.Replace, "/endpoints/e", Definition("HTTP", """{"query":{"{key}":"original"}}"""));
        var before = (await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement;
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/endpoints/e",
            """{"protocoloptions":{"query":{"{key}":42}}}"""), "invalid_attribute");
        await Assert.That(MetadataEqual((await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement, before)).IsTrue();
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);

        var empty = Domain(RegistryModelKind.Endpoint);
        await ExpectCode(() => Send(empty, RegistryAction.Post, "/", """
            {"endpoints":{"a-valid":{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"query":{"{key}":"v"}}},
             "z-invalid":{"usage":["producer"],"protocol":"MQTT/5.0","protocoloptions":{"retain":"{flag}"}}}}
            """), "invalid_attribute");
        await Empty(empty);
    }

    [Test]
    [Arguments("HTTP", """{"endpoints":[{"uri":"not an absolute HTTP URI"}]}""", "endpoints/0/uri", "not an absolute HTTP URI")]
    [Arguments("AMQP/1.0", """{"distribution-mode":"invalid"}""", "distribution-mode", "invalid")]
    [Arguments("MQTT/5.0", """{"topic":"orders/#"}""", "topic", "orders/#")]
    [Arguments("MQTT/5.0", """{"topic":"orders","topicfilter":"orders/+"}""", "topicfilter", "orders/+")]
    [Arguments("KAFKA", """{"autooffsetreset":"earliest","enableautocommit":false}""", "autooffsetreset", "earliest")]
    [Arguments("NATS", """{"subject":"orders.*"}""", "subject", "orders.*")]
    public async Task PassiveStorageLeavesConsumerOnlyViolationsToMaterialization(string protocol, string options, string field, string expected)
    {
        var engine = Domain(RegistryModelKind.Endpoint);
        await Send(engine, RegistryAction.Replace, "/endpoints/e", Definition(protocol, options));
        var stored = (await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!;
        await Assert.That(MetadataEqual(stored.RootElement.GetProperty("protocoloptions"), RegistryJson.Parse(options).RootElement)).IsTrue();
        var value = stored.RootElement.GetProperty("protocoloptions");
        foreach (var part in field.Split('/'))
        {
            value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)] : value.GetProperty(part);
        }
        await Assert.That(value.GetString()).IsEqualTo(expected);
        var failure = await Assert.That(() => EndpointDefinition.Materialize(stored)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a resolved consumer violation.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/protocoloptions/" + field);
    }

    [Test]
    public async Task AbstractEndpointsStayAbstractAndOptionDefaultsAreNotStored()
    {
        var engine = Domain(RegistryModelKind.Endpoint);
        var abstractResult = await Send(engine, RegistryAction.Replace, "/endpoints/abstract", """{"usage":["consumer"],"protocol":"KAFKA"}""");
        await Assert.That(abstractResult.Metadata!.RootElement.TryGetProperty("protocoloptions", out _)).IsFalse();
        var emptyOptions = await Send(engine, RegistryAction.Replace, "/endpoints/empty", Definition("HTTP", "{}"));
        var empty = emptyOptions.Metadata!.RootElement.GetProperty("protocoloptions");
        await Assert.That(empty.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(empty.EnumerateObject().Any()).IsFalse();
        var consumer = EndpointDefinition.Materialize(emptyOptions.Metadata);
        await Assert.That(consumer.GetProtocolOption("method").GetString()).IsEqualTo("POST");
        await Assert.That(consumer.IsDeployed).IsTrue();
        var undeployed = await Send(engine, RegistryAction.Replace, "/endpoints/not-deployed",
            Definition("HTTP", """{"deployed":false}"""));
        await Assert.That(undeployed.Metadata!.RootElement.GetProperty("protocoloptions").GetProperty("deployed").GetBoolean()).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("https://xregistry.io/xreg/domains/message/specs/model.json")]
    public async Task UnmarkedAndOtherDomainCustomModelsDoNotAcquireEndpointRules(string marker)
    {
        var annotation = marker.Length == 0 ? "" : "\"modelcompatiblewith\":\"" + marker + "\",";
        var model = """
            {"groups":{"endpoints":{"singular":"endpoint",ANNOTATION
             "attributes":{"protocol":{"type":"string"},"protocoloptions":{"type":"any"}}}}}
            """.Replace("ANNOTATION", annotation, StringComparison.Ordinal);
        var engine = Create(model);
        var result = await Send(engine, RegistryAction.Replace, "/endpoints/custom",
            """{"protocol":"HTTP","protocoloptions":["{+unrestricted}",42,false]}""");
        var options = result.Metadata!.RootElement.GetProperty("protocoloptions");
        await Assert.That(options.GetArrayLength()).IsEqualTo(3);
        await Assert.That(options[0].GetString()).IsEqualTo("{+unrestricted}");
        await Assert.That(options[1].GetInt32()).IsEqualTo(42);
        await Assert.That(options[2].GetBoolean()).IsFalse();
        await Assert.That(result.Metadata.RootElement.TryGetProperty("usage", out _)).IsFalse();
    }

    [Test]
    public async Task ExplicitEndpointMarkerActivatesAuthoredRulesUnderAnotherGroupName()
    {
        var engine = Create("""
            {"groups":{"ports":{"singular":"port",
             "modelcompatiblewith":"https://xregistry.io/xreg/domains/endpoint/specs/model.json",
             "attributes":{"usage":{"type":"array","item":{"type":"string"}},"protocol":{"type":"string"},"protocoloptions":{"type":"any"}}}}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/ports/invalid",
            Definition("MQTT/5.0", """{"qos":"{qos}"}""")), "invalid_attribute");
        var created = await Send(engine, RegistryAction.Replace, "/ports/valid",
            Definition("MQTT/5.0", """{"qos":2,"topic":"{tenant}/orders"}"""));
        await Assert.That(created.Metadata!.RootElement.GetProperty("protocoloptions").GetProperty("topic").GetString()).IsEqualTo("{tenant}/orders");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
    }

    [Test]
    public async Task AdoptingEndpointMarkerValidatesExistingAuthoredShapesAtomically()
    {
        const string model = """
            {"groups":{"endpoints":{"singular":"endpoint","attributes":{
             "usage":{"type":"array","item":{"type":"string"}},"protocol":{"type":"string"},"protocoloptions":{"type":"any"}}}}}
            """;
        var engine = Create(model);
        await Send(engine, RegistryAction.Replace, "/endpoints/e", Definition("HTTP", """{"query":{"key":42}}"""));
        var before = (await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement;
        var marked = model.Replace("\"singular\":\"endpoint\"",
            "\"singular\":\"endpoint\",\"modelcompatiblewith\":\"https://xregistry.io/xreg/domains/endpoint/specs/model.json\"",
            StringComparison.Ordinal);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", marked), "model_compliance_error");
        var source = (await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement;
        await Assert.That(source.GetProperty("groups").GetProperty("endpoints").TryGetProperty("modelcompatiblewith", out _)).IsFalse();
        await Assert.That(MetadataEqual((await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement, before)).IsTrue();
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        await Send(engine, RegistryAction.Patch, "/endpoints/e", """{"protocoloptions":{"query":{"key":"{value}"}}}""");
        await Send(engine, RegistryAction.Replace, "/modelsource", marked);
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/endpoints/e", """{"protocoloptions":{"query":{"key":42}}}"""),
            "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement
            .GetProperty("protocoloptions").GetProperty("query").GetProperty("key").GetString()).IsEqualTo("{value}");
    }

    [Test]
    [Arguments("""{"usage":["producer","consumer"],"protocol":"HTTP"}""")]
    [Arguments("""{"usage":["producer"],"envelope":"CloudEvents/1.0","envelopeoptions":{"mode":"binary","format":"application/json"}}""")]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"type":""}]}}""")]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP","protocoloptions":{"authorization":[{"authorityuri":""}]}}""")]
    public async Task CommonEndpointRulesStillRejectInvalidMetadataBeforePublication(string metadata)
    {
        var engine = Domain(RegistryModelKind.Endpoint);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/e", metadata), "invalid_attribute");
        await Empty(engine);
    }

    [Test]
    public async Task EndpointMessageGroupConstraintsStillRunWithOpaqueOptions()
    {
        var engine = Domain(RegistryModelKind.Endpoint);
        await Send(engine, RegistryAction.Replace, "/endpoints/e", Definition("HTTP", """{"query":{"{key}":"{value}"}}"""));
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/endpoints/e/messages/m",
            """{"protocol":"NATS","protocoloptions":{}}"""), "invalid_attribute");
        var endpoint = (await Send(engine, RegistryAction.Read, "/endpoints/e")).Metadata!.RootElement;
        await Assert.That(endpoint.GetProperty("messagescount").GetInt32()).IsEqualTo(0);
        await Assert.That(endpoint.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
    }

    [Test]
    public async Task GenericCoreUriMapNameEnumAndFullUriTemplateRulesRemainUnchanged()
    {
        var engine = Create("""
            {"groups":{"controls":{"singular":"control","attributes":{
             "uri":{"type":"uri"},"values":{"type":"map","item":{"type":"string"}},
             "mode":{"type":"string","enum":["move","copy"]},"template":{"type":"uritemplate"}}}}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/controls/uri",
            """{"uri":"https://example.com/{tenant}"}"""), "invalid_attribute");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/controls/name",
            """{"values":{"{key}":"value"}}"""), "invalid_attribute");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/controls/enum",
            """{"mode":"{mode}"}"""), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        var valid = await Send(engine, RegistryAction.Replace, "/controls/valid",
            """{"uri":"https://example.com/a","values":{"key":"value"},"mode":"move","template":"{+path}"}""");
        await Assert.That(valid.Metadata!.RootElement.GetProperty("template").GetString()).IsEqualTo("{+path}");
        await Assert.That(valid.Metadata.RootElement.GetProperty("values").GetProperty("key").GetString()).IsEqualTo("value");
        await Assert.That(valid.Metadata.RootElement.GetProperty("mode").GetString()).IsEqualTo("move");
    }

    private static string Definition(string protocol, string options) =>
        $$$"""{"usage":["producer"],"protocol":"{{{protocol}}}","protocoloptions":{{{options}}}}""";

    private static bool MetadataEqual(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind)
        {
            return false;
        }
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                if (actual.EnumerateObject().Count() != expected.EnumerateObject().Count())
                {
                    return false;
                }
                foreach (var property in expected.EnumerateObject())
                {
                    if (!actual.TryGetProperty(property.Name, out var member) || !MetadataEqual(member, property.Value))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.Array:
                if (actual.GetArrayLength() != expected.GetArrayLength())
                {
                    return false;
                }
                for (var index = 0; index < expected.GetArrayLength(); index++)
                {
                    if (!MetadataEqual(actual[index], expected[index]))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.String:
                return string.Equals(actual.GetString(), expected.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return string.Equals(actual.GetRawText(), expected.GetRawText(), StringComparison.Ordinal);
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                throw new InvalidOperationException("Metadata comparisons require defined JSON values.");
        }
    }

    private static bool ContainsMetadataText(JsonElement value, string text) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains(text, StringComparison.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsMetadataText(item, text)),
        JsonValueKind.Object => value.EnumerateObject().Any(property =>
            property.Name.Contains(text, StringComparison.Ordinal) || ContainsMetadataText(property.Value, text)),
        _ => false
    };

    private static RegistryEngine Domain(RegistryModelKind kind, IRegistryPersistence? persistence = null) =>
        new(new RegistryEngineOptions
        {
            RegistryId = "endpoint-authored",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = BuiltInRegistryModels.Compile(kind),
            AllowAnonymousReads = true
        }, persistence ?? new InMemoryRegistryPersistence(), new PermitPolicy());

    private static async Task Empty(RegistryEngine engine)
    {
        var root = (await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement;
        await Assert.That(root.GetProperty("endpointscount").GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }
}
