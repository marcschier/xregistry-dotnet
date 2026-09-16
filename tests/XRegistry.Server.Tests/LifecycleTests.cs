// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class LifecycleTests
{
    [Test]
    public async Task PostCollectionsReturnsOnlyProcessedChildrenAndDeleteGuardsAreAtomic()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/a", "{}");
        var post = await Send(engine, RegistryAction.Post, "/", """{"teams":{"b":{},"c":{}}}""");
        await Assert.That(post.Metadata!.RootElement.EnumerateObject().Select(static property => property.Name).ToArray()).IsEquivalentTo(["teams"], StringComparer.Ordinal);
        await Assert.That(post.Metadata.RootElement.GetProperty("teams").EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["b", "c"], StringComparer.Ordinal);
        await Send(engine, RegistryAction.Patch, "/teams/b", "{}");
        await ExpectCode(() => Send(engine, RegistryAction.Delete, "/teams", """{"a":{"epoch":0},"b":{"epoch":0}}"""), "mismatched_epoch");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams")).Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(3);
        await Send(engine, RegistryAction.Delete, "/teams", "{}");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams")).Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(3);
        await Send(engine, RegistryAction.Delete, "/teams", """{"a":{"epoch":null},"b":{"epoch":1},"missing":{}}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams")).Metadata!.RootElement.EnumerateObject()
            .Select(static property => property.Name).ToArray()).IsEquivalentTo(["c"], StringComparer.Ordinal);
        await Send(engine, RegistryAction.Delete, "/teams");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams")).Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task ChangingDefaultVersionDoesNotTouchEitherVersionAndDeletingLastRemovesResource()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1"}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"v2"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"v1"}""");
        var after = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
        foreach (var version in new[] { "v1", "v2" })
        {
            await Assert.That(after.GetProperty(version).GetProperty("epoch").GetRawText()).IsEqualTo(before.GetProperty(version).GetProperty("epoch").GetRawText());
            await Assert.That(after.GetProperty(version).GetProperty("modifiedat").GetString()).IsEqualTo(before.GetProperty(version).GetProperty("modifiedat").GetString());
        }

        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/teams/g/notes/n"), "not_found");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g")).Metadata!.RootElement.GetProperty("notescount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task IgnoreFlagsPermitEntityImportWithoutSuppressingNestedIdentityGuards()
    {
        var engine = Create();
        var result = await Send(engine, RegistryAction.Replace, "/", """
            {"registryid":"other","epoch":99,"capabilities":"ignored","modelsource":false,
             "$schema":"https://example/schema","teams":{"g":{"teamid":"g"}}}
            """, new KeyValuePair<string, string?>("ignore", "id,epoch,capabilities,modelsource"));
        await Assert.That(result.Metadata!.RootElement.GetProperty("registryid").GetString()).IsEqualTo("test");
        await Assert.That(result.Metadata.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(1);
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """{"teams":{"g":{"teamid":"different"}}}""",
            new KeyValuePair<string, string?>("ignore", "id")), "mismatched_id");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", "{}",
            new KeyValuePair<string, string?>("ignore", "unsupported")), "bad_ignore");
    }

    [Test]
    public async Task EpochZeroNullMissingAndHugeGuardsAreExact()
    {
        var engine = Create();
        var first = await Send(engine, RegistryAction.Patch, "/", """{"epoch":0,"name":"first"}""");
        await Assert.That(first.Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(1);
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """{"epoch":0,"name":"stale"}"""), "mismatched_epoch");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """{"epoch":184467440737095516160000}"""), "mismatched_epoch");
        await Send(engine, RegistryAction.Patch, "/", """{"epoch":null}""");
        var absent = await Send(engine, RegistryAction.Patch, "/", "{}");
        await Assert.That(absent.Metadata!.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(3);
        await Assert.That(absent.Metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("first");
    }

    [Test]
    public async Task IdenticalWritesTouchOnlyTheEntity()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"same"}""");
        var root = await Send(engine, RegistryAction.Read, "/");
        var group = await Send(engine, RegistryAction.Read, "/teams/g");
        var before = await Send(engine, RegistryAction.Read, "/teams/g/notes/n");
        var after = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"same"}""");
        await Assert.That(after.Metadata!.RootElement.GetProperty("epoch").GetInt32())
            .IsEqualTo(before.Metadata!.RootElement.GetProperty("epoch").GetInt32() + 1);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("epoch").GetRawText())
            .IsEqualTo(root.Metadata!.RootElement.GetProperty("epoch").GetRawText());
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g")).Metadata!.RootElement.GetProperty("epoch").GetRawText())
            .IsEqualTo(group.Metadata!.RootElement.GetProperty("epoch").GetRawText());
    }

    [Test]
    public async Task PatchNullDeletesWhileMissingPreservesAndReadonlyInputIsIgnored()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g", """{"name":"name","description":"keep"}""");
        var result = await Send(engine, RegistryAction.Patch, "/teams/g", """
            {"name":null,"self":{"invalid":"ignored"},"notescount":"ignored"}
            """);
        await Assert.That(result.Metadata!.RootElement.TryGetProperty("name", out _)).IsFalse();
        await Assert.That(result.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("keep");
    }

    [Test]
    public async Task EncodedIdsShareIdentityAndSiblingIdsAreCaseInsensitiveUnique()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/%41", """{"name":"same entity"}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/A")).Metadata!.RootElement.GetProperty("name").GetString())
            .IsEqualTo("same entity");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/a", "{}"), "mismatched_id");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/teams/a"), "not_found");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task NestedVersionsOverrideResourceAttributes()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1","name":"first"}""");
        var result = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """
            {"name":123,"epoch":999,"versions":{"v1":{"name":"nested wins"}}}
            """);
        await Assert.That(result.Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("nested wins");
        await Assert.That(result.Metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("v1");
    }

    [Test]
    public async Task ImplicitParentsDefaultVersionAndRetentionAreAtomic()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f/versions/v1$details", """{"filebase64":"AAE="}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f/versions/v2$details", """{"filebase64":"/w=="}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f/versions/v3$details", """{"filebase64":""}""");
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/files/f/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("v3");
        var versions = await Send(engine, RegistryAction.Read, "/teams/g/files/f/versions");
        await Assert.That(versions.Metadata!.RootElement.EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["v2", "v3"], StringComparer.Ordinal);
        await Assert.That(versions.Metadata.RootElement.GetProperty("v2").GetProperty("ancestorid").GetString()).IsEqualTo("v2");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g")).Metadata!.RootElement.GetProperty("filescount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task StickyDefaultAndDeletingItsVersionSelectNewest()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1"}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"v2"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"v1"}""");
        var sticky = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta");
        await Assert.That(sticky.Metadata!.RootElement.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
        var before = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/v2");
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/v1");
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();
        await Assert.That(meta.Metadata.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("v2");
        var after = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/v2");
        await Assert.That(after.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("v2");
        await Assert.That(RegistryNumber.Parse(after.Metadata.RootElement.GetProperty("epoch").GetRawText()).ToBigInteger())
            .IsGreaterThan(RegistryNumber.Parse(before.Metadata!.RootElement.GetProperty("epoch").GetRawText()).ToBigInteger());
    }

    [Test]
    public async Task AuthorizationCoversImplicitParentsAndEveryNestedEntityBeforeDocumentConsumption()
    {
        var engine = Create(policy: new DenyGroupPolicy());
        using var document = new MemoryStream([1, 2, 3]);
        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/teams/blocked/files/f"))
        {
            Document = document
        }, Writer()), "forbidden");
        await Assert.That(document.Position).IsEqualTo(0L);
        var root = await Send(engine, RegistryAction.Read, "/");
        await Assert.That(root.Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task ResponsePreparationFailureDoesNotCommit()
    {
        var engine = Create();
        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/teams/g"))
        {
            Metadata = RegistryJson.Parse("{}")
        }, new RegistryOperationContext(Writer().Caller)
        {
            PrepareResponseAsync = static (_, _) => throw new RegistryException(new("header_error", "", "Rejected pre-publication."))
        }), "header_error");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    internal static async Task ExpectCode(Func<ValueTask<RegistryResult>> action, string expected)
    {
        RegistryException? failure = null;
        try
        {
            await action();
        }
        catch (RegistryException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostic.Code).IsEqualTo(expected);
    }

    private sealed class DenyGroupPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(path.Kind != RegistryPathKind.Group || path.GroupId?.Value != "blocked");
    }
}
