// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class RegistryErrorContractTests
{
    [Test]
    [Arguments(RegistryAction.Post, "/", """{"name":"wrong context"}""", "groups_only", "/", """{"name":"name"}""")]
    [Arguments(RegistryAction.Post, "/teams/g", """{"name":"wrong context"}""", "resources_only", "/teams/g", """{"name":"name"}""")]
    [Arguments(RegistryAction.Replace, "/teams/g/notes/n", """{"unknown":1}""", "unknown_attribute",
        "/teams/g/notes/n/versions/1", """{"name":"unknown"}""")]
    [Arguments(RegistryAction.Replace, "/teams/g/files/f$details", """{"file":"a","filebase64":"YQ=="}""", "one_resource",
        "/teams/g/files/f/versions/1", """{"list":"file, filebase64, fileurl"}""")]
    [Arguments(RegistryAction.Replace, "/teams/g/notes/n", """{"note":"not allowed"}""", "hasdocument_violation",
        "/teams/g/notes/n/versions/1", """{"plural":"notes"}""")]
    [Arguments(RegistryAction.Replace, "/teams/g/files/f$details", """{"filebase64":"not base64!"}""", "invalid_attribute",
        "/teams/g/files/f/versions/1", """{"name":"filebase64","error_detail":"The Document is not valid base64"}""")]
    [Arguments(RegistryAction.Replace, "/modelsource", """{"attributes":{"field":{"type":"string","default":"x"}}}""",
        "model_required_true", "/model", """{"name":"field"}""")]
    [Arguments(RegistryAction.Replace, "/modelsource", """{"attributes":{"field":{"type":"array","item":{"type":"string"},"default":[]}}}""",
        "model_scalar_default", "/model", """{"name":"field"}""")]
    public async Task EngineErrorsRetainTheCorrectSubjectAndRequiredCatalogArgumentsWithoutPublication(
        RegistryAction action, string path, string body, string code, string subject, string arguments)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        RegistryException? failure = null;
        try
        {
            await Send(engine, action, path, body);
        }
        catch (RegistryException exception)
        {
            failure = exception;
        }

        await Assert.That(failure?.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(failure!.Diagnostic.Path).IsEqualTo(subject);
        var actual = failure.Data["xregistry.args"] as IReadOnlyDictionary<string, string>;
        var expected = RegistryJson.Parse(arguments).RootElement.EnumerateObject().Select(Pair).ToArray();
        await Assert.That(actual?.Select(static pair => pair.Key + "=" + pair.Value).ToArray() ?? []).IsEquivalentTo(expected, StringComparer.Ordinal);
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Generation).IsEqualTo(0L);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
        static string Pair(JsonProperty property) => property.Name + "=" + property.Value.GetString();
    }

    [Test]
    public async Task MetaEpochFailuresIdentifyTheMetaEntityAndKeepExactLargeGuardValues()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        RegistryException? failure = null;
        try
        {
            await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"epoch":184467440737095516160001}""");
        }
        catch (RegistryException exception)
        {
            failure = exception;
        }

        await Assert.That(failure?.Diagnostic.Code).IsEqualTo("mismatched_epoch");
        await Assert.That(failure!.Diagnostic.Path).IsEqualTo("/teams/g/notes/n/meta");
        var arguments = (IReadOnlyDictionary<string, string>)failure.Data["xregistry.args"]!;
        await Assert.That(arguments["bad_epoch"]).IsEqualTo("184467440737095516160001");
        await Assert.That(arguments["epoch"]).IsEqualTo("0");
    }
}
