using System.Security.Claims;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryCrossReferenceConstraintTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AliasesMustSatisfyTheReferringGroupsEnumAndEqualsConstraints(bool equals)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ConstrainedModel(equals), store);
        await Seed(engine, "blue");
        using var before = await store.ReadSnapshotAsync();
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/b/g/rs/alias",
            """{"meta":{"xref":"/a/g/rs/target"}}"""), "constraint_failure");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g")).Metadata!.RootElement.GetProperty("rscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TargetMutationsCannotInvalidateAReferringGroupsConstraints(bool equals)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ConstrainedModel(equals), store);
        await Seed(engine, "red");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        using var before = await store.ReadSnapshotAsync();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/a/g/rs/target", """{"color":"blue"}"""), "constraint_failure");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await Send(engine, RegistryAction.Read, "/a/g/rs/target")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo("red");
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo("red");
    }

    [Test]
    public async Task ReferringGroupChangesCannotInvalidateItsExistingAlias()
    {
        var engine = Create(ConstrainedModel(equals: true));
        await Seed(engine, "red");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/b/g", """{"color":"blue"}"""), "constraint_failure");
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g")).Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("red");
    }

    [Test]
    public async Task AliasConstraintsApplyToNondefaultVersionsAsWell()
    {
        var engine = Create(ConstrainedModel(equals: false));
        await Seed(engine, "blue");
        await Send(engine, RegistryAction.Post, "/a/g/rs/target", """{"color":"red"}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/a/g/rs/target")).Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("red");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/b/g/rs/alias",
            """{"meta":{"xref":"/a/g/rs/target"}}"""), "constraint_failure");
    }

    [Test]
    public async Task APreviouslyDanglingAliasConstrainsTheTargetsLaterCreation()
    {
        var engine = Create(ConstrainedModel(equals: false));
        await Send(engine, RegistryAction.Replace, "/b/g", """{"color":"red"}""");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/a/g/rs/target", """{"color":"blue"}"""), "constraint_failure");
        await Send(engine, RegistryAction.Replace, "/a/g/rs/target", """{"color":"red"}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("red");
    }

    [Test]
    public async Task RemovingAnAliasReleasesOnlyItsOwnReverseConstraint()
    {
        var engine = Create(ConstrainedModel(equals: false));
        await Seed(engine, "red");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        await Send(engine, RegistryAction.Delete, "/b/g/rs/alias");
        var updated = await Send(engine, RegistryAction.Patch, "/a/g/rs/target", """{"color":"blue"}""");
        await Assert.That(updated.Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("blue");
    }

    [Test]
    public async Task ReferringGroupDefaultsNeverMutateTheReferencedVersions()
    {
        var model = JsonNode.Parse(ConstrainedModel(equals: false))!;
        model["groups"]!["b"]!["constraints"]!["rs.color"] = new JsonObject { ["default"] = "red" };
        var engine = Create(model.ToJsonString());
        await Send(engine, RegistryAction.Replace, "/a/g/rs/target", "{}");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        var target = (await Send(engine, RegistryAction.Read, "/a/g/rs/target")).Metadata!.RootElement;
        var alias = (await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement;
        await Assert.That(target.TryGetProperty("color", out _)).IsFalse();
        await Assert.That(alias.TryGetProperty("color", out _)).IsFalse();
        await Assert.That(target.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments("/a/g/rs/target")]
    [Arguments("/a/g/rs/target/meta")]
    [Arguments("/a/g/rs/target/versions/1")]
    public async Task CreatingConstrainedAliasesRequiresReadAccessBeforeEvaluatingTargetValues(string deniedPath)
    {
        var policy = new ReadPolicy();
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ConstrainedModel(equals: false), store, policy);
        await Seed(engine, "blue");
        using var before = await store.ReadSnapshotAsync();
        policy.Denied.Add(deniedPath);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/b/g/rs/alias",
            """{"meta":{"xref":"/a/g/rs/target"}}"""), "forbidden");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That(after.Find("/b/g/rs/alias")).IsNull();
    }

    [Test]
    public async Task ReverseConstraintFailuresDoNotDiscloseUnreadableReferringGroupIdentity()
    {
        var policy = new ReadPolicy();
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ConstrainedModel(equals: false), store, policy);
        await Seed(engine, "red");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        policy.DenyReferringGroup = true;
        var exception = await Assert.That(async () =>
            await Send(engine, RegistryAction.Patch, "/a/g/rs/target", """{"color":"blue"}"""))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the reverse constraint to reject.");
        await Assert.That(exception.Diagnostic.Code).IsEqualTo("constraint_failure");
        await Assert.That(exception.Diagnostic.Path).IsEqualTo("/a/g/rs/target");
        await Assert.That(exception.Diagnostic.Message.Contains("/b/", StringComparison.Ordinal)).IsFalse();
        var arguments = (IReadOnlyDictionary<string, string>)exception.Data["xregistry.args"]!;
        await Assert.That(arguments.Values.Any(static value => value.Contains("/b/", StringComparison.Ordinal))).IsFalse();
        await Assert.That((await Send(engine, RegistryAction.Read, "/a/g/rs/target")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo("red");
    }

    [Test]
    public async Task AtomicTargetAndReferringGroupUpdatesValidateTheirFinalValues()
    {
        var engine = Create(ConstrainedModel(equals: true));
        await Seed(engine, "red");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        await Send(engine, RegistryAction.Patch, "/", """
            {"a":{"g":{"rs":{"target":{"color":"blue"}}}},"b":{"g":{"color":"blue"}}}
            """);
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo("blue");
    }

    [Test]
    public async Task UnrelatedTargetWritesDoNotBecomeSubjectToAnotherTargetsAlias()
    {
        var engine = Create(ConstrainedModel(equals: false));
        await Seed(engine, "red");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        var other = await Send(engine, RegistryAction.Replace, "/a/g/rs/other", """{"color":"blue"}""");
        await Assert.That(other.Metadata!.RootElement.GetProperty("color").GetString()).IsEqualTo("blue");
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo("red");
    }

    [Test]
    public async Task ReferringDefaultsCannotSupplyMissingValuesToSatisfyEquals()
    {
        var model = JsonNode.Parse(ConstrainedModel(equals: true))!;
        model["groups"]!["b"]!["constraints"]!["rs.color"]!["default"] = "red";
        var engine = Create(model.ToJsonString());
        await Send(engine, RegistryAction.Replace, "/a/g/rs/target", "{}");
        await Send(engine, RegistryAction.Replace, "/b/g", """{"color":"red"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/b/g/rs/alias",
            """{"meta":{"xref":"/a/g/rs/target"}}"""), "constraint_failure");
        await Assert.That((await Send(engine, RegistryAction.Read, "/a/g/rs/target")).Metadata!.RootElement
            .TryGetProperty("color", out _)).IsFalse();
    }

    [Test]
    public async Task TimestampAliasConstraintsAcceptTheTargetsNormalizedInstantAndRejectAdjacentValues()
    {
        var model = JsonNode.Parse(ConstrainedModel(equals: false))!;
        model["groups"]!["a"]!["resources"]!["rs"]!["attributes"]!["color"] = new JsonObject { ["type"] = "timestamp" };
        model["groups"]!["b"]!["constraints"]!["rs.color"]!["enum"] = new JsonArray("2026-09-12T14:00:00.123456789+02:00");
        var engine = Create(model.ToJsonString());
        await Seed(engine, "2026-09-12T14:00:00.123456789+02:00");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo("2026-09-12T12:00:00.123456789Z");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/a/g/rs/target",
            """{"color":"2026-09-12T12:00:00.123456788Z"}"""), "constraint_failure");
        await Assert.That((await Send(engine, RegistryAction.Read, "/a/g/rs/target")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo("2026-09-12T12:00:00.123456789Z");
    }

    [Test]
    public async Task InstanceOnlyAndReplacementModelConstraintsAlsoValidateExistingAliases()
    {
        var initial = JsonNode.Parse(ConstrainedModel(equals: false))!;
        initial["groups"]!["b"]!.AsObject().Remove("constraints");
        var store = new InMemoryRegistryPersistence();
        var engine = Create(initial.ToJsonString(), store);
        await Seed(engine, "blue");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        using var before = await store.ReadSnapshotAsync();
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/b/g",
            """{"constraints":{"rs.color":{"enum":["red"]}}}"""), "constraint_failure");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", ConstrainedModel(equals: false)),
            "model_compliance_error");
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement
            .GetProperty("color").GetString()).IsEqualTo("blue");
    }

    [Test]
    public async Task DerivedDefaultMembershipIsCheckedInsteadOfItsStoragePlaceholder()
    {
        var model = JsonNode.Parse(ConstrainedModel(equals: false))!;
        model["groups"]!["b"]!["attributes"]!["expecteddefault"] = new JsonObject
        {
            ["type"] = "boolean",
            ["required"] = true,
            ["default"] = true
        };
        model["groups"]!["b"]!["constraints"] = new JsonObject
        {
            ["rs.isdefault"] = new JsonObject { ["equals"] = "expecteddefault" }
        };
        var engine = Create(model.ToJsonString());
        await Seed(engine, "red");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/a/g/rs/target", """{"color":"red"}"""),
            "constraint_failure");
        await Assert.That((await Send(engine, RegistryAction.Read, "/a/g/rs/target/versions/1")).Metadata!.RootElement
            .GetProperty("isdefault").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task DanglingAndSecondHopReferencesDoNotAcquireOrConstrainAnUnselectedTarget()
    {
        var engine = Create(ConstrainedModel(equals: false));
        await Seed(engine, "blue");
        await Send(engine, RegistryAction.Replace, "/a/g/rs/chain", """{"meta":{"xref":"/a/g/rs/target"}}""");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/chain"}}""");
        var alias = (await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement;
        await Assert.That(alias.TryGetProperty("color", out _)).IsFalse();
        await Send(engine, RegistryAction.Delete, "/a/g/rs/target");
        await Send(engine, RegistryAction.Replace, "/a/g/rs/target", """{"color":"blue"}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/b/g/rs/alias")).Metadata!.RootElement
            .TryGetProperty("color", out _)).IsFalse();
    }

    [Test]
    public async Task CrossReferenceValidationUsesIndexedCollectionsOnceAndNeverOpensDocuments()
    {
        var store = new ObservedPersistence();
        var engine = Create(ConstrainedModel(equals: false), store);
        await Seed(engine, "red");
        await Send(engine, RegistryAction.Replace, "/a/g/rs/other", """{"color":"blue"}""");
        await Send(engine, RegistryAction.Replace, "/b/g/rs/alias", """{"meta":{"xref":"/a/g/rs/target"}}""");
        store.Collections.Clear();
        store.ForbidEnumeration = true;
        await Send(engine, RegistryAction.Patch, "/a/g/rs/target", """{"color":"red"}""");
        await Assert.That(store.Collections.Values.All(static count => count == 1)).IsTrue();
        await Assert.That(store.Collections["/b/g/rs"]).IsEqualTo(1);
        await Assert.That(store.Collections.ContainsKey("/a/g/rs/other/versions")).IsFalse();
        await Assert.That(store.OpenedDocuments).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationDuringAliasAuthorizationPublishesNeitherAliasNorEvents()
    {
        using var cancellation = new CancellationTokenSource();
        var policy = new ReadPolicy();
        var store = new InMemoryRegistryPersistence();
        var engine = Create(ConstrainedModel(equals: false), store, policy);
        await Seed(engine, "red");
        using var before = await store.ReadSnapshotAsync();
        policy.OnRead = path =>
        {
            if (path.EscapedPath == "/a/g/rs/target/versions/1") { cancellation.Cancel(); }
        };
        await Assert.That(async () => await engine.ExecuteAsync(
            new(RegistryAction.Replace, RegistryPath.Parse("/b/g/rs/alias"))
            {
                Metadata = RegistryJson.Parse("""{"meta":{"xref":"/a/g/rs/target"}}""")
            }, Writer(), cancellation.Token)).Throws<OperationCanceledException>();
        using var after = await store.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That(after.Find("/b/g/rs/alias")).IsNull();
    }

    private static async Task Seed(RegistryEngine engine, string color)
    {
        await Send(engine, RegistryAction.Replace, "/a/g/rs/target", "{\"color\":\"" + color + "\"}");
        await Send(engine, RegistryAction.Replace, "/b/g", """{"color":"red"}""");
    }

    private static string ConstrainedModel(bool equals)
    {
        var model = JsonNode.Parse("""
            {"groups":{
              "a":{"singular":"a","resources":{"rs":{"singular":"r","hasdocument":false,
                "attributes":{"color":{"type":"string","enum":["red","blue"]}}}}},
              "b":{"singular":"b","ximportresources":["/a/rs"],"attributes":{"color":{"type":"string"}},
                "constraints":{"rs.color":{"enum":["red"]}}}
            }}
            """)!;
        if (equals) { model["groups"]!["b"]!["constraints"]!["rs.color"] = new JsonObject { ["equals"] = "color" }; }
        return model.ToJsonString();
    }

    private sealed class ReadPolicy : IRegistryAuthorizationPolicy
    {
        internal HashSet<string> Denied { get; } = new(StringComparer.Ordinal);
        internal bool DenyReferringGroup { get; set; }
        internal Action<RegistryPath>? OnRead { get; set; }
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            if (access == RegistryAccess.Read) { OnRead?.Invoke(path); }
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(access != RegistryAccess.Read ||
                !Denied.Contains(path.EscapedPath) && !(DenyReferringGroup && path.EscapedPath.StartsWith("/b/", StringComparison.Ordinal)));
        }
    }

    private sealed class ObservedPersistence : IRegistryPersistence
    {
        private readonly InMemoryRegistryPersistence _inner = new();
        internal Dictionary<string, int> Collections { get; } = new(StringComparer.Ordinal);
        internal bool ForbidEnumeration { get; set; }
        internal int OpenedDocuments { get; set; }
        public bool IsReadOnly => false;
        public async ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
            new Snapshot(this, await _inner.ReadSnapshotAsync(cancellationToken));
        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) => _inner.PrepareAsync(expectedGeneration, mutations, cancellationToken);

        private sealed class Snapshot(ObservedPersistence owner, IRegistrySnapshot inner) : IRegistrySnapshot
        {
            public long Generation => inner.Generation;
            public RegistryRecord? Find(string key) => inner.Find(key);
            public IEnumerable<RegistryRecord> GetChildren(string collectionKey)
            {
                owner.Collections[collectionKey] = owner.Collections.GetValueOrDefault(collectionKey) + 1;
                return inner.GetChildren(collectionKey);
            }
            public IEnumerable<RegistryRecord> EnumerateRecords() => owner.ForbidEnumeration
                ? throw new InvalidOperationException("Ordinary xref validation must not enumerate every persistence record.")
                : inner.EnumerateRecords();
            public Stream OpenDocument(string key, CancellationToken cancellationToken = default)
            {
                owner.OpenedDocuments++;
                return inner.OpenDocument(key, cancellationToken);
            }
            public void Dispose() => inner.Dispose();
        }
    }
}
