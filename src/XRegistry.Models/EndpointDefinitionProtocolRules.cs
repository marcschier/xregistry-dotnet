// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using static XRegistry.Models.EndpointTemplateExpansion;

namespace XRegistry.Models;

internal static partial class EndpointDefinitionSemantics
{
    internal static string? ValidateConsumer(JsonElement endpoint, EndpointDefinitionEvaluation evaluation)
    {
        var protocol = Protocol(endpoint);
        var known = protocol is "HTTP" or "AMQP/1.0" or "MQTT/3.1.1" or "MQTT/5.0" or "KAFKA" or "NATS";
        if (!known)
        {
            evaluation.Defer("protocol_contract", "/protocol", "An explicit protocol contract must be supplied by the consumer.");
        }
        endpoint.TryGetProperty("protocoloptions", out var options);
        ValidateAddresses(protocol, options, evaluation);
        ValidateAuthorization(endpoint, options, evaluation);
        ValidateEnvelopeContentType(endpoint, options, evaluation);
        if (protocol == "KAFKA")
        {
            var consumer = HasRole(endpoint, "consumer");
            var subscriber = HasRole(endpoint, "subscriber");
            var group = options.ValueKind == JsonValueKind.Object ? Text(options, "consumergroup", OptionsPath) : null;
            if (consumer && string.IsNullOrEmpty(group) || subscriber && group is not null)
            {
                throw Invalid("/protocoloptions/consumergroup",
                    "A Kafka consumer requires a nonempty group; a subscriber must not declare a group to join.");
            }
            if (!consumer && options.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "autooffsetreset", "enableautocommit" })
                {
                    if (options.TryGetProperty(name, out _))
                    {
                        throw Invalid(At(OptionsPath, name), "The Kafka option is only valid for an existing-group consumer.");
                    }
                }
            }
        }
        if (options.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var option in options.EnumerateObject())
        {
            evaluation.CancellationToken.ThrowIfCancellationRequested();
            if (known && !IsDefinedOption(protocol, option.Name))
            {
                evaluation.Defer("extension_option", At(OptionsPath, option.Name),
                    "This extension option requires its defining contract; it has not been semantically validated.");
            }
        }
        switch (protocol)
        {
            case "MQTT/3.1.1":
            case "MQTT/5.0":
                return ValidateMqttAddressing(options, protocol, evaluation);
            case "NATS":
                ValidateNatsAddressing(options);
                break;
            case "AMQP/1.0":
                if (Text(options, "node", OptionsPath) is not null)
                {
                    evaluation.Defer("amqp_node_resolution", "/protocoloptions/node",
                        "The explicit node overrides the URI path; resolution to a terminus requires the AMQP container.");
                }
                break;
        }
        return null;
    }

    private static bool HasRole(JsonElement endpoint, string role) =>
        endpoint.GetProperty("usage").EnumerateArray().Any(value => value.GetString() == role);

    private static string? ValidateMqttAddressing(JsonElement options, string protocol, EndpointDefinitionEvaluation evaluation)
    {
        var topic = Text(options, "topic", OptionsPath);
        var filter = Text(options, "topicfilter", OptionsPath);
        if (topic is not null && filter is not null)
        {
            throw Invalid("/protocoloptions/topicfilter", "An MQTT endpoint must not declare both topic and topicfilter.");
        }
        if (topic is not null)
        {
            MqttTopic(topic, "/protocoloptions/topic", false);
        }
        if (filter is not null)
        {
            MqttTopic(filter, "/protocoloptions/topicfilter", true);
        }
        if (Text(options, "willtopic", OptionsPath) is { } willTopic)
        {
            MqttTopic(willTopic, "/protocoloptions/willtopic", false);
        }
        if (Text(options, "willmessage", OptionsPath) is { } will)
        {
            RegistryPath reference;
            try
            {
                reference = RegistryPath.Parse(will);
            }
            catch (RegistryException exception)
            {
                throw new RegistryException(new("invalid_attribute", "/protocoloptions/willmessage",
                    "The Will message must be a Message Resource or Version XID."), exception);
            }
            if (reference.IsDetails || reference.ResourceType != "messages" ||
                reference.Kind is not (RegistryPathKind.Resource or RegistryPathKind.Version))
            {
                throw Invalid("/protocoloptions/willmessage", "The Will message must be a Message Resource or Version XID.");
            }
            evaluation.Defer("message_reference", "/protocoloptions/willmessage",
                "The Will message's target and runtime payload require explicit consumer resolution.");
        }
        var group = protocol == "MQTT/5.0" ? Text(options, "sharedsubscriptiongroup", OptionsPath) : null;
        if (group is not null)
        {
            if (filter is null || group.Length == 0 || group.AsSpan().IndexOfAny('/', '+', '#') >= 0 || group.Contains('\0'))
            {
                throw Invalid("/protocoloptions/sharedsubscriptiongroup",
                    "A shared group needs topicfilter and a nonempty bare name without '/', '+' or '#'.");
            }
            var bytes = (long)Encoding.UTF8.GetByteCount(group) + Encoding.UTF8.GetByteCount(filter) + 8;
            if (bytes > evaluation.Options.MaxExpandedStringBytes)
            {
                throw Error("template_string_limit", "/protocoloptions/topicfilter",
                    "The derived shared subscription filter exceeds its UTF-8 budget.");
            }
            filter = "$share/" + group + "/" + filter;
            MqttTopic(filter, "/protocoloptions/topicfilter", true);
        }
        return filter;
    }

    private static void MqttTopic(string value, string path, bool filter)
    {
        if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > ushort.MaxValue || value.Contains('\0'))
        {
            throw Invalid(path, "MQTT topic names and filters require 1 through 65,535 UTF-8 bytes without U+0000.");
        }
        if (!filter)
        {
            if (value.AsSpan().IndexOfAny('+', '#') >= 0)
            {
                throw Invalid(path, "A concrete MQTT topic must not contain wildcards.");
            }
            return;
        }
        var levels = value.Split('/');
        for (var index = 0; index < levels.Length; index++)
        {
            var level = levels[index];
            if (level.Contains('+') && level != "+" || level.Contains('#') && (level != "#" || index != levels.Length - 1))
            {
                throw Invalid(path, "MQTT '+' occupies a complete level and '#' a complete final level.");
            }
        }
    }

    private static void ValidateNatsAddressing(JsonElement options)
    {
        var subject = Text(options, "subject", OptionsPath);
        var filter = Text(options, "subjectfilter", OptionsPath);
        if (subject is not null && filter is not null)
        {
            throw Invalid("/protocoloptions/subjectfilter", "A NATS endpoint must not declare both subject and subjectfilter.");
        }
        if (subject is not null)
        {
            NatsSubject(subject, "/protocoloptions/subject", false);
        }
        if (filter is not null)
        {
            NatsSubject(filter, "/protocoloptions/subjectfilter", true);
        }
        if (Text(options, "queuegroup", OptionsPath) is { } group &&
            (filter is null || group.Length == 0 || group.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character))))
        {
            throw Invalid("/protocoloptions/queuegroup", "A NATS queue group needs a subject filter and a nonempty, whitespace-free name.");
        }
    }

    private static void NatsSubject(string value, string path, bool filter)
    {
        if (value.Length == 0 || value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw Invalid(path, "NATS subjects must be nonempty and contain no whitespace or controls.");
        }
        var tokens = value.Split('.');
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Length == 0 || token.Contains('*') && (!filter || token != "*") ||
                token.Contains('>') && (!filter || token != ">" || index != tokens.Length - 1))
            {
                throw Invalid(path, "NATS tokens must be nonempty; '*' is a complete filter token and '>' a final filter token.");
            }
        }
    }

    private static bool IsDefinedOption(string? protocol, string name)
    {
        if (name is "deployed" or "endpoints" or "authorization")
        {
            return true;
        }
        return protocol switch
        {
            "HTTP" => name is "method" or "headers" or "query" or "apikeyname" or "apikeyin" or "plainscheme" or
                "plainusernamefield" or "plainpasswordfield",
            "AMQP/1.0" => name is "node" or "durable" or "link-properties" or "connection-properties" or "distribution-mode" or
                "connection-capabilities" or "node-capabilities" or "source-filters" or "dynamic" or "terminus-durability" or
                "expiry-policy" or "timeout" or "sender-settle-mode" or "receiver-settle-mode",
            "MQTT/3.1.1" => name is "topic" or "topicfilter" or "qos" or "retain" or "willtopic" or "willmessage" or "cleansession",
            "MQTT/5.0" => name is "topic" or "topicfilter" or "qos" or "retain" or "willtopic" or "willmessage" or
                "cleanstart" or "sessionexpiryinterval" or "sharedsubscriptiongroup" or "nolocal" or "retainaspublished" or "retainhandling",
            "KAFKA" => name is "topic" or "acks" or "key" or "partition" or "consumergroup" or "headers" or
                "keyserializer" or "valueserializer" or "autooffsetreset" or "enableautocommit",
            "NATS" => name is "subject" or "subjectfilter" or "queuegroup",
            _ => false
        };
    }
}
