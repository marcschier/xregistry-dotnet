// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Models;

/// <summary>Procedural domain rules that cannot be expressed by the core scalar model aspects.</summary>
public static partial class RegistryDomainRules
{
    /// <summary>Checks context-free constraints on completed Message definition metadata, without acquiring references.</summary>
    public static void ValidateMessageMetadata(JsonElement message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Message metadata must be an object.", "");
        }

        ValidateMessageStrings(message, cancellationToken);
        RequireSelectedObject(message, "protocol", "protocoloptions");
        RequireSelectedObject(message, "envelope", "envelopemetadata");
        ValidateMessageProperties(message, cancellationToken);
        ValidateMessageProtocol(message, cancellationToken);
        ValidateMessageContentTypes(message, cancellationToken);

        var inlineSchema = Present(message, "dataschema");
        var schemaUri = Present(message, "dataschemauri");
        if (inlineSchema && schemaUri)
        {
            throw Invalid("dataschema and dataschemauri are mutually exclusive.", "/dataschemauri");
        }
        if ((inlineSchema || schemaUri) && !Present(message, "dataschemaformat"))
        {
            throw Invalid("A Message schema declaration requires dataschemaformat.", "/dataschemaformat");
        }

        if (schemaUri && message.TryGetProperty("envelope", out var envelope) &&
            envelope.ValueKind == JsonValueKind.String &&
            string.Equals(envelope.GetString(), "CloudEvents/1.0", StringComparison.OrdinalIgnoreCase) &&
            message.GetProperty("envelopemetadata").TryGetProperty("dataschema", out var declaration) &&
            declaration.ValueKind == JsonValueKind.Object &&
            declaration.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } reference && !reference.Contains('{', StringComparison.Ordinal) &&
            !string.Equals(reference, message.GetProperty("dataschemauri").GetString(), StringComparison.Ordinal))
        {
            throw Invalid("The envelope dataschema value must agree with dataschemauri.", "/envelopemetadata/dataschema/value");
        }
    }

    /// <summary>Enforces the Endpoint specification's required roles and protocol-specific allowed combinations.</summary>
    /// <remarks>This is the usage rule, not validation of all protocol options or remote endpoint behavior.</remarks>
    public static void ValidateEndpointUsage(JsonElement endpoint)
    {
        if (endpoint.ValueKind != JsonValueKind.Object || !endpoint.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Array || usage.GetArrayLength() is < 1 or > 2)
        {
            throw Invalid("Endpoint usage must be a nonempty array containing at most the allowed two roles.");
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in usage.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String ||
                value.GetString() is not ("producer" or "consumer" or "subscriber") ||
                !roles.Add(value.GetString()!))
            {
                throw Invalid("Endpoint usage contains an unknown or duplicate role.");
            }
        }

        if (roles.Count == 1)
        {
            return;
        }

        if (roles.Contains("producer") || !endpoint.TryGetProperty("protocol", out var protocol) ||
            protocol.ValueKind != JsonValueKind.String)
        {
            throw Invalid("A producer cannot be combined with another role, and mixed roles need a qualifying protocol.");
        }

        var supported = protocol.GetString()!.ToUpperInvariant() is
            "MQTT" or "MQTT/3.1.1" or "MQTT/5.0" or "AMQP" or "AMQP/1.0" or "NATS";
        if (!supported)
        {
            throw Invalid("This protocol does not combine subscription management and consumption into one endpoint.");
        }
    }

    private static void RequireSelectedObject(JsonElement metadata, string selector, string name)
    {
        if (metadata.TryGetProperty(selector, out var value) && value.ValueKind != JsonValueKind.Null &&
            (!metadata.TryGetProperty(name, out var options) || options.ValueKind != JsonValueKind.Object))
        {
            throw Invalid($"A Message {selector} selector requires a {name} object.", "/" + name);
        }
    }

    private static bool Present(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

    private static RegistryException Invalid(string message, string path = "/usage") =>
        new(new RegistryDiagnostic("invalid_attribute", path, message));
}
