// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;

namespace XRegistry.Models;

/// <summary>The decoded standardized query parameters of a CloudEvents WoT source.</summary>
/// <remarks>
/// The caller validates the complete source URI-reference under CloudEvents Core.
/// Query parsing does not resolve a TD, select a Version or interpret additional parameters.
/// </remarks>
public sealed class WotCloudEventSourceQuery
{
    private const int MaximumLength = 16_384;
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    private WotCloudEventSourceQuery(string wotId, string? versionId, string additionalParameters)
    {
        WotId = wotId;
        VersionId = versionId;
        AdditionalParameters = additionalParameters;
    }

    /// <summary>Gets the exactly once decoded Thing identity.</summary>
    public string WotId { get; }

    /// <summary>Gets an exact Version id, or null to request the Core default Version.</summary>
    public string? VersionId { get; }

    /// <summary>Gets the uninterpreted additional query text, without its separating semicolon.</summary>
    public string AdditionalParameters { get; }

    /// <summary>Parses the raw query without '?' or the source's fragment, within a 16,384-character limit.</summary>
    public static WotCloudEventSourceQuery Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        CheckLength(query.Length);
        var parameters = query.Split(';');
        string? wotId = null;
        string? versionId = null;
        var additionalIndex = parameters.Length;
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var equal = parameter.IndexOf('=');
            var name = equal < 0 ? parameter : parameter[..equal];
            if (name == "wotid")
            {
                if (index != 0 || wotId is not null || equal < 0)
                {
                    throw Invalid("wotid must appear exactly once as the first query parameter.");
                }
                wotId = Decode(parameter[(equal + 1)..]);
            }
            else if (name == "wotversion")
            {
                if (index != 1 || wotId is null || versionId is not null || equal < 0)
                {
                    throw Invalid("wotversion must appear at most once immediately after wotid.");
                }
                versionId = Decode(parameter[(equal + 1)..]);
            }
            else
            {
                additionalIndex = Math.Min(additionalIndex, index);
            }
        }
        if (wotId is null)
        {
            throw Invalid("The query requires a nonempty wotid parameter.");
        }
        return new(wotId, versionId, string.Join(';', parameters, additionalIndex,
            parameters.Length - additionalIndex));
    }

    /// <summary>Creates canonical standardized query parameters from decoded values, without a leading '?'.</summary>
    /// <remarks>Additional parameters are supplied by the publication contract, not this encoder.</remarks>
    public static string Encode(string wotId, string? versionId = null)
    {
        ArgumentNullException.ThrowIfNull(wotId);
        var result = "wotid=" + EncodeValue(wotId);
        if (versionId is not null)
        {
            result += ";wotversion=" + EncodeValue(versionId);
        }
        CheckLength(result.Length);
        return result;
    }

    private static string Decode(string value)
    {
        if (value.Length == 0)
        {
            throw Invalid("Standardized query values must be nonempty.");
        }
        var bytes = new byte[value.Length];
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length)
                {
                    throw Invalid("A standardized value contains an invalid percent escape.");
                }
                var high = Hex(value[index + 1]);
                var low = Hex(value[index + 2]);
                if (high < 0 || low < 0)
                {
                    throw Invalid("A standardized value contains an invalid percent escape.");
                }
                bytes[count++] = (byte)((high << 4) | low);
                index += 2;
            }
            else if (Allowed(character))
            {
                bytes[count++] = (byte)character;
            }
            else
            {
                throw Invalid("A standardized value contains an unescaped forbidden character.");
            }
        }
        try
        {
            return s_utf8.GetString(bytes, 0, count);
        }
        catch (DecoderFallbackException)
        {
            throw Invalid("A standardized value is not valid UTF-8.");
        }
    }

    private static string EncodeValue(string value)
    {
        if (value.Length == 0)
        {
            throw Invalid("Standardized query values must be nonempty.");
        }
        CheckLength(value.Length);
        byte[] bytes;
        try
        {
            bytes = s_utf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw Invalid("A standardized value contains invalid Unicode.");
        }
        var result = new StringBuilder();
        const string hexadecimal = "0123456789ABCDEF";
        foreach (var valueByte in bytes)
        {
            if (Allowed((char)valueByte))
            {
                result.Append((char)valueByte);
            }
            else
            {
                result.Append('%').Append(hexadecimal[valueByte >> 4]).Append(hexadecimal[valueByte & 15]);
            }
            CheckLength(result.Length);
        }
        return result.ToString();
    }

    private static bool Allowed(char value) =>
        char.IsAsciiLetterOrDigit(value) || "-._~!$'()*,:@/?".Contains(value, StringComparison.Ordinal);

    private static int Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1
    };

    private static void CheckLength(int length)
    {
        if (length > MaximumLength)
        {
            throw new RegistryException(new("limit.source-query", "/source",
                "The CloudEvents WoT source query exceeds 16,384 characters."));
        }
    }

    private static RegistryException Invalid(string detail) => new(new("invalid-source", "/source", detail));
}
