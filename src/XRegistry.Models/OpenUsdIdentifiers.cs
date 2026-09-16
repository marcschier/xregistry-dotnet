// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace XRegistry.Models;

/// <summary>Pure OpenUSD authored-identifier operations, independent of storage and transport.</summary>
public static class OpenUsdIdentifiers
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>Selects a symbolic ID without mutating the supplied complete sibling assignment snapshot.</summary>
    /// <remarks>
    /// Keys are assigned Core IDs, values are exact source strings. The byte budget covers the source and
    /// every key and value cumulatively. The sibling budget is inclusive. Existing bindings are retained,
    /// IDs conflict case-insensitively, and sources match ordinally. An occupied sole fallback is rejected.
    /// The caller must authorize and supply a complete, consistent snapshot and atomically reserve the result.
    /// </remarks>
    public static string AssignSymbolicId(string source, IReadOnlyDictionary<string, string> siblings,
        int maxUtf8Bytes, int maxSiblings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSiblings);
        var remaining = maxUtf8Bytes - ValidateInput(source, maxUtf8Bytes, nameof(source), cancellationToken);
        if (siblings.Count > maxSiblings)
        {
            throw new InvalidDataException("The OpenUSD assignment exceeds its sibling limit.");
        }

        var candidate = CreateSymbolicIdCandidate(source, maxUtf8Bytes, cancellationToken);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        string? assigned = null;
        var count = 0;
        foreach (var sibling in siblings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count++ == maxSiblings)
            {
                throw new InvalidDataException("The OpenUSD assignment exceeds its sibling limit.");
            }
            remaining -= ValidateInput(sibling.Key, remaining, nameof(siblings), cancellationToken);
            remaining -= ValidateInput(sibling.Value, remaining, nameof(siblings), cancellationToken);
            if (!RegistryId.IsValid(sibling.Key) || !ids.Add(sibling.Key) || !sources.Add(sibling.Value))
            {
                throw new InvalidDataException("The OpenUSD sibling snapshot has an invalid ID or duplicate binding.");
            }
            if (sibling.Value == source) { assigned = sibling.Key; }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (assigned == candidate) { return assigned; }
        if (assigned is null && !ids.Contains(candidate)) { return candidate; }

        var fallback = CreateSymbolicIdCollisionCandidate(source, maxUtf8Bytes, cancellationToken);
        if (assigned is not null)
        {
            return assigned == fallback ? assigned :
                throw new InvalidDataException("An existing OpenUSD source binding cannot be renamed to a symbolic ID.");
        }
        if (ids.Contains(fallback))
        {
            throw new InvalidOperationException("Unresolved OpenUSD symbolic-ID collision: the sole fallback is occupied.");
        }
        return fallback;
    }

    /// <summary>Removes leading ./ components while preserving the rest of an authored identifier.</summary>
    /// <remarks>
    /// The input is the string between USD delimiters, not a scene or filesystem path. The nonnegative
    /// UTF-8 byte budget applies before normalization; empty identifiers and invalid UTF-16 are rejected.
    /// Internal dot segments, percent escapes, case and package selectors are not rewritten.
    /// </remarks>
    public static string NormalizeAssetIdentifier(string identifier, int maxUtf8Bytes,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(identifier, maxUtf8Bytes, nameof(identifier), cancellationToken);
        var start = 0;
        while (identifier.AsSpan(start).StartsWith("./", StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            start += 2;
        }

        if (start == identifier.Length)
        {
            throw new ArgumentException("An authored OpenUSD identifier must not be empty.", nameof(identifier));
        }

        return identifier[start..];
    }

    /// <summary>Derives an OpenUSD symbolic candidate without resolving sibling collisions.</summary>
    /// <remarks>
    /// Uses original URI spelling and percent-decodes path segments once. Normalized source labels remain
    /// whole when shortening; their literal dots do not introduce new labels. A result longer than 128
    /// characters is shortened to a prefix of at most 119 and the exact source's eight-digit SHA-256 suffix.
    /// Use AssignSymbolicId with complete sibling state to select between this candidate and its sole fallback.
    /// This is neither an inverse identifier mapping nor a filesystem locator.
    /// </remarks>
    public static string CreateSymbolicIdCandidate(string source, int maxUtf8Bytes,
        CancellationToken cancellationToken = default) =>
        CreateSymbolicId(source, maxUtf8Bytes, requireSuffix: false, cancellationToken);

    /// <summary>Derives the sole collision fallback from the exact source, not from its candidate ID.</summary>
    /// <remarks>
    /// Reserves nine suffix characters even for collision-only candidates of length 120-128.
    /// A truncation candidate already uses this suffix and has no additional fallback. Computing this
    /// value does not authorize its assignment; it is not a way to unconditionally hash every Resource ID.
    /// </remarks>
    public static string CreateSymbolicIdCollisionCandidate(string source, int maxUtf8Bytes,
        CancellationToken cancellationToken = default) =>
        CreateSymbolicId(source, maxUtf8Bytes, requireSuffix: true, cancellationToken);

    private static string CreateSymbolicId(string source, int maxUtf8Bytes, bool requireSuffix,
        CancellationToken cancellationToken)
    {
        ValidateInput(source, maxUtf8Bytes, nameof(source), cancellationToken);
        var labels = new List<string>();
        var path = source;
        var separator = '/';
        var schemeEnd = source.IndexOf("://", StringComparison.Ordinal);
        if (TryAuthorityEnd(source, schemeEnd, out var authorityEnd, cancellationToken))
        {
            // URI syntax is independent of transport port ranges and System.Uri's framework-specific limits.
            var authorityStart = schemeEnd + 3;
            var authority = source[authorityStart..authorityEnd];
            var host = authority[(authority.LastIndexOf('@') + 1)..];
            string? port = null;
            var portStart = host.StartsWith('[') ? host.IndexOf(']') + 1 : host.LastIndexOf(':');
            if (portStart >= 0 && portStart < host.Length && host[portStart] == ':')
            {
                port = host[(portStart + 1)..];
                host = host[..portStart];
            }

            var hostLabels = host.Split('.');
            for (var index = hostLabels.Length - 1; index >= 0; index--)
            {
                AddLabel(labels, hostLabels[index], cancellationToken);
            }

            if (port is not null)
            {
                AddLabel(labels, port, cancellationToken);
            }

            var pathEnd = source.AsSpan(authorityEnd).IndexOfAny('?', '#');
            path = pathEnd < 0 ? source[authorityEnd..] : source.Substring(authorityEnd, pathEnd);
        }
        else if (source.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            separator = ':';
        }

        foreach (var segment in path.Split(separator))
        {
            AddLabel(labels, DecodeSegment(segment, cancellationToken), cancellationToken);
        }

        if (labels.Count == 0)
        {
            labels.Add("_");
        }

        var length = labels.Count - 1;
        foreach (var label in labels)
        {
            length += label.Length;
        }

        if (length <= 128 && !requireSuffix)
        {
            return string.Join('.', labels);
        }

        while (labels.Count > 1 && length > 119)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length -= labels[^1].Length + 1;
            labels.RemoveAt(labels.Count - 1);
        }

        if (labels[0].Length > 119)
        {
            labels[0] = labels[0].AsSpan(0, 119).TrimEnd("-.").ToString();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var hash = SHA256.HashData(Utf8.GetBytes(source));
        cancellationToken.ThrowIfCancellationRequested();
        return string.Join('.', labels) + "." + Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static bool TryAuthorityEnd(string source, int schemeEnd, out int authorityEnd,
        CancellationToken cancellationToken)
    {
        authorityEnd = 0;
        if (schemeEnd <= 0 || !Uri.CheckSchemeName(source[..schemeEnd]))
        {
            return false;
        }

        var start = schemeEnd + 3;
        var end = source.AsSpan(start).IndexOfAny('/', '?', '#');
        authorityEnd = end < 0 ? source.Length : start + end;
        if (!IsAuthority(source.AsSpan(start, authorityEnd - start), cancellationToken))
        {
            return false;
        }

        var tail = source.AsSpan(authorityEnd);
        var fragment = tail.IndexOf('#');
        if (fragment >= 0)
        {
            if (!IsUriPart(tail[(fragment + 1)..], allowSlash: true, allowQuestion: true, cancellationToken: cancellationToken))
            {
                return false;
            }
            tail = tail[..fragment];
        }

        var query = tail.IndexOf('?');
        if (query >= 0)
        {
            if (!IsUriPart(tail[(query + 1)..], allowSlash: true, allowQuestion: true, cancellationToken: cancellationToken))
            {
                return false;
            }
            tail = tail[..query];
        }

        return IsUriPart(tail, allowSlash: true, cancellationToken: cancellationToken);
    }

    private static bool IsAuthority(ReadOnlySpan<char> authority, CancellationToken cancellationToken)
    {
        var at = authority.LastIndexOf('@');
        if (at >= 0 && !IsUriPart(authority[..at], allowAt: false, cancellationToken: cancellationToken))
        {
            return false;
        }

        var host = authority[(at + 1)..];
        if (host.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = host.IndexOf(']');
            if (closing < 0)
            {
                return false;
            }

            var address = host[1..closing];
            var valid = !address.Contains('%') && IPAddress.TryParse(address, out var ip) &&
                ip.AddressFamily == AddressFamily.InterNetworkV6;
            if (!valid)
            {
                var dot = address.IndexOf('.');
                valid = dot > 1 && address[0] is 'v' or 'V' && dot < address.Length - 1 &&
                    IsHex(address[1..dot]) && !address[(dot + 1)..].Contains('%') &&
                    IsUriPart(address[(dot + 1)..], allowAt: false, cancellationToken: cancellationToken);
            }

            return valid && (closing + 1 == host.Length ||
                host[closing + 1] == ':' && IsPort(host[(closing + 2)..]));
        }

        var colon = host.IndexOf(':');
        return colon < 0
            ? IsUriPart(host, allowAt: false, allowColon: false, cancellationToken: cancellationToken)
            : IsUriPart(host[..colon], allowAt: false, allowColon: false, cancellationToken: cancellationToken) &&
                IsPort(host[(colon + 1)..]);
    }

    private static bool IsPort(ReadOnlySpan<char> port)
    {
        foreach (var character in port)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsUriPart(ReadOnlySpan<char> value, bool allowSlash = false, bool allowQuestion = false,
        bool allowAt = true, bool allowColon = true, CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < value.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = value[index];
            if (character == '%')
            {
                if (value.Length - index < 3 || !char.IsAsciiHexDigit(value[index + 1]) || !char.IsAsciiHexDigit(value[index + 2]))
                {
                    return false;
                }
                index += 2;
            }
            else if (!(char.IsAsciiLetterOrDigit(character) || "-._~!$&'()*+,;=".Contains(character, StringComparison.Ordinal) ||
                allowSlash && character == '/' || allowQuestion && character == '?' ||
                allowAt && character == '@' || allowColon && character == ':'))
            {
                return false;
            }
        }
        return true;
    }

    private static void AddLabel(List<string> labels, string value, CancellationToken cancellationToken)
    {
        var label = NormalizeLabel(value, cancellationToken);
        if (label.Length != 0)
        {
            labels.Add(label);
        }
    }

    private static string DecodeSegment(string source, CancellationToken cancellationToken)
    {
        if (!source.Contains('%'))
        {
            return source;
        }

        var bytes = new byte[Utf8.GetByteCount(source)];
        var written = 0;
        var index = 0;
        while (index < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source[index] == '%')
            {
                if (source.Length - index < 3 || !char.IsAsciiHexDigit(source[index + 1]) ||
                    !char.IsAsciiHexDigit(source[index + 2]))
                {
                    throw new ArgumentException("An OpenUSD symbolic source contains an invalid percent escape.", nameof(source));
                }

                bytes[written++] = (byte)((Hex(source[index + 1]) << 4) | Hex(source[index + 2]));
                index += 3;
            }
            else
            {
                var next = source.IndexOf('%', index);
                var length = (next < 0 ? source.Length : next) - index;
                written += Utf8.GetBytes(source.AsSpan(index, length), bytes.AsSpan(written));
                index += length;
            }
        }

        try
        {
            return Utf8.GetString(bytes, 0, written);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("An OpenUSD symbolic source contains invalid percent-encoded UTF-8.", nameof(source), exception);
        }
    }

    private static int Hex(char value) => char.IsAsciiDigit(value) ? value - '0' : (value | 0x20) - 'a' + 10;

    private static string NormalizeLabel(string source, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(source.Length);
        var previous = '\0';
        foreach (var character in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '-';
            if ((value is '-' or '.') && value == previous)
            {
                continue;
            }

            result.Append(value);
            previous = value;
        }

        return result.ToString().AsSpan().Trim("-.").ToString();
    }

    private static int ValidateInput(string value, int maxUtf8Bytes, string parameterName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ArgumentOutOfRangeException.ThrowIfNegative(maxUtf8Bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (value.Length > maxUtf8Bytes)
        {
            throw new InvalidDataException("The OpenUSD identifier exceeds its UTF-8 input byte limit.");
        }

        int bytes;
        try
        {
            bytes = Utf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("An OpenUSD identifier must have a valid UTF-8 encoding.", parameterName, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (bytes > maxUtf8Bytes)
        {
            throw new InvalidDataException("The OpenUSD identifier exceeds its UTF-8 input byte limit.");
        }
        return bytes;
    }
}
