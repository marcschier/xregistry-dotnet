using System.Net.Http.Headers;

namespace XRegistry.Client;

internal enum RegistryContentCoding
{
    Gzip,
    Deflate,
    Brotli
}

internal static class RegistryHttpContentDecoding
{
    private const string Gzip = "gzip";
    private const string Deflate = "deflate";
    private const string Brotli = "br";
    private const int MaxContentEncodingCharacters = 1024;

    internal static void ConfigureAcceptEncoding(HttpRequestHeaders headers, XRegistryHttpClientOptions options)
    {
        if (options.EnableContentDecoding)
        {
            headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(Gzip));
            headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(Deflate));
            headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(Brotli));
        }
        else
        {
            headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        }
    }

    internal static List<RegistryContentCoding> GetCodings(
        IReadOnlyList<string> fields, XRegistryHttpClientOptions options)
    {
        var result = new List<RegistryContentCoding>();
        var characters = 0;
        var tokens = 0;
        foreach (var field in fields)
        {
            if (field.Length > MaxContentEncodingCharacters - characters)
            {
                throw new InvalidDataException("The Content-Encoding header exceeds its character limit.");
            }

            characters += field.Length;
            var remaining = field.AsSpan();
            while (true)
            {
                var comma = remaining.IndexOf(',');
                var token = (comma < 0 ? remaining : remaining[..comma]).Trim(" \t");
                if (token.IsEmpty || ++tokens > options.MaxContentCodingDepth)
                {
                    throw new InvalidDataException("The Content-Encoding list is empty or exceeds its coding limit.");
                }

                foreach (var character in token)
                {
                    if (!char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character))
                    {
                        throw new InvalidDataException("The Content-Encoding header contains an invalid token.");
                    }
                }

                if (!token.Equals("identity", StringComparison.OrdinalIgnoreCase))
                {
                    if (!options.EnableContentDecoding)
                    {
                        throw new NotSupportedException("Compressed response decoding requires EnableContentDecoding.");
                    }

                    result.Add(token.Equals(Gzip, StringComparison.OrdinalIgnoreCase) ? RegistryContentCoding.Gzip :
                        token.Equals(Deflate, StringComparison.OrdinalIgnoreCase) ? RegistryContentCoding.Deflate :
                        token.Equals(Brotli, StringComparison.OrdinalIgnoreCase) ? RegistryContentCoding.Brotli :
                        throw new NotSupportedException("The response uses an unsupported content coding."));
                }

                if (comma < 0)
                {
                    break;
                }

                remaining = remaining[(comma + 1)..];
            }
        }

        return result;
    }

    internal static RegistryContentReader CreateReader(
        Stream source, IReadOnlyList<RegistryContentCoding> codings, long decodedLimit,
        XRegistryHttpClientOptions options)
    {
        RegistryContentReader reader = new RegistryRawContentReader(source,
            codings.Count == 0 ? Math.Min(decodedLimit, options.MaxEncodedResponseBytes) : options.MaxEncodedResponseBytes);
        var transferred = false;
        try
        {
            var work = new RegistryGzipWorkBudget(options);
            for (var index = codings.Count - 1; index >= 0; index--)
            {
                var limit = index == 0 ? decodedLimit : options.MaxEncodedResponseBytes;
                reader = codings[index] == RegistryContentCoding.Brotli ?
                    new RegistryBrotliContentReader(reader, limit) :
                    new RegistryDeflateContentReader(reader, limit, codings[index] == RegistryContentCoding.Gzip, work);
            }

            transferred = true;
            return reader;
        }
        finally
        {
            if (!transferred)
            {
                reader.Dispose();
            }
        }
    }
}

internal sealed class RegistryGzipWorkBudget(XRegistryHttpClientOptions options)
{
    private int _members = options.MaxGzipMembers;
    private int _headerBytes = options.MaxGzipHeaderBytes;

    internal void BeginMember()
    {
        if (_members-- == 0)
        {
            throw new InvalidDataException("The response exceeds its cumulative gzip member limit.");
        }
    }

    internal void ReadHeaderByte()
    {
        if (_headerBytes-- == 0)
        {
            throw new InvalidDataException("The response exceeds its cumulative gzip header byte limit.");
        }
    }
}
