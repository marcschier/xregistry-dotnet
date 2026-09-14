using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class MessageMaterializationTraversalTests
{
    [Test]
    public async Task RecursiveOverridesReplaceArraysAndScalarsWithoutLosingDuplicatesOrNull()
    {
        var original = Definition("""
            {"messageid":"child","basemessage":"https://registry.example/base",
             "extension":{"nested":{"winner":"child"},"array":[1,1,2],"removed":null,"promoted":{"new":true}}}
            """, "https://registry.example/child");
        var requests = new List<string>();
        var result = await MessageDefinitionMaterializer.MaterializeAsync(original, (request, _) =>
        {
            requests.Add(request.TargetUri.AbsoluteUri);
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(request.TargetUri.AbsoluteUri switch
            {
                "https://registry.example/base" => Definition("""
                    {"messageid":"middle","basemessage":"https://registry.example/oldest",
                     "extension":{"nested":{"middle":2,"winner":"middle"}}}
                    """, "https://registry.example/base"),
                "https://registry.example/oldest" => Definition("""
                    {"messageid":"oldest","extension":{"oldest":1,"nested":{"kept":"oldest","winner":"oldest"},
                     "array":[9,8],"removed":{"old":true},"promoted":"old"}}
                    """, "https://registry.example/oldest"),
                _ => throw new InvalidOperationException("Unexpected source.")
            }));
        });
        var expected = JsonNode.Parse("""
            {"messageid":"child","basemessage":"https://registry.example/base",
             "extension":{"oldest":1,"nested":{"kept":"oldest","middle":2,"winner":"child"},
              "array":[1,1,2],"removed":null,"promoted":{"new":true}}}
            """);
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(result.Metadata.RootElement.GetRawText()), expected)).IsTrue();
        await Assert.That(requests.Count).IsEqualTo(2);
        await Assert.That(requests[0]).IsEqualTo("https://registry.example/base");
        await Assert.That(requests[1]).IsEqualTo("https://registry.example/oldest");
        await Assert.That(result.Definitions.Count).IsEqualTo(3);
        await Assert.That(result.PropertySources["/extension/nested/kept"].Location.AbsoluteUri).IsEqualTo("https://registry.example/oldest");
        await Assert.That(result.PropertySources["/extension/nested/middle"].Location.AbsoluteUri).IsEqualTo("https://registry.example/base");
        await Assert.That(result.PropertySources["/extension/array/1"].Location.AbsoluteUri).IsEqualTo("https://registry.example/child");
    }

    [Test]
    [Arguments("/messagegroups/shared/messages/base")]
    [Arguments("/messagegroups/shared/messages/base/versions/v2")]
    public async Task LocalMessageResourceAndVersionReferencesUseTheExplicitRegistryRoot(string reference)
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
        var original = new MessageDefinition(Json(reference), new Uri("https://registry.example/prefix/messagegroups/g/messages/child"),
            new Uri("https://registry.example/prefix"), model);
        var calls = 0;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(original, (request, _) =>
        {
            calls++;
            if (request.TargetUri.AbsoluteUri != "https://registry.example/prefix" + reference ||
                request.LocalPath?.EscapedPath != reference)
            {
                throw new InvalidOperationException("A local reference escaped its Registry context.");
            }
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(
                new(RegistryJson.Parse("""{"description":"inherited"}"""), request.TargetUri,
                    original.RegistryRoot, model)));
        });
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("inherited");
        await Assert.That(result.Metadata.RootElement.GetProperty("basemessage").GetString()).IsEqualTo(reference);
    }

    [Test]
    public async Task ImportedMessageTypesRemainValidLocalTargets()
    {
        var original = new MessageDefinition(Json("/endpoints/e/messages/base"),
            new Uri("https://registry.example/messagegroups/g/messages/child"),
            new Uri("https://registry.example/"), BuiltInRegistryModels.Compile(RegistryModelKind.CloudEvents));
        var calls = 0;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(original, (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("{}"), request.TargetUri)));
        });
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.IsComplete).IsTrue();
    }

    [Test]
    [Arguments("/")]
    [Arguments("/messagegroups/g")]
    [Arguments("/messagegroups/g/messages/base/meta")]
    [Arguments("/messagegroups/g/messages/base/versions")]
    [Arguments("/messagegroups/g/messages/base$details")]
    [Arguments("/messagegroups/g/messages/base/versions/request")]
    [Arguments("/schemagroups/g/schemas/base")]
    [Arguments("/unknown/g/messages/base")]
    [Arguments("//other.example/messagegroups/g/messages/base")]
    [Arguments("../base")]
    [Arguments("base")]
    [Arguments("#/messagegroups/g/messages/base")]
    public async Task InvalidLocalTargetsNeverReachTheAcquisitionCallback(string reference)
    {
        var calls = 0;
        var original = new MessageDefinition(Json(reference), new Uri("https://registry.example/child"),
            new Uri("https://registry.example/"), BuiltInRegistryModels.Compile(RegistryModelKind.CloudEvents));
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(original, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Invalid targets must be rejected before acquisition.");
        })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Diagnostic.Path).IsEqualTo("/basemessage");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("https://REGISTRY.example:443/messages/child")]
    [Arguments("https://registry.example/messages/./child")]
    [Arguments("https://registry.example/messages/%63hild")]
    [Arguments("https://registry.example/messages/other/../child")]
    public async Task UriEquivalentDirectCyclesAreRejectedBeforeAcquisition(string reference)
    {
        var calls = 0;
        var original = new MessageDefinition(Json(reference), new Uri("https://registry.example/messages/child"));
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(original, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("A direct URI-equivalent cycle must not be acquired.");
        })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_cycle");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ATransitiveCycleCannotReturnAPartialSuccess()
    {
        var calls = 0;
        var original = new MessageDefinition(Json("https://registry.example/base"), new Uri("https://registry.example/child"));
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(original, (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(
                new(Json("https://registry.example/child"), request.TargetUri)));
        })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_cycle");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ActualSourceIdentityDetectsAnAliasCycle()
    {
        var calls = 0;
        var original = new MessageDefinition(Json("https://registry.example/alias"), new Uri("https://registry.example/child"));
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(original, (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(
                new(RegistryJson.Parse("{}"), new Uri("https://registry.example/child"))));
        })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_cycle");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task DistinctFragmentsAreNotCollapsedByUriEquality()
    {
        var calls = 0;
        var original = new MessageDefinition(Json("https://registry.example/definitions#base"),
            new Uri("https://registry.example/definitions#child"));
        var result = await MessageDefinitionMaterializer.MaterializeAsync(original, (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("{}"), request.TargetUri)));
        });
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Definitions[1].Location.Fragment).IsEqualTo("#base");
    }

    [Test]
    [Arguments(MessageDefinitionSourceStatus.NotFound)]
    [Arguments(MessageDefinitionSourceStatus.Unavailable)]
    [Arguments(MessageDefinitionSourceStatus.AccessDenied)]
    public async Task UnavailableBasesRemainExplicitlyIncompleteWithoutFallback(MessageDefinitionSourceStatus status)
    {
        var calls = 0;
        var original = new MessageDefinition(Json("https://registry.example/base"), new Uri("https://registry.example/child"));
        var result = await MessageDefinitionMaterializer.MaterializeAsync(original, (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(MessageDefinitionSourceResult.Unresolved(status));
        });
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.UnresolvedStatus).IsEqualTo(status);
        await Assert.That(result.UnresolvedBase!.ReferenceText).IsEqualTo("https://registry.example/base");
        await Assert.That(result.Definitions.Count).IsEqualTo(1);
        await Assert.That(result.Metadata.RootElement.GetProperty("basemessage").GetString()).IsEqualTo("https://registry.example/base");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task APartiallyAvailableChainKeepsKnownOverlaysAndReportsItsMissingTail()
    {
        var calls = 0;
        var original = Definition("""{"basemessage":"https://registry.example/base","extension":{"child":true}}""",
            "https://registry.example/child");
        var result = await MessageDefinitionMaterializer.MaterializeAsync(original, (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(calls == 1
                ? MessageDefinitionSourceResult.Found(Definition("""
                    {"basemessage":"https://registry.example/missing","extension":{"base":true}}
                    """, request.TargetUri.AbsoluteUri))
                : MessageDefinitionSourceResult.Unresolved(MessageDefinitionSourceStatus.NotFound));
        });
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.Metadata.RootElement.GetProperty("extension").GetProperty("child").GetBoolean()).IsTrue();
        await Assert.That(result.Metadata.RootElement.GetProperty("extension").GetProperty("base").GetBoolean()).IsTrue();
        await Assert.That(result.UnresolvedBase!.TargetUri.AbsoluteUri).IsEqualTo("https://registry.example/missing");
        await Assert.That(result.Definitions.Count).IsEqualTo(2);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task NoConfiguredSourceNeverCausesImplicitNetworkOrFilesystemAcquisition()
    {
        foreach (var reference in new[] { "https://127.0.0.1:1/private", "file:///not-authorized/message.json" })
        {
            var result = await MessageDefinitionMaterializer.MaterializeAsync(
                new(Json(reference), new Uri("https://registry.example/child")));
            await Assert.That(result.IsComplete).IsFalse();
            await Assert.That(result.UnresolvedStatus).IsEqualTo(MessageDefinitionSourceStatus.SourceNotConfigured);
            await Assert.That(result.UnresolvedBase!.ReferenceText).IsEqualTo(reference);
        }
    }

    [Test]
    public async Task LegacySpellingAndMetadataClaimsDoNotAuthorizeAcquisition()
    {
        var calls = 0;
        var model = ExtensionModelWithLegacyField();
        var original = new MessageDefinition(RegistryJson.Parse("""
            {"basemessageuri":"https://must-not-be-read.example/base",
             "extension":{"authority":"admin","authorization":"not-a-credential-grant"}}
            """), new Uri("https://registry.example/child"), model: model);
        var result = await MessageDefinitionMaterializer.MaterializeAsync(original, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Only canonical basemessage can request acquisition.");
        });
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Metadata.RootElement.GetProperty("basemessageuri").GetString())
            .IsEqualTo("https://must-not-be-read.example/base");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task UnexpectedSourceFailuresPropagateWithoutASecondAttempt()
    {
        var failure = new IOException("Explicit source failed.");
        var calls = 0;
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(Json("https://registry.example/base"), new Uri("https://registry.example/child")), (_, _) =>
            {
                calls++;
                throw failure;
            })).Throws<IOException>();
        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task AcquiredAliasesKeepRequestedAndActualIdentityAndUseTheActualRegistryForFurtherBases()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
        var root = new MessageDefinition(Json("https://alias.example/base"), new Uri("https://derived.example/child"),
            new Uri("https://derived.example/"), model);
        var calls = 0;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(root, (request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(
                    Json("/messagegroups/g/messages/oldest"), new Uri("https://actual.example/prefix/messagegroups/g/messages/base"),
                    new Uri("https://actual.example/prefix"), model)));
            }
            if (request.TargetUri.AbsoluteUri != "https://actual.example/prefix/messagegroups/g/messages/oldest")
            {
                throw new InvalidOperationException("A returned base lost its Registry context.");
            }
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("{}"), request.TargetUri)));
        });
        await Assert.That(result.References[0].ReferenceText).IsEqualTo("https://alias.example/base");
        await Assert.That(result.References[0].TargetUri.AbsoluteUri).IsEqualTo("https://alias.example/base");
        await Assert.That(result.Definitions[1].Location.AbsoluteUri).IsEqualTo("https://actual.example/prefix/messagegroups/g/messages/base");
        await Assert.That(result.References[1].Source.Location.AbsoluteUri).IsEqualTo(result.Definitions[1].Location.AbsoluteUri);
        await Assert.That(result.References[1].ReferenceText).IsEqualTo("/messagegroups/g/messages/oldest");
        await Assert.That(result.Original.Location.AbsoluteUri).IsEqualTo("https://derived.example/child");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [Arguments("https://user@registry.example/base")]
    [Arguments("https://@registry.example/base")]
    public async Task EmbeddedUserInformationIsRejectedBeforeAcquisition(string reference)
    {
        var calls = 0;
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(Json(reference), new Uri("https://registry.example/child")), (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("A URI cannot carry acquisition credentials.");
            })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Diagnostic.Path).IsEqualTo("/basemessage");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task MetadataClaimsCannotOverrideTheSourceCallbacksReadAuthorization()
    {
        var model = MessageMaterializationTests.ExtensionModel();
        var root = new MessageDefinition(RegistryJson.Parse("""
            {"basemessage":"https://registry.example/private",
             "extension":{"authority":"admin","modelcompatiblewith":"trusted","authorization":"allow"}}
            """), new Uri("https://registry.example/child"), model: model);
        var calls = 0;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(root, (request, _) =>
        {
            calls++;
            if (request.TargetUri.AbsoluteUri != "https://registry.example/private")
            {
                throw new InvalidOperationException("Unexpected authorization target.");
            }
            return ValueTask.FromResult(MessageDefinitionSourceResult.Unresolved(MessageDefinitionSourceStatus.AccessDenied));
        });
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.UnresolvedStatus).IsEqualTo(MessageDefinitionSourceStatus.AccessDenied);
        await Assert.That(result.Metadata.RootElement.GetProperty("extension").GetProperty("authorization").GetString()).IsEqualTo("allow");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalReferencesRequireBothRegistryRootAndModelContext(bool provideRoot)
    {
        var root = new MessageDefinition(Json("/messagegroups/g/messages/base"), new Uri("https://registry.example/child"),
            provideRoot ? new Uri("https://registry.example/") : null,
            provideRoot ? null : BuiltInRegistryModels.Compile(RegistryModelKind.Message));
        var calls = 0;
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(root, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("A local XID cannot be resolved with guessed context.");
        })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_context_required");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedFoundMetadataDoesNotBecomeAnEmptyDefinition()
    {
        var calls = 0;
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(Json("https://registry.example/base"), new Uri("https://registry.example/child")), (request, _) =>
            {
                calls++;
                return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("[]"), request.TargetUri)));
            })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task SuppliedCoreFieldsStillUseCoreValidationInsteadOfReadonlyInputDiscarding()
    {
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(RegistryJson.Parse("""{"epoch":"not-an-integer"}"""), new Uri("https://registry.example/child"))))
            .Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Diagnostic.Path).IsEqualTo("/epoch");
    }

    [Test]
    public async Task CallerModelConstraintsAreNotReplacedWithPermissiveDefinitions()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Message);
        var node = JsonNode.Parse(source.RootElement.GetRawText())!.AsObject();
        node["groups"]!["messagegroups"]!["resources"]!["messages"]!["attributes"]!["name"] =
            new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("allowed") };
        var model = RegistryModel.Compile(RegistryJson.Parse(node.ToJsonString()));
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(RegistryJson.Parse("""{"name":"rejected"}"""), new Uri("https://registry.example/child"), model: model)))
            .Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Diagnostic.Path).IsEqualTo("/name");
    }

    [Test]
    [Arguments(false, "message_context_required")]
    [Arguments(true, "invalid_attribute")]
    public async Task InvalidDefinitionTypeContextIsRejectedBeforeReadingAnyBase(bool wrongPath, string expected)
    {
        var calls = 0;
        var definition = new MessageDefinition(Json("https://registry.example/base"), new Uri("https://registry.example/child"),
            model: BuiltInRegistryModels.Compile(wrongPath ? RegistryModelKind.CloudEvents : RegistryModelKind.Core),
            path: wrongPath ? RegistryPath.Parse("/schemagroups/g/schemas/s") : null);
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(definition, (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("{}"), request.TargetUri)));
        })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo(expected);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("?")]
    [Arguments("#")]
    public async Task RegistryRootContextRejectsEvenEmptyQueryAndFragmentDelimiters(string suffix)
    {
        var calls = 0;
        var root = new MessageDefinition(Json("/messagegroups/g/messages/base"), new Uri("https://registry.example/child"),
            new Uri("https://registry.example/prefix" + suffix), BuiltInRegistryModels.Compile(RegistryModelKind.Message));
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(root, (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("{}"), request.TargetUri)));
        })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(error.Diagnostic.Path).IsEqualTo("/basemessage");
        await Assert.That(calls).IsEqualTo(0);
    }

    internal static RegistryJson Json(string reference) =>
        RegistryJson.Parse(new JsonObject { ["basemessage"] = reference }.ToJsonString());

    private static MessageDefinition Definition(string json, string location) =>
        new(RegistryJson.Parse(json), new Uri(location), model: MessageMaterializationTests.ExtensionModel());

    private static RegistryModel ExtensionModelWithLegacyField()
    {
        using var packaged = BuiltInRegistryModels.LoadSource(RegistryModelKind.Message);
        var source = JsonNode.Parse(packaged.RootElement.GetRawText())!.AsObject();
        var attributes = source["groups"]!["messagegroups"]!["resources"]!["messages"]!["attributes"]!.AsObject();
        attributes["extension"] = new JsonObject { ["type"] = "any" };
        attributes["basemessageuri"] = new JsonObject { ["type"] = "uri" };
        return RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()));
    }
}
