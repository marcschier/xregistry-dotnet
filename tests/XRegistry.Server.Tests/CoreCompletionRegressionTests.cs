// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class CoreCompletionRegressionTests
{
    [Test]
    [Arguments("uri", false)]
    [Arguments("uri", true)]
    [Arguments("url", false)]
    [Arguments("url", true)]
    public async Task AbsoluteTargetWritesNeverRequireOrInvokeATargetValidator(string type, bool configured)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,
              "attributes":{"reference":{"type":"$TYPE","target":"/gs/rs/versions"}}}}}}}
            """.Replace("$TYPE", type, StringComparison.Ordinal)));
        var validator = new NoAbsoluteTargetValidation();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "targets",
            PublicRoot = new("https://registry.example/catalog"),
            Model = model,
            ObligationValidator = configured ? validator : null
        }, new InMemoryRegistryPersistence(), new PermitPolicy());

        var result = await Send(engine, RegistryAction.Replace, "/gs/g/rs/item",
            """{"reference":"https://outside.invalid/unrelated/path"}""");

        await Assert.That(result.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(result.Metadata!.RootElement.GetProperty("reference").GetString())
            .IsEqualTo("https://outside.invalid/unrelated/path");
        await Assert.That(validator.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(RegistryAction.Replace, "/teams/g/notes/alias/meta", "{}")]
    [Arguments(RegistryAction.Replace, "/teams/g/notes/alias", """{"meta":{}}""")]
    [Arguments(RegistryAction.Replace, "/teams/g/notes/alias", """{"meta":{"noteid":"alias"}}""")]
    [Arguments(RegistryAction.Post, "/teams/g/notes", """{"alias":{"meta":{}}}""")]
    [Arguments(RegistryAction.Replace, "/teams/g", """{"notes":{"alias":{"meta":{}}}}""")]
    [Arguments(RegistryAction.Replace, "/", """{"teams":{"g":{"notes":{"alias":{"meta":{}}}}}}""")]
    public async Task ExplicitMetaReplacementConvertsAnAliasAtEveryContainingRoute(RegistryAction action, string path, string body)
    {
        var engine = Create();
        await SeedAlias(engine);
        var target = (await Send(engine, RegistryAction.Read, "/teams/g/notes/target")).Metadata!.RootElement.GetRawText();
        await Send(engine, action, path, body);
        var meta = (await Send(engine, RegistryAction.Read, "/teams/g/notes/alias/meta")).Metadata!.RootElement;
        var resource = (await Send(engine, RegistryAction.Read, "/teams/g/notes/alias")).Metadata!.RootElement;

        await Assert.That(meta.TryGetProperty("xref", out _)).IsFalse();
        await Assert.That(resource.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        await Assert.That(resource.TryGetProperty("name", out _)).IsFalse();
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/target")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(target);
    }

    [Test]
    public async Task AliasConversionPreservesIncomingVersionAttributesWithoutRevivingOldOnes()
    {
        var engine = Create();
        await SeedAlias(engine);
        var result = await Send(engine, RegistryAction.Replace, "/teams/g/notes/alias",
            """{"meta":{},"name":"new local version"}""");
        await Assert.That(result.Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("new local version");
        await Assert.That(result.Metadata.RootElement.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task FailedContainingMutationRollsBackAliasConversionAndItsNewVersion()
    {
        var engine = Create();
        await SeedAlias(engine);
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/alias/meta")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/notes",
            """{"alias":{"meta":{}},"bad":{"undeclared":true}}"""), "unknown_attribute");

        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/alias/meta")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }

    [Test]
    public async Task ResourceWritesWithoutExplicitMetaCannotImplicitlyUnlinkAnAlias()
    {
        var engine = Create();
        await SeedAlias(engine);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g/notes/alias", """{"name":"not local"}"""),
            "extra_xref_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/alias/meta")).Metadata!.RootElement
            .GetProperty("xref").GetString()).IsEqualTo("/teams/g/notes/target");
    }

    private static async Task SeedAlias(RegistryEngine engine)
    {
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/target", """{"name":"target"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/alias", """{"name":"must not return"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/alias", """{"meta":{"xref":"/teams/g/notes/target"}}""");
    }

    private sealed class NoAbsoluteTargetValidation : IRegistryMetadataObligationValidator
    {
        internal int Calls { get; private set; }
        public ValueTask ValidateAsync(RegistryMetadataObligationContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("An absolute URI has no Core target obligation or acquisition authority.");
        }
    }
}
