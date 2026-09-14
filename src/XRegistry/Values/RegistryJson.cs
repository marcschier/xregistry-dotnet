using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XRegistry;

/// <summary>Distinguishes an absent member, an explicit JSON null, and a present non-null value.</summary>
public enum JsonPresence
{
    /// <summary>The member does not exist.</summary>
    Absent,
    /// <summary>The member is explicitly null.</summary>
    Null,
    /// <summary>The member exists, including an empty string, array or object.</summary>
    Value
}

/// <summary>
/// Immutable, bounded, independently owned JSON. Returned elements remain usable without disposal
/// and never borrow a caller's buffer or disposable document.
/// </summary>
public sealed class RegistryJson
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    private RegistryJson(JsonElement root) => RootElement = root;

    /// <summary>Gets the owned root, preserving numeric tokens and JSON value kinds.</summary>
    public JsonElement RootElement { get; }

    /// <summary>Parses strictly encoded UTF-8, rejecting duplicate names at every depth and exhausted budgets.</summary>
    /// <exception cref="RegistryException">The input is invalid or exceeds a configured limit.</exception>
    public static RegistryJson Parse(ReadOnlySpan<byte> utf8Json, RegistryJsonLimits? limits = null)
    {
        using var document = ParseDocument(utf8Json, limits);
        return new RegistryJson(document.RootElement.Clone());
    }

    /// <summary>Validates the same strict JSON contract and transfers an owning document to the caller.</summary>
    /// <remarks>The caller must dispose the returned document. This avoids a second parse for document-based adapters.</remarks>
    public static JsonDocument ParseDocument(ReadOnlySpan<byte> utf8Json, RegistryJsonLimits? limits = null)
    {
        limits ??= new RegistryJsonLimits();
        limits.Validate();
        if (utf8Json.Length > limits.MaxBytes)
        {
            throw Diagnostics.Error("byte_limit", "", "The JSON exceeds its encoded byte budget.");
        }

        try
        {
            s_utf8.GetCharCount(utf8Json);
        }
        catch (DecoderFallbackException exception)
        {
            throw new RegistryException(new("invalid_utf8", "", "The JSON contains invalid UTF-8."), exception);
        }

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 257 });
        var path = "";
        try
        {
            if (!reader.Read())
            {
                throw Diagnostics.Error("invalid_json", "", "A JSON value is required.");
            }

            var nodes = 0;
            ValidateValue(ref reader, limits, "", ref path, ref nodes);
            if (reader.Read())
            {
                throw Diagnostics.Error("invalid_json", "", "Only one JSON value is allowed.");
            }

            reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = limits.MaxDepth });
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException exception)
        {
            throw new RegistryException(new("invalid_json", path, "The JSON syntax or nesting depth is invalid."), exception);
        }
    }

    /// <summary>Encodes and parses JSON without replacing invalid UTF-16 surrogates.</summary>
    public static RegistryJson Parse(string json, RegistryJsonLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        limits ??= new RegistryJsonLimits();
        limits.Validate();
        try
        {
            if (s_utf8.GetByteCount(json) > limits.MaxBytes)
            {
                throw Diagnostics.Error("byte_limit", "", "The JSON exceeds its encoded byte budget.");
            }

            return Parse(s_utf8.GetBytes(json), limits);
        }
        catch (EncoderFallbackException exception)
        {
            throw new RegistryException(new("invalid_unicode", "", "The JSON contains invalid Unicode."), exception);
        }
    }

    /// <summary>Reads one bounded JSON value, honoring cancellation and leaving the caller's stream open.</summary>
    public static async ValueTask<RegistryJson> ParseAsync(Stream stream, RegistryJsonLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= new RegistryJsonLimits();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var available = (int)Math.Min(8192L, (long)limits.MaxBytes - buffer.WrittenCount + 1);
            var read = await stream.ReadAsync(buffer.GetMemory(available)[..available], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (read > limits.MaxBytes - buffer.WrittenCount)
            {
                throw Diagnostics.Error("byte_limit", "", "The JSON exceeds its encoded byte budget.");
            }

            buffer.Advance(read);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Parse(buffer.WrittenSpan, limits);
    }

    /// <summary>Validates and clones an existing element directly, without a serialization/parsing copy roundtrip.</summary>
    public static RegistryJson FromElement(JsonElement element, RegistryJsonLimits? limits = null)
    {
        limits ??= new RegistryJsonLimits();
        limits.Validate();
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            throw Diagnostics.Error("invalid_json", "", "An undefined element is not a JSON value.");
        }

        var raw = element.GetRawText();
        if (s_utf8.GetByteCount(raw) > limits.MaxBytes)
        {
            throw Diagnostics.Error("byte_limit", "", "The JSON exceeds its encoded byte budget.");
        }

        var reader = new Utf8JsonReader(s_utf8.GetBytes(raw), new JsonReaderOptions { MaxDepth = 257 });
        var path = "";
        var nodes = 0;
        try
        {
            reader.Read();
            ValidateValue(ref reader, limits, "", ref path, ref nodes);
        }
        catch (JsonException exception)
        {
            throw new RegistryException(new("invalid_json", path, "The JSON syntax or nesting depth is invalid."), exception);
        }

        return new RegistryJson(element.Clone());
    }

    /// <summary>Gets a root object's member presence. A non-object root throws <see cref="InvalidOperationException"/>.</summary>
    public JsonPresence GetPresence(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return !RootElement.TryGetProperty(name, out var value) ? JsonPresence.Absent :
            value.ValueKind == JsonValueKind.Null ? JsonPresence.Null : JsonPresence.Value;
    }

    /// <summary>Creates independently owned JSON with a byte-bounded writer, then validates its complete value.</summary>
    /// <remarks>The callback must synchronously write one complete value and must not retain the writer. Callback failures are propagated without returning partial JSON.</remarks>
    public static RegistryJson Create(Action<Utf8JsonWriter> write, RegistryJsonLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        limits ??= new RegistryJsonLimits();
        limits.Validate();
        var buffer = new BoundedJsonBufferWriter(limits.MaxBytes);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 256 }))
        {
            try
            {
                write(writer);
                writer.Flush();
            }
            finally
            {
                // Disposal must not retry a failed write or publish a partial callback's output.
                writer.Reset();
            }
        }

        return Parse(buffer.WrittenSpan, limits);
    }

    private static void ValidateValue(ref Utf8JsonReader reader, RegistryJsonLimits limits,
        string path, ref string currentPath, ref int nodes)
    {
        currentPath = path;
        if (++nodes > limits.MaxNodes)
        {
            throw Diagnostics.Error("node_limit", path, "The JSON exceeds its value count budget.");
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray &&
            reader.CurrentDepth >= limits.MaxDepth)
        {
            throw Diagnostics.Error("depth_limit", path, "The JSON exceeds its nesting budget.");
        }

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var names = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    ValidateEscapes(reader.ValueSpan, path);
                    var name = reader.GetString()!;
                    currentPath = Diagnostics.At(path, name);
                    if (!names.Add(name))
                    {
                        throw Diagnostics.Error("duplicate_member", currentPath, "Duplicate JSON object members are not allowed.");
                    }

                    reader.Read();
                    ValidateValue(ref reader, limits, currentPath, ref currentPath, ref nodes);
                }

                break;
            case JsonTokenType.StartArray:
                var index = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    ValidateValue(ref reader, limits,
                        Diagnostics.At(path, (index++).ToString(CultureInfo.InvariantCulture)), ref currentPath, ref nodes);
                }

                break;
            case JsonTokenType.String:
                ValidateEscapes(reader.ValueSpan, path);
                break;
            case JsonTokenType.Number:
                RegistryNumber.ValidateToken(Encoding.UTF8.GetString(reader.ValueSpan), limits, path);
                break;
        }
    }

    private static void ValidateEscapes(ReadOnlySpan<byte> token, string path)
    {
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != '\\')
            {
                continue;
            }

            if (token[++i] != 'u')
            {
                continue;
            }

            var value = Hex4(token.Slice(i + 1, 4));
            i += 4;
            if (value is >= 0xd800 and <= 0xdbff)
            {
                if (i + 6 >= token.Length || token[i + 1] != '\\' || token[i + 2] != 'u' ||
                    Hex4(token.Slice(i + 3, 4)) is < 0xdc00 or > 0xdfff)
                {
                    throw Diagnostics.Error("invalid_unicode", path, "An escaped high surrogate must have a low surrogate.");
                }

                i += 6;
            }
            else if (value is >= 0xdc00 and <= 0xdfff)
            {
                throw Diagnostics.Error("invalid_unicode", path, "An escaped low surrogate must follow a high surrogate.");
            }
        }
    }

    private static int Hex4(ReadOnlySpan<byte> text)
    {
        var value = 0;
        foreach (var digit in text)
        {
            value = value * 16 + (digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10);
        }

        return value;
    }
}
