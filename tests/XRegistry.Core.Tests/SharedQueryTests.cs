// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Queries;

namespace XRegistry.Core.Tests;

public class SharedQueryTests
{
    private static readonly RegistryModel s_model = RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"fleets":{"singular":"fleet","resources":{"items":{"singular":"item","hasdocument":false,
          "attributes":{"rank":{"type":"decimal"}}}}}}}
        """));
    private static readonly Uri s_root = new("https://public.example/catalog");

    [Test]
    public async Task EvaluatesAlreadyShadowedResourceUnitsWithoutARegistryEngine()
    {
        var first = new SelectedUnit("native-snapshot", "/fleets/g/items/a", """
            {"defaultversionid":"2","readonly":true}
            """, """{"versionid":"2","rank":9007199254740993}""");
        var second = new SelectedUnit("http-detached", "/fleets/g/items/b", """
            {"defaultversionid":"v1","readonly":false}
            """, """{"versionid":"v1","rank":9007199254740992}""");
        var selected = new[] { first, second };
        var source = new DetachedSource();
        foreach (var unit in selected)
        {
            source.Entities.Add(unit.Path, new(RegistryJson.Parse(unit.Meta)));
            source.Entities.Add(unit.Path + "/meta", new(RegistryJson.Parse(unit.Meta)));
            var version = RegistryJson.Parse(unit.Version);
            source.Entities.Add(unit.Path + "/versions/" + version.RootElement.GetProperty("versionid").GetString(), new(version));
        }

        using var budget = new RegistryQueryBudget();
        var result = await RegistryQuery.EvaluateAsync(s_model, source,
            new(RegistryPath.Parse("/fleets/g/items"), s_root)
            {
                Filters = ["rank>9007199254740992,meta.readonly=true"],
                Sort = "rank=desc"
            }, budget);
        var units = result.RootPaths.Select(path => selected.Single(unit => unit.Path == path.ToXid())).ToArray();

        await Assert.That(units.Length).IsEqualTo(1);
        await Assert.That(ReferenceEquals(units[0], first)).IsTrue();
        await Assert.That(units[0].Origin).IsEqualTo("native-snapshot");
        await Assert.That(result.Includes(RegistryPath.Parse("/fleets/g/items/a/versions/2"))).IsTrue();
        await Assert.That(result.Includes(RegistryPath.Parse("/fleets/g/items/b/versions/v1"))).IsFalse();
        await Assert.That(result.CollectionUrl(RegistryPath.Parse("/fleets/g/items"), 1))
            .IsEqualTo("https://public.example/catalog/fleets/g/items?filter=itemid%3Da");
    }

    private sealed record SelectedUnit(string Origin, string Path, string Meta, string Version);

    [Test]
    public async Task SourceDeadlineDisposesTheCompletedEnumeratorWithoutAnInfrastructureFallback()
    {
        var source = new BlockingSource();
        using var budget = new RegistryQueryBudget(new() { MaxDuration = TimeSpan.FromMilliseconds(100) });
        RegistryDiagnostic? failure = null;
        try
        {
            await RegistryQuery.EvaluateAsync(s_model, source, new(RegistryPath.Parse("/fleets"), s_root), budget);
        }
        catch (RegistryException exception)
        {
            failure = exception.Diagnostic;
        }

        await Assert.That(failure?.Code).IsEqualTo("server_busy");
        await Assert.That(source.Disposed).IsTrue();
    }

    [Test]
    public async Task CallerCancellationDuringEnumerationWaitsForSourceCleanupAndRemainsCancellation()
    {
        var source = new BlockingSource();
        using var cancellation = new CancellationTokenSource();
        using var budget = new RegistryQueryBudget(cancellationToken: cancellation.Token);
        var pending = RegistryQuery.EvaluateAsync(s_model, source,
            new(RegistryPath.Parse("/fleets"), s_root), budget).AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(source.Disposed).IsTrue();
    }

    [Test]
    public async Task CallerCancellationWinsWhenCallbackTimeoutAndCancellationRace()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new RegistryQueryBudget(cancellationToken: cancellation.Token);
        await Assert.That(async () => await budget.RunAsync<int>(_ =>
        {
            cancellation.Cancel();
            throw new TimeoutException();
        })).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task InfrastructureSourceFailuresPropagateInsteadOfInventingAnEmptySelection()
    {
        var source = new DetachedSource();
        source.Entities.Add("/fleets/g/items/r", new(RegistryJson.Parse("""{"defaultversionid":"1"}""")));
        using var budget = new RegistryQueryBudget();
        await Assert.That(async () => await RegistryQuery.EvaluateAsync(s_model, source,
            new(RegistryPath.Parse("/fleets/g/items"), s_root) { Filters = ["name"] }, budget))
            .Throws<InvalidDataException>();
    }

    private sealed class BlockingSource : IRegistryQuerySource
    {
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<RegistryQueryEntity?> ReadEntityAsync(RegistryPath path, bool includeDocument,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No entity read was expected.");
        public async IAsyncEnumerable<RegistryPath> GetChildrenAsync(RegistryPath collection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield return RegistryPath.Parse("/fleets/g");
            }
            finally
            {
                await Task.Delay(25, CancellationToken.None);
                Disposed = true;
            }
        }
    }

    private sealed class DetachedSource : IRegistryQuerySource
    {
        internal Dictionary<string, RegistryQueryEntity> Entities { get; } = new(StringComparer.Ordinal);

        public ValueTask<RegistryQueryEntity?> ReadEntityAsync(RegistryPath path, bool includeDocument,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RegistryQueryEntity?>(Entities.TryGetValue(path.EscapedPath, out var entity)
                ? entity : throw new InvalidDataException("A required selected entity is absent."));
        }

        public async IAsyncEnumerable<RegistryPath> GetChildrenAsync(RegistryPath collection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var prefix = collection.EscapedPath + "/";
            foreach (var key in Entities.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) &&
                !key[prefix.Length..].Contains('/')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return RegistryPath.Parse(key);
            }
        }
    }
}
