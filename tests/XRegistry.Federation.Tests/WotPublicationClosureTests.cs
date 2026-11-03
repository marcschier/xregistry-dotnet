// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class WotPublicationClosureTests
{
    private const string Source = "/groups/g/thingmodels/source/versions/1";
    private const string Target = "/groups/g/thingmodels/target/versions/2";

    [Test]
    [Arguments("")]
    [Arguments("../target.json#/properties/a%2Fb")]
    [Arguments("literal\nhref")]
    public async Task ClosureRepresentationRetainsExactAuthoredStringsAndReadOnlyOrder(string href)
    {
        var root = Example();
        root["entries"]!.AsArray().Add(Target);
        root["bindings"]![0]!["href"] = href;
        root["bindings"]!.AsArray().Add(new JsonObject
        {
            ["source"] = Target, ["pointer"] = "", ["operator"] = "td:ref", ["href"] = "#",
            ["target"] = Source
        });
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        var closure = WotPublicationClosure.Parse(bytes);
        bytes.AsSpan().Clear();

        await Assert.That(WotPublicationClosure.ExtensionUri)
            .IsEqualTo("http://opcfoundation.org/UA/WoT-Binding/publication-closure/1");
        await Assert.That(closure.Entries.SequenceEqual(new[] { Source, Target })).IsTrue();
        await Assert.That(closure.Bindings.Count).IsEqualTo(2);
        await Assert.That(closure.Bindings[0]).IsEqualTo(new WotPublicationClosureBinding(
            Source, "/links/0", "tm:extends", href, Target));
        await Assert.That(closure.Bindings[1].Source).IsEqualTo(Target);
        await Assert.That(closure.Bindings[1].SourcePointer).IsEqualTo("");
        await Assert.That(() => ((IList<string>)closure.Entries)[0] = Target).Throws<NotSupportedException>();
        await Assert.That(() => ((IList<WotPublicationClosureBinding>)closure.Bindings).Clear())
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task ClosureRepresentationAllowsNoBindingsWithoutAssertingDependencyCompleteness()
    {
        var root = Example();
        root["bindings"] = new JsonArray();
        var closure = Parse(root);
        await Assert.That(closure.Entries.Count).IsEqualTo(1);
        await Assert.That(closure.Bindings.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("format")]
    [Arguments("entries")]
    [Arguments("bindings")]
    public async Task ClosureRepresentationRequiresEveryRootField(string field)
    {
        var root = Example();
        root.Remove(field);
        await Invalid(root);
    }

    [Test]
    [Arguments("source")]
    [Arguments("pointer")]
    [Arguments("operator")]
    [Arguments("href")]
    [Arguments("target")]
    public async Task ClosureRepresentationRequiresEveryBindingString(string field)
    {
        var missing = Example();
        missing["bindings"]![0]!.AsObject().Remove(field);
        await Invalid(missing);
        var nonstring = Example();
        nonstring["bindings"]![0]![field] = null;
        await Invalid(nonstring);
    }

    [Test]
    [Arguments("""{"format":"wot-publication-closure/2","entries":["/groups/g/thingmodels/source/versions/1"],"bindings":[]}""")]
    [Arguments("""{"format":1,"entries":["/groups/g/thingmodels/source/versions/1"],"bindings":[]}""")]
    [Arguments("""{"format":"wot-publication-closure/1","entries":[],"bindings":[]}""")]
    [Arguments("""{"format":"wot-publication-closure/1","entries":{},"bindings":[]}""")]
    [Arguments("""{"format":"wot-publication-closure/1","entries":[1],"bindings":[]}""")]
    [Arguments("""{"format":"wot-publication-closure/1","entries":["/groups/g/thingmodels/source/versions/1"],"bindings":{}}""")]
    [Arguments("""{"format":"wot-publication-closure/1","entries":["/groups/g/thingmodels/source/versions/1"],"bindings":[null]}""")]
    [Arguments("""{"format":"wot-publication-closure/1","format":"wot-publication-closure/1","entries":["/groups/g/thingmodels/source/versions/1"],"bindings":[]}""")]
    [Arguments("[]")]
    [Arguments("null")]
    public async Task ClosureRepresentationRejectsMalformedRootAndCollectionShapes(string json)
    {
        var failure = await Assert.That(() => WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(json)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected closure format rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClosureRepresentationRejectsUnknownRootAndBindingMembers(bool binding)
    {
        var root = Example();
        (binding ? root["bindings"]![0]! : root)["unexpected"] = true;
        await Invalid(root);
    }

    [Test]
    [Arguments("/groups/g/thingmodels/source")]
    [Arguments("/groups/g/thingmodels/source/meta")]
    [Arguments("/groups/g/thingmodels/source/versions")]
    [Arguments("/groups/g/thingmodels/source/versions/null")]
    [Arguments("/groups/g/thingmodels/source/versions/request")]
    [Arguments("/groups/g/thingmodels/source/%76ersions/1")]
    [Arguments("/groups/g/thingmodels/source/versions/1?query")]
    [Arguments("/groups/g/thingmodels/source/versions/1#fragment")]
    [Arguments("https://example.test/groups/g/thingmodels/source/versions/1")]
    [Arguments("urn:example:source")]
    public async Task ClosureRepresentationRequiresExactVersionXidsEverywhere(string xid)
    {
        var entry = Example();
        entry["entries"]![0] = xid;
        await Invalid(entry);
        foreach (var field in new[] { "source", "target" })
        {
            var root = Example();
            root["bindings"]![0]![field] = xid;
            await Invalid(root);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("/")]
    [Arguments("/properties/a~1b~0c")]
    [Arguments("/properties/a%2Fb")]
    [Arguments("/properties/\u00e4")]
    [Arguments("/~01")]
    [Arguments("/links/00")]
    public async Task ClosureRepresentationPreservesPointerSyntaxWithoutGuessingSourceTopology(string sourcePointer)
    {
        var root = Example();
        root["bindings"]![0]!["pointer"] = sourcePointer;
        await Assert.That(Parse(root).Bindings[0].SourcePointer).IsEqualTo(sourcePointer);
    }

    [Test]
    [Arguments("links/0")]
    [Arguments("#/links/0")]
    [Arguments("/~")]
    [Arguments("/a~2b")]
    public async Task ClosureRepresentationRejectsMalformedOrFragmentPointers(string sourcePointer)
    {
        var root = Example();
        root["bindings"]![0]!["pointer"] = sourcePointer;
        await Invalid(root);
    }

    [Test]
    [Arguments("tm:extends")]
    [Arguments("tm:submodel")]
    [Arguments("tm:ref")]
    [Arguments("td:extends")]
    [Arguments("td:subthing")]
    [Arguments("td:ref")]
    [Arguments("type")]
    [Arguments(WotJsonLdBaseResolver.ProjectionVocabulary + "projects")]
    public async Task ClosureRepresentationAcceptsExactlyTheSelectedCanonicalOperators(string operation)
    {
        var root = Example();
        root["bindings"]![0]!["operator"] = operation;
        await Assert.That(Parse(root).Bindings[0].Operator).IsEqualTo(operation);
    }

    [Test]
    [Arguments("TM:ref")]
    [Arguments("model:ref")]
    [Arguments("describedby")]
    [Arguments("https://www.w3.org/2019/wot/tm#ref")]
    [Arguments("wotproj:projects")]
    public async Task ClosureRepresentationRejectsOperatorAliasesAndUnknownStrings(string operation)
    {
        var root = Example();
        root["bindings"]![0]!["operator"] = operation;
        await Invalid(root);
    }

    [Test]
    [Arguments(Source)]
    [Arguments("/groups/%67/thingmodels/source/versions/1")]
    public async Task ClosureRepresentationRejectsDuplicateKeysUnderCoreSourceIdentity(string equivalent)
    {
        var entry = Example();
        entry["entries"]!.AsArray().Add(equivalent);
        await Invalid(entry);
        var binding = Example();
        var duplicate = binding["bindings"]![0]!.DeepClone();
        duplicate["source"] = equivalent;
        binding["bindings"]!.AsArray().Add(duplicate);
        await Invalid(binding);
        duplicate["target"] = Source;
        await Invalid(binding);
    }

    [Test]
    public async Task ClosureBindingPointerComparisonIsCaseSensitive()
    {
        var root = Example();
        var other = root["bindings"]![0]!.DeepClone();
        other["pointer"] = "/Links/0";
        root["bindings"]!.AsArray().Add(other);
        await Assert.That(Parse(root).Bindings.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ClosureRepresentationUsesSharedInclusiveWorkAndPointerDepthLimitsAndCancellation()
    {
        var bytes = Encoding.UTF8.GetBytes(Example().ToJsonString());
        var measured = new FederationReadBudget();
        _ = WotPublicationClosure.Parse(bytes, measured);
        var atLimit = new FederationReadBudget(new(maxWork: measured.Work));
        await Assert.That(WotPublicationClosure.Parse(bytes, atLimit).Bindings.Count).IsEqualTo(1);
        await Assert.That(atLimit.Work).IsEqualTo(measured.Work);
        foreach (var limits in new[]
                 {
                     new FederationReadLimits(maxWork: measured.Work - 1),
                     new FederationReadLimits(maxObjectBytes: bytes.Length - 1),
                     new FederationReadLimits(maxDepth: 1)
                 })
        {
            var failure = await Assert.That(() => WotPublicationClosure.Parse(bytes, new(limits)))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected closure budget rejection.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => WotPublicationClosure.Parse(bytes, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    private static JsonObject Example() => new()
    {
        ["format"] = "wot-publication-closure/1",
        ["entries"] = new JsonArray(Source),
        ["bindings"] = new JsonArray(new JsonObject
        {
            ["source"] = Source, ["pointer"] = "/links/0", ["operator"] = "tm:extends", ["href"] = "",
            ["target"] = Target
        })
    };

    private static WotPublicationClosure Parse(JsonObject root) =>
        WotPublicationClosure.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));

    private static async Task Invalid(JsonObject root)
    {
        var failure = await Assert.That(() => Parse(root)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected closure representation rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }
}
