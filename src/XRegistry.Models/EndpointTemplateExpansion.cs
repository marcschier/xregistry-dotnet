// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XRegistry.Models;

internal sealed class EndpointTemplateExpansion(EndpointTemplateOptions options, string? protocol, bool resolving,
    CancellationToken cancellationToken)
{
    private static readonly string[] RecordPrefixes = ["/protocoloptions/endpoints/", "/protocoloptions/authorization/"];
    private readonly Dictionary<string, JsonElement> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _encoded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);
    private int _expansions;
    private int _expansionBytes;
    private int _nodes;

    internal static RegistryJson Resolve(RegistryJson authored, RegistryJson? arguments,
        EndpointTemplateOptions options, CancellationToken cancellationToken)
    {
        if (authored.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw Error("invalid_attribute", "", "An Endpoint definition must be an object.");
        }
        if (arguments is not null && arguments.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw Error("invalid_template_arguments", "", "Template arguments must be an object.");
        }
        var expansion = new EndpointTemplateExpansion(options, EndpointDefinitionSemantics.Protocol(authored.RootElement), true, cancellationToken);
        if (arguments is not null)
        {
            foreach (var binding in arguments.RootElement.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expansion._bindings.Count == options.MaxVariables)
                {
                    throw Error("template_variable_limit", "", "The supplied bindings exceed their count budget.");
                }
                expansion._bindings.Add(binding.Name, binding.Value);
            }
        }
        return RegistryJson.Create(writer => expansion.Write(writer, authored.RootElement, "", false, 0), options.JsonLimits);
    }

    internal static void ValidateAuthored(RegistryJson authored, EndpointTemplateOptions options, CancellationToken cancellationToken)
    {
        var validation = new EndpointTemplateExpansion(options, EndpointDefinitionSemantics.Protocol(authored.RootElement), false, cancellationToken);
        validation.Write(null, authored.RootElement, "", false, 0);
    }

    private void Write(Utf8JsonWriter? writer, JsonElement value, string path, bool expand, int depth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_nodes == options.JsonLimits.MaxNodes)
        {
            throw Error("node_limit", path, "The resolved metadata exceeds its JSON value budget.");
        }
        _nodes++;
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && depth >= options.JsonLimits.MaxDepth)
        {
            throw Error("depth_limit", path, "The resolved metadata exceeds its nesting budget.");
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer?.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    var record = expand && IsRecordPath(path);
                    if (record && property.Name.AsSpan().IndexOfAny('{', '}') >= 0)
                    {
                        throw Error("invalid_endpoint_template", At(path, property.Name),
                            "Placeholders apply to map keys, not the declared member names of Endpoint, authorization, or header records.");
                    }
                    var name = expand && !record ? Expand(property.Name, At(path, property.Name)) : property.Name;
                    if (!names.Add(name))
                    {
                        throw Error("template_key_collision", At(path, name), "Expanded map keys must be unique.");
                    }
                    writer?.WritePropertyName(name);
                    Write(writer, property.Value, At(path, name), expand || path.Length == 0 && name == "protocoloptions", depth + 1);
                }
                writer?.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer?.WriteStartArray();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    Write(writer, item, At(path, (index++).ToString(CultureInfo.InvariantCulture)), expand, depth + 1);
                }
                writer?.WriteEndArray();
                break;
            case JsonValueKind.String when expand:
                var text = Expand(value.GetString()!, path, IsUriValuePath(path));
                writer?.WriteStringValue(text);
                break;
            default:
                if (writer is not null)
                {
                    value.WriteTo(writer);
                }
                break;
        }
    }

    private string Expand(string value, string path, bool uri = false)
    {
        if (!resolving && Encoding.UTF8.GetByteCount(value) > options.MaxExpandedStringBytes)
        {
            throw Error("template_string_limit", path, "An authored protocol-option string exceeds its UTF-8 budget.");
        }
        if (value.AsSpan().IndexOfAny('{', '}') < 0)
        {
            if (Encoding.UTF8.GetByteCount(value) > options.MaxExpandedStringBytes)
            {
                throw Error("template_string_limit", path, "A resolved protocol-option string exceeds its UTF-8 budget.");
            }
            return value;
        }
        var result = resolving ? new StringBuilder() : null;
        var resultBytes = 0;
        var index = 0;
        while (index < value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = value.AsSpan(index).IndexOfAny('{', '}');
            if (next < 0)
            {
                if (result is not null)
                {
                    AppendLiteral(result, value.AsSpan(index), path, uri, ref resultBytes);
                }
                break;
            }
            if (result is not null)
            {
                AppendLiteral(result, value.AsSpan(index, next), path, uri, ref resultBytes);
            }
            index += next;
            if (value[index] == '}')
            {
                throw Error("invalid_endpoint_template", path, "A placeholder has an unmatched closing brace.");
            }
            var end = value.IndexOf('}', index + 1);
            if (end < 0)
            {
                throw Error("invalid_endpoint_template", path, "A placeholder has no closing brace.");
            }
            var name = value[(index + 1)..end];
            if (!IsVariableName(name))
            {
                throw Error("invalid_endpoint_template", path,
                    "Endpoint placeholders require one unmodified RFC 6570 Level-1 variable name.");
            }
            if (!resolving)
            {
                CountOccurrence(path);
                if (_referenced.Add(name) && _referenced.Count > options.MaxVariables)
                {
                    throw Error("template_variable_limit", path, "Authored placeholders exceed their distinct-variable budget.");
                }
                index = end + 1;
                continue;
            }
            if (!_bindings.TryGetValue(name, out var argument) ||
                argument.ValueKind == JsonValueKind.Null)
            {
                throw Error("undefined_template_variable", path, "A placeholder has no explicit value.");
            }
            if (argument.ValueKind != JsonValueKind.String)
            {
                throw Error("invalid_template_arguments", path, "A Level-1 variable must have a string value.");
            }
            CountOccurrence(path);
            if (!_encoded.TryGetValue(name, out var encoded))
            {
                var text = argument.GetString()!;
                var bytes = 0L;
                foreach (var rune in text.EnumerateRunes())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bytes += rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) ||
                        "-._~".Contains((char)rune.Value, StringComparison.Ordinal)) ? 1 : 3 * rune.Utf8SequenceLength;
                    if (bytes > options.MaxExpandedStringBytes)
                    {
                        throw Error("template_string_limit", path, "An encoded binding exceeds the resolved string budget.");
                    }
                    if (bytes > options.MaxExpansionBytes - _expansionBytes)
                    {
                        throw Error("template_expansion_byte_limit", path, "Substitutions exceed their aggregate byte budget.");
                    }
                }
                encoded = Uri.EscapeDataString(text);
                _encoded.Add(name, encoded);
            }
            if (encoded.Length > options.MaxExpansionBytes - _expansionBytes)
            {
                throw Error("template_expansion_byte_limit", path, "Substitutions exceed their aggregate byte budget.");
            }
            _expansionBytes += encoded.Length;
            if (result is not null)
            {
                Append(result, encoded, path, ref resultBytes);
            }
            index = end + 1;
        }
        return result is null ? value : result.ToString();
    }

    private void CountOccurrence(string path)
    {
        if (_expansions == options.MaxExpansions)
        {
            throw Error("template_expansion_limit", path, "The endpoint exceeds its placeholder occurrence budget.");
        }
        _expansions++;
    }

    private void AppendLiteral(StringBuilder result, ReadOnlySpan<char> text, string path, bool uri, ref int bytes)
    {
        if (!uri)
        {
            Append(result, text, path, ref bytes);
            return;
        }
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = text[index];
            if (character == '%')
            {
                if (index + 2 >= text.Length || !char.IsAsciiHexDigit(text[index + 1]) || !char.IsAsciiHexDigit(text[index + 2]))
                {
                    throw Error("invalid_endpoint_template", path, "A URI Template literal must contain complete percent triplets.");
                }
                index += 2;
            }
            else if (character < 128)
            {
                if (character is not ('!' or '#' or '$' or '&' or >= '(' and <= ';' or '=' or >= '?' and <= '[' or
                    ']' or '_' or >= 'a' and <= 'z' or '~'))
                {
                    throw Error("invalid_endpoint_template", path, "The URI Template contains an invalid literal character.");
                }
            }
            else
            {
                Append(result, text[start..index], path, ref bytes);
                if (Rune.DecodeFromUtf16(text[index..], out var rune, out var length) != OperationStatus.Done ||
                    rune.Value < 0xa0 || rune.Value is >= 0xfdd0 and <= 0xfdef or >= 0xe0000 and <= 0xe0fff ||
                    (rune.Value & 0xffff) >= 0xfffe)
                {
                    throw Error("invalid_endpoint_template", path, "The URI Template literal contains a disallowed Unicode code point.");
                }
                Append(result, Uri.EscapeDataString(rune.ToString()), path, ref bytes);
                index += length - 1;
                start = index + 1;
            }
        }
        Append(result, text[start..], path, ref bytes);
    }

    private bool IsRecordPath(string path)
    {
        foreach (var prefix in RecordPrefixes)
        {
            if (IsArrayRecord(path, prefix))
            {
                return true;
            }
        }
        return protocol == "HTTP" && IsArrayRecord(path, "/protocoloptions/headers/");
    }

    private static bool IsArrayRecord(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length &&
        !path.AsSpan(prefix.Length).ContainsAnyExcept("0123456789".AsSpan());

    private bool IsUriValuePath(string path)
    {
        var separator = path.LastIndexOf('/');
        if (separator < 0)
        {
            return false;
        }
        var name = path.AsSpan(separator + 1);
        var record = path[..separator];
        return protocol is "MQTT/3.1.1" or "MQTT/5.0" && path == "/protocoloptions/willmessage" ||
            IsArrayRecord(record, "/protocoloptions/endpoints/") && name.SequenceEqual("uri") ||
            IsArrayRecord(record, "/protocoloptions/authorization/") && (name.SequenceEqual("resourceuri") || name.SequenceEqual("authorityuri"));
    }

    private void Append(StringBuilder result, ReadOnlySpan<char> text, string path, ref int bytes)
    {
        var count = Encoding.UTF8.GetByteCount(text);
        if (count > options.MaxExpandedStringBytes - bytes)
        {
            throw Error("template_string_limit", path, "A resolved protocol-option string exceeds its UTF-8 budget.");
        }
        bytes += count;
        result.Append(text);
    }

    private bool IsVariableName(string name)
    {
        var needsCharacter = true;
        for (var index = 0; index < name.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = name[index];
            if (character == '.')
            {
                if (needsCharacter)
                {
                    return false;
                }
                needsCharacter = true;
                continue;
            }
            if (character == '%')
            {
                if (index + 2 >= name.Length || !char.IsAsciiHexDigit(name[index + 1]) ||
                    !char.IsAsciiHexDigit(name[index + 2]))
                {
                    return false;
                }
                index += 2;
            }
            else if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
            needsCharacter = false;
        }
        return !needsCharacter;
    }

    internal static string At(string path, string name) =>
        path + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static RegistryException Error(string code, string path, string message) =>
        new(new RegistryDiagnostic(code, path, message));
}
