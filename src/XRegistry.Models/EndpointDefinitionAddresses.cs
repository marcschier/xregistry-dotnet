// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using static XRegistry.Models.EndpointTemplateExpansion;

namespace XRegistry.Models;

internal static partial class EndpointDefinitionSemantics
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly record struct AddressParts(string Scheme, string Path, string Query, int? Port);

    internal static void ValidateAuthoredAddressShapes(JsonElement endpoint, EndpointDefinitionEvaluation evaluation)
    {
        endpoint.TryGetProperty("protocoloptions", out var options);
        ValidateAddresses(Protocol(endpoint), options, evaluation, authored: true);
    }

    private static void ValidateAddresses(string? protocol, JsonElement options, EndpointDefinitionEvaluation evaluation, bool authored = false)
    {
        const string path = "/protocoloptions/endpoints";
        if (options.ValueKind != JsonValueKind.Object || !options.TryGetProperty("endpoints", out var endpoints))
        {
            if (!authored)
            {
                evaluation.Defer("endpoint_address", path, "A concrete network address must be supplied before communication.");
            }
            return;
        }
        RequireKind(endpoints, JsonValueKind.Array, path);
        if (!authored && endpoints.GetArrayLength() == 0)
        {
            evaluation.Defer("endpoint_address", path, "A concrete network address must be supplied before communication.");
        }
        var index = 0;
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            evaluation.CancellationToken.ThrowIfCancellationRequested();
            var itemPath = At(path, (index++).ToString(CultureInfo.InvariantCulture));
            RequireKind(endpoint, JsonValueKind.Object, itemPath);
            if (protocol == "KAFKA")
            {
                ValidateKafkaAddress(endpoint, itemPath, evaluation, authored);
                continue;
            }
            var known = protocol is "HTTP" or "AMQP/1.0" or "MQTT/3.1.1" or "MQTT/5.0" or "NATS";
            var text = Text(endpoint, "uri", itemPath, required: known && !authored);
            if (text is null || authored)
            {
                continue;
            }
            var uriPath = At(itemPath, "uri");
            var address = ParseNetworkAddress(text, uriPath, evaluation.CancellationToken);
            var allowed = protocol switch
            {
                "HTTP" => address.Scheme is "http" or "https",
                "AMQP/1.0" => address.Scheme is "amqp" or "amqps",
                "MQTT/3.1.1" or "MQTT/5.0" => address.Scheme is "mqtt" or "mqtts" or "tcp" or "ssl" or "wss",
                "NATS" => address.Scheme is "nats" or "tls" or "ws",
                _ => address.Scheme is not ("file" or "data" or "javascript")
            };
            if (!allowed)
            {
                throw AddressError(uriPath, "The address scheme does not match the selected Endpoint protocol.");
            }
            if (protocol == "NATS" && address.Port is null)
            {
                throw AddressError(uriPath, "NATS endpoint addresses must include an explicit port.");
            }
            if (protocol is "MQTT/3.1.1" or "MQTT/5.0" && address.Path.Length != 0)
            {
                if (address.Scheme is "tcp" or "ssl" or "wss")
                {
                    throw AddressError(uriPath, "Informal MQTT transport schemes must not include a path, including '/'.");
                }
                try
                {
                    MqttTopic(DecodeUriText(address.Path, uriPath, evaluation.CancellationToken), uriPath, false);
                }
                catch (RegistryException exception) when (exception.Diagnostic.Code == "invalid_attribute")
                {
                    throw new RegistryException(new("invalid_endpoint_address", uriPath,
                        "The decoded MQTT URI path must be a concrete topic name."), exception);
                }
            }
            if (protocol == "AMQP/1.0" && address.Path.Length != 0 && !options.TryGetProperty("node", out _))
            {
                evaluation.Defer("amqp_node_resolution", uriPath,
                    "The URI node path is syntactically valid; resolution to a terminus requires the AMQP container.");
            }
        }
    }

    private static void ValidateKafkaAddress(JsonElement endpoint, string path, EndpointDefinitionEvaluation evaluation, bool authored)
    {
        var serversPath = At(path, "bootstrap.servers");
        if (!endpoint.TryGetProperty("bootstrap.servers", out var servers))
        {
            if (authored)
            {
                return;
            }
            throw Invalid(serversPath, "A Kafka address entry requires a nonempty bootstrap.servers array.");
        }
        RequireKind(servers, JsonValueKind.Array, serversPath);
        if (!authored && servers.GetArrayLength() == 0)
        {
            throw Invalid(serversPath, "A Kafka address entry requires at least one bootstrap server.");
        }
        if (!authored && endpoint.TryGetProperty("uri", out _))
        {
            throw Invalid(At(path, "uri"), "Kafka uses bootstrap.servers rather than uri.");
        }
        var index = 0;
        foreach (var server in servers.EnumerateArray())
        {
            evaluation.CancellationToken.ThrowIfCancellationRequested();
            var itemPath = At(serversPath, (index++).ToString(CultureInfo.InvariantCulture));
            RequireKind(server, JsonValueKind.String, itemPath);
            if (authored)
            {
                continue;
            }
            var text = server.GetString()!;
            var separator = text.IndexOf("://", StringComparison.Ordinal);
            if (separator >= 0)
            {
                var listener = text[..separator];
                if (listener.Length == 0 || !char.IsAsciiLetter(listener[0]) ||
                    listener.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
                {
                    throw AddressError(itemPath, "The Kafka listener prefix is malformed.");
                }
                if (listener.ToUpperInvariant() is not ("SSL" or "PLAINTEXT" or "SASL_SSL" or "SASL_PLAINTEXT"))
                {
                    evaluation.Defer("kafka_listener_security", itemPath,
                        "An extension listener name needs an explicit listener-to-security-protocol mapping.");
                }
                text = text[(separator + 3)..];
            }
            var address = ParseNetworkAddress("tcp://" + text, itemPath, evaluation.CancellationToken);
            if (address.Port is null || address.Path.Length != 0 || address.Query.Length != 0 || text.Contains(','))
            {
                throw AddressError(itemPath, "Each Kafka bootstrap value must identify one host and explicit port, without a path or query.");
            }
        }
    }

    private static AddressParts ParseNetworkAddress(string value, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !IsReferenceUri(value, out var absolute) || !absolute ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            throw AddressError(path, "The address must be a well-formed, absolute network URI.");
        }
        var start = schemeEnd + 3;
        var end = value.AsSpan(start).IndexOfAny('/', '?', '#');
        end = end < 0 ? value.Length : start + end;
        var authority = value.AsSpan(start, end - start);
        if (authority.Contains('@'))
        {
            throw Error("endpoint_credentials", path, "Endpoint metadata must not contain URI credentials.");
        }
        if (value.Contains('#'))
        {
            throw AddressError(path, "A communication endpoint must not contain a fragment.");
        }
        var colon = authority.StartsWith("[", StringComparison.Ordinal) ? authority.IndexOf(']') + 1 : authority.LastIndexOf(':');
        int? port = null;
        if (colon >= 0 && colon < authority.Length && authority[colon] == ':')
        {
            var digits = authority[(colon + 1)..];
            if (digits.Length == 0 || digits.ContainsAnyExcept("0123456789".AsSpan()) ||
                !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
            {
                throw AddressError(path, "An explicit network port must be between 1 and 65,535.");
            }
            port = number;
        }
        var query = value.IndexOf('?', end);
        var uriPath = value[end..(query < 0 ? value.Length : query)];
        var uriQuery = query < 0 ? "" : value[(query + 1)..];
        var decoded = DecodeUriText(uriPath + (query < 0 ? "" : "?" + uriQuery), path, cancellationToken);
        if (decoded.Any(char.IsControl))
        {
            throw AddressError(path, "A network endpoint must not contain encoded control characters.");
        }
        RejectCredentialQuery(uriQuery, path, cancellationToken);
        return new AddressParts(uri.Scheme, uriPath, uriQuery, port);
    }

    private static bool IsReferenceUri(string value, out bool absolute)
    {
        absolute = false;
        var fragment = value.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0 && !UriPart(value.AsSpan(fragment + 1), slash: true, question: true))
        {
            return false;
        }
        var body = fragment < 0 ? value.AsSpan() : value.AsSpan(0, fragment);
        var query = body.IndexOf('?');
        if (query >= 0 && !UriPart(body[(query + 1)..], slash: true, question: true))
        {
            return false;
        }
        var hierarchy = query < 0 ? body : body[..query];
        var colon = hierarchy.IndexOf(':');
        var slash = hierarchy.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash))
        {
            if (!Uri.CheckSchemeName(hierarchy[..colon].ToString()))
            {
                return false;
            }
            absolute = true;
            hierarchy = hierarchy[(colon + 1)..];
        }
        if (hierarchy.StartsWith("//", StringComparison.Ordinal))
        {
            var end = hierarchy[2..].IndexOf('/');
            end = end < 0 ? hierarchy.Length : end + 2;
            if (!ReferenceAuthority(hierarchy[2..end]))
            {
                return false;
            }
            hierarchy = hierarchy[end..];
        }
        return UriPart(hierarchy, slash: true);
    }

    private static bool ReferenceAuthority(ReadOnlySpan<char> value)
    {
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            if (!UriPart(value[..at], allowAt: false))
            {
                return false;
            }
            value = value[(at + 1)..];
        }
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var end = value.IndexOf(']');
            if (end < 0 || !IPAddress.TryParse(value[1..end], out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
            return end + 1 == value.Length || value[end + 1] == ':' &&
                !value[(end + 2)..].ContainsAnyExcept("0123456789".AsSpan());
        }
        var colon = value.IndexOf(':');
        return colon < 0 ? UriPart(value, allowAt: false, allowColon: false) :
            UriPart(value[..colon], allowAt: false, allowColon: false) &&
            !value[(colon + 1)..].ContainsAnyExcept("0123456789".AsSpan());
    }

    private static bool UriPart(ReadOnlySpan<char> value, bool slash = false, bool question = false,
        bool allowAt = true, bool allowColon = true)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length || !char.IsAsciiHexDigit(value[index + 1]) || !char.IsAsciiHexDigit(value[index + 2]))
                {
                    return false;
                }
                index += 2;
            }
            else if (!(char.IsAsciiLetterOrDigit(character) || "-._~!$&'()*+,;=".Contains(character, StringComparison.Ordinal) ||
                slash && character == '/' || question && character == '?' || allowAt && character == '@' ||
                allowColon && character == ':'))
            {
                return false;
            }
        }
        return true;
    }

    private static string DecodeUriText(string text, string path, CancellationToken cancellationToken)
    {
        var bytes = new byte[text.Length];
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text[index] == '%')
            {
                bytes[count++] = (byte)((Hex(text[index + 1]) << 4) | Hex(text[index + 2]));
                index += 2;
            }
            else
            {
                bytes[count++] = (byte)text[index];
            }
        }
        try
        {
            return StrictUtf8.GetString(bytes.AsSpan(0, count));
        }
        catch (DecoderFallbackException exception)
        {
            throw new RegistryException(new("invalid_endpoint_address", path, "Percent-encoded endpoint text must be valid UTF-8."), exception);
        }
    }

    private static int Hex(char character) => character <= '9' ? character - '0' : (character | 0x20) - 'a' + 10;

    internal static bool ValidateReference(string value, string path)
    {
        if (value.Length == 0 || !IsReferenceUri(value, out var absolute))
        {
            throw Invalid(path, "The value must be a valid URI reference.");
        }
        return absolute;
    }

    internal static bool ReferenceHasUserInfo(string value)
    {
        var start = 2;
        if (!value.StartsWith("//", StringComparison.Ordinal))
        {
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            var slash = value.IndexOf('/', StringComparison.Ordinal);
            if (colon < 0 || slash >= 0 && slash < colon || !value.AsSpan(colon + 1).StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }
            start = colon + 3;
        }
        var end = value.AsSpan(start).IndexOfAny('/', '?', '#');
        return value.AsSpan(start, end < 0 ? value.Length - start : end).Contains('@');
    }

    private static RegistryException AddressError(string path, string message) => Error("invalid_endpoint_address", path, message);
}
