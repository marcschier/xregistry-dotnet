// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class WotVersionAssignmentContractTests
{
    [Test]
    [Arguments("thingmodel", """{ "id" : "urn:example:tm-authored", "version" : { "model" : "1.2.3", "instance" : "9.8.7" }, "title" : "preserve this formatting" }""", "1.2.3", "authored", "urn:example:tm-authored")]
    [Arguments("thingdescription", """{ "id" : "urn:example:td-authored", "version" : { "model" : "9.8.7", "instance" : "4.5.6" }, "title" : "preserve this formatting" }""", "4.5.6", "authored", "urn:example:td-authored")]
    [Arguments("thingmodel", """{ "id" : "urn:example:tm-wrong-term", "version" : { "instance" : "9.8.7" }, "title" : "wrong term is not the model version" }""", "0.0.0-1", "generated", "urn:example:tm-wrong-term")]
    [Arguments("thingdescription", """{ "id" : "urn:example:td-wrong-term", "version" : { "model" : "1.2.3" }, "title" : "wrong term is not the instance version" }""", "0.0.0-1", "generated", "urn:example:td-wrong-term")]
    public async Task ExecuteAsyncAssignsAuthoredTermByResourceKindAndPreservesDocument(
        string resourceSingular, string sourceDocument, string expectedVersionId, string expectedOrigin, string expectedWotId)
    {
        var fixture = CreateWotFixture();
        var resourcePath = fixture.ResourcePath(resourceSingular, "subject");

        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, resourcePath, sourceDocument, "{}");

        await AssertStoredAssignmentAsync(fixture.Engine, resourcePath, expectedVersionId, expectedOrigin,
            expectedWotId, sourceDocument);
    }

    [Test]
    public async Task ExecuteAsyncGeneratesLowestUnusedIdentifier()
    {
        var sequential = CreateWotFixture();
        var sequentialPath = sequential.ResourcePath("thingmodel", "sequential");
        const string firstGeneratedDocument = """{ "id" : "urn:example:generated-exact" }""";
        var firstVersionPath = VersionPath(sequentialPath, "0.0.0-1");
        var firstResult = await ExecuteDocumentAsync(sequential.Engine, RegistryAction.Replace, firstVersionPath,
            firstGeneratedDocument, "{}");
        await Assert.That(firstResult.Path.EscapedPath).IsEqualTo(firstVersionPath.EscapedPath);
        await AssertStoredAssignmentAsync(sequential.Engine, sequentialPath, "0.0.0-1", "generated",
            "urn:example:generated-exact", firstGeneratedDocument);

        const string secondGeneratedDocument = """{ "id" : "urn:example:generated-exact", "title" : "the next available slot" }""";
        var secondResult = await ExecuteDocumentAsync(sequential.Engine, RegistryAction.Post, sequentialPath,
            secondGeneratedDocument, "{}");
        var secondVersionPath = VersionPath(sequentialPath, "0.0.0-2");
        await Assert.That(secondResult.Path.EscapedPath).IsEqualTo(secondVersionPath.EscapedPath);
        await AssertStoredAssignmentAsync(sequential.Engine, sequentialPath, "0.0.0-2", "generated",
            "urn:example:generated-exact", secondGeneratedDocument);

        var sparse = CreateWotFixture();
        var sparsePath = sparse.ResourcePath("thingmodel", "sparse");
        const string occupiedOne = """{ "id" : "urn:example:occupied-one", "version" : { "model" : "0.0.0-1" }, "title" : "occupied first slot" }""";
        var occupiedOnePath = VersionPath(sparsePath, "0.0.0-1");
        var occupiedOneResult = await ExecuteDocumentAsync(sparse.Engine, RegistryAction.Replace, occupiedOnePath,
            occupiedOne, "{}");
        await Assert.That(occupiedOneResult.Path.EscapedPath).IsEqualTo(occupiedOnePath.EscapedPath);
        await AssertStoredAssignmentAsync(sparse.Engine, sparsePath, "0.0.0-1", "authored",
            "urn:example:occupied-one", occupiedOne);

        const string occupiedThree = """{ "id" : "urn:example:occupied-one", "version" : { "model" : "0.0.0-3" }, "title" : "occupied third slot" }""";
        var occupiedThreePath = VersionPath(sparsePath, "0.0.0-3");
        var occupiedThreeResult = await ExecuteDocumentAsync(sparse.Engine, RegistryAction.Replace,
            occupiedThreePath, occupiedThree, "{}");
        await Assert.That(occupiedThreeResult.Path.EscapedPath).IsEqualTo(occupiedThreePath.EscapedPath);
        await AssertStoredAssignmentAsync(sparse.Engine, sparsePath, "0.0.0-3", "authored",
            "urn:example:occupied-one", occupiedThree);

        const string sparseGeneratedDocument = """{ "id" : "urn:example:occupied-one", "title" : "the free interior slot" }""";
        var sparseResult = await ExecuteDocumentAsync(sparse.Engine, RegistryAction.Post, sparsePath,
            sparseGeneratedDocument, "{}");
        var interiorVersionPath = VersionPath(sparsePath, "0.0.0-2");
        await Assert.That(sparseResult.Path.EscapedPath).IsEqualTo(interiorVersionPath.EscapedPath);
        await AssertStoredAssignmentAsync(sparse.Engine, sparsePath, "0.0.0-2", "generated",
            "urn:example:occupied-one", sparseGeneratedDocument);
    }

    [Test]
    public async Task ExecuteAsyncAcceptsMatchingSuppliedIdAndExactVersionPath()
    {
        var fixture = CreateWotFixture();
        var postPath = fixture.ResourcePath("thingmodel", "supplied");
        const string postDocument = """{ "id" : "urn:example:authored-post", "version" : { "model" : "1.2.3", "instance" : "9.8.7" }, "title" : "matching resource-level candidate" }""";
        var postResult = await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Post, postPath, postDocument,
            """{"versionid":"1.2.3"}""");
        var authoredPostVersionPath = VersionPath(postPath, "1.2.3");
        await Assert.That(postResult.Path.EscapedPath).IsEqualTo(authoredPostVersionPath.EscapedPath);
        await AssertStoredAssignmentAsync(fixture.Engine, postPath, "1.2.3", "authored",
            "urn:example:authored-post", postDocument);

        var exactPath = fixture.ResourcePath("thingmodel", "exact");
        const string exactDocument = """{ "id" : "urn:example:authored-exact", "version" : { "model" : "2.3.4", "instance" : "8.7.6" }, "title" : "matching exact Version route" }""";
        var exactVersionPath = VersionPath(exactPath, "2.3.4");
        var exactResult = await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, exactVersionPath,
            exactDocument, "{}");
        await Assert.That(exactResult.Path.EscapedPath).IsEqualTo(exactVersionPath.EscapedPath);
        await AssertStoredAssignmentAsync(fixture.Engine, exactPath, "2.3.4", "authored",
            "urn:example:authored-exact", exactDocument);
    }

    [Test]
    public async Task ExecuteAsyncRejectsMismatchedThingDescriptionVersionIdAndBuildMetadata()
    {
        var fixture = CreateWotFixture();
        var resourcePath = fixture.ResourcePath("thingdescription", "rejected");
        const string authored = """{"id":"urn:example:td","version":{"instance":"1.2.3"}}""";
        var mismatch = await Assert.That(async () => await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Post,
            resourcePath, authored, """{"versionid":"1.2.4"}"""))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a mismatched Version id failure.");
        const string buildMetadata = """{"id":"urn:example:td-build","version":{"instance":"1.2.3+build.7"}}""";
        var invalid = await Assert.That(async () => await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace,
            fixture.ResourcePath("thingdescription", "build"), buildMetadata, "{}"))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected build metadata to be rejected.");

        await Assert.That(mismatch.Diagnostic.Code).IsEqualTo("mismatched_id");
        await Assert.That(invalid.Diagnostic.Code).IsEqualTo("invalid_attribute");
    }

    [Test]
    public async Task ExecuteAsyncLateSemVerPatchDoesNotReplaceNewerDefault()
    {
        var fixture = CreateWotFixture();
        var resourcePath = fixture.ResourcePath("thingmodel", "late-patch");
        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, resourcePath,
            """{"id":"urn:example:pump","version":{"model":"1.0.0"}}""", "{}");
        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Post, resourcePath,
            """{"id":"urn:example:pump","version":{"model":"1.1.0"}}""", "{}");
        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, VersionPath(resourcePath, "1.0.1"),
            """{"id":"urn:example:pump","version":{"model":"1.0.1"}}""", "{}");

        var metadata = await fixture.Engine.ExecuteAsync(new(RegistryAction.Read,
            RegistryPath.Parse(resourcePath.EscapedPath + "/meta")), Reader());
        await Assert.That(metadata.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("1.1.0");
    }

    private static WotFixture CreateWotFixture()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Wot);
        var modelSource = source.RootElement.GetRawText();
        var model = RegistryModel.Compile(RegistryJson.Parse(modelSource));
        var targets = model.Groups.Values.SelectMany(group => group.Resources.Values.Select(resource =>
            (Group: group, Resource: resource)));
        var thingModel = targets.Single(static item => item.Resource.Singular == "thingmodel");
        var thingDescription = targets.Single(static item => item.Resource.Singular == "thingdescription");
        return new(EngineTests.Create(modelSource, new InMemoryRegistryPersistence(), new PermitPolicy()),
            new(thingModel.Group.Plural, thingModel.Resource.Plural),
            new(thingDescription.Group.Plural, thingDescription.Resource.Plural));
    }

    private static async Task<RegistryResult> ExecuteDocumentAsync(RegistryEngine engine, RegistryAction action,
        RegistryPath path, string sourceDocument, string metadata)
    {
        using var document = new MemoryStream(Encoding.UTF8.GetBytes(sourceDocument), writable: false);
        var operation = new RegistryOperation(action, path)
        {
            Metadata = RegistryJson.Parse(metadata),
            Document = document,
            ContentType = "application/json"
        };
        return await engine.ExecuteAsync(operation, Writer());
    }

    private static RegistryPath VersionPath(RegistryPath resourcePath, string versionId) =>
        RegistryPath.Parse(resourcePath.EscapedPath + "/versions/" + Uri.EscapeDataString(versionId));

    private static async Task AssertStoredAssignmentAsync(RegistryEngine engine, RegistryPath resourcePath,
        string expectedVersionId, string expectedOrigin, string expectedWotId, string sourceDocument)
    {
        var versionPath = VersionPath(resourcePath, expectedVersionId);
        var details = await engine.ExecuteAsync(
            new(RegistryAction.Read, RegistryPath.Parse(versionPath.EscapedPath + "$details")), Reader());
        var metadata = details.Metadata!.RootElement;
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo(expectedVersionId);
        await Assert.That(metadata.GetProperty("versionorigin").GetString()).IsEqualTo(expectedOrigin);
        await Assert.That(metadata.GetProperty("wotid").GetString()).IsEqualTo(expectedWotId);

        var storedDocument = await engine.ExecuteAsync(new(RegistryAction.Read, versionPath), Reader());
        await Assert.That(storedDocument.ContentType).IsEqualTo("application/json");
        using var input = storedDocument.Document!.OpenRead();
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        var expectedBytes = Encoding.UTF8.GetBytes(sourceDocument);
        await Assert.That(output.ToArray().SequenceEqual(expectedBytes)).IsTrue();
    }

    private sealed record WotResourceTarget(string GroupPlural, string ResourcePlural);

    private sealed record WotFixture(RegistryEngine Engine, WotResourceTarget ThingModel,
        WotResourceTarget ThingDescription)
    {
        internal RegistryPath ResourcePath(string singular, string resourceId)
        {
            var target = singular switch
            {
                "thingmodel" => ThingModel,
                "thingdescription" => ThingDescription,
                _ => throw new ArgumentOutOfRangeException(nameof(singular))
            };
            return RegistryPath.ForResource(target.GroupPlural, RegistryId.Parse("group"), target.ResourcePlural,
                RegistryId.Parse(resourceId));
        }
    }
}
