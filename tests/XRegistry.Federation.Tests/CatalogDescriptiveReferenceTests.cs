// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class CatalogDescriptiveReferenceTests
{
    [Test]
    [Arguments("weburl")]
    [Arguments("authority")]
    public async Task MissingAndUnresolvedDescriptiveReferencesStayDistinct(string attribute)
    {
        var absent = CatalogDescription.Parse("{}"u8.ToArray());
        await Assert.That(CatalogDescriptiveReferences.Resolve(absent, attribute)).IsNull();
        var description = CatalogDescription.Parse("""{"weburl":"../site","authority":"organizations/team"}"""u8.ToArray());
        var result = CatalogDescriptiveReferences.Resolve(description, attribute);
        await Assert.That(result!.ResolvedValue).IsNull();
        await Assert.That(result.OriginalValue).IsEqualTo(attribute == "weburl" ? "../site" : "organizations/team");
    }

    [Test]
    [Arguments("HTTPS://EXAMPLE.test/%7eA?q=%2F")]
    [Arguments("urn:example:owner")]
    [Arguments("x:a")]
    public async Task AbsoluteDescriptiveReferencesRetainTheirExactSpelling(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject { ["authority"] = value }.ToJsonString());
        var description = CatalogDescription.Parse(bytes);
        var result = CatalogDescriptiveReferences.Resolve(description, "authority");
        await Assert.That(result!.OriginalValue).IsEqualTo(value);
        await Assert.That(result.ResolvedValue).IsEqualTo(value);
    }

    [Test]
    [Arguments("https://catalog.test/root?query")]
    [Arguments("https://catalog.test/root#fragment")]
    [Arguments("https://user@catalog.test/root")]
    [Arguments("file:///catalog")]
    [Arguments("relative/catalog")]
    public async Task RelativeResolutionRejectsInvalidOrCredentialBearingCatalogRoots(string root)
    {
        var description = CatalogDescription.Parse("""{"weburl":"site"}"""u8.ToArray());
        var error = await Assert.That(() => CatalogDescriptiveReferences.Resolve(description, "weburl",
            new Uri(root, UriKind.RelativeOrAbsolute))).Throws<FederationException>();
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CatalogReferenceBudgetsAndCancellationAreExplicit()
    {
        var description = CatalogDescription.Parse("""{"weburl":"a"}"""u8.ToArray());
        var result = CatalogDescriptiveReferences.Resolve(description, "weburl", maxUtf8Bytes: 1);
        await Assert.That(result!.OriginalValue).IsEqualTo("a");
        var error = await Assert.That(() => CatalogDescriptiveReferences.Resolve(description, "weburl", maxUtf8Bytes: 0))
            .Throws<FederationException>();
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(() => CatalogDescriptiveReferences.Resolve(description, "xregurl")).Throws<ArgumentException>();
        await Assert.That(() => CatalogDescriptiveReferences.Resolve(description, "weburl",
            cancellationToken: new CancellationToken(canceled: true))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task RelativeDescriptiveUrlsUseOnlyTheExplicitCatalogRootAsADirectory()
    {
        var description = CatalogDescription.Parse("""
            {"weburl":"about/%7eteam?x=%2F","authority":"../organizations/team",
             "xregurl":"https://advertised.invalid/target"}
            """u8.ToArray());
        var root = new Uri("https://catalog.test/api/catalog");

        var web = CatalogDescriptiveReferences.Resolve(description, "weburl", root);
        var authority = CatalogDescriptiveReferences.Resolve(description, "authority", root);

        await Assert.That(web!.OriginalValue).IsEqualTo("about/%7eteam?x=%2F");
        await Assert.That(web.ResolvedValue).IsEqualTo("https://catalog.test/api/catalog/about/~team?x=%2F");
        await Assert.That(authority!.ResolvedValue).IsEqualTo("https://catalog.test/api/organizations/team");
        await Assert.That(description.Data.GetProperty("weburl").GetString()).IsEqualTo("about/%7eteam?x=%2F");
        await Assert.That(description.Select(new(["http"])).Endpoint).IsEqualTo("https://advertised.invalid/target");
    }

    [Test]
    [Arguments("https://catalog.test/root", "https://catalog.test/root/about")]
    [Arguments("https://catalog.test/root/", "https://catalog.test/root/about")]
    [Arguments("https://catalog.test/root//", "https://catalog.test/root//about")]
    public async Task ExplicitEmptyRootPathSegmentsAreNotRemovedDuringResolution(string root, string expected)
    {
        var description = CatalogDescription.Parse("""{"weburl":"about"}"""u8.ToArray());
        var result = CatalogDescriptiveReferences.Resolve(description, "weburl", new Uri(root));
        await Assert.That(result!.ResolvedValue).IsEqualTo(expected);
    }
}
