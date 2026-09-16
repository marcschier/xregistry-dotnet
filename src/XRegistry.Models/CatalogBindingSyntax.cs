// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

/// <summary>Pure Git advertisement syntax, independent of repository acquisition and caller access policy.</summary>
public static class CatalogBindingSyntax
{
    /// <summary>Tests for a complete Git ref or SHA-1/SHA-256 object ID, not a revision expression.</summary>
    public static bool IsGitRevision(string? value)
    {
        if (value is null)
        {
            return false;
        }
        if (value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit))
        {
            return true;
        }
        return value.StartsWith("refs/", StringComparison.Ordinal) &&
            !value.Any(static c => char.IsControl(c) || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\') &&
            !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal) &&
            !value.EndsWith('.') && !value.Split('/').Any(static part => part.Length == 0 || part.StartsWith('.') ||
                part.EndsWith(".lock", StringComparison.Ordinal));
    }

    /// <summary>Tests the Git binding's portable 1-64 ASCII directory components; empty selects the repository root.</summary>
    /// <remarks>This does not test existence, containment, authorization, or a consumer's total path-length budget.</remarks>
    public static bool IsGitRootPath(string? value)
    {
        if (value is null)
        {
            return false;
        }
        if (value.Length == 0)
        {
            return true;
        }
        foreach (var component in value.Split('/'))
        {
            if (component.Length is < 1 or > 64 || !(char.IsAsciiLetterOrDigit(component[0]) || component[0] == '_') ||
                component.EndsWith('.') ||
                !component.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            {
                return false;
            }
            var device = component.Split('.')[0].ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" ||
                device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) ||
                    device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] is >= '1' and <= '9')
            {
                return false;
            }
        }
        return true;
    }
}
