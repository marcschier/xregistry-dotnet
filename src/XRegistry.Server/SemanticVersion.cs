using System.Globalization;
using System.Numerics;

namespace XRegistry.Server;

internal sealed class SemanticVersion(BigInteger[] numbers, string[]? preRelease) : IComparable<SemanticVersion>
{
    internal static SemanticVersion Parse(string value, string path)
    {
        var build = value.Split('+');
        var parts = build[0].Split('-', 2);
        var numbers = parts[0].Split('.');
        if (build.Length > 2 || build.Length == 2 && !Identifiers(build[1], false) ||
            numbers.Length != 3 || numbers.Any(static part => !Numeric(part, true)) ||
            parts.Length == 2 && !Identifiers(parts[1], true))
        {
            throw ServerErrors.With("invalid_attribute", path, "The Version identifier is not a Semantic Version 2.0.0 value.", ("name", "versionid"));
        }

        return new(numbers.Select(static part => BigInteger.Parse(part, CultureInfo.InvariantCulture)).ToArray(),
            parts.Length == 2 ? parts[1].Split('.') : null);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var index = 0; index < numbers.Length; index++)
        {
            var comparison = numbers[index].CompareTo(other.Numbers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (preRelease is null || other.PreRelease is null)
        {
            return preRelease is null ? other.PreRelease is null ? 0 : 1 : -1;
        }

        for (var index = 0; index < Math.Min(preRelease.Length, other.PreRelease.Length); index++)
        {
            var a = preRelease[index];
            var b = other.PreRelease[index];
            var numericA = Numeric(a, false);
            var numericB = Numeric(b, false);
            var comparison = numericA && numericB
                ? BigInteger.Parse(a, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(b, CultureInfo.InvariantCulture))
                : numericA != numericB ? numericA ? -1 : 1 : string.CompareOrdinal(a, b);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return preRelease.Length.CompareTo(other.PreRelease.Length);
    }

    private BigInteger[] Numbers => numbers;
    private string[]? PreRelease => preRelease;
    private static bool Numeric(string value, bool noLeadingZero) =>
        value.Length != 0 && value.All(char.IsAsciiDigit) && (!noLeadingZero || value.Length == 1 || value[0] != '0');
    private static bool Identifiers(string value, bool pre) =>
        value.Split('.').All(part => part.Length != 0 && part.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') &&
            (!pre || !part.All(char.IsAsciiDigit) || Numeric(part, true)));
}
