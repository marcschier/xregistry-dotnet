// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;

namespace XRegistry.Wot;

/// <summary>A validated SemVer 2.0.0 version, ordered by SemVer precedence.</summary>
public sealed class WotSemanticVersion : IComparable<WotSemanticVersion>, IEquatable<WotSemanticVersion>
{
    private readonly BigInteger _major;
    private readonly BigInteger _minor;
    private readonly BigInteger _patch;
    private readonly string[]? _prerelease;

    private WotSemanticVersion(BigInteger major, BigInteger minor, BigInteger patch, string[]? prerelease)
    {
        _major = major;
        _minor = minor;
        _patch = patch;
        _prerelease = prerelease;
    }

    /// <summary>Gets whether this version contains prerelease identifiers.</summary>
    public bool IsPrerelease => _prerelease is not null;

    /// <summary>Parses a SemVer 2.0.0 string without build metadata and within Core's identifier limit.</summary>
    /// <exception cref="ArgumentException">The input is not a valid version identifier.</exception>
    public static WotSemanticVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryParse(value, out var parsed))
        {
            throw new ArgumentException("The value is not a SemVer 2.0.0 identifier.", nameof(value));
        }

        return parsed!;
    }

    /// <summary>Attempts to parse SemVer 2.0.0 without build metadata and within Core's identifier limit.</summary>
    public static bool TryParse(string? value, out WotSemanticVersion? version) => TryParse(value, enforceLength: true, out version);

    /// <inheritdoc />
    public int CompareTo(WotSemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var comparison = _major.CompareTo(other._major);
        if (comparison == 0)
        {
            comparison = _minor.CompareTo(other._minor);
        }

        if (comparison == 0)
        {
            comparison = _patch.CompareTo(other._patch);
        }

        if (comparison != 0)
        {
            return comparison;
        }

        if (_prerelease is null || other._prerelease is null)
        {
            return _prerelease is null ? other._prerelease is null ? 0 : 1 : -1;
        }

        for (var index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
        {
            var left = _prerelease[index];
            var right = other._prerelease[index];
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            comparison = leftNumeric && rightNumeric
                ? BigInteger.Parse(left, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(right, CultureInfo.InvariantCulture))
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : string.CompareOrdinal(left, right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    /// <inheritdoc />
    public bool Equals(WotSemanticVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WotSemanticVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_major);
        hash.Add(_minor);
        hash.Add(_patch);
        if (_prerelease is not null)
        {
            foreach (var identifier in _prerelease)
            {
                hash.Add(identifier, StringComparer.Ordinal);
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>Compares SemVer precedence for two versions.</summary>
    public static bool operator ==(WotSemanticVersion? left, WotSemanticVersion? right) => Equals(left, right);

    /// <summary>Compares SemVer precedence for two versions.</summary>
    public static bool operator !=(WotSemanticVersion? left, WotSemanticVersion? right) => !Equals(left, right);

    /// <summary>Tests whether the left version has lower SemVer precedence.</summary>
    public static bool operator <(WotSemanticVersion left, WotSemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>Tests whether the left version has lower or equal SemVer precedence.</summary>
    public static bool operator <=(WotSemanticVersion left, WotSemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Tests whether the left version has higher SemVer precedence.</summary>
    public static bool operator >(WotSemanticVersion left, WotSemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>Tests whether the left version has higher or equal SemVer precedence.</summary>
    public static bool operator >=(WotSemanticVersion left, WotSemanticVersion right) => left.CompareTo(right) >= 0;
    internal bool HasSameMajor(WotSemanticVersion other) => _major == other._major;

    internal static bool IsSemVerSyntax(string? value) => TryParse(value, enforceLength: false, out _);

    internal static bool HasValidBuildMetadata(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var separator = value.IndexOf('+');
        return separator > 0 && separator == value.LastIndexOf('+') &&
            TryParse(value[..separator], enforceLength: false, out _) && IsIdentifierList(value[(separator + 1)..], allowNumericLeadingZero: true);
    }

    private static bool TryParse(string? value, bool enforceLength, out WotSemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || enforceLength && value.Length > 128)
        {
            return false;
        }

        var separator = value.IndexOf('-');
        var core = separator < 0 ? value : value[..separator];
        var components = core.Split('.');
        if (components.Length != 3 || !TryNumber(components[0], out var major) ||
            !TryNumber(components[1], out var minor) || !TryNumber(components[2], out var patch))
        {
            return false;
        }

        string[]? prerelease = null;
        if (separator >= 0)
        {
            var text = value[(separator + 1)..];
            if (!IsIdentifierList(text, allowNumericLeadingZero: false))
            {
                return false;
            }

            prerelease = text.Split('.');
        }

        version = new(major, minor, patch, prerelease);
        return true;
    }

    private static bool TryNumber(string value, out BigInteger number)
    {
        number = BigInteger.Zero;
        return value.Length > 0 && (value.Length == 1 || value[0] != '0') && value.All(char.IsAsciiDigit) &&
            BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsIdentifierList(string value, bool allowNumericLeadingZero)
    {
        var identifiers = value.Split('.');
        return identifiers.All(identifier => identifier.Length > 0 && identifier.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character == '-') &&
            (allowNumericLeadingZero || !IsNumeric(identifier) || identifier.Length == 1 || identifier[0] != '0'));
    }

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
}
