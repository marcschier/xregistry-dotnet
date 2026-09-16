// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    private static void ValidateMessageContentTypes(JsonElement message, CancellationToken cancellationToken)
    {
        var declared = OptionalText(message, "datacontenttype", "/datacontenttype");
        var baseline = declared is null ? null : ParseContentType(declared, "/datacontenttype");
        foreach (var (value, path, samePlane) in ContentTypeDeclarations(message))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var literal = TemplateLiteral(value, path, false);
            var parsed = ParseContentType(literal, path);
            if (!samePlane || literal != value)
            {
                continue;
            }
            if (baseline is not null && !SameContentType(baseline, parsed))
            {
                throw Invalid("Content-type declarations for the same message representation must agree.", path);
            }
            baseline ??= parsed;
        }
    }

    private static IEnumerable<(string Value, string Path, bool SamePlane)> ContentTypeDeclarations(JsonElement message)
    {
        if (SelectorIs(message, "envelope", "CloudEvents/1.0") &&
            TryObject(message, "envelopemetadata", out var envelope) &&
            TryObject(envelope, "datacontenttype", out var declaration) &&
            OptionalText(declaration, "value", "/envelopemetadata/datacontenttype/value") is { } contentType)
        {
            var binary = TryObject(message, "envelopeoptions", out var envelopeOptions) &&
                envelopeOptions.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String &&
                mode.GetString() == "binary";
            yield return (contentType, "/envelopemetadata/datacontenttype/value", binary);
        }
        if (!TryObject(message, "protocoloptions", out var options))
        {
            yield break;
        }
        if ((SelectorIs(message, "protocol", "HTTP") || SelectorIs(message, "protocol", "NATS")) &&
            options.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var header in headers.EnumerateArray())
            {
                var path = "/protocoloptions/headers/" + index.ToString(CultureInfo.InvariantCulture) + "/value";
                if (header.ValueKind == JsonValueKind.Object && SelectorIs(header, "name", "content-type") &&
                    OptionalText(header, "value", path) is { } value)
                {
                    yield return (value, path, true);
                }
                index++;
            }
        }
        else if (SelectorIs(message, "protocol", "AMQP/1.0") &&
            TryObject(options, "properties", out var properties) &&
            TryObject(properties, "content-type", out var property) &&
            OptionalText(property, "value", "/protocoloptions/properties/content-type/value") is { } amqp)
        {
            yield return (amqp, "/protocoloptions/properties/content-type/value", true);
        }
        else if (SelectorIs(message, "protocol", "MQTT/5.0") &&
            OptionalText(options, "content_type", "/protocoloptions/content_type") is { } mqtt)
        {
            yield return (mqtt, "/protocoloptions/content_type", true);
        }
        else if (SelectorIs(message, "protocol", "KAFKA") && TryObject(options, "headers", out var kafkaHeaders))
        {
            foreach (var header in kafkaHeaders.EnumerateObject())
            {
                var path = At("/protocoloptions/headers", header.Name) + "/value";
                if (string.Equals(header.Name, "content-type", StringComparison.OrdinalIgnoreCase) &&
                    header.Value.ValueKind == JsonValueKind.Object &&
                    OptionalText(header.Value, "value", path) is { } value)
                {
                    yield return (value, path, true);
                }
            }
        }
    }

    private static MediaTypeHeaderValue ParseContentType(string value, string path)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out var parsed) || parsed.MediaType is null ||
            parsed.Parameters.Any(static parameter => parameter.Value is null))
        {
            throw Invalid("The Message content type must be a valid media type with valid parameters.", path);
        }
        return parsed;
    }

    private static bool SameContentType(MediaTypeHeaderValue left, MediaTypeHeaderValue right)
    {
        if (!string.Equals(left.MediaType, right.MediaType, StringComparison.OrdinalIgnoreCase) ||
            left.Parameters.Count != right.Parameters.Count)
        {
            return false;
        }
        var first = OrderedParameters(left);
        var second = OrderedParameters(right);
        return first.Zip(second).All(static pair =>
            string.Equals(pair.First.Key, pair.Second.Key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal));
    }

    private static IOrderedEnumerable<KeyValuePair<string, string>> OrderedParameters(MediaTypeHeaderValue contentType) =>
        contentType.Parameters.Select(static parameter =>
            new KeyValuePair<string, string>(parameter.Name, ParameterValue(parameter.Value!)))
            .OrderBy(static parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static parameter => parameter.Value, StringComparer.Ordinal);

    private static string ParameterValue(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }
        var text = new StringBuilder(value.Length - 2);
        for (var index = 1; index < value.Length - 1; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length - 1)
            {
                index++;
            }
            text.Append(value[index]);
        }
        return text.ToString();
    }
}
