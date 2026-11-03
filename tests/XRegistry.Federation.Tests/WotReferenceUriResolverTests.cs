// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class WotReferenceUriResolverTests
{
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
