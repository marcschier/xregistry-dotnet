using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XRegistry;

internal static class ScalarValues
{
    internal const int MaxAttributeBytes = 4096;

    internal static bool FitsString(string text, int maximumBytes) =>
        text.Length <= maximumBytes && Encoding.UTF8.GetByteCount(text) <= maximumBytes;

    internal static bool Matches(RegistryValueType type, JsonElement value) => Matches(type, value, out _);

    internal static bool Matches(RegistryValueType type, JsonElement value, out int nonStringBytes, string? text = null)
    {
        nonStringBytes = 0;
        if (type is RegistryValueType.Decimal or RegistryValueType.Integer or RegistryValueType.UInteger)
        {
            if (value.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            var number = RegistryNumber.FromElement(value);
            nonStringBytes = number.RawText.Length;
            return type == RegistryValueType.Decimal ||
                number.IsInteger && (type != RegistryValueType.UInteger || number.Significand.Sign >= 0);
        }

        if (type == RegistryValueType.Boolean)
        {
            nonStringBytes = value.ValueKind == JsonValueKind.True ? 4 : 5;
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text ??= value.GetString()!;
        return type switch
        {
            RegistryValueType.String => true,
            RegistryValueType.Timestamp => TryTimestamp(text, out _),
            RegistryValueType.Uri or RegistryValueType.Url => UriSyntax.IsReference(text, out _),
            RegistryValueType.UriAbsolute or RegistryValueType.UrlAbsolute => UriSyntax.IsReference(text, out var absolute) && absolute,
            RegistryValueType.UriRelative or RegistryValueType.UrlRelative => UriSyntax.IsReference(text, out var absolute) && !absolute,
            RegistryValueType.UriTemplate => UriSyntax.IsTemplate(text),
            RegistryValueType.Xid => IsXid(text),
            RegistryValueType.XidType => IsXidType(text),
            _ => false
        };
    }

    internal static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            return RegistryNumber.FromElement(left).Equals(RegistryNumber.FromElement(right));
        }

        return left.ValueKind == right.ValueKind && left.ValueKind switch
        {
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => false
        };
    }

    internal static bool Equal(RegistryValueType type, JsonElement left, JsonElement right) =>
        type == RegistryValueType.Timestamp
            ? left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String &&
                TryTimestamp(left.GetString()!, out var first) && TryTimestamp(right.GetString()!, out var second) &&
                SameTimestamp(first, second)
            : Equal(left, right);

    internal static bool Allowed(RegistryAttributeDefinition definition, JsonElement value)
    {
        if (!definition.Strict || definition.EnumValues.Count == 0)
        {
            return true;
        }

        if (definition.Type == RegistryValueType.Timestamp)
        {
            if (value.ValueKind != JsonValueKind.String || !TryTimestamp(value.GetString()!, out var timestamp))
            {
                return false;
            }

            return definition.EnumValues.Any(item => item.ValueKind == JsonValueKind.String &&
                TryTimestamp(item.GetString()!, out var candidate) && SameTimestamp(timestamp, candidate));
        }

        return definition.EnumValues.Any(item =>
            definition.IsSystemDefined && definition.Name == "compatibility" &&
            item.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String
                ? string.Equals(item.GetString(), value.GetString(), StringComparison.OrdinalIgnoreCase)
                : Equal(item, value));
    }

    private static bool SameTimestamp(string left, string right) =>
        left.AsSpan(0, 19).SequenceEqual(right.AsSpan(0, 19)) &&
        left.AsSpan(19, left.Length - 20).TrimEnd('0').TrimEnd('.')
            .SequenceEqual(right.AsSpan(19, right.Length - 20).TrimEnd('0').TrimEnd('.'));

    internal static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()!
        : value.GetRawText();

    internal static bool TryTimestamp(string text, out string normalized)
    {
        normalized = "";
        if (text.Length < 20 || text[4] != '-' || text[7] != '-' || text[10] is not ('T' or 't') ||
            text[13] != ':' || text[16] != ':')
        {
            return false;
        }

        if (!Digits(text.AsSpan(0, 4), out var year) || !Digits(text.AsSpan(5, 2), out var month) ||
            !Digits(text.AsSpan(8, 2), out var day) || !Digits(text.AsSpan(11, 2), out var hour) ||
            !Digits(text.AsSpan(14, 2), out var minute) || !Digits(text.AsSpan(17, 2), out var second) ||
            year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 ||
            day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 60)
        {
            return false;
        }

        var index = 19;
        var fraction = "";
        if (text[index] == '.')
        {
            var start = index++;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            if (index == start + 1)
            {
                return false;
            }

            fraction = text[start..index];
        }

        var offset = 0;
        if (index + 1 == text.Length && text[index] is 'Z' or 'z')
        {
            index++;
        }
        else if (index + 6 == text.Length && text[index] is '+' or '-' && text[index + 3] == ':' &&
            Digits(text.AsSpan(index + 1, 2), out var offsetHour) && offsetHour <= 23 &&
            Digits(text.AsSpan(index + 4, 2), out var offsetMinute) && offsetMinute <= 59)
        {
            offset = (offsetHour * 60 + offsetMinute) * (text[index] == '-' ? -1 : 1);
            index += 6;
        }

        if (index != text.Length)
        {
            return false;
        }

        var local = new DateTime(year, month, day, hour, minute, Math.Min(second, 59), DateTimeKind.Utc);
        var ticks = local.Ticks - offset * TimeSpan.TicksPerMinute;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        var utc = new DateTime(ticks, DateTimeKind.Utc);
        if (second == 60 && !(utc.Hour == 23 && utc.Minute == 59 &&
            (utc.Month == 6 && utc.Day == 30 || utc.Month == 12 && utc.Day == 31)))
        {
            return false;
        }

        normalized = utc.ToString("yyyy-MM-dd'T'HH:mm:", CultureInfo.InvariantCulture) +
            (second == 60 ? "60" : utc.Second.ToString("00", CultureInfo.InvariantCulture)) + fraction + "Z";
        return true;
    }

    private static bool Digits(ReadOnlySpan<char> text, out int result)
    {
        result = 0;
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }

            result = result * 10 + character - '0';
        }

        return true;
    }

    private static bool IsXid(string text)
    {
        try
        {
            var path = RegistryPath.Parse(text);
            return !path.IsDetails && path.Kind is RegistryPathKind.Registry or RegistryPathKind.Group or
                RegistryPathKind.Resource or RegistryPathKind.Meta or RegistryPathKind.Version;
        }
        catch (RegistryException)
        {
            return false;
        }
    }

    private static bool IsXidType(string text)
    {
        try
        {
            ModelPaths.ParseType(text, "");
            return true;
        }
        catch (RegistryException)
        {
            return false;
        }
    }
}
