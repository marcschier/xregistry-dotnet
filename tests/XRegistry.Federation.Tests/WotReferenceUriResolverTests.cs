// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class WotReferenceUriResolverTests
{
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
}
