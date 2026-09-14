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
                    if (char.IsAsciiDigit(pattern[i]) && pattern[i] != '0' || pattern[i] is 'k' or 'p' or 'P')
                    {
                        Unsupported(path);
                    }
                    if (pattern[i] is 'u' or 'x')
                    {
                        var length = pattern[i] == 'u' ? 4 : 2;
                        for (var j = 0; j < length; j++)
                        {
                            ValidationContext.Require(++i < pattern.Length && char.IsAsciiHexDigit(pattern[i]),
                                path, "schema.pattern", "Invalid hexadecimal regular-expression escape.");
                        }
                    }
                    atom = true;
                    quantified = false;
                    break;
                case '[':
                    var closed = false;
                    for (++i; i < pattern.Length; i++)
                    {
                        if (pattern[i] == ']')
                        {
                            closed = true;
                            break;
                        }
                        if (pattern[i] == '\\')
                        {
                            ValidationContext.Require(++i < pattern.Length, path, "schema.pattern", "Incomplete character-class escape.");
                            if (pattern[i] is 'u' or 'x' or 'p' or 'P')
                            {
                                Unsupported(path);
                            }
                        }
                        else if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
                        {
                            if (pattern[i + 2] == '\\')
                            {
                                Unsupported(path);
                            }
                            ValidationContext.Require(pattern[i] <= pattern[i + 2], path, "schema.pattern", "A character range is reversed.");
                            i += 2;
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

    private static void Unsupported(string path)
        => ValidationContext.Fail(DocumentValidationStatus.Unsupported, path, "schema.pattern_unsupported",
            "This regular expression is outside the bounded ECMA-262 syntax subset.");
}
