using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;

namespace XRegistry.Http.Tests;

public class RegistryPaginationTests
{
    [Test]
    [Arguments("relative")]
    [Arguments("root")]
    [Arguments("absolute")]
    [Arguments("network")]
    public async Task EscapedPathAndCursorSurviveEachValidReferenceForm(string form)
    {
        var targets = new List<string>();
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/{**path}", (HttpContext context) =>
            {
                targets.Add(context.Features.Get<IHttpRequestFeature>()!.RawTarget!);
                if (targets.Count == 1)
                {
                    var reference = form switch
                    {
                        "relative" => "pages/./%41?token=%61",
                        "root" => "/registry/pages/%41?token=%61",
                        "absolute" => $"http://{context.Request.Host}/registry/pages/%41?token=%61",
                        _ => $"//{context.Request.Host}/registry/pages/%41?token=%61"
                    };
                    context.Response.Headers.Link = $"<{reference}>;rel=next;count=3";
                }
                else if (targets.Count == 2)
                {
                    context.Response.Headers.Link = "<../%42?token=%62>;rel=next;count=3";
                }

                return Results.Text(targets.Count switch
                {
                    1 => """{"a":{}}""",
                    2 => """{"b":{}}""",
                    _ => """{"c":{}}"""
                }, "application/json");
            }));
        using var client = Client(app);
        var pages = await CollectAsync(client);

        await Assert.That(pages.Count).IsEqualTo(3);
        await Assert.That(targets[1]).IsEqualTo("/registry/pages/%41?token=%61");
        await Assert.That(targets[2]).IsEqualTo("/registry/%42?token=%62");
        await Assert.That(pages[2].Records.GetProperty("c").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Object);
    }

    [Test]
    public async Task ContinuationsPreserveOpaqueQueryWithoutReapplyingInitialFlags()
    {
        var targets = new List<string>();
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                targets.Add(context.Features.Get<IHttpRequestFeature>()!.RawTarget!);
                if (targets.Count == 1)
                {
                    context.Response.Headers.Link =
                        """<?cursor=a%2fb&same=%41+%2B&empty=&flag>; title="first, \"part\""; rel="next"; count=2, </registry/>;rel=xregistry-root""";
                    return Results.Text("""{"one":{"epoch":184467440737095516160}}""", "application/json");
                }

                return Results.Text("""{"two":{"label":"second"}}""", "application/json");
            }));
        using var client = Client(app);
        var pages = new List<RegistryCollectionPage>();
        await foreach (var page in client.ReadCollectionPagesAsync(
            "items", [new("limit", "1"), new("filter", "a=1"), new("filter", "b=2")]))
        {
            pages.Add(page);
        }

        await Assert.That(targets.Count).IsEqualTo(2);
        await Assert.That(targets[0]).IsEqualTo("/registry/items?limit=1&filter=a%3D1&filter=b%3D2");
        await Assert.That(targets[1]).IsEqualTo("/registry/items?cursor=a%2fb&same=%41+%2B&empty=&flag");
        await Assert.That(pages[0].Records.GetProperty("one").GetProperty("epoch").GetRawText())
            .IsEqualTo("184467440737095516160");
        await Assert.That(pages[1].Records.GetProperty("two").GetProperty("label").GetString()).IsEqualTo("second");
        await Assert.That(pages[0].Links[0].Parameters["title"]).IsEqualTo("first, \"part\"");
        await Assert.That(pages[1].TotalCount).IsEqualTo((ulong?)2);
        client.Dispose();
        await Assert.That(pages[0].Records.GetProperty("one").GetProperty("epoch").GetRawText())
            .IsEqualTo("184467440737095516160");
    }

    [Test]
    [Arguments("http://other.invalid/registry/items")]
    [Arguments("//other.invalid/registry/items")]
    [Arguments("/outside")]
    [Arguments("../../outside")]
    [Arguments("/registry-other/items")]
    [Arguments("/registry/%2Foutside")]
    [Arguments("/registry/%5coutside")]
    [Arguments("/registry/%00")]
    [Arguments("?cursor=%ZZ")]
    [Arguments("?cursor=ok#fragment")]
    [Arguments("https://name@other.invalid/registry/items")]
    public async Task UnsafeContinuationFailsBeforeCredentialsOrAnotherRequest(string target)
    {
        var requests = 0;
        var credentials = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                requests++;
                context.Response.Headers.Link = $"<{target}>;rel=next";
                return Results.Text("""{"one":{}}""", "application/json");
            }));
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            AuthorizationProvider = (_, _) =>
            {
                credentials++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });

        await Assert.That(async () => await CollectAsync(client)).Throws<InvalidDataException>();
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(credentials).IsEqualTo(1);
    }

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("18446744073709551616")]
    [Arguments("+1")]
    [Arguments("")]
    public async Task InvalidInitialLimitFailsWithoutNetwork(string value)
    {
        using var client = new XRegistryHttpClient(new Uri("https://example.invalid/"));
        await Assert.That(async () =>
        {
            await foreach (var page in client.ReadCollectionPagesAsync("items", [new("limit", value)]))
            {
                _ = page;
            }
        }).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("1")]
    [Arguments("18446744073709551615")]
    public async Task InitialLimitAcceptsBothUInt64EndpointsWithoutChangingTheWireValue(string value)
    {
        var targets = new List<string>();
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                targets.Add(context.Features.Get<IHttpRequestFeature>()!.RawTarget!);
                return Results.Text("""{"one":{}}""", "application/json");
            }));
        using var client = Client(app);
        var pages = new List<RegistryCollectionPage>();
        await foreach (var page in client.ReadCollectionPagesAsync("items", [new("limit", value)]))
        {
            pages.Add(page);
        }

        await Assert.That(targets.Single()).IsEqualTo("/registry/items?limit=" + value);
        await Assert.That(pages.Count).IsEqualTo(1);
        await Assert.That(pages[0].Records.EnumerateObject().Single().Name).IsEqualTo("one");
    }

    [Test]
    public async Task EmptyCollectionIsOneOwnedPageWithoutInventedCount()
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", static () => Results.Text("{}", "application/json")));
        using var client = Client(app);
        var pages = await CollectAsync(client, new() { MaxPages = 1, MaxRecords = 0, MaxTotalBytes = 2 });

        await Assert.That(pages.Count).IsEqualTo(1);
        await Assert.That(pages[0].Records.EnumerateObject().Count()).IsEqualTo(0);
        await Assert.That(pages[0].TotalCount).IsNull();
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(2, true)]
    public async Task PageBudgetIncludesEmptyPagesAndStopsBeforeExcessDispatch(int budget, bool succeeds)
    {
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                requests++;
                if (requests == 1)
                {
                    context.Response.Headers.Link = "<?cursor=two>;rel=next;count=0";
                }

                return Results.Text("{}", "application/json");
            }));
        using var client = Client(app);
        if (succeeds)
        {
            var pages = await CollectAsync(client, new() { MaxPages = budget });
            await Assert.That(pages.Count).IsEqualTo(2);
        }
        else
        {
            await Assert.That(async () => await CollectAsync(client, new() { MaxPages = budget }))
                .Throws<InvalidDataException>();
        }

        await Assert.That(requests).IsEqualTo(budget);
    }

    [Test]
    [Arguments(16L, 2UL, true)]
    [Arguments(15L, 2UL, false)]
    [Arguments(16L, 1UL, false)]
    public async Task CumulativeByteAndRecordLimitsAreExact(long bytes, ulong records, bool succeeds)
    {
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                requests++;
                if (requests == 1)
                {
                    context.Response.Headers.Link = "<?page=2>;rel=next;count=2";
                }

                return Results.Text(requests == 1 ? """{"a":{}}""" : """{"b":{}}""", "application/json");
            }));
        using var client = Client(app);
        var options = new RegistryPaginationOptions { MaxTotalBytes = bytes, MaxRecords = records };
        if (succeeds)
        {
            var pages = await CollectAsync(client, options);
            await Assert.That(pages.Count).IsEqualTo(2);
            await Assert.That(pages[1].TotalCount).IsEqualTo((ulong?)2);
        }
        else
        {
            await Assert.That(async () => await CollectAsync(client, options)).Throws<InvalidDataException>();
        }
    }

    [Test]
    [Arguments("cycle")]
    [Arguments("ambiguous")]
    [Arguments("duplicates")]
    [Arguments("changing-count")]
    [Arguments("under-count")]
    [Arguments("over-count")]
    [Arguments("bad-count")]
    [Arguments("page-limit")]
    public async Task InconsistentOrNonterminatingSetsFailExplicitly(string failure)
    {
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                requests++;
                context.Response.Headers.Link = requests == 1
                    ? failure switch
                    {
                        "cycle" => "<?limit=1>;rel=next",
                        "ambiguous" => "<?page=2>;rel=next, <?page=3>;rel=next",
                        "under-count" => "<?page=2>;rel=next;count=1",
                        "over-count" => "<?page=2>;rel=next;count=3",
                        "bad-count" => "<?page=2>;rel=next;count=18446744073709551616",
                        _ => "<?page=2>;rel=next;count=2"
                    }
                    : failure == "changing-count" ? "<?page=1>;rel=prev;count=3" : "";
                return Results.Text(failure == "page-limit" ? """{"a":{},"b":{}}""" :
                    requests == 1 || failure == "duplicates" ? """{"a":{}}""" : """{"b":{}}""", "application/json");
            }));
        using var client = Client(app);
        await Assert.That(async () =>
        {
            await foreach (var page in client.ReadCollectionPagesAsync("items", [new("limit", "1")]))
            {
                _ = page;
            }
        }).Throws<InvalidDataException>();
        await Assert.That(requests).IsLessThanOrEqualTo(2);
    }

    [Test]
    [Arguments(401)]
    [Arguments(410)]
    [Arguments(500)]
    [Arguments(302)]
    public async Task PageErrorsAndRedirectsRemainErrorsWithoutRetries(int status)
    {
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                requests++;
                context.Response.Headers.Location = "/registry/items";
                return Results.StatusCode(status);
            }));
        using var client = Client(app);
        HttpStatusCode? observed = null;
        try
        {
            await CollectAsync(client);
        }
        catch (HttpRequestException exception)
        {
            observed = exception.StatusCode;
        }

        await Assert.That(observed).IsEqualTo((HttpStatusCode?)status);
        await Assert.That(requests).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationBetweenPagesPreventsAnotherDispatch()
    {
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                requests++;
                context.Response.Headers.Link = "<?page=2>;rel=next";
                return Results.Text("{}", "application/json");
            }));
        using var client = Client(app);
        using var cancellation = new CancellationTokenSource();
        await using var iterator = client.ReadCollectionPagesAsync("items", cancellationToken: cancellation.Token)
            .GetAsyncEnumerator();
        await Assert.That(await iterator.MoveNextAsync()).IsTrue();
        cancellation.Cancel();

        await Assert.That(async () => await iterator.MoveNextAsync()).Throws<OperationCanceledException>();
        await Assert.That(requests).IsEqualTo(1);
    }

    [Test]
    public async Task AnchoredPaginationFailsWithoutAssumingItRefersToThisCollection()
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", (HttpContext context) =>
            {
                context.Response.Headers.Link = "<?page=2>;rel=next;anchor=\"/another-context\"";
                return Results.Text("{}", "application/json");
            }));
        using var client = Client(app);
        await Assert.That(async () => await CollectAsync(client)).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""{"a":{},"a":{}}""")]
    public async Task NoncollectionOrDuplicateMetadataIsRejected(string body)
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.MapGet("/registry/items", () => Results.Text(body, "application/json")));
        using var client = Client(app);
        if (body == "[]")
        {
            await Assert.That(async () => await CollectAsync(client)).Throws<InvalidDataException>();
        }
        else
        {
            await Assert.That(async () => await CollectAsync(client)).Throws<RegistryException>();
        }
    }

    private static XRegistryHttpClient Client(WebApplication app, XRegistryHttpClientOptions? options = null) =>
        new(new Uri(new Uri(app.Urls.Single()), "/registry/"),
            options ?? new XRegistryHttpClientOptions { AllowLoopbackHttp = true });

    private static async Task<List<RegistryCollectionPage>> CollectAsync(
        XRegistryHttpClient client, RegistryPaginationOptions? options = null)
    {
        var result = new List<RegistryCollectionPage>();
        await foreach (var page in client.ReadCollectionPagesAsync("items", pagination: options))
        {
            result.Add(page);
        }

        return result;
    }
}
