// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryQueryTests
{
    internal const string QueryModel = """
        {"groups":{"fleets":{"singular":"fleet","attributes":{
          "rank":{"type":"decimal"},"active":{"type":"boolean"},"at":{"type":"timestamp"},
          "info":{"type":"object","attributes":{"owner":{"type":"string"},"reviewers":{"type":"array","item":{"type":"string"}},
            "addresses":{"type":"map","item":{"type":"object","attributes":{"state":{"type":"string"}}}}}}
        },"resources":{"items":{"singular":"item","hasdocument":false,"attributes":{
          "rank":{"type":"decimal"},"active":{"type":"boolean"}}},"docs":{"singular":"doc"}}}}}
        """;

    [Test]
    public async Task ExistingDescribeCallsKeepTheirDefaultCancellationTokenBinding()
    {
        var engine = await Seed();
        var route = await engine.DescribeAsync(RegistryAction.Read, RegistryPath.Parse("/"), Writer(), default);
        await Assert.That(route.Path.Kind).IsEqualTo(RegistryPathKind.Registry);
        await Assert.That(route.AllowedActions.Contains(RegistryAction.Read)).IsTrue();
    }

    [Test]
    public async Task FilterCommasAreAndAndRepeatedParametersAreOrWithoutNumericStringComparison()
    {
        var engine = await Seed();
        var result = await Send(engine, RegistryAction.Read, "/fleets", null,
            new("filter", "active=true,rank<20"), new("filter", "name=ALPHA,rank=2"));
        await Assert.That(Ids(result.Metadata!.RootElement)).IsEquivalentTo(["a", "b"], StringComparer.Ordinal);
        await Assert.That(result.Metadata.RootElement.GetProperty("b").GetProperty("rank").GetInt32()).IsEqualTo(10);
    }

    [Test]
    [Arguments("rank>9007199254740992", "c")]
    [Arguments("rank=2e0", "a")]
    [Arguments("rank<=2", "a")]
    [Arguments("rank<2", "")]
    [Arguments("rank!=2", "b,c,d")]
    [Arguments("rank<>2", "b,c,d")]
    [Arguments("rank=null", "d")]
    [Arguments("rank", "a,b,c")]
    [Arguments("rank<>null", "a,b,c")]
    [Arguments("description=", "d")]
    [Arguments("description=null", "a,b,c")]
    [Arguments("name=\"Alpha\"", "")]
    [Arguments("name=*LPH*", "a,c")]
    [Arguments("active>false", "b,c")]
    [Arguments("active!=false", "b,c,d")]
    [Arguments("unknown=value", "")]
    [Arguments("unknown=null", "a,b,c,d")]
    public async Task FilterLiteralMissingNullAndPrecisionCasesHaveIndependentExpectedIds(string filter, string expected)
    {
        var engine = await Seed();
        var result = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("filter", filter));
        await Assert.That(string.Join(',', Ids(result.Metadata!.RootElement))).IsEqualTo(expected);
    }

    [Test]
    public async Task NestedAndIntersectsMatchingLeavesRatherThanDifferentSiblingWitnesses()
    {
        var engine = await Seed();
        var result = await Send(engine, RegistryAction.Read, "/fleets", null,
            new("filter", "items.name=match,items.rank=10"), new("inline", "*"));
        await Assert.That(Ids(result.Metadata!.RootElement)).IsEquivalentTo(["b"], StringComparer.Ordinal);
        await Assert.That(Ids(result.Metadata.RootElement.GetProperty("b").GetProperty("items"))).IsEquivalentTo(["z"], StringComparer.Ordinal);
        await Assert.That(result.Metadata.RootElement.GetProperty("b").GetProperty("itemscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task OrMatchingParentIncludesItsChildrenWhileAndRestrictsTheChildCollection()
    {
        var engine = await Seed();
        var union = await Send(engine, RegistryAction.Read, "/", null,
            new("filter", "fleets.fleetid=a"), new("filter", "fleets.items.itemid=z"), new("inline", "*"));
        var fleets = union.Metadata!.RootElement.GetProperty("fleets");
        await Assert.That(Ids(fleets)).IsEquivalentTo(["a", "b"], StringComparer.Ordinal);
        await Assert.That(Ids(fleets.GetProperty("a").GetProperty("items"))).IsEquivalentTo(["x", "y"], StringComparer.Ordinal);
        await Assert.That(Ids(fleets.GetProperty("b").GetProperty("items"))).IsEquivalentTo(["z"], StringComparer.Ordinal);
        var intersection = await Send(engine, RegistryAction.Read, "/", null,
            new("filter", "fleets.fleetid=a,fleets.items.itemid=x"), new("inline", "*"));
        var selected = intersection.Metadata!.RootElement.GetProperty("fleets");
        await Assert.That(Ids(selected)).IsEquivalentTo(["a"], StringComparer.Ordinal);
        await Assert.That(Ids(selected.GetProperty("a").GetProperty("items"))).IsEquivalentTo(["x"], StringComparer.Ordinal);
        await Assert.That(intersection.Metadata.RootElement.GetProperty("fleetscount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task FiltersDoNotInlineAndEmptyCollectionLinksUseExcludeAll()
    {
        var engine = await Seed();
        var result = await Send(engine, RegistryAction.Read, "/", null,
            new KeyValuePair<string, string?>("filter", "fleets.name=missing"));
        await Assert.That(result.Metadata!.RootElement.TryGetProperty("fleets", out _)).IsFalse();
        await Assert.That(result.Metadata.RootElement.GetProperty("fleetscount").GetInt32()).IsEqualTo(0);
        await Assert.That(result.Metadata.RootElement.GetProperty("fleetsurl").GetString())
            .IsEqualTo("https://registry.example/catalog/fleets?filter=excludeall");
        await Assert.That(Ids((await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("filter", "excludeall"))).Metadata!.RootElement).Length).IsEqualTo(0);
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets/a", null,
            new KeyValuePair<string, string?>("filter", "excludeall")), "not_found");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/", null,
            new KeyValuePair<string, string?>("filter", "name=missing")), "not_found");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets", null,
            new("filter", "excludeall"), new("filter", "name=Alpha")), "bad_filter");
    }

    [Test]
    [Arguments("info.owner=JOE")]
    [Arguments("info.reviewers[*]=steve")]
    [Arguments("info.reviewers[1]=Steve")]
    [Arguments("info.addresses['home.office'].state=ca")]
    [Arguments("info.addresses[\"home.office\"].state=CA")]
    [Arguments("info.addresses.*.state=ca")]
    public async Task DotPathsTraverseObjectsMapsAndArraysWithRegistryIdsElided(string filter)
    {
        var engine = await Seed();
        await Send(engine, RegistryAction.Patch, "/fleets/a", """
            {"info":{"owner":"Joe","reviewers":["Mary","STEVE"],"addresses":{"home.office":{"state":"CA"}}}}
            """);
        var result = await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("filter", filter));
        await Assert.That(Ids(result.Metadata!.RootElement)).IsEquivalentTo(["a"], StringComparer.Ordinal);
    }

    [Test]
    public async Task EscapedStarsAreLiteralsAndTimestampOffsetsNormalizeBeforeComparison()
    {
        var engine = await Seed();
        await Send(engine, RegistryAction.Patch, "/fleets/a", """{"name":"a*b","at":"2025-01-01T02:00:00.0000+02:00"}""");
        await Send(engine, RegistryAction.Patch, "/fleets/b", """{"name":"axxb","at":"2024-12-31T23:59:59Z"}""");
        var literal = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("filter", "name=a\\*b"));
        await Assert.That(Ids(literal.Metadata!.RootElement)).IsEquivalentTo(["a"], StringComparer.Ordinal);
        var timestamp = await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("filter", "at=2024-12-31T19:00:00-05:00"));
        await Assert.That(Ids(timestamp.Metadata!.RootElement)).IsEquivalentTo(["a"], StringComparer.Ordinal);
    }

    [Test]
    public async Task FractionalAndNegativeExponentComparisonsNeverUseFloatingPoint()
    {
        var engine = await Seed();
        await Send(engine, RegistryAction.Patch, "/fleets/a", """{"rank":0.1000000000000000000001}""");
        await Send(engine, RegistryAction.Patch, "/fleets/b", """{"rank":0.1}""");
        var greater = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("filter", "rank>0.1"));
        await Assert.That(Ids(greater.Metadata!.RootElement)).IsEquivalentTo(["a", "c"], StringComparer.Ordinal);
        var equal = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("filter", "rank=1e-1"));
        await Assert.That(Ids(equal.Metadata!.RootElement)).IsEquivalentTo(["b"], StringComparer.Ordinal);
        await Send(engine, RegistryAction.Patch, "/fleets/b", """{"rank":-1e1000}""");
        var negative = await Send(engine, RegistryAction.Read, "/fleets", null, new KeyValuePair<string, string?>("filter", "rank<-2"));
        await Assert.That(Ids(negative.Metadata!.RootElement)).IsEquivalentTo(["b"], StringComparer.Ordinal);
    }
    [Test]
    [Arguments("rank=null<5")]
    [Arguments("active=TRUE")]
    [Arguments("info=non-scalar")]
    [Arguments("name>*a")]
    [Arguments("rank>=null")]
    [Arguments("info.reviewers[-1]=x")]
    [Arguments("name!x")]
    public async Task InvalidFilterLiteralsAndUnsupportedPathOperatorsRejectExplicitly(string filter)
    {
        var engine = await Seed();
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("filter", filter)), "bad_filter");
    }

    [Test]
    [Arguments("rank=desc", "c,b,a,d")]
    [Arguments("name", "d,a,c,b")]
    [Arguments("name=desc", "b,c,a,d")]
    [Arguments("active", "d,a,b,c")]
    public async Task SortingUsesScalarValuesMissingLowAndSameDirectionIdTies(string sort, string expected)
    {
        var result = await Send(await Seed(), RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("sort", sort));
        await Assert.That(string.Join(',', Ids(result.Metadata!.RootElement))).IsEqualTo(expected);
    }

    [Test]
    [Arguments("items.rank")]
    [Arguments("info")]
    [Arguments("unknown")]
    [Arguments("rank=sideways")]
    [Arguments("labels.*")]
    public async Task SortRejectsUnknownComplexWildcardAndNestedCollectionProjections(string sort)
    {
        var engine = await Seed();
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("sort", sort)), "bad_sort");
    }

    [Test]
    public async Task ConditionalTimestampDefinitionsUseTheirActiveModelType()
    {
        var engine = Create("""
            {"groups":{"entries":{"singular":"entry","attributes":{"kind":{"type":"string","ifvalues":{
              "clock":{"siblingattributes":{"when":{"type":"timestamp"}}},
              "text":{"siblingattributes":{"when":{"type":"string"}}}
            }}}}}}
            """);
        await Send(engine, RegistryAction.Post, "/entries", """
            {"a":{"kind":"clock","when":"2025-01-01T02:00:00+02:00"},
             "b":{"kind":"text","when":"2025-01-01T00:00:00Z"}}
            """);
        var result = await Send(engine, RegistryAction.Read, "/entries", null,
            new KeyValuePair<string, string?>("filter", "when=2024-12-31T19:00:00-05:00"));
        await Assert.That(Ids(result.Metadata!.RootElement)).IsEquivalentTo(["a"], StringComparer.Ordinal);
    }

    [Test]
    public async Task MetadataAndDocumentQueriesUseLogicalValuesBeforeProjection()
    {
        var engine = await Seed();
        await Send(engine, RegistryAction.Replace, "/fleets/a/docs/d$details", """{"contenttype":"text/plain","doc":"hello"}""");
        var document = await Send(engine, RegistryAction.Read, "/fleets/a/docs", null,
            new("filter", "doc=HELLO"), new("inline", "*"), new("doc", null), new("binary", null));
        await Assert.That(Ids(document.Metadata!.RootElement)).IsEquivalentTo(["d"], StringComparer.Ordinal);
        var projected = document.Metadata.RootElement.GetProperty("d");
        await Assert.That(projected.TryGetProperty("doc", out _)).IsFalse();
        await Assert.That(projected.GetProperty("versions").GetProperty("1").GetProperty("docbase64").GetString()).IsEqualTo("aGVsbG8=");
        var metadata = await Send(engine, RegistryAction.Read, "/fleets/a/items", null,
            new("filter", "meta.defaultversionid=1"), new("sort", "meta.defaultversionid"));
        await Assert.That(Ids(metadata.Metadata!.RootElement)).IsEquivalentTo(["x", "y"], StringComparer.Ordinal);
    }

    [Test]
    public async Task DocumentViewDoesNotCreateRelativePointersToFilteredOutDefaultVersions()
    {
        var engine = await Seed();
        await Send(engine, RegistryAction.Post, "/fleets/a/items/x", """{"versionid":"2","name":"new default"}""");
        var result = await Send(engine, RegistryAction.Read, "/fleets/a/items/x", null,
            new("filter", "versions.versionid=1"), new("inline", "meta,versions"), new("doc", null));
        await Assert.That(Ids(result.Metadata!.RootElement.GetProperty("versions"))).IsEquivalentTo(["1"], StringComparer.Ordinal);
        await Assert.That(result.Metadata.RootElement.GetProperty("meta").GetProperty("defaultversionid").GetString()).IsEqualTo("2");
        await Assert.That(result.Metadata.RootElement.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("https://registry.example/catalog/fleets/a/items/x/versions/2");
    }

    [Test]
    public async Task XrefFiltersUseLogicalVersionsButDocumentViewStillHidesTheTargetProjection()
    {
        var engine = await Seed();
        await Send(engine, RegistryAction.Replace, "/fleets/target/items/original", """{"name":"visible"}""");
        await Send(engine, RegistryAction.Replace, "/fleets/source/items/alias",
            """{"meta":{"xref":"/fleets/target/items/original"}}""");
        var result = await Send(engine, RegistryAction.Read, "/fleets/source/items/alias", null,
            new("filter", "versions.name=visible"), new("doc", null), new("inline", "meta"));
        await Assert.That(result.Metadata!.RootElement.GetProperty("meta").GetProperty("xref").GetString())
            .IsEqualTo("/fleets/target/items/original");
        await Assert.That(result.Metadata.RootElement.TryGetProperty("name", out _)).IsFalse();
        await Assert.That(result.Metadata.RootElement.TryGetProperty("versions", out _)).IsFalse();
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/fleets/source/items/alias/versions", null,
            new("filter", "name=visible"), new("doc", null)), "cannot_doc_xref");
    }
    [Test]
    public async Task NullCharacterInAStringLiteralIsNotAnImplicitWildcard()
    {
        var engine = await Seed();
        await Send(engine, RegistryAction.Patch, "/fleets/a", """{"name":"\u0000"}""");
        var result = await Send(engine, RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("filter", "name=\0"));
        await Assert.That(Ids(result.Metadata!.RootElement)).IsEquivalentTo(["a"], StringComparer.Ordinal);
    }

    [Test]
    public async Task WriteResponseFiltersDoNotRestrictTheEntitiesBeingMutated()
    {
        var engine = await Seed();
        var result = await Send(engine, RegistryAction.Patch, "/fleets", """
            {"a":{"rank":50},"b":{"rank":60}}
            """, new KeyValuePair<string, string?>("filter", "fleetid=a"));
        await Assert.That(Ids(result.Metadata!.RootElement)).IsEquivalentTo(["a"], StringComparer.Ordinal);
        await Assert.That((await Send(engine, RegistryAction.Read, "/fleets/b")).Metadata!.RootElement.GetProperty("rank").GetInt32()).IsEqualTo(60);
    }

    [Test]
    public async Task CapabilitiesAdvertiseOnlyTheImplementedQueryAndExistingProfiles()
    {
        var engine = await Seed();
        var capabilities = (await Send(engine, RegistryAction.Read, "/capabilities")).Metadata!.RootElement;
        var flags = capabilities.GetProperty("flags").EnumerateArray().Select(static value => value.GetString()).ToArray();
        await Assert.That(flags.Contains("filter", StringComparer.Ordinal)).IsTrue();
        await Assert.That(flags.Contains("sort", StringComparer.Ordinal)).IsTrue();
        await Assert.That(capabilities.GetProperty("pagination").GetBoolean()).IsTrue();
        await Assert.That(capabilities.GetProperty("shortself").GetBoolean()).IsFalse();
        await Assert.That(capabilities.GetProperty("available").GetProperty("capabilities").GetProperty("mutable").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task UnknownFlagsRemainIgnoredRatherThanProducingAnEmptyQuerySuccess()
    {
        var result = await Send(await Seed(), RegistryAction.Read, "/fleets", null,
            new KeyValuePair<string, string?>("future-query-extension", "not-a-supported-filter"));
        await Assert.That(Ids(result.Metadata!.RootElement)).IsEquivalentTo(["a", "b", "c", "d"], StringComparer.Ordinal);
        await Assert.That(result.Page!.TotalCount).IsEqualTo(4UL);
    }
    internal static async Task<RegistryEngine> Seed()
    {
        var engine = Create(QueryModel);
        await Send(engine, RegistryAction.Replace, "/", """
            {"name":"registry","fleets":{
              "a":{"name":"Alpha","rank":2,"active":false,"labels":{"stage":"prod"},"items":{
                "x":{"name":"match","rank":2},"y":{"name":"other","rank":10}}},
              "b":{"name":"Beta","rank":10,"active":true,"items":{"z":{"name":"match","rank":10}}},
              "c":{"name":"ALPHA","rank":9007199254740993,"active":true},
              "d":{"description":""}
            }}
            """);
        return engine;
    }

    internal static string[] Ids(JsonElement value) => value.EnumerateObject().Select(static property => property.Name).ToArray();
}
