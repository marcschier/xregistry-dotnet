// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Queries;
using static XRegistry.Core.Tests.QueryFixture;

namespace XRegistry.Core.Tests;

public class SharedQueryBoundaryTests
{
    [Test]
    [Arguments("default")]
    [Arguments("version")]
    [Arguments("document")]
    public async Task MissingRequiredSourcePlanesFailRatherThanBecomingEmptyResults(string missing)
    {
        var source = new MemoryQuerySource();
        source.Resource("/fleets/g/docs/r", """{"defaultversionid":"1"}""", """{"versionid":"1"}""");
        if (missing == "default")
        {
            source.Add("/fleets/g/docs/r", "{}");
        }
        else if (missing == "version")
        {
            source.Entities.Remove("/fleets/g/docs/r/versions/1");
        }

        using var budget = new RegistryQueryBudget();
        await ExpectCode(() => Evaluate(source, budget, "/fleets/g/docs", [missing == "document" ? "doc" : "name=null"]),
            "query_source_incomplete");
    }

    [Test]
    public async Task MissingSelectedOriginVersionNeverFallsBackToAnUnselectedVersion()
    {
        var source = new MemoryQuerySource();
        source.Add("/fleets/g/items/r", """{"defaultversionid":"selected"}""");
        source.Add("/fleets/g/items/r/meta", """{"defaultversionid":"selected"}""");
        source.Add("/fleets/g/items/r/versions/other", """{"versionid":"other","name":"tempting fallback"}""");
        using var budget = new RegistryQueryBudget();
        await ExpectCode(() => Evaluate(source, budget, "/fleets/g/items", ["name=*"]), "query_source_incomplete");
    }

    [Test]
    public async Task ResourceAndMetaDefaultFactsMustDescribeTheSamePinnedUnit()
    {
        var source = Tree();
        source.Add("/fleets/a/items/x/meta", """{"defaultversionid":"different","readonly":false}""");
        using var budget = new RegistryQueryBudget();
        await ExpectCode(() => Evaluate(source, budget, "/fleets/a/items/x", ["meta.defaultversionid=different"]),
            "invalid_query_source");
    }

    [Test]
    public async Task ASourceVersionCannotRelabelADifferentVersionAsTheRequestedIdentity()
    {
        var source = Tree();
        source.Add("/fleets/a/items/x/versions/1", """{"versionid":"2","rank":2}""");
        using var budget = new RegistryQueryBudget();
        await ExpectCode(() => Evaluate(source, budget, "/fleets/a/items", ["rank=2"]), "invalid_query_source");
    }

    [Test]
    [Arguments("/entries/a", "/entries/%61")]
    [Arguments("/entries/a", "/entries/A")]
    [Arguments("/entries/a", "/entries/a/items/r")]
    [Arguments("/entries/a", "/fleets/b")]
    public async Task DuplicateDecodedCaseConflictingOrNonDirectMembersRejectIncompleteSelections(string first, string second)
    {
        var source = Small();
        source.Collections["/entries"] = [first, second];
        using var budget = new RegistryQueryBudget();
        await ExpectCode(() => Evaluate(source, budget, "/entries", model: SmallModel), "invalid_query_source");
    }

    [Test]
    public async Task SourceMetadataMustRespectTheQueriesJsonDepthAndNodeBudgets()
    {
        var source = Small();
        source.Add("/entries/a", """{"entryid":"a","labels":{"one":"value","two":"value"}}""");
        using var depth = new RegistryQueryBudget(new() { Json = new() { MaxDepth = 1 } });
        using var nodes = new RegistryQueryBudget(new() { Json = new() { MaxNodes = 3 } });
        await ExpectCode(() => Evaluate(source, depth, "/entries", ["name=null"], model: SmallModel), "depth_limit");
        await ExpectCode(() => Evaluate(source, nodes, "/entries", ["name=null"], model: SmallModel), "node_limit");
    }

    [Test]
    public async Task SourceAndFactByteLimitsAreInclusiveAtTheExactIndependentFixtureSizes()
    {
        const string facts = """{"entryid":"a","self":"https://public.example/catalog/entries/a","xid":"/entries/a"}""";
        var factBytes = Encoding.UTF8.GetByteCount(facts);
        var source = Small();
        using var exact = new RegistryQueryBudget(new() { MaxFactBytes = factBytes, MaxSourceBytes = 25 });
        await Assert.That(Ids(await Evaluate(source, exact, "/entries", ["entryid=a"], model: SmallModel))).IsEqualTo("a");
        using var oneFactByteShort = new RegistryQueryBudget(new() { MaxFactBytes = factBytes - 1 });
        await ExpectCode(() => Evaluate(source, oneFactByteShort, "/entries", ["entryid=a"], model: SmallModel), "too_large");
        using var oneSourceByteShort = new RegistryQueryBudget(new() { MaxSourceBytes = 24 });
        await ExpectCode(() => Evaluate(source, oneSourceByteShort, "/entries", ["entryid=a"], model: SmallModel), "too_large");
    }

    [Test]
    public async Task FactCountersRemainCumulativeAcrossTwoEvaluationsUsingTheSameBudget()
    {
        using var budget = new RegistryQueryBudget(new() { MaxEntities = 1 });
        var source = Small();
        await Assert.That(Ids(await Evaluate(source, budget, "/entries", ["entryid=a"], model: SmallModel))).IsEqualTo("a");
        await ExpectCode(() => Evaluate(source, budget, "/entries", ["entryid=a"], model: SmallModel), "too_large");
    }

    [Test]
    public async Task CollectionAndSourceReadLimitsRejectTheFirstMemberBeyondTheirInclusiveBounds()
    {
        var source = Small();
        using var exact = new RegistryQueryBudget(new() { MaxCollectionMembers = 1, MaxSourceReads = 1 });
        await Assert.That(Ids(await Evaluate(source, exact, "/entries", ["entryid=a"], model: SmallModel))).IsEqualTo("a");
        source.Add("/entries/b", """{"entryid":"b"}""");
        using var members = new RegistryQueryBudget(new() { MaxCollectionMembers = 1 });
        using var reads = new RegistryQueryBudget(new() { MaxSourceReads = 1 });
        await ExpectCode(() => Evaluate(source, members, "/entries", model: SmallModel), "too_large");
        await ExpectCode(() => Evaluate(source, reads, "/entries", model: SmallModel), "too_large");
    }

    [Test]
    public async Task ExpressionSegmentQueryAndWorkLimitsRejectBeforeReturningAnySelection()
    {
        var source = Groups();
        using var expressions = new RegistryQueryBudget(new() { MaxFilterExpressions = 1 });
        using var segments = new RegistryQueryBudget(new() { MaxPathSegments = 1 });
        using var query = new RegistryQueryBudget(new() { MaxQueryCharacters = 7 });
        using var work = new RegistryQueryBudget(new() { MaxWork = 1 });
        await ExpectCode(() => Evaluate(source, expressions, filters: ["name=Alpha,rank=2"]), "too_large");
        await ExpectCode(() => Evaluate(source, segments, filters: ["info.owner=Joe"]), "too_large");
        await ExpectCode(() => Evaluate(source, query, filters: ["name=Alpha"]), "too_large");
        await ExpectCode(() => Evaluate(source, work, filters: ["rank"]), "too_large");
    }

    [Test]
    public async Task APresentEmptyDocumentFitsAZeroByteLimitButOneByteDoesNot()
    {
        var source = new MemoryQuerySource();
        source.Resource("/fleets/g/docs/r", """{"defaultversionid":"1"}""", """{"versionid":"1"}""",
            ReadOnlyMemory<byte>.Empty);
        using var exact = new RegistryQueryBudget(new() { MaxDocumentBytes = 0 });
        await Assert.That(Ids(await Evaluate(source, exact, "/fleets/g/docs", ["docbase64="]))).IsEqualTo("r");
        source.Entities["/fleets/g/docs/r/versions/1"] = new(RegistryJson.Parse("""{"versionid":"1"}"""))
        {
            Document = new byte[] { 0 }
        };
        using var shortBudget = new RegistryQueryBudget(new() { MaxDocumentBytes = 0 });
        await ExpectCode(() => Evaluate(source, shortBudget, "/fleets/g/docs", ["docbase64"]), "too_large");
    }

    [Test]
    public async Task MalformedJsonFallsBackToBytesButJsonWorkExhaustionDoesNotPretendToBeBinary()
    {
        var source = new MemoryQuerySource();
        source.Resource("/fleets/g/docs/r", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"application/json"}""", Encoding.UTF8.GetBytes("""{"a":1,"a":2}"""));
        using var fallback = new RegistryQueryBudget();
        await Assert.That(Ids(await Evaluate(source, fallback, "/fleets/g/docs", ["doc=null"]))).IsEqualTo("r");
        await Assert.That(Ids(await Evaluate(source, fallback, "/fleets/g/docs", ["docbase64=eyJhIjoxLCJhIjoyfQ=="]))).IsEqualTo("r");
        source.Resource("/fleets/g/docs/r", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"application/json"}""", Encoding.UTF8.GetBytes("""{"a":{"b":{"c":1}}}"""));
        using var depth = new RegistryQueryBudget(new() { Json = new() { MaxDepth = 2 } });
        await ExpectCode(() => Evaluate(source, depth, "/fleets/g/docs", ["doc"]), "depth_limit");
    }

    [Test]
    public async Task FilterLinkLengthIsBoundedAndCannotSilentlyLoseASelectedSubtree()
    {
        var source = Groups();
        using var budget = new RegistryQueryBudget(new() { MaxQueryCharacters = 10 });
        var result = await Evaluate(source, budget, filters: ["fleetid=a"]);
        RegistryDiagnostic? failure = null;
        try
        {
            result.CollectionUrl(RegistryPath.Parse("/fleets"), 1);
        }
        catch (RegistryException exception)
        {
            failure = exception.Diagnostic;
        }

        await Assert.That(failure?.Code).IsEqualTo("too_large");
    }

    [Test]
    public async Task AContradictoryProjectionCountCannotCreateAnUnfilteredCollectionLink()
    {
        using var budget = new RegistryQueryBudget();
        var selection = await Evaluate(Groups(), budget, filters: ["excludeall"]);
        await Assert.That(() => selection.CollectionUrl(RegistryPath.Parse("/fleets"), 1)).Throws<ArgumentException>();
    }

    [Test]
    public async Task ExplicitDanglingAliasesRemainMinimalWithoutInventingADefaultVersion()
    {
        var source = new MemoryQuerySource();
        var metadata = RegistryJson.Parse("""{"xref":"/fleets/other/items/r"}""");
        source.Entities["/fleets/g/items/alias"] = new(metadata) { IsDanglingCrossReference = true };
        source.Entities["/fleets/g/items/alias/meta"] = new(metadata) { IsDanglingCrossReference = true };
        using var budget = new RegistryQueryBudget();
        var result = await Evaluate(source, budget, "/fleets/g/items",
            ["meta.xref=/fleets/other/items/r,name=null"]);
        await Assert.That(Ids(result)).IsEqualTo("alias");
        source.Hidden.Add("/fleets/g/items/alias/meta");
        await Assert.That(Ids(await Evaluate(source, budget, "/fleets/g/items", ["meta.xref!=null"]))).IsEqualTo("");
    }

    [Test]
    public async Task CallerCancellationIsNotReclassifiedAsATimeoutOrAnEmptySuccess()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new RegistryQueryBudget(cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();
        var canceled = false;
        try
        {
            await Evaluate(Small(), budget, "/entries", model: SmallModel);
        }
        catch (OperationCanceledException exception)
        {
            canceled = exception.CancellationToken == cancellation.Token;
        }

        await Assert.That(canceled).IsTrue();
    }

    [Test]
    [Arguments("https://user:secret@host/catalog")]
    [Arguments("https://host/catalog?token=secret")]
    [Arguments("https://host/catalog#fragment")]
    [Arguments("file:///catalog")]
    public async Task QueryNavigationRejectsUntrustedRootForms(string root)
    {
        await Assert.That(() => new RegistryQueryRequest(RegistryPath.Parse("/"), new Uri(root))).Throws<ArgumentException>();
    }

    private static MemoryQuerySource Small()
    {
        var source = new MemoryQuerySource();
        source.Add("/entries/a", """{"entryid":"a"}""");
        return source;
    }
}
