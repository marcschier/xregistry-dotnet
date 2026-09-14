using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.RegistryCapabilityTests;

namespace XRegistry.Server.Tests;

public class RegistryShortSelfTests
{
    [Test]
    public async Task ShortSelfIsStableAcrossDisableEnableAndFrozenRestart()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = MutableEngine(store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"kept"}""");
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":true}""");
        var first = await Send(engine, RegistryAction.Read, "/teams/g/notes/n");
        var shortUrl = first.Metadata!.RootElement.GetProperty("shortself").GetString()!;
        await Assert.That(shortUrl.StartsWith("https://registry.example/catalog/~/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(shortUrl.Contains('$', StringComparison.Ordinal)).IsFalse();
        var path = await engine.ResolvePathAsync(RegistryAction.Read, new Uri(shortUrl).AbsolutePath["/catalog".Length..], Writer());
        await Assert.That(path.ToXid()).IsEqualTo("/teams/g/notes/n");
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":false}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n")).Metadata!.RootElement.TryGetProperty("shortself", out _)).IsFalse();
        var restarted = MutableEngine(store);
        await Send(restarted, RegistryAction.Patch, "/capabilities", """{"shortself":true}""");
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g/notes/n")).Metadata!.RootElement.GetProperty("shortself").GetString()).IsEqualTo(shortUrl);
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g/notes/n", null,
            new KeyValuePair<string, string?>("doc", null))).Metadata!.RootElement.TryGetProperty("shortself", out _)).IsFalse();
    }

    [Test]
    public async Task ShortSelfCoversEntityPlanesWithoutRewritingDomainUrlsOrDocuments()
    {
        var engine = MutableEngine();
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """
            {"documentation":"https://registry.example/catalog/teams/g/files/f",
             "file":{"shortself":"domain value","url":"https://registry.example/catalog/teams/g/files/f"}}
            """);
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/files/f$details")).Metadata!.RootElement.GetProperty("epoch").GetInt32();
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":true}""");
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { "/", "/teams/g", "/teams/g/files/f$details", "/teams/g/files/f/meta", "/teams/g/files/f/versions/1$details" })
        {
            var entity = (await Send(engine, RegistryAction.Read, path)).Metadata!.RootElement;
            var shortUrl = entity.GetProperty("shortself").GetString()!;
            await Assert.That(urls.Add(shortUrl)).IsTrue();
            await Assert.That(shortUrl.Contains('$', StringComparison.Ordinal)).IsFalse();
        }

        var result = await Send(engine, RegistryAction.Read, "/teams/g/files/f$details", null, new KeyValuePair<string, string?>("inline", "file,meta"));
        await Assert.That(result.Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(before);
        await Assert.That(result.Metadata.RootElement.GetProperty("documentation").GetString()).IsEqualTo("https://registry.example/catalog/teams/g/files/f");
        await Assert.That(result.Metadata.RootElement.GetProperty("file").GetProperty("shortself").GetString()).IsEqualTo("domain value");
        await Assert.That(result.Metadata.RootElement.GetProperty("file").GetProperty("url").GetString()).IsEqualTo("https://registry.example/catalog/teams/g/files/f");
        var document = await Send(engine, RegistryAction.Read, "/teams/g/files/f", null, new("doc", null), new("inline", "*"));
        await Assert.That(document.Metadata!.RootElement.TryGetProperty("shortself", out _)).IsFalse();
        await Assert.That(document.Metadata.RootElement.GetProperty("meta").TryGetProperty("shortself", out _)).IsFalse();
        await Assert.That(document.Metadata.RootElement.GetProperty("versions").GetProperty("1").TryGetProperty("shortself", out _)).IsFalse();
        await Assert.That(document.Metadata.RootElement.GetProperty("versions").GetProperty("1").GetProperty("file").GetProperty("shortself").GetString()).IsEqualTo("domain value");
    }

    [Test]
    public async Task ShortNavigationRetainsLocalAliasIdentityAndDeletedAliasesDoNotRebind()
    {
        var engine = MutableEngine();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/target", """{"name":"target name"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/alias", """{"meta":{"xref":"/teams/g/notes/target"}}""");
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":true}""");
        var source = (await Send(engine, RegistryAction.Read, "/teams/g/notes/alias")).Metadata!.RootElement.GetProperty("shortself").GetString()!;
        var target = (await Send(engine, RegistryAction.Read, "/teams/g/notes/target")).Metadata!.RootElement.GetProperty("shortself").GetString();
        await Assert.That(source == target).IsFalse();
        var versionPath = await engine.ResolvePathAsync(RegistryAction.Read, new Uri(source).AbsolutePath["/catalog".Length..] + "/versions/1$details", Writer());
        var version = await engine.ExecuteAsync(new(RegistryAction.Read, versionPath), Writer());
        await Assert.That(version.Metadata!.RootElement.GetProperty("noteid").GetString()).IsEqualTo("alias");
        await Assert.That(version.Metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("target name");
        await Assert.That(version.Metadata.RootElement.GetProperty("shortself").GetString()).IsEqualTo(source + "/versions/1");
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/alias");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/alias", "{}");
        await Assert.That(async () => await engine.ResolvePathAsync(RegistryAction.Read,
            new Uri(source).AbsolutePath["/catalog".Length..], Writer())).Throws<RegistryException>();
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/alias")).Metadata!.RootElement.GetProperty("shortself").GetString() == source).IsFalse();
    }

    [Test]
    public async Task ShortRepresentationChangesInvalidateFrozenPagesInsteadOfLeakingAnOldVariant()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = MutableEngine(store);
        await Send(engine, RegistryAction.Post, "/teams", """{"a":{},"b":{}}""");
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":true}""");
        var first = await Send(engine, RegistryAction.Read, "/teams", null, new KeyValuePair<string, string?>("limit", "1"));
        await Assert.That(first.Metadata!.RootElement.GetProperty("a").TryGetProperty("shortself", out _)).IsTrue();
        var other = MutableEngine(store);
        await Send(other, RegistryAction.Patch, "/capabilities", """{"shortself":false}""");
        await ExpectCode(() => RegistryPaginationQueryTests.Follow(engine,
            first.Page!.Links.Single(static link => link.Relation == "next").Target), "bad_cursor");
        var current = await Send(engine, RegistryAction.Read, "/teams");
        await Assert.That(current.Metadata!.RootElement.GetProperty("a").TryGetProperty("shortself", out _)).IsFalse();
    }

    [Test]
    public async Task ShortMappingQuotaFailureDoesNotPublishCapabilitiesOrPartialAliases()
    {
        var store = new InMemoryRegistryPersistence(maxRecords: 9);
        var engine = MutableEngine(store);
        await Send(engine, RegistryAction.Replace, "/teams/g", "{}");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":true}"""), "operation_limit");
        await Assert.That((await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement.GetProperty("shortself").GetBoolean()).IsFalse();
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.GetChildren("$short").Count()).IsEqualTo(0);
        await Assert.That(snapshot.Find("$capabilities")).IsNull();
    }

    [Test]
    public async Task ShortAliasesDoNotBypassCanonicalPathAuthorization()
    {
        var engine = MutableEngine(authorization: new DenyCanonicalRead());
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"shortself":true}""");
        var created = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        var shortUrl = created.Metadata!.RootElement.GetProperty("shortself").GetString()!;
        var error = await Assert.That(async () => await engine.ResolvePathAsync(RegistryAction.Read,
            new Uri(shortUrl).AbsolutePath["/catalog".Length..], Writer())).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("forbidden");
    }

    [Test]
    public async Task ReadonlyBackendRejectsShortWritesBeforeLookingUpAnAlias()
    {
        var engine = MutableEngine(new ReadonlyStore());
        var error = await Assert.That(async () => await engine.ResolvePathAsync(RegistryAction.Replace,
            "/~/AAAAAAAAAAAAAAAA", Writer())).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("readonly");
    }

    private sealed class DenyCanonicalRead : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(access != RegistryAccess.Read || path.ToString() != "/teams/g/notes/n");
    }

    private sealed class ReadonlyStore : IRegistryPersistence
    {
        public bool IsReadOnly => true;
        public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A readonly short write must not acquire a snapshot.");
        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("A readonly store must not prepare.");
    }
}
