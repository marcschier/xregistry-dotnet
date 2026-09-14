using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;
using XRegistry.Federation;
using XRegistry.Queries;
using XRegistry.Samples.Bridge;

namespace XRegistry.Bridge.Tests;

public class BridgePagingTests
{
    [Test]
    public async Task PagesFreezeSelectedBytesAndDoNotReopenSources()
    {
        var source = new ControlledSource("one", "v1", ["a", "b", "c"]);
        var options = BridgeFixture.Options with
        {
            AuthorizeRetainedRead = static (_, _, _, _) => ValueTask.FromResult(true)
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()], options);
        using var client = BridgeFixture.Client(app);
        using var first = await client.GetAsync("/registry/dirs/g/files?limit=1&sort=fileid%3Ddesc&doc&inline=versions");
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await BridgeFixture.Json(first);
        await Assert.That(string.Join(",", metadata.EnumerateObject().Select(property => property.Name))).IsEqualTo("c");
        await Assert.That(metadata.GetProperty("c").GetProperty("self").GetString()).IsEqualTo("#/c");
        await Assert.That(metadata.GetProperty("c").GetProperty("versions").GetProperty("v1").GetProperty("self").GetString())
            .IsEqualTo("#/c/versions/v1");
        var next = Next(first);
        var reads = source.Reads.Count;
        source.Override = static (_, _) => throw new InvalidOperationException("Retained pages must not reopen source data.");
        using var second = await client.GetAsync(next.PathAndQuery);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", (await BridgeFixture.Json(second)).EnumerateObject().Select(property => property.Name))).IsEqualTo("b");
        using var third = await client.GetAsync(Next(second).PathAndQuery);
        await Assert.That(third.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", (await BridgeFixture.Json(third)).EnumerateObject().Select(property => property.Name))).IsEqualTo("a");
        await Assert.That(RegistryHttpLink.Parse(third.Headers.GetValues("Link")).Any(link => link.HasRelation("next"))).IsFalse();
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
        await Assert.That(source.Opens).IsEqualTo(1);
        await Assert.That(source.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task CursorRejectsDifferentCallerPathAndAddedRepresentationQuery()
    {
        var user = "first";
        var source = new ControlledSource("one", "v1", ["a", "b"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], PagingOptions,
            configure: app => app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));
                return next(context);
            }));
        using var client = BridgeFixture.Client(app);
        using var first = await client.GetAsync("/registry/dirs/g/files?limit=1");
        var next = Next(first);
        user = "second";
        using var otherCaller = await client.GetAsync(next.PathAndQuery);
        await Assert.That(otherCaller.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await BridgeFixture.Json(otherCaller)).GetProperty("code").GetString()).IsEqualTo("bad_cursor");
        user = "first";
        using var changedView = await client.GetAsync(next.PathAndQuery + "&doc");
        await Assert.That(changedView.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var wrongPath = await client.GetAsync("/registry/dirs/g/notes" + next.Query);
        await Assert.That(wrongPath.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(source.Opens).IsEqualTo(1);
    }

    [Test]
    public async Task RetainedReadAuthorizationAndCredentialContextAreRecheckedWithoutOpeningSources()
    {
        var allowed = true;
        var stamp = "context-one";
        var source = new ControlledSource("one", "v1", ["a", "b"]);
        var registration = new FederationSourceRegistration("one", FederationRepresentation.ApiView, (_, _) =>
        {
            source.Opens++;
            return ValueTask.FromResult(new FederationSourceLease(source, () =>
            {
                source.Closes++;
                return ValueTask.CompletedTask;
            }, stamp));
        }, _ => ValueTask.FromResult(stamp));
        var options = PagingOptions with { AuthorizeRetainedRead = (_, _, _, _) => ValueTask.FromResult(allowed) };
        await using var app = await BridgeFixture.StartAsync([registration], options);
        using var client = BridgeFixture.Client(app);
        using var first = await client.GetAsync("/registry/dirs/g/files?limit=1");
        var next = Next(first);
        allowed = false;
        using var revoked = await client.GetAsync(next.PathAndQuery);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        allowed = true;
        stamp = "context-two";
        using var changed = await client.GetAsync(next.PathAndQuery);
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await BridgeFixture.Json(changed)).GetProperty("code").GetString()).IsEqualTo("cursor_context_changed");
        await Assert.That(source.Opens).IsEqualTo(1);
        await Assert.That(source.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task CursorCapacityAndExpiryNeverEvictLiveCapturesOrResetTheirLifetime()
    {
        var clock = new PageClock();
        var source = new ControlledSource("one", "v1", ["a", "b"]);
        var options = PagingOptions with
        {
            TimeProvider = clock,
            PagingLimits = new BridgePagingLimits { MaxCaptures = 1, Lifetime = TimeSpan.FromSeconds(10) }
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()], options);
        using var client = BridgeFixture.Client(app);
        using var first = await client.GetAsync("/registry/dirs/g/files?limit=1");
        var next = Next(first);
        using var full = await client.GetAsync("/registry/dirs/g/files?limit=1");
        await Assert.That(full.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var retained = await client.GetAsync(next.PathAndQuery);
        await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromSeconds(10));
        using var expired = await client.GetAsync(next.PathAndQuery);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await Assert.That((await BridgeFixture.Json(expired)).GetProperty("code").GetString()).IsEqualTo("cursor_expired");
        using var renewed = await client.GetAsync("/registry/dirs/g/files?limit=1");
        await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task EmptyAndExactBoundaryPagesHaveCompleteCountsAndRequireExplicitReplayPolicy()
    {
        var source = new ControlledSource("one", "v1", ["a", "b"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], PagingOptions);
        using var client = BridgeFixture.Client(app);
        using var exact = await client.GetAsync("/registry/dirs/g/files?limit=2");
        await Assert.That(exact.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(exact)).EnumerateObject().Count()).IsEqualTo(2);
        var links = RegistryHttpLink.Parse(exact.Headers.GetValues("Link"));
        await Assert.That(links.Any(link => link.HasRelation("next"))).IsFalse();
        await Assert.That(links.Single(link => link.HasRelation("last")).Parameters["count"]).IsEqualTo("2");
        using var empty = await client.GetAsync("/registry/dirs/g/files?filter=excludeall&limit=1");
        await Assert.That((await BridgeFixture.Json(empty)).EnumerateObject().Count()).IsEqualTo(0);
        await Assert.That(RegistryHttpLink.Parse(empty.Headers.GetValues("Link")).Single(link => link.HasRelation("last")).Parameters["count"]).IsEqualTo("0");
        var noPolicy = new ControlledSource("none", "v1", ["a"]);
        await using var unsupported = await BridgeFixture.StartAsync([noPolicy.Registration()]);
        using var otherClient = BridgeFixture.Client(unsupported);
        using var denied = await otherClient.GetAsync("/registry/dirs/g/files?limit=1");
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotImplemented);
        await Assert.That(noPolicy.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task PageCaptureByteQuotaFailsBeforeReturningAnyPageToken()
    {
        var source = new ControlledSource("one", "v1", ["a", "b"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()], PagingOptions with
        {
            PagingLimits = new BridgePagingLimits { MaxCaptureBytes = 1024 }
        });
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files?limit=1");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(RegistryHttpLink.Parse(response.Headers.GetValues("Link")).Any(link => link.HasRelation("next"))).IsFalse();
    }

    [Test]
    public async Task CaptureExpiringDuringAuthorizationIsNotPublished()
    {
        var clock = new PageClock();
        var source = new ControlledSource("one", "v1", ["a", "b"]);
        var advanced = false;
        await using var app = await BridgeFixture.StartAsync([source.Registration()], PagingOptions with
        {
            TimeProvider = clock,
            QueryLimits = new RegistryQueryEvaluationLimits { MaxDuration = TimeSpan.FromMinutes(1) },
            PagingLimits = new BridgePagingLimits { Lifetime = TimeSpan.FromSeconds(10) },
            AuthorizeRetainedRead = (_, _, _, _) =>
            {
                if (!advanced) { clock.Advance(TimeSpan.FromSeconds(10)); advanced = true; }
                return ValueTask.FromResult(true);
            }
        });
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files?limit=1");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("cursor_expired");
        await Assert.That(RegistryHttpLink.Parse(response.Headers.GetValues("Link")).Any(link => link.HasRelation("next"))).IsFalse();
    }

    private static BridgeHostOptions PagingOptions => BridgeFixture.Options with
    {
        AuthorizeRetainedRead = static (_, _, _, _) => ValueTask.FromResult(true)
    };

    internal static Uri Next(HttpResponseMessage response) =>
        new(RegistryHttpLink.Parse(response.Headers.GetValues("Link")).Single(link => link.HasRelation("next")).Reference);

    private sealed class PageClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(_ticks);
        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
