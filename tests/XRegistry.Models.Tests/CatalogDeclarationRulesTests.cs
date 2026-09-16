// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class CatalogDeclarationRulesTests
{
    [Test]
    [Arguments("""{"federationprofiles":[{"endpoint":"urn:example:catalog"}]}""", "/federationprofiles/0/name", "invalid_attribute")]
    [Arguments("""{"federationprofiles":[{"name":"future"}]}""", "/federationprofiles/0/endpoint", "invalid_attribute")]
    [Arguments("""{"federationprofiles":[{"name":"future","endpoint":"urn:example:catalog","extra":true}]}""", "/federationprofiles/0/extra", "unknown_attribute")]
    [Arguments("""{"relationships":[{"type":"future","target":"/categories/a/registries/b","extra":true}]}""", "/relationships/0/extra", "unknown_attribute")]
    [Arguments("""{"federationprofiles":[{"name":"future","endpoint":"urn:example:catalog","parameters":{"Bad":1}}]}""", "/federationprofiles/0/parameters/Bad", "invalid_attribute")]
    [Arguments("""{"relationships":[{"type":"future","target":"/categories/a/registries/b","labels":{"Bad":""}}]}""", "/relationships/0/labels/Bad", "invalid_attribute")]
    [Arguments("""{"relationships":[{"type":"future","target":"/categories/a/registries/b$details"}]}""", "/relationships/0/target", "invalid_attribute")]
    public async Task CommonFieldsHaveExactCoreDiagnostics(string json, string path, string code)
    {
        await Error(() => RegistryDomainRules.ValidateCatalogDescription(RegistryJson.Parse(json).RootElement), path, code);
    }

    [Test]
    [Arguments("false")]
    [Arguments("-1")]
    [Arguments("-0.1")]
    [Arguments("0.5")]
    [Arguments("1e-1")]
    [Arguments("\"1\"")]
    public async Task CatalogPriorityRequiresAnUnsignedIntegerValue(string priority)
    {
        await Error(() => RegistryDomainRules.ValidateCatalogMetadata(RegistryJson.Parse($$"""
            {"federationprofiles":[{"name":"future","endpoint":"urn:example:catalog","priority":{{priority}}}]}
            """).RootElement), "/federationprofiles/0/priority");
    }

    [Test]
    public async Task PublicationAndConsumerValidationHaveDistinctBindingObligations()
    {
        var metadata = RegistryJson.Parse("""
            {"federationprofiles":[{"name":"git","endpoint":"https://git.example/repo","parameters":{"future":true}}]}
            """).RootElement;
        await Assert.That(() => RegistryDomainRules.ValidateCatalogDescription(metadata)).ThrowsNothing();
        await Error(() => RegistryDomainRules.ValidateCatalogMetadata(metadata), "/federationprofiles/0/parameters/revision");
        await Error(() => RegistryDomainRules.ValidateCatalogAdvertisement(metadata.GetProperty("federationprofiles")[0]), "/parameters/revision");
    }

    [Test]
    [Arguments("https://@registry.example/root")]
    [Arguments("file://@host/catalog")]
    public async Task EmptyUserInformationIsNotNormalizedIntoACredentialFreeEndpoint(string endpoint)
    {
        var advertisement = Advertisement("future", endpoint, "{}");
        await Error(() => RegistryDomainRules.ValidateCatalogAdvertisement(advertisement), "/endpoint");
    }

    [Test]
    [Arguments("x:a")]
    [Arguments("x:/a")]
    [Arguments("x://host/a")]
    public async Task AbsoluteExtensionUrisAreNotInterpretedAsWindowsPaths(string endpoint)
    {
        var advertisement = Advertisement("future", endpoint, "{}");
        await Assert.That(() => RegistryDomainRules.ValidateCatalogAdvertisement(advertisement)).ThrowsNothing();
        var metadata = RegistryJson.Parse(new JsonObject
        {
            ["registrytypes"] = new JsonArray(endpoint),
            ["authority"] = endpoint
        }.ToJsonString()).RootElement;
        await Assert.That(() => RegistryDomainRules.ValidateCatalogMetadata(metadata)).ThrowsNothing();
        await Assert.That(advertisement.GetProperty("endpoint").GetString()).IsEqualTo(endpoint);
    }

    [Test]
    [Arguments("oci", "urn:x")]
    [Arguments("oci", "x:a")]
    [Arguments("oci", "https://host/repo")]
    [Arguments("file", "urn:x")]
    [Arguments("git", "urn:x")]
    [Arguments("http", "urn:x")]
    public async Task WrongBindingUrisFailWithMetadataDiagnosticsRatherThanRuntimeErrors(string name, string endpoint)
    {
        var advertisement = Advertisement(name, endpoint, """{"reference":"stable","layout":"document-tree","revision":"refs/heads/main"}""");
        await Error(() => RegistryDomainRules.ValidateCatalogAdvertisement(advertisement), "/endpoint");
    }

    [Test]
    [Arguments("oci", 0, false)]
    [Arguments("oci", 1, true)]
    [Arguments("oci", 127, true)]
    [Arguments("oci", 128, true)]
    [Arguments("oci", 129, false)]
    [Arguments("file", 128, true)]
    [Arguments("file", 129, false)]
    public async Task OciTagLengthBoundariesAlsoApplyToFileLayouts(string name, int length, bool valid)
    {
        var reference = new string('a', length);
        var advertisement = Advertisement(name, name == "oci" ? "oci://host/team/repo" : "file:///catalog",
            new JsonObject { ["layout"] = "oci-layout", ["reference"] = reference }.ToJsonString());
        await ValidateOrReject(advertisement, valid, "/parameters/reference");
    }

    [Test]
    [Arguments("sha256:", 63, "a", false)]
    [Arguments("sha256:", 64, "a", true)]
    [Arguments("sha256:", 65, "a", false)]
    [Arguments("sha256:", 64, "A", false)]
    [Arguments("sha256:", 64, "g", false)]
    [Arguments("SHA256:", 64, "a", false)]
    public async Task OciDigestSpellingAndLengthAreExact(string prefix, int length, string digit, bool valid)
    {
        var advertisement = Advertisement("oci", "oci://host/team/repo",
            new JsonObject { ["reference"] = prefix + new string(digit[0], length) }.ToJsonString());
        await ValidateOrReject(advertisement, valid, "/parameters/reference");
    }

    [Test]
    [Arguments("/a", true)]
    [Arguments("/a_b", true)]
    [Arguments("/a__b", true)]
    [Arguments("/a---b", true)]
    [Arguments("/a.b/c0", true)]
    [Arguments("/", false)]
    [Arguments("/A", false)]
    [Arguments("/a___b", false)]
    [Arguments("/a..b", false)]
    [Arguments("/a.-b", false)]
    [Arguments("/a/", false)]
    [Arguments("/a/../b", false)]
    [Arguments("/%61", false)]
    public async Task OciRepositoryGrammarUsesOriginalUnnormalizedSpelling(string repository, bool valid)
    {
        var advertisement = Advertisement("oci", "oci://host" + repository, """{"reference":"stable"}""");
        await ValidateOrReject(advertisement, valid, "/endpoint");
    }

    [Test]
    [Arguments(39, false)]
    [Arguments(40, true)]
    [Arguments(41, false)]
    [Arguments(63, false)]
    [Arguments(64, true)]
    [Arguments(65, false)]
    public async Task GitObjectIdsMustBeCompleteWithoutCaseNormalization(int length, bool valid)
    {
        var revision = new string('A', length);
        await Assert.That(CatalogBindingSyntax.IsGitRevision(revision)).IsEqualTo(valid);
        var advertisement = Advertisement("git", "https://git.example/repo",
            new JsonObject { ["revision"] = revision }.ToJsonString());
        await ValidateOrReject(advertisement, valid, "/parameters/revision");
        await Assert.That(advertisement.GetProperty("parameters").GetProperty("revision").GetString()).IsEqualTo(revision);
    }

    [Test]
    [Arguments("refs/heads/main", true)]
    [Arguments("refs/a", true)]
    [Arguments("Refs/a", false)]
    [Arguments("refs/heads/a.lock", false)]
    [Arguments("refs/heads/a.LOCK", true)]
    [Arguments("HEAD", false)]
    [Arguments("refs/a//b", false)]
    [Arguments("refs/.a", false)]
    [Arguments("refs/a@{1}", false)]
    [Arguments("refs/a^1", false)]
    public async Task GitRefsCannotBecomeRevisionExpressions(string revision, bool valid)
    {
        await Assert.That(CatalogBindingSyntax.IsGitRevision(revision)).IsEqualTo(valid);
        await ValidateOrReject(Advertisement("git", "https://git.example/repo",
            new JsonObject { ["revision"] = revision }.ToJsonString()), valid, "/parameters/revision");
    }

    [Test]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(63, true)]
    [Arguments(64, true)]
    [Arguments(65, false)]
    public async Task GitDirectoryComponentsHaveExactPortableBoundaries(int length, bool valid)
    {
        var root = new string('a', length);
        await Assert.That(CatalogBindingSyntax.IsGitRootPath(root)).IsEqualTo(valid);
        await ValidateOrReject(Advertisement("git", "https://git.example/repo",
            new JsonObject { ["revision"] = "refs/heads/main", ["path"] = root }.ToJsonString()), valid, "/parameters/path");
    }

    [Test]
    [Arguments("_", true)]
    [Arguments("a/COM0", true)]
    [Arguments("a/COM1", false)]
    [Arguments("a/lpt9.txt", false)]
    [Arguments("a/../b", false)]
    [Arguments("a.", false)]
    [Arguments("a//b", false)]
    [Arguments("/a", false)]
    [Arguments("a\\b", false)]
    [Arguments("a%2fb", false)]
    [Arguments("x/\u00e9", false)]
    public async Task GitRootSyntaxIsPortableWithoutFilesystemInterpretation(string root, bool valid)
    {
        await Assert.That(CatalogBindingSyntax.IsGitRootPath(root)).IsEqualTo(valid);
        await ValidateOrReject(Advertisement("git", "https://git.example/repo",
            new JsonObject { ["revision"] = "refs/heads/main", ["path"] = root }.ToJsonString()), valid, "/parameters/path");
    }

    [Test]
    [Arguments(1, false, true)]
    [Arguments(128, false, true)]
    [Arguments(129, false, false)]
    [Arguments(1, true, true)]
    [Arguments(128, true, true)]
    [Arguments(129, true, false)]
    public async Task LocalRelationshipIdsFollowCoreDecodedLengthLimits(int length, bool escaped, bool valid)
    {
        var id = escaped ? string.Concat(Enumerable.Repeat("%41", length)) : new string('A', length);
        var metadata = RegistryJson.Parse($$"""
            {"relationships":[{"type":"future","target":"/categories/{{id}}/registries/schema:main@v1"}]}
            """).RootElement;
        if (valid)
        {
            await Assert.That(() => RegistryDomainRules.ValidateCatalogMetadata(metadata)).ThrowsNothing();
        }
        else
        {
            await Error(() => RegistryDomainRules.ValidateCatalogMetadata(metadata), "/relationships/0/target");
        }
    }

    [Test]
    public async Task ValidationObservesCancellationBeforeInspectingMetadata()
    {
        var token = new CancellationToken(canceled: true);
        await Assert.That(() => RegistryDomainRules.ValidateCatalogMetadata(default, token)).Throws<OperationCanceledException>();
        await Assert.That(() => RegistryDomainRules.ValidateCatalogDescription(default, token)).Throws<OperationCanceledException>();
        await Assert.That(() => RegistryDomainRules.ValidateCatalogAdvertisement(default, token)).Throws<OperationCanceledException>();
    }

    private static System.Text.Json.JsonElement Advertisement(string name, string endpoint, string parameters) =>
        RegistryJson.Parse(new JsonObject
        {
            ["name"] = name,
            ["endpoint"] = endpoint,
            ["parameters"] = JsonNode.Parse(parameters)
        }.ToJsonString()).RootElement;

    private static async Task ValidateOrReject(System.Text.Json.JsonElement advertisement, bool valid, string path)
    {
        if (valid)
        {
            await Assert.That(() => RegistryDomainRules.ValidateCatalogAdvertisement(advertisement)).ThrowsNothing();
        }
        else
        {
            await Error(() => RegistryDomainRules.ValidateCatalogAdvertisement(advertisement), path);
        }
    }

    private static async Task Error(Action action, string path, string code = "invalid_attribute")
    {
        var exception = await Assert.That(action).Throws<RegistryException>();
        await Assert.That(exception!.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(exception.Diagnostic.Path).IsEqualTo(path);
    }
}
