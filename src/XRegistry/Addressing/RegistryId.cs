// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry;

/// <summary>A case-sensitive registry identifier, not a URI and never a native filesystem name.</summary>
public sealed record RegistryId
{
    private RegistryId(string value) => Value = value;

    /// <summary>Gets the exact identifier spelling.</summary>
    public string Value { get; }

    /// <summary>
    /// Validates the core ID grammar: 1-128 ASCII characters; first ALPHA, DIGIT or underscore;
    /// subsequent characters are RFC3986 unreserved, colon or at-sign.
    /// </summary>
    public static RegistryId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValid(value))
        {
            throw Diagnostics.Error("malformed_id", "", "The registry identifier does not satisfy the core ID grammar.");
        }

        return new RegistryId(value);
    }

    /// <summary>Decodes exactly one bounded URI path segment and validates the resulting Core identifier.</summary>
    public static RegistryId ParseEscaped(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Length > 384)
        {
            throw Diagnostics.Error("malformed_id", "", "The escaped identifier exceeds its URI byte budget.");
        }
        return Parse(RegistryPath.DecodeSegment(segment));
    }

    /// <summary>Tests whether a string satisfies the exact core identifier grammar.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !IsInitial(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsInitial(character) && character is not ('-' or '.' or '~' or ':' or '@'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Tests the case-insensitive uniqueness rule; existence and parent scope remain the caller's responsibility.</summary>
    public bool ConflictsWith(RegistryId other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsInitial(char value) => char.IsAsciiLetterOrDigit(value) || value == '_';
}

internal static class RegistryNames
{
    internal static bool IsAttribute(string name, bool extended = false, int maxLength = 63)
    {
        if (name.Length == 0 || name.Length > maxLength ||
            !(IsLetter(name[0]) || (extended ? char.IsAsciiDigit(name[0]) : name[0] == '_')))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!(IsLetter(character) || char.IsAsciiDigit(character) || character == '_' ||
                (extended && character is ':' or '-' or '.')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLetter(char value) => value is >= 'a' and <= 'z';
}
