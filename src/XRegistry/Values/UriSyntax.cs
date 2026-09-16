// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;

namespace XRegistry;

internal static class UriSyntax
{
    internal static bool IsReference(string text, out bool absolute)
    {
        absolute = false;
        var fragment = text.IndexOf('#');
        if (fragment >= 0 && !Part(text.AsSpan(fragment + 1), slash: true, question: true))
        {
            return false;
        }

        var body = fragment < 0 ? text : text[..fragment];
        var query = body.IndexOf('?');
        if (query >= 0 && !Part(body.AsSpan(query + 1), slash: true, question: true))
        {
            return false;
        }

        var hierarchy = query < 0 ? body : body[..query];
        var colon = hierarchy.IndexOf(':');
        var slash = hierarchy.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash))
        {
            if (colon == 0 || !char.IsAsciiLetter(hierarchy[0]) ||
                hierarchy.AsSpan(1, colon - 1).ContainsAnyExcept(
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.".AsSpan()))
            {
                return false;
            }

            absolute = true;
            hierarchy = hierarchy[(colon + 1)..];
        }

        if (hierarchy.StartsWith("//", StringComparison.Ordinal))
        {
            var end = hierarchy.IndexOf('/', 2);
            var authority = end < 0 ? hierarchy[2..] : hierarchy[2..end];
            if (!Authority(authority))
            {
                return false;
            }

            hierarchy = end < 0 ? "" : hierarchy[end..];
        }

        return Part(hierarchy.AsSpan(), slash: true);
    }

    internal static bool IsTemplate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '{')
            {
                if (text[index] == '}' || char.IsControl(text[index]) || char.IsWhiteSpace(text[index]) ||
                    text[index] is '"' or '\\' or '<' or '>' or '`' ||
                    text[index] == '%' && !Percent(text, ref index))
                {
                    return false;
                }

                continue;
            }

            var end = text.IndexOf('}', index + 1);
            if (end < 0)
            {
                return false;
            }

            var expression = text[(index + 1)..end];
            if (expression.Length > 0 && "+#./;?&".Contains(expression[0], StringComparison.Ordinal))
            {
                expression = expression[1..];
            }

            foreach (var variable in expression.Split(','))
            {
                var name = variable;
                if (name.EndsWith('*'))
                {
                    name = name[..^1];
                }
                else
                {
                    var prefix = name.IndexOf(':');
                    if (prefix >= 0)
                    {
                        var length = name.AsSpan(prefix + 1);
                        if (length.Length is < 1 or > 4 || length[0] is < '1' or > '9' ||
                            length.ContainsAnyExcept("0123456789".AsSpan()))
                        {
                            return false;
                        }

                        name = name[..prefix];
                    }
                }

                foreach (var component in name.Split('.'))
                {
                    if (component.Length == 0)
                    {
                        return false;
                    }

                    for (var i = 0; i < component.Length; i++)
                    {
                        if (component[i] == '%' ? !Percent(component, ref i) :
                            !char.IsAsciiLetterOrDigit(component[i]) && component[i] != '_')
                        {
                            return false;
                        }
                    }
                }
            }

            index = end;
        }

        return true;
    }

    private static bool Authority(string text)
    {
        var at = text.LastIndexOf('@');
        if (at >= 0 && !Part(text.AsSpan(0, at), allowAt: false))
        {
            return false;
        }

        var host = text[(at + 1)..];
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            if (end < 0)
            {
                return false;
            }

            var ip = host[1..end];
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }

            return end + 1 == host.Length || host[end + 1] == ':' && Port(host.AsSpan(end + 2));
        }

        var colon = host.IndexOf(':');
        return colon < 0 ? Part(host.AsSpan(), allowAt: false, allowColon: false) :
            Part(host.AsSpan(0, colon), allowAt: false, allowColon: false) && Port(host.AsSpan(colon + 1));
    }

    private static bool Port(ReadOnlySpan<char> text) => !text.ContainsAnyExcept("0123456789".AsSpan());

    private static bool Part(ReadOnlySpan<char> text, bool slash = false, bool question = false,
        bool allowAt = true, bool allowColon = true)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '%')
            {
                if (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2]))
                {
                    return false;
                }

                i += 2;
            }
            else if (!(char.IsAsciiLetterOrDigit(character) || "-._~!$&'()*+,;=".Contains(character, StringComparison.Ordinal) ||
                allowAt && character == '@' || allowColon && character == ':' ||
                slash && character == '/' || question && character == '?'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Percent(string text, ref int index)
    {
        if (index + 2 >= text.Length || !char.IsAsciiHexDigit(text[index + 1]) || !char.IsAsciiHexDigit(text[index + 2]))
        {
            return false;
        }

        index += 2;
        return true;
    }
}
