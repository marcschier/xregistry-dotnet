// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Collections.ObjectModel;

namespace XRegistry.Validation;

/// <summary>Identifies the syntax used by an API operation selector.</summary>
public enum ApiOperationSelectorKind
{
    /// <summary>An operation name, such as an OpenAPI operationId.</summary>
    Operation,
    /// <summary>A service and operation name.</summary>
    ServiceOperation,
    /// <summary>An interface and operation name.</summary>
    InterfaceOperation,
    /// <summary>An interface operation selected by its input and output names.</summary>
    InterfaceOperationInputOutput,
    /// <summary>A GraphQL root operation kind and field name.</summary>
    GraphQlField,
    /// <summary>An RFC 6901 pointer to an operation object.</summary>
    JsonPointer
}

/// <summary>A parsed, decoded API operation selector.</summary>
public sealed class ApiOperationSelector
{
    internal ApiOperationSelector(ApiOperationSelectorKind kind, IReadOnlyList<string> tokens)
    {
        Kind = kind;
        Tokens = tokens;
    }

    /// <summary>Gets the selector syntax.</summary>
    public ApiOperationSelectorKind Kind { get; }

    /// <summary>Gets decoded tokens in selector order. JSON Pointer tokens are already unescaped.</summary>
    public IReadOnlyList<string> Tokens { get; }
}

/// <summary>A parsed API operation reference, before entity and document resolution.</summary>
public sealed class ApiOperationReference
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    private ApiOperationReference(string? entityReference, IReadOnlyList<string>? registryEntityPointer,
        ApiOperationSelector selector)
    {
        EntityReference = entityReference;
        RegistryEntityPointer = registryEntityPointer;
        Selector = selector;
    }

    /// <summary>Gets the XID or absolute URL before the operation fragment, or null for a document-view reference.</summary>
    public string? EntityReference { get; }

    /// <summary>Gets the decoded Registry document pointer for a document-view reference.</summary>
    public IReadOnlyList<string>? RegistryEntityPointer { get; }

    /// <summary>Gets the parsed operation selector.</summary>
    public ApiOperationSelector Selector { get; }

    /// <summary>Parses an xRegistry entity reference combined with an API operation locator.</summary>
    /// <exception cref="FormatException">The reference or selector is malformed or unsupported by this draft.</exception>
    public static ApiOperationReference Parse(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var hash = reference.IndexOf('#');
        if (hash < 0 || reference.IndexOf('#', hash + 1) >= 0)
        {
            throw Invalid("An API operation reference must contain exactly one raw fragment delimiter.");
        }

        var fragment = reference[(hash + 1)..];
        if (hash == 0 && fragment.StartsWith("/apigroups/", StringComparison.Ordinal))
        {
            var separator = fragment.IndexOf(':');
            if (separator <= 0 || separator == fragment.Length - 1)
            {
                throw Invalid("A document-view API reference must separate its Registry pointer and operation selector with a colon.");
            }

            var registryPointer = ParsePointer(PercentDecode(fragment[..separator]));
            var selector = ParseSelector(fragment[(separator + 1)..]);
            return new(null, registryPointer, selector);
        }

        var entityReference = reference[..hash];
        if (entityReference.Length == 0 || fragment.Length == 0)
        {
            throw Invalid("An API operation reference requires an entity reference and a non-empty operation selector.");
        }

        return new(entityReference, null, ParseSelector(fragment));
    }

    private static ApiOperationSelector ParseSelector(string rawSelector)
    {
        var selector = PercentDecode(rawSelector);
        if (selector.Length != 0 && selector[0] == '/')
        {
            return new(ApiOperationSelectorKind.JsonPointer, ParsePointer(selector));
        }

        var tokens = selector.Split('/');
        if (tokens.Any(static token => token.Length == 0))
        {
            throw Invalid("API operation selector tokens must be non-empty.");
        }

        if (tokens[0] == "operations" && tokens.Length == 2)
        {
            return Create(ApiOperationSelectorKind.Operation, tokens, 1);
        }

        if (tokens[0] == "services" && tokens.Length == 4 && tokens[2] == "operations")
        {
            return Create(ApiOperationSelectorKind.ServiceOperation, tokens, 1, 3);
        }

        if (tokens[0] == "interfaces" && tokens.Length == 4 && tokens[2] == "operations")
        {
            return Create(ApiOperationSelectorKind.InterfaceOperation, tokens, 1, 3);
        }

        if (tokens[0] == "interfaces" && tokens.Length == 8 && tokens[2] == "operations" &&
            tokens[4] == "input" && tokens[6] == "output")
        {
            return Create(ApiOperationSelectorKind.InterfaceOperationInputOutput, tokens, 1, 3, 5, 7);
        }

        if (tokens[0] == "operations" && tokens.Length == 3 &&
            tokens[1] is "query" or "mutation" or "subscription")
        {
            return Create(ApiOperationSelectorKind.GraphQlField, tokens, 2);
        }

        throw Invalid("The API operation selector does not match a supported locator form.");
    }

    private static ApiOperationSelector Create(ApiOperationSelectorKind kind, string[] source, params int[] nameIndexes)
    {
        var tokens = (string[])source.Clone();
        foreach (var index in nameIndexes)
        {
            tokens[index] = DecodeTildeEscapes(tokens[index]);
            if (tokens[index].Length == 0)
            {
                throw Invalid("API operation selector name tokens must be non-empty.");
            }
        }

        return new(kind, Array.AsReadOnly(tokens));
    }

    private static ReadOnlyCollection<string> ParsePointer(string pointer)
    {
        if (pointer.Length == 0 || pointer[0] != '/')
        {
            throw Invalid("A JSON Pointer selector must begin with '/'.");
        }

        var rawTokens = pointer[1..].Split('/');
        if (rawTokens.Length == 0 || rawTokens.Any(static token => token.Length == 0))
        {
            throw Invalid("API Registry and JSON Pointer tokens must be non-empty.");
        }

        var tokens = new string[rawTokens.Length];
        for (var index = 0; index < rawTokens.Length; index++)
        {
            tokens[index] = DecodeTildeEscapes(rawTokens[index]);
        }

        return Array.AsReadOnly(tokens);
    }

    private static string DecodeTildeEscapes(string token)
    {
        var decoded = new StringBuilder(token.Length);
        for (var index = 0; index < token.Length; index++)
        {
            var character = token[index];
            if (character != '~')
            {
                decoded.Append(character);
                continue;
            }

            if (++index >= token.Length || token[index] is not ('0' or '1'))
            {
                throw Invalid("A selector token contains an invalid JSON Pointer tilde escape.");
            }

            decoded.Append(token[index] == '0' ? '~' : '/');
        }

        return decoded.ToString();
    }

    private static string PercentDecode(string value)
    {
        var bytes = new byte[value.Length];
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length ||
                    !byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var decoded))
                {
                    throw Invalid("A reference contains an invalid percent escape.");
                }

                bytes[count++] = decoded;
                index += 2;
                continue;
            }

            if (character > 0x7f || character == ' ' || character is '{' or '}')
            {
                throw Invalid("Non-ASCII characters, spaces, and braces must be percent-encoded.");
            }

            bytes[count++] = (byte)character;
        }

        try
        {
            return s_utf8.GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException)
        {
            throw Invalid("A reference contains invalid UTF-8 percent encoding.");
        }
    }

    private static FormatException Invalid(string message) => new(message);
}
