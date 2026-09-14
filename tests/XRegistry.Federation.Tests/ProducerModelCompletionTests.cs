using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class ProducerModelCompletionTests
{
    private const string ConstraintModel = """
        {"groups":{"gs":{"singular":"g","attributes":{"expected":"string"},
          "constraints":{"rs.color":{"enum":["red","blue"],"default":"red","equals":"expected"}},
          "resources":{"rs":{"singular":"r","attributes":{"color":{"type":"string","required":true,"default":"blue"}}}}
        }}}
        """;

    private const string MatchingModel = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","attributes":{
          "payload":{"type":"object","attributes":{
            "number":{"type":"decimal","matchversions":true},
            "at":{"type":"timestamp","matchversions":true},
            "opaque":{"type":"any"}
          }}
        }}}}}}
        """;

    [Test]
    public async Task StaticNestedMatchVersionsUsesExactNumbersAndTimestampInstantsFromOneSelectedResource()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(MatchingModel));
        var first = new ProducerModelDataSource("first", model);
        first.AddGroup("g");
        first.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"payload":{"number":9007199254740993.000,"at":"2026-09-14T00:00:00.123456789+02:00","opaque":{"formatvalidated":true}}}""",
            ["v2"] = """{"payload":{"number":9007199254740993000e-3,"at":"2026-09-13T22:00:00.123456789000Z"}}""",
        });
        var later = new ProducerModelDataSource("later", model);
        later.AddGroup("g");
        later.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"payload":{"number":1,"at":"2020-01-01T00:00:00Z"}}""",
            ["v3"] = """{"payload":{"number":2,"at":"2021-01-01T00:00:00Z"}}""",
        });
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view",
            [first.Registration(), later.Registration()]);

        var result = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));

        var payload = result.Metadata.GetProperty("payload");
        await Assert.That(payload.GetProperty("number").GetRawText()).IsEqualTo("9007199254740993.000");
        await Assert.That(payload.GetProperty("at").GetString()).IsEqualTo("2026-09-13T22:00:00.123456789Z");
        await Assert.That(payload.GetProperty("opaque").GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(result.SelectedXid).IsEqualTo("/gs/g/rs/item/versions/v1");
        await Assert.That(first.Reads.Any(r => r.Operation == FederationOperation.Collection && r.Target == "/gs/g/rs/item/versions")).IsTrue();
        await Assert.That(later.Opens).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"payload":{"number":9007199254740992,"at":"2026-09-13T22:00:00.123456789Z"}}""")]
    [Arguments("""{"payload":{"number":9007199254740993,"at":"2026-09-13T22:00:00.123456788Z"}}""")]
    [Arguments("""{"payload":{"at":"2026-09-13T22:00:00.123456789Z"}}""")]
    public async Task MatchedVersionDifferencesNeverUseAnotherOriginOrACachedPartialSuccess(string secondVersion)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(MatchingModel));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"payload":{"number":9007199254740993,"at":"2026-09-13T22:00:00.123456789Z"}}""",
            ["v2"] = secondVersion,
        });
        var later = new ProducerModelDataSource("later", model);
        later.AddGroup("g");
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view",
            [source.Registration(), later.Registration()]);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.That(async () =>
            {
                await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
            }).Throws<FederationException>();
            await Assert.That(error!.Diagnostic).IsEqualTo("matchversions_failure");
        }
        await Assert.That(later.Opens).IsEqualTo(0);
        await Assert.That(source.Entities["/gs/g/rs/item/versions/v2"]["payload"]!.ToJsonString())
            .IsEqualTo(JsonNode.Parse(secondVersion)!["payload"]!.ToJsonString());
    }

    [Test]
    [Arguments("blue")]
    [Arguments("green")]
    public async Task GroupConstraintFailuresReturnNoResultAndDoNotMutateOrFallBack(string color)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ConstraintModel));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("g", """{"expected":"red"}""");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = new JsonObject { ["color"] = color }.ToJsonString(),
        });
        var original = source.Entities["/gs/g/rs/item/versions/v1"].ToJsonString();
        var later = new ProducerModelDataSource("later", model);
        later.AddGroup("g", """{"expected":"red"}""");
        later.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = """{"color":"red"}""" });
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view",
            [source.Registration(), later.Registration()]);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.That(async () =>
            {
                await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item$details"));
            }).Throws<FederationException>();
            await Assert.That(error!.Diagnostic).IsEqualTo("constraint_failure");
        }
        await Assert.That(source.Entities["/gs/g/rs/item/versions/v1"].ToJsonString()).IsEqualTo(original);
        await Assert.That(later.Opens).IsEqualTo(0);
    }

    [Test]
    [Arguments("/gs/g")]
    [Arguments("/gs/g/rs/item/versions")]
    public async Task PrivateMetadataNeededForModelChecksFailsClosed(string denied)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ConstraintModel));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("g", """{"expected":"red"}""");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = """{"color":"red"}""" });
        source.Denied.Add(denied);
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [source.Registration()]);

        var error = await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
        }).Throws<FederationException>();
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(source.Reads.Any(r => r.Operation == FederationOperation.Document)).IsFalse();
    }

    [Test]
    [Arguments("/gs/ref/rs/alias$details")]
    [Arguments("/gs/ref/rs/alias/versions/v1$details")]
    [Arguments("/gs/ref/rs/alias/versions")]
    [Arguments("/gs/ref/rs/alias/versions/v1")]
    public async Task ResolvedAliasesMustSatisfyTheirConsumerVisibleReferringGroup(string path)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ConstraintModel));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("target", """{"expected":"blue"}""");
        source.AddGroup("ref", """{"expected":"red"}""");
        source.AddResource("target", "real", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = """{"color":"blue"}""" });
        source.AddAlias("ref", "alias", "/gs/target/rs/real");
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [source.Registration()]);

        var error = await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse(path));
        }).Throws<FederationException>();
        await Assert.That(error!.Diagnostic).IsEqualTo("constraint_failure");
    }

    [Test]
    public async Task IncompleteSelectedVersionSetsNeverDischargeMatchedVersionRules()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(MatchingModel));
        var source = new ProducerModelDataSource("selected", model) { Complete = false };
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = """{"payload":{"number":1}}""" });
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [source.Registration()]);

        var error = await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
        }).Throws<FederationException>();
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
    }

    [Test]
    [Arguments("requests")]
    [Arguments("objects")]
    [Arguments("bytes")]
    [Arguments("work")]
    [Arguments("depth")]
    public async Task ModelPreparationCannotTurnAnExhaustedBudgetIntoSuccess(string dimension)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(MatchingModel));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"payload":{"number":1}}""",
            ["v2"] = """{"payload":{"number":1}}""",
        });
        var limits = new FederationReadLimits(
            maxRequests: dimension == "requests" ? 1 : 4096,
            maxObjects: dimension == "objects" ? 1 : 4096,
            maxTotalBytes: dimension == "bytes" ? 1 : 1_000_000,
            maxWork: dimension == "work" ? 1 : 1_000_000,
            maxDepth: dimension == "depth" ? 1 : 64);
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view",
            [source.Registration()], new FederationReadBudget(limits));
        var error = await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
        }).Throws<FederationException>();
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    [Test]
    public async Task ProducerOwnedCompleteCaptureChecksVersionsWithoutOpeningOrRetraversingOtherSources()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(MatchingModel));
        var source = new ProducerModelDataSource("producer", model)
        {
            ProducerOwned = true,
            Representation = FederationRepresentation.DocumentView,
            CaptureVersions = true,
        };
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"payload":{"number":1}}""",
            ["v2"] = """{"payload":{"number":1.0}}""",
        });
        var later = new ProducerModelDataSource("later", model);
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view",
            [source.Registration(), later.Registration()]);

        var result = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item$details"));

        await Assert.That(result.Metadata.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(source.Reads.Count).IsEqualTo(1);
        await Assert.That(later.Opens).IsEqualTo(0);
        await Assert.That(result.Dependencies.Any(d => d.Path.EscapedPath == "/gs/g/rs/item/versions/v2")).IsTrue();
    }

    [Test]
    public async Task AliasAdmissionChecksNondefaultVersionsInTheReferringGroup()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ConstraintModel));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("target");
        source.AddGroup("ref", """{"expected":"red"}""");
        source.AddResource("target", "real", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"color":"red"}""",
            ["v2"] = """{"color":"blue"}""",
        });
        source.AddAlias("ref", "alias", "/gs/target/rs/real");
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [source.Registration()]);

        var error = await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse("/gs/ref/rs/alias$details"));
        }).Throws<FederationException>();
        await Assert.That(error!.Diagnostic).IsEqualTo("constraint_failure");
    }

    [Test]
    [Arguments("""{"color":"red"}""")]
    [Arguments("{}")]
    public async Task ResourceConstraintsUseTheConsumerVisibleGroupAndItsDefaultWithoutMutatingSources(string attributes)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ConstraintModel));
        var first = new ProducerModelDataSource("first", model);
        first.AddGroup("g", """{"expected":"red"}""");
        var second = new ProducerModelDataSource("second", model);
        second.AddGroup("g", """{"expected":"blue"}""");
        second.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = attributes });
        var before = second.Entities["/gs/g/rs/item/versions/v1"].ToJsonString();
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view",
            [first.Registration(), second.Registration()]);

        var result = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));

        await Assert.That(result.Metadata.GetProperty("color").GetString()).IsEqualTo("red");
        await Assert.That(result.Metadata.GetProperty("rid").GetString()).IsEqualTo("item");
        await Assert.That(second.Entities["/gs/g/rs/item/versions/v1"].ToJsonString()).IsEqualTo(before);
        await Assert.That(result.Dependencies.Any(d => d.Source == "first" && d.Path.EscapedPath == "/gs/g")).IsTrue();
        await Assert.That(second.Reads.Any(r => r.Target == "/gs/g")).IsFalse();
        await Assert.That(result.Origins.Select(o => o.Name)).IsEquivalentTo(["first", "second"], StringComparer.Ordinal);
    }

    [Test]
    [Arguments("schemagroups", "schemagroup", "schemas", "schema")]
    [Arguments("assetgroups", "assetgroup", "assets", "asset")]
    public async Task ReadOnlyProducerAdmitsWriteValidationControlsAndPreservesTheDeclaredModel(
        string groups, string group, string resources, string resource)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(new JsonObject
        {
            ["groups"] = new JsonObject
            {
                [groups] = new JsonObject
                {
                    ["singular"] = group,
                    ["resources"] = new JsonObject
                    {
                        [resources] = new JsonObject
                        {
                            ["singular"] = resource,
                            ["validateformat"] = true,
                            ["validatecompatibility"] = true,
                        },
                    },
                },
            },
        }.ToJsonString()));
        var source = new ProducerModelTestSource(model);
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"),
            "view", [source.Registration()]);
        var result = await view.ReadAsync(RegistryPath.Parse("/"), new ProducerViewOptions { Inline = ["modelsource"] });

        await Assert.That(result.Metadata.GetProperty("registryid").GetString()).IsEqualTo("view");
        await Assert.That(result.Metadata.GetProperty(groups + "count").GetInt32()).IsEqualTo(0);
        var declared = result.Metadata.GetProperty("modelsource").GetProperty("groups").GetProperty(groups)
            .GetProperty("resources").GetProperty(resources);
        await Assert.That(declared.GetProperty("validateformat").GetBoolean()).IsTrue();
        await Assert.That(declared.GetProperty("validatecompatibility").GetBoolean()).IsTrue();
        await Assert.That(source.Reads.Count).IsEqualTo(1);
        await Assert.That(source.Reads[0]).IsEqualTo("Entity:/");
        await Assert.That(result.Origins[0].Name).IsEqualTo("origin");
    }
}

internal sealed class ProducerModelTestSource(RegistryModel model) : IFederationReadSource
{
    internal List<string> Reads { get; } = [];
    public RegistryModel Model { get; } = model;
    public NativeRegistryContext Context { get; } = new("http", "https://origin.test/registry");
    public JsonElement Capabilities => RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement;

    internal FederationSourceRegistration Registration() => new("origin", FederationRepresentation.ApiView,
        (_, _) => ValueTask.FromResult(new FederationSourceLease(this, static () => ValueTask.CompletedTask)));

    public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add(request.Operation + ":" + request.Target);
        if (request.Operation != FederationOperation.Entity || request.Target != "/")
        {
            throw new InvalidOperationException("The captured producer root must not retraverse another source plane.");
        }
        var root = new JsonObject
        {
            ["registryid"] = "origin",
            ["specversion"] = "1.0-rc4",
            ["epoch"] = 4,
            ["self"] = "https://origin.test/registry",
            ["xid"] = "/",
            ["createdat"] = "2026-09-14T00:00:00Z",
            ["modifiedat"] = "2026-09-14T00:00:00Z",
        };
        foreach (var name in Model.Groups.Keys)
        {
            root[name + "count"] = 0;
            root[name + "url"] = "https://origin.test/registry/" + name;
        }
        return ValueTask.FromResult(FederationReadResult.FromMetadata("/", Context, RegistryJson.Parse(root.ToJsonString()).RootElement));
    }
}
