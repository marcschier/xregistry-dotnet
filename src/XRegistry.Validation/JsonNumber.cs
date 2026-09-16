// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace XRegistry.Validation;

internal readonly record struct JsonNumber(bool Negative, string Digits, BigInteger Exponent) : IComparable<JsonNumber>
{
    internal bool IsZero => Digits == "0";
    internal bool IsInteger => IsZero || Exponent >= 0;

    internal static JsonNumber Read(JsonElement value, ValidationContext context, string path)
    {
        ValidationContext.Require(value.ValueKind == JsonValueKind.Number, path, "schema.number", "A number is required.");
        var raw = value.GetRawText();
        context.Work(raw.Length, path);
        if (raw.Length > 256)
        {
            ValidationContext.Fail(DocumentValidationStatus.Indeterminate, path, "limit.number",
                "Numeric schema constraints are limited to 256 source characters.");
        }

        var exponentOffset = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentOffset < 0 ? raw : raw[..exponentOffset];
        var exponent = exponentOffset < 0 ? BigInteger.Zero
            : BigInteger.Parse(raw.AsSpan(exponentOffset + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        if (dot >= 0)
        {
            exponent -= mantissa.Length - dot - 1;
        }

        var digits = new StringBuilder(mantissa.Length);
        foreach (var character in mantissa)
        {
            if (char.IsAsciiDigit(character))
            {
                digits.Append(character);
            }
        }

        var coefficient = digits.ToString().TrimStart('0');
        if (coefficient.Length == 0)
        {
            return new(false, "0", BigInteger.Zero);
        }

        var normalized = coefficient.TrimEnd('0');
        exponent += coefficient.Length - normalized.Length;
        return new(raw[0] == '-', normalized, exponent);
    }

    public int CompareTo(JsonNumber other)
    {
        if (IsZero || other.IsZero)
        {
            return IsZero ? (other.IsZero ? 0 : other.Negative ? 1 : -1) : Negative ? -1 : 1;
        }

        if (Negative != other.Negative)
        {
            return Negative ? -1 : 1;
        }

        var result = (Exponent + Digits.Length).CompareTo(other.Exponent + other.Digits.Length);
        if (result == 0)
        {
            for (var i = 0; i < Math.Max(Digits.Length, other.Digits.Length); i++)
            {
                var left = i < Digits.Length ? Digits[i] : '0';
                var right = i < other.Digits.Length ? other.Digits[i] : '0';
                result = left.CompareTo(right);
                if (result != 0)
                {
                    break;
                }
            }
        }

        return Negative ? -result : result;
    }
}
