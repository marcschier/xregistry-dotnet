// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;

namespace XRegistry.Http;

/// <summary>Encodes and decodes attribute strings using the xRegistry HTTP header rules.</summary>
public static class RegistryHeaderEncoding
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private const string Hex = "0123456789ABCDEF";

    /// <summary>Percent-encodes the characters required by the xRegistry HTTP binding.</summary>
    /// <param name="value">The canonical attribute string, not an already encoded header.</param>
    /// <param name="maxEncodedBytes">The maximum resulting ASCII byte length.</param>
    /// <returns>The canonical header value, with uppercase hexadecimal escapes.</returns>
    /// <exception cref="FormatException">The string contains invalid Unicode or exceeds its byte limit.</exception>
    public static string Encode(string value, int maxEncodedBytes = 65536)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxEncodedBytes);
        if (value.Length > maxEncodedBytes)
        {
            throw new FormatException("The encoded header exceeds its byte limit.");
        }

        byte[] bytes;
        try
        {
            bytes = s_utf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new FormatException("The attribute contains invalid Unicode.", exception);
        }

        var length = 0;
        foreach (var item in bytes)
        {
            var required = MustEncode(item) ? 3 : 1;
            if (length > maxEncodedBytes - required)
            {
                throw new FormatException("The encoded header exceeds its byte limit.");
            }

            length += required;
        }

        return string.Create(length, bytes, static (output, input) =>
        {
            var index = 0;
            foreach (var item in input)
            {
                if (MustEncode(item))
                {
                    output[index++] = '%';
                    output[index++] = Hex[item >> 4];
                    output[index++] = Hex[item & 15];
                }
                else
                {
                    output[index++] = (char)item;
                }
            }
        });
    }

    /// <summary>Unquotes a legacy header and performs exactly one strict UTF-8 percent-decoding pass.</summary>
    /// <param name="value">The HTTP field value, optionally surrounded by transport whitespace.</param>
    /// <param name="maxDecodedBytes">The maximum decoded UTF-8 byte length.</param>
    /// <returns>The decoded attribute string. Escaped CR/LF remains data, not an emitted HTTP header.</returns>
    /// <exception cref="FormatException">The quoting, percent encoding, UTF-8, or byte budget is invalid.</exception>
    public static string Decode(string value, int maxDecodedBytes = 65536)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecodedBytes);
        if (value.Length > 6L * maxDecodedBytes + 8)
        {
            throw new FormatException("The header exceeds its encoded input budget.");
        }

        var unquoted = Unquote(value.Trim(' ', '\t'));
        var bytes = new byte[Math.Min(maxDecodedBytes, unquoted.Length)];
        var count = 0;
        for (var index = 0; index < unquoted.Length; index++)
        {
            var character = unquoted[index];
            byte item;
            if (character == '%')
            {
                if (index + 2 >= unquoted.Length)
                {
                    throw new FormatException("The header contains an incomplete percent escape.");
                }

                var high = HexValue(unquoted[++index]);
                var low = HexValue(unquoted[++index]);
                if (high < 0 || low < 0)
                {
                    throw new FormatException("The header contains an invalid percent escape.");
                }

                item = (byte)((high << 4) | low);
            }
            else
            {
                if (character is < ' ' or > '~')
                {
                    throw new FormatException("Unencoded header characters must be printable ASCII.");
                }

                item = (byte)character;
            }

            if (count == bytes.Length)
            {
                throw new FormatException("The decoded header exceeds its byte limit.");
            }

            bytes[count++] = item;
        }

        try
        {
            return s_utf8.GetString(bytes.AsSpan(0, count));
        }
        catch (DecoderFallbackException exception)
        {
            throw new FormatException("The header contains invalid UTF-8.", exception);
        }
    }

    private static string Unquote(string value)
    {
        if (!value.StartsWith('"'))
        {
            if (value.Contains('"', StringComparison.Ordinal))
            {
                throw new FormatException("A legacy quoted header must contain one complete quoted string.");
            }

            return value;
        }

        if (value.Length < 2 || value[^1] != '"')
        {
            throw new FormatException("The legacy quoted header is unterminated.");
        }

        var builder = new StringBuilder(value.Length - 2);
        for (var index = 1; index < value.Length - 1; index++)
        {
            var character = value[index];
            if (character == '\\')
            {
                if (++index == value.Length - 1)
                {
                    throw new FormatException("The legacy quoted header has an incomplete escape.");
                }

                character = value[index];
            }
            else if (character == '"')
            {
                throw new FormatException("The legacy quoted header has trailing or unescaped content.");
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static bool MustEncode(byte value) => value is < 0x21 or > 0x7e or 0x22 or 0x25;

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1
    };
}
