using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace XRegistry;

/// <summary>An exact finite JSON number, stored as a normalized integer significand and base-ten exponent.</summary>
public sealed class RegistryNumber : IEquatable<RegistryNumber>
{
    private RegistryNumber(string rawText, BigInteger significand, int exponent)
    {
        RawText = rawText;
        Significand = significand;
        Exponent = exponent;
    }

    /// <summary>Gets the original JSON numeric token without floating-point conversion.</summary>
    public string RawText { get; }

    /// <summary>Gets the signed normalized significand (zero has exponent zero).</summary>
    public BigInteger Significand { get; }

    /// <summary>Gets the power of ten multiplying the significand.</summary>
    public int Exponent { get; }

    /// <summary>Gets whether the mathematical value is an integer, including integer-valued exponential notation.</summary>
    public bool IsInteger => Exponent >= 0;

    /// <summary>Parses one bounded JSON number. Invalid tokens or exhausted budgets throw <see cref="RegistryException"/>.</summary>
    public static RegistryNumber Parse(string text, RegistryJsonLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FromElement(RegistryJson.Parse(text, limits).RootElement, limits);
    }

    /// <summary>Reads an exact number from an element without borrowing its storage.</summary>
    public static RegistryNumber FromElement(JsonElement element, RegistryJsonLimits? limits = null)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw Diagnostics.Error("invalid_number", "", "A JSON number is required.");
        }

        limits ??= new RegistryJsonLimits();
        limits.Validate();
        var raw = element.GetRawText();
        ValidateToken(raw, limits, "");
        var e = raw.IndexOfAny(['e', 'E']);
        var significandText = e < 0 ? raw : raw[..e];
        var exponent = e < 0 ? 0 : ParseExponent(raw.AsSpan(e + 1));
        var dot = significandText.IndexOf('.');
        if (dot >= 0)
        {
            exponent -= significandText.Length - dot - 1;
            significandText = significandText.Remove(dot, 1);
        }

        var last = significandText.Length;
        while (last > 0 && significandText[last - 1] == '0')
        {
            last--;
            exponent++;
        }

        if (last == 0 || (last == 1 && significandText[0] == '-'))
        {
            return new RegistryNumber(raw, BigInteger.Zero, 0);
        }

        return new RegistryNumber(raw,
            BigInteger.Parse(significandText.AsSpan(0, last), CultureInfo.InvariantCulture), exponent);
    }

    /// <summary>Returns the exact integer; non-integral numbers throw <see cref="InvalidOperationException"/>.</summary>
    public BigInteger ToBigInteger()
    {
        if (!IsInteger)
        {
            throw new InvalidOperationException("The number is not an integer.");
        }

        return Significand * BigInteger.Pow(10, Exponent);
    }

    /// <summary>Compares mathematical values, ignoring spelling, negative zero and redundant decimal zeros.</summary>
    public bool Equals(RegistryNumber? other) =>
        other is not null && Significand == other.Significand && Exponent == other.Exponent;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RegistryNumber number && Equals(number);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Significand, Exponent);

    /// <summary>Returns an exact normalized numeric token without expanding large powers of ten.</summary>
    public override string ToString() => Exponent == 0
        ? Significand.ToString(CultureInfo.InvariantCulture)
        : string.Concat(Significand.ToString(CultureInfo.InvariantCulture), "e",
            Exponent.ToString(CultureInfo.InvariantCulture));

    internal static void ValidateToken(string raw, RegistryJsonLimits limits, string path)
    {
        if (raw.Length > limits.MaxNumberCharacters)
        {
            throw Diagnostics.Error("number_limit", path, "The number token exceeds its character budget.");
        }

        var e = raw.IndexOfAny(['e', 'E']);
        if (e < 0)
        {
            return;
        }

        var exponent = 0;
        foreach (var digit in raw.AsSpan(e + 1))
        {
            if (digit is '+' or '-')
            {
                continue;
            }

            if (exponent > (limits.MaxNumberExponent - (digit - '0')) / 10 ||
                (exponent == 0 && digit - '0' > limits.MaxNumberExponent))
            {
                throw Diagnostics.Error("number_limit", path, "The number exceeds its exponent budget.");
            }

            exponent = exponent * 10 + digit - '0';
        }
    }

    private static int ParseExponent(ReadOnlySpan<char> text)
    {
        var negative = text[0] == '-';
        var value = 0;
        foreach (var character in text)
        {
            if (character is not ('+' or '-'))
            {
                value = value * 10 + character - '0';
            }
        }

        return negative ? -value : value;
    }
}
