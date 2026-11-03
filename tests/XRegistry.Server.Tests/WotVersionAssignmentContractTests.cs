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
    [Arguments("thingmodel", WotVersionProfile.SemVer, "0.0.0-1")]
    [Arguments("thingdescription", WotVersionProfile.SemVer, "0.0.0-1")]
    [Arguments("thingmodel", WotVersionProfile.Incremental, "1")]
    [Arguments("thingdescription", WotVersionProfile.Incremental, "1")]
    public async Task DocumentsWithoutIdUseExplicitPublisherIdentityWithoutRewritingBytes(
        string kind, WotVersionProfile profile, string versionId)
    {
        var fixture = CreateWotFixture(profile);
        var path = fixture.ResourcePath(kind, "explicit-identity");
        const string document = "{\r\n  \"title\": \"No authored identifier\"\r\n}\r\n";

        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, path, document,
            """{"wotid":"urn:example:explicit-identity"}""");

        await AssertStoredAssignmentAsync(fixture.Engine, path, versionId, "generated",
            "urn:example:explicit-identity", document);
    }

    [Test]
    [Arguments("thingmodel", WotVersionProfile.SemVer, "model", "1.2.3")]
    [Arguments("thingdescription", WotVersionProfile.SemVer, "instance", "1.2.3")]
    [Arguments("thingmodel", WotVersionProfile.Incremental, "model", "42")]
    [Arguments("thingdescription", WotVersionProfile.Incremental, "instance", "42")]
    public async Task ExplicitPublisherIdentityRetainsAuthoredVersionAssignment(
        string kind, WotVersionProfile profile, string field, string versionId)
    {
        var fixture = CreateWotFixture(profile);
        var path = fixture.ResourcePath(kind, "authored-identity");
        var document = $$$"""{"title":"No id","version":{"{{{field}}}":"{{{versionId}}}"}}""";

        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, path, document,
            """{"wotid":"urn:example:authored-identity"}""");

        await AssertStoredAssignmentAsync(fixture.Engine, path, versionId, "authored",
            "urn:example:authored-identity", document);
    }

    [Test]
    [Arguments(WotVersionProfile.SemVer, "0.0.0-1")]
    [Arguments(WotVersionProfile.Incremental, "1")]
    public async Task MissingBothIdentitiesRejectsWithoutAllocatingARevision(
        WotVersionProfile profile, string versionId)
    {
        var fixture = CreateWotFixture(profile);
        var path = fixture.ResourcePath("thingmodel", "missing-identity");
        var error = await Assert.That(async () => await ExecuteDocumentAsync(fixture.Engine,
            RegistryAction.Replace, path, """{"title":"No identity"}""", "{}"))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("An identity must be supplied.");
        await Assert.That(error.Diagnostic.Code).IsEqualTo("invalid_attribute");

        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, path, """{"title":"No identity"}""",
            """{"wotid":"urn:example:missing-identity"}""");
        await AssertStoredAssignmentAsync(fixture.Engine, path, versionId, "generated",
            "urn:example:missing-identity", """{"title":"No identity"}""");
    }

    [Test]
    [Arguments("null")]
    [Arguments("42")]
    [Arguments("{}")]
    public async Task ExplicitPublisherIdentityDoesNotReplaceAnInvalidAuthoredId(string invalidId)
    {
        var fixture = CreateWotFixture();
        var path = fixture.ResourcePath("thingmodel", "invalid-identity");
        var error = await Assert.That(async () => await ExecuteDocumentAsync(fixture.Engine,
            RegistryAction.Replace, path, $$"""{"id":{{invalidId}}}""",
            """{"wotid":"urn:example:invalid-identity"}"""))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("An invalid id must be rejected.");
        await Assert.That(error.Diagnostic.Code).IsEqualTo("invalid_attribute");
    }

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

        await Assert.That(mismatch.Diagnostic.Code).IsEqualTo("version-mismatch");
        await Assert.That(invalid.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    [Arguments("thingmodel", "model", 122, true)]
    [Arguments("thingmodel", "model", 123, false)]
    [Arguments("thingdescription", "instance", 122, true)]
    [Arguments("thingdescription", "instance", 123, false)]
    public async Task ExecuteAsyncEnforcesTheCoreVersionIdLengthBoundary(
        string kind, string field, int prereleaseLength, bool accepted)
    {
        var fixture = CreateWotFixture();
        var resourcePath = fixture.ResourcePath(kind, "version-length");
        var authoredVersion = "1.0.0-" + new string('a', prereleaseLength);
        var document = "{\"id\":\"urn:example:version-length\",\"version\":{\"" + field +
            "\":\"" + authoredVersion + "\"}}";

        if (accepted)
        {
            await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, resourcePath, document, "{}");
            await AssertStoredAssignmentAsync(fixture.Engine, resourcePath, authoredVersion, "authored",
                "urn:example:version-length", document);
            return;
        }

        var error = await Assert.That(async () => await ExecuteDocumentAsync(fixture.Engine,
            RegistryAction.Replace, resourcePath, document, "{}"))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("An overlength SemVer must be rejected.");
        await Assert.That(error.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    [Arguments("thingmodel", "model", "1.2.3", "generated", "1.2.3", "authored")]
    [Arguments("thingdescription", "instance", "1.2.3", "generated", "1.2.3", "authored")]
    [Arguments("thingmodel", "model", "", "authored", "0.0.0-1", "generated")]
    [Arguments("thingdescription", "instance", "", "authored", "0.0.0-1", "generated")]
    public async Task ExecuteAsyncIgnoresClientSuppliedVersionOrigin(
        string kind, string field, string authoredVersion, string suppliedOrigin, string expectedVersionId,
        string expectedOrigin)
    {
        var fixture = CreateWotFixture();
        var resourcePath = fixture.ResourcePath(kind, "version-origin");
        var version = authoredVersion.Length == 0
            ? ""
            : ",\"version\":{\"" + field + "\":\"" + authoredVersion + "\"}";
        var document = "{\"id\":\"urn:example:version-origin\"" + version + "}";

        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, resourcePath, document,
            "{\"versionorigin\":\"" + suppliedOrigin + "\"}");

        await AssertStoredAssignmentAsync(fixture.Engine, resourcePath, expectedVersionId, expectedOrigin,
            "urn:example:version-origin", document);
    }

    [Test]
    [Arguments("thingmodel")]
    [Arguments("thingdescription")]
    public async Task ExecuteAsyncRejectsWotIdChangesAcrossResourceVersions(string kind)
    {
        var fixture = CreateWotFixture();
        var resourcePath = fixture.ResourcePath(kind, "changed-wotid");
        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, resourcePath,
            """{"id":"urn:example:original"}""", "{}");

        await Assert.That(async () => await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Post, resourcePath,
            """{"id":"urn:example:replacement"}""", "{}")).Throws<RegistryException>();
        var missingRevision = await Assert.That(async () => await fixture.Engine.ExecuteAsync(
            new(RegistryAction.Read, VersionPath(resourcePath, "0.0.0-2")), Reader()))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("The rejected identity change must not persist.");
        await Assert.That(missingRevision.Diagnostic.Code).IsEqualTo("not_found");
    }

    [Test]
    [Arguments("thingmodel")]
    [Arguments("thingdescription")]
    public async Task RegistryStoresDocumentsWithUnresolvedThingModelReferences(string kind)
    {
        var fixture = CreateWotFixture();
        var resourcePath = fixture.ResourcePath(kind, "unresolved-reference");
        const string document = """
            {"id":"urn:example:unresolved-reference","links":[
              {"rel":"tm:extends","href":"https://missing.example/models/base"}
            ]}
            """;

        await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Replace, resourcePath, document, "{}");

        await AssertStoredAssignmentAsync(fixture.Engine, resourcePath, "0.0.0-1", "generated",
            "urn:example:unresolved-reference", document);
    }

    [Test]
    public async Task ExecuteAsyncRejectsInvalidSuppliedVersionIdBeforeAssignmentComparison()
    {
        var fixture = CreateWotFixture();
        var error = await Assert.That(async () => await ExecuteDocumentAsync(fixture.Engine, RegistryAction.Post,
            fixture.ResourcePath("thingmodel", "invalid-version-id"),
            """{"id":"urn:example:invalid-version-id","version":{"model":"1.2.3"}}""",
            """{"versionid":"invalid+id"}"""))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("An invalid Version id must be rejected.");

        await Assert.That(error.Diagnostic.Code).IsEqualTo("invalid-version");
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

    [Test]
    public async Task ConcurrentGeneratedAssignmentsCommitAtMostOneClaimAndRetryUsesTheNextId()
    {
        var persistence = new InMemoryRegistryPersistence();
        var first = CreateWotFixture(persistence: persistence);
        var second = CreateWotFixture(persistence: persistence);
        var resourcePath = first.ResourcePath("thingmodel", "concurrent");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var participants = 0;
        var context = new RegistryOperationContext(Writer().Caller)
        {
            PrepareResponseAsync = async (_, ct) =>
            {
                if (Interlocked.Increment(ref participants) == 2) { ready.SetResult(); }
                await ready.Task.WaitAsync(ct);
            }
        };

        async Task<string?> Attempt(WotFixture fixture, string title)
        {
            using var document = new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"id\":\"urn:example:concurrent\",\"title\":\"" + title + "\"}"), writable: false);
            try
            {
                var result = await fixture.Engine.ExecuteAsync(new(RegistryAction.Post, resourcePath)
                {
                    Document = document,
                    ContentType = "application/json",
                    Metadata = RegistryJson.Parse("{}")
                }, context);
                await Assert.That(result.Path.EscapedPath)
                    .IsEqualTo(VersionPath(resourcePath, "0.0.0-1").EscapedPath);
                return title;
            }
            catch (RegistryConcurrencyException)
            {
                return null;
            }
        }

        var outcomes = await Task.WhenAll(Attempt(first, "first"), Attempt(second, "second"))
            .WaitAsync(TimeSpan.FromSeconds(15));
        var committedTitles = outcomes.Where(static title => title is not null).ToArray();
        await Assert.That(committedTitles.Length).IsEqualTo(1);
        var committedDocument = "{\"id\":\"urn:example:concurrent\",\"title\":\"" + committedTitles[0] + "\"}";
        await AssertStoredAssignmentAsync(first.Engine, resourcePath, "0.0.0-1", "generated",
            "urn:example:concurrent", committedDocument);

        var retry = await ExecuteDocumentAsync(first.Engine, RegistryAction.Post, resourcePath,
            """{"id":"urn:example:concurrent","title":"retry"}""", "{}");
        await Assert.That(retry.Path.EscapedPath)
            .IsEqualTo(VersionPath(resourcePath, "0.0.0-2").EscapedPath);
        await AssertStoredAssignmentAsync(first.Engine, resourcePath, "0.0.0-2", "generated",
            "urn:example:concurrent", """{"id":"urn:example:concurrent","title":"retry"}""");
    }

    private static WotFixture CreateWotFixture(WotVersionProfile? profile = null,
        InMemoryRegistryPersistence? persistence = null)
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Wot);
        var modelSource = profile is { } selected
            ? WotRegistryProfiles.Compile(selected).Source.RootElement.GetRawText()
            : source.RootElement.GetRawText();
        var model = RegistryModel.Compile(RegistryJson.Parse(modelSource));
        var targets = model.Groups.Values.SelectMany(group => group.Resources.Values.Select(resource =>
            (Group: group, Resource: resource)));
        var thingModel = targets.Single(static item => item.Resource.Singular == "thingmodel");
        var thingDescription = targets.Single(static item => item.Resource.Singular == "thingdescription");
        return new(EngineTests.Create(modelSource, persistence ?? new InMemoryRegistryPersistence(), new PermitPolicy()),
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

    [Test]
    [Arguments("012-late-patch-default")]
    [Arguments("019-generated-skip-occupied")]
    [Arguments("036-publish-authored-assignment")]
    [Arguments("058-td-publish-authored-assignment")]
    [Arguments("060-td-publish-version-mismatch")]
    [Arguments("061-td-publish-build-metadata")]
    public async Task CanonicalAssignmentVector(string vectorId)
    {
        var vector = XRegistry.WotResolution.Testing.WotResolutionVectors.Assignment(vectorId);
        var fixture = CreateWotFixture();
        var resourcePath = fixture.ResourcePath(vector.ResourceKind, "canonical-wot");
        await SeedCanonicalRegistryAsync(fixture.Engine, resourcePath, vector);
        var metadataPath = RegistryPath.Parse(resourcePath.EscapedPath + "/meta");
        var initialMetadata = await fixture.Engine.ExecuteAsync(new(RegistryAction.Read, metadataPath), Reader());
        await Assert.That(initialMetadata.Metadata!.RootElement.GetProperty("defaultversionid").GetString())
            .IsEqualTo(vector.Registry.GetProperty("defaultversionid").GetString());
        await Assert.That(initialMetadata.Metadata.RootElement.GetProperty("defaultversionsticky").GetBoolean())
            .IsEqualTo(vector.Registry.GetProperty("defaultversionsticky").GetBoolean());

        var document = XRegistry.WotResolution.Testing.WotResolutionVectors.AssignmentDocument(vector);
        var requestedVersionId = vector.Publish.TryGetProperty("versionid", out var versionIdElement)
            ? versionIdElement.GetString()
            : null;
        var action = requestedVersionId is null ? RegistryAction.Post : RegistryAction.Replace;
        var path = requestedVersionId is null ? resourcePath : VersionPath(resourcePath, requestedVersionId);

        if (vector.Expected.TryGetProperty("error", out var expectedError))
        {
            var failure = await Assert.That(async () =>
                    await ExecuteDocumentAsync(fixture.Engine, action, path, document, "{}"))
                .Throws<RegistryException>()
                ?? throw new InvalidOperationException(
                    $"Vector '{vectorId}' expected diagnostic '{expectedError.GetString()}'.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(expectedError.GetString());
            var unchangedMetadata = await fixture.Engine.ExecuteAsync(
                new(RegistryAction.Read, metadataPath), Reader());
            await Assert.That(unchangedMetadata.Metadata!.RootElement.GetRawText())
                .IsEqualTo(initialMetadata.Metadata.RootElement.GetRawText());
            return;
        }

        var assigned = vector.Expected.GetProperty("assigned");
        var expectedVersion = assigned.GetProperty("versionid").GetString()!;
        var expectedOrigin = assigned.GetProperty("versionorigin").GetString()!;
        var expectedDefaultVersion = assigned.GetProperty("defaultversionid").GetString()!;
        var result = await ExecuteDocumentAsync(fixture.Engine, action, path, document, "{}");
        var expectedVersionPath = VersionPath(resourcePath, expectedVersion);
        await Assert.That(result.Path.EscapedPath).IsEqualTo(expectedVersionPath.EscapedPath);
        await AssertStoredAssignmentAsync(fixture.Engine, resourcePath, expectedVersion, expectedOrigin,
            vector.WotId, document);

        var resourceMetadata = await fixture.Engine.ExecuteAsync(new(RegistryAction.Read,
            RegistryPath.Parse(resourcePath.EscapedPath + "/meta")), Reader());
        await Assert.That(resourceMetadata.Metadata!.RootElement
            .GetProperty("defaultversionid").GetString()).IsEqualTo(expectedDefaultVersion);
    }

    private static async Task SeedCanonicalRegistryAsync(RegistryEngine engine, RegistryPath resourcePath,
        XRegistry.WotResolution.Testing.CanonicalWotAssignment vector)
    {
        var versions = vector.Registry.GetProperty("versions").EnumerateArray().ToArray();
        if (versions.Length == 0)
        {
            throw new InvalidDataException("A canonical assignment vector must seed at least one version.");
        }

        if (StringComparer.Ordinal.Equals(vector.Id, "019-generated-skip-occupied"))
        {
            var authoredGeneratedSlot = versions.Single(version =>
                version.GetProperty("versionid").GetString() == "0.0.0-1");
            var stableVersion = versions.Single(version =>
                version.GetProperty("versionid").GetString() == "1.1.0");
            var generatedThirdSlot = versions.Single(version =>
                version.GetProperty("versionid").GetString() == "0.0.0-3");

            await SeedCanonicalVersionAsync(engine, resourcePath, vector.WotId, authoredGeneratedSlot, first: true);
            await SeedCanonicalVersionAsync(engine, resourcePath, vector.WotId, stableVersion, first: false);

            // Fill the lower generated slot so the public assignment path can create the canonical generated 0.0.0-3.
            await ExecuteDocumentAsync(engine, RegistryAction.Post, resourcePath,
                XRegistry.WotResolution.Testing.WotResolutionVectors.Document(vector.WotId, default), "{}");
            await SeedCanonicalVersionAsync(engine, resourcePath, vector.WotId, generatedThirdSlot, first: false);
            await engine.ExecuteAsync(new(RegistryAction.Delete, VersionPath(resourcePath, "0.0.0-2")), Writer());
            return;
        }

        for (var index = 0; index < versions.Length; index++)
        {
            await SeedCanonicalVersionAsync(engine, resourcePath, vector.WotId, versions[index], first: index == 0);
        }
    }

    private static async Task SeedCanonicalVersionAsync(RegistryEngine engine, RegistryPath resourcePath,
        string wotId, System.Text.Json.JsonElement version, bool first)
    {
        var sourceDocument = XRegistry.WotResolution.Testing.WotResolutionVectors.Document(wotId, version);
        var versionId = version.GetProperty("versionid").GetString()!;
        var path = first ? resourcePath : VersionPath(resourcePath, versionId);
        await ExecuteDocumentAsync(engine, RegistryAction.Replace, path, sourceDocument, "{}");
    }
}
