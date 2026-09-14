using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.RegistryQueryTests;

namespace XRegistry.Server.Tests;

public class RegistryPaginationQueryTests
{
    [Test]
    public async Task PaginationFreezesSortedRecordsAndCountsAcrossSubsequentWrites()
    {
        var engine = await Seed();
        var first = await Send(engine, RegistryAction.Read, "/fleets", null, new("limit", "2"), new("sort", "rank=desc"));
        await Assert.That(string.Join(',', Ids(first.Metadata!.RootElement))).IsEqualTo("c,b");
        await Assert.That(first.Page!.TotalCount).IsEqualTo(4UL);
        var next = first.Page.Links.Single(static link => link.Relation == "next").Target;
        await Assert.That(next.AbsoluteUri.StartsWith("https://registry.example/catalog/fleets?cursor=", StringComparison.Ordinal)).IsTrue();
        await Send(engine, RegistryAction.Patch, "/fleets/a", """{"rank":999,"name":"changed"}""");
        await Send(engine, RegistryAction.Delete, "/fleets/c");
        await Send(engine, RegistryAction.Replace, "/fleets/e", """{"rank":0}""");
        var second = await Follow(engine, next);
        await Assert.That(string.Join(',', Ids(second.Metadata!.RootElement))).IsEqualTo("a,d");
        await Assert.That(second.Metadata.RootElement.GetProperty("a").GetProperty("rank").GetInt32()).IsEqualTo(2);
        await Assert.That(second.Metadata.RootElement.GetProperty("a").GetProperty("name").GetString()).IsEqualTo("Alpha");
        await Assert.That(second.Metadata.RootElement.GetProperty("a").GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That((await Send(engine, RegistryAction.Read, "/fleets/a")).Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(1);
        await Assert.That(second.Page!.TotalCount).IsEqualTo(4UL);
        await Assert.That(second.Page.ExpiresAt).IsEqualTo(first.Page.ExpiresAt);
        await Assert.That(second.Page.Links.Any(static link => link.Relation == "next")).IsFalse();
    }

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("+1")]
    [Arguments("1.0")]
    [Arguments("1e1")]
    [Arguments("18446744073709551616")]
    [Arguments("")]
    public async Task InitialPageLimitRequiresAPositiveUInt64(string limit)
    {
        var engine = await Seed();
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", limit)), "bad_flag");
    }

    [Test]
    public async Task UInt64MaximumIsAcceptedWithoutNarrowingAndEmptySetsHaveExactCounts()
    {
        var engine = await Seed();
        var all = await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "18446744073709551615"));
        await Assert.That(Ids(all.Metadata!.RootElement).Length).IsEqualTo(4);
        await Assert.That(all.Page!.TotalCount).IsEqualTo(4UL);
        await Assert.That(all.Page.Links.Count).IsEqualTo(0);
        var empty = await Send(engine, RegistryAction.Read, "/fleets", null, new("limit", "1"), new("filter", "excludeall"));
        await Assert.That(empty.Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(0);
        await Assert.That(empty.Page!.TotalCount).IsEqualTo(0UL);
        await Assert.That(empty.Page.Links.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OnlyTheCaseSensitiveLimitNameControlsPageSize()
    {
        var engine = await Seed();
        var extension = await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("Limit", "1"));
        await Assert.That(Ids(extension.Metadata!.RootElement).Length).IsEqualTo(4);
        var limited = await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "1"));
        await Assert.That(Ids(limited.Metadata!.RootElement).Length).IsEqualTo(1);
        await Assert.That(limited.Page!.TotalCount).IsEqualTo(4UL);
    }

    [Test]
    public async Task ContinuationsBindCallerPathAndUnmodifiedQueryWithoutExposingClaims()
    {
        var engine = await Seed();
        var caller = new RegistryOperationContext(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "private-user"), new Claim("credential", "never-in-a-url")], "trusted")));
        var first = await engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/fleets"))
        {
            Parameters = [new("limit", "1"), new("filter", "rank")]
        }, caller);
        var next = first.Page!.Links.Single(static link => link.Relation == "next").Target;
        await Assert.That(next.AbsoluteUri.Contains("private-user", StringComparison.Ordinal)).IsFalse();
        await Assert.That(next.AbsoluteUri.Contains("never-in-a-url", StringComparison.Ordinal)).IsFalse();
        await Assert.That(next.Query.Count(static character => character == '&')).IsEqualTo(0);
        await ExpectCode(() => Follow(engine, next), "forbidden");
        await ExpectCode(() => Follow(engine, new Uri(next.AbsoluteUri + "&limit=1"), caller), "bad_cursor");
        await ExpectCode(() => Follow(engine, new Uri(next.AbsoluteUri.Replace("/fleets?", "/fleets/a/items?", StringComparison.Ordinal)), caller), "bad_cursor");
        var permitted = await Follow(engine, next, caller);
        await Assert.That(permitted.Page!.TotalCount).IsEqualTo(3UL);
        await Assert.That(Ids(permitted.Metadata!.RootElement)).IsEquivalentTo(["b"], StringComparer.Ordinal);
    }

    [Test]
    public async Task ForgedAndForeignEngineCursorsFailExplicitly()
    {
        var engine = await Seed();
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("cursor", new string('A', 43))), "bad_cursor");
        var first = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "1"));
        var foreign = await Seed();
        await ExpectCode(() => Follow(foreign, first.Page!.Links.Single(static link => link.Relation == "next").Target), "bad_cursor");
    }

    [Test]
    public async Task CursorExpiryDoesNotSlideAndCapacityIsReclaimedWithoutAWorker()
    {
        var clock = new QueryClock();
        var engine = await Configured(new() { MaxCursors = 1, CursorLifetime = TimeSpan.FromMinutes(1) }, clock: clock);
        var first = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "1"));
        var next = first.Page!.Links.Single(static link => link.Relation == "next").Target;
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = await Follow(engine, next);
        await Assert.That(second.Page!.ExpiresAt).IsEqualTo(first.Page.ExpiresAt);
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "1")), "server_busy");
        clock.Advance(TimeSpan.FromSeconds(30));
        await ExpectCode(() => Follow(engine, next), "cursor_expired");
        var renewed = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "1"));
        await Assert.That(renewed.Page!.TotalCount).IsEqualTo(4UL);
    }

    [Test]
    public async Task RevokedAccessInvalidatesFrozenPagesInsteadOfLeakingOrChangingTheirCounts()
    {
        var policy = new QueryPolicy();
        var engine = await Configured(new(), policy: policy);
        var first = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "1"));
        policy.Revoked.Add("/fleets/b");
        await ExpectCode(() => Follow(engine, first.Page!.Links.Single(static link => link.Relation == "next").Target), "forbidden");
        policy.Revoked.Clear();
        await ExpectCode(() => Follow(engine, first.Page!.Links.Single(static link => link.Relation == "next").Target), "bad_cursor");
    }

    [Test]
    public async Task FilteringSortingAndAuthorizationPrecedeTheFrozenCount()
    {
        var policy = new QueryPolicy();
        policy.Revoked.Add("/fleets/b");
        var engine = await Configured(new(), policy: policy);
        var first = await Send(engine, RegistryAction.Read, "/fleets", null,
            new("filter", "rank>=2"), new("sort", "rank=desc"), new("limit", "1"));
        await Assert.That(Ids(first.Metadata!.RootElement)).IsEquivalentTo(["c"], StringComparer.Ordinal);
        await Assert.That(first.Page!.TotalCount).IsEqualTo(2UL);
        var next = await Follow(engine, first.Page.Links.Single(static link => link.Relation == "next").Target);
        await Assert.That(Ids(next.Metadata!.RootElement)).IsEquivalentTo(["a"], StringComparer.Ordinal);
        await Assert.That(next.Page!.TotalCount).IsEqualTo(2UL);
    }

    [Test]
    public async Task FirstPreviousAndLastLinksStayInOneFrozenSetAndNextNeverCycles()
    {
        var engine = await Seed();
        var first = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "1"));
        await Assert.That(first.Page!.Links.Any(static link => link.Relation == "prev")).IsFalse();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>(Ids(first.Metadata!.RootElement));
        var page = first;
        while (page.Page!.Links.SingleOrDefault(static link => link.Relation == "next") is { } next)
        {
            await Assert.That(visited.Add(next.Target.AbsoluteUri)).IsTrue();
            page = await Follow(engine, next.Target);
            ids.AddRange(Ids(page.Metadata!.RootElement));
            await Assert.That(page.Page!.TotalCount).IsEqualTo(4UL);
        }

        await Assert.That(string.Join(',', ids)).IsEqualTo("a,b,c,d");
        var previous = await Follow(engine, page.Page.Links.Single(static link => link.Relation == "prev").Target);
        await Assert.That(Ids(previous.Metadata!.RootElement)).IsEquivalentTo(["c"], StringComparer.Ordinal);
        var again = await Follow(engine, page.Page.Links.Single(static link => link.Relation == "first").Target);
        await Assert.That(Ids(again.Metadata!.RootElement)).IsEquivalentTo(["a"], StringComparer.Ordinal);
        var last = await Follow(engine, first.Page.Links.Single(static link => link.Relation == "last").Target);
        await Assert.That(Ids(last.Metadata!.RootElement)).IsEquivalentTo(["d"], StringComparer.Ordinal);
    }

    [Test]
    public async Task ProjectedDocumentViewAndNestedMetadataStayFrozenWithoutBackendLeases()
    {
        var persistence = new SnapshotCounter();
        var engine = await Configured(new(), persistence: persistence);
        await Send(engine, RegistryAction.Replace, "/fleets/a/items/x", """{"name":"before"}""");
        var first = await Send(engine, RegistryAction.Read, "/fleets", null,
            new("limit", "1"), new("inline", "*"), new("doc", null));
        await Assert.That(first.Metadata!.RootElement.GetProperty("a").GetProperty("self").GetString()).IsEqualTo("#/a");
        await Assert.That(persistence.OpenSnapshots).IsEqualTo(0);
        var firstLink = first.Page!.Links.Single(static link => link.Relation == "first").Target;
        await Send(engine, RegistryAction.Patch, "/fleets/a/items/x", """{"name":"after"}""");
        var reads = persistence.Reads;
        var retained = await Follow(engine, firstLink);
        await Assert.That(persistence.Reads).IsEqualTo(reads);
        await Assert.That(retained.Metadata!.RootElement.GetProperty("a").GetProperty("items").GetProperty("x")
            .GetProperty("versions").GetProperty("1").GetProperty("name").GetString()).IsEqualTo("before");
    }

    [Test]
    public async Task CursorRecordAndTokenLimitsRejectRatherThanTruncate()
    {
        var records = await Configured(new() { MaxCursorRecords = 3 });
        await ExpectCode(() => Send(records, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "1")), "too_large");
        var tokens = await Configured(new() { MaxCursorTokens = 3 });
        await ExpectCode(() => Send(tokens, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "1")), "too_large");
    }

    [Test]
    public async Task FailedResponsePreparationDoesNotConsumeCursorCapacity()
    {
        var engine = await Configured(new() { MaxCursors = 1 });
        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/fleets"))
        {
            Parameters = [new("limit", "1")]
        }, new RegistryOperationContext(Writer().Caller)
        {
            PrepareResponseAsync = static (_, _) => throw new RegistryException(new("header_error", "", "Rejected."))
        }), "header_error");
        var valid = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "1"));
        await Assert.That(valid.Page!.TotalCount).IsEqualTo(4UL);
    }

    [Test]
    public async Task CanceledPagePreparationDoesNotRetainCursorState()
    {
        var engine = await Configured(new() { MaxCursors = 1 });
        using var canceled = new CancellationTokenSource();
        await Assert.That(async () => await engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/fleets"))
        {
            Parameters = [new("limit", "1")]
        }, new RegistryOperationContext(Writer().Caller)
        {
            PrepareResponseAsync = async (_, _) => await canceled.CancelAsync()
        }, canceled.Token)).Throws<OperationCanceledException>();
        var first = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "1"));
        await Assert.That(first.Page!.TotalCount).IsEqualTo(4UL);
    }

    [Test]
    public async Task QueryDeadlineAlsoBoundsTheResponsePreparationHook()
    {
        var duration = TimeSpan.FromMilliseconds(100);
        var clock = new ManualQueryClock();
        var engine = await Configured(new() { MaxDuration = duration, MaxCursors = 1 }, clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/fleets"))
        {
            Parameters = [new("limit", "1")]
        }, new RegistryOperationContext(Writer().Caller)
        {
            PrepareResponseAsync = async (_, ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    if (ct.IsCancellationRequested)
                    {
                        canceled.TrySetResult();
                    }
                }
            }
        }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(duration);
        await ExpectCode(async () => await operation, "server_busy");
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That((await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "1"))).Page!.TotalCount).IsEqualTo(4UL);
    }

    [Test]
    public async Task PerCursorAndFactBytesFailExplicitlyBeforeAnyRetention()
    {
        var cursor = await Configured(new() { MaxCursorBytes = 1 });
        await ExpectCode(() => Send(cursor, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "1")), "too_large");
        var facts = await Configured(new() { MaxFactBytes = 1 });
        await ExpectCode(() => Send(facts, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("filter", "name=Alpha")), "too_large");
    }

    [Test]
    public async Task EscapedIdentifiersAndMetadataModelChangesDoNotRebindRetainedPages()
    {
        var engine = await Seed();
        await Send(engine, RegistryAction.Post, "/fleets/g%40one/items", """{"a":{"rank":1},"b":{"rank":2}}""");
        var first = await Send(engine, RegistryAction.Read, "/fleets/g@one/items", null,
            new KeyValuePair<string, string?>("limit", "1"));
        var next = first.Page!.Links.Single(static link => link.Relation == "next").Target;
        await Assert.That(next.AbsoluteUri.StartsWith("https://registry.example/catalog/fleets/g%40one/items?cursor=", StringComparison.Ordinal)).IsTrue();
        await Send(engine, RegistryAction.Replace, "/modelsource", QueryModel);
        var retained = await Follow(engine, next);
        await Assert.That(Ids(retained.Metadata!.RootElement)).IsEquivalentTo(["b"], StringComparer.Ordinal);
        await Assert.That(retained.Page!.TotalCount).IsEqualTo(2UL);
    }

    [Test]
    public async Task PageByteBudgetCanReduceAnAcceptedClientLimitWithoutDroppingRecords()
    {
        var engine = await Configured(new() { MaxPageBytes = 5000 });
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            await Send(engine, RegistryAction.Patch, "/fleets/" + id, "{\"name\":\"" + new string('x', 3000) + "\"}");
        }

        var page = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "100"));
        var records = new List<string>();
        while (true)
        {
            await Assert.That(System.Text.Encoding.UTF8.GetByteCount(page.Metadata!.RootElement.GetRawText())).IsLessThanOrEqualTo(5000);
            await Assert.That(Ids(page.Metadata.RootElement).Length).IsEqualTo(1);
            records.AddRange(Ids(page.Metadata.RootElement));
            var next = page.Page!.Links.SingleOrDefault(static link => link.Relation == "next");
            if (next is null)
            {
                break;
            }

            page = await Follow(engine, next.Target);
        }

        await Assert.That(string.Join(',', records)).IsEqualTo("a,b,c,d");
    }

    [Test]
    public async Task GlobalCursorRecordsTokensAndBytesHaveIndependentAdmissionBounds()
    {
        foreach (var limits in new[]
        {
            new RegistryQueryLimits { MaxTotalCursorRecords = 5 },
            new RegistryQueryLimits { MaxCursorTokens = 3 }
        })
        {
            var engine = await Configured(limits);
            var first = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("limit", "2"));
            await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets", null,
                new KeyValuePair<string, string?>("limit", "2")), "server_busy");
            await Assert.That((await Follow(engine, first.Page!.Links.Single(static link => link.Relation == "next").Target)).Page!.TotalCount).IsEqualTo(4UL);
        }

        var bytes = await Configured(new() { MaxTotalCursorBytes = 1 });
        await ExpectCode(() => Send(bytes, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "2")), "server_busy");
        await Assert.That((await Send(bytes, RegistryAction.Read, "/fleets")).Page!.TotalCount).IsEqualTo(4UL);
    }

    [Test]
    public async Task RevokingAnInlinedDescendantInvalidatesItsRetainedParentPage()
    {
        var policy = new QueryPolicy();
        var engine = await Configured(new(), policy: policy);
        await Send(engine, RegistryAction.Replace, "/fleets/a/items/x", """{"name":"private-child"}""");
        var first = await Send(engine, RegistryAction.Read, "/fleets", null, new("limit", "1"), new("inline", "*"));
        policy.Revoked.Add("/fleets/a/items/x/versions/1");
        await ExpectCode(() => Follow(engine, first.Page!.Links.Single(static link => link.Relation == "first").Target), "forbidden");
    }

    [Test]
    public async Task QueryAuthorizationDeadlineCancelsCooperativePolicyWork()
    {
        var duration = TimeSpan.FromMilliseconds(100);
        var clock = new ManualQueryClock();
        var policy = new DeadlinePolicy();
        var engine = await Configured(new() { MaxDuration = duration, MaxCursors = 1 }, clock, policy);
        policy.Block = true;
        var operation = Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "1")).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(duration);
        await ExpectCode(async () => await operation, "server_busy");
        await policy.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        policy.Block = false;
        await Assert.That((await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("limit", "1"))).Page!.TotalCount).IsEqualTo(4UL);
    }

    [Test]
    public async Task SortWorkExhaustionRemainsAQueryProblemRatherThanAnInfrastructureFallback()
    {
        var engine = await Configured(new() { MaxWork = 5500 });
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            await Send(engine, RegistryAction.Patch, "/fleets/" + id, "{\"name\":\"" + new string('x', 600) + "\"}");
        }

        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("sort", "name")), "too_large");
    }

    internal static ValueTask<RegistryResult> Follow(RegistryEngine engine, Uri uri, RegistryOperationContext? context = null)
    {
        var mount = engine.PublicRoot.AbsolutePath.TrimEnd('/');
        var path = uri.AbsolutePath[mount.Length..];
        var query = uri.Query[1..].Split('&').Select(field =>
        {
            var parts = field.Split('=', 2);
            return new KeyValuePair<string, string?>(Uri.UnescapeDataString(parts[0]),
                parts.Length == 1 ? null : Uri.UnescapeDataString(parts[1]));
        }).ToArray();
        return engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse(path))
        {
            Parameters = query
        }, context ?? Writer());
    }

    internal static async Task<RegistryEngine> Configured(RegistryQueryLimits limits, TimeProvider? clock = null,
        IRegistryAuthorizationPolicy? policy = null, IRegistryPersistence? persistence = null)
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "queries",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = RegistryModel.Compile(RegistryJson.Parse(QueryModel)),
            QueryLimits = limits,
            TimeProvider = clock ?? TimeProvider.System
        }, persistence ?? new InMemoryRegistryPersistence(), policy ?? new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/", """
            {"fleets":{"a":{"name":"Alpha","rank":2},"b":{"name":"Beta","rank":10},
              "c":{"name":"Gamma","rank":30},"d":{"name":"Delta"}}}
            """);
        return engine;
    }

    internal sealed class QueryClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    internal sealed class ManualQueryClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<DeadlineTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            DeadlineTimer[] ready;
            lock (_gate)
            {
                _ticks += duration.Ticks;
                ready = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in ready) { _timers.Remove(timer); }
            }
            foreach (var timer in ready) { timer.Fire(); }
        }

        private sealed class DeadlineTimer(ManualQueryClock owner, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            internal long Due { get; private set; }
            internal void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan)
                {
                    throw new InvalidOperationException("Query deadline tests expect one-shot timers.");
                }
                if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                {
                    throw new ArgumentOutOfRangeException(nameof(dueTime));
                }
                lock (owner._gate)
                {
                    if (_disposed) { return false; }
                    owner._timers.Remove(this);
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner._ticks + dueTime.Ticks);
                    if (dueTime != Timeout.InfiniteTimeSpan) { owner._timers.Add(this); }
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    _disposed = true;
                    owner._timers.Remove(this);
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal sealed class QueryPolicy : IRegistryAuthorizationPolicy
    {
        internal HashSet<string> Revoked { get; } = new(StringComparer.Ordinal);
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(access != RegistryAccess.Read || !Revoked.Contains(path.EscapedPath));
    }

    private sealed class DeadlinePolicy : IRegistryAuthorizationPolicy
    {
        internal bool Block { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            if (Block && access == RegistryAccess.Read && path.Kind == RegistryPathKind.Group)
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Canceled.TrySetResult();
                    }
                }
            }

            return true;
        }
    }

    private sealed class SnapshotCounter : IRegistryPersistence
    {
        private readonly InMemoryRegistryPersistence _inner = new();
        internal int OpenSnapshots { get; private set; }
        internal int Reads { get; private set; }
        public bool IsReadOnly => false;
        public async ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = await _inner.ReadSnapshotAsync(cancellationToken);
            OpenSnapshots++;
            Reads++;
            return new Counted(this, snapshot);
        }
        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) => _inner.PrepareAsync(expectedGeneration, mutations, cancellationToken);
        private sealed class Counted(SnapshotCounter owner, IRegistrySnapshot snapshot) : IRegistrySnapshot
        {
            public long Generation => snapshot.Generation;
            public RegistryRecord? Find(string key) => snapshot.Find(key);
            public IEnumerable<RegistryRecord> GetChildren(string collectionKey) => snapshot.GetChildren(collectionKey);
            public IEnumerable<RegistryRecord> EnumerateRecords() => snapshot.EnumerateRecords();
            public Stream OpenDocument(string key, CancellationToken cancellationToken = default) => snapshot.OpenDocument(key, cancellationToken);
            public void Dispose()
            {
                snapshot.Dispose();
                owner.OpenSnapshots--;
            }
        }
    }
}
