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
    [Arguments("missing", "An inline path is unknown or is not an inlineable attribute.")]
    [Arguments("name", "The named attribute is not inlineable.")]
    [Arguments("teams.name", "The named attribute is not inlineable.")]
    public async Task InlineFailuresUseBadInlineAndRetainTheCompleteExpression(string expression, string detail)
    {
        var engine = Create();
        var failure = await CaptureFailure(async () =>
            await Send(engine, RegistryAction.Read, "/", null, new KeyValuePair<string, string?>("inline", expression)));

        await AssertDiagnostic(failure, "bad_inline", "/", detail,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["value"] = expression,
                ["error_detail"] = detail.TrimEnd('.')
            }));
    }

    [Test]
    [Arguments("limit", "1")]
    [Arguments("cursor", "opaque-arbitrary-token")]
    public async Task PaginationFlagsOnRootReadsUseNoncollectionDiagnostics(string flag, string value)
    {
        var engine = RegistryCapabilityTests.MutableEngine();
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"pagination":true}""");
        var failure = await CaptureFailure(async () =>
            await Send(engine, RegistryAction.Read, "/", null, new KeyValuePair<string, string?>(flag, value)));

        await AssertDiagnostic(failure, "pagination_noncollection", "/",
            "Can't use pagination on a non-collection result set.", "{}");
    }

    [Test]
    [Arguments("limit", "1", "bad_flag", "limit requires a positive UInt64 on an initial collection read.", """{"flag":"limit"}""")]
    [Arguments("cursor", "opaque-arbitrary-token", "bad_cursor",
        "A continuation must use its opaque URL without added or modified parameters.", "{}")]
    public async Task WriteOperationsKeepTheirPaginationFlagDiagnostics(
        string flag, string value, string code, string message, string arguments)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = RegistryCapabilityTests.MutableEngine(store);
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"pagination":true}""");
        long generation;
        using (var baseline = await store.ReadSnapshotAsync())
        {
            generation = baseline.Generation;
        }

        var eventCount = (await engine.ReadEventBatchesAsync(Writer())).Count;
        var failure = await CaptureFailure(async () =>
            await Send(engine, RegistryAction.Replace, "/teams/g", "{}", new KeyValuePair<string, string?>(flag, value)));

        await AssertDiagnostic(failure, code, "/teams/g", message, arguments);
        using var snapshot = await store.ReadSnapshotAsync();
        await Assert.That(snapshot.Generation).IsEqualTo(generation);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(eventCount);
    }

    [Test]
    public async Task DisabledPaginationAndInlineCapabilitiesSkipTheirValidation()
    {
        var engine = CreateMutable();
        await Send(engine, RegistryAction.Patch, "/capabilities", """{"flags":["filter"],"pagination":false}""");
        var baseline = await Send(engine, RegistryAction.Read, "/");
        var baselineJson = baseline.Metadata!.RootElement.GetRawText();

        foreach (var value in new[] { "1", "not-a-number" })
        {
            var result = await Send(engine, RegistryAction.Read, "/", null, new KeyValuePair<string, string?>("limit", value));
            await Assert.That(result.Metadata!.RootElement.GetRawText()).IsEqualTo(baselineJson);
        }

        foreach (var expression in new[] { "missing", "name" })
        {
            var result = await Send(engine, RegistryAction.Read, "/", null, new KeyValuePair<string, string?>("inline", expression));
            await Assert.That(result.Metadata!.RootElement.GetRawText()).IsEqualTo(baselineJson);
        }
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

    private static async Task<RegistryException> CaptureFailure(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (RegistryException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The operation was expected to fail.");
    }

    private static async Task AssertDiagnostic(RegistryException failure, string code, string path,
        string message, string arguments)
    {
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(path);
        await Assert.That(failure.Diagnostic.Message).IsEqualTo(message);

        var actual = failure.Data.Contains("xregistry.args")
            ? failure.Data["xregistry.args"] as IReadOnlyDictionary<string, string>
            : null;
        var expected = RegistryJson.Parse(arguments).RootElement.EnumerateObject()
            .Select(static property => property.Name + "=" + property.Value.GetString()).ToArray();
        await Assert.That(actual?.Select(static pair => pair.Key + "=" + pair.Value).ToArray() ?? [])
            .IsEquivalentTo(expected, StringComparer.Ordinal);
    }

    private static RegistryEngine CreateMutable() =>
        new(new RegistryEngineOptions
        {
            RegistryId = "test",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
            AllowAnonymousReads = true,
            AllowCapabilityUpdates = true
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
}
