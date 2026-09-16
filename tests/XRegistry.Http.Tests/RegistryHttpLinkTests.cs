// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;

namespace XRegistry.Http.Tests;

public class RegistryHttpLinkTests
{
    [Test]
    public async Task DelimitersInsideUriAndQuotedParametersAreNotLinkSeparators()
    {
        var links = RegistryHttpLink.Parse(
        [
            """<page?a=1,2;b=3>;rel="next last";title="part, \"two\"";count=12""",
            "<first>;rel=first, <previous>;rel=prev"
        ]);

        await Assert.That(links.Count).IsEqualTo(3);
        await Assert.That(links[0].Reference).IsEqualTo("page?a=1,2;b=3");
        await Assert.That(links[0].HasRelation("NEXT")).IsTrue();
        await Assert.That(links[0].HasRelation("last")).IsTrue();
        await Assert.That(links[0].Parameters["TITLE"]).IsEqualTo("part, \"two\"");
        await Assert.That(links[0].Parameters["count"]).IsEqualTo("12");
        await Assert.That(links[2].Reference).IsEqualTo("previous");
    }

    [Test]
    public async Task RfcFirstRelationParameterWinsAndSameCountCanRepeat()
    {
        var links = RegistryHttpLink.Parse(["<page>;rel=next;rel=prev;count=2;COUNT=2"]);
        await Assert.That(links[0].HasRelation("next")).IsTrue();
        await Assert.That(links[0].HasRelation("prev")).IsFalse();
        await Assert.That(links[0].Parameters["count"]).IsEqualTo("2");
    }

    [Test]
    [Arguments("page;rel=next")]
    [Arguments("<page;rel=next")]
    [Arguments("<page>;rel=\"next")]
    [Arguments("<page>;=next")]
    [Arguments("<page>;rel=")]
    [Arguments("<page>;rel=next garbage")]
    [Arguments("<page>;count=2;count=3")]
    [Arguments("<bad path>;rel=next")]
    [Arguments("<bad\\path>;rel=next")]
    [Arguments("<page>;title=\"a\r\nb\";rel=next")]
    public async Task MalformedLinkSyntaxFailsRatherThanDroppingNavigation(string field)
    {
        await Assert.That(() => RegistryHttpLink.Parse([field])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task LinkAndCharacterBudgetsAcceptBoundaryAndRejectNextValue()
    {
        const string field = "<a>;rel=next";
        var result = RegistryHttpLink.Parse([field], 1, field.Length);
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(() => RegistryHttpLink.Parse([field], 1, field.Length - 1)).Throws<InvalidDataException>();
        await Assert.That(() => RegistryHttpLink.Parse([field, field], 1)).Throws<InvalidDataException>();
    }
}
