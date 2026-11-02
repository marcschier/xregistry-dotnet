// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Globalization;
using System.Text;

namespace XRegistry.Models;

/// <summary>Binding declaration syntax, independent of acquisition and caller access policy.</summary>
public static class CatalogBindingSyntax
{
    /// <summary>Checks a built-in binding's selected parameter names; extension bindings retain their own vocabulary.</summary>
    public static bool IsParameterSupported(string binding, string parameter) => binding switch
    {
        "http" => false,
        "git" => parameter is "revision" or "path",
        "oci" => parameter == "reference",
        "file" => parameter is "layout" or "reference",
        "opcua" => parameter is "registryroot" or "applicationuri" or "transportprofileuri",
        _ => true
    };

    /// <summary>Checks an absolute OPC UA endpoint URI before transport discovery.</summary>
    public static bool IsNativeEndpoint(string? value)
    {
        if (value is null || value.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !Uri.IsWellFormedUriString(value, UriKind.Absolute))
        {
            return false;
        }

        return uri.Host.Length != 0 && uri.UserInfo.Length == 0 &&
            !value.Contains('?', StringComparison.Ordinal) && !value.Contains('#', StringComparison.Ordinal) &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    /// <summary>Checks the portable ExpandedNodeId form used for an OPC UA Registry root.</summary>
    public static bool IsPortableNodeId(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var nodeId = value;
        var hasNamespaceUri = false;
        if (value.StartsWith("nsu=", StringComparison.Ordinal))
        {
            var separator = value.IndexOf(';');
            if (separator <= 4 || separator == value.Length - 1)
            {
                return false;
            }

            var encodedNamespace = value[4..separator];
            for (var index = 0; index < encodedNamespace.Length; index++)
            {
                if (encodedNamespace[index] == '%' &&
                    (index + 2 >= encodedNamespace.Length ||
                        !char.IsAsciiHexDigit(encodedNamespace[++index]) ||
                        !char.IsAsciiHexDigit(encodedNamespace[++index])))
                {
                    return false;
                }
            }
            var namespaceUri = Uri.UnescapeDataString(encodedNamespace);
            if (!Uri.TryCreate(namespaceUri, UriKind.Absolute, out _) ||
                !Uri.IsWellFormedUriString(namespaceUri, UriKind.Absolute) ||
                namespaceUri == "http://opcfoundation.org/UA/")
            {
                return false;
            }

            hasNamespaceUri = true;
            nodeId = value[(separator + 1)..];
        }
        if (nodeId.Length < 2 || nodeId[1] != '=')
        {
            return false;
        }

        var identifier = nodeId.AsSpan(2);
        return nodeId[0] switch
        {
            'i' => uint.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) &&
                (hasNamespaceUri || numericId != 0),
            's' => (hasNamespaceUri || !identifier.IsEmpty) && IsNodeIdString(identifier),
            'g' => Guid.TryParseExact(identifier, "D", out var guid) && (hasNamespaceUri || guid != Guid.Empty),
            'b' => (hasNamespaceUri || !identifier.IsEmpty) && IsBase64(identifier),
            _ => false
        };
    }

    private static bool IsNodeIdString(ReadOnlySpan<char> value)
    {
        var characters = 0;
        while (!value.IsEmpty)
        {
            if (++characters > 4096 ||
                Rune.DecodeFromUtf16(value, out var rune, out var consumed) != OperationStatus.Done ||
                Rune.IsControl(rune))
            {
                return false;
            }
            value = value[consumed..];
        }
        return true;
    }

    private static bool IsBase64(ReadOnlySpan<char> value)
    {
        if (value.Length > 5464 || value.Length % 4 != 0)
        {
            return false;
        }

        var padding = value.Length > 0 && value[^1] == '='
            ? value.Length > 1 && value[^2] == '=' ? 2 : 1
            : 0;
        for (var index = 0; index < value.Length - padding; index++)
        {
            var character = value[index];
            if (!(char.IsAsciiLetterOrDigit(character) || character is '+' or '/'))
            {
                return false;
            }
        }

        if (value[^padding..].IndexOfAnyExcept('=') >= 0)
        {
            return false;
        }
        Span<byte> decoded = stackalloc byte[4096];
        return Convert.TryFromBase64Chars(value, decoded, out _);
    }

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
