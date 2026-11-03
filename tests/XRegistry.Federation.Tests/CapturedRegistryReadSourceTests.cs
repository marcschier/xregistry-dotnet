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
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task AuthoredRequirementsShareInclusiveWorkAndCancellation(int delta)
    {
        var document = InventoryDocument("""{"a":{"tm:ref":"urn:inventory:tm","tm:minVersion":"1.0.0"}}""", "[]");
        var uri = new Uri("https://example.test/entry.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var dependency = (await WotJsonLdBaseResolver.DiscoverCapturedReferenceInventoryAsync(document, uri,
            WotReferenceTargetKind.ThingModel, contexts, InventoryIndex(new Fixture().Open()))).Dependencies.Single();
        var baseline = new FederationReadBudget();
        _ = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingModel, dependency, contexts, baseline);
        var budget = new FederationReadBudget(new(maxWork: baseline.Work + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document,
                uri, WotReferenceTargetKind.ThingModel, dependency, contexts, budget)).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected shared requirement traversal limit.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var request = await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
                WotReferenceTargetKind.ThingModel, dependency, contexts, budget);
            await Assert.That(request.MinimumVersion).IsEqualTo("1.0.0");
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledBudget = new FederationReadBudget();
        await Assert.That(async () => await WotJsonLdBaseResolver.ReadSemVerReferenceRequirementsAsync(document, uri,
            WotReferenceTargetKind.ThingModel, dependency, contexts, canceledBudget, cancellation.Token)).Throws<OperationCanceledException>();
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
