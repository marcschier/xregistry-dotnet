// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class GitAdvertisementValidationTests
{
    [Test]
    [Arguments("{}")]
    [Arguments("""{"revision":null}""")]
    [Arguments("""{"revision":17}""")]
    [Arguments("""{"revision":false}""")]
    [Arguments("""{"revision":[]}""")]
    [Arguments("""{"revision":{}}""")]
    [Arguments("""{"revision":""}""")]
    [Arguments("""{"revision":" "}""")]
    public async Task SelectedGitRequiresANonemptyStringRevisionBeforeAnyFallback(string parameters)
    {
        var description = Description(RegistryJson.Parse(parameters));
        await Assert.That(description.Select(new(["http"])).Endpoint).IsEqualTo("https://fallback.invalid");
        await Check.Error(() => description.Select(new(["git", "http"])), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task MissingGitParametersDoNotTurnIntoAnImplicitRevisionOrHttpFallback()
    {
        var description = Description(null);
        await Assert.That(description.Advertisements[0].Parameters.EnumerateObject().Count()).IsEqualTo(0);
        await Check.Error(() => description.Select(new(["git", "http"])), FederationErrorCode.InvalidPackage);
        await Assert.That(description.Select(new(["http"])).OriginalIndex).IsEqualTo(1);
    }

    [Test]
    [Arguments("refs/heads/main")]
    [Arguments("refs/tags/release-1")]
    [Arguments("refs/heads/a.b")]
    [Arguments("refs/heads/a.LOCK")]
    [Arguments("refs/heads/@")]
    [Arguments("refs/heads/nested/item")]
    [Arguments("refs/main")]
    public async Task CompleteRefsRemainCaseSensitiveSelectionInputs(string revision)
    {
        GitBindingSyntax.ValidateRevision(revision);
        var selected = Select(revision);
        await Assert.That(selected.Name).IsEqualTo("git");
        await Assert.That(selected.OriginalIndex).IsEqualTo(0);
        await Assert.That(selected.Parameters.GetProperty("revision").GetString()).IsEqualTo(revision);
    }

    [Test]
    [Arguments("HEAD")]
    [Arguments("main")]
    [Arguments("Refs/heads/main")]
    [Arguments("refs/")]
    [Arguments("refs//main")]
    [Arguments("refs/heads/main/")]
    [Arguments("refs/heads/a..b")]
    [Arguments("refs/heads/.hidden")]
    [Arguments("refs/heads/a.lock")]
    [Arguments("refs/heads/a.lock/child")]
    [Arguments("refs/heads/a@{1}")]
    [Arguments("refs/heads/a~1")]
    [Arguments("refs/heads/a^1")]
    [Arguments("refs/heads/a:path")]
    [Arguments("refs/heads/a.")]
    [Arguments("refs/heads/a b")]
    [Arguments("refs/heads/a?b")]
    [Arguments("refs/heads/a*b")]
    [Arguments("refs/heads/a[b")]
    [Arguments("refs/heads/a\\b")]
    [Arguments("refs/heads/a\tb")]
    [Arguments("refs/heads/a\u007fb")]
    public async Task RevisionExpressionsAndInvalidRefComponentsCannotSelectAFallback(string revision)
    {
        await Check.Error(() => GitBindingSyntax.ValidateRevision(revision), FederationErrorCode.InvalidPackage);
        await Check.Error(() => Select(revision), FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(40)]
    [Arguments(64)]
    public async Task CompleteObjectIdsKeepTheirOriginalCaseUntilPinResolution(int length)
    {
        var revision = new string('A', length);
        GitBindingSyntax.ValidateRevision(revision);
        await Assert.That(Select(revision).Parameters.GetProperty("revision").GetString()).IsEqualTo(revision);
    }

    [Test]
    [Arguments(39)]
    [Arguments(41)]
    [Arguments(63)]
    [Arguments(65)]
    public async Task AbbreviatedAndOverlongObjectIdsAreNotReinterpretedAsRefs(int length)
    {
        await Check.Error(() => Select(new string('a', length)), FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(4096)]
    [Arguments(4097)]
    public async Task RevisionLengthBudgetIsInclusiveAtBothPublicSeams(int length)
    {
        var revision = "refs/heads/" + new string('a', length - "refs/heads/".Length);
        if (length == 4096)
        {
            GitBindingSyntax.ValidateRevision(revision);
            await Assert.That(Select(revision).Parameters.GetProperty("revision").GetString()!.Length).IsEqualTo(length);
        }
        else
        {
            await Check.Error(() => GitBindingSyntax.ValidateRevision(revision), FederationErrorCode.LimitExceeded);
            await Check.Error(() => Select(revision), FederationErrorCode.LimitExceeded);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("xregistry")]
    [Arguments("a")]
    [Arguments("_")]
    [Arguments("A0._-1")]
    [Arguments("root/_cache/child")]
    [Arguments("COM0")]
    [Arguments("COM10")]
    [Arguments("LPT0")]
    public async Task PortableRootLocatorsRetainExactSpellingIncludingTheExplicitEmptyRoot(string root)
    {
        GitBindingSyntax.ValidateRootPath(root);
        var selected = Select("refs/heads/main", root);
        await Assert.That(selected.Parameters.GetProperty("path").GetString()).IsEqualTo(root);
        await Assert.That(selected.Endpoint).IsEqualTo("https://repository.invalid/registry.git");
    }

    [Test]
    public async Task AbsentRootRemainsAbsentInsteadOfInjectingAPathIntoCatalogMetadata()
    {
        var selected = Select("refs/heads/main");
        await Assert.That(selected.Parameters.TryGetProperty("path", out _)).IsFalse();
        await Assert.That(selected.Parameters.GetProperty("revision").GetString()).IsEqualTo("refs/heads/main");
    }

    [Test]
    [Arguments("""{"revision":"refs/heads/main","path":null}""")]
    [Arguments("""{"revision":"refs/heads/main","path":17}""")]
    [Arguments("""{"revision":"refs/heads/main","path":false}""")]
    [Arguments("""{"revision":"refs/heads/main","path":[]}""")]
    public async Task SuppliedRootMustBeAStringRatherThanSilentlyUsingTheDefault(string parameters)
    {
        await Check.Error(() => Description(RegistryJson.Parse(parameters)).Select(new(["git", "http"])),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(1)]
    [Arguments(64)]
    [Arguments(65)]
    public async Task EachRootComponentHasAnInclusiveSixtyFourCharacterLimit(int length)
    {
        var root = "outer/" + new string('a', length) + "/inner";
        if (length <= 64)
        {
            GitBindingSyntax.ValidateRootPath(root);
            await Assert.That(Select("refs/heads/main", root).Parameters.GetProperty("path").GetString()).IsEqualTo(root);
        }
        else
        {
            await Check.Error(() => GitBindingSyntax.ValidateRootPath(root), FederationErrorCode.InvalidPackage);
            await Check.Error(() => Select("refs/heads/main", root), FederationErrorCode.InvalidPackage);
        }
    }

    [Test]
    [Arguments(4095)]
    [Arguments(4096)]
    [Arguments(4097)]
    public async Task TotalRootLengthHasASeparateInclusiveBudget(int length)
    {
        var prefix = string.Join('/', Enumerable.Repeat(new string('a', 63), 63));
        var root = prefix + "/" + new string('b', length - prefix.Length - 3) + "/c";
        await Assert.That(root.Length).IsEqualTo(length);
        await Assert.That(root.Split('/').All(static component => component.Length is >= 1 and <= 64)).IsTrue();
        if (length <= 4096)
        {
            GitBindingSyntax.ValidateRootPath(root);
            await Assert.That(Select("refs/heads/main", root).Parameters.GetProperty("path").GetString()).IsEqualTo(root);
        }
        else
        {
            await Check.Error(() => GitBindingSyntax.ValidateRootPath(root), FederationErrorCode.LimitExceeded);
            await Check.Error(() => Select("refs/heads/main", root), FederationErrorCode.LimitExceeded);
        }
    }

    [Test]
    [Arguments("/xregistry", FederationErrorCode.PolicyDenied)]
    [Arguments("xregistry/", FederationErrorCode.PolicyDenied)]
    [Arguments("a//b", FederationErrorCode.PolicyDenied)]
    [Arguments(".", FederationErrorCode.PolicyDenied)]
    [Arguments("..", FederationErrorCode.PolicyDenied)]
    [Arguments("a/../b", FederationErrorCode.PolicyDenied)]
    [Arguments("a.", FederationErrorCode.PolicyDenied)]
    [Arguments("a ", FederationErrorCode.PolicyDenied)]
    [Arguments("a\\b", FederationErrorCode.PolicyDenied)]
    [Arguments("C:\\root", FederationErrorCode.PolicyDenied)]
    [Arguments("C:/root", FederationErrorCode.PolicyDenied)]
    [Arguments("%2e", FederationErrorCode.PolicyDenied)]
    [Arguments("%2E%2e", FederationErrorCode.PolicyDenied)]
    [Arguments("a/%2f/b", FederationErrorCode.PolicyDenied)]
    [Arguments("a/%5C/b", FederationErrorCode.PolicyDenied)]
    [Arguments("CON", FederationErrorCode.PolicyDenied)]
    [Arguments("con.txt", FederationErrorCode.PolicyDenied)]
    [Arguments("PrN.json", FederationErrorCode.PolicyDenied)]
    [Arguments("aux", FederationErrorCode.PolicyDenied)]
    [Arguments("nul.data", FederationErrorCode.PolicyDenied)]
    [Arguments("COM1", FederationErrorCode.PolicyDenied)]
    [Arguments("cOm9.log", FederationErrorCode.PolicyDenied)]
    [Arguments("LPT1", FederationErrorCode.PolicyDenied)]
    [Arguments("lPt9.log", FederationErrorCode.PolicyDenied)]
    [Arguments("-root", FederationErrorCode.InvalidPackage)]
    [Arguments(".hidden", FederationErrorCode.InvalidPackage)]
    [Arguments("a b", FederationErrorCode.InvalidPackage)]
    [Arguments("a%41", FederationErrorCode.InvalidPackage)]
    [Arguments("caf\u00e9", FederationErrorCode.InvalidPackage)]
    public async Task ForbiddenRootSpellingsFailExplicitlyWithoutDecodingOrFallback(string root, FederationErrorCode code)
    {
        await Check.Error(() => GitBindingSyntax.ValidateRootPath(root), code);
        await Check.Error(() => Select("refs/heads/main", root), code);
    }

    private static CatalogAdvertisement Select(string revision, string? root = null)
    {
        var parameters = new JsonObject { ["revision"] = revision };
        if (root is not null) { parameters["path"] = root; }
        return Description(RegistryJson.Parse(parameters.ToJsonString())).Select(new(["git", "http"]));
    }

    private static CatalogDescription Description(RegistryJson? parameters)
    {
        var git = new JsonObject { ["name"] = "git", ["endpoint"] = "https://repository.invalid/registry.git" };
        if (parameters is not null) { git["parameters"] = JsonNode.Parse(parameters.RootElement.GetRawText()); }
        return CatalogDescription.Parse(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["federationprofiles"] = new JsonArray(git, new JsonObject
            {
                ["name"] = "http",
                ["endpoint"] = "https://fallback.invalid",
                ["priority"] = 9
            })
        }.ToJsonString()));
    }
}
