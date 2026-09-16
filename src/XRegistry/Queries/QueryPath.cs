// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace XRegistry.Queries;

internal enum QueryStepKind { Property, Index, AnyProperty, AnyIndex }
internal sealed record QueryStep(QueryStepKind Kind, string Name = "", int Index = 0);

internal sealed class QueryPath(IReadOnlyList<QueryStep> steps)
{
    internal IReadOnlyList<QueryStep> Steps { get; } = steps;

    internal static QueryPath Parse(string text, RegistryQueryEvaluationLimits limits, string code)
    {
        if (text.Length == 0)
        {
            throw Error(code);
        }

        var steps = new List<QueryStep>();
        var index = text[0] == '.' ? 1 : 0;
        var needName = true;
        while (index < text.Length)
        {
            if (steps.Count == limits.MaxPathSegments)
            {
                throw Diagnostics.Error("too_large", "", "The query path exceeds its segment budget.");
            }

            if (text[index] == '[')
            {
                index++;
                if (index == text.Length)
                {
                    throw Error(code);
                }

                if (text[index] is '\'' or '"')
                {
                    var quote = text[index++];
                    var name = new StringBuilder();
                    var closed = false;
                    while (index < text.Length)
                    {
                        var character = text[index++];
                        if (character == quote)
                        {
                            closed = true;
                            break;
                        }

                        if (character == '\\')
                        {
                            if (index == text.Length)
                            {
                                throw Error(code);
                            }

                            character = text[index++];
                            if (character is not ('\\' or '\'' or '"'))
                            {
                                throw Error(code);
                            }
                        }

                        name.Append(character);
                    }

                    if (!closed || index == text.Length || text[index++] != ']')
                    {
                        throw Error(code);
                    }

                    steps.Add(new(QueryStepKind.Property, name.ToString()));
                }
                else
                {
                    var start = index;
                    while (index < text.Length && text[index] != ']')
                    {
                        index++;
                    }

                    if (index == text.Length)
                    {
                        throw Error(code);
                    }

                    var token = text[start..index++];
                    if (token == "*")
                    {
                        steps.Add(new(QueryStepKind.AnyIndex));
                    }
                    else if (token.Length != 0 && token.All(char.IsAsciiDigit))
                    {
                        steps.Add(new(QueryStepKind.Index, Index: int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var arrayIndex)
                            ? arrayIndex : int.MaxValue));
                    }
                    else
                    {
                        throw Error(code);
                    }
                }
            }
            else
            {
                if (!needName)
                {
                    throw Error(code);
                }

                var start = index;
                while (index < text.Length && text[index] is not ('.' or '['))
                {
                    if (text[index] is ']' or '\'' or '"' or '\\' or '=' or '<' or '>' or '!' or ',' || char.IsWhiteSpace(text[index]))
                    {
                        throw Error(code);
                    }

                    index++;
                }

                var name = text[start..index];
                if (name.Length == 0 || name.Contains('*', StringComparison.Ordinal) && name != "*")
                {
                    throw Error(code);
                }

                steps.Add(new(name == "*" ? QueryStepKind.AnyProperty : QueryStepKind.Property, name));
            }

            needName = false;
            if (index < text.Length && text[index] == '.')
            {
                needName = true;
                if (++index == text.Length || text[index] == '[')
                {
                    throw Error(code);
                }
            }
            else if (index < text.Length && text[index] != '[')
            {
                throw Error(code);
            }
        }

        if (steps.Count == 0)
        {
            throw Error(code);
        }

        return new(steps.AsReadOnly());
    }

    internal static List<string> SplitAnd(string text)
    {
        var result = new List<string>();
        var start = 0;
        var brackets = 0;
        char quote = '\0';
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (brackets > 0 && character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '[')
            {
                brackets++;
            }
            else if (character == ']')
            {
                brackets--;
            }
            else if (character == ',' && brackets == 0)
            {
                result.Add(text[start..index]);
                start = index + 1;
            }
            else if (brackets == 0 && character is '=' or '!' or '<' or '>')
            {
                var comma = text.IndexOf(',', index);
                if (comma < 0)
                {
                    break;
                }

                result.Add(text[start..comma]);
                start = comma + 1;
                index = comma;
            }
        }

        result.Add(text[start..]);
        return result;
    }

    internal static (string Path, string Operator, string? Value) Expression(string text, string code)
    {
        var brackets = 0;
        char quote = '\0';
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (brackets > 0 && character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '[')
            {
                brackets++;
            }
            else if (character == ']')
            {
                brackets--;
            }
            else if (brackets == 0 && character is '=' or '!' or '<' or '>')
            {
                var length = index + 1 < text.Length && (text[index + 1] == '=' || character == '<' && text[index + 1] == '>') ? 2 : 1;
                var operation = text.Substring(index, length);
                if (operation is not ("=" or "!=" or "<>" or "<" or "<=" or ">" or ">="))
                {
                    throw Error(code);
                }

                return (text[..index], operation, text[(index + length)..]);
            }
        }

        return (text, "", null);
    }

    private static RegistryException Error(string code) => Diagnostics.Error(code, "", "The query dot-notation path or expression is invalid.");
}
