using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class ProducerRepresentationTests
{
    [Test]
    public async Task DefaultProducerRootRequiresExplicitConfigurationInline()
    {
        var source = new RootSource();
        await using (var view = new ProducerRegistryView(source.Model, new Uri("https://view.example/registry"),
            "view", [source.Registration()]))
        {
            var result = await view.ReadAsync(RegistryPath.Parse("/"));
            await Assert.That(result.Metadata.TryGetProperty("model", out _)).IsFalse();
            await Assert.That(result.Metadata.TryGetProperty("modelsource", out _)).IsFalse();
            await Assert.That(result.Metadata.TryGetProperty("capabilities", out _)).IsFalse();
            await Assert.That(result.Metadata.GetProperty("registryid").GetString()).IsEqualTo("view");
            await Assert.That(result.Metadata.GetProperty("self").GetString()).IsEqualTo("https://view.example/registry");
            await Assert.That(result.Metadata.GetProperty("name").GetString()).IsEqualTo("Visible root");
            await Assert.That(result.Metadata.GetProperty("opaque").GetProperty("model").GetString()).IsEqualTo("domain model");
            await Assert.That(source.Requests).IsEquivalentTo(["Entity:/"], StringComparer.Ordinal);
        }

        await Assert.That(source.Closes).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false, "", "")]
    [Arguments(true, false, "", "")]
    [Arguments(false, false, "model", "model")]
    [Arguments(true, false, "model", "model")]
    [Arguments(false, false, "modelsource", "modelsource")]
    [Arguments(true, false, "modelsource", "modelsource")]
    [Arguments(false, false, "capabilities", "capabilities")]
    [Arguments(true, false, "capabilities", "capabilities")]
    [Arguments(false, false, "*", "")]
    [Arguments(true, false, "*", "")]
    [Arguments(false, true, "*", "")]
    [Arguments(true, true, "*", "")]
    [Arguments(false, false, "model,modelsource,capabilities", "model,modelsource,capabilities")]
    [Arguments(true, true, "model,modelsource,capabilities", "model,modelsource,capabilities")]
    public async Task ProducerRootInlineKeepsConfiguredValuesAndOneSourceCapture(
        bool producerOwned, bool doc, string inline, string expected)
    {
        var source = new RootSource(producerOwned);
        await using var view = new ProducerRegistryView(source.Model, new Uri("https://view.example/registry"),
            "view", [source.Registration()]);
        var result = await view.ReadAsync(RegistryPath.Parse("/"), new ProducerViewOptions
        {
            DocumentView = doc,
            Inline = inline.Length == 0 ? [] : inline.Split(',')
        });
        await Assert.That(result.Metadata.EnumerateObject().Select(static property => property.Name)
            .Where(static name => name is "model" or "modelsource" or "capabilities").ToArray())
            .IsEquivalentTo(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        if (result.Metadata.TryGetProperty("model", out var model))
        {
            await Assert.That(model.GetProperty("description").GetString()).IsEqualTo("Configured model");
            await Assert.That(model.GetProperty("attributes").GetProperty("registryid").GetProperty("type").GetString())
                .IsEqualTo("string");
        }
        if (result.Metadata.TryGetProperty("modelsource", out var declared))
        {
            await Assert.That(declared.GetProperty("description").GetString()).IsEqualTo("Configured model");
            await Assert.That(declared.GetProperty("attributes").TryGetProperty("registryid", out _)).IsFalse();
        }
        if (result.Metadata.TryGetProperty("capabilities", out var capabilities))
        {
            await Assert.That(capabilities.GetProperty("federation").GetProperty("resolution").GetString()).IsEqualTo("producer");
            await Assert.That(capabilities.GetProperty("available").GetProperty("entities").GetProperty("mutable").GetBoolean()).IsFalse();
        }

        await Assert.That(result.Metadata.GetProperty("self").GetString())
            .IsEqualTo(doc ? "#/" : "https://view.example/registry");
        var shallow = await view.ReadAsync(RegistryPath.Parse("/"));
        await Assert.That(shallow.Metadata.TryGetProperty("model", out _)).IsFalse();
        await Assert.That(shallow.Metadata.TryGetProperty("modelsource", out _)).IsFalse();
        await Assert.That(shallow.Metadata.TryGetProperty("capabilities", out _)).IsFalse();
        await Assert.That(source.Requests).IsEquivalentTo(["Entity:/"], StringComparer.Ordinal);
        await Assert.That(shallow.Metadata.GetProperty("opaque").GetProperty("model").GetString()).IsEqualTo("domain model");
    }

    [Test]
    public async Task ResolvedProducerAliasRequiresExplicitMetaInline()
    {
        var source = new AliasSource();
        await using var view = new ProducerRegistryView(source.Model, new Uri("https://view.example/registry"),
            "view", [source.Registration()]);
        var shallow = await view.ReadAsync(RegistryPath.Parse("/dirs/g/files/alias$details"));
        await Assert.That(shallow.Metadata.TryGetProperty("meta", out _)).IsFalse();
        await Assert.That(shallow.Metadata.GetProperty("fileid").GetString()).IsEqualTo("alias");
        await Assert.That(shallow.Metadata.GetProperty("name").GetString()).IsEqualTo("Target Version");
        await Assert.That(shallow.Metadata.GetProperty("metaurl").GetString())
            .IsEqualTo("https://view.example/registry/dirs/g/files/alias/meta");
        await Assert.That(source.Requests).IsEquivalentTo(
            ["Entity:/dirs/g/files/alias", "Entity:/dirs/g/files/target", "Entity:/dirs/g/files/target/meta",
             "Collection:/dirs/g/files/target/versions"], StringComparer.Ordinal);

        var explicitMeta = await view.ReadAsync(RegistryPath.Parse("/dirs/g/files/alias$details"),
            new ProducerViewOptions { Inline = ["meta"] });
        var meta = explicitMeta.Metadata.GetProperty("meta");
        await Assert.That(meta.GetProperty("xref").GetString()).IsEqualTo("/dirs/g/files/target");
        await Assert.That(meta.GetProperty("fileid").GetString()).IsEqualTo("alias");
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("v1");
        await Assert.That(source.Requests).IsEquivalentTo(
            ["Entity:/dirs/g/files/alias", "Entity:/dirs/g/files/target", "Entity:/dirs/g/files/target/meta",
             "Collection:/dirs/g/files/target/versions", "Entity:/dirs/g/files/alias/meta"], StringComparer.Ordinal);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DanglingProducerAliasKeepsItsIdentityMetaWithoutInline(bool doc)
    {
        var source = new AliasSource { Dangling = true };
        await using var view = new ProducerRegistryView(source.Model, new Uri("https://view.example/registry"),
            "view", [source.Registration()]);
        var result = await view.ReadAsync(RegistryPath.Parse("/dirs/g/files/alias$details"),
            new ProducerViewOptions { DocumentView = doc });
        await Assert.That(result.Metadata.EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["fileid", "self", "xid", "metaurl", "meta"], StringComparer.Ordinal);
        var meta = result.Metadata.GetProperty("meta");
        await Assert.That(meta.EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["fileid", "self", "xid", "xref"], StringComparer.Ordinal);
        await Assert.That(meta.GetProperty("xref").GetString()).IsEqualTo("/dirs/g/files/target");
        await Assert.That(meta.GetProperty("self").GetString())
            .IsEqualTo(doc ? "#/meta" : "https://view.example/registry/dirs/g/files/alias/meta");
        await Assert.That(source.Requests).IsEquivalentTo(doc
            ? ["Entity:/dirs/g/files/alias"]
            : ["Entity:/dirs/g/files/alias", "Entity:/dirs/g/files/target"], StringComparer.Ordinal);
    }

    [Test]
    public async Task CapturedProducerDefaultKeepsItsContextWithoutFetchingMetaOrVersions()
    {
        var source = new AliasSource { CapturedOnly = true };
        await using var view = new ProducerRegistryView(source.Model, new Uri("https://view.example/registry"),
            "view", [source.Registration()]);
        var first = await view.ReadAsync(RegistryPath.Parse("/dirs/g/files/target$details"));
        var second = await view.ReadAsync(RegistryPath.Parse("/dirs/g/files/target$details"));
        await Assert.That(first.Metadata.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(first.Metadata.GetProperty("epoch").GetInt32()).IsEqualTo(3);
        await Assert.That(first.Metadata.GetProperty("name").GetString()).IsEqualTo("Target Version");
        await Assert.That(first.Metadata.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        await Assert.That(first.Metadata.TryGetProperty("meta", out _)).IsFalse();
        await Assert.That(first.Metadata.GetProperty("self").GetString())
            .IsEqualTo("https://view.example/registry/dirs/g/files/target$details");
        await Assert.That(second.Metadata.GetRawText()).IsEqualTo(first.Metadata.GetRawText());
        await Assert.That(source.Requests).IsEquivalentTo(["Entity:/dirs/g/files/target"], StringComparer.Ordinal);
    }

    [Test]
    public async Task ProducerRootByteBudgetStillRejectsBeforeReturningAPartialView()
    {
        var source = new RootSource();
        var failure = await Assert.That(async () =>
        {
            await using var view = new ProducerRegistryView(source.Model, new Uri("https://view.example/registry"),
                "view", [source.Registration()], new FederationReadBudget(new(maxResultBytes: 32)));
            await view.ReadAsync(RegistryPath.Parse("/"));
        }).Throws<FederationException>();
        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(source.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task ProducerRepresentationAdmitsReadOnlyControlsButRejectsIncompatibleSourceModels()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"dirs":{"singular":"dir","resources":{"files":{
              "singular":"file","validateformat":true
            }}}}}
            """));
        var source = new RootSource();
        var failure = await Assert.That(async () =>
        {
            await using var view = new ProducerRegistryView(model, new Uri("https://view.example/registry"),
                "view", [source.Registration()]);
            await view.ReadAsync(RegistryPath.Parse("/"));
        }).Throws<FederationException>();
        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(source.Requests).IsEmpty();
        await Assert.That(source.Closes).IsEqualTo(1);
    }

    [Test]
    public async Task ProducerDocOmitsCapturedValidationReasonsWithoutRevalidatingOrChangingApiMetadata()
    {
        var source = new AliasSource { ValidationMetadata = true };
        await using var view = new ProducerRegistryView(source.Model, new Uri("https://view.example/registry"),
            "view", [source.Registration()]);
        var path = RegistryPath.Parse("/dirs/g/files/target/versions/v1$details");
        var api = await view.ReadAsync(path);
        await Assert.That(api.Metadata.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(api.Metadata.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(api.Metadata.GetProperty("formatvalidatedreason").GetString()).IsEqualTo("Captured format was not checked.");
        await Assert.That(api.Metadata.GetProperty("compatibilityvalidatedreason").GetString()).IsEqualTo("Captured compatibility was not checked.");
        var doc = await view.ReadAsync(path, new ProducerViewOptions { DocumentView = true });
        await Assert.That(doc.Metadata.TryGetProperty("formatvalidated", out _)).IsFalse();
        await Assert.That(doc.Metadata.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(doc.Metadata.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        await Assert.That(doc.Metadata.TryGetProperty("compatibilityvalidatedreason", out _)).IsFalse();
        await Assert.That(doc.Metadata.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That((await view.ReadAsync(path)).Metadata.GetRawText()).IsEqualTo(api.Metadata.GetRawText());
        await Assert.That(source.Requests).IsEquivalentTo(
            ["Entity:/dirs/g/files/target/versions/v1", "Entity:/dirs/g/files/target"], StringComparer.Ordinal);
    }

    private sealed class RootSource(bool producerOwned = false) : IFederationReadSource
    {
        internal List<string> Requests { get; } = [];
        internal int Closes { get; private set; }
        public NativeRegistryContext Context { get; } = new("http", "https://origin.example/root");
        public RegistryModel Model { get; } = RegistryModel.Compile(RegistryJson.Parse("""
            {"description":"Configured model","attributes":{"opaque":{"type":"any"}}}
            """));
        public JsonElement Capabilities => RegistryJson.Parse(producerOwned
            ? """{"federation":{"resolution":"producer"}}""" : "{}").RootElement;

        internal FederationSourceRegistration Registration() => new("origin", FederationRepresentation.ApiView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(this, () =>
            {
                Closes++;
                return ValueTask.CompletedTask;
            })));

        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.Operation + ":" + request.Target);
            if (request.Operation != FederationOperation.Entity || request.Target != "/")
            {
                throw new InvalidOperationException("Root representation must not fetch another source plane.");
            }

            return ValueTask.FromResult(FederationReadResult.FromMetadata("/", Context, RegistryJson.Parse("""
                {"registryid":"origin","specversion":"1.0-rc4","self":"https://origin.example/root",
                 "xid":"/","epoch":7,"name":"Visible root",
                 "createdat":"2026-09-12T00:00:00Z","modifiedat":"2026-09-12T00:00:00Z",
                 "model":{"captured":"source model"},"modelsource":{"captured":"source declaration"},
                 "capabilities":{"captured":"source capabilities"},"opaque":{"model":"domain model"}}
                """).RootElement));
        }
    }

    private sealed class AliasSource : IFederationReadSource
    {
        private readonly RegistryJson _target = RegistryJson.Parse("""
            {"fileid":"target","versionid":"v1","self":"https://origin.example/root/dirs/g/files/target$details",
             "xid":"/dirs/g/files/target","epoch":3,"name":"Target Version","ancestorid":"v1","isdefault":true,
             "createdat":"2026-09-12T00:00:00Z","modifiedat":"2026-09-12T00:00:00Z",
             "metaurl":"https://origin.example/root/dirs/g/files/target/meta",
             "versionsurl":"https://origin.example/root/dirs/g/files/target/versions","versionscount":1,
             "meta":{"fileid":"target","self":"https://origin.example/root/dirs/g/files/target/meta",
               "xid":"/dirs/g/files/target/meta","epoch":9,"readonly":false,"defaultversionsticky":false,
               "createdat":"2026-09-12T00:00:00Z","modifiedat":"2026-09-12T00:00:00Z",
               "defaultversionid":"v1","defaultversionurl":"https://origin.example/root/dirs/g/files/target/versions/v1$details"},
             "versions":{"v1":{"fileid":"target","versionid":"v1",
               "self":"https://origin.example/root/dirs/g/files/target/versions/v1$details",
               "xid":"/dirs/g/files/target/versions/v1","epoch":3,"name":"Target Version","ancestorid":"v1","isdefault":true,
               "createdat":"2026-09-12T00:00:00Z","modifiedat":"2026-09-12T00:00:00Z"}}}
            """);
        private readonly RegistryJson _alias = RegistryJson.Parse("""
            {"fileid":"alias","self":"https://origin.example/root/dirs/g/files/alias$details",
             "xid":"/dirs/g/files/alias","metaurl":"https://origin.example/root/dirs/g/files/alias/meta",
             "meta":{"fileid":"alias","self":"https://origin.example/root/dirs/g/files/alias/meta",
               "xid":"/dirs/g/files/alias/meta","xref":"/dirs/g/files/target"}}
            """);

        internal List<string> Requests { get; } = [];
        internal bool Dangling { get; init; }
        internal bool CapturedOnly { get; init; }
        internal bool ValidationMetadata { get; init; }
        public NativeRegistryContext Context { get; } = new("http", "https://origin.example/root");
        public RegistryModel Model { get; } = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"dirs":{"singular":"dir","resources":{"files":{"singular":"file"}}}}}
            """));
        public JsonElement Capabilities => RegistryJson.Parse(CapturedOnly || ValidationMetadata
            ? """{"federation":{"resolution":"producer"}}""" : "{}").RootElement;

        internal FederationSourceRegistration Registration() => new("origin",
            CapturedOnly ? FederationRepresentation.DocumentView : FederationRepresentation.ApiView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(this, static () => ValueTask.CompletedTask)));

        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.Operation + ":" + request.Target);
            if (CapturedOnly)
            {
                if (request.Operation != FederationOperation.Entity || request.Target != "/dirs/g/files/target")
                {
                    throw new InvalidOperationException("Only the captured producer Resource may be fetched.");
                }
                var capture = JsonNode.Parse(_target.RootElement.GetRawText())!.AsObject();
                foreach (var name in new[] { "versionid", "epoch", "name", "ancestorid", "isdefault", "createdat", "modifiedat" })
                {
                    capture.Remove(name);
                }
                return ValueTask.FromResult(FederationReadResult.FromMetadata(request.Target, Context,
                    RegistryJson.Parse(capture.ToJsonString()).RootElement));
            }
            if (Dangling && request.Target.StartsWith("/dirs/g/files/target", StringComparison.Ordinal))
            {
                throw new FederationException(FederationErrorCode.NotFound, "The selected source has no target.");
            }
            JsonElement metadata;
            if (request.Operation == FederationOperation.Entity)
            {
                metadata = request.Target switch
                {
                    "/dirs/g/files/alias" => _alias.RootElement,
                    "/dirs/g/files/alias/meta" => _alias.RootElement.GetProperty("meta"),
                    "/dirs/g/files/target" => _target.RootElement,
                    "/dirs/g/files/target/meta" => _target.RootElement.GetProperty("meta"),
                    "/dirs/g/files/target/versions/v1" => _target.RootElement.GetProperty("versions").GetProperty("v1"),
                    _ => throw new InvalidOperationException("No additional entity fetch belongs to this capture.")
                };
            }
            else if (request.Operation == FederationOperation.Collection && request.Target == "/dirs/g/files/target/versions")
            {
                metadata = RegistryJson.Parse(new JsonObject
                {
                    ["complete"] = true,
                    ["entities"] = JsonNode.Parse(_target.RootElement.GetProperty("versions").GetRawText())
                }.ToJsonString()).RootElement;
            }
            else
            {
                throw new InvalidOperationException("Representation selection must not fetch Documents or alternate sources.");
            }
            if (ValidationMetadata && request.Target == "/dirs/g/files/target/versions/v1")
            {
                var captured = JsonNode.Parse(metadata.GetRawText())!.AsObject();
                captured["formatvalidated"] = false;
                captured["formatvalidatedreason"] = "Captured format was not checked.";
                captured["compatibilityvalidated"] = false;
                captured["compatibilityvalidatedreason"] = "Captured compatibility was not checked.";
                metadata = RegistryJson.Parse(captured.ToJsonString()).RootElement;
            }
            return ValueTask.FromResult(FederationReadResult.FromMetadata(request.Target, Context, metadata));
        }
    }
}
