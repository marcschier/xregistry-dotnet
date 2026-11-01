// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace XRegistry.Validation;

internal static class SchemaPattern
{
    internal static void Validate(string pattern, ValidationContext context, string path)
    {
        context.Work(pattern.Length, path);
        var groups = 0;
        var atom = false;
        var quantified = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var character = pattern[i];
            switch (character)
            {
                case '\\':
                    ValidationContext.Require(++i < pattern.Length, path, "schema.pattern", "Incomplete regular-expression escape.");
                    if (char.IsAsciiDigit(pattern[i]) && pattern[i] != '0' || pattern[i] is 'k' or 'p' or 'P'
                        || pattern[i] == '0' && i + 1 < pattern.Length && char.IsAsciiDigit(pattern[i + 1]))
                    {
                        Unsupported(path);
                    }
                    if (pattern[i] is 'u' or 'x')
                    {
                        ReadHexEscape(pattern, ref i, path);
                    }
                    atom = true;
                    quantified = false;
                    break;
                case '[':
                    var closed = false;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '^') { i++; }
                    for (++i; i < pattern.Length; i++)
                    {
                        if (pattern[i] == ']')
                        {
                            closed = true;
                            break;
                        }
                        var start = ReadClassCharacter(pattern, ref i, path);
                        if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
                        {
                            i += 2;
                            var endCharacter = ReadClassCharacter(pattern, ref i, path);
                            if (start is null || endCharacter is null) { Unsupported(path); }
                            ValidationContext.Require(start <= endCharacter, path, "schema.pattern", "A character range is reversed.");
                        }
                    }
                    ValidationContext.Require(closed, path, "schema.pattern", "An unclosed character class was found.");
                    atom = true;
                    quantified = false;
                    break;
                case '(':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                    {
                        if (i + 2 >= pattern.Length || pattern[i + 2] != ':')
                        {
                            Unsupported(path);
                        }
                        i += 2;
                    }
                    ValidationContext.Require(++groups <= context.Options.MaxDepth, path, "schema.pattern", "Pattern group depth exceeded.");
                    atom = false;
                    quantified = false;
                    break;
                case ')':
                    ValidationContext.Require(groups-- > 0, path, "schema.pattern", "An unmatched ')' was found.");
                    atom = true;
                    quantified = false;
                    break;
                case '*':
                case '+':
                case '?':
                    if (character == '?' && quantified)
                    {
                        quantified = false;
                        atom = false;
                        break;
                    }
                    ValidationContext.Require(atom && !quantified, path, "schema.pattern", "A quantifier has no preceding atom.");
                    quantified = true;
                    break;
                case '{':
                    var end = pattern.IndexOf('}', i + 1);
                    if (end < 0)
                    {
                        Unsupported(path);
                    }
                    var range = pattern[(i + 1)..end].Split(',');
                    if (range.Length > 2 || !uint.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min))
                    {
                        Unsupported(path);
                        break;
                    }
                    ValidationContext.Require(atom && !quantified, path, "schema.pattern", "A quantifier has no preceding atom.");
                    if (range.Length == 2 && range[1].Length > 0)
                    {
                        ValidationContext.Require(uint.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max >= min,
                            path, "schema.pattern", "An invalid quantifier range was found.");
                    }
                    i = end;
                    quantified = true;
                    break;
                case '|':
                case '^':
                case '$':
                    atom = false;
                    quantified = false;
                    break;
                default:
                    atom = true;
                    quantified = false;
                    break;
            }
        }
        ValidationContext.Require(groups == 0, path, "schema.pattern", "An unclosed regular-expression group was found.");
    }

    private static int ReadHexEscape(string pattern, ref int index, string path)
    {
        var length = pattern[index] == 'u' ? 4 : 2;
        var value = 0;
        for (var i = 0; i < length; i++)
        {
            ValidationContext.Require(++index < pattern.Length && char.IsAsciiHexDigit(pattern[index]),
                path, "schema.pattern", "Invalid hexadecimal regular-expression escape.");
            var digit = pattern[index];
            value = value * 16 + (digit <= '9' ? digit - '0' : (digit | 32) - 'a' + 10);
        }
        return value;
    }

    private static int? ReadClassCharacter(string pattern, ref int index, string path)
    {
        if (pattern[index] != '\\') { return pattern[index]; }
        ValidationContext.Require(++index < pattern.Length, path, "schema.pattern", "Incomplete character-class escape.");
        var escaped = pattern[index];
        if (escaped is 'u' or 'x') { return ReadHexEscape(pattern, ref index, path); }
        if (escaped is 'p' or 'P' or 'k' || char.IsAsciiDigit(escaped) && escaped != '0') { Unsupported(path); }
        if (escaped == '0' && index + 1 < pattern.Length && char.IsAsciiDigit(pattern[index + 1])) { Unsupported(path); }
        if (escaped is 'd' or 'D' or 's' or 'S' or 'w' or 'W') { return null; }
        if (escaped == 'c')
        {
            ValidationContext.Require(++index < pattern.Length && char.IsAsciiLetter(pattern[index]),
                path, "schema.pattern", "A control escape requires an ASCII letter.");
            return pattern[index] % 32;
        }
        return escaped switch
        {
            'b' => 8,
            'f' => 12,
            'n' => 10,
            'r' => 13,
            't' => 9,
            'v' => 11,
            '0' => 0,
            _ => escaped
        };
    }

    private static void Unsupported(string path)
        => ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "schema.pattern_unsupported",
            "This regular expression is outside the bounded ECMA-262 syntax subset.");
}
