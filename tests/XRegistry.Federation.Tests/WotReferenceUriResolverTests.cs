// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using XRegistry.Models;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class WotReferenceUriResolverTests
{
    [Test]
    [Arguments(WotReferenceTargetKind.ThingModel, "tm:ThingModel")]
    [Arguments(WotReferenceTargetKind.ThingDescription, "Thing")]
    public async Task ProjectionSourcesRetainManifestOrderCrossKindDeclarationsAndExactEntryPointers(
        WotReferenceTargetKind sourceKind, string ordinaryType)
    {
        var document = ProjectionDocument("""
            [{"p:sourceName":"z-source","href":"../td/source.json","type":"application/td+json"},
             {"p:sourceName":"a-source","href":"../tm/source.td.json","type":"application/tm+json"}]
            """, ordinaryType);
        var original = await ReadBytesAsync(document);
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
            new Uri("https://example.test/plans/plan.json"), sourceKind, new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(2);
        await Assert.That(references[0].SourceName).IsEqualTo("z-source");
        await Assert.That(references[1].SourceName).IsEqualTo("a-source");
        await Assert.That(references[0].TargetKind).IsEqualTo(WotReferenceTargetKind.ThingDescription);
        await Assert.That(references[1].TargetKind).IsEqualTo(WotReferenceTargetKind.ThingModel);
        await Assert.That(references[0].Reference.SourcePointer).IsEqualTo("/p:projects/0");
        await Assert.That(references[1].Reference.SourcePointer).IsEqualTo("/p:projects/1");
        await Assert.That(references[0].Reference.PropertyName).IsEqualTo("href");
        await Assert.That(references[0].Reference.Operator).IsEqualTo(
            "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projects");
        await Assert.That(references[0].Reference.Href).IsEqualTo("../td/source.json");
        await Assert.That(references[0].Reference.Target.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/td/source.json");
        await Assert.That(references[1].Reference.Target.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/tm/source.td.json");
        await Assert.That((await ReadBytesAsync(document)).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task ProjectionMarkerAndMembersUseCapturedExpandedMeaningsRatherThanLiteralPrefixes()
    {
        var contextUri = new Uri("https://contexts.example.test/projection.jsonld");
        var context = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":{
              "kinds":"@type",
              "TD":"https://www.w3.org/2019/wot/td#Thing",
              "unresolved":"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projection",
              "manifest~sources":{
                "@id":"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projects",
                "@container":"@list",
                "@context":{"identity":"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#sourceName"}
              }
            }}
            """));
        var original = await ReadBytesAsync(context);
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":[{"@base":"https://original.example.test/plans/plan.json"},
                         "https://contexts.example.test/projection.jsonld"],
             "kinds":["TD","unresolved"],
             "manifest~sources":[{"identity":"pump","href":"../td/pump.json","type":"application/td+json"}]}
            """));
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
            new Uri("https://replica.example.test/storage/plan.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument> { [contextUri] = context });

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourceName).IsEqualTo("pump");
        await Assert.That(references[0].Reference.SourcePointer).IsEqualTo("/manifest~0sources/0");
        await Assert.That(references[0].Reference.Target.DocumentUri.AbsoluteUri)
            .IsEqualTo("https://original.example.test/td/pump.json");
        await Assert.That((await ReadBytesAsync(context)).SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task ProjectionSourcesUseTheSelectedExperimentalContextAsCapturedBytes()
    {
        var uri = new Uri("https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld");
        var context = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context": {
                "wotproj": {
                  "@id": "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#",
                  "@prefix": true
                },
                "wotproj:projects": {
                  "@id": "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projects",
                  "@container": "@list"
                },
                "wotproj:scenario": {
                  "@id": "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#scenario",
                  "@type": "@id"
                },
                "wotproj:resolvedFrom": {
                  "@id": "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#resolvedFrom",
                  "@type": "@id"
                }
              }
            }
            """));
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld",
             "@type":["https://www.w3.org/2019/wot/td#Thing","wotproj:projection"],
             "wotproj:projects":[{"wotproj:sourceName":"pump","href":"pump.json","type":"application/td+json"}]}
            """));
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
            new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument> { [uri] = context });
        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourceName).IsEqualTo("pump");
        await Assert.That(references[0].Reference.SourcePointer).IsEqualTo("/wotproj:projects/0");
        await Assert.That(references[0].Reference.Target.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/pump.json");
    }

    [Test]
    [Arguments("https://example.org/wot/projection#projection")]
    [Arguments("https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#ProjectionPlan")]
    [Arguments("https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#Projection")]
    [Arguments("https://example.test/vendor#projection")]
    public async Task ProjectionDiscoveryDoesNotAliasPlaceholderCaseChangedOrForeignMarkers(string marker)
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@type":["https://www.w3.org/2019/wot/td#Thing",
            """ + System.Text.Json.JsonSerializer.Serialize(marker) + "]}"));
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
            new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("@graph")]
    [Arguments("@included")]
    [Arguments("https://example.test/nested")]
    public async Task AProjectionMarkerOnAnotherNodeDoesNotMarkTheAuthoredRoot(string container)
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("{\"" + container + "\":" + """
            [{"@type":["https://www.w3.org/2019/wot/td#Thing",
              "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projection"]}]}
            """));
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
            new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""["Thing","tm:ThingModel"]""")]
    [Arguments("""["tm:ThingModel"]""")]
    [Arguments("""["https://example.test/NotAThing"]""")]
    public async Task MarkedProjectionRequiresExactlyItsCapturedOrdinaryRootKind(string ordinaryTypes)
    {
        var types = System.Text.Json.JsonSerializer.Deserialize<string[]>(ordinaryTypes)!;
        var document = ProjectionDocument("""
            [{"p:sourceName":"source","href":"source.json","type":"application/td+json"}]
            """, types);
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
                new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected captured projection-kind rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("{}")]
    [Arguments("[1]")]
    public async Task ProjectionManifestMustBeANonemptyArrayOfObjects(string entries)
    {
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(ProjectionDocument(entries, "Thing"),
                new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected projection-manifest rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("""{"href":"source.json","type":"application/td+json"}""")]
    [Arguments("""{"p:sourceName":"","href":"source.json","type":"application/td+json"}""")]
    [Arguments("""{"p:sourceName":1,"href":"source.json","type":"application/td+json"}""")]
    [Arguments("""{"p:sourceName":"pump","type":"application/td+json"}""")]
    [Arguments("""{"p:sourceName":"pump","href":null,"type":"application/td+json"}""")]
    [Arguments("""{"p:sourceName":"pump","href":["source.json"],"type":"application/td+json"}""")]
    [Arguments("""{"p:sourceName":"pump","href":"source.json"}""")]
    [Arguments("""{"p:sourceName":"pump","href":"source.json","type":1}""")]
    [Arguments("""{"p:sourceName":"pump","href":"source.json","type":"application/json"}""")]
    [Arguments("""{"p:sourceName":"pump","href":"source.json","type":"application/td+json garbage"}""")]
    [Arguments("""{"p:sourceName":"pump","href":"source.json#","type":"application/td+json"}""")]
    [Arguments("""{"p:sourceName":"pump","href":"source.json#/properties/a","type":"application/td+json"}""")]
    public async Task ProjectionSourcesRejectMissingMalformedOrFragmentBearingIdentityFields(string entry)
    {
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(ProjectionDocument("[" + entry + "]", "Thing"),
                new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected projection-source rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("pump", "other.json")]
    [Arguments("other", "./folder/../source.json")]
    public async Task ProjectionSourceNamesAndResolvedDocumentUrisMustBeUnique(string secondName, string secondHref)
    {
        var entries = """
            [{"p:sourceName":"pump","href":"source.json","type":"application/td+json"},
             {"p:sourceName":
            """ + System.Text.Json.JsonSerializer.Serialize(secondName) + ",\"href\":" +
            System.Text.Json.JsonSerializer.Serialize(secondHref) + ""","type":"application/tm+json"}]""";
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(ProjectionDocument(entries, "Thing"),
                new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected duplicate projection-source rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ProjectionSourcesKeepCaseDistinctAliasesAndDocumentLocalCycleCandidates()
    {
        var document = ProjectionDocument("""
            [{"p:sourceName":"pump","href":"","type":"application/td+json"},
             {"p:sourceName":"Pump","href":"source.json","type":"application/td+json"}]
            """, "Thing");
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
            new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(2);
        await Assert.That(references[0].SourceName).IsEqualTo("pump");
        await Assert.That(references[1].SourceName).IsEqualTo("Pump");
        await Assert.That(references[0].Reference.Href).IsEqualTo("");
        await Assert.That(references[0].Reference.Target.IsDocumentLocal).IsTrue();
    }

    [Test]
    [Arguments("application/TD+json", WotReferenceTargetKind.ThingDescription)]
    [Arguments("APPLICATION/TM+JSON; charset=utf-8", WotReferenceTargetKind.ThingModel)]
    public async Task ProjectionSourceMediaTypesUseMimeCaseAndParameterRules(string mediaType,
        WotReferenceTargetKind expectedKind)
    {
        var document = ProjectionDocument("""
            [{"p:sourceName":"pump","href":"source.json","type":
            """ + System.Text.Json.JsonSerializer.Serialize(mediaType) + "}]", "Thing");
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
            new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].TargetKind).IsEqualTo(expectedKind);
    }

    [Test]
    [Arguments("""{"p:projectionKind":"Thing"}""")]
    [Arguments("""{"other":{"@id":"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projects","@container":"@list"}}""")]
    public async Task ProjectionDiscoveryRejectsSeparateKindAndCompetingManifestAliases(string addition)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(await ReadBytesAsync(ProjectionDocument("""
            [{"p:sourceName":"pump","href":"source.json","type":"application/td+json"}]
            """, "Thing")))!.AsObject();
        var extra = System.Text.Json.Nodes.JsonNode.Parse(addition)!.AsObject();
        if (extra.ContainsKey("p:projectionKind"))
        {
            root["p:projectionKind"] = "Thing";
        }
        else
        {
            root["@context"]!["other"] = extra["other"]!.DeepClone();
            root["other"] = root["p:projects"]!.DeepClone();
        }
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(
                new(Encoding.UTF8.GetBytes(root.ToJsonString())),
                new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected conflicting projection-member rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ProjectionSourcesRejectCompetingExpandedSourceNameMembers()
    {
        var document = ProjectionDocument("""
            [{"@context":{"alias":"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#sourceName"},
              "p:sourceName":"pump","alias":"pump","href":"source.json","type":"application/td+json"}]
            """, "Thing");
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
                new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected ambiguous projection source-name rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task MarkedProjectionCannotOmitItsManifest()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@type":["https://www.w3.org/2019/wot/td#Thing",
              "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projection"]}
            """));
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
                new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected missing projection-manifest rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ExpandedProjectionManifestNamesRetainJsonPointerSlashEscaping()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@type":["https://www.w3.org/2019/wot/td#Thing",
              "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projection"],
             "https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projects":[
               {"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#sourceName":"pump",
                "href":"source.json","type":"application/td+json"}]}
            """));
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
            new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].Reference.SourcePointer).IsEqualTo(
            "/https:~1~1raw.githubusercontent.com~1marcschier~1wot-thing-description~1main~1proposals~1wot-projections~1context.experimental-v1.jsonld#projects/0");
    }

    [Test]
    public async Task UnmarkedProjectionDiscoveryStillChecksCapturedContextsAndCancellation()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("{}"));
        var contexts = new Dictionary<Uri, FederationDocument>();
        var uri = new Uri("https://example.test/plan.json");
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts);
        await Assert.That(references.Count).IsEqualTo(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document, uri,
                WotReferenceTargetKind.ThingDescription, contexts, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(
                new(Encoding.UTF8.GetBytes("""{"@context":"https://uncaptured.example.test/context.jsonld"}""")),
                uri, WotReferenceTargetKind.ThingDescription, contexts)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected uncaptured projection-context rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task ProjectionSourceDiscoveryUsesSharedInclusiveWorkLimitsAndCancellation()
    {
        var document = ProjectionDocument("""
            [{"p:sourceName":"pump","href":"source.json","type":"application/td+json"}]
            """, "Thing");
        var uri = new Uri("https://example.test/plan.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var measured = new FederationReadBudget();
        var references = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts, measured);
        await Assert.That(references.Count).IsEqualTo(1);
        var atLimit = new FederationReadBudget(new(maxWork: measured.Work));
        var repeated = await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts, atLimit);
        await Assert.That(repeated[0].Reference).IsEqualTo(references[0].Reference);
        await Assert.That(atLimit.Work).IsEqualTo(measured.Work);
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document, uri,
                WotReferenceTargetKind.ThingDescription, contexts, new(new(maxWork: measured.Work - 1))))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected adjacent projection work exhaustion.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document, uri,
                WotReferenceTargetKind.ThingDescription, contexts, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProjectionSourceDiscoveryChecksInputAndExpandedResultBounds(bool resultLimit)
    {
        var document = ProjectionDocument("""
            [{"p:sourceName":"pump","href":"source.json","type":"application/td+json"}]
            """, "Thing");
        var limits = resultLimit
            ? new FederationReadLimits(maxResultBytes: 1)
            : new FederationReadLimits(maxObjectBytes: (int)document.Length - 1);
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverProjectionSourceReferencesAsync(document,
                new Uri("https://example.test/plan.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>(), new(limits))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected projection document-bound rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    private static FederationDocument ProjectionDocument(string entries, params string[] ordinaryTypes)
    {
        var types = ordinaryTypes.Append("p:projection").ToArray();
        return new(Encoding.UTF8.GetBytes("""
            {"@context":{
              "Thing":"https://www.w3.org/2019/wot/td#Thing",
              "tm":"https://www.w3.org/2019/wot/tm#",
              "p":"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#",
              "p:projects":{
                "@id":"https://raw.githubusercontent.com/marcschier/wot-thing-description/main/proposals/wot-projections/context.experimental-v1.jsonld#projects",
                "@container":"@list"
              }
            },"@type":
            """ + System.Text.Json.JsonSerializer.Serialize(types) + ",\"p:projects\":" + entries + "}"));
    }

    [Test]
    [Arguments("tm:extends")]
    [Arguments("tm:submodel")]
    [Arguments("td:extends")]
    [Arguments("td:subthing")]
    [Arguments("type")]
    [Arguments("describedby")]
    public async Task DiscoversLiteralReferenceLinksWithUnchangedHrefAndLinkObjectPointer(string relation)
    {
        var document = LinkDocument("[{\"rel\":" + System.Text.Json.JsonSerializer.Serialize(relation) +
            ""","href":"../models/target.json#/properties/a%2Fb"}]""");
        var references = await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document,
            new Uri("https://example.test/td/document.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/links/0");
        await Assert.That(references[0].PropertyName).IsEqualTo("href");
        await Assert.That(references[0].Operator).IsEqualTo(relation);
        await Assert.That(references[0].Href).IsEqualTo("../models/target.json#/properties/a%2Fb");
        await Assert.That(references[0].Target.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/models/target.json");
        await Assert.That(references[0].Target.Fragment).IsEqualTo("#/properties/a%2Fb");
    }

    [Test]
    public async Task LinkMemberAliasesRetainAuthoredSpellingAndEffectiveBase()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context":{
                "edges~name":{
                  "@id":"https://www.w3.org/2019/wot/td#hasLink",
                  "@container":"@set",
                  "@context":{
                    "relationship":"https://www.w3.org/2019/wot/hypermedia#hasRelationType",
                    "target":{
                      "@id":"https://www.w3.org/2019/wot/hypermedia#hasTarget",
                      "@context":{"@base":"https://original.example.test/scoped/document.json"}
                    }
                  }
                }
              },
              "edges~name":[{"relationship":"tm:extends","target":"model.json#/properties/a"}]
            }
            """));
        var references = await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document,
            new Uri("https://example.test/td/document.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/edges~0name/0");
        await Assert.That(references[0].PropertyName).IsEqualTo("target");
        await Assert.That(references[0].Operator).IsEqualTo("tm:extends");
        await Assert.That(references[0].Href).IsEqualTo("model.json#/properties/a");
        await Assert.That(references[0].Target.DocumentUri.AbsoluteUri).IsEqualTo("https://original.example.test/scoped/model.json");
    }

    [Test]
    [Arguments("model:extends")]
    [Arguments("https://www.w3.org/2019/wot/tm#extends")]
    [Arguments("TM:extends")]
    [Arguments("Type")]
    [Arguments("relatedto")]
    [Arguments("@type")]
    public async Task LinkRelationsAreLiteralRatherThanVocabularyExpandedOrCaseFolded(string relation)
    {
        var document = LinkDocument("[{\"rel\":" + System.Text.Json.JsonSerializer.Serialize(relation) +
            ""","href":"urn:example:target"}]""");
        var references = await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document,
            new Uri("https://example.test/document.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(WotReferenceTargetKind.ThingModel, 0)]
    [Arguments(WotReferenceTargetKind.ThingDescription, 1)]
    public async Task OnlyACapturedThingDescriptionContributesATypeLink(
        WotReferenceTargetKind sourceKind, int expectedCount)
    {
        var document = LinkDocument("""[{"rel":"type","href":"urn:example:model"}]""");
        var references = await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document,
            new Uri("https://example.test/document.json"), sourceKind,
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(expectedCount);
        if (expectedCount != 0)
        {
            await Assert.That(references[0].Operator).IsEqualTo("type");
        }
    }

    [Test]
    public async Task RepeatedLinkTargetsRemainSeparateAuthoredOccurrencesAndLocalLinksRemainClassified()
    {
        var document = LinkDocument("""
            [
              {"rel":"tm:extends","href":"urn:example:model"},
              {"rel":"tm:extends","href":"urn:example:model"},
              {"rel":"td:subthing","href":"#/properties/a"}
            ]
            """);
        var references = await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document,
            new Uri("https://example.test/document.json"), WotReferenceTargetKind.ThingDescription,
            new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(3);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/links/0");
        await Assert.That(references[1].SourcePointer).IsEqualTo("/links/1");
        await Assert.That(references[0].Href).IsEqualTo(references[1].Href);
        await Assert.That(references[2].SourcePointer).IsEqualTo("/links/2");
        await Assert.That(references[2].Target.IsDocumentLocal).IsTrue();
    }

    [Test]
    [Arguments("""{"rel":"tm:extends"}""")]
    [Arguments("""{"rel":"tm:extends","href":null}""")]
    [Arguments("""{"rel":"tm:extends","href":1}""")]
    [Arguments("""{"rel":"tm:extends","href":["urn:example:target"]}""")]
    [Arguments("""{"rel":"tm:extends","href":{"@id":"urn:example:target"}}""")]
    [Arguments("""{"rel":["tm:extends"],"href":"urn:example:target"}""")]
    [Arguments("""{"rel":false,"href":"urn:example:target"}""")]
    [Arguments("""{"rel":"tm:extends","relation":"tm:extends","href":"urn:example:target"}""")]
    [Arguments("""{"rel":"tm:extends","href":"urn:example:target","target":"urn:example:target"}""")]
    public async Task ReferenceLinksRejectMissingMalformedOrCompetingSemanticMembers(string link)
    {
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(LinkDocument("[" + link + "]"),
                new Uri("https://example.test/document.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected invalid reference-link rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("null")]
    [Arguments("""{"rel":"tm:extends","href":"urn:example:target"}""")]
    [Arguments("[1]")]
    public async Task LinkDiscoveryRejectsNonArrayCollectionsAndNonObjectEntries(string links)
    {
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(LinkDocument(links),
                new Uri("https://example.test/document.json"), WotReferenceTargetKind.ThingDescription,
                new Dictionary<Uri, FederationDocument>())).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected invalid link-collection rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task LinkDiscoveryIsEmptyCancellableAndBoundedWithoutReturningAPartialList()
    {
        var document = LinkDocument("[]");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var uri = new Uri("https://example.test/document.json");
        var empty = await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document, uri,
            WotReferenceTargetKind.ThingDescription, contexts);
        await Assert.That(empty.Count).IsEqualTo(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document, uri,
                WotReferenceTargetKind.ThingDescription, contexts, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverLinkReferencesAsync(document, uri,
                WotReferenceTargetKind.ThingDescription, contexts, new(new(maxWork: 1))))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected shared link-discovery limits.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    private static FederationDocument LinkDocument(string links) => new(Encoding.UTF8.GetBytes("""
        {
          "@context":{
            "@vocab":"urn:example:",
            "model":"https://www.w3.org/2019/wot/tm#",
            "links":{
              "@id":"https://www.w3.org/2019/wot/td#hasLink",
              "@container":"@set",
              "@context":{
                "rel":"https://www.w3.org/2019/wot/hypermedia#hasRelationType",
                "relation":"https://www.w3.org/2019/wot/hypermedia#hasRelationType",
                "href":{"@id":"https://www.w3.org/2019/wot/hypermedia#hasTarget",
                        "@type":"http://www.w3.org/2001/XMLSchema#anyURI"},
                "target":"https://www.w3.org/2019/wot/hypermedia#hasTarget"
              }
            }
          },
          "links":
        """ + links + "}"));

    [Test]
    public async Task DiscoversOrdinaryReferencesByExpandedMeaningAndPreservesAuthoredPointersAndHrefs()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context": {
                "@vocab": "urn:example:",
                "model": "https://www.w3.org/2019/wot/tm#",
                "descriptionRef": "https://www.w3.org/2019/wot/td#ref",
                "tm": "urn:not-the-tm-vocabulary:"
              },
              "properties": {
                "a/b~c": {
                  "model:ref": "../models/device.json#/properties/a%2Fb",
                  "tm:ref": "urn:not-a-dependency"
                }
              },
              "children": [
                {"descriptionRef":"#/properties/a~1b~0c"},
                {"model:ref":"urn:example:other#/"}
              ]
            }
            """), documentBase: "https://example.test/td/document.json");
        var original = await ReadBytesAsync(document);
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/td/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(3);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/properties/a~1b~0c");
        await Assert.That(references[0].PropertyName).IsEqualTo("model:ref");
        await Assert.That(references[0].Operator).IsEqualTo("tm:ref");
        await Assert.That(references[0].Href).IsEqualTo("../models/device.json#/properties/a%2Fb");
        await Assert.That(references[0].Target.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/models/device.json");
        await Assert.That(references[0].Target.Fragment).IsEqualTo("#/properties/a%2Fb");
        await Assert.That(references[1].SourcePointer).IsEqualTo("/children/0");
        await Assert.That(references[1].Operator).IsEqualTo("td:ref");
        await Assert.That(references[1].Target.IsDocumentLocal).IsTrue();
        await Assert.That(references[2].SourcePointer).IsEqualTo("/children/1");
        await Assert.That(references[2].Target.IsDocumentLocal).IsFalse();
        await Assert.That((await ReadBytesAsync(document)).SequenceEqual(original)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MemberMeaningHonorsPropertyAndTypeScopedContexts(bool typeScoped)
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes(typeScoped
            ? """
              {
                "@context": {
                  "@vocab":"urn:example:",
                  "Scoped": {"@id":"urn:example:Scoped","@context":{
                    "ref":"https://www.w3.org/2019/wot/td#ref"
                  }}
                },
                "nested":{"@type":"Scoped","ref":"urn:example:target"}
              }
              """
            : """
              {
                "@context": {
                  "@vocab":"urn:example:",
                  "nested":{"@id":"urn:example:nested","@context":{
                    "ref":"https://www.w3.org/2019/wot/tm#ref"
                  }}
                },
                "nested":{"ref":"urn:example:target"}
              }
              """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/nested");
        await Assert.That(references[0].Operator).IsEqualTo(typeScoped ? "td:ref" : "tm:ref");
        await Assert.That(references[0].Href).IsEqualTo("urn:example:target");
    }

    [Test]
    public async Task MemberMeaningSurvivesContextResetWithoutRestoringRemovedAliases()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context":{"@vocab":"urn:example:","model":"https://www.w3.org/2019/wot/tm#"},
              "nested":{
                "@context":null,
                "model:ref":"urn:not-a-dependency",
                "https://www.w3.org/2019/wot/td#ref":"urn:example:target"
              }
            }
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/nested");
        await Assert.That(references[0].PropertyName).IsEqualTo("https://www.w3.org/2019/wot/td#ref");
        await Assert.That(references[0].Operator).IsEqualTo("td:ref");
    }

    [Test]
    public async Task MemberMeaningDoesNotInterpretReferenceShapedMembersInsideJsonLiterals()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context":{
                "@vocab":"urn:example:",
                "tm":"https://www.w3.org/2019/wot/tm#",
                "literal":{"@id":"urn:example:literal","@type":"@json"}
              },
              "literal":{"tm:ref":"urn:not-a-dependency"},
              "tm:ref":"urn:example:target"
            }
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourcePointer).IsEqualTo("");
        await Assert.That(references[0].Href).IsEqualTo("urn:example:target");
    }

    [Test]
    public async Task NonPropagatingContextAppliesOnlyToTheObjectWhereItWasDeclared()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context":{"@vocab":"urn:example:","ref":"https://www.w3.org/2019/wot/tm#ref"},
              "nested":{
                "@context":{"@propagate":false,"ref":"https://www.w3.org/2019/wot/td#ref"},
                "ref":"urn:example:td",
                "child":{"ref":"urn:example:tm"}
              }
            }
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(2);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/nested");
        await Assert.That(references[0].Operator).IsEqualTo("td:ref");
        await Assert.That(references[1].SourcePointer).IsEqualTo("/nested/child");
        await Assert.That(references[1].Operator).IsEqualTo("tm:ref");
    }

    [Test]
    [Arguments("@index")]
    [Arguments("@id")]
    [Arguments("@type")]
    public async Task ReferenceDiscoveryPreservesAuthoredContainerKeys(string container)
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes(
            """
            {"@context":{
              "@vocab":"urn:example:",
              "ref":"https://www.w3.org/2019/wot/tm#ref",
              "map":{"@id":"urn:example:map","@container":"
            """ + container + """
            "}},"map":{"key/with~escapes":{"ref":"urn:example:target"}}}
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/map/key~1with~0escapes");
        await Assert.That(references[0].Operator).IsEqualTo("tm:ref");
    }

    [Test]
    public async Task ContextAliasesForKeywordsDoNotBecomeOrdinaryReferenceMembers()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context":{
                "@vocab":"urn:example:",
                "kind":"@type","identity":"@id",
                "ref":"https://www.w3.org/2019/wot/tm#ref"
              },
              "kind":"urn:example:Thing",
              "identity":"urn:example:source",
              "ref":"urn:example:target"
            }
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].PropertyName).IsEqualTo("ref");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ContextArraysRetainTheOuterContextProcessingPropagationParameter(bool remote)
    {
        const string nestedContext = """
            [{"@propagate":false,"ref":"https://www.w3.org/2019/wot/td#ref"}]
            """;
        var document = new FederationDocument(Encoding.UTF8.GetBytes(
            """{"@context":{"@vocab":"urn:example:","ref":"https://www.w3.org/2019/wot/tm#ref"},"nested":{"@context":""" +
            (remote ? "\"https://contexts.example.test/scoped.jsonld\"" : nestedContext) +
            ""","ref":"urn:example:td","child":{"ref":"urn:example:tm"}}}"""));
        var contexts = new Dictionary<Uri, FederationDocument>();
        if (remote)
        {
            contexts.Add(new("https://contexts.example.test/scoped.jsonld"),
                new(Encoding.UTF8.GetBytes("{\"@context\":" + nestedContext + "}")));
        }
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), contexts);

        await Assert.That(references.Count).IsEqualTo(2);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/nested");
        await Assert.That(references[0].Operator).IsEqualTo("td:ref");
        await Assert.That(references[1].SourcePointer).IsEqualTo("/nested/child");
        // JSON-LD API 4.1.2 reads @propagate from the local context object before array normalization.
        await Assert.That(references[1].Operator).IsEqualTo("td:ref");
    }

    [Test]
    public async Task ReferenceDiscoveryCannotBeDisabledByRedefiningTheUrnPrefix()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":{
              "urn":{"@id":"https://unrelated.example.test/","@prefix":true},
              "ref":"https://www.w3.org/2019/wot/tm#ref"
            },"ref":"https://example.test/target.json"}
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].Operator).IsEqualTo("tm:ref");
        await Assert.That(references[0].Target.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/target.json");
    }

    [Test]
    public async Task EmptyDiscoveryStillValidatesItsCapturedContexts()
    {
        var uri = new Uri("https://example.test/document.json");
        var contexts = new Dictionary<Uri, FederationDocument>();
        var empty = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(
            new(Encoding.UTF8.GetBytes("{}")), uri, contexts);
        await Assert.That(empty.Count).IsEqualTo(0);

        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(
                new(Encoding.UTF8.GetBytes("""{"@context":"https://contexts.example.test/missing.jsonld"}""")),
                uri, contexts)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected validation even without candidate members.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task ReferenceDiscoveryUsesImportedProtectedAndRelativeCapturedContexts()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context":"https://contexts.example.test/contexts/wot.jsonld",
              "nested":{"ref":"urn:example:target"}
            }
            """));
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [new("https://contexts.example.test/contexts/wot.jsonld")] = new(Encoding.UTF8.GetBytes("""
                {"@context":{"@import":"base.jsonld","nested":{
                  "@id":"urn:example:nested","@context":{"ref":"https://www.w3.org/2019/wot/td#ref"}
                }}}
                """)),
            [new("https://contexts.example.test/contexts/base.jsonld")] = new(Encoding.UTF8.GetBytes("""
                {"@context":{"@protected":true,"@vocab":"urn:example:","term":"urn:example:term"}}
                """))
        };
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), contexts);

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/nested");
        await Assert.That(references[0].Operator).IsEqualTo("td:ref");
    }

    [Test]
    public async Task OrdinaryDiscoveryRetainsBothAliasesForLaterTargetKindAndAmbiguityChecks()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":{
              "one":"https://www.w3.org/2019/wot/tm#ref",
              "two":"https://www.w3.org/2019/wot/tm#ref"
            },"one":"urn:example:first","two":"urn:example:second"}
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(2);
        await Assert.That(references[0].SourcePointer).IsEqualTo("");
        await Assert.That(references[1].SourcePointer).IsEqualTo("");
        await Assert.That(references[0].PropertyName).IsEqualTo("one");
        await Assert.That(references[1].PropertyName).IsEqualTo("two");
        await Assert.That(references.All(reference => reference.Operator == "tm:ref")).IsTrue();
    }

    [Test]
    [Arguments("null")]
    [Arguments("1")]
    [Arguments("false")]
    [Arguments("""{"@id":"urn:example:target"}""")]
    [Arguments("""["urn:example:target"]""")]
    public async Task OrdinaryReferenceMeaningRequiresAnAuthoredStringRatherThanAJsonLdValueShape(string value)
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes(
            """{"@context":{"ref":"https://www.w3.org/2019/wot/tm#ref"},"ref":""" + value + "}"));
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
                new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>()))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected invalid authored reference.");

        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task OrdinaryDiscoveryUsesCapturedRemoteContextsAndRejectsAnUnavailableContext()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":"https://contexts.example.test/wot.jsonld","reference":"urn:example:target"}
            """));
        var context = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":{"reference":"https://www.w3.org/2019/wot/td#ref"}}
            """));
        var contextBytes = await ReadBytesAsync(context);
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [new("https://contexts.example.test/wot.jsonld")] = context
        };
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), contexts);
        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].Operator).IsEqualTo("td:ref");
        await Assert.That((await ReadBytesAsync(context)).SequenceEqual(contextBytes)).IsTrue();

        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
                new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>()))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected uncaptured context rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task OrdinaryDiscoveryDoesNotTurnLiteralPrefixesOrContextDefinitionsIntoOccurrences()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":{"tm":"urn:not-tm:","example":"urn:example:","ignored":null},
             "tm:ref":"urn:not-a-dependency","ignored":null,"example:value":"ordinary"}
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());
        await Assert.That(references.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OrdinaryDiscoveryCancellationAndWorkExhaustionCannotReturnPartialOccurrences()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":{"tm":"https://www.w3.org/2019/wot/tm#"},"tm:ref":"urn:example:target"}
            """));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>(),
            cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();

        var budget = new FederationReadBudget(new(maxWork: 1));
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
                new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>(), budget))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected cumulative discovery limits.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AuthoredAliasesCannotImpersonateASyntheticMemberMeaning(bool capturedContext)
    {
        var marker = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("\nref"))).ToLowerInvariant();
        var predicate = "https://xregistry.invalid/.well-known/jsonld-probe/member/" + marker;
        var context = $$"""
            {"alias":{"@id":"{{predicate}}","@type":"@id"},"ref":null}
            """;
        var document = new FederationDocument(Encoding.UTF8.GetBytes(
            """{"@context":""" + (capturedContext ? "\"https://contexts.example.test/context.jsonld\"" : context) +
            ""","alias":"https://www.w3.org/2019/wot/tm#ref","ref":"urn:not-a-dependency"}"""));
        var contexts = new Dictionary<Uri, FederationDocument>();
        if (capturedContext)
        {
            contexts.Add(new("https://contexts.example.test/context.jsonld"),
                new(Encoding.UTF8.GetBytes("{\"@context\":" + context + "}")));
        }
        var meaning = await WotJsonLdBaseResolver.ExpandMemberIriAsync(document, "", "ref",
            new Uri("https://example.test/document.json"), contexts);

        await Assert.That(meaning).IsNull();
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), contexts);
        await Assert.That(references.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AuthoredAliasesCannotImpersonateASyntheticResolvedTarget()
    {
        const string reference = "target.json";
        var marker = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("\nref\n" + reference))).ToLowerInvariant();
        var predicate = "https://xregistry.invalid/.well-known/jsonld-probe/reference/" + marker;
        var document = new FederationDocument(Encoding.UTF8.GetBytes($$"""
            {"@context":{
              "@vocab":"urn:example:",
              "alias":{"@id":"{{predicate}}","@type":"@id"}
            },"alias":"https://incorrect.example.test/target","ref":"target.json"}
            """));
        var resolved = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document, "", "ref", reference,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(resolved.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/target.json");
    }

    [Test]
    public async Task MemberExpansionWorkLimitIsInclusiveAndAdjacentExhaustionRejects()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":{"ref":"https://www.w3.org/2019/wot/tm#ref"},"ref":"urn:example:target"}
            """));
        var contexts = new Dictionary<Uri, FederationDocument>();
        var measured = new FederationReadBudget();
        var meaning = await WotJsonLdBaseResolver.ExpandMemberIriAsync(document, "", "ref",
            new Uri("https://example.test/document.json"), contexts, measured);
        await Assert.That(meaning).IsEqualTo("https://www.w3.org/2019/wot/tm#ref");
        var atLimit = new FederationReadBudget(new(maxWork: measured.Work));
        var repeated = await WotJsonLdBaseResolver.ExpandMemberIriAsync(document, "", "ref",
            new Uri("https://example.test/document.json"), contexts, atLimit);
        await Assert.That(repeated).IsEqualTo(meaning);
        await Assert.That(atLimit.Work).IsEqualTo(measured.Work);

        var belowLimit = new FederationReadBudget(new(maxWork: measured.Work - 1));
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.ExpandMemberIriAsync(document, "", "ref",
                new Uri("https://example.test/document.json"), contexts, belowLimit))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected adjacent work exhaustion.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    [Test]
    [Arguments("target.json#/properties/a", "https://scoped.example.test/models/target.json", "#/properties/a")]
    [Arguments("", "https://scoped.example.test/models/current.json", "#inherited")]
    [Arguments("#", "https://scoped.example.test/models/current.json", "#")]
    public async Task OrdinaryReferenceUsesItsOwnPropertyScopedBaseAndPreservesEmptyReferenceFragment(
        string href, string expectedDocumentUri, string expectedFragment)
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":{"ref":{
              "@id":"https://www.w3.org/2019/wot/tm#ref",
              "@context":{"@base":"https://scoped.example.test/models/current.json#inherited"}
            }},"ref":
            """ + System.Text.Json.JsonSerializer.Serialize(href) + "}"));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].Href).IsEqualTo(href);
        await Assert.That(references[0].Target.DocumentUri.AbsoluteUri).IsEqualTo(expectedDocumentUri);
        await Assert.That(references[0].Target.Fragment).IsEqualTo(expectedFragment);
    }

    [Test]
    public async Task ScalarReferenceRetainsTheContainingNonPropagatingBaseDuringScopedExpansion()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context":{"@vocab":"urn:example:","@base":"https://parent.example.test/document.json"},
              "nested":{
                "@context":{"@propagate":false,"@base":"https://child.example.test/document.json",
                            "ref":"https://www.w3.org/2019/wot/tm#ref"},
                "ref":"target.json"
              }
            }
            """));
        var references = await WotJsonLdBaseResolver.DiscoverOrdinaryReferencesAsync(document,
            new Uri("https://example.test/document.json"), new Dictionary<Uri, FederationDocument>());

        await Assert.That(references.Count).IsEqualTo(1);
        await Assert.That(references[0].SourcePointer).IsEqualTo("/nested");
        await Assert.That(references[0].Target.DocumentUri.AbsoluteUri).IsEqualTo("https://child.example.test/target.json");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task MemberExpansionHonorsInputResultAndRemoteObjectBounds(int bound)
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":"https://contexts.example.test/one.jsonld","ref":"urn:example:target"}
            """));
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [new("https://contexts.example.test/one.jsonld")] = new(Encoding.UTF8.GetBytes("""
                {"@context":"https://contexts.example.test/two.jsonld"}
                """)),
            [new("https://contexts.example.test/two.jsonld")] = new(Encoding.UTF8.GetBytes("""
                {"@context":{"ref":"https://www.w3.org/2019/wot/tm#ref"}}
                """))
        };
        var limits = bound switch
        {
            0 => new FederationReadLimits(maxObjectBytes: (int)document.Length - 1),
            1 => new FederationReadLimits(maxResultBytes: 1),
            _ => new FederationReadLimits(maxObjects: 1)
        };
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.ExpandMemberIriAsync(document, "", "ref",
                new Uri("https://example.test/document.json"), contexts, new(limits)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected configured expansion bounds.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DuplicateCapturedContextTermsRejectInBaseAndMemberProcessing(bool expandMember)
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {"@context":"https://contexts.example.test/context.jsonld","ref":"urn:example:target"}
            """));
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [new("https://contexts.example.test/context.jsonld")] = new(Encoding.UTF8.GetBytes("""
                {"@context":{"ref":"https://www.w3.org/2019/wot/tm#ref","ref":"urn:other"}}
                """))
        };
        var failure = await Assert.That(async () =>
        {
            if (expandMember)
            {
                await WotJsonLdBaseResolver.ExpandMemberIriAsync(document, "", "ref",
                    new Uri("https://example.test/document.json"), contexts);
            }
            else
            {
                await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document, "", "ref", "urn:example:target",
                    new Uri("https://example.test/document.json"), contexts);
            }
        }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected duplicate-context rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    private static async ValueTask<byte[]> ReadBytesAsync(FederationDocument document)
    {
        await using var stream = document.OpenRead();
        using var result = new MemoryStream();
        await stream.CopyToAsync(result);
        return result.ToArray();
    }

    [Test]
    [Arguments("", "https://example.test/models/td.json", null, true)]
    [Arguments("#/properties/status", "https://example.test/models/td.json", "#/properties/status", true)]
    [Arguments("#", "https://example.test/models/td.json", "#", true)]
    [Arguments("./td.json#/events/overheated", "https://example.test/models/td.json", "#/events/overheated", true)]
    [Arguments("../models/td.json#/", "https://example.test/models/td.json", "#/", true)]
    [Arguments("other.json#/properties/status", "https://example.test/models/other.json", "#/properties/status", false)]
    [Arguments("td.json?revision=2", "https://example.test/models/td.json?revision=2", null, false)]
    [Arguments("https://EXAMPLE.test:443/models/td.json#/properties/status",
        "https://example.test/models/td.json", "#/properties/status", true)]
    public async Task ResolvesReferenceAgainstCapturedBaseAndClassifiesDocumentLocality(
        string reference, string expectedDocumentUri, string? expectedFragment, bool expectedLocal)
    {
        var result = WotReferenceUriResolver.Resolve(reference,
            new Uri("https://example.test/models/td.json"),
            new Uri("https://example.test/models/td.json#existing"));

        await Assert.That(result.DocumentUri.AbsoluteUri).IsEqualTo(expectedDocumentUri);
        await Assert.That(result.Fragment).IsEqualTo(expectedFragment);
        await Assert.That(result.IsDocumentLocal).IsEqualTo(expectedLocal);
    }

    [Test]
    public async Task RejectsMalformedReferenceUris()
    {
        var failure = await Assert.That(() => WotReferenceUriResolver.Resolve("bad%reference",
            new Uri("https://example.test/models/td.json"),
            new Uri("https://example.test/models/td.json")))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a malformed URI-reference rejection.");

        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RejectsCredentialsInResolvedTarget()
    {
        var failure = await Assert.That(() => WotReferenceUriResolver.Resolve(
            "https://user:secret@example.test/models/td.json",
            new Uri("https://example.test/models/td.json"),
            new Uri("https://example.test/models/td.json")))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected embedded credentials to be rejected.");

        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
    }

    [Test]
    public async Task ResolvesNestedInlineJsonLdBaseAtAuthoredReferencePointer()
    {
        const string reference = "model.json#/properties/status";
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context": {
                "@base": "https://example.test/root/document.json",
                "@vocab": "urn:example:"
              },
              "properties": {
                "status": {
                  "@context": { "@base": "https://example.test/nested/document.json" },
                  "tm:ref": "model.json#/properties/status"
                }
              }
            }
            """), documentBase: "https://example.test/registry/document.json");

        var result = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document,
            "/properties/status", "tm:ref", reference,
            new Uri("https://example.test/document-view.json"),
            new Dictionary<Uri, FederationDocument>());

        await Assert.That(result.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/nested/model.json");
        await Assert.That(result.Fragment).IsEqualTo("#/properties/status");
    }

    [Test]
    public async Task ResolvesJsonLdScopedBaseAtAuthoredReferencePointer()
    {
        const string reference = "model.json#/properties/status";
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context": {
                "@vocab": "urn:example:",
                "nested": {
                  "@id": "nested",
                  "@context": { "@base": "https://example.test/scoped/document.json" }
                }
              },
              "nested": {
                "ref": "model.json#/properties/status"
              }
            }
            """), documentBase: "https://example.test/root/document.json");

        var result = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document,
            "/nested", "ref", reference, new Uri("https://example.test/document-view.json"),
            new Dictionary<Uri, FederationDocument>());

        await Assert.That(result.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/scoped/model.json");
        await Assert.That(result.Fragment).IsEqualTo("#/properties/status");
    }

    [Test]
    public async Task ContextResetRestoresTheDocumentBase()
    {
        const string reference = "#/properties/status";
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context": { "@base": "https://example.test/inline/document.json" },
              "properties": {
                "status": {
                  "@context": null,
                  "ref": "#/properties/status"
                }
              }
            }
            """), documentBase: "https://example.test/registry/document.json");

        var result = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document,
            "/properties/status", "ref", reference, new Uri("https://example.test/document-view.json"),
            new Dictionary<Uri, FederationDocument>());

        await Assert.That(result.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/registry/document.json");
        await Assert.That(result.Fragment).IsEqualTo("#/properties/status");
    }

    [Test]
    public async Task ScopedContextResetRestoresTheDocumentBase()
    {
        const string reference = "model.json#/properties/status";
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context": {
                "@base": "https://example.test/inline/document.json",
                "nested": { "@id": "urn:example:nested", "@context": null }
              },
              "nested": {
                "ref": "model.json#/properties/status"
              }
            }
            """), documentBase: "https://example.test/registry/document.json");

        var result = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document,
            "/nested", "ref", reference, new Uri("https://example.test/document-view.json"),
            new Dictionary<Uri, FederationDocument>());

        await Assert.That(result.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/registry/model.json");
        await Assert.That(result.Fragment).IsEqualTo("#/properties/status");
    }

    [Test]
    public async Task PreservesBaseFragmentForAnEmptyReference()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""{"ref":""}"""),
            documentBase: "https://example.test/document.json#revision");

        var result = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document,
            "", "ref", "", new Uri("https://example.test/document-view.json"),
            new Dictionary<Uri, FederationDocument>());

        await Assert.That(result.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/document.json");
        await Assert.That(result.Fragment).IsEqualTo("#revision");
    }

    [Test]
    public async Task ResolvesRemoteContextOnlyFromCapturedContextSet()
    {
        const string contextUri = "https://contexts.example.test/wot.jsonld";
        const string reference = "#/properties/status";
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context": "https://contexts.example.test/wot.jsonld",
              "urn:example:ref": "#/properties/status"
            }
            """));
        var contexts = new Dictionary<Uri, FederationDocument>
        {
            [new(contextUri)] = new(Encoding.UTF8.GetBytes("""{"@context":{"@vocab":"urn:example:"}}"""))
        };
        var budget = new FederationReadBudget();

        var result = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document,
            "", "urn:example:ref", reference, new Uri("https://example.test/document-view.json"),
            contexts, budget);

        await Assert.That(result.DocumentUri.AbsoluteUri).IsEqualTo("https://example.test/document-view.json");
        await Assert.That(result.Fragment).IsEqualTo("#/properties/status");
        await Assert.That(budget.ObjectsRead).IsEqualTo(1);
        await Assert.That(budget.BytesRead).IsEqualTo(contexts[new(contextUri)].Length);
    }

    [Test]
    public async Task RejectsRemoteContextAbsentFromCapturedContextSet()
    {
        var document = new FederationDocument(Encoding.UTF8.GetBytes("""
            {
              "@context": "https://contexts.example.test/not-captured.jsonld",
              "ref": "#/properties/status"
            }
            """));
        var failure = await Assert.That(async () =>
            await WotJsonLdBaseResolver.ResolveReferenceUriAsync(document,
                "", "ref", "#/properties/status", new Uri("https://example.test/document.json"),
                new Dictionary<Uri, FederationDocument>()))
            .Throws<FederationException>() ??
            throw new InvalidOperationException("Expected a missing captured-context rejection.");

        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }
}
