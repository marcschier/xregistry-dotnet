// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
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
