// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using System.Text.Json.Nodes;

namespace XRegistry.Models.Tests;

public class RegistryDomainRulesTests
{
    [Test]
    public async Task EndpointRoleCombinationsFollowExactNativeAndProtocolRules()
    {
        var roles = new[] { "producer", "consumer", "subscriber" };
        foreach (var protocol in new[]
        {
            "MQTT", "mqtt", "MQTT/3.1.1", "mQtT/5.0", "AMQP", "amqp/1.0", "NATS", "nats",
            "HTTP", "http", "KAFKA", "kafka", "MQTT/4.0", "AMQP/0.9", "NATS/1", "VendorProtocol", ""
        })
        {
            for (var length = 1; length <= 3; length++)
            {
                var combinations = (int)Math.Pow(roles.Length, length);
                for (var combination = 0; combination < combinations; combination++)
                {
                    var names = new List<string>();
                    var remaining = combination;
                    for (var index = 0; index < length; index++)
                    {
                        names.Add(roles[remaining % roles.Length]);
                        remaining /= roles.Length;
                    }
                    var sharedConnection = protocol.ToUpperInvariant() is
                        "MQTT" or "MQTT/3.1.1" or "MQTT/5.0" or "AMQP" or "AMQP/1.0" or "NATS";
                    var valid = length == 1 || length == 2 && names.Distinct(StringComparer.Ordinal).Count() == 2 &&
                        !names.Contains("producer", StringComparer.Ordinal) && sharedConnection;
                    var usage = new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
                    var input = new JsonObject { ["usage"] = usage, ["protocol"] = protocol };
                    var metadata = RegistryJson.Parse(input.ToJsonString());
                    if (valid)
                    {
                        await Assert.That(() => RegistryDomainRules.ValidateEndpointUsage(metadata.RootElement)).ThrowsNothing();
                        await Assert.That(string.Join(",", metadata.RootElement.GetProperty("usage").EnumerateArray()
                            .Select(value => value.GetString()))).IsEqualTo(string.Join(",", names));
                        await Assert.That(metadata.RootElement.GetProperty("protocol").GetString()).IsEqualTo(protocol);
                    }
                    else
                    {
                        var failure = await Assert.That(() => RegistryDomainRules.ValidateEndpointUsage(metadata.RootElement))
                            .Throws<RegistryException>() ?? throw new InvalidOperationException("An invalid role combination was accepted.");
                        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
                        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/usage");
                    }
                }
            }
        }
    }

    [Test]
    public async Task EndpointUsageRejectsMissingWrongKindsUnknownRolesAndUnqualifiedMixedRoles()
    {
        foreach (var input in new[]
        {
            "null", "true", "42", "\"endpoint\"", "[]", "{}",
            """{"usage":null}""", """{"usage":true}""", """{"usage":42}""",
            """{"usage":"producer"}""", """{"usage":{}}""", """{"usage":[]}""",
            """{"usage":[null]}""", """{"usage":[true]}""", """{"usage":[42]}""",
            """{"usage":[{}]}""", """{"usage":[[]]}""", """{"usage":[""]}""",
            """{"usage":["Producer"]}""", """{"usage":["CONSUMER"]}""", """{"usage":["vendor-role"]}""",
            """{"usage":["consumer","subscriber"]}""",
            """{"usage":["consumer","subscriber"],"protocol":null}""",
            """{"usage":["consumer","subscriber"],"protocol":42}""",
            """{"usage":["consumer","subscriber"],"protocol":["MQTT"]}""",
            """{"usage":["consumer","subscriber"],"protocol":{}}"""
        })
        {
            var failure = await Assert.That(() => RegistryDomainRules.ValidateEndpointUsage(RegistryJson.Parse(input).RootElement))
                .Throws<RegistryException>() ?? throw new InvalidOperationException("Invalid usage metadata was accepted.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid_attribute");
            await Assert.That(failure.Diagnostic.Path).IsEqualTo("/usage");
        }
    }

    [Test]
    [Arguments("""{"usage":["producer"],"protocol":"HTTP"}""")]
    [Arguments("""{"usage":["consumer"],"protocol":"KAFKA"}""")]
    [Arguments("""{"usage":["subscriber"],"protocol":"HTTP"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"MQTT/5.0"}""")]
    [Arguments("""{"usage":["consumer","subscriber"],"protocol":"AMQP/1.0"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"NATS"}""")]
    public async Task AllowedEndpointRolesAreAcceptedWithoutInventingNetworkBehavior(string input)
    {
        var metadata = RegistryJson.Parse(input);
        await Assert.That(() => RegistryDomainRules.ValidateEndpointUsage(metadata.RootElement)).ThrowsNothing();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"usage":[]}""")]
    [Arguments("""{"usage":["unknown"]}""")]
    [Arguments("""{"usage":["Producer"]}""")]
    [Arguments("""{"usage":["consumer","consumer"]}""")]
    [Arguments("""{"usage":["producer","consumer"],"protocol":"MQTT/5.0"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"HTTP"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"KAFKA"}""")]
    [Arguments("""{"usage":["subscriber","consumer"],"protocol":"invented"}""")]
    [Arguments("""{"usage":["subscriber","consumer","producer"],"protocol":"NATS"}""")]
    public async Task InvalidRoleSetsFailTheUnchangedNormativeDomainRule(string input)
    {
        var exception = await Assert.That(() => RegistryDomainRules.ValidateEndpointUsage(
            RegistryJson.Parse(input).RootElement)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("The expected domain-rule exception was not returned.");
        await Assert.That(exception.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(exception.Diagnostic.Path).IsEqualTo("/usage");
    }
}
