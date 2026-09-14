using System.Net;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Federation;
using XRegistry.Samples;
using XRegistry.Samples.Bridge;

namespace XRegistry.Bridge.Tests;

public class BridgeHostTests
{
    [Test]
    public async Task RealDemoConfigurationUsesSharedSecurityAndServesFrozenMetadataOnlyAndDocumentViews()
    {
        var config = await BridgeConfiguration.LoadAsync(Path.Combine(BridgeFixture.Repository,
            "samples", "XRegistry.FederationBridge", "bridge.demo.json"));
        var hosting = config.Hosting with { ListenPort = 0 };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        RegistrySampleHosting.Configure(builder, hosting);
        await using var app = builder.Build();
        RegistrySampleHosting.UseSecurity(app, hosting);
        BridgeApplication.Map(app, config.Options, config.Sources, config.Mounts).AllowAnonymousRegistryReads();
        await app.StartAsync();
        using var client = BridgeFixture.Client(app);
        using var root = await client.GetAsync("/registry");
        await Assert.That(root.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(root)).GetProperty("registryid").GetString()).IsEqualTo("federation-bridge");
        using var metadataOnly = await client.GetAsync("/registry/categories/main/registries/site");
        var entity = await BridgeFixture.Json(metadataOnly);
        await Assert.That(metadataOnly.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(entity.GetProperty("registryid").GetString()).IsEqualTo("site");
        await Assert.That(entity.GetProperty("weburl").GetString()).IsEqualTo("https://example.com/cataloged-registry");
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("http://127.0.0.1:5091/registry/categories/main/registries/site");
        using var noteVersion = await client.GetAsync("/registry/documents/main/notes/item/versions/v1");
        var note = await BridgeFixture.Json(noteVersion);
        await Assert.That(noteVersion.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(note.GetProperty("noteid").GetString()).IsEqualTo("item");
        await Assert.That(note.GetProperty("self").GetString()).IsEqualTo("http://127.0.0.1:5091/registry/documents/main/notes/item/versions/v1");
        using var document = await client.GetAsync("/registry/documents/main/assets/item");
        await Assert.That(Convert.ToHexString(await document.Content.ReadAsByteArrayAsync())).IsEqualTo("7B2268656C6C6F223A22776F726C64227D0A");
        using var page = await client.GetAsync("/registry/documents/main/assets?filter=assetid%3Ditem&limit=1");
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(page)).TryGetProperty("item", out _)).IsTrue();
        await Assert.That(XRegistry.Client.RegistryHttpLink.Parse(page.Headers.GetValues("Link")).Any(link => link.HasRelation("last"))).IsTrue();
        using var invalidCredential = new HttpRequestMessage(HttpMethod.Get, "/registry");
        invalidCredential.Headers.Authorization = new("Bearer", "not-valid-in-explicit-demo");
        using var rejected = await client.SendAsync(invalidCredential);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ConfigurationDefersUpstreamCredentialsAndRejectsUnknownBindingsAndUnsafeFields()
    {
        var calls = 0;
        var path = Path.Combine(BridgeFixture.Repository, "samples", "XRegistry.FederationBridge", "bridge.production.example.json");
        var config = await BridgeConfiguration.LoadAsync(path, _ => { calls++; return null; });
        await Assert.That(config.Sources.Count).IsEqualTo(4);
        await Assert.That(config.Mounts.Count).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(0);
        using var temporary = new TemporaryBridgeConfiguration();
        foreach (var mutation in new[] { "unknown-binding", "native-api", "bad-environment", "unknown-setting", "too-many-sources", "zero-budget" })
        {
            var value = TemporaryBridgeConfiguration.Document();
            var source = value["sources"]![0]!.AsObject();
            switch (mutation)
            {
                case "unknown-binding": source["binding"] = "opcua"; break;
                case "native-api": source["view"] = "api"; break;
                case "bad-environment": source["credentialEnvironment"] = "SECRET;not-a-name"; break;
                case "unknown-setting": value["automaticCatalogDiscovery"] = true; break;
                case "too-many-sources":
                    for (var index = 0; index < 8; index++) { value["sources"]!.AsArray().Add(source.DeepClone()); }
                    break;
                case "zero-budget": value["limits"] = new JsonObject { ["maxBodyBytes"] = 0 }; break;
            }
            temporary.Write(value);
            await Assert.That(async () => await BridgeConfiguration.LoadAsync(temporary.Path)).Throws<ArgumentException>();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ReadDeadlineAndByteBudgetsFailBeforeSuccessAndDisposeTheOwnedSession()
    {
        var cancelled = false;
        var source = new ControlledSource("slow", "v1", ["shared"]);
        source.Override = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            throw new InvalidOperationException("A cancelled source must not return.");
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()],
            BridgeFixture.Options with { RequestTimeout = TimeSpan.FromMilliseconds(200) });
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/shared$details");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.GatewayTimeout);
        await Assert.That(cancelled).IsTrue();
        await Assert.That(source.Opens).IsEqualTo(1);
        await Assert.That(source.Closes).IsEqualTo(1);

        var large = new ControlledSource("large", "v1", ["shared"]);
        large.Override = (request, _) => request.Operation == FederationOperation.Document
            ? ValueTask.FromResult(FederationReadResult.FromDocument("/dirs/g/files/shared/versions/v1", large.Context,
                new FederationDocument(new byte[1025], "application/octet-stream")))
            : large.ReadDefault(request);
        await using var bounded = await BridgeFixture.StartAsync([large.Registration()], BridgeFixture.Options with { MaxResponseBytes = 1024 });
        using var boundedClient = BridgeFixture.Client(bounded);
        using var tooLarge = await boundedClient.GetAsync("/registry/dirs/g/files/shared");
        await Assert.That(tooLarge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That((await BridgeFixture.Json(tooLarge)).GetProperty("code").GetString()).IsEqualTo("too_large");
        await Assert.That(large.Opens).IsEqualTo(large.Closes);
    }

    [Test]
    public async Task AdmissionIsBoundedWithoutQueuingOrOverlappingNativeSessions()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ControlledSource("one", "v1", ["shared"]);
        var active = 0;
        var overlap = false;
        source.Override = async (request, token) =>
        {
            if (Interlocked.Increment(ref active) != 1) { overlap = true; }
            try
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return await source.ReadDefault(request);
            }
            finally { Interlocked.Decrement(ref active); }
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()],
            BridgeFixture.Options with { MaxConcurrentRequests = 1 });
        using var client = BridgeFixture.Client(app);
        var first = client.GetAsync("/registry/dirs/g/files/shared$details");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var busy = await client.GetAsync("/registry/dirs/g/files/shared$details");
        await Assert.That(busy.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(source.Opens).IsEqualTo(1);
        release.TrySetResult();
        using var completed = await first;
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(overlap).IsFalse();
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    public async Task NativeRunContractRejectsJitWhenNativeQualificationIsRequired()
    {
        if (Environment.GetEnvironmentVariable("XREGISTRY_BRIDGE_REQUIRE_NATIVE") == "1")
        {
            await Assert.That(RuntimeFeature.IsDynamicCodeSupported).IsFalse();
            await Assert.That(JitInfo.GetCompiledMethodCount()).IsEqualTo(0);
        }
        else
        {
            await Assert.That(JitInfo.GetCompiledMethodCount() > 0).IsTrue();
        }
    }
}

internal sealed class TemporaryBridgeConfiguration : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("xregistry-bridge-config-");
    internal string Path => System.IO.Path.Combine(_directory.FullName, "bridge.json");

    internal static JsonObject Document()
    {
        var original = System.IO.Path.Combine(BridgeFixture.Repository, "samples", "XRegistry.FederationBridge", "bridge.demo.json");
        var value = JsonNode.Parse(System.IO.File.ReadAllText(original))!.AsObject();
        value["modelFile"] = System.IO.Path.Combine(BridgeFixture.Repository, "samples", "XRegistry.FederationBridge", "bridge-model.json");
        var fixture = System.IO.Path.Combine(BridgeFixture.Repository, "tests", "Conformance", "Sources", "workingdrafts", "bindings", "samples", "mapping");
        value["sources"]![0]!["directory"] = fixture;
        value["sources"]![0]!["authorizedDirectory"] = fixture;
        return value;
    }

    internal void Write(JsonObject value) => System.IO.File.WriteAllText(Path, value.ToJsonString());
    public void Dispose() => _directory.Delete(recursive: true);
}
