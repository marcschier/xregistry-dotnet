using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XRegistry.Validation;

internal static class SchemaJson
{
    internal static string Path(string parent, string name)
        => parent + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static string String(JsonElement value, string path)
    {
        ValidationContext.Require(value.ValueKind == JsonValueKind.String, path, "schema.string", "A string is required.");
        return value.GetString()!;
    }

    internal static JsonElement Member(JsonElement value, string name, string path)
    {
        ValidationContext.Require(value.TryGetProperty(name, out var member), Path(path, name), "schema.required", $"The '{name}' member is required.");
        return member;
    }

    internal static void Object(JsonElement value, string path)
        => ValidationContext.Require(value.ValueKind == JsonValueKind.Object, path, "schema.object", "An object is required.");

    internal static void Array(JsonElement value, string path)
        => ValidationContext.Require(value.ValueKind == JsonValueKind.Array, path, "schema.array", "An array is required.");

    internal static void Boolean(JsonElement value, string path)
        => ValidationContext.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, path, "schema.boolean", "A boolean is required.");

    internal static HashSet<string> Strings(JsonElement value, string path, bool nonempty = false)
    {
        Array(value, path);
        var strings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            ValidationContext.Require(strings.Add(String(item, path)), path, "schema.duplicate", "Array entries must be unique.");
        }

        ValidationContext.Require(!nonempty || strings.Count > 0, path, "schema.empty", "At least one entry is required.");
        return strings;
    }

    internal static bool Identifier(string value)
        => value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    internal static bool Equal(JsonElement left, JsonElement right, ValidationContext context)
    {
        context.Work();
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var count = left.EnumerateObject().Count();
                if (count != right.EnumerateObject().Count())
                {
                    return false;
                }

                foreach (var property in left.EnumerateObject())
                {
                    context.Work(count);
                    if (!right.TryGetProperty(property.Name, out var value) || !Equal(property.Value, value, context))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.Array:
                if (left.GetArrayLength() != right.GetArrayLength())
                {
                    return false;
                }

                for (var i = 0; i < left.GetArrayLength(); i++)
                {
                    if (!Equal(left[i], right[i], context))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.Number:
                return JsonNumber.Read(left, context, "$").CompareTo(JsonNumber.Read(right, context, "$")) == 0;
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            default:
                return true;
        }
    }

    internal static void UniqueValues(JsonElement values, ValidationContext context, string path)
    {
        Array(values, path);
        for (var i = 0; i < values.GetArrayLength(); i++)
        {
            for (var j = 0; j < i; j++)
            {
                ValidationContext.Require(!Equal(values[i], values[j], context), path,
                    "schema.duplicate", "Array values must be unique.");
            }
        }
    }

    internal static (JsonElement Value, string Path) Pointer(JsonElement root, string fragment, string path,
        ValidationContext? context = null)
    {
        ValidationContext.Require(fragment.StartsWith('#'), path, "reference.pointer", "A JSON Pointer fragment is required.");
        return DecodedPointer(root, DecodeFragment(fragment[1..], path, "reference.pointer", context), path, context);
    }

    internal static string DecodeFragment(string fragment, string path, string code, ValidationContext? context = null)
    {
        context?.Work(fragment.Length, path);
        var bytes = new ArrayBufferWriter<byte>();
        for (var i = 0; i < fragment.Length;)
        {
            if (fragment[i] == '%')
            {
                ValidationContext.Require(i + 2 < fragment.Length && char.IsAsciiHexDigit(fragment[i + 1])
                    && char.IsAsciiHexDigit(fragment[i + 2]), path, code, "Invalid percent escape.");
                bytes.GetSpan(1)[0] = byte.Parse(fragment.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                bytes.Advance(1);
                i += 3;
            }
            else
            {
                ValidationContext.Require(Rune.DecodeFromUtf16(fragment.AsSpan(i), out var rune, out var consumed) == OperationStatus.Done,
                    path, code, "The URI fragment contains invalid Unicode.");
                bytes.Advance(rune.EncodeToUtf8(bytes.GetSpan(4)));
                i += consumed;
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes.WrittenSpan);
        }
        catch (DecoderFallbackException)
        {
            throw new ValidationFailure(DocumentValidationStatus.Invalid,
                new(path, code, "Percent escapes must encode valid UTF-8."));
        }
    }

    internal static (JsonElement Value, string Path) DecodedPointer(JsonElement root, string pointer, string path,
        ValidationContext? context = null)
    {
        ValidationContext.Require(pointer.Length == 0 || pointer[0] == '/', path,
            "reference.pointer", "A JSON Pointer must be empty or start with '/'.");
        var current = root;
        var targetPath = "$";
        if (pointer.Length == 0)
        {
            return (current, targetPath);
        }

        var segments = pointer[1..].Split('/');
        foreach (var segment in segments)
        {
            context?.Work(segment.Length + 1, path);
            for (var i = 0; i < segment.Length; i++)
            {
                if (segment[i] == '~')
                {
                    ValidationContext.Require(++i < segment.Length && segment[i] is '0' or '1',
                        path, "reference.pointer", "Invalid JSON Pointer escape.");
                }
            }
        }
        foreach (var segment in segments)
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object)
            {
                context?.Work(current.EnumerateObject().Count(), path);
                ValidationContext.Require(current.TryGetProperty(name, out current), path,
                    "reference.not_found", $"The JSON Pointer '{pointer}' does not resolve.");
            }
            else if (current.ValueKind == JsonValueKind.Array && (name == "0" || !name.StartsWith('0'))
                && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                ValidationContext.Fail(DocumentValidationStatus.Invalid, path, "reference.not_found",
                    $"The JSON Pointer '{pointer}' does not resolve.");
            }

            targetPath = Path(targetPath, name);
        }

        return (current, targetPath);
    }
}
