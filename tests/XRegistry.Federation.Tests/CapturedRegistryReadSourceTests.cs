// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using XRegistry.Publication;

namespace XRegistry.Federation.Tests;

public class CapturedRegistryReadSourceTests
{
    [Test]
    [Arguments("local")]
    [Arguments("alias")]
    [Arguments("pair")]
    [Arguments("nested")]
    public async Task CapturedProjectionSourceGraphsRejectLocalAndTransitiveCycles(string cycle)
    {
        var (entry, identities) = cycle switch
        {
            "local" => await DependencyGraphFixture(configureFirst: root =>
                MarkGraphProjection(root, "https://graph.test/1.0.0.json")),
            "alias" => await DependencyGraphFixture(configureFirst: root =>
                MarkGraphProjection(root, "urn:graph:1")),
            "pair" => await DependencyGraphFixture(
                configureFirst: root => MarkGraphProjection(root, "urn:graph:2"),
                configureSecond: root => MarkGraphProjection(root, "urn:graph:1")),
            _ => await DependencyGraphFixture("""{"a":{"tm:ref":"urn:graph:2"}}""",
                configureSecond: root => MarkGraphProjection(root, "urn:graph:3"),
                configureThird: root => MarkGraphProjection(root, "urn:graph:2"))
        };
        var failure = await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                new Dictionary<Uri, FederationDocument>(), identities);
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected a projection-source cycle rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CapturedProjectionSourceGraphsPermitOrdinaryDependencyCycles()
    {
        var (entry, identities) = await DependencyGraphFixture(third: """{"back":{"tm:ref":"urn:graph:1"}}""",
            configureFirst: root => MarkGraphProjection(root, "urn:graph:2"),
            configureSecond: root => MarkGraphProjection(root, "urn:graph:3"));
        var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities);
        await Assert.That(graph.Nodes.Count).IsEqualTo(3);
        await Assert.That(graph.Edges.Count).IsEqualTo(3);
        await Assert.That(graph.Edges[0].Reference.Operator).IsEqualTo(WotJsonLdBaseResolver.ProjectionVocabulary + "projects");
        await Assert.That(graph.Edges[1].Reference.Operator).IsEqualTo(WotJsonLdBaseResolver.ProjectionVocabulary + "projects");
        await Assert.That(graph.Edges[2].Reference.Operator).IsEqualTo("tm:ref");
        await Assert.That(ReferenceEquals(graph.Edges[2].Target, graph.Entries[0])).IsTrue();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task CapturedProjectionSourceCycleChecksShareInclusiveWorkAndCancellation(int delta)
    {
        var (entry, identities) = await DependencyGraphFixture(
            configureFirst: root => MarkGraphProjection(root, "urn:graph:2"),
            configureSecond: root => MarkGraphProjection(root, "urn:graph:3"));
        var baseline = new FederationReadBudget();
        _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities, budget: baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () =>
            {
                _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                    new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
            }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected bounded projection-cycle checking.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
            await Assert.That(graph.Nodes.Count).IsEqualTo(3);
            await Assert.That(graph.Edges.Count).IsEqualTo(2);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                new Dictionary<Uri, FederationDocument>(), identities, cancellationToken: cancellation.Token);
        }).Throws<OperationCanceledException>();
    }

    private static void MarkGraphProjection(JsonNode root, string source)
    {
        root["@context"]!["p"] = WotJsonLdBaseResolver.ProjectionVocabulary;
        root["@context"]!["p:projects"] = new JsonObject
        {
            ["@id"] = WotJsonLdBaseResolver.ProjectionVocabulary + "projects", ["@container"] = "@list"
        };
        root["@type"] = new JsonArray("tm:ThingModel", "p:projection");
        root["p:projects"] = new JsonArray(new JsonObject
        {
            ["p:sourceName"] = "base", ["href"] = source, ["type"] = "application/tm+json", ["p:selectAll"] = true
        });
    }

    [Test]
    [Arguments("urn:test:line:a", null, null)]
    [Arguments("urn:test:line:a", "41", null)]
    [Arguments("urn:test:line:a", "40", null)]
    [Arguments("urn:test:line:a", "43", "unsatisfied")]
    [Arguments("urn:test:line:b", null, "unsatisfied")]
    [Arguments(null, null, "compatibility-required")]
    [Arguments(null, "41", "compatibility-required")]
    [Arguments("urn:test:line:a", "1.0.0", "invalid-version")]
    public async Task CapturedLocalIncrementalReferencesRequireTheirOwnLineAndUseTheContainingRegistryRevision(
        string? line, string? floor, string? expected)
    {
        var fixture = IncrementalFixture();
        var root = JsonNode.Parse(await Bytes(InventoryDocument(
            """{"a":{"tm:ref":"#/properties/value"},"value":{}}""", "[]")))!;
        root["@context"]!["uav"] = "http://opcfoundation.org/UA/WoT-Binding/";
        root["id"] = Fixture.WotId;
        root["uav:compatibilityLine"] = "urn:test:line:a";
        root["version"] = JsonNode.Parse("""{"model":"999"}""");
        if (line is not null) { root["properties"]!["a"]!["uav:compatibilityLine"] = line; }
        if (floor is not null) { root["properties"]!["a"]!["uav:minVersion"] = floor; }
        fixture.Documents[Fixture.Resource + "/versions/41"] = new(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var document = await WotRegistryResolver.ResolveAsync(fixture.Open(), Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "41"));
        var uri = new Uri("https://view.test/local.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var reference = (await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document.Document,
            uri, contexts)).Single();
        if (expected is not null)
        {
            var failure = await Assert.That(async () =>
            {
                _ = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(document, reference, uri, contexts);
            }).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected explicit local incremental requirements.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(expected);
        }
        else
        {
            var selected = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(document, reference, uri, contexts);
            await Assert.That(ReferenceEquals(selected.ContainingDocument, document)).IsTrue();
            await Assert.That(selected.ContainingDocument.Version.VersionId).IsEqualTo("41");
            await Assert.That(selected.ContainingDocument.Version.CompatibilityLine).IsEqualTo("urn:test:line:a");
        }
    }

    [Test]
    public async Task CapturedLocalReferencesTreatReboundFloorNamesAsAnnotations()
    {
        var (entry, _) = await DependencyGraphFixture(configureFirst: root =>
        {
            root["@context"]!["model"] = "https://www.w3.org/2019/wot/tm#";
            root["@context"]!["tm"] = "urn:vendor:";
            root["version"] = new JsonObject();
            root["properties"] = JsonNode.Parse("""
                {"a":{"model:ref":"#/properties/value","tm:minVersion":{"vendor":"annotation"}},"value":{}}
                """);
        });
        var contexts = new Dictionary<Uri, FederationDocument>();
        var reference = (await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(entry.Document.Document,
            entry.DocumentUri, contexts)).Single();
        var selected = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document, reference,
            entry.DocumentUri, contexts);
        await Assert.That(ReferenceEquals(selected.ContainingDocument, entry.Document)).IsTrue();
        await Assert.That(selected.Value.ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    [Arguments(false, "1.0.0", "1.0.0", null)]
    [Arguments(false, "2.0.0", "1.0.0", null)]
    [Arguments(false, "1.0.0", "2.0.0", "unsatisfied")]
    [Arguments(false, null, "1.0.0", "unsatisfied")]
    [Arguments(false, "bad", "1.0.0", "invalid-version")]
    [Arguments(false, "1.0.0", "bad", "invalid-version")]
    [Arguments(false, null, null, null)]
    [Arguments(true, "1.2.0", "1.1.0", null)]
    [Arguments(true, "2.0.0", "1.0.0", "unsatisfied")]
    [Arguments(true, "1.0.0", "2.0.0", "unsatisfied")]
    [Arguments(true, null, "1.0.0", "unsatisfied")]
    [Arguments(true, "bad", "1.0.0", "invalid-version")]
    [Arguments(true, null, null, null)]
    public async Task CapturedLocalReferencesUseAuthoredVersionsAndNeverSelectAnotherRevision(
        bool description, string? authoredVersion, string? floor, string? expectedError)
    {
        var (entry, identities) = await DependencyGraphFixture(configureFirst: root =>
        {
            root["@context"]!["model"] = "https://www.w3.org/2019/wot/tm#";
            root["@context"]!["minimum"] = "http://opcfoundation.org/UA/WoT-Binding/minVersion";
            root["version"] = authoredVersion is null ? new JsonObject() :
                new JsonObject { [description ? "instance" : "model"] = authoredVersion };
            root["properties"] = new JsonObject
            {
                ["a"] = new JsonObject
                {
                    [description ? "td:ref" : "model:ref"] = "#/properties/value",
                },
                ["value"] = new JsonObject { ["type"] = "number" }
            };
            if (floor is not null) { root["properties"]!["a"]!["minimum"] = floor; }
        }, description: description);
        var contexts = new Dictionary<Uri, FederationDocument>();
        var reference = (await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(entry.Document.Document,
            entry.DocumentUri, contexts)).Single();
        if (expectedError is not null)
        {
            var failure = await Assert.That(async () =>
            {
                _ = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document, reference,
                    entry.DocumentUri, contexts);
            }).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected authored local floor enforcement.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(expectedError);
            var graphFailure = await Assert.That(async () =>
            {
                _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry], contexts, identities);
            }).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected graph-local requirements.");
            await Assert.That(graphFailure.Diagnostic.Code).IsEqualTo(expectedError);
        }
        else
        {
            var selected = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document,
                reference, entry.DocumentUri, contexts);
            await Assert.That(ReferenceEquals(selected.ContainingDocument, entry.Document)).IsTrue();
            await Assert.That(selected.ContainingDocument.Version.VersionId).IsEqualTo("1.0.0");
            await Assert.That(selected.Value.GetProperty("type").GetString()).IsEqualTo("number");
            var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([entry], contexts, identities);
            await Assert.That(graph.Nodes.Count).IsEqualTo(1);
            await Assert.That(graph.Edges.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("competing", "invalid-floor")]
    [Arguments("nonstring", "invalid-version")]
    [Arguments("wrong-floor-kind", "invalid-floor")]
    [Arguments("wrong-reference-kind", "invalid-floor")]
    [Arguments("line", "invalid-line")]
    [Arguments("authored-kind", "invalid-version")]
    [Arguments("missing-version", "unsatisfied")]
    [Arguments("missing-fragment", "not-found")]
    public async Task CapturedLocalReferencesRejectMalformedOrWrongKindRequirements(string change, string expected)
    {
        var (entry, _) = await DependencyGraphFixture(configureFirst: root =>
        {
            root["@context"]!["uav"] = "http://opcfoundation.org/UA/WoT-Binding/";
            root["properties"] = JsonNode.Parse("""{"a":{"tm:ref":"#/properties/value"},"value":{}}""");
            var occurrence = root["properties"]!["a"]!;
            if (change == "competing")
            {
                occurrence["tm:minVersion"] = "1.0.0";
                occurrence["uav:minVersion"] = "1.0.0";
            }
            if (change == "nonstring") { occurrence["tm:minVersion"] = 1; }
            if (change == "wrong-floor-kind") { occurrence["td:minVersion"] = "1.0.0"; }
            if (change == "wrong-reference-kind")
            {
                occurrence.AsObject().Remove("tm:ref");
                occurrence["td:ref"] = "#/properties/value";
            }
            if (change == "line") { occurrence["uav:compatibilityLine"] = "other"; }
            if (change == "authored-kind")
            {
                occurrence["tm:minVersion"] = "1.0.0";
                root["version"]!["model"] = 1;
            }
            if (change == "missing-version")
            {
                occurrence["tm:minVersion"] = "1.0.0";
                root.AsObject().Remove("version");
            }
            if (change == "missing-fragment") { occurrence["tm:ref"] = "#/properties/missing"; }
        });
        var contexts = new Dictionary<Uri, FederationDocument>();
        var reference = (await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(entry.Document.Document,
            entry.DocumentUri, contexts)).Single();
        var failure = await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document, reference,
                entry.DocumentUri, contexts);
        }).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected local requirement diagnostics.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedLocalReferencesRejectForgedOccurrencesAndExternalTargets(bool external)
    {
        var (entry, _) = await DependencyGraphFixture("""{"a":{"tm:ref":"#/properties/a"}}""");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var reference = (await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(entry.Document.Document,
            entry.DocumentUri, contexts)).Single();
        reference = external ? reference with { Target = reference.Target with { IsDocumentLocal = false } } :
            reference with { Href = "#/properties/other" };
        var failure = await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document, reference,
                entry.DocumentUri, contexts);
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected revalidation of captured locality.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task CapturedLocalReferencesShareInclusiveWorkAndCancellation(int delta)
    {
        var (entry, _) = await DependencyGraphFixture(
            """{"a":{"tm:ref":"#/properties/value","tm:minVersion":"1.0.0"},"value":{}}""");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var reference = (await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(entry.Document.Document,
            entry.DocumentUri, contexts)).Single();
        var baseline = new FederationReadBudget();
        _ = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document, reference,
            entry.DocumentUri, contexts, baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () =>
            {
                _ = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document, reference,
                    entry.DocumentUri, contexts, budget);
            }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected shared local work limits.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var result = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document,
                reference, entry.DocumentUri, contexts, budget);
            await Assert.That(ReferenceEquals(result.ContainingDocument, entry.Document)).IsTrue();
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        await Assert.That(budget.Requests).IsEqualTo(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.ResolveCapturedLocalReferenceAsync(entry.Document, reference,
                entry.DocumentUri, contexts, cancellationToken: cancellation.Token);
        }).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CapturedDependencyGraphsRejectLiveProvidersBeforeTargetAcquisition()
    {
        var fixture = new Fixture();
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = InventoryDocument(
            """{"a":{"tm:ref":"urn:live-target"}}""", "[]");
        var source = fixture.Open();
        var live = new GraphLiveSource(source);
        var identities = new WotCapturedUriIdentityIndex([
            new(new("urn:live-target"), WotCapturedUriIdentityKind.DocumentView,
                new(live, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId, "2.0.0"))
        ]);
        var entry = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        var failure = await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.CaptureDependencyGraphAsync([new(entry, new("https://view.test/entry.json"))],
                new Dictionary<Uri, FederationDocument>(), identities);
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected retained-only graph traversal.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(live.Reads).IsEqualTo(0);
    }

    [Test]
    [Arguments("complete")]
    [Arguments("missing")]
    [Arguments("extra")]
    public async Task ClosureBindingComparisonUsesIndependentEntrySetsAndCoreXidIdentity(string change)
    {
        var (entry, identities) = await DependencyGraphFixture("""{"a":{"tm:ref":"urn:graph:2"}}""");
        var initial = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities);
        var second = initial.Nodes[1];
        var graph = await WotRegistryResolver.CaptureDependencyGraphAsync(
            [entry, new(second.Document, second.DocumentUri)], new Dictionary<Uri, FederationDocument>(), identities);
        var root = GraphClosure(("1.0.0", "/properties/a", "tm:ref", "urn:graph:2", "2.0.0"));
        root["entries"] = new JsonArray(Fixture.Resource + "/versions/2.0.0",
            Fixture.Resource.Replace("team", "%74eam", StringComparison.Ordinal) + "/versions/1.0.0");
        root["bindings"]![0]!["source"] = Fixture.Resource.Replace("team", "%74eam", StringComparison.Ordinal) +
            "/versions/1.0.0";
        root["bindings"]![0]!["target"] = Fixture.Resource.Replace("team", "%74eam", StringComparison.Ordinal) +
            "/versions/2.0.0";
        if (change == "missing") { root["entries"]!.AsArray().RemoveAt(0); }
        if (change == "extra") { root["entries"]!.AsArray().Add(Fixture.Resource + "/versions/3.0.0"); }
        var closure = WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));
        if (change == "complete")
        {
            closure.ValidateAgainstGraph(graph);
            await Assert.That(graph.Entries.Count).IsEqualTo(2);
            await Assert.That(graph.Nodes.Count).IsEqualTo(2);
            await Assert.That(graph.Edges.Count).IsEqualTo(1);
        }
        else
        {
            var failure = await Assert.That(() => closure.ValidateAgainstGraph(graph)).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected the independently supplied entry set.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        }
    }

    private sealed class GraphLiveSource(CapturedRegistryReadSource source) : IFederationReadSource
    {
        public NativeRegistryContext Context => source.Context;
        public JsonElement Capabilities => source.Capabilities;
        public RegistryModel? Model => source.Model;
        public int Reads { get; private set; }
        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return source.ReadAsync(request, cancellationToken);
        }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task CapturedDependencyGraphsBoundDistinctSourceContextsInclusively(int delta)
    {
        var (first, identities) = await DependencyGraphFixture();
        var (second, _) = await DependencyGraphFixture(registryId: "another");
        var budget = new FederationReadBudget(new(maxSources: 2 + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () =>
            {
                _ = await WotRegistryResolver.CaptureDependencyGraphAsync([first, second],
                    new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
            }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected distinct source accounting.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([first, second],
                new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
            await Assert.That(graph.Nodes.Count).IsEqualTo(2);
            await Assert.That(graph.Edges.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedDependencyGraphsRequireEntriesAndAbsoluteDocumentUris(bool relativeUri)
    {
        var (entry, identities) = await DependencyGraphFixture();
        var entries = relativeUri
            ? new[] { entry with { DocumentUri = new Uri("relative.json", UriKind.Relative) } }
            : Array.Empty<WotCapturedGraphEntry>();
        var failure = await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.CaptureDependencyGraphAsync(entries,
                new Dictionary<Uri, FederationDocument>(), identities);
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected independent absolute graph roots.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CapturedProjectionGraphsBindManifestSourcesRatherThanAffordanceSelectors()
    {
        var (entry, identities) = await DependencyGraphFixture(
            """{"selected":{"tm:ref":"urn:graph:2#/properties/value"}}""",
            """{"value":{}}""", configureFirst: root =>
            {
                root["@context"]!["p"] = WotJsonLdBaseResolver.ProjectionVocabulary;
                root["@context"]!["p:projects"] = new JsonObject
                {
                    ["@id"] = WotJsonLdBaseResolver.ProjectionVocabulary + "projects", ["@container"] = "@list"
                };
                root["@type"] = new JsonArray("tm:ThingModel", "p:projection");
                root["p:projects"] = JsonNode.Parse("""
                    [{"p:sourceName":"base","href":"urn:graph:2","type":"application/tm+json"}]
                    """);
            });
        var closureJson = GraphClosure(("1.0.0", "/p:projects/0",
            WotJsonLdBaseResolver.ProjectionVocabulary + "projects", "urn:graph:2", "2.0.0"));
        var closure = WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(closureJson.ToJsonString()));
        var graph = await closure.ValidateCapturedBindingsAsync([entry], new Dictionary<Uri, FederationDocument>(), identities);
        await Assert.That(graph.Nodes.Count).IsEqualTo(2);
        await Assert.That(graph.Edges.Count).IsEqualTo(1);
        await Assert.That(graph.Entries[0].Inventory.ProjectionSelectors.Count).IsEqualTo(1);
        await Assert.That(graph.Edges[0].Reference.SourcePointer).IsEqualTo("/p:projects/0");
        var extra = new JsonObject
        {
            ["source"] = Fixture.Resource + "/versions/1.0.0", ["pointer"] = "/properties/selected",
            ["operator"] = "tm:ref", ["href"] = "urn:graph:2#/properties/value",
            ["target"] = Fixture.Resource + "/versions/2.0.0"
        };
        closureJson["bindings"]!.AsArray().Add(extra);
        await Assert.That(() => WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(closureJson.ToJsonString()))
            .ValidateAgainstGraph(graph)).Throws<FederationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DependencyGraphEntriesRejectDuplicateOrContradictoryRevisions(bool contradictory)
    {
        var (entry, identities) = await DependencyGraphFixture();
        var other = contradictory ? (await DependencyGraphFixture("""{"different":{}}""")).Entry : entry;
        var failure = await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry, other],
                new Dictionary<Uri, FederationDocument>(), identities);
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected unambiguous exact entries.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ClosureBindingComparisonRejectsXidsThatCannotDistinguishCapturedRegistries()
    {
        var (first, identities) = await DependencyGraphFixture();
        var (second, _) = await DependencyGraphFixture(registryId: "another");
        var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([first, second],
            new Dictionary<Uri, FederationDocument>(), identities);
        await Assert.That(graph.Entries.Count).IsEqualTo(2);
        await Assert.That(graph.Nodes.Count).IsEqualTo(2);
        var closure = WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(GraphClosure().ToJsonString()));
        var failure = await Assert.That(() => closure.ValidateAgainstGraph(graph)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected exact closure XIDs to remain unambiguous.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CapturedGraphAndBindingValidationShareOneOperationBudget()
    {
        var (entry, identities) = await DependencyGraphFixture("""{"a":{"tm:ref":"urn:graph:2"}}""");
        var closure = WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(GraphClosure(
            ("1.0.0", "/properties/a", "tm:ref", "urn:graph:2", "2.0.0")).ToJsonString()));
        var baseline = new FederationReadBudget();
        var graph = await closure.ValidateCapturedBindingsAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities, budget: baseline);
        await Assert.That(graph.Edges.Count).IsEqualTo(1);
        foreach (var delta in new[] { -1, 0, 1 })
        {
            var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
            if (delta < 0)
            {
                var failure = await Assert.That(async () =>
                {
                    _ = await closure.ValidateCapturedBindingsAsync([entry],
                        new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
                }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected one shared operation budget.");
                await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            }
            else
            {
                _ = await closure.ValidateCapturedBindingsAsync([entry],
                    new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
                await Assert.That(budget.Work).IsEqualTo(baseline.Work);
            }
        }
    }

    [Test]
    public async Task CapturedGraphAggregateBytesIncludeRetainedDocumentsAndEveryOccurrence()
    {
        var (entry, identities) = await DependencyGraphFixture(
            """{"a":{"tm:ref":"urn:graph:2"},"b":{"tm:ref":"urn:graph:2"}}""");
        var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities);
        var bytes = graph.Nodes.Sum(node => node.Document.Document.Length +
            Encoding.UTF8.GetByteCount(node.VersionXid) + Encoding.UTF8.GetByteCount(node.DocumentUri.AbsoluteUri)) +
            graph.Edges.Sum(edge => (long)Encoding.UTF8.GetByteCount(edge.Reference.SourcePointer) +
                Encoding.UTF8.GetByteCount(edge.Reference.Operator) + Encoding.UTF8.GetByteCount(edge.Reference.Href) +
                Encoding.UTF8.GetByteCount(edge.Source.VersionXid) + Encoding.UTF8.GetByteCount(edge.Target.VersionXid));
        foreach (var delta in new[] { -1, 0, 1 })
        {
            var budget = new FederationReadBudget(new(maxResultBytes: checked((int)bytes + delta)));
            if (delta < 0)
            {
                var failure = await Assert.That(async () =>
                {
                    _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                        new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
                }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected aggregate graph size rejection.");
                await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            }
            else
            {
                var result = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                    new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
                await Assert.That(result.Edges.Count).IsEqualTo(2);
            }
        }
    }

    [Test]
    public async Task CapturedDependencyGraphsTraverseExactRevisionsAndKeepEveryOccurrenceAcrossCycles()
    {
        var (entry, identities) = await DependencyGraphFixture(
            """
            {"first":{"tm:ref":"urn:graph:2#/properties/value"},
             "again":{"tm:ref":"urn:graph:2#/properties/value"},"local":{"tm:ref":"#/properties/first"}}
            """,
            """{"value":{},"next":{"tm:ref":"urn:graph:3"}}""",
            """{"back":{"tm:ref":"urn:graph:1"}}""");
        var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities);
        await Assert.That(graph.Entries.Count).IsEqualTo(1);
        await Assert.That(graph.Nodes.Count).IsEqualTo(3);
        await Assert.That(graph.Edges.Count).IsEqualTo(4);
        await Assert.That(graph.Nodes[0].VersionXid).IsEqualTo(Fixture.Resource + "/versions/1.0.0");
        await Assert.That(graph.Nodes[1].VersionXid).IsEqualTo(Fixture.Resource + "/versions/2.0.0");
        await Assert.That(graph.Nodes[2].VersionXid).IsEqualTo(Fixture.Resource + "/versions/3.0.0");
        await Assert.That(graph.Edges[0].Reference.SourcePointer).IsEqualTo("/properties/first");
        await Assert.That(graph.Edges[1].Reference.SourcePointer).IsEqualTo("/properties/again");
        await Assert.That(ReferenceEquals(graph.Edges[0].Target, graph.Edges[1].Target)).IsTrue();
        await Assert.That(ReferenceEquals(graph.Edges[3].Target, graph.Entries[0])).IsTrue();
        await Assert.That(graph.Nodes[0].Document.Version.ContentDigest).IsEqualTo(entry.Document.Version.ContentDigest);
        var originalBytes = await Bytes(entry.Document.Document);
        var capturedBytes = await Bytes(graph.Nodes[0].Document.Document);
        await Assert.That(capturedBytes.SequenceEqual(originalBytes)).IsTrue();
        var closure = GraphClosure(
            ("1.0.0", "/properties/first", "tm:ref", "urn:graph:2#/properties/value", "2.0.0"),
            ("1.0.0", "/properties/again", "tm:ref", "urn:graph:2#/properties/value", "2.0.0"),
            ("2.0.0", "/properties/next", "tm:ref", "urn:graph:3", "3.0.0"),
            ("3.0.0", "/properties/back", "tm:ref", "urn:graph:1", "1.0.0"));
        WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(closure.ToJsonString())).ValidateAgainstGraph(graph);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("operator")]
    [Arguments("href")]
    [Arguments("decoded-href")]
    [Arguments("pointer-case")]
    [Arguments("pointer-fragment")]
    [Arguments("wrong-version")]
    [Arguments("local")]
    [Arguments("unreachable")]
    [Arguments("entry")]
    public async Task ClosureBindingsCannotReplaceIndependentOccurrenceOrTargetEvidence(string change)
    {
        var (entry, identities) = await DependencyGraphFixture(
            """{"a/b~c":{"tm:ref":"urn:graph:2#/properties/%76alue"},"local":{"tm:ref":"#/properties/a~1b~0c"}}""",
            """{"value":{}}""");
        var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities);
        var root = GraphClosure(("1.0.0", "/properties/a~1b~0c", "tm:ref",
            "urn:graph:2#/properties/%76alue", "2.0.0"));
        var binding = root["bindings"]![0]!;
        switch (change)
        {
            case "missing": root["bindings"] = new JsonArray(); break;
            case "operator": binding["operator"] = "td:ref"; break;
            case "href": binding["href"] = "urn:graph:2"; break;
            case "decoded-href": binding["href"] = "urn:graph:2#/properties/value"; break;
            case "pointer-case": binding["pointer"] = "/properties/A~1b~0c"; break;
            case "pointer-fragment": binding["pointer"] = "/properties/a%2Fb~0c"; break;
            case "wrong-version": binding["target"] = Fixture.Resource + "/versions/3.0.0"; break;
            case "local": binding["pointer"] = "/properties/local"; binding["href"] = "#/properties/a~1b~0c"; break;
            case "unreachable": binding["source"] = Fixture.Resource + "/versions/3.0.0"; break;
            case "entry": root["entries"] = new JsonArray(Fixture.Resource + "/versions/2.0.0"); break;
        }
        var closure = WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var failure = await Assert.That(() => closure.ValidateAgainstGraph(graph)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected independent binding evidence to reject altered closure.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CapturedDependencyGraphsDoNotIncludeSchemaOrDocumentLocalOccurrences()
    {
        var (entry, identities) = await DependencyGraphFixture(
            """{"schema":{"tm:ref":"urn:graph:schema#/definitions/value"},"local":{"tm:ref":"#/properties/schema"}}""");
        var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities);
        await Assert.That(graph.Nodes.Count).IsEqualTo(1);
        await Assert.That(graph.Edges.Count).IsEqualTo(0);
        WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(GraphClosure().ToJsonString())).ValidateAgainstGraph(graph);
        var extra = GraphClosure(("1.0.0", "/properties/schema", "tm:ref",
            "urn:graph:schema#/definitions/value", "2.0.0"));
        await Assert.That(() => WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(extra.ToJsonString()))
            .ValidateAgainstGraph(graph)).Throws<FederationException>();
    }

    [Test]
    [Arguments("missing-identity")]
    [Arguments("wrong-kind")]
    [Arguments("missing-fragment")]
    public async Task CapturedDependencyGraphsRejectUnresolvableTargetsWithoutPartialResults(string change)
    {
        var href = change switch
        {
            "missing-identity" => "urn:missing",
            "wrong-kind" => "urn:graph:schema",
            _ => "urn:graph:2#/properties/missing"
        };
        var properties = new JsonObject
        {
            ["first"] = new JsonObject { ["tm:ref"] = "urn:graph:2" },
            ["later"] = new JsonObject { [change == "wrong-kind" ? "td:ref" : "tm:ref"] = href }
        };
        var (entry, identities) = await DependencyGraphFixture(properties.ToJsonString());
        if (change == "missing-fragment")
        {
            var failure = await Assert.That(async () =>
            {
                _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                    new Dictionary<Uri, FederationDocument>(), identities);
            }).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected exact target-fragment failure.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("not-found");
        }
        else
        {
            await Assert.That(async () =>
            {
                _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                    new Dictionary<Uri, FederationDocument>(), identities);
            }).Throws<FederationException>();
        }
    }

    [Test]
    [Arguments("work", -1)]
    [Arguments("work", 0)]
    [Arguments("work", 1)]
    [Arguments("depth", -1)]
    [Arguments("depth", 0)]
    [Arguments("depth", 1)]
    [Arguments("requests", -1)]
    [Arguments("requests", 0)]
    [Arguments("requests", 1)]
    [Arguments("bytes", -1)]
    [Arguments("bytes", 0)]
    [Arguments("bytes", 1)]
    public async Task CapturedDependencyGraphTraversalHasInclusiveSharedLimits(string limit, int delta)
    {
        var (entry, identities) = await DependencyGraphFixture("""{"a":{"tm:ref":"urn:graph:2"}}""",
            """{"b":{"tm:ref":"urn:graph:3"}}""");
        async Task<WotCapturedDependencyGraph> Capture(FederationReadBudget budget) =>
            await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                new Dictionary<Uri, FederationDocument>(), identities, budget: budget);
        var baseline = new FederationReadBudget();
        _ = await Capture(baseline);
        var budget = new FederationReadBudget(new(
            maxWork: limit == "work" ? baseline.Work + delta : 1_000_000,
            maxHops: limit == "depth" ? 2 + delta : 16,
            maxRequests: limit == "requests" ? baseline.Requests + delta : 4096,
            maxTotalBytes: limit == "bytes" ? baseline.BytesRead + delta : 64 * 1024 * 1024));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => { _ = await Capture(budget); }).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected bounded graph traversal.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var result = await Capture(budget);
            await Assert.That(result.Nodes.Count).IsEqualTo(3);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task IndependentClosureBindingChecksUseInclusiveWorkAndCancellation(int delta)
    {
        var (entry, identities) = await DependencyGraphFixture("""{"a":{"tm:ref":"urn:graph:2"}}""");
        var graph = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
            new Dictionary<Uri, FederationDocument>(), identities);
        var closure = WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(GraphClosure(
            ("1.0.0", "/properties/a", "tm:ref", "urn:graph:2", "2.0.0")).ToJsonString()));
        var baseline = new FederationReadBudget();
        closure.ValidateAgainstGraph(graph, baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(() => closure.ValidateAgainstGraph(graph, budget)).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected closure comparison work limit.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            closure.ValidateAgainstGraph(graph, budget);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => closure.ValidateAgainstGraph(graph, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () =>
        {
            _ = await WotRegistryResolver.CaptureDependencyGraphAsync([entry],
                new Dictionary<Uri, FederationDocument>(), identities, cancellationToken: cancellation.Token);
        }).Throws<OperationCanceledException>();
    }

    private static async Task<(WotCapturedGraphEntry Entry, WotCapturedUriIdentityIndex Identities)> DependencyGraphFixture(
        string first = "{}", string second = "{}", string third = "{}",
        Action<JsonNode>? configureFirst = null, string registryId = "captured", bool description = false,
        Action<JsonNode>? configureSecond = null, Action<JsonNode>? configureThird = null)
    {
        var fixture = new Fixture();
        fixture.Root["registryid"] = registryId;
        if (description)
        {
            fixture.Root["modelsource"]!["groups"]!["sets"]!["resources"]!["things"]!["singular"] = "thingdescription";
            fixture.Model = RegistryModel.Compile(RegistryJson.Parse(fixture.Root["modelsource"]!.ToJsonString()));
        }
        var versions = fixture.Root["sets"]!["team"]!["things"]!["a"]!["versions"]!.AsObject();
        versions["3.0.0"] = versions["2.0.0"]!.DeepClone();
        versions["3.0.0"]!["versionid"] = "3.0.0";
        versions["3.0.0"]!["xid"] = Fixture.Resource + "/versions/3.0.0";
        versions["3.0.0"]!["self"] = "#" + Fixture.Resource + "/versions/3.0.0";
        fixture.Root["sets"]!["team"]!["things"]!["a"]!["versionscount"] = 3;
        var properties = new[] { first, second, third };
        for (var index = 0; index < properties.Length; index++)
        {
            var version = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0.0";
            var root = JsonNode.Parse(await Bytes(InventoryDocument(properties[index], "[]")))!;
            root["id"] = Fixture.WotId;
            root["version"] = new JsonObject { [description ? "instance" : "model"] = version };
            if (index == 0) { configureFirst?.Invoke(root); }
            if (index == 1) { configureSecond?.Invoke(root); }
            if (index == 2) { configureThird?.Invoke(root); }
            fixture.Documents[Fixture.Resource + "/versions/" + version] =
                new(Encoding.UTF8.GetBytes(root.ToJsonString()), documentBase: "https://graph.test/" + version + ".json");
        }
        var source = fixture.Open();
        var associations = new List<WotCapturedUriIdentity>();
        foreach (var version in versions)
        {
            associations.Add(new(new("urn:graph:" + version.Key[0]), WotCapturedUriIdentityKind.DocumentView,
                new(source, "/sets/team", Fixture.Collection, Fixture.Resource,
                    description ? "thingdescription" : "thingmodel", Fixture.WotId, version.Key)));
        }
        associations.Add(new(new("urn:graph:schema"), WotCapturedUriIdentityKind.DocumentView,
            new(InventorySource("schema"), "/sets/team", Fixture.Collection, Fixture.Resource, "schema", null)));
        var document = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, description ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel,
                description ? WotReferenceOperator.ThingDescription : WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        return (new(document, new("https://graph.test/1.0.0.json")), new(associations));
    }

    private static JsonObject GraphClosure(params (string Source, string Pointer, string Operator, string Href, string Target)[] bindings)
    {
        var values = new JsonArray();
        foreach (var binding in bindings)
        {
            values.Add(new JsonObject
            {
                ["source"] = Fixture.Resource + "/versions/" + binding.Source, ["pointer"] = binding.Pointer,
                ["operator"] = binding.Operator, ["href"] = binding.Href,
                ["target"] = Fixture.Resource + "/versions/" + binding.Target
            });
        }
        return new()
        {
            ["format"] = WotPublicationClosure.Format, ["entries"] = new JsonArray(Fixture.Resource + "/versions/1.0.0"),
            ["bindings"] = values
        };
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedProjectionBulkTiesUseOriginalNamesAndCategoryOrder(bool reverse)
    {
        var source = await SelectionSource(reverse
            ? """{"Alpha":{"const":"upper"},"alpha":{"const":"lower"}}"""
            : """{"alpha":{"const":"lower"},"Alpha":{"const":"upper"}}""", """{"z":{}}""", """{"a":{}}""");
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:selectAll"] = true;
        root["p:projects"]![0]!["p:namePrefix"] = "p";
        var selected = await SelectProjection(root, source);
        await Assert.That(selected.Count).IsEqualTo(3);
        await Assert.That(selected[0].Name).IsEqualTo("pAlpha");
        await Assert.That(selected[0].Definition.GetProperty("const").GetString()).IsEqualTo("upper");
        await Assert.That(selected[1].Category).IsEqualTo("actions");
        await Assert.That(selected[2].Category).IsEqualTo("events");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task CapturedProjectionMembershipBoundsAggregateSelectedDefinitionBytes(int delta)
    {
        var source = await SelectionSource(new JsonObject
        {
            ["large"] = new JsonObject { ["const"] = new string('x', 4096) }
        }.ToJsonString());
        var root = await SelectionRuleDocument();
        root["properties"] = JsonNode.Parse("""
            {"a":{"tm:ref":"urn:inventory:tm#/properties/large"},
             "b":{"tm:ref":"urn:inventory:tm#/properties/large"},
             "c":{"tm:ref":"urn:inventory:tm#/properties/large"}}
            """);
        var containing = await RetainProjectionPlan(root);
        var index = InventoryIndex(source);
        async Task<IReadOnlyList<WotProjectionAffordanceSelection>> Select(FederationReadBudget budget) =>
            await WotRegistryResolver.SelectCapturedProjectionAffordancesAsync(containing,
                new("https://example.test/plan.json"), new Dictionary<Uri, FederationDocument>(), index, budget: budget);
        var baseline = await Select(new());
        var size = baseline.Sum(selection => Encoding.UTF8.GetByteCount(selection.Definition.GetRawText()) +
            Encoding.UTF8.GetByteCount(selection.Name) + Encoding.UTF8.GetByteCount(selection.SourcePointer));
        var budget = new FederationReadBudget(new(maxResultBytes: size + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => { _ = await Select(budget); }).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected aggregate definition size rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            await Assert.That((await Select(budget)).Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task ProjectionSelectionMatchingBoundsAllInspectedTypesAndHonorsCancellation()
    {
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""[{"@type":"urn:test:A"}]""");
        var rule = (await ReadSelectionRules(root))[0];
        var types = new HashSet<string> { "urn:test:A", "urn:test:B" };
        var baseline = new FederationReadBudget();
        await Assert.That(rule.Matches("property", types, baseline)).IsTrue();
        foreach (var delta in new[] { -1, 0, 1 })
        {
            var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
            if (delta < 0)
            {
                var failure = await Assert.That(() => rule.Matches("property", types, budget)).Throws<FederationException>() ??
                    throw new InvalidOperationException("Expected predicate work exhaustion.");
                await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            }
            else
            {
                await Assert.That(rule.Matches("property", types, budget)).IsTrue();
                await Assert.That(budget.Work).IsEqualTo(baseline.Work);
            }
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => rule.Matches("property", types, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ProjectionRuleNamesUseEffectiveNamespacesAndRejectDuplicateFilterAliases()
    {
        var root = await SelectionRuleDocument();
        root["@context"]!["unrelated"] = "urn:vendor:";
        root["p:projects"]![0]!["unrelated:selectAll"] = "not-a-rule";
        var rule = (await ReadSelectionRules(root))[0];
        await Assert.That(rule.SelectAll).IsFalse();
        root["@context"]!["kind"] = "p:affordanceKind";
        root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""
            [{"kind":"property","p:affordanceKind":"property"}]
            """);
        await Assert.That(async () => { _ = await ReadSelectionRules(root); }).Throws<FederationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedProjectionMembershipRanksExplicitBeforeBulkAndLocalAfterImports(bool reverse)
    {
        var source = await SelectionSource("""
            {"same":{"const":"bulk"},"other":{"const":"explicit"},"local":{"const":"source"}}
            """);
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:selectAll"] = true;
        root["properties"] = JsonNode.Parse(reverse
            ? """{"local":{"const":"plan"},"same":{"tm:ref":"urn:inventory:tm#/properties/other"}}"""
            : """{"same":{"tm:ref":"urn:inventory:tm#/properties/other"},"local":{"const":"plan"}}""");
        var selected = await SelectProjection(root, source);
        await Assert.That(selected.Count).IsEqualTo(3);
        await Assert.That(selected.Single(value => value.Name == "same").Definition.GetProperty("const").GetString())
            .IsEqualTo("explicit");
        await Assert.That(selected.Single(value => value.Name == "same").Selector).IsNotNull();
        await Assert.That(selected.Single(value => value.Name == "local").Definition.GetProperty("const").GetString())
            .IsEqualTo("plan");
        await Assert.That(selected.Single(value => value.Name == "local").Source).IsNull();
        await Assert.That(selected.Single(value => value.Name == "other").SourcePointer).IsEqualTo("/properties/other");
        await Assert.That(selected.Single(value => value.Name == "other").Owner.Version.ContentDigest)
            .IsEqualTo(selected.Single(value => value.Name == "same").Owner.Version.ContentDigest);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedProjectionMembershipUsesManifestPriorityAndChecksDisplacedTargets(bool missing)
    {
        var first = await SelectionSource("""{"same":{"const":"first"}}""");
        var second = await SelectionSource("""{"other":{"const":"second"}}""");
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:selectAll"] = true;
        root["p:projects"]!.AsArray().Add(new JsonObject
        {
            ["p:sourceName"] = "second", ["href"] = "urn:source:second", ["type"] = "application/tm+json"
        });
        root["properties"] = JsonNode.Parse("""{"same":{"tm:ref":"urn:source:second#/properties/"""
            + (missing ? "missing" : "other") + "\"}}");
        var index = InventoryIndex(first);
        var identities = new WotCapturedUriIdentityIndex(new[]
        {
            new WotCapturedUriIdentity(new("urn:inventory:tm"), WotCapturedUriIdentityKind.DocumentView,
                index.Resolve(new("urn:inventory:tm"))),
            new WotCapturedUriIdentity(new("urn:source:second"), WotCapturedUriIdentityKind.DocumentView,
                new(second, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId))
        });
        var containing = await RetainProjectionPlan(root);
        async Task<IReadOnlyList<WotProjectionAffordanceSelection>> Select() =>
            await WotRegistryResolver.SelectCapturedProjectionAffordancesAsync(containing,
                new("https://example.test/plan.json"), new Dictionary<Uri, FederationDocument>(), identities);
        if (missing)
        {
            var failure = await Assert.That(async () => { _ = await Select(); }).Throws<RegistryException>() ??
                throw new InvalidOperationException("Expected even a displaced target to be checked.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("not-found");
        }
        else
        {
            var selected = await Select();
            await Assert.That(selected.Count).IsEqualTo(1);
            await Assert.That(selected[0].Definition.GetProperty("const").GetString()).IsEqualTo("first");
            await Assert.That(selected[0].Source!.Source.SourceName).IsEqualTo("base");
            await Assert.That(selected[0].Selector).IsNull();
        }
    }

    [Test]
    public async Task CapturedProjectionBulkNamesUseUnicodeCodePointsAndRetainOriginalPointers()
    {
        var source = await SelectionSource("""
            {"\ud801\udc28name":{"const":"supplementary"},"\ue000name":{"const":"bmp"},
             "a/b~c":{"const":"escaped"}}
            """);
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:selectAll"] = true;
        root["p:projects"]![0]!["p:namePrefix"] = "pre";
        var selected = await SelectProjection(root, source);
        await Assert.That(selected.Count).IsEqualTo(3);
        await Assert.That(selected[0].Name).IsEqualTo("preA/b~c");
        await Assert.That(selected[0].SourcePointer).IsEqualTo("/properties/a~1b~0c");
        await Assert.That(selected[1].Name).IsEqualTo("pre\ue000name");
        await Assert.That(selected[2].Name).IsEqualTo("pre\U00010400name");
        await Assert.That(selected[2].Definition.GetProperty("const").GetString()).IsEqualTo("supplementary");
    }

    [Test]
    public async Task CapturedProjectionPredicatesCompareExpandedSourceTypesAcrossAllCategories()
    {
        var source = await SelectionSource("""
            {"match":{"@type":["semantic:A","semantic:B"]},"partial":{"@type":"semantic:A"},
             "lexical":{"@type":"different:A"}}
            """, """{"action":{"@type":["semantic:A","semantic:B"]}}""",
            """{"alarm":{"@type":"semantic:Alarm"}}""");
        var root = await SelectionRuleDocument();
        root["@context"]!["tag"] = "urn:shared:";
        root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""
            [{"p:affordanceKind":"property","@type":["tag:A","tag:B"]},
             {"p:affordanceKind":"event","@type":"tag:Alarm"}]
            """);
        var selected = await SelectProjection(root, source);
        await Assert.That(selected.Count).IsEqualTo(2);
        await Assert.That(selected[0].Category).IsEqualTo("properties");
        await Assert.That(selected[0].Name).IsEqualTo("match");
        await Assert.That(selected[1].Category).IsEqualTo("events");
        await Assert.That(selected[1].Name).IsEqualTo("alarm");
    }

    [Test]
    public async Task CapturedProjectionSelectionChecksUnusedSourcesAndRejectsNestedPlansExplicitly()
    {
        var source = await SelectionSource("""{"a":{}}""");
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["tm:minVersion"] = "2.0.0";
        var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected all source constraints to apply.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
        root["p:projects"]![0]!.AsObject().Remove("tm:minVersion");
        var nestedRoot = await SelectionRuleDocument();
        nestedRoot["id"] = Fixture.WotId;
        nestedRoot["version"] = JsonNode.Parse("""{"model":"1.0.0"}""");
        var nestedFixture = new Fixture();
        nestedFixture.Root["sets"]!["team"]!["things"]!["a"]!["meta"]!["defaultversionsticky"] = true;
        nestedFixture.Documents[Fixture.Resource + "/versions/1.0.0"] =
            new(Encoding.UTF8.GetBytes(nestedRoot.ToJsonString()));
        var nested = await Assert.That(async () => { _ = await SelectProjection(root, nestedFixture.Open()); })
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected explicit nested-plan limitation.");
        await Assert.That(nested.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task CapturedProjectionMembershipSharesInclusiveWorkAndCancellation(int delta)
    {
        var source = await SelectionSource("""{"b":{},"a":{}}""");
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:selectAll"] = true;
        var containing = await RetainProjectionPlan(root);
        var index = InventoryIndex(source);
        async Task<IReadOnlyList<WotProjectionAffordanceSelection>> Select(FederationReadBudget budget,
            CancellationToken token = default) =>
            await WotRegistryResolver.SelectCapturedProjectionAffordancesAsync(containing,
                new("https://example.test/plan.json"), new Dictionary<Uri, FederationDocument>(), index,
                budget: budget, cancellationToken: token);
        var baseline = new FederationReadBudget();
        _ = await Select(baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => { _ = await Select(budget); }).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected shared selection budget.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var result = await Select(budget);
            await Assert.That(result.Count).IsEqualTo(2);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => { _ = await Select(new(), cancellation.Token); }).Throws<OperationCanceledException>();
    }

    private static async Task<CapturedRegistryReadSource> SelectionSource(string properties,
        string actions = "{}", string events = "{}", JsonNode? propertyContext = null)
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument(properties, "[]")))!;
        root["@context"]!["semantic"] = "urn:shared:";
        root["@context"]!["different"] = "urn:other:";
        if (propertyContext is not null)
        {
            root["@context"]!["properties"]!["@context"] = propertyContext.DeepClone();
        }
        root["@context"]!["actions"] = JsonNode.Parse("""{"@id":"td:hasActionAffordance","@container":"@index"}""");
        root["@context"]!["events"] = JsonNode.Parse("""{"@id":"td:hasEventAffordance","@container":"@index"}""");
        root["actions"] = JsonNode.Parse(actions);
        root["events"] = JsonNode.Parse(events);
        root["id"] = Fixture.WotId;
        root["version"] = JsonNode.Parse("""{"model":"1.0.0"}""");
        var fixture = new Fixture();
        fixture.Root["sets"]!["team"]!["things"]!["a"]!["meta"]!["defaultversionsticky"] = true;
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new(Encoding.UTF8.GetBytes(root.ToJsonString()));
        return fixture.Open();
    }

    private static async Task<IReadOnlyList<WotProjectionAffordanceSelection>> SelectProjection(JsonNode root,
        CapturedRegistryReadSource source, IReadOnlyDictionary<Uri, FederationDocument>? contexts = null,
        FederationReadBudget? budget = null, CancellationToken token = default) =>
        await WotRegistryResolver.SelectCapturedProjectionAffordancesAsync(await RetainProjectionPlan(root),
            new("https://example.test/plan.json"), contexts ?? new Dictionary<Uri, FederationDocument>(),
            InventoryIndex(source), budget: budget, cancellationToken: token);

    [Test]
    public async Task ProjectionEnvelopeReaderRetainsTheAbsoluteScenarioAndOrdinaryKind()
    {
        var root = await SelectionRuleDocument();
        var envelope = await WotJsonLdBaseResolver.ReadProjectionPlanEnvelopeAsync(
            new(Encoding.UTF8.GetBytes(root.ToJsonString())), new("https://example.test/plan.json"),
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(envelope.Kind).IsEqualTo(WotReferenceTargetKind.ThingModel);
        await Assert.That(envelope.ScenarioIri).IsEqualTo("urn:example:scenario");
    }

    [Test]
    [Arguments("missing-marker")]
    [Arguments("missing-kind")]
    [Arguments("multiple-kinds")]
    [Arguments("relative-id")]
    [Arguments("missing-scenario")]
    [Arguments("relative-scenario")]
    [Arguments("literal-scenario")]
    public async Task ProjectionEnvelopeReaderRejectsMissingOrInvalidRequiredIdentity(string invalidField)
    {
        var root = await SelectionRuleDocument();
        switch (invalidField)
        {
            case "missing-marker":
                root["@type"] = new JsonArray("tm:ThingModel");
                break;
            case "missing-kind":
                root["@type"] = new JsonArray("p:projection");
                break;
            case "multiple-kinds":
                root["@type"] = new JsonArray("tm:ThingModel", "Thing", "p:projection");
                break;
            case "relative-id":
                root["id"] = "relative";
                break;
            case "missing-scenario":
                root.AsObject().Remove("p:scenario");
                break;
            case "relative-scenario":
                root["p:scenario"] = "relative";
                break;
            case "literal-scenario":
                root["@context"]!["p:scenario"] = WotJsonLdBaseResolver.ProjectionVocabulary + "scenario";
                break;
        }
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.ReadProjectionPlanEnvelopeAsync(document,
                new("https://example.test/plan.json"), new Dictionary<Uri, FederationDocument>()))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected invalid projection envelope.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ProjectionSelectionRulesRetainDefaultsAndDoNotImplicitlySelectSources()
    {
        var rules = await ReadSelectionRules(await SelectionRuleDocument());
        await Assert.That(rules.Count).IsEqualTo(1);
        await Assert.That(rules[0].Source.SourceName).IsEqualTo("base");
        await Assert.That(rules[0].Routing).IsEqualTo("source");
        await Assert.That(rules[0].NamePrefix).IsNull();
        await Assert.That(rules[0].Filters.Count).IsEqualTo(0);
        await Assert.That(rules[0].Matches("property", new HashSet<string>())).IsFalse();
    }

    [Test]
    public async Task CapturedProjectionRejectsAnEmptyResolvedSelectionSet()
    {
        var source = await SelectionSource("""{"notSelected":{}}""");
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""[{"@type":"urn:example:unmatched"}]""");
        var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected empty projection rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("source", false, false, false, true)]
    [Arguments("source", true, false, false, false)]
    [Arguments("source", false, true, false, false)]
    [Arguments("projection", true, true, false, true)]
    [Arguments("projection", true, true, true, false)]
    [Arguments("projection", false, true, false, false)]
    [Arguments("projection", true, false, false, false)]
    public async Task ProjectionSelectionEnforcesFormAndSecurityRouting(string routing,
        bool hasForms, bool hasSecurity, bool duplicateForms, bool valid)
    {
        var source = await SelectionSource("""{"a":{}}""");
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:routing"] = routing;
        root["@context"]!["forms"] = JsonNode.Parse("""{"@id":"td:hasForm"}""");
        root["@context"]!["security"] = JsonNode.Parse("""{"@id":"td:hasSecurityConfiguration"}""");
        if (duplicateForms) { root["@context"]!["alternateForms"] = "td:hasForm"; }
        var selector = new JsonObject { ["tm:ref"] = "urn:inventory:tm#/properties/a" };
        if (hasForms) { selector["forms"] = new JsonArray(); }
        if (duplicateForms) { selector["alternateForms"] = new JsonArray(); }
        if (hasSecurity) { selector["security"] = "nosec"; }
        root["properties"] = new JsonObject { ["selected"] = selector };

        if (valid)
        {
            var selections = await SelectProjection(root, source);
            await Assert.That(selections.Single().Name).IsEqualTo("selected");
            if (routing == "projection")
            {
                await Assert.That(selections.Single().Annotations.Count).IsEqualTo(2);
                await Assert.That(selections.Single().Annotations.Any(annotation =>
                    annotation.Iri == "https://www.w3.org/2019/wot/td#hasForm")).IsTrue();
                await Assert.That(selections.Single().Annotations.Any(annotation =>
                    annotation.Iri == "https://www.w3.org/2019/wot/td#hasSecurityConfiguration")).IsTrue();
            }
        }
        else
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected routing annotation rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        }
    }

    [Test]
    public async Task ProjectionSelectionRetainsPermittedAnnotationsInThePlanContext()
    {
        var source = await SelectionSource("""{"a":{}}""");
        var root = await SelectionRuleDocument();
        root["@context"]!["title"] = "td:title";
        root["@context"]!["description"] = "td:description";
        root["@context"]!["domain"] = "https://example.test/types/";
        root["@context"]!["vendor"] = JsonNode.Parse(
            """{"@id":"https://example.test/vendor/","@prefix":true}""");
        root["properties"] = new JsonObject
        {
            ["selected"] = new JsonObject
            {
                ["tm:ref"] = "urn:inventory:tm#/properties/a",
                ["title"] = "Selected title",
                ["description"] = "Selected description",
                ["@type"] = "domain:ConditionSignal",
                ["vendor:classification"] = JsonNode.Parse("""{"level":"restricted"}""")
            }
        };

        var selection = (await SelectProjection(root, source)).Single();
        await Assert.That(selection.Annotations.Count).IsEqualTo(4);
        await Assert.That(selection.Annotations.Single(annotation =>
            annotation.Iri == "https://www.w3.org/2019/wot/td#title").Value.GetString())
            .IsEqualTo("Selected title");
        await Assert.That(selection.Annotations.Single(annotation =>
            annotation.Iri == "https://www.w3.org/2019/wot/td#title").VendorPrefixIri).IsNull();
        await Assert.That(selection.Annotations.Single(annotation =>
            annotation.Iri == "https://www.w3.org/2019/wot/td#description").Value.GetString())
            .IsEqualTo("Selected description");
        await Assert.That(selection.Annotations.Single(annotation => annotation.Iri == "@type").Value.GetString())
            .IsEqualTo("domain:ConditionSignal");
        await Assert.That(selection.Annotations.Single(annotation =>
            annotation.Iri == "https://example.test/vendor/classification").Value.GetProperty("level").GetString())
            .IsEqualTo("restricted");
        await Assert.That(selection.Annotations.Single(annotation =>
            annotation.Iri == "https://example.test/vendor/classification").VendorPrefixIri)
            .IsEqualTo("https://example.test/vendor/");
    }

    [Test]
    [Arguments("absent", false)]
    [Arguments("identical", false)]
    [Arguments("equivalent", false)]
    [Arguments("different", true)]
    [Arguments("local-match", false)]
    [Arguments("local-conflict", true)]
    [Arguments("local-disabled", false)]
    [Arguments("local-reset", false)]
    [Arguments("authored-identical", false)]
    [Arguments("authored-conflict", true)]
    public async Task ProjectionSelectionChecksVendorPrefixAgainstTheSourceEffectiveContext(
        string binding, bool conflict)
    {
        var properties = binding switch
        {
            "local-match" => """{"a":{"@context":{"semantic":"urn:plan:"},"const":"source"}}""",
            "local-conflict" => """{"a":{"@context":{"semantic":"urn:other:"},"const":"source"}}""",
            "local-disabled" => """{"a":{"@context":{"semantic":{"@id":"urn:other:","@prefix":false}},"const":"source"}}""",
            "local-reset" => """{"a":{"@context":null,"const":"source"}}""",
            "authored-identical" or "authored-conflict" => """{"a":{"semantic":"original","const":"source"}}""",
            _ => """{"a":{"const":"source"}}"""
        };
        var source = await SelectionSource(properties);
        var root = await SelectionRuleDocument();
        var prefix = binding == "absent" ? "vendor" : "semantic";
        var planNamespace = binding switch
        {
            "absent" => "https://example.test/vendor/",
            "different" or "local-match" or "local-disabled" or "local-reset" or "authored-conflict" => "urn:plan:",
            _ => "urn:shared:"
        };
        root["@context"]![prefix] = binding == "equivalent"
            ? JsonNode.Parse("""{"@id":"urn:shared:","@prefix":true}""")
            : JsonValue.Create(planNamespace);
        root["properties"] = new JsonObject
        {
            ["selected"] = new JsonObject
            {
                ["tm:ref"] = "urn:inventory:tm#/properties/a",
                [prefix + ":classification"] = "restricted"
            }
        };

        if (conflict)
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a prefix binding conflict.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("prefix");
        }
        else
        {
            var selection = (await SelectProjection(root, source)).Single();
            var annotation = selection.Annotations.Single();
            await Assert.That(selection.SourcePointer).IsEqualTo("/properties/a");
            await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
            await Assert.That(selection.Definition.GetRawText())
                .IsEqualTo(JsonNode.Parse(properties)!["a"]!.ToJsonString());
            await Assert.That(annotation.MemberName).IsEqualTo(prefix + ":classification");
            await Assert.That(annotation.Iri).IsEqualTo(planNamespace + "classification");
            await Assert.That(annotation.VendorPrefixIri).IsEqualTo(planNamespace);
            await Assert.That(annotation.Value.GetString()).IsEqualTo("restricted");
        }
    }

    [Test]
    public async Task ProjectionSelectionUsesThePlanTermScopedPrefixBinding()
    {
        var source = await SelectionSource("""{"a":{"const":"source"}}""");
        var root = await SelectionRuleDocument();
        root["@context"]!["semantic"] = "urn:root:";
        root["@context"]!["properties"]!["@context"] = new JsonObject { ["semantic"] = "urn:shared:" };
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","semantic:classification":"restricted"}}
            """);

        var selection = (await SelectProjection(root, source)).Single();
        await Assert.That(selection.Annotations.Single().Iri).IsEqualTo("urn:shared:classification");
        await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionUsesTheSourceTermScopedPrefixBinding(bool conflict)
    {
        var sourceNamespace = conflict ? "urn:other:" : "urn:plan:";
        var source = await SelectionSource("""{"a":{"const":"source"}}""",
            propertyContext: new JsonObject { ["semantic"] = sourceNamespace });
        var root = await SelectionRuleDocument();
        root["@context"]!["semantic"] = "urn:plan:";
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","semantic:classification":"restricted"}}
            """);

        if (conflict)
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a scoped prefix conflict.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("prefix");
        }
        else
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("urn:plan:");
            await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionUsesTheSourceTypeScopedPrefixBinding(bool conflict)
    {
        var properties = new JsonObject
        {
            ["a"] = new JsonObject
            {
                ["@context"] = new JsonObject
                {
                    ["ScopedType"] = new JsonObject
                    {
                        ["@id"] = "urn:example:type",
                        ["@context"] = new JsonObject { ["semantic"] = conflict ? "urn:other:" : "urn:plan:" }
                    }
                },
                ["@type"] = "ScopedType",
                ["const"] = "source"
            }
        };
        var source = await SelectionSource(properties.ToJsonString());
        var root = await SelectionRuleDocument();
        root["@context"]!["semantic"] = "urn:plan:";
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","semantic:classification":"restricted"}}
            """);

        if (conflict)
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a type-scoped prefix conflict.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("prefix");
        }
        else
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("urn:plan:");
            await Assert.That(selection.Definition.GetProperty("@type").GetString()).IsEqualTo("ScopedType");
        }
    }

    [Test]
    [Arguments("same")]
    [Arguments("conflict")]
    [Arguments("missing")]
    public async Task ProjectionSelectionUsesCapturedRemotePrefixContexts(string binding)
    {
        var planContextUri = new Uri("https://contexts.invalid/plan.jsonld");
        var sourceContextUri = new Uri("https://contexts.invalid/source.jsonld");
        var source = await SelectionSource(
            $$$"""{"a":{"@context":"{{{sourceContextUri}}}","const":"source"}}""");
        var root = await SelectionRuleDocument();
        root["@context"] = new JsonArray(root["@context"]!.DeepClone(), JsonValue.Create(planContextUri.AbsoluteUri));
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [planContextUri] = new(Encoding.UTF8.GetBytes("""{"@context":{"vendor":"urn:plan:"}}"""))
        };
        if (binding != "missing")
        {
            var sourceNamespace = binding == "same" ? "urn:plan:" : "urn:other:";
            contexts[sourceContextUri] = new(Encoding.UTF8.GetBytes(
                $$$"""{"@context":{"vendor":"{{{sourceNamespace}}}"}}"""));
        }

        if (binding == "same")
        {
            var selection = (await SelectProjection(root, source, contexts)).Single();
            await Assert.That(selection.Annotations.Single().Iri).IsEqualTo("urn:plan:classification");
            await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
        }
        else
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source, contexts); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a captured-context rejection.");
            await Assert.That(failure.Code).IsEqualTo(binding == "missing"
                ? FederationErrorCode.UnsupportedOperation : FederationErrorCode.InvalidPackage);
            if (binding == "conflict") { await Assert.That(failure.Message).Contains("prefix"); }
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ProjectionSelectionChecksPrefixConflictsOnlyForWinningImports(bool firstSourceWins)
    {
        var first = await SelectionSource("""{"same":{"const":"first"}}""");
        var second = await SelectionSource("""{"other":{"const":"second"}}""");
        var root = await SelectionRuleDocument();
        root["@context"]!["semantic"] = "urn:plan:";
        if (firstSourceWins) { root["p:projects"]![0]!["p:selectAll"] = true; }
        root["p:projects"]!.AsArray().Add(new JsonObject
        {
            ["p:sourceName"] = "second", ["href"] = "urn:source:second", ["type"] = "application/tm+json"
        });
        root["properties"] = JsonNode.Parse("""
            {"same":{"tm:ref":"urn:source:second#/properties/other","semantic:classification":"restricted"}}
            """);
        var index = InventoryIndex(first);
        var identities = new WotCapturedUriIdentityIndex(new[]
        {
            new WotCapturedUriIdentity(new("urn:inventory:tm"), WotCapturedUriIdentityKind.DocumentView,
                index.Resolve(new("urn:inventory:tm"))),
            new WotCapturedUriIdentity(new("urn:source:second"), WotCapturedUriIdentityKind.DocumentView,
                new(second, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId))
        });
        var containing = await RetainProjectionPlan(root);
        async Task<IReadOnlyList<WotProjectionAffordanceSelection>> Select() =>
            await WotRegistryResolver.SelectCapturedProjectionAffordancesAsync(containing,
                new("https://example.test/plan.json"), new Dictionary<Uri, FederationDocument>(), identities);

        if (firstSourceWins)
        {
            var selection = (await Select()).Single();
            await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("first");
            await Assert.That(selection.Annotations.Count).IsEqualTo(0);
        }
        else
        {
            var failure = await Assert.That(async () => { _ = await Select(); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a winning prefix conflict.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("prefix");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionRequiresTheExplicitVendorPrefixFlag(bool bound)
    {
        var source = await SelectionSource("""{"a":{}}""");
        var root = await SelectionRuleDocument();
        root["@context"] = new JsonArray(root["@context"]!.DeepClone(),
            new JsonObject { ["namespace"] = "vendor:" },
            new JsonObject { ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = bound } });
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);

        if (bound)
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Annotations.Single().Iri).IsEqualTo("vendor:classification");
        }
        else
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an unbound prefix rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("bound compact-IRI prefix");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionReadsAmbiguousPrefixFlagsWithoutChangingContextAliases(bool bound)
    {
        var source = await SelectionSource("""{"a":{"const":"source"}}""");
        var root = await SelectionRuleDocument();
        root["@context"] = new JsonArray(root["@context"]!.DeepClone(),
            new JsonObject { ["namespace"] = "vendor:", ["specific"] = "vendor:special" },
            new JsonObject
            {
                ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = bound },
                ["vendor:special"] = "specific"
            });
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);

        if (bound)
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("vendor:");
            await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
        }
        else
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an unbound prefix rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("bound compact-IRI prefix");
        }
    }

    [Test]
    [Arguments("plan", "@language", false)]
    [Arguments("plan", "@index", false)]
    [Arguments("source", "@language", false)]
    [Arguments("source", "@index", true)]
    public async Task ProjectionSelectionHonorsPrefixTermContainers(string owner, string container, bool conflict)
    {
        var binding = new JsonObject
        {
            ["@id"] = conflict ? "urn:other:" : "urn:plan:",
            ["@prefix"] = true,
            ["@container"] = container
        };
        var properties = new JsonObject
        {
            ["a"] = new JsonObject { ["const"] = "source" }
        };
        if (owner == "source")
        {
            properties["a"]!["@context"] = new JsonObject { ["vendor"] = binding.DeepClone() };
        }
        var source = await SelectionSource(properties.ToJsonString());
        var root = await SelectionRuleDocument();
        root["@context"]!["vendor"] = owner == "plan" ? binding : JsonValue.Create("urn:plan:");
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);

        if (conflict)
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a source prefix conflict.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("conflicts with the selected source");
        }
        else
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("urn:plan:");
            await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionReadsImplicitAmbiguousPrefixFlags(bool shorthand)
    {
        var source = await SelectionSource("""{"a":{"const":"source"}}""");
        var root = await SelectionRuleDocument();
        root["@context"] = new JsonArray(root["@context"]!.DeepClone(),
            new JsonObject { ["namespace"] = "vendor:" },
            new JsonObject
            {
                ["vendor"] = shorthand ? JsonValue.Create("namespace") : new JsonObject { ["@id"] = "namespace" }
            });
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);

        if (shorthand)
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("vendor:");
        }
        else
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an unbound prefix rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("bound compact-IRI prefix");
        }
    }

    [Test]
    [Arguments("plan", false)]
    [Arguments("plan", true)]
    [Arguments("source", false)]
    [Arguments("source", true)]
    public async Task ProjectionSelectionReadsProtectedImportedPrefixFlags(string owner, bool bound)
    {
        var uri = new Uri("https://contexts.invalid/imported-prefix.jsonld");
        var imported = new JsonObject
        {
            ["@context"] = new JsonObject
            {
                ["@protected"] = true,
                ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = bound }
            }
        };
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [uri] = new(Encoding.UTF8.GetBytes(imported.ToJsonString()))
        };
        var scoped = new JsonArray(new JsonObject { ["namespace"] = "vendor:" },
            new JsonObject { ["@import"] = uri.AbsoluteUri });
        var properties = new JsonObject
        {
            ["a"] = new JsonObject { ["const"] = "source" }
        };
        if (owner == "source") { properties["a"]!["@context"] = scoped.DeepClone(); }
        var source = await SelectionSource(properties.ToJsonString());
        var root = await SelectionRuleDocument();
        if (owner == "plan")
        {
            root["@context"] = new JsonArray(root["@context"]!.DeepClone(),
                scoped[0]!.DeepClone(), scoped[1]!.DeepClone());
        }
        else
        {
            root["@context"]!["vendor"] = "urn:plan:";
        }
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);

        if (owner == "plan" && !bound || owner == "source" && bound)
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source, contexts); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected prefix admission rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("prefix");
        }
        else
        {
            var selection = (await SelectProjection(root, source, contexts)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri)
                .IsEqualTo(owner == "plan" ? "vendor:" : "urn:plan:");
            await Assert.That(selection.Definition.GetRawText()).IsEqualTo(properties["a"]!.ToJsonString());
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionUsesLocalOverridesOfImportedPrefixFlags(bool bound)
    {
        var uri = new Uri("https://contexts.invalid/overridden-prefix.jsonld");
        var imported = new JsonObject
        {
            ["@context"] = new JsonObject
            {
                ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = !bound }
            }
        };
        var source = await SelectionSource("""{"a":{"const":"source"}}""");
        var root = await SelectionRuleDocument();
        root["@context"] = new JsonArray(root["@context"]!.DeepClone(),
            new JsonObject { ["namespace"] = "vendor:" },
            new JsonObject
            {
                ["@import"] = uri.AbsoluteUri,
                ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = bound }
            });
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [uri] = new(Encoding.UTF8.GetBytes(imported.ToJsonString()))
        };

        if (bound)
        {
            var selection = (await SelectProjection(root, source, contexts)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("vendor:");
        }
        else
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source, contexts); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an unbound prefix rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("bound compact-IRI prefix");
        }
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task ProjectionSelectionReadsThePrefixDefinitionAfterAContextReset(bool first, bool last)
    {
        var source = await SelectionSource("""{"a":{"const":"source"}}""");
        var root = await SelectionRuleDocument();
        var originalContext = root["@context"]!.DeepClone();
        root["@context"] = new JsonArray(originalContext.DeepClone(),
            new JsonObject { ["namespace"] = "vendor:" },
            new JsonObject { ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = first } },
            null, originalContext.DeepClone(), new JsonObject { ["namespace"] = "vendor:" },
            new JsonObject { ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = last } });
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);

        if (last)
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("vendor:");
        }
        else
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an unbound prefix rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("bound compact-IRI prefix");
        }
    }

    [Test]
    public async Task ProjectionSelectionRetainsSharedPrefixAnnotationsAndCapturedBytes()
    {
        var uri = new Uri("https://contexts.invalid/shared-prefix.jsonld");
        var remote = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":[{"namespace":"vendor:"},{"vendor":{"@id":"namespace","@prefix":true}}]}
            """));
        var source = await SelectionSource("""{"a":{"const":"source"}}""");
        var sourceDocument = (await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource))).Document!;
        var sourceBytes = await Bytes(sourceDocument);
        var remoteBytes = await Bytes(remote);
        var root = await SelectionRuleDocument();
        var marker = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("/properties/selected\nvendor"))).ToLowerInvariant();
        root["@context"]!["__xregistry_prefix_scope_" + marker + "__"] = "urn:occupied:marker";
        root["@context"] = new JsonArray(root["@context"]!.DeepClone(), JsonValue.Create(uri.AbsoluteUri));
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a",
              "vendor:classification":"restricted","vendor:metadata":{"level":2}}}
            """);
        var containing = await RetainProjectionPlan(root);
        var planBytes = await Bytes(containing.Document);

        var selection = (await WotRegistryResolver.SelectCapturedProjectionAffordancesAsync(containing,
            new("https://example.test/plan.json"), new Dictionary<Uri, FederationDocument> { [uri] = remote },
            InventoryIndex(source))).Single();

        await Assert.That(selection.Annotations.Count).IsEqualTo(2);
        await Assert.That(selection.Annotations.All(annotation => annotation.VendorPrefixIri == "vendor:")).IsTrue();
        await Assert.That(selection.Annotations.Single(annotation => annotation.MemberName == "vendor:classification")
            .Value.GetString()).IsEqualTo("restricted");
        await Assert.That(selection.Annotations.Single(annotation => annotation.MemberName == "vendor:metadata")
            .Value.GetProperty("level").GetInt32()).IsEqualTo(2);
        await Assert.That((await Bytes(selection.Owner.Document)).SequenceEqual(sourceBytes)).IsTrue();
        await Assert.That((await Bytes(containing.Document)).SequenceEqual(planBytes)).IsTrue();
        await Assert.That((await Bytes(remote)).SequenceEqual(remoteBytes)).IsTrue();
    }

    [Test]
    public async Task ProjectionSelectionAvoidsPrefixScopeMarkerReferencesInContexts()
    {
        var marker = "__xregistry_prefix_scope_" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("/properties/selected\nvendor"))).ToLowerInvariant() + "__";
        var uri = new Uri("https://contexts.invalid/marker-reference.jsonld");
        var remote = new JsonObject
        {
            ["@context"] = new JsonObject
            {
                ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = true },
                ["occupiedAlias"] = new JsonObject { ["@id"] = marker, ["@protected"] = true }
            }
        };
        var source = await SelectionSource("""{"a":{"const":"source"}}""");
        var root = await SelectionRuleDocument();
        root["@context"]!["@vocab"] = "urn:context:";
        root["@context"]!["occupiedAlias"] = new JsonObject
        {
            ["@id"] = "urn:context:" + marker, ["@protected"] = true
        };
        root["@context"] = new JsonArray(root["@context"]!.DeepClone(),
            new JsonObject { ["namespace"] = "vendor:" }, JsonValue.Create(uri.AbsoluteUri));
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [uri] = new(Encoding.UTF8.GetBytes(remote.ToJsonString()))
        };

        var selection = (await SelectProjection(root, source, contexts)).Single();
        await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("vendor:");
        await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionChecksTheSourceExplicitPrefixFlag(bool bound)
    {
        var properties = new JsonObject
        {
            ["a"] = new JsonObject
            {
                ["@context"] = new JsonArray(new JsonObject { ["namespace"] = "vendor:" },
                    new JsonObject { ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = bound } }),
                ["vendor"] = "original",
                ["const"] = "source"
            }
        };
        var source = await SelectionSource(properties.ToJsonString());
        var root = await SelectionRuleDocument();
        root["@context"]!["vendor"] = "urn:plan:";
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);

        if (bound)
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a source prefix conflict.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("prefix");
        }
        else
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
            await Assert.That(selection.Definition.GetProperty("vendor").GetString()).IsEqualTo("original");
            await Assert.That(selection.Annotations.Single().Iri).IsEqualTo("urn:plan:classification");
        }
    }

    [Test]
    [Arguments("plan")]
    [Arguments("source")]
    public async Task ProjectionSelectionAvoidsPrefixProbeNamesDefinedInContexts(string owner)
    {
        var uri = new Uri("https://contexts.invalid/names.jsonld");
        var sourceProperties = owner == "source"
            ? $$$"""{"a":{"@context":"{{{uri}}}","const":"source"}}"""
            : """{"a":{"const":"source"}}""";
        var source = await SelectionSource(sourceProperties);
        var root = await SelectionRuleDocument();
        root["@context"]!["vendor"] = "urn:plan:";
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);
        var probeDefinition = new JsonObject
        {
            ["@id"] = "urn:plan:__xregistry_prefix_probe__", ["@container"] = "@language"
        };
        var contexts = new Dictionary<Uri, FederationDocument>();
        if (owner == "plan")
        {
            root["@context"]!["vendor:__xregistry_prefix_probe__"] = probeDefinition;
        }
        else
        {
            var context = new JsonObject
            {
                ["@context"] = new JsonObject
                {
                    ["vendor"] = "urn:plan:", ["vendor:__xregistry_prefix_probe__"] = probeDefinition
                }
            };
            contexts[uri] = new(Encoding.UTF8.GetBytes(context.ToJsonString()));
        }

        var selection = (await SelectProjection(root, source, contexts)).Single();
        await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("urn:plan:");
        await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
    }

    [Test]
    public async Task ProjectionSelectionAvoidsExpandedPrefixProbeValueCollisions()
    {
        var uri = new Uri("https://contexts.invalid/values.jsonld");
        var marker = "urn:xregistry:projection-prefix:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("/properties/a\nvendor"))).ToLowerInvariant();
        var source = await SelectionSource(
            $$$"""{"a":{"@context":"{{{uri}}}","other":"marker","const":"source"}}""");
        var root = await SelectionRuleDocument();
        root["@context"]!["vendor"] = "urn:plan:";
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);
        var context = new JsonObject
        {
            ["@context"] = new JsonObject
            {
                ["vendor"] = "urn:other:",
                ["marker"] = marker,
                ["other"] = new JsonObject { ["@id"] = "urn:test:other", ["@type"] = "@vocab" }
            }
        };
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [uri] = new(Encoding.UTF8.GetBytes(context.ToJsonString()))
        };

        var failure = await Assert.That(async () => { _ = await SelectProjection(root, source, contexts); })
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected the real source prefix conflict.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(failure.Message).Contains("prefix");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionReadsAmbiguousTypeScopedPrefixFlags(bool bound)
    {
        var properties = new JsonObject
        {
            ["a"] = new JsonObject
            {
                ["@context"] = new JsonObject
                {
                    ["ScopedType"] = new JsonObject
                    {
                        ["@id"] = "urn:example:type",
                        ["@context"] = new JsonArray(new JsonObject { ["namespace"] = "vendor:" },
                            new JsonObject { ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = bound } })
                    }
                },
                ["@type"] = "ScopedType",
                ["const"] = "source"
            }
        };
        var source = await SelectionSource(properties.ToJsonString());
        var root = await SelectionRuleDocument();
        root["@context"]!["vendor"] = "urn:plan:";
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);

        if (bound)
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a type-scoped prefix conflict.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("prefix");
        }
        else
        {
            var selection = (await SelectProjection(root, source)).Single();
            await Assert.That(selection.Definition.GetProperty("@type").GetString()).IsEqualTo("ScopedType");
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("urn:plan:");
        }
    }

    [Test]
    [Arguments("plan", false)]
    [Arguments("plan", true)]
    [Arguments("source", false)]
    [Arguments("source", true)]
    public async Task ProjectionSelectionReadsAmbiguousPrefixFlagsFromCapturedRemoteContexts(string owner, bool bound)
    {
        var uri = new Uri("https://contexts.invalid/prefix.jsonld");
        var properties = owner == "source"
            ? $$$"""{"a":{"@context":"{{{uri}}}","const":"source"}}"""
            : """{"a":{"const":"source"}}""";
        var source = await SelectionSource(properties);
        var root = await SelectionRuleDocument();
        if (owner == "plan")
        {
            root["@context"] = new JsonArray(root["@context"]!.DeepClone(), JsonValue.Create(uri.AbsoluteUri));
        }
        else
        {
            root["@context"]!["vendor"] = "urn:plan:";
        }
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);
        var remoteContext = new JsonObject
        {
            ["@context"] = new JsonArray(new JsonObject { ["namespace"] = "vendor:" },
                new JsonObject { ["vendor"] = new JsonObject { ["@id"] = "namespace", ["@prefix"] = bound } })
        };
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [uri] = new(Encoding.UTF8.GetBytes(remoteContext.ToJsonString()))
        };

        if (owner == "plan" && !bound || owner == "source" && bound)
        {
            var failure = await Assert.That(async () => { _ = await SelectProjection(root, source, contexts); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected prefix admission rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("prefix");
        }
        else
        {
            var selection = (await SelectProjection(root, source, contexts)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri)
                .IsEqualTo(owner == "plan" ? "vendor:" : "urn:plan:");
            await Assert.That(selection.Definition.GetProperty("const").GetString()).IsEqualTo("source");
        }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task ProjectionSelectionPrefixChecksShareInclusiveWorkAndCancellation(int delta)
    {
        var source = await SelectionSource("""{"a":{"const":"source"}}""");
        var root = await SelectionRuleDocument();
        root["@context"]!["vendor"] = "urn:plan:";
        root["properties"] = JsonNode.Parse("""
            {"selected":{"tm:ref":"urn:inventory:tm#/properties/a","vendor:classification":"restricted"}}
            """);
        var containing = await RetainProjectionPlan(root);
        var identities = InventoryIndex(source);
        async Task<IReadOnlyList<WotProjectionAffordanceSelection>> Select(FederationReadBudget budget,
            CancellationToken token = default) =>
            await WotRegistryResolver.SelectCapturedProjectionAffordancesAsync(containing,
                new("https://example.test/plan.json"), new Dictionary<Uri, FederationDocument>(), identities,
                budget: budget, cancellationToken: token);

        var baseline = new FederationReadBudget();
        _ = await Select(baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => { _ = await Select(budget); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected shared prefix work limit.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var selection = (await Select(budget)).Single();
            await Assert.That(selection.Annotations.Single().VendorPrefixIri).IsEqualTo("urn:plan:");
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => { _ = await Select(new(), cancellation.Token); })
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task ProjectionSelectionBoundsRetainedPrefixAndAnnotationBytes(int delta)
    {
        var source = await SelectionSource(new JsonObject
        {
            ["a"] = new JsonObject { ["const"] = new string('s', 4096) }
        }.ToJsonString());
        var root = await SelectionRuleDocument();
        root["@context"]!["vendor"] = "urn:plan:";
        var selectors = new JsonObject();
        foreach (var name in new[] { "one", "two", "three" })
        {
            selectors[name] = new JsonObject
            {
                ["tm:ref"] = "urn:inventory:tm#/properties/a",
                ["vendor:classification"] = new string('v', 4096)
            };
        }
        root["properties"] = selectors;
        var containing = await RetainProjectionPlan(root);
        var identities = InventoryIndex(source);
        async Task<IReadOnlyList<WotProjectionAffordanceSelection>> Select(FederationReadBudget budget) =>
            await WotRegistryResolver.SelectCapturedProjectionAffordancesAsync(containing,
                new("https://example.test/plan.json"), new Dictionary<Uri, FederationDocument>(), identities,
                budget: budget);

        var baseline = await Select(new());
        var size = baseline.Sum(selection => Encoding.UTF8.GetByteCount(selection.Definition.GetRawText()) +
            Encoding.UTF8.GetByteCount(selection.Name) + Encoding.UTF8.GetByteCount(selection.SourcePointer) +
            selection.Annotations.Sum(annotation => Encoding.UTF8.GetByteCount(annotation.MemberName) +
                Encoding.UTF8.GetByteCount(annotation.Iri) + Encoding.UTF8.GetByteCount(annotation.Value.GetRawText()) +
                Encoding.UTF8.GetByteCount(annotation.VendorPrefixIri ?? "")));
        var budget = new FederationReadBudget(new(maxResultBytes: size + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => { _ = await Select(budget); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected retained annotation size rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            await Assert.That((await Select(budget)).Count).IsEqualTo(3);
        }
    }

    [Test]
    [Arguments("schema")]
    [Arguments("td")]
    [Arguments("projection")]
    [Arguments("unbound")]
    [Arguments("plain")]
    [Arguments("keyword")]
    [Arguments("duplicate")]
    public async Task ProjectionSelectorsRejectOverridesAndUnboundedAnnotations(string invalidAnnotation)
    {
        var source = await SelectionSource("""{"a":{}}""");
        var root = await SelectionRuleDocument();
        root["@context"]!["vendor"] = JsonNode.Parse(
            """{"@id":"https://example.test/vendor/","@prefix":true}""");
        root["@context"]!["tdonly"] = JsonNode.Parse(
            """{"@id":"https://www.w3.org/2019/wot/td#","@prefix":true}""");
        root["@context"]!["planonly"] = JsonNode.Parse(
            $$"""{"@id":"{{WotJsonLdBaseResolver.ProjectionVocabulary}}","@prefix":true}""");
        root["@context"]!["titleAlias"] = "td:title";
        var selector = new JsonObject { ["tm:ref"] = "urn:inventory:tm#/properties/a" };
        switch (invalidAnnotation)
        {
            case "schema":
                root["@context"]!["type"] = "td:type";
                selector["type"] = "string";
                break;
            case "td":
                selector["tdonly:extension"] = true;
                break;
            case "projection":
                selector["planonly:extension"] = true;
                break;
            case "unbound":
                selector["missing:extension"] = true;
                break;
            case "plain":
                root["@context"]!["extension"] = "https://example.test/vendor/extension";
                selector["extension"] = true;
                break;
            case "keyword":
                selector["@id"] = "urn:annotation";
                break;
            case "duplicate":
                selector["title"] = "First";
                selector["titleAlias"] = "Second";
                break;
        }
        root["properties"] = new JsonObject { ["selected"] = selector };

        var failure = await Assert.That(async () => { _ = await SelectProjection(root, source); })
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected selector annotation rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ProjectionSelectionRulesExpandTypesAndApplyConjunctiveDisjunctivePredicates()
    {
        var root = await SelectionRuleDocument();
        root["@context"]!["choose"] = "p:select";
        root["@context"]!["kind"] = "p:affordanceKind";
        root["@context"]!["domain"] = "https://example.test/types/";
        var entry = root["p:projects"]![0]!;
        entry["p:routing"] = "projection";
        entry["p:namePrefix"] = "\U00010428device";
        entry["choose"] = JsonNode.Parse("""
            [{"kind":"property","@type":["domain:Sensor","domain:Temperature"]},
             {"kind":"event","@type":"domain:Alarm"}]
            """);
        var rule = (await ReadSelectionRules(root))[0];
        await Assert.That(rule.Routing).IsEqualTo("projection");
        await Assert.That(rule.NamePrefix).IsEqualTo("\U00010428device");
        await Assert.That(rule.Filters.Count).IsEqualTo(2);
        await Assert.That(rule.Filters[0].TypeIris.Count).IsEqualTo(2);
        await Assert.That(rule.Filters[0].TypeIris[0]).IsEqualTo("https://example.test/types/Sensor");
        await Assert.That(rule.Filters[0].TypeIris[1]).IsEqualTo("https://example.test/types/Temperature");
        await Assert.That(rule.Matches("property", new HashSet<string>
            { "https://example.test/types/Sensor", "https://example.test/types/Temperature" })).IsTrue();
        await Assert.That(rule.Matches("property", new HashSet<string>
            { "https://example.test/types/Sensor" })).IsFalse();
        await Assert.That(rule.Matches("action", new HashSet<string>
            { "https://example.test/types/Sensor", "https://example.test/types/Temperature" })).IsFalse();
        await Assert.That(rule.Matches("event", new HashSet<string>
            { "https://example.test/types/Alarm" })).IsTrue();
        await Assert.That(rule.Matches("event", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "https://example.test/types/alarm" })).IsFalse();
        await Assert.That(rule.Matches("event", new HashSet<string>
            { "https://example.test/types/SpecialAlarm" })).IsFalse();
    }

    [Test]
    [Arguments("""{"p:routing":false}""")]
    [Arguments("""{"p:routing":"SOURCE"}""")]
    [Arguments("""{"p:routing":"proxy"}""")]
    [Arguments("""{"p:namePrefix":null}""")]
    [Arguments("""{"p:namePrefix":42}""")]
    [Arguments("""{"p:selectAll":"true"}""")]
    [Arguments("""{"p:select":{}}""")]
    [Arguments("""{"p:select":[false]}""")]
    [Arguments("""{"p:select":[{"p:affordanceKind":"properties"}]}""")]
    [Arguments("""{"p:select":[{"p:affordanceKind":true}]}""")]
    [Arguments("""{"p:select":[{"pattern":"*"}]}""")]
    [Arguments("""{"p:select":[{"vendor:pattern":"*"}]}""")]
    [Arguments("""{"p:select":[{"@id":"urn:filter"}]}""")]
    [Arguments("""{"p:select":[{"@type":false}]}""")]
    [Arguments("""{"p:select":[{"@type":["urn:type",false]}]}""")]
    public async Task ProjectionSelectionRulesRejectUnboundedOrMalformedGrammar(string fields)
    {
        var root = await SelectionRuleDocument();
        foreach (var field in JsonNode.Parse(fields)!.AsObject())
        {
            root["p:projects"]![0]![field.Key] = field.Value?.DeepClone();
        }
        var failure = await Assert.That(async () => { _ = await ReadSelectionRules(root); })
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected bounded projection grammar.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("predicate")]
    [Arguments("selector")]
    [Arguments("source")]
    public async Task ProjectionTypeArraysRejectAnExpandedNullBeforeAValidType(string owner)
    {
        var root = await SelectionRuleDocument();
        root["@context"]!["Disabled"] = null;
        var types = new JsonArray("Disabled", "urn:type:allowed");
        if (owner == "selector")
        {
            root["properties"] = new JsonObject
            {
                ["selected"] = new JsonObject
                {
                    ["tm:ref"] = "urn:inventory:tm#/properties/a",
                    ["@type"] = types
                }
            };
        }
        else if (owner == "predicate")
        {
            root["p:projects"]![0]!["p:select"] = new JsonArray(new JsonObject { ["@type"] = types });
        }
        else
        {
            root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""[{"@type":"urn:type:allowed"}]""");
        }

        var source = owner == "source"
            ? await SelectionSource("""
                {"a":{"@context":{"Disabled":null},"@type":["Disabled","urn:type:allowed"]}}
                """)
            : await SelectionSource("""{"a":{}}""");
        var failure = await Assert.That(async () =>
        {
            if (owner == "predicate") { _ = await ReadSelectionRules(root); }
            else { _ = await SelectProjection(root, source); }
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected every type to resolve.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ProjectionTypesAllowAnUnusedDisabledTermAndRepeatedResolvedTypes()
    {
        var root = await SelectionRuleDocument();
        root["@context"]!["Disabled"] = null;
        root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""
            [{"@type":["urn:type:allowed","urn:type:allowed"]}]
            """);

        var rule = (await ReadSelectionRules(root)).Single();
        var typeIris = rule.Filters.Single().TypeIris;
        await Assert.That(typeIris.Count).IsEqualTo(2);
        await Assert.That(typeIris[0]).IsEqualTo("urn:type:allowed");
        await Assert.That(typeIris[1]).IsEqualTo("urn:type:allowed");
        var source = await SelectionSource("""{"a":{"@type":"urn:type:allowed"}}""");
        await Assert.That((await SelectProjection(root, source)).Single().Name).IsEqualTo("a");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionRootRejectsAnExpandedNullBeforeItsRequiredTypes(bool envelope)
    {
        var root = await SelectionRuleDocument();
        root["@context"]!["Disabled"] = null;
        root["@type"] = new JsonArray("Disabled", "tm:ThingModel", "p:projection");
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var failure = await Assert.That(async () =>
        {
            if (envelope)
            {
                _ = await WotJsonLdBaseResolver.ReadProjectionPlanEnvelopeAsync(document, uri, contexts);
            }
            else
            {
                _ = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document, uri,
                    WotReferenceTargetKind.ThingModel, contexts);
            }
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected every root type to resolve.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ProjectionTypeFiltersResolveRelativeClassesAgainstTheirCapturedBase()
    {
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""[{"@type":"relative"}]""");
        var rule = (await ReadSelectionRules(root))[0];
        await Assert.That(rule.Filters[0].TypeIris[0]).IsEqualTo("https://example.test/relative");
        await Assert.That(rule.Matches("property", new HashSet<string>
            { "https://example.test/relative" })).IsTrue();
    }

    [Test]
    public async Task ProjectionTypeFiltersHonorKeywordAliasesAndCapturedRemoteContexts()
    {
        var root = await SelectionRuleDocument();
        var local = root["@context"]!.DeepClone();
        root["@context"] = new JsonArray("https://example.test/context", local);
        root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""[{"classes":["remote:A","remote:B"]}]""");
        var rules = await WotJsonLdBaseResolver.ReadProjectionSelectionRulesAsync(
            new(Encoding.UTF8.GetBytes(root.ToJsonString())), new("https://example.test/plan.json"),
            WotReferenceTargetKind.ThingModel, new Dictionary<Uri, FederationDocument>
            {
                [new("https://example.test/context")] = new(Encoding.UTF8.GetBytes("""
                    {"@context":{"classes":"@type","remote":"urn:captured:"}}
                    """))
            });
        await Assert.That(rules[0].Filters[0].TypeIris.Count).IsEqualTo(2);
        await Assert.That(rules[0].Filters[0].TypeIris[0]).IsEqualTo("urn:captured:A");
        await Assert.That(rules[0].Filters[0].TypeIris[1]).IsEqualTo("urn:captured:B");
    }

    [Test]
    [Arguments("routing", "\"source\"")]
    [Arguments("selectAll", "false")]
    [Arguments("namePrefix", "\"prefix\"")]
    [Arguments("select", "[]")]
    public async Task ProjectionSelectionRulesRejectCompetingAliases(string term, string json)
    {
        var root = await SelectionRuleDocument();
        root["@context"]!["duplicate"] = "p:" + term;
        var entry = root["p:projects"]![0]!;
        entry["p:" + term] = JsonNode.Parse(json);
        entry["duplicate"] = JsonNode.Parse(json);
        await Assert.That(async () => { _ = await ReadSelectionRules(root); }).Throws<FederationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSelectionRulesHandleEmptyPredicatesAndExplicitWholeSourceSelection(bool all)
    {
        var root = await SelectionRuleDocument();
        var entry = root["p:projects"]![0]!;
        entry["p:selectAll"] = all;
        entry["p:namePrefix"] = "";
        entry["p:select"] = JsonNode.Parse("""[{},{"@type":[]} ]""");
        var rule = (await ReadSelectionRules(root))[0];
        await Assert.That(rule.SelectAll).IsEqualTo(all);
        await Assert.That(rule.NamePrefix).IsEqualTo("");
        await Assert.That(rule.Filters.Count).IsEqualTo(2);
        await Assert.That(rule.Filters[1].TypeIris.Count).IsEqualTo(0);
        await Assert.That(rule.Matches("action", new HashSet<string>())).IsTrue();
        entry["p:select"] = new JsonArray();
        rule = (await ReadSelectionRules(root))[0];
        await Assert.That(rule.Matches("event", new HashSet<string>())).IsEqualTo(all);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task ProjectionSelectionRulesShareInclusiveWorkAndHonorCancellation(int delta)
    {
        var root = await SelectionRuleDocument();
        root["p:projects"]![0]!["p:select"] = JsonNode.Parse("""[{"@type":"urn:test:class"}]""");
        var baseline = new FederationReadBudget();
        _ = await ReadSelectionRules(root, baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => { _ = await ReadSelectionRules(root, budget); })
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected shared rule work limit.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            _ = await ReadSelectionRules(root, budget);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => { _ = await ReadSelectionRules(root, token: cancellation.Token); })
            .Throws<OperationCanceledException>();
    }

    private static async Task<JsonNode> SelectionRuleDocument() => await ProjectionDocument(new()
    {
        ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json"
    });

    private static ValueTask<IReadOnlyList<WotProjectionSourceSelectionRule>> ReadSelectionRules(
        JsonNode root, FederationReadBudget? budget = null, CancellationToken token = default) =>
        WotJsonLdBaseResolver.ReadProjectionSelectionRulesAsync(
            new(Encoding.UTF8.GetBytes(root.ToJsonString())), new("https://example.test/plan.json"),
            WotReferenceTargetKind.ThingModel, new Dictionary<Uri, FederationDocument>(), budget, token);

    [Test]
    [Arguments("tm:minVersion", "invalid-floor", false)]
    [Arguments("td:minVersion", "invalid-floor", true)]
    [Arguments("uav:minVersion", "invalid-floor", true)]
    [Arguments("uav:compatibilityLine", "invalid-line", false)]
    [Arguments("uav:compatibilityLine", "invalid-line", true)]
    [Arguments("p:sourceDigest", "digest", true)]
    public async Task ProjectionSelectorsRejectSourceRequirementsUnderCapturedAliases(
        string term, string expected, bool alias)
    {
        var root = await ProjectionDocument(new()
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json"
        });
        root["properties"] = JsonNode.Parse("""{"value":{"tm:ref":"urn:inventory:tm#/properties/a"}}""");
        var name = alias ? "requirement" : term;
        if (alias) { root["@context"]![name] = term; }
        root["properties"]!["value"]![name] = false;
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        foreach (var combined in new[] { false, true })
        {
            async Task Discover()
            {
                if (combined)
                {
                    _ = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
                        WotReferenceTargetKind.ThingModel, contexts, InventoryIndex(new Fixture().Open()));
                }
                else
                {
                    _ = await WotJsonLdBaseResolver.DiscoverProjectionSelectorReferencesAsync(document, uri,
                        WotReferenceTargetKind.ThingModel, contexts);
                }
            }
            if (expected == "digest")
            {
                var failure = await Assert.That(Discover).Throws<FederationException>() ??
                    throw new InvalidOperationException("Expected selector-level source digest rejection.");
                await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            }
            else
            {
                var failure = await Assert.That(Discover).Throws<RegistryException>() ??
                    throw new InvalidOperationException("Expected requirement-location rejection before source selection.");
                await Assert.That(failure.Diagnostic.Code).IsEqualTo(expected);
                await Assert.That(failure.Diagnostic.Path).IsEqualTo("/properties/value");
            }
        }
    }

    [Test]
    public async Task ProjectionSelectorsUseExpandedRequirementMeaningRatherThanLiteralPrefixes()
    {
        var root = await ProjectionDocument(new()
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json",
            ["tm:minVersion"] = "1.0.0"
        });
        root["properties"] = JsonNode.Parse("""
            {"value":{"@context":{"uav":"urn:vendor:"},"tm:ref":"urn:inventory:tm#/properties/a",
              "uav:minVersion":"annotation","td:ref":"urn:inventory:td"}}
            """);
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document,
            new("https://example.test/plan.json"), WotReferenceTargetKind.ThingModel,
            new Dictionary<Uri, FederationDocument>(),
            InventoryIndex(new Fixture().Open(), description: InventorySource("thingdescription")));
        await Assert.That(inventory.ProjectionSelectors.Count).IsEqualTo(1);
        await Assert.That(inventory.Dependencies.Count).IsEqualTo(2);
        await Assert.That(inventory.Dependencies[0].Reference.Operator).IsEqualTo("td:ref");
        await Assert.That(inventory.Dependencies[1].Reference.SourcePointer).IsEqualTo("/p:projects/0");
    }

    [Test]
    [Arguments("first", "1.0.0", "1.0.0", null)]
    [Arguments("first", "2.0.0", null, "unsatisfied")]
    [Arguments("second", "2.0.0", "2.0.0", null)]
    [Arguments("missing", "1.0.0", null, "pin-mismatch")]
    [Arguments(null, "2.0.0", "2.0.0", null)]
    public async Task ProjectionSourceDigestAndFloorBothApplyWithoutFallback(
        string? pin, string floor, string? expectedVersion, string? expectedError)
    {
        var target = new Fixture().Open();
        var entry = new JsonObject
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json",
            ["tm:minVersion"] = floor
        };
        if (pin is not null)
        {
            entry["p:sourceDigest"] = "sha-256:" + (pin == "missing" ? new string('0', 64) :
                Digest(pin == "first" ? Fixture.First : Fixture.Second));
        }
        var containing = await RetainProjectionPlan(await ProjectionDocument(entry));
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = InventoryIndex(target);
        var source = (await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(containing.Document,
            uri, containing.Kind, contexts)).Single();
        if (expectedError is not null)
        {
            var failure = await Assert.That(async () => await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(
                containing, source, uri, contexts, index)).Throws<RegistryException>() ??
                throw new InvalidOperationException("Expected a pinned projection requirement failure without fallback.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(expectedError);
            await Assert.That(failure.Diagnostic.Path).IsEqualTo("/p:projects/0");
            await Assert.That(failure.Diagnostic.Message.Contains("'base'", StringComparison.Ordinal)).IsTrue();
        }
        else
        {
            var selected = await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(containing, source,
                uri, contexts, index);
            var expectedBytes = expectedVersion == "1.0.0" ? Fixture.First : Fixture.Second;
            await Assert.That(selected.Version.VersionId).IsEqualTo(expectedVersion);
            await Assert.That(selected.Version.ContentDigest).IsEqualTo("sha256:" + Digest(expectedBytes));
            await Assert.That((await Bytes(selected.Document)).SequenceEqual(expectedBytes)).IsTrue();
        }
    }

    [Test]
    [Arguments("41", "urn:test:line:a", "42", null, "unsatisfied")]
    [Arguments("45", "urn:test:line:a", null, null, "unsatisfied")]
    [Arguments("45", "urn:test:line:b", null, "45", null)]
    [Arguments("41", null, null, null, "compatibility-required")]
    [Arguments(null, "urn:test:line:a", "42", "43", null)]
    public async Task ProjectionSourcePinsRetainIncrementalLineAndFloorRequirements(
        string? pinVersion, string? line, string? floor, string? expectedVersion, string? expectedError)
    {
        var fixture = IncrementalFixture();
        var target = fixture.Open();
        var entry = new JsonObject
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json"
        };
        if (line is not null) { entry["uav:compatibilityLine"] = line; }
        if (floor is not null) { entry["uav:minVersion"] = floor; }
        if (pinVersion is not null)
        {
            entry["p:sourceDigest"] = "sha-256:" +
                Digest(await Bytes(fixture.Documents[Fixture.Resource + "/versions/" + pinVersion]));
        }
        var root = await ProjectionDocument(entry);
        root["uav:compatibilityLine"] = "urn:test:line:a";
        var containing = await RetainProjectionPlan(root);
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = InventoryIndex(target);
        var source = (await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(containing.Document,
            uri, containing.Kind, contexts)).Single();
        if (expectedError is not null)
        {
            var failure = await Assert.That(async () => await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(
                containing, source, uri, contexts, index)).Throws<RegistryException>() ??
                throw new InvalidOperationException("Expected a projection pin not to waive the explicit line or floor.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(expectedError);
        }
        else
        {
            var selected = await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(containing, source,
                uri, contexts, index);
            await Assert.That(selected.Version.VersionId).IsEqualTo(expectedVersion);
            await Assert.That(selected.Version.CompatibilityLine).IsEqualTo(line);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ProjectionSourceFloorsUseTheDeclaredAndCapturedKindRatherThanTheSelectorTerm(bool description)
    {
        var fixture = new Fixture();
        if (description)
        {
            fixture.Root["modelsource"]!["groups"]!["sets"]!["resources"]!["things"]!["singular"] = "thingdescription";
            fixture.Model = RegistryModel.Compile(RegistryJson.Parse(fixture.Root["modelsource"]!.ToJsonString()));
            foreach (var id in new[] { "1.0.0", "2.0.0" })
            {
                fixture.Documents[Fixture.Resource + "/versions/" + id] = new(Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["id"] = Fixture.WotId, ["version"] = new JsonObject { ["instance"] = id }
                }.ToJsonString()));
            }
        }
        var target = fixture.Open();
        var entry = new JsonObject
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:target",
            ["type"] = description ? "application/td+json" : "application/tm+json",
            [description ? "td:minVersion" : "tm:minVersion"] = "1.0.0"
        };
        var containing = await RetainProjectionPlan(await ProjectionDocument(entry));
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = new WotCapturedUriIdentityIndex([new(new("urn:inventory:target"), WotCapturedUriIdentityKind.WotId,
            new(target, "/sets/team", Fixture.Collection, Fixture.Resource,
                description ? "thingdescription" : "thingmodel", Fixture.WotId))]);
        var source = (await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(containing.Document,
            uri, containing.Kind, contexts)).Single();
        var selected = await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(containing, source,
            uri, contexts, index);
        await Assert.That(selected.Kind).IsEqualTo(description ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel);
        await Assert.That(selected.Version.VersionId).IsEqualTo(description ? "1.0.0" : "2.0.0");
    }

    [Test]
    [Arguments("sha256:0123")]
    [Arguments("sha-256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [Arguments("false")]
    public async Task ProjectionSourceRequirementsRejectMalformedDigestPins(string digest)
    {
        var entry = new JsonObject
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json",
            ["p:sourceDigest"] = digest == "false" ? JsonValue.Create(false) : JsonValue.Create(digest)
        };
        var containing = await RetainProjectionPlan(await ProjectionDocument(entry));
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var source = (await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(containing.Document,
            uri, containing.Kind, contexts)).Single();
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.ReadProjectionSourceRequirementsAsync(
            containing.Document, uri, containing.Kind, source, contexts, InventoryIndex(new Fixture().Open())))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an exact-byte lowercase digest spelling.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("2.0.0", "second", null)]
    [Arguments("3.0.0", "second", "unsatisfied")]
    [Arguments("1.0.0", "first", "digest")]
    public async Task ExactVersionProjectionSourcesStillCheckTheirOwnFloorAndDigest(
        string floor, string pin, string? error)
    {
        var target = new Fixture().Open();
        var entry = new JsonObject
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:exact", ["type"] = "application/tm+json",
            ["tm:minVersion"] = floor, ["p:sourceDigest"] = "sha-256:" + Digest(pin == "first" ? Fixture.First : Fixture.Second)
        };
        var containing = await RetainProjectionPlan(await ProjectionDocument(entry));
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = new WotCapturedUriIdentityIndex([new(new("urn:inventory:exact"), WotCapturedUriIdentityKind.DocumentView,
            new(target, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId, "2.0.0"))]);
        var source = (await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(containing.Document,
            uri, containing.Kind, contexts)).Single();
        if (error == "digest")
        {
            var failure = await Assert.That(async () => await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(
                containing, source, uri, contexts, index)).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected an exact URI association not to waive the byte digest.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        }
        else if (error is not null)
        {
            var failure = await Assert.That(async () => await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(
                containing, source, uri, contexts, index)).Throws<RegistryException>() ??
                throw new InvalidOperationException("Expected an exact URI association not to waive the source floor.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(error);
        }
        else
        {
            var result = await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(containing, source, uri, contexts, index);
            await Assert.That(result.Version.VersionId).IsEqualTo("2.0.0");
            await Assert.That(result.Version.ContentDigest).IsEqualTo("sha256:" + Digest(Fixture.Second));
        }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task ProjectionSourceSelectionHasInclusiveSharedWorkAndCancellation(int delta)
    {
        var containing = await RetainProjectionPlan(await ProjectionDocument(new()
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json",
            ["tm:minVersion"] = "1.0.0", ["p:sourceDigest"] = "sha-256:" + Digest(Fixture.Second)
        }));
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = InventoryIndex(new Fixture().Open());
        var source = (await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(containing.Document,
            uri, containing.Kind, contexts)).Single();
        var baseline = new FederationReadBudget();
        _ = await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(containing, source, uri, contexts, index, budget: baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(
                containing, source, uri, contexts, index, budget: budget)).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected one shared traversal and selection work limit.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var selected = await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(containing, source, uri,
                contexts, index, budget: budget);
            await Assert.That(selected.Version.VersionId).IsEqualTo("2.0.0");
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledBudget = new FederationReadBudget();
        await Assert.That(async () => await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(containing,
            source, uri, contexts, index, budget: canceledBudget, cancellationToken: canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(canceledBudget.Work).IsEqualTo(0L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSourceDigestPinsDoNotWaiveWrongKindOrCompetingFloors(bool competing)
    {
        var entry = new JsonObject
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json",
            ["p:sourceDigest"] = "sha-256:" + Digest(Fixture.First)
        };
        if (competing)
        {
            entry["tm:minVersion"] = "1.0.0";
            entry["uav:minVersion"] = "1.0.0";
        }
        else { entry["td:minVersion"] = "1.0.0"; }
        var containing = await RetainProjectionPlan(await ProjectionDocument(entry));
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var source = (await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(containing.Document,
            uri, containing.Kind, contexts)).Single();
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveCapturedProjectionSourceAsync(
            containing, source, uri, contexts, InventoryIndex(new Fixture().Open()))).Throws<RegistryException>() ??
            throw new InvalidOperationException("Expected a source pin not to waive floor syntax and target kind.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-floor");
    }

    [Test]
    public async Task ProjectionSourceDigestAliasesCannotCreateCompetingPins()
    {
        var entry = new JsonObject
        {
            ["p:sourceName"] = "base", ["href"] = "urn:inventory:tm", ["type"] = "application/tm+json",
            ["p:sourceDigest"] = "sha-256:" + Digest(Fixture.First),
            ["digest"] = "sha-256:" + Digest(Fixture.First)
        };
        var root = await ProjectionDocument(entry);
        root["@context"]!["digest"] = WotJsonLdBaseResolver.ProjectionVocabulary + "sourceDigest";
        var containing = await RetainProjectionPlan(root);
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var source = (await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(containing.Document,
            uri, containing.Kind, contexts)).Single();
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.ReadProjectionSourceRequirementsAsync(
            containing.Document, uri, containing.Kind, source, contexts, InventoryIndex(new Fixture().Open())))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a single authored source digest.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    private static async Task<JsonNode> ProjectionDocument(JsonObject entry)
    {
        var root = await RequirementDocument("{}", "[]");
        root["@context"]!["p"] = WotJsonLdBaseResolver.ProjectionVocabulary;
        root["@context"]!["Thing"] = "https://www.w3.org/2019/wot/td#Thing";
        root["@context"]!["id"] = "@id";
        root["@context"]!["p:scenario"] = new JsonObject
        {
            ["@id"] = WotJsonLdBaseResolver.ProjectionVocabulary + "scenario", ["@type"] = "@id"
        };
        root["@context"]!["p:projects"] = new JsonObject
        {
            ["@id"] = WotJsonLdBaseResolver.ProjectionVocabulary + "projects", ["@container"] = "@list"
        };
        root["@type"] = new JsonArray("tm:ThingModel", "p:projection");
        root["id"] = "urn:example:projection";
        root["p:scenario"] = "urn:example:scenario";
        root["p:projects"] = new JsonArray(entry);
        return root;
    }

    private static async Task<WotResolvedDocument> RetainProjectionPlan(JsonNode root)
    {
        root["id"] = Fixture.WotId;
        root["version"] = JsonNode.Parse("""{"model":"1.0.0"}""");
        var fixture = new Fixture();
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new(Encoding.UTF8.GetBytes(root.ToJsonString()));
        return await WotRegistryResolver.ResolveAsync(fixture.Open(), Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RootLineDeclarationNeverSelectsTheTargetLineOfARootReference(bool incremental)
    {
        var source = incremental ? IncrementalFixture().Open() : new Fixture().Open();
        var root = await RequirementDocument("{}", "[]");
        root["tm:ref"] = "urn:inventory:tm";
        root["uav:compatibilityLine"] = "urn:test:line:a";
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = InventoryIndex(source);
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, contexts, index)).Dependencies.Single();
        await Assert.That(dependency.Reference.SourcePointer).IsEqualTo("");
        var request = await WotJsonLdBaseResolver.ReadReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingModel, dependency, contexts, index);
        await Assert.That(request.CompatibilityLine).IsNull();
        var resource = new WotVersionResource(Fixture.WotId, WotReferenceTargetKind.ThingModel,
            incremental ? "41" : "1.0.0", false,
            [new(incremental ? "41" : "1.0.0", "", "") { CompatibilityLine = "urn:test:line:a" }])
        {
            Profile = incremental ? WotVersionProfile.Incremental : WotVersionProfile.SemVer
        };
        if (incremental)
        {
            var failure = await Assert.That(() => WotVersionResolver.SelectVersionId(resource, request))
                .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected no inherited target line.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("compatibility-required");
        }
        else { await Assert.That(WotVersionResolver.SelectVersionId(resource, request)).IsEqualTo("1.0.0"); }
    }

    [Test]
    [Arguments(false, "urn:test:line:a", "\"42\"", "43", null)]
    [Arguments(false, "urn:test:line:a", null, "43", null)]
    [Arguments(false, "urn:test:line:b", null, "45", null)]
    [Arguments(false, "urn:test:line:a", "\"44\"", null, "unsatisfied")]
    [Arguments(true, "urn:test:line:a", "\"42\"", null, "unsatisfied")]
    [Arguments(true, "urn:test:line:b", null, null, "unsatisfied")]
    [Arguments(false, null, "\"42\"", null, "compatibility-required")]
    [Arguments(false, null, null, null, "compatibility-required")]
    [Arguments(false, "relative", null, null, "invalid-line")]
    [Arguments(false, "urn:test:line:a", "\"1.0.0\"", null, "invalid-version")]
    public async Task IncrementalCapturedDependencyUsesExplicitLineAndNumericFloor(
        bool sticky, string? line, string? floor, string? expectedVersion, string? expectedError)
    {
        var fixture = IncrementalFixture();
        fixture.Root["sets"]!["team"]!["things"]!["a"]!["meta"]!["defaultversionsticky"] = sticky;
        var target = fixture.Open(context: new("incremental", "urn:test:incremental", "captured", true));
        var owner = new Fixture();
        var authored = await RequirementDocument("""{"a":{"tm:ref":"urn:inventory:incremental"}}""", "[]");
        authored["id"] = Fixture.WotId;
        authored["version"] = JsonNode.Parse("""{"model":"1.0.0","instance":"999.0.0"}""");
        authored["uav:compatibilityLine"] = "urn:never:inherit";
        var occurrence = authored["properties"]!["a"]!;
        if (line is not null) { occurrence["uav:compatibilityLine"] = line; }
        if (floor is not null) { occurrence["uav:minVersion"] = JsonNode.Parse(floor); }
        owner.Documents[Fixture.Resource + "/versions/1.0.0"] = new(Encoding.UTF8.GetBytes(authored.ToJsonString()));
        var source = owner.Open();
        var containing = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        var identity = new WotCapturedResourceIdentity(target, "/sets/team", Fixture.Collection, Fixture.Resource,
            "thingmodel", Fixture.WotId);
        var index = new WotCapturedUriIdentityIndex([new(new("urn:inventory:incremental"),
            WotCapturedUriIdentityKind.WotId, identity)]);
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(containing.Document,
            uri, containing.Kind, contexts, index)).Dependencies.Single();
        if (expectedError is not null)
        {
            var failure = await Assert.That(async () => await WotRegistryResolver.ResolveCapturedDependencyAsync(
                containing, dependency.Reference, uri, contexts, index)).Throws<RegistryException>() ??
                throw new InvalidOperationException("Expected explicit incremental requirement failure.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(expectedError);
        }
        else
        {
            var result = await WotRegistryResolver.ResolveCapturedDependencyAsync(containing, dependency.Reference,
                uri, contexts, index);
            await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo(expectedVersion);
            await Assert.That(result.ContainingDocument.Version.Profile).IsEqualTo(WotVersionProfile.Incremental);
            await Assert.That(result.ContainingDocument.Version.CompatibilityLine).IsEqualTo(line);
            var bytes = await Bytes(fixture.Documents[Fixture.Resource + "/versions/" + expectedVersion]);
            await Assert.That(result.ContainingDocument.Version.ContentDigest).IsEqualTo("sha256:" + Digest(bytes));
        }
        await Assert.That(containing.Version.VersionId).IsEqualTo("1.0.0");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MixedTypeLinkRequirementsApplyImplicitRulesOnlyToSemVerTargets(bool additionalSemVer)
    {
        var semVer = new Fixture().Open();
        var incremental = IncrementalFixture().Open(context: new("incremental", "urn:test:incremental", "captured", true));
        var index = new WotCapturedUriIdentityIndex([
            new(new("urn:inventory:tm"), WotCapturedUriIdentityKind.WotId,
                new(semVer, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId)),
            new(new("urn:inventory:incremental"), WotCapturedUriIdentityKind.WotId,
                new(incremental, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId))
        ]);
        var root = await RequirementDocument("{}", """
            [{"rel":"type","href":"urn:inventory:tm"},
             {"rel":"type","href":"urn:inventory:incremental","uav:compatibilityLine":"urn:test:line:a"}]
            """);
        if (additionalSemVer)
        {
            root["links"]!.AsArray().Add(JsonNode.Parse("""{"rel":"type","href":"urn:inventory:tm","uav:minVersion":"1.0.0"}"""));
        }
        root["version"] = JsonNode.Parse("""{"model":"1.5.0"}""");
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var dependencies = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts, index)).Dependencies;
        var numeric = await WotJsonLdBaseResolver.ReadReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, dependencies[1], contexts, index);
        await Assert.That(numeric.MinimumVersion).IsNull();
        await Assert.That(numeric.ReferencingThingDescriptionModelVersion).IsNull();
        await Assert.That(numeric.CompatibilityLine).IsEqualTo("urn:test:line:a");
        foreach (var dependency in dependencies.Where(item => item.Reference.Href == "urn:inventory:tm"))
        {
            if (additionalSemVer)
            {
                var failure = await Assert.That(async () => await WotJsonLdBaseResolver.ReadReferenceRequirementsAsync(
                    document, uri, WotReferenceTargetKind.ThingDescription, dependency, contexts, index))
                    .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected SemVer-subset ambiguity.");
                await Assert.That(failure.Diagnostic.Code).IsEqualTo("ambiguous-floor");
            }
            else
            {
                var request = await WotJsonLdBaseResolver.ReadReferenceRequirementsAsync(document, uri,
                    WotReferenceTargetKind.ThingDescription, dependency, contexts, index);
                await Assert.That(request.ThingModelTypeLinkCount).IsEqualTo(1);
                await Assert.That(request.ReferencingThingDescriptionModelVersion).IsEqualTo("1.5.0");
                await Assert.That(request.AllThingModelTypeLinksHaveMinimumVersion).IsFalse();
                await Assert.That(WotVersionResolver.SelectVersionId(new(Fixture.WotId, WotReferenceTargetKind.ThingModel,
                    "1.0.0", false, [new("1.0.0", "", ""), new("2.0.0", "", "")]), request)).IsEqualTo("2.0.0");
            }
        }
    }

    [Test]
    [Arguments("""{"uav:minVersion":"1.0.0"}""", null)]
    [Arguments("""{"uav:minVersion":"1.0.0","tm:minVersion":"1.0.0"}""", "invalid-floor")]
    [Arguments("""{"uav:compatibilityLine":"urn:test:line:a"}""", "invalid-line")]
    [Arguments("""{"uav:compatibilityLine":false}""", "invalid-line")]
    public async Task ProfileAwareRequirementsNormalizeOrRejectDeployedSelectors(string selectors, string? error)
    {
        var root = await RequirementDocument("""{"a":{"tm:ref":"urn:inventory:tm"}}""", "[]");
        foreach (var member in JsonNode.Parse(selectors)!.AsObject())
        {
            root["properties"]!["a"]![member.Key] = member.Value!.DeepClone();
        }
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = InventoryIndex(new Fixture().Open());
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, contexts, index)).Dependencies.Single();
        async Task<string> Select()
        {
            var request = await WotJsonLdBaseResolver.ReadReferenceRequirementsAsync(document, uri,
                WotReferenceTargetKind.ThingModel, dependency, contexts, index);
            return WotVersionResolver.SelectVersionId(new(Fixture.WotId, WotReferenceTargetKind.ThingModel,
                "1.0.0", false, [new("1.0.0", "", ""), new("2.0.0", "", "")]), request);
        }
        if (error is not null)
        {
            var failure = await Assert.That(async () => { _ = await Select(); }).Throws<RegistryException>() ??
                throw new InvalidOperationException("Expected explicit selector rejection.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(error);
        }
        else { await Assert.That(await Select()).IsEqualTo("2.0.0"); }
    }

    [Test]
    public async Task MixedTypeLinkRequirementsRejectUnknownOtherProfileBeforeFloorAmbiguity()
    {
        var fixture = IncrementalFixture();
        var model = JsonNode.Parse(fixture.Model.Source.RootElement.GetRawText())!;
        model["attributes"]!["wotversionprofile"]!["default"] = "future";
        model["attributes"]!["wotversionprofile"]!["enum"] = new JsonArray("future");
        fixture.Model = RegistryModel.Compile(RegistryJson.Parse(model.ToJsonString()));
        fixture.Root["modelsource"] = model;
        var other = fixture.Open(context: new("unknown", "urn:test:unknown-profile", "captured", true));
        var source = new Fixture().Open();
        var index = new WotCapturedUriIdentityIndex([
            new(new("urn:inventory:tm"), WotCapturedUriIdentityKind.WotId,
                new(source, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId)),
            new(new("urn:inventory:other"), WotCapturedUriIdentityKind.WotId,
                new(other, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId))
        ]);
        var root = await RequirementDocument("{}", """
            [{"rel":"type","href":"urn:inventory:tm","tm:minVersion":false},
             {"rel":"type","href":"urn:inventory:other"}]
            """);
        root["version"] = JsonNode.Parse("""{"model":false}""");
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts, index)).Dependencies[0];
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.ReadReferenceRequirementsAsync(document,
            uri, WotReferenceTargetKind.ThingDescription, dependency, contexts, index)).Throws<RegistryException>() ??
            throw new InvalidOperationException("Expected discovery of every type link's captured profile.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unknown-profile");
    }

    [Test]
    public async Task ExactIncrementalDependencyBypassesUnusableSelectors()
    {
        var source = IncrementalFixture().Open();
        var index = new WotCapturedUriIdentityIndex([
            new(new("urn:inventory:exact"), WotCapturedUriIdentityKind.DocumentView,
                new(source, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId, "45"))
        ]);
        var root = await RequirementDocument("""
            {"a":{"tm:ref":"urn:inventory:exact",
            "uav:minVersion":false,"tm:minVersion":"wrong","uav:compatibilityLine":false}}
            """, "[]");
        root["id"] = Fixture.WotId;
        root["version"] = JsonNode.Parse("""{"model":"1.0.0"}""");
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, contexts, index)).Dependencies.Single();
        var request = await WotJsonLdBaseResolver.ReadReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingModel, dependency, contexts, index);
        await Assert.That(request.MinimumVersion).IsNull();
        await Assert.That(request.CompatibilityLine).IsNull();
        await Assert.That(request.VersionIdPin).IsEqualTo("45");
        var fixture = new Fixture();
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = document;
        var containing = await WotRegistryResolver.ResolveAsync(fixture.Open(), Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        var result = await WotRegistryResolver.ResolveCapturedDependencyAsync(containing, dependency.Reference,
            uri, contexts, index);
        await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo("45");
        await Assert.That(result.ContainingDocument.Version.CompatibilityLine).IsEqualTo("urn:test:line:b");
    }

    private static async Task<JsonNode> RequirementDocument(string properties, string links)
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument(properties, links)))!;
        root["@context"]!["uav"] = "http://opcfoundation.org/UA/WoT-Binding/";
        return root;
    }

    private static Fixture IncrementalFixture()
    {
        var fixture = new Fixture();
        var model = JsonNode.Parse(fixture.Model.Source.RootElement.GetRawText())!;
        model["attributes"] = JsonNode.Parse("""
            {"wotversionprofile":{"type":"string","required":true,"readonly":true,"strict":true,
             "enum":["incremental"],"default":"incremental"}}
            """);
        var definition = model["groups"]!["sets"]!["resources"]!["things"]!;
        definition["versionmode"] = "manual";
        definition["singleversionroot"] = false;
        definition["attributes"] = JsonNode.Parse("""{"compatibilityline":{"type":"uriabsolute"}}""");
        fixture.Model = RegistryModel.Compile(RegistryJson.Parse(model.ToJsonString()));
        fixture.Root["modelsource"] = model;
        var resource = fixture.Root["sets"]!["team"]!["things"]!["a"]!;
        var template = resource["versions"]!["1.0.0"]!.DeepClone();
        resource["versions"] = new JsonObject();
        resource["versionscount"] = 3;
        resource["meta"]!["defaultversionid"] = "41";
        resource["meta"]!["defaultversionurl"] = "#/sets/team/things/a/versions/41";
        fixture.Documents.Clear();
        foreach (var id in new[] { "41", "43", "45" })
        {
            var version = template.DeepClone();
            var line = id == "45" ? "urn:test:line:b" : "urn:test:line:a";
            version["versionid"] = id;
            version["xid"] = Fixture.Resource + "/versions/" + id;
            version["self"] = "#/sets/team/things/a/versions/" + id;
            version["compatibilityline"] = line;
            resource["versions"]![id] = version;
            fixture.Documents[Fixture.Resource + "/versions/" + id] = new(Encoding.UTF8.GetBytes(
                new JsonObject
                {
                    ["id"] = Fixture.WotId, ["version"] = new JsonObject { ["model"] = id }
                }.ToJsonString()));
        }
        return fixture;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AuthoredDependencyResolutionAppliesFloorsWithoutLiveFallbackOrWeakeningStickyDefaults(bool sticky)
    {
        var fixture = new Fixture();
        fixture.Root["sets"]!["team"]!["things"]!["a"]!["meta"]!["defaultversionsticky"] = sticky;
        var authored = JsonNode.Parse(await Bytes(InventoryDocument(
            """{"a":{"tm:ref":"urn:test:device:v1","tm:minVersion":"2.0.0"}}""", "[]")))!;
        authored["id"] = Fixture.WotId;
        authored["version"] = JsonNode.Parse("""{"model":"1.0.0"}""");
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new(Encoding.UTF8.GetBytes(authored.ToJsonString()),
            documentBase: "https://example.test/entry.json");
        var source = fixture.Open();
        var containing = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source);
        var reference = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(containing.Document,
            uri, containing.Kind, contexts, index)).Dependencies.Single().Reference;
        if (sticky)
        {
            var failure = await Assert.That(async () => await WotRegistryResolver.ResolveCapturedSemVerDependencyAsync(
                containing, reference, uri, contexts, index)).Throws<RegistryException>() ??
                throw new InvalidOperationException("Expected no newer-Version retry for an unsatisfied sticky default.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
        }
        else
        {
            var result = await WotRegistryResolver.ResolveCapturedSemVerDependencyAsync(containing, reference, uri, contexts, index);
            await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo("2.0.0");
            await Assert.That(result.ContainingDocument.Version.ContentDigest).IsEqualTo("sha256:" + Digest(Fixture.Second));
            await Assert.That(result.Value.GetProperty("version").GetProperty("model").GetString()).IsEqualTo("2.0.0");
        }
        await Assert.That(containing.Version.VersionId).IsEqualTo("1.0.0");
    }

    [Test]
    public async Task ExactCapturedDependencyBypassesMalformedFloorsButRetainsExactBytes()
    {
        var fixture = new Fixture();
        var authored = JsonNode.Parse(await Bytes(InventoryDocument(
            """{"a":{"tm:ref":"urn:target:exact","tm:minVersion":false}}""", "[]")))!;
        authored["id"] = Fixture.WotId;
        authored["version"] = JsonNode.Parse("""{"model":"1.0.0"}""");
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new(Encoding.UTF8.GetBytes(authored.ToJsonString()));
        var source = fixture.Open();
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source,
            [new(source, new("urn:target:exact"), WotCapturedUriIdentityKind.DocumentView, Fixture.Resource + "/versions/2.0.0")]);
        var containing = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        var uri = new Uri("https://example.test/entry.json");
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(containing.Document,
            uri, containing.Kind, contexts, index)).Dependencies.Single();
        var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(containing.Document, uri,
            containing.Kind, dependency, contexts);
        await Assert.That(request.MinimumVersion).IsNull();
        await Assert.That(request.VersionIdPin).IsEqualTo("2.0.0");
        var result = await WotRegistryResolver.ResolveCapturedSemVerDependencyAsync(containing, dependency.Reference, uri, contexts, index);
        await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo("2.0.0");
        await Assert.That(result.ContainingDocument.Version.ContentDigest).IsEqualTo("sha256:" + Digest(Fixture.Second));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AuthoredRequirementsDoNotApplyTdVersionsToOrdinaryTdReferences(bool invalidVersion)
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument("""{"a":{"td:ref":"urn:inventory:td"}}""", "[]")))!;
        root["version"] = JsonNode.Parse(invalidVersion ? """{"model":false,"instance":"bad"}""" : """{"model":"9.0.0","instance":"9.0.0"}""");
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts,
            InventoryIndex(new Fixture().Open(), description: InventorySource("thingdescription")));
        var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, inventory.Dependencies.Single(), contexts);
        await Assert.That(request.MinimumVersion).IsNull();
        await Assert.That(request.ReferencingThingDescriptionModelVersion).IsNull();
        await Assert.That(request.MinimumVersionKind).IsNull();
    }

    [Test]
    [Arguments(1, true)]
    [Arguments(2, false)]
    public async Task AuthoredTypeLinkRequirementsAllowMissingExplicitFloorsWhenUnambiguous(int count, bool implicitFloor)
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument("{}", count == 1
            ? """[{"rel":"type","href":"urn:inventory:tm"}]"""
            : """[{"rel":"type","href":"urn:inventory:tm"},{"rel":"type","href":"urn:inventory:tm"}]""")))!;
        if (implicitFloor) { root["version"] = JsonNode.Parse("""{"model":"1.0.0"}"""); }
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts, InventoryIndex(new Fixture().Open()));
        var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, inventory.Dependencies[0], contexts);
        await Assert.That(request.MinimumVersion).IsNull();
        await Assert.That(request.ThingModelTypeLinkCount).IsEqualTo(count);
        await Assert.That(request.AllThingModelTypeLinksHaveMinimumVersion).IsFalse();
        await Assert.That(request.ReferencingThingDescriptionModelVersion).IsEqualTo(implicitFloor ? "1.0.0" : null);
        await Assert.That(WotVersionResolver.SelectVersionId(new(Fixture.WotId, WotReferenceTargetKind.ThingModel,
            "1.0.0", false, [new("1.0.0", "", "")]), request)).IsEqualTo("1.0.0");
    }

    [Test]
    [Arguments("href")]
    [Arguments("operator")]
    [Arguments("member")]
    [Arguments("target-kind")]
    public async Task AuthoredRequirementsRejectInventedOccurrenceOrTargetMeaning(string change)
    {
        var document = InventoryDocument("""{"a":{"tm:ref":"urn:inventory:tm"}}""", "[]");
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, contexts, InventoryIndex(new Fixture().Open()));
        var dependency = inventory.Dependencies.Single();
        dependency = change switch
        {
            "href" => dependency with { Reference = dependency.Reference with { Href = "urn:forged" } },
            "operator" => dependency with { Reference = dependency.Reference with { Operator = "tm:extends" } },
            "member" => dependency with { Reference = dependency.Reference with { PropertyName = "missing" } },
            _ => dependency with { Target = new(dependency.Target.Source, "/sets/team", Fixture.Collection,
                Fixture.Resource, "thingdescription", Fixture.WotId) }
        };
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document,
            uri, WotReferenceTargetKind.ThingModel, dependency, contexts)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected a requirement derived only from actual authored reference meaning.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(-1, false)]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(-1, true)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    public async Task AuthoredRequirementsShareInclusiveWorkAndCancellation(int delta, bool profileAware)
    {
        var document = InventoryDocument("""{"a":{"tm:ref":"urn:inventory:tm","tm:minVersion":"1.0.0"}}""", "[]");
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var index = InventoryIndex(new Fixture().Open());
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, contexts, index)).Dependencies.Single();
        ValueTask<WotVersionResolutionRequest> Read(FederationReadBudget budget, CancellationToken token = default) =>
            profileAware
                ? WotJsonLdBaseResolver.ReadReferenceRequirementsAsync(document, uri,
                    WotReferenceTargetKind.ThingModel, dependency, contexts, index, budget, token)
                : WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
                    WotReferenceTargetKind.ThingModel, dependency, contexts, budget, token);
        var baseline = new FederationReadBudget();
        _ = await Read(baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await Read(budget)).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected shared requirement traversal limit.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var request = await Read(budget);
            await Assert.That(request.MinimumVersion).IsEqualTo("1.0.0");
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledBudget = new FederationReadBudget();
        await Assert.That(async () => await Read(canceledBudget, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(canceledBudget.Work).IsEqualTo(0L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AuthoredSemVerRequirementsUseExpandedFloorMembersAndTargetKind(bool description)
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument(
            description ? """{"a":{"td:ref":"urn:inventory:td","floor":"1.0.0"}}"""
                : """{"a":{"tm:ref":"urn:inventory:tm","floor":"1.0.0"}}""", "[]")))!;
        root["@context"]!["floor"] = description ? "td:minVersion" : "tm:minVersion";
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var index = InventoryIndex(new Fixture().Open(), description: InventorySource("thingdescription"));
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, new Dictionary<Uri, FederationDocument>(), index);
        var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingModel, inventory.Dependencies.Single(), new Dictionary<Uri, FederationDocument>());
        await Assert.That(request.MinimumVersion).IsEqualTo("1.0.0");
        await Assert.That(request.MinimumVersionKind).IsEqualTo(description ? WotMinimumVersionKind.ThingDescription : WotMinimumVersionKind.ThingModel);
        await Assert.That(request.TargetKind).IsEqualTo(description ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel);
        await Assert.That(request.VersionIdPin).IsNull();
        await Assert.That(request.DigestPin).IsNull();
        var candidates = new WotVersionResource(Fixture.WotId, request.TargetKind, "1.0.0", false,
            [new("1.0.0", "", ""), new("2.0.0", "", "")]);
        await Assert.That(WotVersionResolver.SelectVersionId(candidates, request)).IsEqualTo(description ? "1.0.0" : "2.0.0");
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    public async Task AuthoredTypeLinkRequirementsCarryImplicitFloorAndWholeLinkInventory(int links, bool hasImplicit)
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument("{}",
            links == 1 ? """[{"rel":"type","href":"urn:inventory:tm","tm:minVersion":"1.0.0"}]"""
                : """[{"rel":"type","href":"urn:inventory:tm","tm:minVersion":"1.0.0"},{"rel":"type","href":"urn:inventory:tm","tm:minVersion":"1.2.0"}]""")))!;
        if (hasImplicit) { root["version"] = JsonNode.Parse("""{"model":"1.5.0"}"""); }
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, new Dictionary<Uri, FederationDocument>(), InventoryIndex(new Fixture().Open()));
        var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, inventory.Dependencies[0], new Dictionary<Uri, FederationDocument>());
        await Assert.That(request.Operator).IsEqualTo(WotReferenceOperator.ThingModelTypeLink);
        await Assert.That(request.MinimumVersion).IsEqualTo("1.0.0");
        await Assert.That(request.ReferencingThingDescriptionModelVersion).IsEqualTo(hasImplicit ? "1.5.0" : null);
        await Assert.That(request.ThingModelTypeLinkCount).IsEqualTo(links);
        await Assert.That(request.AllThingModelTypeLinksHaveMinimumVersion).IsTrue();
        var candidates = new WotVersionResource(Fixture.WotId, WotReferenceTargetKind.ThingModel, "1.0.0", true,
            [new("1.0.0", "", ""), new("1.5.0", "", "")]);
        if (hasImplicit)
        {
            var failure = await Assert.That(() => WotVersionResolver.SelectVersionId(candidates, request)).Throws<RegistryException>() ??
                throw new InvalidOperationException("Expected explicit lower floor not to weaken version.model.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
        }
        else { await Assert.That(WotVersionResolver.SelectVersionId(candidates, request)).IsEqualTo("1.0.0"); }
    }

    [Test]
    public async Task AuthoredTypeLinkRequirementsReportGlobalAmbiguityBeforeMalformedSelectedFloor()
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument("{}",
            """[{"rel":"type","href":"urn:inventory:tm","tm:minVersion":false},{"rel":"type","href":"urn:inventory:tm"}]""")))!;
        root["version"] = JsonNode.Parse("""{"model":"not-semver"}""");
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, new Dictionary<Uri, FederationDocument>(), InventoryIndex(new Fixture().Open()));
        foreach (var dependency in inventory.Dependencies)
        {
            var failure = await Assert.That(async () => await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document,
                uri, WotReferenceTargetKind.ThingDescription, dependency, new Dictionary<Uri, FederationDocument>()))
                .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected whole-document type-link floor ambiguity.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("ambiguous-floor");
        }
    }

    [Test]
    [Arguments("false")]
    [Arguments("null")]
    [Arguments("\"not-semver\"")]
    [Arguments("\"0.0.0-1\"")]
    [Arguments("\"1.0.0+build\"")]
    public async Task AuthoredTypeLinkRequirementsRejectNonSemVerImplicitFloor(string model)
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument("{}",
            """[{"rel":"type","href":"urn:inventory:tm","tm:minVersion":"1.0.0"}]""")))!;
        root["version"] = JsonNode.Parse("""{"model":""" + model + "}");
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts, InventoryIndex(new Fixture().Open()))).Dependencies.Single();
        var failure = await Assert.That(async () =>
        {
            var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
                WotReferenceTargetKind.ThingDescription, dependency, contexts);
            _ = WotVersionResolver.SelectVersionId(new(Fixture.WotId, WotReferenceTargetKind.ThingModel, "1.0.0", false,
                [new("1.0.0", "", "")]), request);
        }).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an invalid authored implicit floor.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    [Arguments("\"^1.0.0\"", "invalid-version")]
    [Arguments("\"0.0.0-1\"", "invalid-version")]
    [Arguments("false", "invalid-version")]
    [Arguments("\"1.0.0+build\"", "invalid-version")]
    public async Task AuthoredSemVerRequirementsRejectInvalidFloorsDuringRequestOrSelection(string floor, string code)
    {
        var document = InventoryDocument("""{"a":{"tm:ref":"urn:inventory:tm","tm:minVersion":""" + floor + "}}", "[]");
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, contexts, InventoryIndex(new Fixture().Open()));
        var failure = await Assert.That(async () =>
        {
            var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
                WotReferenceTargetKind.ThingModel, inventory.Dependencies.Single(), contexts);
            _ = WotVersionResolver.SelectVersionId(new(Fixture.WotId, WotReferenceTargetKind.ThingModel, "1.0.0", false,
                [new("1.0.0", "", "")]), request);
        }).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected invalid authored minimum version.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("""{"td:minVersion":"1.0.0"}""")]
    [Arguments("""{"tm:minVersion":"1.0.0","againFloor":"2.0.0"}""")]
    public async Task AuthoredSemVerRequirementsRejectWrongKindOrCompetingFloorAliases(string members)
    {
        var root = JsonNode.Parse(await Bytes(InventoryDocument("""{"a":{"tm:ref":"urn:inventory:tm"}}""", "[]")))!;
        root["@context"]!["againFloor"] = "tm:minVersion";
        foreach (var member in JsonNode.Parse(members)!.AsObject()) { root["properties"]!["a"]![member.Key] = member.Value!.DeepClone(); }
        var document = new FederationDocument(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, contexts, InventoryIndex(new Fixture().Open()));
        var failure = await Assert.That(async () =>
        {
            var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
                WotReferenceTargetKind.ThingModel, inventory.Dependencies.Single(), contexts);
            _ = WotVersionResolver.SelectVersionId(new(Fixture.WotId, WotReferenceTargetKind.ThingModel, "1.0.0", false,
                [new("1.0.0", "", "")]), request);
        }).Throws<RegistryException>() ?? throw new InvalidOperationException("Expected conflicting floor semantics.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-floor");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SchemaDescriptionExclusionRetainsResourceOrExactVersionScopeWithoutReads(bool exact)
    {
        var source = InventorySource("schema");
        var index = new WotCapturedUriIdentityIndex(
        [
            new(new("urn:inventory:schema"), WotCapturedUriIdentityKind.DocumentView,
                new(source, "/sets/team", Fixture.Collection, Fixture.Resource, "schema", null, exact ? "1.0.0" : null))
        ]);
        var document = InventoryDocument("{}", """[{"rel":"describedby","href":"urn:inventory:schema#/definitions/value"}]""");
        var budget = new FederationReadBudget();
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document,
            new("https://example.test/entry.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>(), index, budget);
        await Assert.That(inventory.Dependencies.Count).IsEqualTo(0);
        var description = inventory.SchemaDescriptions.Single();
        await Assert.That(description.Reference.SourcePointer).IsEqualTo("/links/0");
        await Assert.That(description.Reference.Href).IsEqualTo("urn:inventory:schema#/definitions/value");
        await Assert.That(description.Target.VersionId).IsEqualTo(exact ? "1.0.0" : null);
        await Assert.That(description.Target.ResourceKind).IsEqualTo("schema");
        await Assert.That(budget.Requests).IsEqualTo(0L);
    }

    [Test]
    [Arguments("urn:inventory:tm", FederationErrorCode.InvalidPackage)]
    [Arguments("urn:inventory:absent", FederationErrorCode.NotFound)]
    public async Task SchemaDescriptionExclusionRejectsUnestablishedSchemaTargets(string href, FederationErrorCode code)
    {
        var index = InventoryIndex(new Fixture().Open());
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(
            InventoryDocument("{}", "[{\"rel\":\"describedby\",\"href\":" + JsonSerializer.Serialize(href) + "}]"),
            new("https://example.test/entry.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>(), index)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected no Schema exclusion without captured Schema identity.");
        await Assert.That(failure.Code).IsEqualTo(code);
    }

    [Test]
    public async Task SchemaDescriptionExclusionRejectsAmbiguousIdentityAndWrongOperatorButBypassesLocalLookup()
    {
        var source = InventorySource("schema");
        var target = new WotCapturedResourceIdentity(source, "/sets/team", Fixture.Collection, Fixture.Resource, "schema", null);
        var exact = new WotCapturedResourceIdentity(source, "/sets/team", Fixture.Collection, Fixture.Resource, "schema", null, "1.0.0");
        var index = new WotCapturedUriIdentityIndex(
        [
            new(new("urn:inventory:schema"), WotCapturedUriIdentityKind.DocumentView, target),
            new(new("urn:inventory:schema"), WotCapturedUriIdentityKind.DocumentView, exact)
        ]);
        var uri = new Uri("https://example.test/entry.json");
        var external = new WotAuthoredReferenceOccurrence("/links/0", "href", "describedby", "urn:inventory:schema",
            WotReferenceUriResolver.Resolve("urn:inventory:schema", uri, uri));
        var failure = await Assert.That(() => index.ClassifySchemaDescription(external)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected competing captured Schema scopes.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Ambiguous);
        var wrong = await Assert.That(() => index.ClassifySchemaDescription(external with { Operator = "type" }))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected literal operator validation.");
        await Assert.That(wrong.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        var local = external with { Href = "#/definitions/value", Target = WotReferenceUriResolver.Resolve("#/definitions/value", uri, uri) };
        var budget = new FederationReadBudget(new(maxWork: 1));
        await Assert.That(index.ClassifySchemaDescription(local, budget)).IsNull();
        await Assert.That(budget.Work).IsEqualTo(1L);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => index.ClassifySchemaDescription(local, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CombinedInventoryPreservesDistinctOccurrencesAndSeparatesSchemaDescriptionCandidates()
    {
        var sourceBudget = new FederationReadBudget();
        var source = new Fixture().Open(budget: sourceBudget);
        var index = InventoryIndex(source, InventorySource("schema"));
        var work = sourceBudget.Work;
        var document = InventoryDocument("""
            {"a/b~c":{"tm:ref":"urn:inventory:tm#/properties/value"},"other":{"again":"urn:inventory:tm"}}
            """, """[{"rel":"tm:extends","href":"urn:inventory:tm"},{"rel":"describedby","href":"urn:inventory:schema"}]""");
        var original = await Bytes(document);
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document,
            new("https://example.test/entry.json"), WotReferenceTargetKind.ThingModel, new Dictionary<Uri, FederationDocument>(), index);
        await Assert.That(inventory.Dependencies.Count).IsEqualTo(3);
        await Assert.That(inventory.Dependencies.Select(value => value.Reference.SourcePointer).SequenceEqual(
            ["/properties/a~1b~0c", "/properties/other", "/links/0"])).IsTrue();
        await Assert.That(inventory.Dependencies[0].Reference.Href).IsEqualTo("urn:inventory:tm#/properties/value");
        await Assert.That(inventory.Dependencies[1].Reference.PropertyName).IsEqualTo("again");
        await Assert.That(inventory.Dependencies[1].Reference.Operator).IsEqualTo("tm:ref");
        await Assert.That(inventory.Dependencies.All(value => value.Target.ResourceXid == Fixture.Resource)).IsTrue();
        await Assert.That(inventory.SchemaDescriptions.Single().Reference.SourcePointer).IsEqualTo("/links/1");
        await Assert.That(inventory.SchemaDescriptions.Single().Reference.Href).IsEqualTo("urn:inventory:schema");
        await Assert.That(inventory.SchemaDescriptions.Single().Target.ResourceKind).IsEqualTo("schema");
        await Assert.That(inventory.ProjectionSelectors.Count).IsEqualTo(0);
        await Assert.That(sourceBudget.Work).IsEqualTo(work);
        await Assert.That((await Bytes(document)).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task CombinedInventoryExcludesLocalAndSchemaReferencesBeforePointerAmbiguityChecks()
    {
        var source = new Fixture().Open();
        var schema = InventorySource("schema");
        var index = InventoryIndex(source, schema);
        var document = InventoryDocument("""
            {"local":{"tm:ref":"#/properties/local"},
             "mixed":{"tm:ref":"urn:inventory:schema","again":"urn:inventory:tm"},
             "schemas":{"tm:ref":"urn:inventory:schema","again":"urn:inventory:schema"}}
            """, """[{"rel":"tm:extends","href":"#"}]""");
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document,
            new("https://example.test/entry.json"), WotReferenceTargetKind.ThingModel, new Dictionary<Uri, FederationDocument>(), index);
        await Assert.That(inventory.Dependencies.Count).IsEqualTo(1);
        await Assert.That(inventory.Dependencies.Single().Reference.SourcePointer).IsEqualTo("/properties/mixed");
        await Assert.That(inventory.Dependencies.Single().Reference.PropertyName).IsEqualTo("again");
    }

    [Test]
    [Arguments("""{"a":{"tm:ref":"urn:inventory:tm","again":"urn:inventory:tm"}}""", "[]")]
    [Arguments("{}", """[{"rel":"tm:extends","href":"urn:inventory:tm","tm:ref":"urn:inventory:tm"}]""")]
    public async Task CombinedInventoryRejectsCompetingOrdinaryOrCrossKindOccurrences(string properties, string links)
    {
        var source = new Fixture().Open();
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(
            InventoryDocument(properties, links), new("https://example.test/entry.json"),
            WotReferenceTargetKind.ThingModel, new Dictionary<Uri, FederationDocument>(), InventoryIndex(source)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected one ambiguous authored object.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CombinedInventoryProjectionSelectorsUseOnlyTheirManifestEntryDependency()
    {
        var source = new Fixture().Open();
        var root = JsonNode.Parse(await Bytes(InventoryDocument("""
            {"a":{"tm:ref":"urn:inventory:tm#/properties/value","td:ref":"urn:inventory:td"},"other":{"tm:ref":"urn:inventory:tm"}}
            """, "[]")))!;
        root["@context"]!["p"] = WotJsonLdBaseResolver.ProjectionVocabulary;
        root["@context"]!["p:projects"] = new JsonObject
        {
            ["@id"] = WotJsonLdBaseResolver.ProjectionVocabulary + "projects", ["@container"] = "@list"
        };
        root["@type"] = new JsonArray("tm:ThingModel", "p:projection");
        root["p:projects"] = JsonNode.Parse("""[{"p:sourceName":"base","href":"urn:inventory:tm","type":"application/tm+json"}]""");
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(
            new(Encoding.UTF8.GetBytes(root.ToJsonString())), new("https://example.test/entry.json"),
            WotReferenceTargetKind.ThingModel, new Dictionary<Uri, FederationDocument>(),
            InventoryIndex(source, description: InventorySource("thingdescription")));
        await Assert.That(inventory.Dependencies.Count).IsEqualTo(3);
        await Assert.That(inventory.Dependencies.Select(value => value.Reference.SourcePointer).SequenceEqual(
            ["/properties/a", "/properties/other", "/p:projects/0"])).IsTrue();
        await Assert.That(inventory.Dependencies[0].Reference.Operator).IsEqualTo("td:ref");
        await Assert.That(inventory.Dependencies[0].Target.ResourceKind).IsEqualTo("thingdescription");
        await Assert.That(inventory.Dependencies[2].Reference.Operator).IsEqualTo(WotJsonLdBaseResolver.ProjectionVocabulary + "projects");
        await Assert.That(inventory.ProjectionSelectors.Single().Reference.SourcePointer).IsEqualTo("/properties/a");
        await Assert.That(inventory.ProjectionSelectors.Single().Source.Reference.SourcePointer).IsEqualTo("/p:projects/0");
    }

    [Test]
    [Arguments("urn:inventory:missing", FederationErrorCode.NotFound)]
    [Arguments("urn:inventory:wrong-kind", FederationErrorCode.InvalidPackage)]
    public async Task CombinedInventoryPropagatesMissingOrWrongKindTargets(string href, FederationErrorCode code)
    {
        var source = new Fixture().Open();
        var index = InventoryIndex(source);
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(
            InventoryDocument("""{"a":{"td:ref":""" + JsonSerializer.Serialize(href) + "}}", "[]"),
            new("https://example.test/entry.json"), WotReferenceTargetKind.ThingModel,
            new Dictionary<Uri, FederationDocument>(), index)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected unresolved or incorrectly typed captured metadata.");
        await Assert.That(failure.Code).IsEqualTo(code);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task CombinedInventorySharesAnInclusiveWorkBudget(int delta)
    {
        var source = new Fixture().Open();
        var index = InventoryIndex(source);
        var document = InventoryDocument("""{"a":{"tm:ref":"urn:inventory:tm"}}""", "[]");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var baseline = new FederationReadBudget();
        _ = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, new("https://example.test/entry.json"),
            WotReferenceTargetKind.ThingModel, contexts, index, baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document,
                new("https://example.test/entry.json"), WotReferenceTargetKind.ThingModel, contexts, index, budget))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected combined inventory work exhaustion.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, new("https://example.test/entry.json"),
                WotReferenceTargetKind.ThingModel, contexts, index, budget);
            await Assert.That(inventory.Dependencies.Single().Reference.SourcePointer).IsEqualTo("/properties/a");
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
    }

    [Test]
    public async Task CombinedInventoryAcceptsAnEmptyInventoryAndHonorsCancellationBeforeDiscovery()
    {
        var index = InventoryIndex(new Fixture().Open());
        var document = InventoryDocument("{}", "[]");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, new("https://example.test/entry.json"),
            WotReferenceTargetKind.ThingModel, contexts, index);
        await Assert.That(inventory.Dependencies.Count).IsEqualTo(0);
        await Assert.That(inventory.SchemaDescriptions.Count).IsEqualTo(0);
        await Assert.That(inventory.ProjectionSelectors.Count).IsEqualTo(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var budget = new FederationReadBudget();
        await Assert.That(async () => await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document,
            new("https://example.test/entry.json"), WotReferenceTargetKind.ThingModel, contexts, index, budget, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(budget.Work).IsEqualTo(0L);
    }

    [Test]
    [Arguments(WotReferenceTargetKind.ThingDescription, 2)]
    [Arguments(WotReferenceTargetKind.ThingModel, 1)]
    public async Task CombinedInventoryRetainsTypeLinksOnlyForDescriptionSources(WotReferenceTargetKind kind, int count)
    {
        var document = InventoryDocument("""{"a":{"td:ref":"urn:inventory:td"}}""",
            """[{"rel":"type","href":"urn:inventory:tm"}]""");
        var inventory = await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document,
            new("https://example.test/entry.json"), kind, new Dictionary<Uri, FederationDocument>(),
            InventoryIndex(new Fixture().Open(), description: InventorySource("thingdescription")));
        await Assert.That(inventory.Dependencies.Count).IsEqualTo(count);
        await Assert.That(inventory.Dependencies[0].Reference.Operator).IsEqualTo("td:ref");
        await Assert.That(inventory.Dependencies[0].Target.ResourceKind).IsEqualTo("thingdescription");
        if (count == 2) { await Assert.That(inventory.Dependencies[1].Reference.Operator).IsEqualTo("type"); }
    }

    [Test]
    public async Task CombinedInventoryRejectsCompetingUriScopesWithoutReadingTargetDocuments()
    {
        var source = new Fixture().Open();
        var index = new WotCapturedUriIdentityIndex(
        [
            new(new("urn:inventory:tm"), WotCapturedUriIdentityKind.DocumentView,
                new(source, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId)),
            new(new("urn:inventory:tm"), WotCapturedUriIdentityKind.DocumentView,
                new(source, "/sets/team", Fixture.Collection, Fixture.Resource, "thingmodel", Fixture.WotId, "1.0.0"))
        ]);
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(
            InventoryDocument("""{"a":{"tm:ref":"urn:inventory:tm"}}""", "[]"), new("https://example.test/entry.json"),
            WotReferenceTargetKind.ThingModel, new Dictionary<Uri, FederationDocument>(), index))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected ambiguous captured scopes.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task CombinedInventoryRejectsUncapturedContextsWithoutLiveAcquisition()
    {
        var budget = new FederationReadBudget();
        var failure = await Assert.That(async () => await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(
            new(Encoding.UTF8.GetBytes("""{"@context":"https://never-fetch.invalid/context"}""")),
            new("https://example.test/entry.json"), WotReferenceTargetKind.ThingModel,
            new Dictionary<Uri, FederationDocument>(), InventoryIndex(new Fixture().Open()), budget))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a missing captured context.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(budget.Requests).IsEqualTo(0L);
    }

    private static CapturedRegistryReadSource InventorySource(string kind)
    {
        var fixture = new Fixture();
        fixture.Root["modelsource"]!["groups"]!["sets"]!["resources"]!["things"]!["singular"] = kind;
        fixture.Model = RegistryModel.Compile(RegistryJson.Parse(fixture.Root["modelsource"]!.ToJsonString()));
        return fixture.Open();
    }

    private static FederationDocument InventoryDocument(string properties, string links) => new(Encoding.UTF8.GetBytes("""
        {"@context":{"tm":"https://www.w3.org/2019/wot/tm#","td":"https://www.w3.org/2019/wot/td#",
        "h":"https://www.w3.org/2019/wot/hypermedia#","again":"tm:ref",
        "properties":{"@id":"td:hasPropertyAffordance","@container":"@index"},"links":"td:hasLink",
        "href":{"@id":"h:hasTarget","@type":"@id"},"rel":"h:hasRelationType"},"properties":
        """ + properties + ",\"links\":" + links + "}"));

    private static WotCapturedUriIdentityIndex InventoryIndex(CapturedRegistryReadSource source,
        CapturedRegistryReadSource? schema = null, CapturedRegistryReadSource? description = null)
    {
        WotCapturedUriIdentity Identity(string uri, CapturedRegistryReadSource owner, string kind) =>
            new(new(uri), WotCapturedUriIdentityKind.DocumentView,
                new(owner, "/sets/team", Fixture.Collection, Fixture.Resource, kind, kind == "schema" ? null : Fixture.WotId));
        var identities = new List<WotCapturedUriIdentity>
        {
            Identity("urn:inventory:tm", source, "thingmodel"), Identity("urn:inventory:wrong-kind", source, "thingmodel")
        };
        if (schema is not null) { identities.Add(Identity("urn:inventory:schema", schema, "schema")); }
        if (description is not null) { identities.Add(Identity("urn:inventory:td", description, "thingdescription")); }
        return new(identities);
    }

    private static async Task<byte[]> Bytes(FederationDocument document)
    {
        using var stream = document.OpenRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Test]
    public async Task AutomaticCapturedUriIndexPreservesResourceAndExactVersionScopeOffline()
    {
        const string secondBase = "https://example.test/relocated/second.json";
        var fixture = new Fixture();
        var publication = new PublicationFixture(fixture, manifest => manifest["documents"]![1]!["base"] = secondBase);
        var source = await CapturedRegistryReadSource.FromPublicationAsync(await publication.OpenAsync());
        publication.FailOnRead = true;
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source);
        var resource = index.Resolve(new(Fixture.WotId));
        await Assert.That(ReferenceEquals(resource.Source, source)).IsTrue();
        await Assert.That(resource.ResourceXid).IsEqualTo(Fixture.Resource);
        await Assert.That(resource.VersionId).IsNull();
        await Assert.That(resource.ResourceKind).IsEqualTo("thingmodel");
        var exact = index.Resolve(new(secondBase));
        await Assert.That(exact.ResourceXid).IsEqualTo(Fixture.Resource);
        await Assert.That(exact.VersionId).IsEqualTo("2.0.0");
        var first = index.Resolve(new("https://example.test/relocated/model.json"));
        await Assert.That(first.VersionId).IsEqualTo("1.0.0");
        var containing = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        var selected = await WotRegistryResolver.ResolveCapturedReferenceAsync(containing, secondBase,
            new("https://example.test/relocated/model.json"), new("https://example.test/relocated/model.json"),
            index, new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "2.0.0"));
        await Assert.That(selected.ContainingDocument.Version.VersionId).IsEqualTo("2.0.0");
        await Assert.That(selected.ContainingDocument.Version.ContentDigest).IsEqualTo("sha256:" + Digest(Fixture.Second));
        await Assert.That(publication.ChannelReads).IsEqualTo(1);
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(4);
    }

    [Test]
    public async Task AutomaticCapturedUriIndexDoesNotChooseBetweenVersionsSharingOneBase()
    {
        var source = new Fixture().Open();
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source);
        var failure = await Assert.That(() => index.Resolve(new("https://example.test/original/model.json")))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected ambiguous exact-Version bases.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Ambiguous);
        await Assert.That(index.Resolve(new(Fixture.WotId)).VersionId).IsNull();
    }

    [Test]
    public async Task AutomaticCapturedUriIndexKeepsDistinctAliasResourceIdentitiesAmbiguous()
    {
        var fixture = new Fixture();
        fixture.AddAlias();
        var source = fixture.Open();
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source,
            [new(source, new("https://recorded.test/alias"), WotCapturedUriIdentityKind.DocumentView, Fixture.Collection + "/alias")]);
        var alias = index.Resolve(new("https://recorded.test/alias"));
        await Assert.That(alias.ResourceXid).IsEqualTo(Fixture.Collection + "/alias");
        await Assert.That(alias.VersionId).IsNull();
        await Assert.That(alias.WotId).IsEqualTo(Fixture.WotId);
        var failure = await Assert.That(() => index.Resolve(new(Fixture.WotId))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected distinct Resource identities rather than alias collapse.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task AutomaticCapturedUriIndexUsesModelKindForNonWotDocumentAssociations()
    {
        var fixture = new Fixture();
        fixture.Root["modelsource"]!["groups"]!["sets"]!["resources"]!["things"]!["singular"] = "schema";
        fixture.Model = RegistryModel.Compile(RegistryJson.Parse(fixture.Root["modelsource"]!.ToJsonString()));
        foreach (var version in fixture.Root["sets"]!["team"]!["things"]!["a"]!["versions"]!.AsObject())
        {
            version.Value!.AsObject().Remove("wotid");
        }
        fixture.Documents.Remove(Fixture.Resource + "/versions/2.0.0");
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(fixture.Open());
        var selected = index.Resolve(new("https://example.test/original/model.json"));
        await Assert.That(selected.ResourceKind).IsEqualTo("schema");
        await Assert.That(selected.WotId).IsNull();
        await Assert.That(selected.VersionId).IsEqualTo("1.0.0");
        var failure = await Assert.That(() => index.Resolve(new(Fixture.WotId))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected no invented WoT identity for a Schema Resource.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task AutomaticCapturedUriIndexDoesNotInventMissingBasesOrDocumentViewUris()
    {
        var fixture = new Fixture();
        fixture.Documents.Clear();
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new(Fixture.First, documentOrigin: "https://origin.test/model.json");
        var source = fixture.Open();
        const string recordedView = "https://recorded.test/view";
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source,
            [new(source, new(recordedView), WotCapturedUriIdentityKind.DocumentView, Fixture.Resource + "/versions/1.0.0")]);
        await Assert.That(index.Resolve(new(recordedView)).VersionId).IsEqualTo("1.0.0");
        await Assert.That(index.Resolve(new(Fixture.WotId)).VersionId).IsNull();
        foreach (var uri in new[] { source.Context.Source, "https://origin.test/model.json", "https://example.test/original/model.json" })
        {
            var failure = await Assert.That(() => index.Resolve(new(uri))).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected no inferred URI association.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.NotFound);
        }
    }

    [Test]
    [Arguments("wotid", "relative")]
    [Arguments("wotid", "https://example.test/model#fragment")]
    [Arguments("base", "relative")]
    [Arguments("base", "https://example.test/model#fragment")]
    public async Task AutomaticCapturedUriIndexRejectsInvalidCapturedUriIdentities(string field, string uri)
    {
        var fixture = new Fixture();
        if (field == "wotid") { fixture.Root["sets"]!["team"]!["things"]!["a"]!["versions"]!["1.0.0"]!["wotid"] = uri; }
        else { fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new(Fixture.First, documentBase: uri); }
        var source = fixture.Open();
        var failure = await Assert.That(async () => await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an unusable captured URI identity.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("work", -1)]
    [Arguments("work", 0)]
    [Arguments("work", 1)]
    [Arguments("bytes", -1)]
    [Arguments("bytes", 0)]
    [Arguments("bytes", 1)]
    [Arguments("objects", -1)]
    [Arguments("objects", 0)]
    [Arguments("objects", 1)]
    [Arguments("requests", -1)]
    [Arguments("requests", 0)]
    [Arguments("requests", 1)]
    public async Task AutomaticCapturedUriIndexHasInclusiveCumulativeBoundaries(string limit, int delta)
    {
        var source = new Fixture().Open();
        var baseline = new FederationReadBudget();
        _ = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source, budget: baseline);
        var budget = new FederationReadBudget(new(
            maxWork: limit == "work" ? baseline.Work + delta : 1_000_000,
            maxTotalBytes: limit == "bytes" ? baseline.BytesRead + delta : 64 * 1024 * 1024,
            maxObjects: limit == "objects" ? baseline.ObjectsRead + delta : 4096,
            maxRequests: limit == "requests" ? baseline.Requests + delta : 4096));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source, budget: budget))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected URI capture work exhaustion.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source, budget: budget);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
            await Assert.That(budget.BytesRead).IsEqualTo(baseline.BytesRead);
            await Assert.That(budget.ObjectsRead).IsEqualTo(baseline.ObjectsRead);
            await Assert.That(budget.Requests).IsEqualTo(baseline.Requests);
            await Assert.That(index.Resolve(new(Fixture.WotId)).ResourceXid).IsEqualTo(Fixture.Resource);
        }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task AutomaticCapturedUriIndexBoundsEachRecordedUriBeforeParsing(int delta)
    {
        var fixture = new Fixture();
        var documentBase = "https://example.test/" + new string('a', 2048);
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new(Fixture.First, documentBase: documentBase);
        var source = fixture.Open();
        var budget = new FederationReadBudget(new(maxObjectBytes: Encoding.UTF8.GetByteCount(documentBase) + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source, budget: budget))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a bounded captured URI.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            await Assert.That(budget.Requests).IsEqualTo(0L);
        }
        else
        {
            var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source, budget: budget);
            await Assert.That(index.Resolve(new(documentBase)).VersionId).IsEqualTo("1.0.0");
        }
    }

    [Test]
    public async Task AutomaticCapturedUriIndexAcceptsAnEmptyCapturedGraph()
    {
        var fixture = new Fixture();
        fixture.Root["sets"] = new JsonObject();
        fixture.Root["setscount"] = 0;
        fixture.Documents.Clear();
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(fixture.Open());
        var failure = await Assert.That(() => index.Resolve(new(Fixture.WotId))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected an empty captured URI index.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AutomaticCapturedUriIndexHonorsCancellationDuringAssociationInventory(bool duringInventory)
    {
        var source = new Fixture().Open();
        using var cancellation = new CancellationTokenSource();
        var budget = new FederationReadBudget();
        IEnumerable<WotCapturedUriAssociation> Associations()
        {
            cancellation.Cancel();
            yield return new(source, new("https://recorded.test/view"), WotCapturedUriIdentityKind.DocumentView, Fixture.Resource);
        }
        if (!duringInventory) { cancellation.Cancel(); }
        await Assert.That(async () => await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source,
            duringInventory ? Associations() : null, budget, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(budget.Requests).IsEqualTo(0L);
        if (!duringInventory) { await Assert.That(budget.Work).IsEqualTo(0L); }
    }

    [Test]
    public async Task PublicationCaptureCompilesRetainedModelSourceAndLocalIncludesWithoutCallerModel()
    {
        var fixture = new Fixture();
        fixture.Root["modelsource"]!["attributes"] = JsonNode.Parse("""
            {"first":{"type":"string"},"second":{"$include":"#/attributes/first"}}
            """);
        var publication = new PublicationFixture(fixture);
        var source = await CapturedRegistryReadSource.FromPublicationAsync(await publication.OpenAsync());
        await Assert.That(source.Model.Attributes["second"].Type).IsEqualTo(RegistryValueType.String);
        await Assert.That(source.Model.Source.RootElement.GetProperty("attributes").GetProperty("second")
            .GetProperty("$include").GetString()).IsEqualTo("#/attributes/first");
        await Assert.That(source.Model.EffectiveModel.RootElement.GetProperty("attributes").GetProperty("second")
            .TryGetProperty("$include", out _)).IsFalse();
        publication.FailOnRead = true;
        var document = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        await Assert.That(document.Version.ContentDigest).IsEqualTo("sha256:" + Digest(Fixture.First));
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(4);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task PublicationCapturePreservesActualResourceTypeSharingForCrossGroupAliases(bool shared)
    {
        var fixture = new Fixture();
        fixture.AddAlias();
        var groups = fixture.Root["modelsource"]!["groups"]!;
        groups["copies"] = shared
            ? JsonNode.Parse("""{"singular":"copy","ximportresources":["/sets/things"]}""")
            : new JsonObject
            {
                ["singular"] = "copy", ["resources"] = new JsonObject
                {
                    ["things"] = groups["sets"]!["resources"]!["things"]!.DeepClone()
                }
            };
        var alias = fixture.Root["sets"]!["team"]!["things"]!["alias"]!.DeepClone();
        const string xid = "/copies/archive/things/alias";
        const string pointer = "#/copies/archive/things/alias";
        alias["xid"] = xid;
        alias["self"] = pointer;
        alias["metaurl"] = pointer + "/meta";
        alias["meta"]!["xid"] = xid + "/meta";
        alias["meta"]!["self"] = pointer + "/meta";
        fixture.Root["copies"] = new JsonObject
        {
            ["archive"] = new JsonObject
            {
                ["copyid"] = "archive", ["xid"] = "/copies/archive", ["self"] = "#/copies/archive",
                ["things"] = new JsonObject { ["alias"] = alias }
            }
        };
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        if (shared)
        {
            var source = await CapturedRegistryReadSource.FromPublicationAsync(opened);
            await Assert.That(ReferenceEquals(source.Model.Groups["copies"].Resources["things"],
                source.Model.Groups["sets"].Resources["things"])).IsTrue();
            await Assert.That(source.Model.Source.RootElement.GetProperty("groups").GetProperty("copies")
                .GetProperty("ximportresources")[0].GetString()).IsEqualTo("/sets/things");
            var document = await source.ReadAsync(new(FederationOperation.Document, xid));
            await Assert.That(document.SelectedXid).IsEqualTo(Fixture.Resource + "/versions/1.0.0");
            using var stream = document.Document!.OpenRead();
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            await Assert.That(bytes.ToArray().SequenceEqual(Fixture.First)).IsTrue();
        }
        else
        {
            var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a distinct compiled Resource type.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
        }
    }

    [Test]
    [Arguments("missing-source")]
    [Arguments("external-include")]
    [Arguments("relative-include")]
    [Arguments("cycle")]
    [Arguments("model-mismatch")]
    [Arguments("unknown-core")]
    [Arguments("invalid-source")]
    public async Task AutomaticCapturedModelConstructionRejectsUnretainedOrContradictoryMaterial(string change)
    {
        var fixture = new Fixture();
        switch (change)
        {
            case "missing-source":
                fixture.Root.Remove("modelsource");
                fixture.Root["model"] = JsonNode.Parse(fixture.Model.EffectiveModel.RootElement.GetRawText());
                break;
            case "external-include": fixture.Root["modelsource"]!["$include"] = "https://never-fetch.invalid/model.json"; break;
            case "relative-include": fixture.Root["modelsource"]!["$include"] = "model.json"; break;
            case "cycle": fixture.Root["modelsource"]!["$include"] = "#"; break;
            case "model-mismatch": fixture.Root["model"] = new JsonObject(); break;
            case "unknown-core": fixture.Root["specversion"] = "2.0"; break;
            case "invalid-source": fixture.Root["modelsource"] = new JsonArray(); break;
        }
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected unusable captured model material.");
        await Assert.That(failure.Code).IsEqualTo(change == "unknown-core"
            ? FederationErrorCode.UnsupportedVersion : FederationErrorCode.InvalidPackage);
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task AutomaticCapturedModelConstructionUsesAnInclusiveSharedWorkBudget(int delta)
    {
        var fixture = new Fixture();
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        var baseline = new FederationReadBudget();
        _ = await CapturedRegistryReadSource.FromPublicationAsync(opened, budget: baseline);
        publication.OpenedVersions.Clear();
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, budget: budget))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected cumulative work exhaustion.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            await Assert.That(publication.OpenedVersions.Count).IsEqualTo(0);
        }
        else
        {
            var source = await CapturedRegistryReadSource.FromPublicationAsync(opened, budget: budget);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
            await Assert.That(source.Model.Groups["sets"].Resources["things"].Singular).IsEqualTo("thingmodel");
            await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task PublicationCaptureRetainsVerifiedAssociationsWithoutReopeningTheChannel()
    {
        var fixture = new Fixture();
        fixture.Root["sets"]!["team"]!["things"]!["a"]!["versions"]!["1.0.0"]!["contenttype"] = "application/tm+json";
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        var budget = new FederationReadBudget();
        var source = await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model, budget);
        await Assert.That(publication.ChannelReads).IsEqualTo(1);
        await Assert.That(publication.OpenedVersions.SequenceEqual([
            publication.ManifestDescriptor.Reference.Version, PublicationFixture.RegistryVersion,
            PublicationFixture.FirstVersion, PublicationFixture.SecondVersion])).IsTrue();
        await Assert.That(source.Context.Binding).IsEqualTo("publication");
        await Assert.That(source.Context.Source).IsEqualTo(opened.Manifest.Scope);
        await Assert.That(source.Context.Revision).IsEqualTo(opened.Manifest.SnapshotId);
        await Assert.That(source.Context.RootSha256).IsEqualTo(Digest(fixture.Bytes()));
        await Assert.That(source.GetExactRegistryBytes().SequenceEqual(opened.GetExactRegistryBytes())).IsTrue();
        await Assert.That(budget.BytesRead).IsEqualTo((long)fixture.Bytes().Length + Fixture.First.Length + Fixture.Second.Length);
        await Assert.That(budget.ObjectsRead).IsEqualTo(3L);
        await Assert.That(budget.Requests).IsEqualTo(2L);
        publication.FailOnRead = true;
        var document = await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource));
        await Assert.That(document.Document!.ContentType).IsEqualTo("application/tm+json");
        await Assert.That(document.Document.Base).IsEqualTo("https://example.test/relocated/model.json");
        await Assert.That(document.Document.Origin).IsNull();
        await Assert.That(opened.Manifest.Documents[0].OriginRegistry).IsEqualTo("urn:test:original-registry");
        await Assert.That(opened.Manifest.Documents[0].OriginVersion).IsEqualTo("/originals/site/models/model/versions/old");
        using var stream = document.Document.OpenRead();
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        await Assert.That(bytes.ToArray().SequenceEqual(Fixture.First)).IsTrue();
        var selected = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        await Assert.That(selected.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(selected.Document.Base).IsEqualTo("https://example.test/relocated/model.json");
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(4);
        await Assert.That(publication.DisposedBindings).IsEqualTo(5);
    }

    [Test]
    [Arguments("missing-version")]
    [Arguments("resource")]
    [Arguments("size")]
    [Arguments("total-bytes")]
    [Arguments("objects")]
    [Arguments("model")]
    [Arguments("registry-size")]
    public async Task PublicationCaptureChecksGraphAndCumulativeLimitsBeforeAcquiringDocuments(string change)
    {
        var fixture = new Fixture();
        var publication = new PublicationFixture(fixture, manifest =>
        {
            if (change == "missing-version") { manifest["documents"]![0]!["viewVersion"] = Fixture.Resource + "/versions/9.0.0"; }
            if (change == "resource") { manifest["documents"]![0]!["viewVersion"] = "/missing/team/things/a/versions/1.0.0"; }
            if (change == "size") { manifest["documents"]![0]!["content"]!["size"] = 1_000_000; }
        });
        var opened = await publication.OpenAsync();
        var limits = new FederationReadLimits(
            maxTotalBytes: change == "total-bytes" ? fixture.Bytes().Length + Fixture.First.Length + Fixture.Second.Length - 1 : 64 * 1024 * 1024,
            maxObjects: change == "objects" ? 2 : 4096,
            maxObjectBytes: change == "registry-size" ? fixture.Bytes().Length - 1 : 100_000);
        var model = change == "model" ? RegistryModel.Compile(RegistryJson.Parse("""{"groups":{}}""")) : fixture.Model;
        var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, model, new(limits)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected pre-acquisition rejection.");
        await Assert.That(failure.Code).IsEqualTo(change is "total-bytes" or "objects" or "registry-size" or "size"
            ? FederationErrorCode.LimitExceeded : FederationErrorCode.InvalidPackage);
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
        await Assert.That(publication.ChannelReads).IsEqualTo(1);
    }

    [Test]
    [Arguments("size")]
    [Arguments("digest")]
    [Arguments("missing")]
    [Arguments("cancel")]
    public async Task PublicationCapturePropagatesByteFailuresAndNeverSubstitutesDocuments(string change)
    {
        var fixture = new Fixture();
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        if (change == "size") { publication.Objects[PublicationFixture.FirstVersion] = [1, 2, 3]; }
        if (change == "digest")
        {
            var changed = (byte[])Fixture.First.Clone();
            changed[0] = (byte)'X';
            publication.Objects[PublicationFixture.FirstVersion] = changed;
        }
        if (change == "missing") { publication.Objects.Remove(PublicationFixture.FirstVersion); }
        if (change == "cancel") { publication.OnOpen = _ => cancellation.Cancel(); }
        if (change == "cancel")
        {
            await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model,
                cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        }
        else
        {
            var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model))
                .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected exact descriptor failure.");
            await Assert.That(failure.Code).IsEqualTo(change switch
            {
                "size" => "size_mismatch", "digest" => "digest_mismatch", _ => "object_not_found"
            });
        }
        await Assert.That(publication.ChannelReads).IsEqualTo(1);
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(3);
        await Assert.That(publication.OpenedVersions[^1]).IsEqualTo(PublicationFixture.FirstVersion);
        await Assert.That(publication.DisposedBindings).IsEqualTo(4);
    }

    [Test]
    [Arguments("bytes", -1)]
    [Arguments("bytes", 0)]
    [Arguments("bytes", 1)]
    [Arguments("objects", -1)]
    [Arguments("objects", 0)]
    [Arguments("objects", 1)]
    [Arguments("work", -1)]
    [Arguments("work", 0)]
    [Arguments("work", 1)]
    [Arguments("requests", -1)]
    [Arguments("requests", 0)]
    [Arguments("requests", 1)]
    public async Task PublicationCaptureReservationsHaveInclusiveCumulativeBoundaries(string limit, int delta)
    {
        var fixture = new Fixture();
        var baselinePublication = new PublicationFixture(fixture);
        var baseline = new FederationReadBudget();
        _ = await CapturedRegistryReadSource.FromPublicationAsync(await baselinePublication.OpenAsync(), fixture.Model, baseline);
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        var budget = new FederationReadBudget(new(
            maxTotalBytes: limit == "bytes" ? baseline.BytesRead + delta : 64 * 1024 * 1024,
            maxObjects: limit == "objects" ? baseline.ObjectsRead + delta : 4096,
            maxRequests: limit == "requests" ? baseline.Requests + delta : 4096,
            maxWork: limit == "work" ? baseline.Work + delta : 1_000_000));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model, budget))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected reservation exhaustion.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
        }
        else
        {
            var source = await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model, budget);
            await Assert.That(source.GetExactRegistryBytes().SequenceEqual(fixture.Bytes())).IsTrue();
            await Assert.That(budget.BytesRead).IsEqualTo(baseline.BytesRead);
            await Assert.That(budget.ObjectsRead).IsEqualTo(baseline.ObjectsRead);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
            await Assert.That(budget.Requests).IsEqualTo(baseline.Requests);
            await Assert.That(publication.OpenedVersions.Count).IsEqualTo(4);
        }
    }

    [Test]
    public async Task PublicationCaptureHonorsCancellationBeforeRetainingOrAcquiringBytes()
    {
        var fixture = new Fixture();
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var budget = new FederationReadBudget();
        await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model,
            budget, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(budget.BytesRead).IsEqualTo(0L);
        await Assert.That(budget.ObjectsRead).IsEqualTo(0L);
        await Assert.That(budget.Work).IsEqualTo(0L);
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CapturedReadsPreserveOriginalBytesMetadataAndResponseLocalNavigation()
    {
        var fixture = new Fixture();
        var bytes = fixture.Bytes();
        var original = (byte[])bytes.Clone();
        var budget = new FederationReadBudget();
        var source = fixture.Open(bytes, budget);
        Array.Fill(bytes, (byte)0);
        fixture.Root["registryid"] = "changed";
        fixture.Documents.Clear();
        var returned = source.GetExactRegistryBytes();
        await Assert.That(returned.SequenceEqual(original)).IsTrue();
        Array.Fill(returned, (byte)0);
        await Assert.That(source.GetExactRegistryBytes().SequenceEqual(original)).IsTrue();
        await Assert.That(source.Context.RootSha256).IsEqualTo(Digest(original));
        await Assert.That(source.Context.Revision).IsEqualTo("snapshot-1");
        await Assert.That(source.Context.RootPath).IsEqualTo("captured");
        await Assert.That(source.Context.RequestedRevision).IsEqualTo("stable");
        await Assert.That(ReferenceEquals(source.Model, fixture.Model)).IsTrue();

        var root = await source.ReadAsync(new(FederationOperation.Entity, "/"));
        await Assert.That(root.Metadata.GetProperty("entity").GetProperty("registryid").GetString()).IsEqualTo("captured");
        await Assert.That(root.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        var resource = await source.ReadAsync(new(FederationOperation.Entity, Fixture.Resource));
        var entity = resource.Metadata.GetProperty("entity");
        await Assert.That(entity.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/entity/versions/1.0.0");
        await Assert.That(entity.GetProperty("versions").GetProperty("2.0.0").GetProperty("self").GetString())
            .IsEqualTo("#/entity/versions/2.0.0");
        await Assert.That(entity.GetProperty("versions").GetProperty("1.0.0").GetProperty("epoch").GetUInt64())
            .IsEqualTo(71UL);
        await Assert.That(entity.GetProperty("versions").GetProperty("1.0.0").GetProperty("createdat").GetString())
            .IsEqualTo("2025-01-02T03:04:05Z");
        var meta = await source.ReadAsync(new(FederationOperation.Entity, Fixture.Resource + "/meta"));
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/related/defaultversion");
        await Assert.That(meta.Metadata.GetProperty("related").GetProperty("defaultversion").GetProperty("versionid").GetString())
            .IsEqualTo("1.0.0");
        var collection = await source.ReadAsync(new(FederationOperation.Collection, Fixture.Collection));
        await Assert.That(collection.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(collection.Metadata.GetProperty("entities").GetProperty("a").GetProperty("self").GetString())
            .IsEqualTo("#/entities/a");
        var model = await source.ReadAsync(new(FederationOperation.Model, "/"));
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(model.Metadata.GetProperty("modelsource").GetRawText()),
            JsonNode.Parse(fixture.Model.Source.RootElement.GetRawText()))).IsTrue();
        var capabilities = await source.ReadAsync(new(FederationOperation.Capabilities, "/"));
        await Assert.That(capabilities.Metadata.GetProperty("available").GetProperty("entities").GetProperty("mutable").GetBoolean()).IsFalse();
        await Assert.That(budget.Requests).IsEqualTo(0L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedDocumentsUseExactAssociationsAndTheRecordedDefaultWithoutRewriting(bool exact)
    {
        var fixture = new Fixture();
        var source = fixture.Open();
        var result = await source.ReadAsync(new(FederationOperation.Document,
            Fixture.Resource + (exact ? "/versions/2.0.0" : "")));
        await Assert.That(result.SelectedXid).IsEqualTo(Fixture.Resource + (exact ? "/versions/2.0.0" : "/versions/1.0.0"));
        var document = result.Document ?? throw new InvalidOperationException("Expected associated document bytes.");
        await Assert.That(document.Base).IsEqualTo("https://example.test/original/model.json");
        await Assert.That(document.Origin).IsEqualTo("urn:test:original-document");
        await Assert.That(document.ContentType).IsEqualTo("application/tm+json");
        using var stream = document.OpenRead();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        await Assert.That(copy.ToArray().SequenceEqual(exact ? Fixture.Second : Fixture.First)).IsTrue();
        await Assert.That(result.Context).IsEqualTo(source.Context);
    }

    [Test]
    public async Task CapturedGraphFeedsModelDerivedWotIdentityAndVersionResolutionWithoutLiveReads()
    {
        var fixture = new Fixture();
        var budget = new FederationReadBudget();
        var source = fixture.Open(budget: budget);
        var index = await WotCapturedUriIdentityIndex.CaptureAsync(
            [new(source, new Uri("https://example.test/definition"), WotCapturedUriIdentityKind.DocumentView,
                Fixture.Resource + "/versions/1.0.0")], budget);
        var reference = new WotAuthoredReferenceOccurrence("/data", "tm:ref", "tm:ref", "definition",
            WotReferenceUriResolver.Resolve("definition", new Uri("https://example.test/plan"), new Uri("https://example.test/plan")));
        var captured = index.ClassifyOrdinaryReference(reference, budget) ??
            throw new InvalidOperationException("Expected a captured Thing Model.");
        await Assert.That(captured.ResourceKind).IsEqualTo("thingmodel");
        await Assert.That(captured.VersionId).IsEqualTo("1.0.0");
        await Assert.That(captured.WotId).IsEqualTo(Fixture.WotId);
        var acquired = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"), budget);
        await Assert.That(acquired.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(acquired.Version.ContentDigest).IsEqualTo("sha256:" + Digest(Fixture.First));
        await Assert.That(acquired.Document.Base).IsEqualTo("https://example.test/original/model.json");
        await Assert.That(source.GetExactRegistryBytes().SequenceEqual(fixture.Bytes())).IsTrue();
    }

    [Test]
    [Arguments("v1", true)]
    [Arguments("V1", true)]
    [Arguments("absent", false)]
    public async Task CapturedResourceLabelSelectionUsesItsCapturedDefaultVersion(string label, bool found)
    {
        var source = new Fixture().Open();
        var request = new FederationReadRequest(FederationOperation.Collection, Fixture.Collection, new("release", label));
        if (found)
        {
            var result = await source.ReadAsync(request);
            await Assert.That(result.SelectedXid).IsEqualTo(Fixture.Resource);
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        }
        else
        {
            var failure = await Assert.That(async () => await source.ReadAsync(request)).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected a missing captured label.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.NotFound);
        }
    }

    [Test]
    public async Task CapturedAliasesResolveOnlyTheirCapturedSameTypeTargetAndPreserveAliasMetadata()
    {
        var fixture = new Fixture();
        fixture.AddAlias();
        var source = fixture.Open();
        var metadata = await source.ReadAsync(new(FederationOperation.Entity, Fixture.Collection + "/alias"));
        await Assert.That(metadata.Metadata.GetProperty("entity").GetProperty("meta").GetProperty("xref").GetString())
            .IsEqualTo(Fixture.Resource);
        var document = await source.ReadAsync(new(FederationOperation.Document, Fixture.Collection + "/alias"));
        await Assert.That(document.SelectedXid).IsEqualTo(Fixture.Resource + "/versions/1.0.0");
        var failure = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Entity,
            Fixture.Collection + "/alias/versions/1.0.0"))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected undefined alias-Version document view.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        var ambiguous = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Collection,
            Fixture.Collection, new("release", "v1")))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected two visible Resources with the selected label.");
        await Assert.That(ambiguous.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    [Arguments("missing-map")]
    [Arguments("wrong-count")]
    [Arguments("external-navigation")]
    [Arguments("wrong-self")]
    [Arguments("wrong-xid")]
    [Arguments("wrong-id")]
    [Arguments("wrong-version")]
    [Arguments("missing-default")]
    [Arguments("case-collision")]
    [Arguments("duplicate-json")]
    [Arguments("model-mismatch")]
    [Arguments("source-mismatch")]
    [Arguments("missing-model")]
    [Arguments("mutable-capabilities")]
    [Arguments("bad-count-type")]
    [Arguments("bad-alias")]
    [Arguments("dangling-alias")]
    [Arguments("model-not-object")]
    [Arguments("shortself")]
    [Arguments("invalid-pointer-encoding")]
    [Arguments("invalid-pointer-utf8")]
    public async Task CapturedGraphsRejectIncompleteContradictoryOrUnretrievableState(string change)
    {
        var fixture = new Fixture();
        var resource = fixture.Root["sets"]!["team"]!["things"]!["a"]!.AsObject();
        switch (change)
        {
            case "missing-map": fixture.Root.Remove("sets"); break;
            case "wrong-count": fixture.Root["setscount"] = 2; break;
            case "external-navigation": fixture.Root["setsurl"] = "https://untrusted.invalid/sets"; break;
            case "wrong-self": resource["self"] = "#/sets/team/things/another"; break;
            case "wrong-xid": resource["xid"] = "/sets/team/things/another"; break;
            case "wrong-id": resource["thingmodelid"] = "another"; break;
            case "wrong-version": resource["versions"]!["1.0.0"]!["versionid"] = "2.0.0"; break;
            case "missing-default": resource["meta"]!["defaultversionid"] = "3.0.0"; break;
            case "case-collision": fixture.Root["sets"]!.AsObject()["TEAM"] = fixture.Root["sets"]!["team"]!.DeepClone(); fixture.Root["setscount"] = 2; break;
            case "model-mismatch": fixture.Root["model"] = new JsonObject(); break;
            case "source-mismatch": fixture.Root["modelsource"] = new JsonObject(); break;
            case "missing-model": fixture.Root.Remove("modelsource"); break;
            case "mutable-capabilities": fixture.Root["capabilities"]!["available"]!["entities"]!["mutable"] = true; break;
            case "bad-count-type": fixture.Root["setscount"] = "one"; break;
            case "bad-alias": fixture.AddAlias(); fixture.Root["sets"]!["team"]!["things"]!["alias"]!["meta"]!["xref"] = Fixture.Resource + "/versions/1.0.0"; break;
            case "dangling-alias": fixture.AddAlias(); fixture.Root["sets"]!["team"]!["things"]!["alias"]!["meta"]!["xref"] = Fixture.Collection + "/missing"; break;
            case "model-not-object": fixture.Root["model"] = 1; break;
            case "shortself": resource["shortself"] = "https://untrusted.invalid/short"; break;
            case "invalid-pointer-encoding": resource["self"] = "#/sets/team/things/%QQ"; break;
            case "invalid-pointer-utf8": resource["self"] = "#/sets/team/things/%FF"; break;
        }
        var bytes = fixture.Bytes();
        if (change == "duplicate-json")
        {
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"registryid\":\"captured\"",
                "\"registryid\":\"captured\",\"registryid\":\"other\"", StringComparison.Ordinal));
        }
        var failure = await Assert.That(() => fixture.Open(bytes)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected invalid captured graph.");
        await Assert.That(failure.Code is FederationErrorCode.InvalidPackage or FederationErrorCode.NotFound).IsTrue();
    }

    [Test]
    [Arguments("a~b")]
    [Arguments("a:b")]
    [Arguments("a@b")]
    public async Task CapturedPointerTokensUseRfc6901AndUriFragmentEncoding(string id)
    {
        var fixture = new Fixture();
        fixture.RenameResource(id);
        var source = fixture.Open();
        var xid = Fixture.Collection + "/" + Uri.EscapeDataString(id);
        var result = await source.ReadAsync(new(FederationOperation.Collection, Fixture.Collection));
        var entity = result.Metadata.GetProperty("entities").GetProperty(id);
        var token = Uri.EscapeDataString(id.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entities/" + token);
        await Assert.That(entity.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/entities/" + token + "/versions/1.0.0");
        var version = await source.ReadAsync(new(FederationOperation.Entity, xid + "/versions/1.0.0"));
        await Assert.That(version.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        var document = await source.ReadAsync(new(FederationOperation.Document, xid));
        await Assert.That(document.SelectedXid).IsEqualTo(xid + "/versions/1.0.0");
    }

    [Test]
    [Arguments("a/b")]
    [Arguments("a%b")]
    [Arguments("a%2Fb")]
    [Arguments("a b")]
    [Arguments("\u03b1")]
    public async Task CapturedCollectionIdsUseCoreGrammarRatherThanArbitraryJsonPointerKeys(string id)
    {
        var fixture = new Fixture();
        fixture.RenameResource(id);
        var failure = await Assert.That(() => fixture.Open()).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected an invalid Core collection ID.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CapturedGraphAcceptsOmittedOptionalCollectionHintsAndPresentEmptyDocumentBytes()
    {
        var fixture = new Fixture();
        foreach (var item in new[]
        {
            (fixture.Root, "sets"), (fixture.Root["sets"]!["team"]!.AsObject(), "things"),
            (fixture.Root["sets"]!["team"]!["things"]!["a"]!.AsObject(), "versions")
        })
        {
            item.Item1.Remove(item.Item2 + "count");
            item.Item1.Remove(item.Item2 + "url");
        }
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new([], "application/octet-stream");
        var source = fixture.Open();
        var document = await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource));
        await Assert.That(document.Document!.Length).IsEqualTo(0L);
        var collection = await source.ReadAsync(new(FederationOperation.Collection, Fixture.Collection));
        await Assert.That(collection.Metadata.GetProperty("entities").GetProperty("a").TryGetProperty("versionscount", out _)).IsFalse();
    }

    [Test]
    public async Task CapturedMapKeysSupplyOmittedEntityIdAttributes()
    {
        var fixture = new Fixture();
        var group = fixture.Root["sets"]!["team"]!.AsObject();
        group.Remove("setid");
        var resource = group["things"]!["a"]!.AsObject();
        resource.Remove("thingmodelid");
        resource["meta"]!.AsObject().Remove("thingmodelid");
        foreach (var version in resource["versions"]!.AsObject())
        {
            version.Value!.AsObject().Remove("versionid");
            version.Value.AsObject().Remove("thingmodelid");
        }
        var source = fixture.Open();
        var result = await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource));
        await Assert.That(result.SelectedXid).IsEqualTo(Fixture.Resource + "/versions/1.0.0");
        var index = await WotCapturedUriIdentityIndex.CaptureRegistryAsync(source);
        await Assert.That(index.Resolve(new(Fixture.WotId)).ResourceXid).IsEqualTo(Fixture.Resource);
        var resolved = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        await Assert.That(resolved.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(resolved.Version.ContentDigest).IsEqualTo("sha256:" + Digest(Fixture.First));
    }

    [Test]
    public async Task CapturedEmptyGraphIsCompleteAndOpaqueDomainUrlsAreUnchanged()
    {
        var fixture = new Fixture();
        fixture.Root["sets"] = new JsonObject();
        fixture.Root["setscount"] = 0;
        fixture.Root["advisory"] = JsonNode.Parse("""{"self":"https://untrusted.invalid/no-fetch","tm:ref":"other"}""");
        fixture.Documents.Clear();
        var source = fixture.Open();
        var result = await source.ReadAsync(new(FederationOperation.Collection, "/sets"));
        await Assert.That(result.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(result.Metadata.GetProperty("entities").EnumerateObject().Any()).IsFalse();
        var root = await source.ReadAsync(new(FederationOperation.Entity, "/"));
        await Assert.That(root.Metadata.GetProperty("entity").GetProperty("advisory").GetProperty("self").GetString())
            .IsEqualTo("https://untrusted.invalid/no-fetch");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task CapturedResultByteLimitIsIndependentOfTheInputObjectLimit(int delta)
    {
        var fixture = new Fixture();
        var result = await fixture.Open().ReadAsync(new(FederationOperation.Entity, "/"));
        var length = Encoding.UTF8.GetByteCount(result.Metadata.GetRawText());
        var source = fixture.Open(budget: new(new(maxObjectBytes: fixture.Bytes().Length, maxResultBytes: length + delta)));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Entity, "/")))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a bounded metadata result.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var actual = await source.ReadAsync(new(FederationOperation.Entity, "/"));
            await Assert.That(actual.Metadata.GetProperty("entity").GetProperty("registryid").GetString()).IsEqualTo("captured");
        }
    }

    [Test]
    [Arguments("resource")]
    [Arguments("missing-version")]
    [Arguments("duplicate-escaped-version")]
    [Arguments("metadata-only")]
    public async Task CapturedDocumentAssociationsMustNameUniquePresentDocumentBearingExactVersions(string change)
    {
        var fixture = new Fixture();
        var document = fixture.Documents[Fixture.Resource + "/versions/1.0.0"];
        switch (change)
        {
            case "resource": fixture.Documents[Fixture.Resource] = document; break;
            case "missing-version": fixture.Documents[Fixture.Resource + "/versions/9.0.0"] = document; break;
            case "duplicate-escaped-version": fixture.Documents[Fixture.Resource + "/versions/%31.0.0"] = document; break;
            case "metadata-only":
                var model = JsonNode.Parse(fixture.Model.Source.RootElement.GetRawText())!;
                model["groups"]!["sets"]!["resources"]!["things"]!["hasdocument"] = false;
                fixture.Model = RegistryModel.Compile(RegistryJson.Parse(model.ToJsonString()));
                fixture.Root["modelsource"] = model;
                break;
        }
        var failure = await Assert.That(() => fixture.Open()).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected invalid document association.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task MissingCapturedBytesNeverUseAnotherVersionOrAnUnrecordedLocation()
    {
        var fixture = new Fixture();
        fixture.Documents.Remove(Fixture.Resource + "/versions/1.0.0");
        var source = fixture.Open();
        var failure = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected missing default bytes.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.NotFound);
        var exact = await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource + "/versions/2.0.0"));
        await Assert.That(exact.SelectedXid).IsEqualTo(Fixture.Resource + "/versions/2.0.0");
    }

    [Test]
    public async Task CapturedSourceChecksRevisionCommitmentUnsupportedViewsAndCancellation()
    {
        var fixture = new Fixture();
        var failure = await Assert.That(() => fixture.Open(context: new("capture", "urn:test:snapshot", "snapshot-1", true, new('0', 64))))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a root commitment mismatch.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.IntegrityError);
        await Assert.That(() => fixture.Open(context: new("capture", "urn:test:snapshot")))
            .Throws<ArgumentException>();
        var source = fixture.Open();
        var unsupported = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Entity, "/",
            representation: FederationRepresentation.ApiView))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected unsupported API view.");
        await Assert.That(unsupported.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var budget = new FederationReadBudget();
        await Assert.That(() => fixture.Open(budget: budget, token: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(budget.Work).IsEqualTo(0L);
        await Assert.That(budget.BytesRead).IsEqualTo(0L);
        await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Entity, "/"), cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("bytes", -1)]
    [Arguments("bytes", 0)]
    [Arguments("bytes", 1)]
    [Arguments("objects", -1)]
    [Arguments("objects", 0)]
    [Arguments("objects", 1)]
    [Arguments("work", -1)]
    [Arguments("work", 0)]
    [Arguments("work", 1)]
    [Arguments("object", -1)]
    [Arguments("object", 0)]
    [Arguments("object", 1)]
    public async Task CapturedInputLimitsHaveInclusiveBoundaries(string limit, int delta)
    {
        var fixture = new Fixture();
        var baseline = new FederationReadBudget();
        _ = fixture.Open(budget: baseline);
        var limits = new FederationReadLimits(
            maxTotalBytes: limit == "bytes" ? baseline.BytesRead + delta : 64 * 1024 * 1024,
            maxObjects: limit == "objects" ? baseline.ObjectsRead + delta : 4096,
            maxWork: limit == "work" ? baseline.Work + delta : 1_000_000,
            maxObjectBytes: limit == "object" ? fixture.Bytes().Length + delta : 16 * 1024 * 1024);
        if (delta < 0)
        {
            var failure = await Assert.That(() => fixture.Open(budget: new(limits))).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected captured input limit exhaustion.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var budget = new FederationReadBudget(limits);
            var source = fixture.Open(budget: budget);
            await Assert.That(source.GetExactRegistryBytes().SequenceEqual(fixture.Bytes())).IsTrue();
            await Assert.That(budget.BytesRead).IsEqualTo(baseline.BytesRead);
            await Assert.That(budget.ObjectsRead).IsEqualTo(baseline.ObjectsRead);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class PublicationFixture
    {
        internal const string RegistryVersion = "/captures/team/objects/registry/versions/fixed";
        internal const string FirstVersion = "/captures/team/objects/one/versions/fixed";
        internal const string SecondVersion = "/captures/team/objects/two/versions/fixed";
        private static readonly PublicationRegistryAccess Access = new("http", "https://publisher.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        internal Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        internal List<string> OpenedVersions { get; } = [];
        internal int ChannelReads { get; private set; }
        internal int DisposedBindings { get; private set; }
        internal PublicationByteDescriptor ManifestDescriptor { get; }
        internal bool FailOnRead { get; set; }
        internal Action<string>? OnOpen { get; set; }

        internal PublicationFixture(Fixture fixture, Action<JsonObject>? change = null)
        {
            var registry = fixture.Bytes();
            Objects[RegistryVersion] = registry;
            Objects[FirstVersion] = Fixture.First;
            Objects[SecondVersion] = Fixture.Second;
            var manifest = new JsonObject
            {
                ["format"] = "xregistry-publication/1", ["scope"] = "urn:test:scope",
                ["capture"] = "assembled", ["completeness"] = "linked",
                ["registry"] = Descriptor(RegistryVersion, registry),
                ["documents"] = new JsonArray(
                    Association("1.0.0", FirstVersion, Fixture.First),
                    Association("2.0.0", SecondVersion, Fixture.Second))
            };
            change?.Invoke(manifest);
            var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            var manifestXid = "/publications/team/snapshots/" + Digest(manifestBytes) + "/versions/manifest";
            ManifestDescriptor = new(new(Access, manifestXid), "sha256:" + Digest(manifestBytes), manifestBytes.Length);
            Objects[manifestXid] = manifestBytes;
        }

        internal ValueTask<PublicationOpenedSnapshot> OpenAsync() =>
            new PublicationReadClient((_, _) => ValueTask.FromResult<IPublicationReadBinding>(new Binding(this)))
                .OpenChannelAsync(Access, "/publications/team/channels/stable");

        private static JsonObject Association(string version, string storage, byte[] bytes) => new()
        {
            ["viewVersion"] = Fixture.Resource + "/versions/" + version,
            ["originRegistry"] = "urn:test:original-registry",
            ["originVersion"] = "/originals/site/models/model/versions/old",
            ["base"] = "https://example.test/relocated/model.json", ["content"] = Descriptor(storage, bytes)
        };

        private static JsonObject Descriptor(string version, byte[] bytes) => new()
        {
            ["reference"] = new JsonObject
            {
                ["registry"] = new JsonObject { ["binding"] = Access.Binding, ["endpoint"] = Access.Endpoint, ["parameters"] = new JsonObject() },
                ["version"] = version
            },
            ["digest"] = "sha256:" + Digest(bytes), ["size"] = bytes.Length
        };

        private sealed class Binding(PublicationFixture owner) : IPublicationReadBinding
        {
            public PublicationRegistryAccess Access => PublicationFixture.Access;
            public ValueTask<PublicationChannelMetadata> ReadChannelMetadataAsync(string channelResource, CancellationToken cancellationToken = default)
            {
                if (owner.FailOnRead) { throw new InvalidOperationException("The selected channel must not reopen."); }
                owner.ChannelReads++;
                return ValueTask.FromResult(new PublicationChannelMetadata("urn:test:scope", owner.ManifestDescriptor, 1));
            }
            public ValueTask<Stream> OpenVersionDocumentAsync(string version, CancellationToken cancellationToken = default)
            {
                if (owner.FailOnRead) { throw new InvalidOperationException("Retained document reads must not reopen a binding."); }
                owner.OpenedVersions.Add(version);
                owner.OnOpen?.Invoke(version);
                if (!owner.Objects.TryGetValue(version, out var bytes))
                {
                    throw new PublicationException("object_not_found", "", "The exact stored Version is unavailable.");
                }
                return ValueTask.FromResult<Stream>(new MemoryStream(bytes, false));
            }
            public ValueTask DisposeAsync()
            {
                owner.DisposedBindings++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Fixture
    {
        internal const string Collection = "/sets/team/things";
        internal const string Resource = Collection + "/a";
        internal const string WotId = "urn:test:device:v1";
        internal static readonly byte[] First = Encoding.UTF8.GetBytes(" {\"id\":\"urn:test:device:v1\",\"version\":{\"model\":\"1.0.0\"},\"properties\":{\"a\":{\"type\":\"number\"}}}\r\n");
        internal static readonly byte[] Second = Encoding.UTF8.GetBytes("{\"id\":\"urn:test:device:v1\",\"version\":{\"model\":\"2.0.0\"}}\n");
        internal RegistryModel Model { get; set; } = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"sets":{"singular":"set","resources":{"things":{"singular":"thingmodel","versionmode":"semver","singleversionroot":true}}}}}
            """));
        internal JsonObject Root { get; }
        internal Dictionary<string, FederationDocument> Documents { get; } = new(StringComparer.Ordinal)
        {
            [Resource + "/versions/1.0.0"] = new(First, "application/tm+json", "https://example.test/original/model.json", "urn:test:original-document"),
            [Resource + "/versions/2.0.0"] = new(Second, "application/tm+json", "https://example.test/original/model.json", "urn:test:original-document")
        };

        internal Fixture()
        {
            Root = new()
            {
                ["registryid"] = "captured", ["specversion"] = "1.0-rc4", ["xid"] = "/", ["self"] = "#",
                ["modelsource"] = JsonNode.Parse(Model.Source.RootElement.GetRawText()),
                ["capabilities"] = JsonNode.Parse("""
                    {"available":{"entities":{"mutable":false},"model":{"mutable":false},"capabilities":{"mutable":false}},
                    "federation":{"resolution":"producer"}}
                    """),
                ["setscount"] = 1, ["setsurl"] = "#/sets", ["sets"] = new JsonObject
                {
                    ["team"] = new JsonObject
                    {
                        ["setid"] = "team", ["xid"] = "/sets/team", ["self"] = "#/sets/team",
                        ["thingscount"] = 1, ["thingsurl"] = "#/sets/team/things", ["things"] = new JsonObject
                        {
                            ["a"] = new JsonObject
                            {
                                ["thingmodelid"] = "a", ["xid"] = Resource, ["self"] = "#/sets/team/things/a",
                                ["metaurl"] = "#/sets/team/things/a/meta",
                                ["meta"] = new JsonObject
                                {
                                    ["thingmodelid"] = "a", ["xid"] = Resource + "/meta", ["self"] = "#/sets/team/things/a/meta",
                                    ["defaultversionid"] = "1.0.0", ["defaultversionsticky"] = false,
                                    ["defaultversionurl"] = "#/sets/team/things/a/versions/1.0.0"
                                },
                                ["versionscount"] = 2, ["versionsurl"] = "#/sets/team/things/a/versions",
                                ["versions"] = new JsonObject { ["1.0.0"] = Version("1.0.0"), ["2.0.0"] = Version("2.0.0") }
                            }
                        }
                    }
                }
            };
        }

        internal void AddAlias()
        {
            var group = Root["sets"]!["team"]!;
            group["thingscount"] = 2;
            group["things"]!["alias"] = new JsonObject
            {
                ["thingmodelid"] = "alias", ["xid"] = Collection + "/alias", ["self"] = "#/sets/team/things/alias",
                ["metaurl"] = "#/sets/team/things/alias/meta",
                ["meta"] = new JsonObject
                {
                    ["thingmodelid"] = "alias", ["xid"] = Collection + "/alias/meta",
                    ["self"] = "#/sets/team/things/alias/meta", ["xref"] = Resource
                }
            };
        }

        internal void RenameResource(string id)
        {
            var map = Root["sets"]!["team"]!["things"]!.AsObject();
            var resource = map["a"]!.AsObject();
            map.Remove("a");
            map[id] = resource;
            var xid = Collection + "/" + Uri.EscapeDataString(id);
            var pointer = "#/sets/team/things/" + Uri.EscapeDataString(
                id.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            resource["thingmodelid"] = id;
            resource["xid"] = xid;
            resource["self"] = pointer;
            resource["metaurl"] = pointer + "/meta";
            resource["versionsurl"] = pointer + "/versions";
            resource["meta"]!["thingmodelid"] = id;
            resource["meta"]!["xid"] = xid + "/meta";
            resource["meta"]!["self"] = pointer + "/meta";
            resource["meta"]!["defaultversionurl"] = pointer + "/versions/1.0.0";
            foreach (var version in resource["versions"]!.AsObject())
            {
                version.Value!["thingmodelid"] = id;
                version.Value!["xid"] = xid + "/versions/" + version.Key;
                version.Value!["self"] = pointer + "/versions/" + version.Key;
                var document = Documents[Resource + "/versions/" + version.Key];
                Documents.Remove(Resource + "/versions/" + version.Key);
                Documents[xid + "/versions/" + version.Key] = document;
            }
        }

        internal byte[] Bytes() => Encoding.UTF8.GetBytes(Root.ToJsonString());
        internal CapturedRegistryReadSource Open(byte[]? bytes = null, FederationReadBudget? budget = null,
            NativeRegistryContext? context = null, CancellationToken token = default) =>
            new(bytes ?? Bytes(), Model, context ?? new("capture", "urn:test:snapshot", "snapshot-1", true)
            { RootPath = "captured", RequestedRevision = "stable" }, Documents, budget, token);

        private static JsonObject Version(string id) => new()
        {
            ["thingmodelid"] = "a", ["versionid"] = id, ["xid"] = Resource + "/versions/" + id,
            ["self"] = "#/sets/team/things/a/versions/" + id, ["wotid"] = WotId,
            ["epoch"] = 71, ["createdat"] = "2025-01-02T03:04:05Z",
            ["labels"] = new JsonObject { ["release"] = id == "1.0.0" ? "v1" : "v2" }
        };
    }
}
