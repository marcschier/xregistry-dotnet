// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Queries;
using static XRegistry.Core.Tests.QueryFixture;

namespace XRegistry.Core.Tests;

public class SharedQuerySemanticsTests
{
    [Test]
    [Arguments("rank>9007199254740992", "c")]
    [Arguments("rank=2e0", "a")]
    [Arguments("rank<2", "")]
    [Arguments("rank<=2", "a")]
    [Arguments("rank!=2", "b,c,d,e")]
    [Arguments("rank<>2", "b,c,d,e")]
    [Arguments("rank=null", "d,e")]
    [Arguments("rank", "a,b,c")]
    [Arguments("rank<>null", "a,b,c")]
    [Arguments("description=", "d")]
    [Arguments("description=null", "a,b,c,e")]
    [Arguments("name=\"Alpha\"", "")]
    [Arguments("name=*LPH*", "a,c")]
    [Arguments("active>false", "b,c")]
    [Arguments("active!=false", "b,c,d,e")]
    [Arguments("unknown=value", "")]
    [Arguments("unknown=null", "a,b,c,d,e")]
    public async Task LiteralComparisonUsesExactNumbersAndDistinctMissingNullAndEmptyValues(string filter, string expected)
    {
        using var budget = new RegistryQueryBudget();
        var result = await Evaluate(Groups(), budget, filters: [filter], defaultSort: true);
        await Assert.That(Ids(result)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("info.owner=JOE")]
    [Arguments("info.reviewers[*]=steve")]
    [Arguments("info.reviewers[1]=Steve")]
    [Arguments("info.addresses['home.office'].state=ca")]
    [Arguments("info.addresses[\"home.office\"].state=CA")]
    [Arguments("info.addresses.*.state=ca")]
    [Arguments("info.addresses['*'].state=ny")]
    public async Task NormativeDotPathsElideRegistryIdsButKeepMapKeysAndArrayIndexes(string filter)
    {
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(Groups(), budget, filters: [filter]))).IsEqualTo("a");
    }

    [Test]
    public async Task AndRequiresOneMatchingSubtreeWhileRepeatedFiltersUnionParentsAndLeaves()
    {
        var source = Tree();
        using var budget = new RegistryQueryBudget();
        var intersection = await Evaluate(source, budget, filters: ["items.name=match,items.rank=10"]);
        await Assert.That(Ids(intersection)).IsEqualTo("b");
        await Assert.That(intersection.Includes(RegistryPath.Parse("/fleets/b/items/z/versions/1"))).IsTrue();
        await Assert.That(intersection.Includes(RegistryPath.Parse("/fleets/a/items/y"))).IsFalse();

        var union = await Evaluate(source, budget, filters: ["fleetid=a", "items.itemid=z"]);
        await Assert.That(Ids(union)).IsEqualTo("a,b");
        await Assert.That(union.Includes(RegistryPath.Parse("/fleets/a/items/x/versions/1"))).IsTrue();
        await Assert.That(union.Includes(RegistryPath.Parse("/fleets/a/items/y/versions/1"))).IsTrue();
        await Assert.That(union.CollectionUrl(RegistryPath.Parse("/fleets/b/items"), 1))
            .IsEqualTo("https://public.example/catalog/fleets/b/items?filter=itemid%3Dz");
    }

    [Test]
    public async Task NestedFailureRetainsTheTargetButDirectFailureAndExcludeAllRejectEntities()
    {
        using var budget = new RegistryQueryBudget();
        var source = Tree();
        var empty = await Evaluate(source, budget, "/", ["fleets.items.name=missing"]);
        await Assert.That(Ids(empty)).IsEqualTo("/");
        await Assert.That(empty.Includes(RegistryPath.Parse("/"))).IsTrue();
        await Assert.That(empty.Includes(RegistryPath.Parse("/fleets/a"))).IsFalse();
        await Assert.That(empty.CollectionUrl(RegistryPath.Parse("/fleets"), 0))
            .IsEqualTo("https://public.example/catalog/fleets?filter=excludeall");
        await ExpectCode(() => Evaluate(source, budget, "/", ["name=missing"]), "not_found");
        await ExpectCode(() => Evaluate(source, budget, "/fleets/a", ["excludeall"]), "not_found");
        await Assert.That(Ids(await Evaluate(source, budget, filters: ["excludeall"]))).IsEqualTo("");
        await ExpectCode(() => Evaluate(source, budget, filters: ["excludeall", "name=Alpha"]), "bad_filter");
    }

    [Test]
    [Arguments("rank", "d,e,a,b,c")]
    [Arguments("rank=desc", "c,b,a,e,d")]
    [Arguments("name", "d,a,c,b,e")]
    [Arguments("name=desc", "e,b,c,a,d")]
    [Arguments("active", "d,e,a,b,c")]
    public async Task ModelKnownScalarSortUsesMissingLowAndSameDirectionIdTies(string sort, string expected)
    {
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(Groups(), budget, sort: sort))).IsEqualTo(expected);
    }

    [Test]
    public async Task EscapedStarsFractionsLargeNegativeExponentsAndConditionalTimestampsRemainExact()
    {
        var source = Groups();
        source.Add("/fleets/a", """
            {"fleetid":"a","name":"a*b","rank":0.1000000000000000000001,"kind":"clock","when":"2025-01-01T00:00:00Z"}
            """);
        source.Add("/fleets/b", """
            {"fleetid":"b","name":"axxb","rank":0.1,"kind":"text","when":"2025-01-01T00:00:00Z"}
            """);
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(source, budget, filters: ["name=a\\*b"]))).IsEqualTo("a");
        await Assert.That(Ids(await Evaluate(source, budget, filters: ["rank>0.1"]))).IsEqualTo("a,c");
        await Assert.That(Ids(await Evaluate(source, budget, filters: ["rank=1e-1"]))).IsEqualTo("b");
        await Assert.That(Ids(await Evaluate(source, budget, filters: ["when=2024-12-31T19:00:00-05:00"]))).IsEqualTo("a");
        source.Add("/fleets/b", """{"fleetid":"b","rank":-1e1000}""");
        await Assert.That(Ids(await Evaluate(source, budget, filters: ["rank<-2"]))).IsEqualTo("b");
    }

    [Test]
    [Arguments("rank>=null", "bad_filter")]
    [Arguments("active=TRUE", "bad_filter")]
    [Arguments("info=non-scalar", "bad_filter")]
    [Arguments("name>*a", "bad_filter")]
    [Arguments("info.reviewers[-1]=x", "bad_filter")]
    [Arguments("name!x", "bad_filter")]
    [Arguments("*.name=x", "bad_filter")]
    [Arguments("info..owner=x", "bad_filter")]
    [Arguments("info.reviewers[0:2]=x", "bad_filter")]
    [Arguments("info.addresses[?(@.state)]=x", "bad_filter")]
    public async Task NonCorePathSyntaxAndInvalidLiteralsHaveExplicitDiagnostics(string filter, string expected)
    {
        using var budget = new RegistryQueryBudget();
        await ExpectCode(() => Evaluate(Groups(), budget, filters: [filter]), expected);
    }

    [Test]
    [Arguments("items.rank")]
    [Arguments("info")]
    [Arguments("unknown")]
    [Arguments("rank=sideways")]
    [Arguments("rank!bad")]
    [Arguments("info.addresses.*.state")]
    public async Task SortRejectsUnknownComplexWildcardAndCrossCollectionProjections(string sort)
    {
        using var budget = new RegistryQueryBudget();
        await ExpectCode(() => Evaluate(Groups(), budget, sort: sort), "bad_sort");
    }

    [Test]
    public async Task SortRejectsAnEntityTargetEvenWithNoMatchingRows()
    {
        using var budget = new RegistryQueryBudget();
        await ExpectCode(() => Evaluate(Groups(), budget, "/fleets/a", ["name=missing"], "name"), "sort_noncollection");
    }

    [Test]
    public async Task ResourceFactsUseDefaultVersionWhileMetaAndIsDefaultKeepTheirOwnMeaning()
    {
        var source = new MemoryQuerySource();
        source.Resource("/fleets/g/items/r", """{"defaultversionid":"2","epoch":99,"readonly":true}""",
            """{"versionid":"2","epoch":7,"rank":10}""");
        source.Add("/fleets/g/items/r/versions/1", """{"versionid":"1","epoch":1000,"rank":2000}""");
        using var budget = new RegistryQueryBudget();
        var resource = await Evaluate(source, budget, "/fleets/g/items",
            ["rank=10,epoch=7,meta.epoch=99,meta.readonly=true,meta.defaultversionid=2,isdefault=true"]);
        await Assert.That(Ids(resource)).IsEqualTo("r");
        var old = await Evaluate(source, budget, "/fleets/g/items/r/versions", ["isdefault=false"]);
        await Assert.That(Ids(old)).IsEqualTo("1");
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/g/items", ["rank=2000"]))).IsEqualTo("");
    }

    [Test]
    public async Task ExplicitVersionDefaultContextSupportsAnAuthorizedVersionOnlyView()
    {
        var source = new MemoryQuerySource();
        source.Entities["/fleets/g/items/r/versions/1"] = new(RegistryJson.Parse("""{"versionid":"1"}"""))
        {
            DefaultVersionId = "2"
        };
        source.Entities["/fleets/g/items/r/versions/2"] = new(RegistryJson.Parse("""{"versionid":"2"}"""))
        {
            DefaultVersionId = "2"
        };
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/g/items/r/versions", ["isdefault=false"]))).IsEqualTo("1");
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/g/items/r/versions", ["isdefault=true"]))).IsEqualTo("2");
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/g/items/r/versions", ["defaultversionid=2"]))).IsEqualTo("");
        source.Hidden.Add("/fleets/g/items/r");
        await ExpectCode(() => Evaluate(source, budget, "/fleets/g/items/r", ["name=null"]), "not_found");
        source.Hidden.Clear();
        source.Add("/fleets/g/items/r", """{"defaultversionid":"1"}""");
        await ExpectCode(() => Evaluate(source, budget, "/fleets/g/items/r", ["versions.isdefault"]), "invalid_query_source");
    }

    [Test]
    [Arguments("doc.a>9007199254740992", "json")]
    [Arguments("docbase64=eyJhIjoxfQ==", "binary")]
    [Arguments("docbase64=/v8=", "invalid")]
    [Arguments("docbase64=", "empty")]
    [Arguments("docurl=https://unfetched.invalid/private", "url")]
    public async Task DocumentFactsUseExactJsonStringBinaryInvalidUtf8EmptyAndUnfetchedUrlPlanes(string filter, string expected)
    {
        var source = Documents();
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/g/docs", [filter]))).IsEqualTo(expected);
        if (filter.StartsWith("docurl", StringComparison.Ordinal))
        {
            await Assert.That(source.DocumentReads.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task StringDocumentLiteralWorksButComparingAnObjectDocumentToItRejectsTheWholeQuery()
    {
        var source = Documents();
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/g/docs/text$details", ["doc=HELLO"]))).IsEqualTo("text");
        await ExpectCode(() => Evaluate(source, budget, "/fleets/g/docs", ["doc=HELLO"]), "bad_filter");
    }

    [Test]
    public async Task AModelSingularEndingInBase64DoesNotRequestTheSeparateBinaryDocumentField()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"spaces":{"singular":"space","resources":{"payloads":{"singular":"payloadbase64"}}}}}
            """));
        var source = new MemoryQuerySource();
        source.Resource("/spaces/g/payloads/r", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"text/plain"}""", System.Text.Encoding.UTF8.GetBytes("hello"));
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(source, budget, "/spaces/g/payloads",
            ["payloadbase64=HELLO"], model: model))).IsEqualTo("r");
        await Assert.That(Ids(await Evaluate(source, budget, "/spaces/g/payloads",
            ["payloadbase64base64=aGVsbG8="], model: model))).IsEqualTo("r");
    }

    [Test]
    public async Task LogicalNavigationUsesTheTrustedMountAndCanonicalIdsWithoutRewritingDomainValues()
    {
        var source = new MemoryQuerySource();
        source.Resource("/fleets/g/docs/r%3A1", """{"defaultversionid":"v:2"}""",
            """{"versionid":"v:2","self":"https://origin.invalid/wrong","xid":"/wrong","name":"https://origin.invalid/domain"}""");
        source.Entities["/fleets/g/docs/r%3A1"] = new(source.Entities["/fleets/g/docs/r%3A1"].Metadata)
        {
            ShortSelf = "https://public.example/catalog/~/short"
        };
        using var budget = new RegistryQueryBudget();
        var result = await Evaluate(source, budget, "/fleets/g/docs/r%3a1$details",
            ["self=https://public.example/catalog/fleets/g/docs/r%3A1$details,name=https://origin.invalid/domain," +
             "shortself=https://public.example/catalog/~/short," +
             "meta.defaultversionurl=https://public.example/catalog/fleets/g/docs/r%3A1/versions/v%3A2$details"]);
        await Assert.That(result.RootPaths.Single().ToXid()).IsEqualTo("/fleets/g/docs/r%3A1");
        await Assert.That(result.CollectionUrl(RegistryPath.Parse("/fleets/g/docs/r%3a1/versions"), 1))
            .IsEqualTo("https://public.example/catalog/fleets/g/docs/r%3A1/versions");
        await Assert.That(result.Includes(RegistryPath.Parse("/fleets/g/docs/other/versions/v%3A2"))).IsFalse();
    }

    [Test]
    public async Task VisibilityPrecedesCountsPredicatesAndMetaAccessAndNeverMeansMissingNull()
    {
        var source = Tree();
        source.Hidden.Add("/fleets/a/items/y");
        source.Hidden.Add("/fleets/a/items/x/meta");
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/a", ["itemscount=1"]))).IsEqualTo("a");
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/a/items", ["meta.readonly!=true"]))).IsEqualTo("");
        source.Hidden.Add("/fleets/a/items/x/versions/1");
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/a/items", ["name!=missing"]))).IsEqualTo("");
    }

    [Test]
    public async Task ConfigurationFactsRequireTheAuthorizedActualPlaneRatherThanTheOfferedOrSourceModel()
    {
        var source = Groups();
        source.Add("/capabilities", """{"pagination":false}""");
        source.Add("/modelsource", """{"description":"declared source"}""");
        source.Add("/model", """{"description":"frozen effective"}""");
        using var budget = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(source, budget, "/",
            ["capabilities.pagination=false,model.description=frozen effective,modelsource.description=declared source"])))
            .IsEqualTo("/");
        source.Hidden.Add("/capabilities");
        await ExpectCode(() => Evaluate(source, budget, "/", ["capabilities=null"]), "not_found");
    }
}
