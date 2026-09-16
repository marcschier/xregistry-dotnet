// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class CrossReferenceTests
{
    [Test]
    public async Task ImportedTypeIdentitySurvivesPersistenceButIndependentCopiesAreRejected()
    {
        const string model = """
            {"groups":{
              "first":{"singular":"one","resources":{"notes":{"singular":"note","hasdocument":false}}},
              "second":{"singular":"two","ximportresources":["/first/notes"]},
              "third":{"singular":"three","resources":{"notes":{"singular":"note","hasdocument":false}}}
            }}
            """;
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, store);
        await Send(engine, RegistryAction.Replace, "/first/g/notes/n", """{"name":"shared"}""");
        await Send(engine, RegistryAction.Replace, "/second/g/notes/alias", """{"meta":{"xref":"/first/g/notes/n"}}""");
        using var snapshot = await store.ReadSnapshotAsync();
        var frozen = snapshot.Find("$modelsource")!.Metadata.RootElement.GetProperty("compiledsource");
        await Assert.That(frozen.GetProperty("groups").GetProperty("second").GetProperty("attributes")
            .GetProperty("notesurl").GetProperty("immutable").GetBoolean()).IsTrue();
        var restarted = Create(model, store);
        await Assert.That((await Send(restarted, RegistryAction.Read, "/second/g/notes/alias")).Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("shared");
        await ExpectCode(() => Send(restarted, RegistryAction.Replace, "/third/g/notes/copy",
            """{"meta":{"xref":"/first/g/notes/n"}}"""), "malformed_xref");
        await ExpectCode(() => Send(restarted, RegistryAction.Read, "/second/g/notes/alias/versions/1", null,
            new KeyValuePair<string, string?>("doc", null)), "cannot_doc_xref");
    }

    [Test]
    public async Task CrossReferencesFollowOneHopOfTheSameActualType()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/target/notes/original", """{"name":"target name"}""");
        await Send(engine, RegistryAction.Replace, "/teams/source/notes/alias", """{"meta":{"xref":"/teams/target/notes/original"}}""");
        var alias = await Send(engine, RegistryAction.Read, "/teams/source/notes/alias", null, new KeyValuePair<string, string?>("inline", "meta,versions"));
        await Assert.That(alias.Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("target name");
        await Assert.That(alias.Metadata.RootElement.GetProperty("noteid").GetString()).IsEqualTo("alias");
        await Assert.That(alias.Metadata.RootElement.GetProperty("self").GetString()).IsEqualTo("https://registry.example/catalog/teams/source/notes/alias");
        await Assert.That(alias.Metadata.RootElement.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo("/teams/target/notes/original");
        await Assert.That(alias.Metadata.RootElement.GetProperty("versions").GetProperty("1").GetProperty("noteid").GetString()).IsEqualTo("alias");
        await Send(engine, RegistryAction.Replace, "/teams/source/notes/chain", """{"meta":{"xref":"/teams/source/notes/alias"}}""");
        var chain = await Send(engine, RegistryAction.Read, "/teams/source/notes/chain");
        await Assert.That(chain.Metadata!.RootElement.TryGetProperty("name", out _)).IsFalse();
        await Assert.That(chain.Metadata.RootElement.GetProperty("meta").GetProperty("xref").GetString()).IsEqualTo("/teams/source/notes/alias");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/source/notes/wrong", """{"meta":{"xref":"/teams/target/files/original"}}"""), "malformed_xref");
    }

    [Test]
    public async Task ClearingCrossReferenceCreatesFreshVersionWithoutResurrectingOldMetadata()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/target", """{"name":"target"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/source", """{"name":"must not reappear"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/source/meta", """{"xref":"/teams/g/notes/target"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/source/meta", """{"xref":null}""");
        var source = await Send(engine, RegistryAction.Read, "/teams/g/notes/source");
        await Assert.That(source.Metadata!.RootElement.TryGetProperty("name", out _)).IsFalse();
        await Assert.That(source.Metadata.RootElement.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/notes/source/meta");
        await Assert.That(meta.Metadata!.RootElement.TryGetProperty("xref", out _)).IsFalse();
        await Assert.That(meta.Metadata.RootElement.GetProperty("epoch").GetInt32()).IsGreaterThan(0);
    }
}
