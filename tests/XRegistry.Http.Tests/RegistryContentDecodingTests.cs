using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;

namespace XRegistry.Http.Tests;

public class RegistryContentDecodingTests
{
    private const string Metadata = """{"value":"decoded","n":42}""";
    private const string BinaryHex = "00FF0D0A018000";
    private const string OptionalGzip =
        "H4sIHwAAAAAA/wYAWFkCAAD/Zml4dHVyZS5iaW4AaW5kZXBlbmRlbnQA0b2rVipLzClNVbJSSklNzk9JTVHSUcpTsjIxqgUAEtsFthoAAAA=";
    private static readonly string[] MixedCaseContentEncoding = ["Identity, GZip", " BR, identity "];

    [Test]
    [Arguments("identity")]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    [Arguments("gzip, br")]
    [Arguments("br, gzip")]
    [Arguments("gzip, deflate")]
    [Arguments("deflate, gzip, br")]
    [Arguments("gzip, gzip")]
    [Arguments("br, deflate")]
    [Arguments("gzip, deflate, br, gzip")]
    public async Task IndependentFixturesDecodeExactBinaryDocuments(string coding)
    {
        var encoded = BinaryFixture(coding);
        await using var app = await StartBodyAsync(encoded, coding, fragmentSize: 1);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();

        await response.CopyDocumentToAsync(destination);
        response.Dispose();

        await Assert.That(Convert.ToHexString(destination.ToArray())).IsEqualTo(BinaryHex);
        await Assert.That(response.BodyBytesRead).IsEqualTo(7L);
        await Assert.That(destination.CanWrite).IsTrue();
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    [Arguments("gzip, br")]
    [Arguments("br, gzip")]
    [Arguments("deflate, gzip, br")]
    public async Task MetadataLifetimeAndOriginalStatusAndHeadersSurviveDecoding(string coding)
    {
        var encoded = MetadataFixture(coding);
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/body", async (HttpContext context) =>
            {
                context.Response.StatusCode = 400;
                context.Response.Headers.Location = "/unchanged";
                context.Response.Headers.ETag = "\"encoded-version\"";
                context.Response.Headers["X-Correlation-Id"] = "decode-fixture";
                await WriteBodyAsync(context, encoded, coding);
            }));
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        var originalCodings = string.Join(", ", response.ContentHeaders.ContentEncoding);
        using var metadata = await response.ReadMetadataAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/unchanged");
        await Assert.That(response.Headers.ETag!.Tag).IsEqualTo("\"encoded-version\"");
        await Assert.That(response.Headers.GetValues("X-Correlation-Id").Single()).IsEqualTo("decode-fixture");
        await Assert.That(response.ContentHeaders.ContentLength).IsEqualTo((long?)encoded.Length);
        await Assert.That(string.Join(", ", response.ContentHeaders.ContentEncoding)).IsEqualTo(originalCodings);
        await Assert.That(originalCodings).IsEqualTo(coding);
        await Assert.That(response.BodyBytesRead).IsEqualTo(26L);
        response.Dispose();
        client.Dispose();
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(Metadata);
        await Assert.That(metadata.RootElement.GetProperty("value").GetString()).IsEqualTo("decoded");
        await Assert.That(metadata.RootElement.GetProperty("n").GetInt32()).IsEqualTo(42);
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task DefaultIdentityModeExplicitlyRejectsCompressedBodies(string coding)
    {
        string? negotiation = null;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/body", async (HttpContext context) =>
            {
                negotiation = context.Request.Headers.AcceptEncoding.ToString();
                await WriteBodyAsync(context, MetadataFixture(coding), coding);
            }));
        using var client = Client(app, new() { AllowLoopbackHttp = true });
        using var response = await client.SendAsync(HttpMethod.Get, "body");

        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<NotSupportedException>();
        await Assert.That(negotiation).IsEqualTo("identity");
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        await Assert.That(async () =>
        {
            using var second = await response.ReadMetadataAsync();
        }).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryRequestSurfaceUsesMatchingContentNegotiation(bool enabled)
    {
        var negotiations = new List<string>();
        string? upload = null;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.Map("/{**path}", async (HttpContext context) =>
            {
                negotiations.Add(context.Request.Headers.AcceptEncoding.ToString());
                if (context.Request.Method == "PUT")
                {
                    using var received = new MemoryStream();
                    await context.Request.Body.CopyToAsync(received, context.RequestAborted);
                    upload = Convert.ToHexString(received.ToArray());
                }

                var path = context.Request.Path.Value;
                if (path is "/registry/.xregistry" or "/.well-known/xregistry")
                {
                    await WriteBodyAsync(context, enabled ?
                        Convert.FromBase64String("GzIAAMTOlvqt06Vqoo1mKPZ7cMiB0+1a84AwmMgJBUE7vw9fYbfTB6KI3ZoIJEfVZekY9gE=") :
                        Encoding.UTF8.GetBytes("""{"registries":["https://registry.example/custom/"]}"""),
                        enabled ? "br" : null);
                }
                else if (path == "/registry/items")
                {
                    var second = context.Request.Query.ContainsKey("page");
                    if (!second)
                    {
                        context.Response.Headers.Link = "<?page=2>;rel=next;count=2";
                    }

                    await WriteBodyAsync(context, PageFixture(enabled ? "gzip" : "identity", second),
                        enabled ? "gzip" : null);
                }
                else
                {
                    if (path == "/registry/linked")
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Headers.ETag = "\"linked-version\"";
                    }

                    await WriteBodyAsync(context, MetadataFixture(enabled ? "deflate" : "identity"),
                        enabled ? "deflate" : null);
                }
            }));
        using var client = Client(app, Options() with { EnableContentDecoding = enabled });
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var metadata = await response.ReadMetadataAsync();
        using var source = new MemoryStream(Convert.FromHexString(BinaryHex));
        using var uploaded = await client.SendDocumentAsync(HttpMethod.Put, "body", source, "application/octet-stream");
        using var uploadedMetadata = await uploaded.ReadMetadataAsync();
        var linked = await client.GetLinkAsync(new Uri(client.Root, "body?initial=ignored"), "linked?cursor=%61&empty=&flag");
        using var linkedResponse = linked.Response;
        using var linkedDestination = new MemoryStream();
        await linkedResponse.CopyDocumentToAsync(linkedDestination);
        var registry = await client.DiscoverAsync();
        var host = await client.DiscoverAsync(RegistryDiscoveryLocation.Host);
        var pages = new List<RegistryCollectionPage>();
        await foreach (var page in client.ReadCollectionPagesAsync("items"))
        {
            pages.Add(page);
        }

        await Assert.That(negotiations.Count).IsEqualTo(7);
        foreach (var negotiation in negotiations)
        {
            await Assert.That(negotiation).IsEqualTo(enabled ? "gzip, deflate, br" : "identity");
        }

        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(Metadata);
        await Assert.That(uploadedMetadata.RootElement.GetRawText()).IsEqualTo(Metadata);
        await Assert.That(linked.RequestUri.PathAndQuery).IsEqualTo("/registry/linked?cursor=%61&empty=&flag");
        await Assert.That(linkedResponse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(linkedResponse.Headers.ETag!.Tag).IsEqualTo("\"linked-version\"");
        await Assert.That(Encoding.UTF8.GetString(linkedDestination.ToArray())).IsEqualTo(Metadata);
        await Assert.That(linkedResponse.BodyBytesRead).IsEqualTo(26L);
        await Assert.That(upload).IsEqualTo(BinaryHex);
        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(registry.Registries.Single().AbsoluteUri).IsEqualTo("https://registry.example/custom/");
        await Assert.That(host.Registries.Single().AbsoluteUri).IsEqualTo("https://registry.example/custom/");
        await Assert.That(pages.Count).IsEqualTo(2);
        await Assert.That(pages[0].Records.GetRawText()).IsEqualTo("""{"a":{}}""");
        await Assert.That(pages[1].Records.GetRawText()).IsEqualTo("""{"b":{}}""");
    }

    [Test]
    [Arguments("identity", false)]
    [Arguments("identity", true)]
    [Arguments("gzip", false)]
    [Arguments("gzip", true)]
    [Arguments("deflate", false)]
    [Arguments("deflate", true)]
    [Arguments("br", false)]
    [Arguments("br", true)]
    public async Task EncodedByteLimitsAreExactWithAndWithoutContentLength(string coding, bool chunked)
    {
        var encoded = MetadataFixture(coding);
        await using var app = await StartBodyAsync(encoded, coding, chunked);
        foreach (var limit in new[] { encoded.Length, encoded.Length - 1 })
        {
            using var client = Client(app, Options() with { MaxEncodedResponseBytes = limit });
            using var response = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            using var metadataResponse = await client.SendAsync(HttpMethod.Get, "body");
            await Assert.That(response.ContentHeaders.ContentLength).IsEqualTo(chunked ? null : (long?)encoded.Length);
            if (limit == encoded.Length)
            {
                await response.CopyDocumentToAsync(destination);
                using var metadata = await metadataResponse.ReadMetadataAsync();
                await Assert.That(Encoding.UTF8.GetString(destination.ToArray())).IsEqualTo(Metadata);
                await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(Metadata);
                await Assert.That(response.BodyBytesRead).IsEqualTo(26L);
            }
            else
            {
                await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
                await Assert.That(async () =>
                {
                    using var metadata = await metadataResponse.ReadMetadataAsync();
                }).Throws<InvalidDataException>();
                await Assert.That(response.BodyBytesRead).IsEqualTo(destination.Length);
                await Assert.That(destination.Length).IsEqualTo(0L);
            }

            await Assert.That(destination.CanWrite).IsTrue();
        }
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task EncodedLimitsCountAcrossMultipleInputBuffersAndPreservePartialWriteCounts(string coding)
    {
        var encoded = StoredFixture(coding);
        var expected = Encoding.UTF8.GetBytes(new string('A', 20000) + new string('B', 20000));
        await using var app = await StartBodyAsync(encoded, coding, chunked: true, fragmentSize: 8191);
        foreach (var budget in new[] { encoded.Length, encoded.Length - 1 })
        {
            using var client = Client(app, Options() with { MaxEncodedResponseBytes = budget, MaxDocumentBytes = 40000 });
            using var response = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            if (budget == encoded.Length)
            {
                await response.CopyDocumentToAsync(destination);
                await Assert.That(destination.Length).IsEqualTo(40000L);
            }
            else
            {
                await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
                await Assert.That(destination.Length > 0).IsTrue();
                await Assert.That(destination.Length <= 40000).IsTrue();
            }

            await Assert.That(destination.ToArray().AsSpan().SequenceEqual(expected.AsSpan(0, (int)destination.Length)))
                .IsTrue();
            await Assert.That(response.BodyBytesRead).IsEqualTo(destination.Length);
            await Assert.That(destination.CanWrite).IsTrue();
        }
    }

    [Test]
    [Arguments("identity")]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task ZeroEncodedBudgetAllowsOnlyAnEmptyIdentityBody(string coding)
    {
        await using var app = await StartBodyAsync(EmptyFixture(coding), coding, chunked: true);
        using var client = Client(app, Options() with { MaxEncodedResponseBytes = 0, MaxDocumentBytes = 0 });
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();
        if (coding == "identity")
        {
            await response.CopyDocumentToAsync(destination);
        }
        else
        {
            await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
        }

        await Assert.That(destination.Length).IsEqualTo(0L);
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
    }

    [Test]
    [Arguments("identity")]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    [Arguments("gzip, br")]
    public async Task DecodedMetadataAndDocumentLimitsAreExact(string coding)
    {
        var encoded = MetadataFixture(coding);
        await using var app = await StartBodyAsync(encoded, coding);
        foreach (var limit in new[] { 26, 25 })
        {
            using var client = Client(app, Options() with { MaxMetadataBytes = limit, MaxDocumentBytes = limit });
            using var document = await client.SendAsync(HttpMethod.Get, "body");
            using var metadataResponse = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            if (limit == 26)
            {
                await document.CopyDocumentToAsync(destination);
                using var metadata = await metadataResponse.ReadMetadataAsync();
                await Assert.That(Encoding.UTF8.GetString(destination.ToArray())).IsEqualTo(Metadata);
                await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(Metadata);
                await Assert.That(document.BodyBytesRead).IsEqualTo(26L);
                await Assert.That(metadataResponse.BodyBytesRead).IsEqualTo(26L);
            }
            else
            {
                await Assert.That(async () => await document.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
                await Assert.That(async () =>
                {
                    using var metadata = await metadataResponse.ReadMetadataAsync();
                }).Throws<InvalidDataException>();
                await Assert.That(destination.Length <= limit).IsTrue();
                await Assert.That(document.BodyBytesRead).IsEqualTo(destination.Length);
                await Assert.That(metadataResponse.BodyBytesRead <= limit).IsTrue();
            }
        }
    }

    [Test]
    [Arguments("identity")]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task ZeroDocumentLimitAllowsOnlyAnEmptyDecodedRepresentation(string coding)
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/body", async (HttpContext context) =>
                await WriteBodyAsync(context, context.Request.Query.ContainsKey("present") ?
                    BinaryFixture(coding) : EmptyFixture(coding), coding)));
        using var client = Client(app, Options() with { MaxDocumentBytes = 0 });
        using var empty = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();
        await empty.CopyDocumentToAsync(destination);
        await Assert.That(empty.BodyBytesRead).IsEqualTo(0L);
        await Assert.That(destination.Length).IsEqualTo(0L);

        using var present = await client.SendAsync(HttpMethod.Get, "body", query: [new("present", null)]);
        await Assert.That(async () => await present.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
        await Assert.That(present.BodyBytesRead).IsEqualTo(0L);
        await Assert.That(destination.Length).IsEqualTo(0L);
        await Assert.That(destination.CanWrite).IsTrue();
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    [Arguments("gzip, br")]
    [Arguments("br, gzip")]
    [Arguments("gzip-options")]
    public async Task EveryTruncatedPrefixFailsBeforeMetadataIsReturned(string fixture)
    {
        var coding = fixture == "gzip-options" ? "gzip" : fixture;
        var encoded = fixture == "gzip-options" ? Convert.FromBase64String(OptionalGzip) : MetadataFixture(coding);
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/body", async (HttpContext context) =>
            {
                var length = int.Parse(context.Request.Query["length"].ToString(), CultureInfo.InvariantCulture);
                await WriteBodyAsync(context, encoded[..length], coding, chunked: true);
            }));
        using var client = Client(app);
        for (var length = 0; length < encoded.Length; length++)
        {
            using var response = await client.SendAsync(HttpMethod.Get, "body",
                query: [new("length", length.ToString(CultureInfo.InvariantCulture))]);
            await Assert.That(async () =>
            {
                using var metadata = await response.ReadMetadataAsync();
            }).Throws<InvalidDataException>();
            await Assert.That(response.BodyBytesRead <= 26).IsTrue();
        }
    }

    [Test]
    [Arguments("magic")]
    [Arguments("method")]
    [Arguments("reserved")]
    [Arguments("crc32")]
    [Arguments("isize")]
    [Arguments("fhcrc")]
    [Arguments("fhcrc-endian")]
    [Arguments("invalid-block")]
    public async Task MalformedGzipHeadersChecksumsAndSizesAreRejected(string corruption)
    {
        var encoded = corruption.StartsWith("fhcrc", StringComparison.Ordinal) ?
            Convert.FromBase64String(OptionalGzip) : MetadataFixture("gzip");
        switch (corruption)
        {
            case "magic": encoded[1] ^= 1; break;
            case "method": encoded[2] = 7; break;
            case "reserved": encoded[3] = 0x20; break;
            case "crc32": encoded[^8] ^= 1; break;
            case "isize": encoded[^4] ^= 1; break;
            case "fhcrc": encoded[42] ^= 1; break;
            case "fhcrc-endian": (encoded[42], encoded[43]) = (encoded[43], encoded[42]); break;
            case "invalid-block": encoded[10] = 7; break;
        }

        await using var app = await StartBodyAsync(encoded, "gzip", fragmentSize: 1);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("garbage")]
    [Arguments("zero")]
    [Arguments("short-header")]
    [Arguments("bad-header")]
    [Arguments("short-footer")]
    [Arguments("bad-crc")]
    [Arguments("bad-size")]
    [Arguments("bad-header-crc")]
    public async Task BadSubsequentGzipMembersAndTrailingDataAreRejected(string suffix)
    {
        var following = EmptyFixture("gzip");
        switch (suffix)
        {
            case "garbage": following = "trailing"u8.ToArray(); break;
            case "zero": following = [0]; break;
            case "short-header": following = following[..9]; break;
            case "bad-header": following[3] = 0x80; break;
            case "short-footer": following = following[..^1]; break;
            case "bad-crc": following[^8] ^= 1; break;
            case "bad-size": following[^4] ^= 1; break;
            case "bad-header-crc":
                following = Convert.FromBase64String(OptionalGzip);
                following[42] ^= 1;
                break;
        }

        byte[] encoded = [.. MetadataFixture("gzip"), .. following];
        await using var app = await StartBodyAsync(encoded, "gzip", chunked: true);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(26L);
        await Assert.That(async () =>
        {
            using var retry = await response.ReadMetadataAsync();
        }).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("method")]
    [Arguments("window")]
    [Arguments("header-check")]
    [Arguments("adler")]
    [Arguments("raw")]
    [Arguments("invalid-block")]
    public async Task ZlibRejectsMalformedHeadersChecksumsAndRawDeflate(string corruption)
    {
        var encoded = MetadataFixture("deflate");
        switch (corruption)
        {
            case "method": encoded[0] = 0x79; encoded[1] = 0x18; break;
            case "window": encoded[0] = 0x88; encoded[1] = 0x1c; break;
            case "header-check": encoded[1] ^= 1; break;
            case "adler": encoded[^1] ^= 1; break;
            case "raw": encoded = encoded[2..^4]; break;
            case "invalid-block": encoded[2] = 7; break;
        }

        await using var app = await StartBodyAsync(encoded, "deflate");
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();
    }

    [Test]
    public async Task PresetZlibDictionariesAreExplicitlyUnsupported()
    {
        await using var app = await StartBodyAsync(Convert.FromBase64String("ePl0nAgeq8YpAwB0nAge"), "deflate");
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<NotSupportedException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
    }

    [Test]
    public async Task ZlibSmallWindowHeadersAreSupported()
    {
        await using var app = await StartBodyAsync(
            Convert.FromBase64String("GNOrVipLzClNVbJSSklNzk9JTVHSUcpTsjIxqgUAdJwIHg=="), "deflate", fragmentSize: 1);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var metadata = await response.ReadMetadataAsync();
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(Metadata);
    }

    [Test]
    public async Task BrotliRejectsMalformedContent()
    {
        await using var app = await StartBodyAsync([255, 255, 255, 255], "br");
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();
        await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
    }

    [Test]
    [Arguments("deflate", "zero")]
    [Arguments("deflate", "stream")]
    [Arguments("deflate", "short-stream")]
    [Arguments("br", "zero")]
    [Arguments("br", "stream")]
    [Arguments("br", "short-stream")]
    public async Task SingleStreamCodingsRejectTrailingAndConcatenatedData(string coding, string suffix)
    {
        var following = suffix == "zero" ? [0] : MetadataFixture(coding);
        if (suffix == "short-stream")
        {
            following = following[..^1];
        }

        byte[] encoded = [.. MetadataFixture(coding), .. following];
        await using var app = await StartBodyAsync(encoded, coding, chunked: true);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("empty")]
    [Arguments("empty-empty")]
    [Arguments("empty-binary")]
    [Arguments("binary-empty")]
    [Arguments("binary-empty-binary")]
    public async Task GzipMembersConcatenateIncludingEmptyMembers(string members)
    {
        var encoded = new List<byte>();
        var expected = new StringBuilder();
        foreach (var member in members.Split('-'))
        {
            encoded.AddRange(member == "empty" ? EmptyFixture("gzip") : BinaryFixture("gzip"));
            if (member == "binary")
            {
                expected.Append(BinaryHex);
            }
        }

        await using var app = await StartBodyAsync(encoded.ToArray(), "gzip", fragmentSize: 1);
        using var client = Client(app, Options() with { MaxDocumentBytes = expected.Length / 2, MaxGzipMembers = 3 });
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();
        await response.CopyDocumentToAsync(destination);
        await Assert.That(Convert.ToHexString(destination.ToArray())).IsEqualTo(expected.ToString());
        await Assert.That(response.BodyBytesRead).IsEqualTo((long)expected.Length / 2);
    }

    [Test]
    public async Task GzipOptionalFieldsAndLittleEndianHeaderCrcAreValidated()
    {
        await using var app = await StartBodyAsync(Convert.FromBase64String(OptionalGzip), "gzip", fragmentSize: 1);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var metadata = await response.ReadMetadataAsync();
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(Metadata);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task GzipOptionalHeaderBytesHaveExactAndCumulativeBounds(int members)
    {
        var member = Convert.FromBase64String(OptionalGzip);
        var encoded = members == 1 ? member : [.. member, .. member];
        await using var app = await StartBodyAsync(encoded, "gzip");
        foreach (var budget in new[] { 44 * members, 44 * members - 1 })
        {
            using var client = Client(app, Options() with { MaxGzipHeaderBytes = budget });
            using var response = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            if (budget == 44 * members)
            {
                await response.CopyDocumentToAsync(destination);
                await Assert.That(Encoding.UTF8.GetString(destination.ToArray()))
                    .IsEqualTo(members == 1 ? Metadata : Metadata + Metadata);
            }
            else
            {
                await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
                await Assert.That(destination.Length).IsEqualTo((members - 1) * 26L);
            }
        }
    }

    [Test]
    public async Task EmptyGzipMembersCountTowardsTheExactMemberBudget()
    {
        var member = EmptyFixture("gzip");
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/body", async (HttpContext context) =>
                await WriteBodyAsync(context, context.Request.Query.ContainsKey("extra") ?
                    [.. member, .. member, .. member, .. member] : [.. member, .. member, .. member], "gzip")));
        using var client = Client(app, Options() with { MaxGzipMembers = 3, MaxDocumentBytes = 0 });
        using var exact = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();
        await exact.CopyDocumentToAsync(destination);
        await Assert.That(exact.BodyBytesRead).IsEqualTo(0L);
        using var extra = await client.SendAsync(HttpMethod.Get, "body", query: [new("extra", null)]);
        await Assert.That(async () => await extra.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
        await Assert.That(destination.Length).IsEqualTo(0L);
    }

    [Test]
    [Arguments("members")]
    [Arguments("headers")]
    public async Task GzipWorkBudgetsAreSharedAcrossCodingLayers(string budgetKind)
    {
        await using var app = await StartBodyAsync(BinaryFixture("gzip, gzip"), "gzip, gzip");
        foreach (var exact in new[] { true, false })
        {
            var options = budgetKind == "members" ?
                Options() with { MaxGzipMembers = exact ? 2 : 1 } :
                Options() with { MaxGzipHeaderBytes = exact ? 20 : 19 };
            using var client = Client(app, options);
            using var response = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            if (exact)
            {
                await response.CopyDocumentToAsync(destination);
                await Assert.That(Convert.ToHexString(destination.ToArray())).IsEqualTo(BinaryHex);
            }
            else
            {
                await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
                await Assert.That(destination.Length).IsEqualTo(0L);
            }
        }
    }

    [Test]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(16)]
    public async Task UnfinishedGzipOptionalFieldsStopAtTheHeaderBudget(int flag)
    {
        var header = EmptyFixture("gzip")[..10];
        header[3] = (byte)flag;
        byte[] encoded = [.. header, .. Enumerable.Repeat((byte)65, 128)];
        if (flag == 4)
        {
            encoded[10] = 255;
            encoded[11] = 255;
        }

        await using var app = await StartBodyAsync(encoded, "gzip");
        using var client = Client(app, Options() with { MaxGzipHeaderBytes = 32 });
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();
        var error = await Assert.That(async () => await response.CopyDocumentToAsync(destination))
            .Throws<InvalidDataException>();
        await Assert.That(error!.Message).Contains("header byte limit");
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
    }

    [Test]
    public async Task IntermediateCodingBytesHaveTheirOwnExactBudget()
    {
        var encoded = Convert.FromBase64String("GzsAAAQ8ZB/FlCLEIE0QxZhCqPaAapYH");
        await using var app = await StartBodyAsync(encoded, "gzip, br");
        foreach (var budget in new[] { 60, 59 })
        {
            using var client = Client(app, Options() with
            {
                MaxEncodedResponseBytes = budget,
                MaxDocumentBytes = 0,
                MaxGzipMembers = 3,
                MaxGzipHeaderBytes = 30
            });
            using var response = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            if (budget == 60)
            {
                await response.CopyDocumentToAsync(destination);
            }
            else
            {
                await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
            }

            await Assert.That(destination.Length).IsEqualTo(0L);
            await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        }
    }

    [Test]
    public async Task StackedStoredBlocksCrossInputBuffersWithoutLosingFramingBytes()
    {
        var encoded = Convert.FromBase64String(
            "G1ucAATCdr+73W6XSKQr0sjSQEceUIjdWhSBHvtNAQj9OJddBKCNyLuGsosAJheo2vZ+JAA=");
        await using var app = await StartBodyAsync(encoded, "gzip, br", chunked: true);
        foreach (var budget in new[] { 40028, 40027 })
        {
            using var client = Client(app, Options() with { MaxEncodedResponseBytes = budget, MaxDocumentBytes = 40000 });
            using var response = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            if (budget == 40028)
            {
                await response.CopyDocumentToAsync(destination);
                await Assert.That(Encoding.UTF8.GetString(destination.ToArray()))
                    .IsEqualTo(new string('A', 20000) + new string('B', 20000));
                await Assert.That(response.BodyBytesRead).IsEqualTo(40000L);
            }
            else
            {
                await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
                await Assert.That(destination.Length > 0).IsTrue();
                await Assert.That(response.BodyBytesRead).IsEqualTo(destination.Length);
            }
        }
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task LargeExpansionIsExactAndNeverWritesPastTheDecodedLimit(string coding)
    {
        await using var app = await StartBodyAsync(ExpansionFixture(coding), coding);
        foreach (var limit in new[] { 1024 * 1024, 64 * 1024 })
        {
            using var client = Client(app, Options() with { MaxDocumentBytes = limit, MaxEncodedResponseBytes = 2048 });
            using var response = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            if (limit == 1024 * 1024)
            {
                await response.CopyDocumentToAsync(destination);
            }
            else
            {
                await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
            }

            await Assert.That(destination.Length).IsEqualTo((long)limit);
            await Assert.That(response.BodyBytesRead).IsEqualTo((long)limit);
            await Assert.That(destination.ToArray().All(value => value == (byte)'A')).IsTrue();
            await Assert.That(destination.CanWrite).IsTrue();
        }
    }

    [Test]
    public async Task InnerGzipChecksumFailureIsNotHiddenByAValidBrotliLayer()
    {
        var encoded = MetadataFixture("gzip, br");
        // This independent fixture is one uncompressed Brotli block containing the gzip bytes.
        encoded[^9] ^= 1;
        await using var app = await StartBodyAsync(encoded, "gzip, br");
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(26L);
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task DecodingStreamsBeforeTheEncodedBodyCompletes(string coding)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (prefix, suffix) = StreamingFixture(coding);
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/body", async (HttpContext context) =>
            {
                context.Response.Headers.ContentEncoding = coding;
                await context.Response.Body.WriteAsync(prefix, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await release.Task.WaitAsync(context.RequestAborted);
                await context.Response.Body.WriteAsync(suffix, context.RequestAborted);
            }));
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new ObservedDestination();
        var copy = response.CopyDocumentToAsync(destination).AsTask();
        try
        {
            await destination.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(copy.IsCompleted).IsFalse();
            await Assert.That(destination.Length).IsEqualTo(32768L);
        }
        finally
        {
            release.TrySetResult();
        }

        await copy.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(Encoding.UTF8.GetString(destination.ToArray()))
            .IsEqualTo(new string('A', 32768) + new string('B', 32768));
        await Assert.That(response.BodyBytesRead).IsEqualTo(65536L);
    }

    [Test]
    // The original request deadline must reach the stalled body, not expire in a starved header setup.
    [NotInParallel]
    [Arguments("gzip", "send")]
    [Arguments("gzip", "read")]
    [Arguments("gzip", "deadline")]
    [Arguments("gzip", "dispose")]
    [Arguments("deflate", "send")]
    [Arguments("deflate", "read")]
    [Arguments("deflate", "deadline")]
    [Arguments("deflate", "dispose")]
    [Arguments("br", "send")]
    [Arguments("br", "read")]
    [Arguments("br", "deadline")]
    [Arguments("br", "dispose")]
    public async Task CancellationAndOriginalDeadlineReachPendingNetworkReads(string coding, string cause)
    {
        var (prefix, _) = StreamingFixture(coding);
        await using var app = await StartStalledBodyAsync(prefix, coding);
        using var cancellation = new CancellationTokenSource();
        using var client = Client(app, Options() with
        {
            RequestTimeout = cause == "deadline" ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30)
        });
        using var response = await client.SendAsync(HttpMethod.Get, "body",
            cancellationToken: cause == "send" ? cancellation.Token : default);
        using var destination = new ObservedDestination();
        var copy = response.CopyDocumentToAsync(destination, cause == "read" ? cancellation.Token : default).AsTask();
        await destination.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cause == "dispose")
        {
            response.Dispose();
        }
        else if (cause != "deadline")
        {
            cancellation.Cancel();
        }

        await Assert.That(async () => await copy.WaitAsync(TimeSpan.FromSeconds(6))).Throws<OperationCanceledException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(32768L);
        await Assert.That(destination.Length).IsEqualTo(32768L);
        await Assert.That(destination.CanWrite).IsTrue();
    }

    [Test]
    [Arguments("gzip", true)]
    [Arguments("gzip", false)]
    [Arguments("deflate", true)]
    [Arguments("deflate", false)]
    public async Task CancellationInterruptsHeaderAndTrailerNetworkReads(string coding, bool header)
    {
        var encoded = MetadataFixture(coding);
        var prefix = header ? encoded[..1] : encoded[..^(coding == "gzip" ? 8 : 4)];
        await using var app = await StartStalledBodyAsync(prefix, coding);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var cancellation = new CancellationTokenSource();
        using var destination = new ObservedDestination();
        var copy = response.CopyDocumentToAsync(destination, cancellation.Token).AsTask();
        if (!header)
        {
            await destination.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        cancellation.Cancel();
        await Assert.That(async () => await copy.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(header ? 0L : 26L);
        await Assert.That(destination.CanWrite).IsTrue();
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task FailedDestinationWritesAreNotCountedOrRetried(string coding)
    {
        await using var app = await StartBodyAsync(BinaryFixture(coding), coding);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new FailingDestination();
        await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<IOException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        await Assert.That(destination.Length).IsEqualTo(0L);
        await Assert.That(destination.CanWrite).IsTrue();
        await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task DecodedBodiesRemainSingleUseAcrossBothConsumerMethods(string coding)
    {
        await using var app = await StartBodyAsync(MetadataFixture(coding), coding);
        using var client = Client(app);
        using var documentResponse = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();
        await documentResponse.CopyDocumentToAsync(destination);
        await Assert.That(async () =>
        {
            using var metadata = await documentResponse.ReadMetadataAsync();
        }).Throws<InvalidOperationException>();
        using var metadataResponse = await client.SendAsync(HttpMethod.Get, "body");
        using var metadata = await metadataResponse.ReadMetadataAsync();
        await Assert.That(async () => await metadataResponse.CopyDocumentToAsync(destination)).Throws<InvalidOperationException>();
        await Assert.That(Encoding.UTF8.GetString(destination.ToArray())).IsEqualTo(Metadata);
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(Metadata);
        await Assert.That(documentResponse.BodyBytesRead).IsEqualTo(26L);
        await Assert.That(metadataResponse.BodyBytesRead).IsEqualTo(26L);
        await Assert.That(destination.CanWrite).IsTrue();
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task DecodingFailuresNeverFallBackToIdentityJson(string coding)
    {
        await using var app = await StartBodyAsync(Encoding.UTF8.GetBytes(Metadata), coding);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task SharedConnectionPolicyRemainsAnUndecodedRawTransport(string coding)
    {
        var encoded = BinaryFixture(coding);
        string? negotiation = null;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/body", async (HttpContext context) =>
            {
                negotiation = context.Request.Headers.AcceptEncoding.ToString();
                await WriteBodyAsync(context, encoded, coding);
            }));
        var root = new Uri(new Uri(app.Urls.Single()), "/registry/");
        using var client = new RegistryHttpConnectionPolicy(root, allowLoopbackHttp: true).CreateClient();
        using var response = await client.GetAsync(new Uri(root, "body"));
        var actual = await response.Content.ReadAsByteArrayAsync();
        await Assert.That(Convert.ToHexString(actual)).IsEqualTo(Convert.ToHexString(encoded));
        await Assert.That(response.Content.Headers.ContentEncoding.Single()).IsEqualTo(coding);
        await Assert.That(response.Content.Headers.ContentLength).IsEqualTo((long?)encoded.Length);
        await Assert.That(negotiation).IsEqualTo("");
    }

    [Test]
    [Arguments("")]
    [Arguments(",gzip")]
    [Arguments("gzip,")]
    [Arguments("gzip,,br")]
    [Arguments("gzip;q=1")]
    [Arguments("g zip")]
    [Arguments("\"gzip\"")]
    public async Task MalformedContentEncodingListsCannotBeSilentlyIgnored(string coding)
    {
        await using var app = await StartBodyAsync(MetadataFixture("gzip"), coding);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
    }

    [Test]
    [Arguments("zstd")]
    [Arguments("x-gzip")]
    [Arguments("gzip, unsupported")]
    public async Task UnsupportedContentCodingStacksFailWithoutIdentityFallback(string coding)
    {
        await using var app = await StartBodyAsync(Encoding.UTF8.GetBytes(Metadata), coding);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<NotSupportedException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
    }

    [Test]
    [Arguments(1024)]
    [Arguments(1025)]
    public async Task ContentEncodingHeaderCharacterLimitIsExact(int characters)
    {
        var coding = "gzip," + new string(' ', characters - 7) + "br";
        await using var app = await StartBodyAsync(MetadataFixture("gzip, br"), coding);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        if (characters == 1024)
        {
            using var metadata = await response.ReadMetadataAsync();
            await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(Metadata);
            await Assert.That(response.BodyBytesRead).IsEqualTo(26L);
        }
        else
        {
            await Assert.That(async () =>
            {
                using var metadata = await response.ReadMetadataAsync();
            }).Throws<InvalidDataException>();
            await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        }
    }

    [Test]
    [Arguments("identity, identity, identity, identity")]
    [Arguments("gzip, deflate, br, gzip")]
    public async Task CodingDepthIncludesIdentityAndAllowsItsExactBoundary(string coding)
    {
        var encoded = coding.StartsWith("identity", StringComparison.Ordinal) ?
            BinaryFixture("identity") : BinaryFixture(coding);
        await using var app = await StartBodyAsync(encoded, coding);
        foreach (var depth in new[] { 4, 3 })
        {
            using var client = Client(app, Options() with { MaxContentCodingDepth = depth });
            using var response = await client.SendAsync(HttpMethod.Get, "body");
            using var destination = new MemoryStream();
            if (depth == 4)
            {
                await response.CopyDocumentToAsync(destination);
                await Assert.That(Convert.ToHexString(destination.ToArray())).IsEqualTo(BinaryHex);
            }
            else
            {
                await Assert.That(async () => await response.CopyDocumentToAsync(destination)).Throws<InvalidDataException>();
                await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
            }
        }
    }

    [Test]
    public async Task CodingTokensAreCaseInsensitiveAndOrderedAcrossHeaderFields()
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/body", async (HttpContext context) =>
            {
                context.Response.Headers.ContentEncoding = MixedCaseContentEncoding;
                await context.Response.Body.WriteAsync(BinaryFixture("gzip, br"), context.RequestAborted);
            }));
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        using var destination = new MemoryStream();
        await response.CopyDocumentToAsync(destination);
        await Assert.That(Convert.ToHexString(destination.ToArray())).IsEqualTo(BinaryHex);
        await Assert.That(response.ContentHeaders.ContentEncoding.Count).IsEqualTo(4);
    }

    [Test]
    [Arguments("encoded")]
    [Arguments("depth-zero")]
    [Arguments("depth-high")]
    [Arguments("members-zero")]
    [Arguments("members-high")]
    [Arguments("headers-low")]
    [Arguments("headers-high")]
    public async Task InvalidDecodingOptionsFailBeforeDispatch(string option)
    {
        var options = option switch
        {
            "encoded" => Options() with { MaxEncodedResponseBytes = -1 },
            "depth-zero" => Options() with { MaxContentCodingDepth = 0 },
            "depth-high" => Options() with { MaxContentCodingDepth = 17 },
            "members-zero" => Options() with { MaxGzipMembers = 0 },
            "members-high" => Options() with { MaxGzipMembers = 4097 },
            "headers-low" => Options() with { MaxGzipHeaderBytes = 9 },
            "headers-high" => Options() with { MaxGzipHeaderBytes = 1024 * 1024 + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(option))
        };
        await Assert.That(() => new XRegistryHttpClient(new Uri("https://example.invalid/"), options))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task CompressedPagingEnforcesRemainingDecodedBudgetBeforeYielding(string coding)
    {
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", async (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                var second = context.Request.Query.ContainsKey("page");
                if (!second)
                {
                    context.Response.Headers.Link = "<?page=2>;rel=next;count=2";
                }

                await WriteBodyAsync(context, PageFixture(coding, second), coding);
            }));
        foreach (var budget in new[] { 16, 15, 8 })
        {
            requests = 0;
            using var client = Client(app);
            var pages = new List<RegistryCollectionPage>();
            async Task ReadPagesAsync()
            {
                await foreach (var page in client.ReadCollectionPagesAsync(
                    "items", pagination: new() { MaxTotalBytes = budget }))
                {
                    pages.Add(page);
                }
            }

            if (budget == 16)
            {
                await ReadPagesAsync();
                await Assert.That(pages.Count).IsEqualTo(2);
                await Assert.That(pages[1].Records.GetRawText()).IsEqualTo("""{"b":{}}""");
            }
            else
            {
                await Assert.That(ReadPagesAsync).Throws<InvalidDataException>();
                await Assert.That(pages.Count).IsEqualTo(1);
            }

            await Assert.That(requests).IsEqualTo(budget == 8 ? 1 : 2);
            client.Dispose();
            await Assert.That(pages[0].Records.GetRawText()).IsEqualTo("""{"a":{}}""");
        }
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("br")]
    public async Task PagingBudgetStopsDecodingBeforeAnUnfinishedSecondPageCanBeBuffered(string coding)
    {
        var prefix = Convert.FromBase64String(coding switch
        {
            "gzip" => "H4sIAAAAAAACCuzBMREAIAwEMC8voxtS4I6NFZaax0iSzkp13jx3pzIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgPEBAAD//w==",
            "deflate" => "eNrswTERACAMBDAvL6MbUuCOjRWWmsdIks5Kdd48d6cyAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIDxAQAA//8=",
            "br" => "CwdAAIQx4MgT3CZd9JrltEIqb3I39w5AlAE=",
            _ => throw new ArgumentOutOfRangeException(nameof(coding))
        });
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", async (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                if (!context.Request.Query.ContainsKey("page"))
                {
                    context.Response.Headers.Link = "<?page=2>;rel=next;count=2";
                    await WriteBodyAsync(context, PageFixture(coding, second: false), coding);
                    return;
                }

                context.Response.Headers.ContentEncoding = coding;
                await context.Response.Body.WriteAsync(prefix, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await WaitForRequestAbortAsync(context);
            }));
        using var client = Client(app);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pages = new List<RegistryCollectionPage>();
        await Assert.That(async () =>
        {
            await foreach (var page in client.ReadCollectionPagesAsync("items",
                pagination: new() { MaxTotalBytes = 15 }, cancellationToken: cancellation.Token))
            {
                pages.Add(page);
            }
        }).Throws<InvalidDataException>();
        await Assert.That(requests).IsEqualTo(2);
        await Assert.That(pages.Count).IsEqualTo(1);
        await Assert.That(pages[0].Records.GetRawText()).IsEqualTo("""{"a":{}}""");
        await Assert.That(cancellation.IsCancellationRequested).IsFalse();
    }

    [Test]
    [Arguments("list")]
    [Arguments("characters")]
    public async Task ReadingTypedHeadersDoesNotRelaxContentDecodingValidation(string problem)
    {
        var coding = problem == "list" ? "gzip,,br" : "gzip," + new string(' ', 1018) + "br";
        await using var app = await StartBodyAsync(MetadataFixture("gzip, br"), coding);
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, "body");
        _ = response.ContentHeaders.ContentEncoding.ToArray();
        await Assert.That(async () =>
        {
            using var metadata = await response.ReadMetadataAsync();
        }).Throws<InvalidDataException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
    }

    private static XRegistryHttpClientOptions Options() => new()
    {
        AllowLoopbackHttp = true,
        EnableContentDecoding = true
    };

    private static XRegistryHttpClient Client(WebApplication app, XRegistryHttpClientOptions? options = null) =>
        new(new Uri(new Uri(app.Urls.Single()), "/registry/"), options ?? Options());

    private static Task<WebApplication> StartBodyAsync(
        byte[] encoded, string? coding, bool chunked = false, int fragmentSize = 0) =>
        XRegistryHttpClientTests.StartAsync(app => app.MapGet("/registry/body", async (HttpContext context) =>
            await WriteBodyAsync(context, encoded, coding, chunked, fragmentSize)));

    private static async Task WriteBodyAsync(
        HttpContext context, byte[] encoded, string? coding, bool chunked = false, int fragmentSize = 0)
    {
        context.Response.ContentType = "application/octet-stream";
        if (coding is not null)
        {
            context.Response.Headers.ContentEncoding = coding;
        }

        if (!chunked)
        {
            context.Response.ContentLength = encoded.Length;
        }

        if (fragmentSize == 0)
        {
            await context.Response.Body.WriteAsync(encoded, context.RequestAborted);
        }
        else
        {
            for (var offset = 0; offset < encoded.Length; offset += fragmentSize)
            {
                await context.Response.Body.WriteAsync(
                    encoded.AsMemory(offset, Math.Min(fragmentSize, encoded.Length - offset)), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
    }

    private static Task<WebApplication> StartStalledBodyAsync(byte[] prefix, string coding) =>
        XRegistryHttpClientTests.StartAsync(app => app.MapGet("/registry/body", async (HttpContext context) =>
        {
            context.Response.Headers.ContentEncoding = coding;
            await context.Response.Body.WriteAsync(prefix, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await WaitForRequestAbortAsync(context);
        }));

    private static async Task WaitForRequestAbortAsync(HttpContext context)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client deliberately cancels an incomplete network read.
        }
    }

    // Independent fixtures: CPython 3.13.15, zlib 1.3.1, Python Brotli 1.2.0.
    // gzip: compressobj(9, DEFLATED, 31); deflate: zlib.compress(data, 9); br: compress(data, quality=5).
    // Stacks apply these encoders left-to-right. No production decoder or encoder computes expected bytes.
    private static byte[] BinaryFixture(string coding) => coding == "identity" ? Convert.FromHexString(BinaryHex) :
        Convert.FromBase64String(coding switch
        {
            "gzip" => "H4sIAAAAAAACCmP4z8vF2MAAABuixNQHAAAA",
            "deflate" => "eNpj+M/LxdjAAAAHbQGY",
            "br" => "CwOAAP8NCgGAAAM=",
            "gzip, br" => "Cw2AH4sIAAAAAAACCmP4z8vF2MAAABuixNQHAAAAAw==",
            "br, gzip" => "H4sIAAAAAAACCuNmbmD4z8vF2MDADAD04VxhCwAAAA==",
            "gzip, deflate" => "eNqT7+ZgAAEmruQf508fvXGAgUF60ZEr7EAhAGrtCG0=",
            "deflate, gzip, br" => "ixGAH4sIAAAAAAACCqu4lfzj/OmjNw4wMLDnMs4AAGoWXfoPAAAAAw==",
            "gzip, gzip" => "H4sIAAAAAAACCpPv5mAAASau5B/nTx+9cYCBQXrRkSvsQCEAmK3GBxsAAAA=",
            "br, deflate" => "eNrjZm5g+M/LxdjAwAwADiICKQ==",
            "gzip, deflate, br, gzip" => "H4sIAAAAAAACCuvmb6i4Nfn9swQGRrV1T+Sf+8vvLWxodKy6OFH7jYMiQ9ZbjlxmAH/iCJkkAAAA",
            _ => throw new ArgumentOutOfRangeException(nameof(coding))
        });

    private static byte[] MetadataFixture(string coding) => coding == "identity" ? Encoding.UTF8.GetBytes(Metadata) :
        Convert.FromBase64String(coding switch
        {
            "gzip" => "H4sIAAAAAAACCqtWKkvMKU1VslJKSU3OT0lNUdJRylOyMjGqBQAS2wW2GgAAAA==",
            "deflate" => "eNqrVipLzClNVbJSSklNzk9JTVHSUcpTsjIxqgUAdJwIHg==",
            "br" => "GxkAAARyceTPoy6a8dJAunISVQQKyuEwmU4Qi7se",
            "gzip, br" => "ixaAH4sIAAAAAAACCqtWKkvMKU1VslJKSU3OT0lNUdJRylOyMjGqBQAS2wW2GgAAAAM=",
            "br, gzip" => "H4sIAAAAAAACCpOWZGBgKSp8cn6x3qyPlxx2FQmFsnCdemgw00+ge7ccAOmDtBgeAAAA",
            "deflate, gzip, br" => "CxuAH4sIAAAAAAACCqu4tTpMy/uMpm/opiAvT99z/p6+gZcCTwVvMjJcxcpQModDDgDiQzBMIgAAAAM=",
            _ => throw new ArgumentOutOfRangeException(nameof(coding))
        });

    private static byte[] EmptyFixture(string coding) => coding == "identity" ? [] : Convert.FromBase64String(coding switch
    {
        "gzip" => "H4sIAAAAAAACCgMAAAAAAAAAAAA=",
        "deflate" => "eNoDAAAAAAE=",
        "br" => "Ow==",
        _ => throw new ArgumentOutOfRangeException(nameof(coding))
    });

    private static byte[] PageFixture(string coding, bool second) => coding == "identity" ?
        Encoding.UTF8.GetBytes(second ? """{"b":{}}""" : """{"a":{}}""") :
        Convert.FromBase64String((coding, second) switch
        {
            ("gzip", false) => "H4sIAAAAAAACCqtWSlSyqq6tBQDPT4THCAAAAA==",
            ("gzip", true) => "H4sIAAAAAAACCqtWSlKyqq6tBQBhPRBBCAAAAA==",
            ("deflate", false) => "eNqrVkpUsqqurQUAC44C0A==",
            ("deflate", true) => "eNqrVkpSsqqurQUAC5QC0Q==",
            ("br", false) => "iwOAeyJhIjp7fX0D",
            ("br", true) => "iwOAeyJiIjp7fX0D",
            _ => throw new ArgumentOutOfRangeException(nameof(coding))
        });

    private static byte[] ExpansionFixture(string coding) => coding switch
    {
        "gzip" => [.. Convert.FromHexString("1F8B080000000000020AEDC13101000000C2A06CEB5FCA10BE4001"),
            .. new byte[1015], .. Convert.FromHexString("7C06C9BEF68100001000")],
        "deflate" => [.. Convert.FromHexString("78DAEDC13101000000C2A06CEB5FCA10BE4001"),
            .. new byte[1015], .. Convert.FromHexString("7C06B18C3CF1")],
        "br" => Convert.FromBase64String("W///D0AiKB4LJPf+AQ=="),
        _ => throw new ArgumentOutOfRangeException(nameof(coding))
    };

    // zlib level 0 stored blocks. Brotli's literal block was generated independently and checked with Python Brotli.
    private static byte[] StoredFixture(string coding)
    {
        var (prefix, suffix) = coding switch
        {
            "gzip" => ("1F8B080000000000040A00409CBF63", "010000FFFF8206CDFD409C0000"),
            "deflate" => ("780100409CBF63", "010000FFFFBDB9FCAA"),
            "br" => ("8B1FCE", "03"),
            _ => throw new ArgumentOutOfRangeException(nameof(coding))
        };
        return [.. Convert.FromHexString(prefix), .. Encoding.UTF8.GetBytes(new string('A', 20000) + new string('B', 20000)),
            .. Convert.FromHexString(suffix)];
    }

    // Independent compressor flush after 32768 ASCII A bytes, then finish after 32768 ASCII B bytes.
    private static (byte[] Prefix, byte[] Suffix) StreamingFixture(string coding)
    {
        var (prefix, suffix) = coding switch
        {
            "gzip" => ("H4sIAAAAAAACCuzBgQAAAACAILb9pRapCgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAYAAD//w==",
                "7cGBAAAAAIAgt/2hFqkCAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABoEaniEAAABAA=="),
            "deflate" => ("eNrswYEAAAAAgCC2/aUWqQoAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGAAA//8=",
                "7cGBAAAAAIAgt/2hFqkCAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABo+pYPQ"),
            "br" => ("i/8/ACSC4rFAcu8ADA==", "8f8HQEIoHgsg9w4A"),
            _ => throw new ArgumentOutOfRangeException(nameof(coding))
        };
        return (Convert.FromBase64String(prefix), Convert.FromBase64String(suffix));
    }

    private sealed class ObservedDestination : MemoryStream
    {
        internal TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            FirstWrite.TrySetResult();
        }
    }

    private sealed class FailingDestination : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The fixture destination rejected the write."));
    }
}
