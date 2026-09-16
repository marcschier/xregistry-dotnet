// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text;

namespace XRegistry.Client;

/// <summary>An HTTP Link value whose URI reference and extension parameters remain opaque.</summary>
public sealed class RegistryHttpLink
{
    private RegistryHttpLink(string reference, Dictionary<string, string> parameters)
    {
        Reference = reference;
        Parameters = new ReadOnlyDictionary<string, string>(parameters);
        Relations = Array.AsReadOnly(parameters.TryGetValue("rel", out var relations)
            ? relations.Split(' ', StringSplitOptions.RemoveEmptyEntries) : []);
    }

    /// <summary>Gets the URI-reference exactly as supplied between angle brackets.</summary>
    public string Reference { get; }

    /// <summary>Gets the relation types, without splitting quoted commas or URI delimiters.</summary>
    public IReadOnlyList<string> Relations { get; }

    /// <summary>Gets the case-insensitive parameter map with quoted-string escaping removed.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Tests a registered, case-insensitive relation type.</summary>
    public bool HasRelation(string relation) => Relations.Contains(relation, StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses bounded RFC 8288 Link fields, including multiple fields and comma-separated values.</summary>
    /// <remarks>URI references are not fetched or trusted by parsing them. The first repeated rel parameter wins.</remarks>
    public static IReadOnlyList<RegistryHttpLink> Parse(
        IEnumerable<string> fields, int maxLinks = 256, int maxCharacters = 65536)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLinks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        var result = new List<RegistryHttpLink>();
        var characters = 0;
        foreach (var field in fields)
        {
            if (field is null || field.Length > maxCharacters - characters)
            {
                throw new InvalidDataException("HTTP Link fields exceed their character budget.");
            }

            characters += field.Length;
            var position = 0;
            while (position < field.Length)
            {
                SkipWhitespace(field, ref position);
                if (position == field.Length)
                {
                    break;
                }

                if (field[position] != '<' || result.Count >= maxLinks)
                {
                    throw new InvalidDataException("An HTTP Link value is malformed or exceeds the link limit.");
                }

                var start = ++position;
                while (position < field.Length && field[position] != '>')
                {
                    var character = field[position++];
                    if (character is <= ' ' or >= '\x7f' or '<' or '"' or '\\')
                    {
                        throw new InvalidDataException("An HTTP Link URI-reference contains an invalid character.");
                    }
                }

                if (position == field.Length)
                {
                    throw new InvalidDataException("An HTTP Link URI-reference is unterminated.");
                }

                var reference = field[start..position++];
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                SkipWhitespace(field, ref position);
                while (position < field.Length && field[position] == ';')
                {
                    position++;
                    SkipWhitespace(field, ref position);
                    var name = ReadToken(field, ref position);
                    SkipWhitespace(field, ref position);
                    var value = "";
                    if (position < field.Length && field[position] == '=')
                    {
                        position++;
                        SkipWhitespace(field, ref position);
                        value = position < field.Length && field[position] == '"'
                            ? ReadQuoted(field, ref position) : ReadToken(field, ref position);
                    }

                    if (!parameters.TryAdd(name, value) &&
                        StringComparer.OrdinalIgnoreCase.Equals(name, "count") &&
                        !StringComparer.Ordinal.Equals(parameters[name], value))
                    {
                        throw new InvalidDataException("An HTTP Link has conflicting counts.");
                    }

                    SkipWhitespace(field, ref position);
                }

                result.Add(new RegistryHttpLink(reference, parameters));
                if (position < field.Length && field[position++] != ',')
                {
                    throw new InvalidDataException("HTTP Link values must be separated by commas.");
                }
            }
        }

        return result.AsReadOnly();
    }

    private static void SkipWhitespace(string value, ref int position)
    {
        while (position < value.Length && value[position] is ' ' or '\t')
        {
            position++;
        }
    }

    private static string ReadToken(string value, ref int position)
    {
        var start = position;
        while (position < value.Length && (char.IsAsciiLetterOrDigit(value[position]) ||
            "!#$%&'*+-.^_`|~".Contains(value[position], StringComparison.Ordinal)))
        {
            position++;
        }

        if (start == position)
        {
            throw new InvalidDataException("An HTTP Link parameter requires a token.");
        }

        return value[start..position];
    }

    private static string ReadQuoted(string value, ref int position)
    {
        position++;
        var result = new StringBuilder();
        while (position < value.Length)
        {
            var character = value[position++];
            if (character == '"')
            {
                return result.ToString();
            }

            if (character == '\\')
            {
                if (position == value.Length)
                {
                    break;
                }

                character = value[position++];
            }

            if (character is < ' ' and not '\t' or '\x7f')
            {
                throw new InvalidDataException("An HTTP Link quoted value contains a control character.");
            }

            result.Append(character);
        }

        throw new InvalidDataException("An HTTP Link quoted value is unterminated.");
    }
}
