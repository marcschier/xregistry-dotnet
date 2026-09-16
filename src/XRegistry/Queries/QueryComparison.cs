// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Queries;

internal static class QueryComparison
{
    private static readonly IReadOnlyDictionary<string, RegistryAttributeDefinition> s_timestamp =
        new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal)
        {
            ["stamp"] = RegistryModel.Compile(RegistryJson.Parse("""{"attributes":{"stamp":{"type":"timestamp"}}}""")).Attributes["stamp"]
        };

    internal static bool Matches(IReadOnlyList<JsonNode?> values, RegistryValueType? type, string operation,
        string? literal, RegistryJsonLimits limits, RegistryQueryBudget budget)
    {
        budget.Spend(values.Count + 1);
        var present = values.Where(static value => value is not null).ToArray();
        if (operation.Length == 0 || literal == "null")
        {
            return operation switch
            {
                "" or "!=" or "<>" => present.Length != 0,
                "=" => present.Length == 0,
                _ => throw Diagnostics.Error("bad_filter", "", "Relative comparisons cannot use null.")
            };
        }

        var equal = false;
        foreach (var value in present)
        {
            var comparison = CompareLiteral(value!, type, literal!, operation, limits, budget);
            var matches = operation switch
            {
                "=" or "!=" or "<>" => comparison == 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                _ => throw Diagnostics.Error("bad_filter", "", "The filter operator is invalid.")
            };
            equal |= matches;
        }

        return operation is "!=" or "<>" ? !equal : equal;
    }

    internal static void ValidateLiteral(RegistryValueType? type, string operation, string? literal, RegistryJsonLimits limits)
    {
        if (operation.Length == 0 || literal == "null")
        {
            if (literal == "null" && operation is "<" or "<=" or ">" or ">=")
            {
                throw Diagnostics.Error("bad_filter", "", "Relative comparisons cannot use null.");
            }

            return;
        }

        if (type is RegistryValueType.Object or RegistryValueType.Array or RegistryValueType.Map or RegistryValueType.Binary)
        {
            throw Diagnostics.Error("bad_filter", "", "Only scalar attributes support non-null literal comparisons.");
        }

        if (type == RegistryValueType.Boolean && literal is not ("true" or "false"))
        {
            throw Diagnostics.Error("bad_filter", "", "Boolean filter literals must be exactly true or false.");
        }

        if (type is RegistryValueType.Integer or RegistryValueType.UInteger or RegistryValueType.Decimal)
        {
            _ = Number(literal!, limits, "bad_filter");
        }
        else if (type == RegistryValueType.Timestamp)
        {
            _ = Timestamp(literal!, limits);
        }

        if (operation is "<" or "<=" or ">" or ">=" && HasWildcard(literal!))
        {
            throw Diagnostics.Error("bad_filter", "", "String wildcards only support equality and inequality.");
        }
    }

    internal static int Compare(JsonNode? left, JsonNode? right, RegistryValueType? type, RegistryJsonLimits limits, RegistryQueryBudget budget)
    {
        budget.Spend();
        if (left is null || right is null)
        {
            return left is null ? right is null ? 0 : -1 : 1;
        }

        var first = left.GetValueKind();
        var second = right.GetValueKind();
        if (first is JsonValueKind.Array or JsonValueKind.Object || second is JsonValueKind.Array or JsonValueKind.Object)
        {
            throw Diagnostics.Error("bad_sort", "", "The sort projection must have a scalar value.");
        }

        if (first == JsonValueKind.Number && second == JsonValueKind.Number)
        {
            return CompareNumbers(Number(left.ToJsonString(), limits, "bad_sort"), Number(right.ToJsonString(), limits, "bad_sort"), budget);
        }

        if (first is JsonValueKind.True or JsonValueKind.False && second is JsonValueKind.True or JsonValueKind.False)
        {
            return (first == JsonValueKind.True).CompareTo(second == JsonValueKind.True);
        }

        if (first != second)
        {
            return Rank(first).CompareTo(Rank(second));
        }

        var a = left.GetValue<string>();
        var b = right.GetValue<string>();
        budget.Spend(a.Length + b.Length);
        return type == RegistryValueType.Timestamp ? CompareTimestamps(a, b) : StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }

    private static int CompareLiteral(JsonNode value, RegistryValueType? type, string literal, string operation,
        RegistryJsonLimits limits, RegistryQueryBudget budget)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                return CompareNumbers(Number(value.ToJsonString(), limits, "bad_filter"), Number(literal, limits, "bad_filter"), budget);
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (literal is not ("true" or "false"))
                {
                    throw Diagnostics.Error("bad_filter", "", "Boolean filter literals must be exactly true or false.");
                }

                return value.GetValue<bool>().CompareTo(literal == "true");
            case JsonValueKind.String:
                var text = value.GetValue<string>();
                if (type == RegistryValueType.Timestamp)
                {
                    budget.Spend(text.Length + literal.Length);
                    return CompareTimestamps(text, Timestamp(literal, limits));
                }

                if (operation is "=" or "!=" or "<>")
                {
                    return Glob(text, literal, budget) ? 0 : 1;
                }

                if (HasWildcard(literal))
                {
                    throw Diagnostics.Error("bad_filter", "", "Relative comparisons cannot contain wildcards.");
                }

                literal = literal.Replace("\\*", "*", StringComparison.Ordinal);
                budget.Spend(text.Length + literal.Length);
                return StringComparer.OrdinalIgnoreCase.Compare(text, literal);
            default:
                throw Diagnostics.Error("bad_filter", "", "A non-null filter literal cannot compare a complex value.");
        }
    }

    private static bool Glob(string text, string pattern, RegistryQueryBudget budget)
    {
        var pieces = new List<string>();
        var current = new System.Text.StringBuilder();
        var wildcard = false;
        foreach (var segment in Tokens(pattern))
        {
            budget.Spend(segment.Text.Length + 1);
            if (segment.Wildcard)
            {
                pieces.Add(current.ToString());
                current.Clear();
                wildcard = true;
            }
            else
            {
                current.Append(segment.Text);
            }
        }

        pieces.Add(current.ToString());
        budget.Spend(text.Length + pattern.Length);
        if (!wildcard)
        {
            return text.Equals(pieces[0], StringComparison.OrdinalIgnoreCase);
        }

        if (!text.StartsWith(pieces[0], StringComparison.OrdinalIgnoreCase) ||
            !text.EndsWith(pieces[^1], StringComparison.OrdinalIgnoreCase) ||
            text.Length < pieces[0].Length + pieces[^1].Length)
        {
            return false;
        }

        var position = pieces[0].Length;
        var end = text.Length - pieces[^1].Length;
        for (var index = 1; index < pieces.Count - 1; index++)
        {
            budget.Spend((long)(end - position + 1) * Math.Max(1, pieces[index].Length));
            var found = text.AsSpan(position, end - position).IndexOf(pieces[index], StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            position += found + pieces[index].Length;
        }

        return true;
    }

    private static IEnumerable<(bool Wildcard, string Text)> Tokens(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length && value[index + 1] == '*')
            {
                index++;
                yield return (false, "*");
            }
            else
            {
                yield return value[index] == '*' ? (true, "") : (false, value[index].ToString());
            }
        }
    }

    private static bool HasWildcard(string value) => Tokens(value).Any(static token => token.Wildcard);

    private static RegistryNumber Number(string text, RegistryJsonLimits limits, string code)
    {
        try
        {
            return RegistryNumber.Parse(text, limits);
        }
        catch (RegistryException exception)
        {
            throw new RegistryException(new(code, "", "The query requires a bounded, finite numeric literal."), exception);
        }
    }

    private static int CompareNumbers(RegistryNumber left, RegistryNumber right, RegistryQueryBudget budget)
    {
        var sign = left.Significand.Sign.CompareTo(right.Significand.Sign);
        if (sign != 0 || left.Significand.IsZero)
        {
            return sign;
        }

        var a = BigInteger.Abs(left.Significand).ToString(CultureInfo.InvariantCulture);
        var b = BigInteger.Abs(right.Significand).ToString(CultureInfo.InvariantCulture);
        budget.Spend(a.Length + b.Length);
        var magnitude = ((long)a.Length + left.Exponent).CompareTo((long)b.Length + right.Exponent);
        if (magnitude == 0)
        {
            for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
            {
                magnitude = (index < a.Length ? a[index] : '0').CompareTo(index < b.Length ? b[index] : '0');
                if (magnitude != 0)
                {
                    break;
                }
            }
        }

        return magnitude * left.Significand.Sign;
    }

    private static string Timestamp(string text, RegistryJsonLimits limits)
    {
        try
        {
            return RegistryMetadataValidator.Validate(QueryJson.Own(new JsonObject { ["stamp"] = text }, limits),
                s_timestamp, new() { Limits = limits }).Metadata.RootElement.GetProperty("stamp").GetString()!;
        }
        catch (RegistryException exception)
        {
            throw new RegistryException(new("bad_filter", "", "The timestamp query literal is invalid."), exception);
        }
    }

    private static int CompareTimestamps(string left, string right)
    {
        var seconds = string.CompareOrdinal(left[..19], right[..19]);
        return seconds != 0 ? seconds : string.CompareOrdinal(
            left[19..^1].TrimEnd('0').TrimEnd('.'), right[19..^1].TrimEnd('0').TrimEnd('.'));
    }

    private static int Rank(JsonValueKind kind) => kind switch
    {
        JsonValueKind.False or JsonValueKind.True => 0,
        JsonValueKind.Number => 1,
        JsonValueKind.String => 2,
        _ => throw Diagnostics.Error("bad_sort", "", "The sort value is not scalar.")
    };
}
