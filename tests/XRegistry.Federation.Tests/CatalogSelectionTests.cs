using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class CatalogSelectionTests
{
    [Test]
    [Arguments("file:///C:/catalog")]
    [Arguments("file://localhost/C:/catalog")]
    [Arguments("file:///R:/team-registry")]
    [Arguments("file:///mnt/team-registry")]
    public async Task AbsoluteFileAdvertisementsRemainSelectableAcrossPlatformUriPathForms(string endpoint)
    {
        var description = Parse($$$"""
            {"federationprofiles":[
              {"name":"file","endpoint":"{{{endpoint}}}","parameters":{"layout":"document-tree"}},
              {"name":"http","endpoint":"https://fallback.invalid","priority":9}]}
            """);
        var selected = description.Select(new(["file", "http"]));
        await Assert.That(selected.Name).IsEqualTo("file");
        await Assert.That(selected.Endpoint).IsEqualTo(endpoint);
        await Assert.That(selected.Parameters.GetProperty("layout").GetString()).IsEqualTo("document-tree");
    }

    [Test]
    public async Task ImplicitHttpKeepsZeroPriority()
    {
        var description = CatalogDescription.Parse(Encoding.UTF8.GetBytes("""
            {
              "xregurl": "https://example.test/Registry",
              "federationprofiles": [
                {"name":"http","endpoint":"https://example.test/Registry","priority":5},
                {"name":"file","endpoint":"file:///catalog","priority":2,
                 "parameters":{"layout":"document-tree"}}
              ]
            }
            """));

        var selected = description.Select(new AdvertisementSelectionOptions(["http", "file"]));

        await Assert.That(selected.Name).IsEqualTo("http");
        await Assert.That(selected.Endpoint).IsEqualTo("https://example.test/Registry");
        await Assert.That(selected.Priority).IsEqualTo(0UL);
        await Assert.That(selected.OriginalIndex).IsEqualTo(2);
        await Assert.That(selected.IsImplicit).IsTrue();
        await Assert.That(description.Advertisements.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CatalogDescriptionUsesExplicitOrDefaultVersion()
    {
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Catalog", "multi-profile-catalog.json")));
        var resource = json.RootElement.GetProperty("categories").GetProperty("public")
            .GetProperty("registries").GetProperty("schemas");
        var current = CatalogDescription.FromResource(resource);
        var old = CatalogDescription.FromResource(resource, "1");

        await Assert.That(current.Data.GetProperty("versionid").GetString()).IsEqualTo("2");
        await Assert.That(current.Select(new(["http", "git"])).Name).IsEqualTo("git");
        await Assert.That(old.Data.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(old.Select(new(["http", "oci"])).IsImplicit).IsTrue();
        await Check.Error(() => CatalogDescription.FromResource(resource, "missing"), FederationErrorCode.NotFound);
    }

    [Test]
    public async Task CallerChoicePrecedesPriority()
    {
        var description = Parse("""
            {"federationprofiles":[
              {"name":"http","endpoint":"https://first.test","priority":0},
              {"name":"git","endpoint":"https://git.test/repo","priority":20,
               "parameters":{"revision":"refs/heads/main","path":""}},
              {"name":"http","endpoint":"https://last.test","priority":30}]}
            """);
        await Assert.That(description.Select(new(["http", "git"], profileName: "git")).OriginalIndex)
            .IsEqualTo(1);
        await Assert.That(description.Select(new(["http", "git"], originalIndex: 2)).Endpoint)
            .IsEqualTo("https://last.test");
        await Check.Error(() => description.Select(new(["http"], profileName: "HTTP")),
            FederationErrorCode.UnsupportedBinding);
    }

    [Test]
    public async Task DuplicateAdvertisementsKeepFirst()
    {
        var description = Parse("""
            {"federationprofiles":[
              {"name":"http","endpoint":"https://same.test","priority":7},
              {"name":"http","endpoint":"https://same.test","priority":7}]}
            """);
        var selected = description.Select(new(["http"]));
        await Assert.That(selected.OriginalIndex).IsEqualTo(0);
        await Assert.That(description.Advertisements.Count).IsEqualTo(2);
        await Assert.That(description.Select(new(["http"], originalIndex: 1)).OriginalIndex).IsEqualTo(1);
    }

    [Test]
    [Arguments("https://example.test/Registry/")]
    [Arguments("https://EXAMPLE.test/Registry")]
    [Arguments("https://other.test/Registry")]
    public async Task HttpConsistencyPrecedesExclusion(string endpoint)
    {
        var input = $$$"""
            {"xregurl":"https://example.test/Registry","federationprofiles":[
             {"name":"http","endpoint":"{{{endpoint}}}"},
             {"name":"file","endpoint":"file:///root","parameters":{"layout":"document-tree"}}]}
            """;
        await Check.Error(() => Parse(input).Select(new(["file"], profileName: "file")),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("""{"name":"other","endpoint":"/relative"}""")]
    [Arguments("""{"name":"other","endpoint":"urn:example:x","priority":false}""")]
    [Arguments("""{"name":"other","endpoint":"urn:example:x","priority":0.5}""")]
    [Arguments("""{"name":"other","endpoint":"urn:example:x","parameters":[]}""")]
    [Arguments("""{"name":"other","endpoint":"urn:example:x","parameters":{"Bad":1}}""")]
    [Arguments("""{"name":"other","endpoint":"urn:example:x","extra":1}""")]
    public async Task CommonFieldsAreValidatedBeforeExclusion(string candidate)
    {
        await Check.Error(() => Parse($$"""
            {"federationprofiles":[{{candidate}},{"name":"http","endpoint":"https://ok.test"}]}
            """).Select(new(["http"])), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task UnsupportedParametersFailOnlyWhenSelected()
    {
        var description = Parse("""
            {"federationprofiles":[
              {"name":"git","endpoint":"https://git.test/repo","parameters":{"future":true}},
              {"name":"http","endpoint":"https://ok.test","priority":2}]}
            """);
        await Assert.That(description.Select(new(["http"])).Endpoint).IsEqualTo("https://ok.test");
        await Check.Error(() => description.Select(new(["git", "http"])),
            FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    [Arguments("""{"name":"git","endpoint":"https://git.test/repo","parameters":{"revision":"HEAD"}}""")]
    [Arguments("""{"name":"oci","endpoint":"oci://host/team/name:tag","parameters":{"reference":"stable"}}""")]
    [Arguments("""{"name":"oci","endpoint":"oci://host/team/name","parameters":{"reference":"bad/tag"}}""")]
    [Arguments("""{"name":"file","endpoint":"file:///root","parameters":{"layout":"unknown"}}""")]
    public async Task SelectedInvalidBindingNeverFallsBack(string candidate)
    {
        var description = Parse($$"""
            {"federationprofiles":[{{candidate}},{"name":"http","endpoint":"https://ok.test","priority":9}]}
            """);
        await Check.Error(() => description.Select(new(["http", "git", "oci", "file"])),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CallerPolicyCanExcludeButDenialStopsSelection()
    {
        var description = Parse("""
            {"federationprofiles":[
              {"name":"http","endpoint":"https://first.test"},
              {"name":"http","endpoint":"https://second.test"}]}
            """);
        await Assert.That(description.Select(new(["http"], accessPolicy: candidate =>
            candidate.OriginalIndex == 0 ? AdvertisementAccess.Exclude : AdvertisementAccess.Allow)).OriginalIndex)
            .IsEqualTo(1);
        await Check.Error(() => description.Select(new(["http"], accessPolicy: _ => AdvertisementAccess.Deny)),
            FederationErrorCode.PolicyDenied);
        await Check.Error(() => description.Select(new(["http"], accessPolicy: _ => AdvertisementAccess.Exclude)),
            FederationErrorCode.PolicyDenied);
    }

    [Test]
    [Arguments("""{"registrytypes":["schema"]}""")]
    [Arguments("""{"registrytypes":null}""")]
    [Arguments("""{"weburl":1}""")]
    [Arguments("""{"authority":false}""")]
    [Arguments("""{"relationships":[{"type":"","target":"/categories/a/registries/b"}]}""")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"../b"}]}""")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"//host/categories/a/registries/b"}]}""")]
    [Arguments("""{"relationships":[{"type":"mirrors","target":"/categories/a/registries/b","labels":{"Bad":"x"}}]}""")]
    public async Task CatalogAttributesAndRelationshipsAreValidated(string input)
    {
        await Check.Error(() => Parse(input), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CyclicDescriptiveRelationshipsDoNotBecomeCandidates()
    {
        var description = Parse("""
            {"weburl":"about/catalog","authority":"organizations/team",
             "registrytypes":["urn:example:model","urn:example:model"],
             "relationships":[
               {"type":"mirrors","target":"/categories/a/registries/self","labels":{"note":""}},
               {"type":"mirrors","target":"/categories/a/registries/self"}]}
            """);
        await Assert.That(description.Data.GetProperty("relationships").GetArrayLength()).IsEqualTo(2);
        await Check.Error(() => description.Select(new(["http"])), FederationErrorCode.UnsupportedBinding);
    }

    [Test]
    public async Task ArbitrarilyLargeUnsignedPriorityIsNotTruncated()
    {
        var description = Parse("""
            {"federationprofiles":[
              {"name":"http","endpoint":"https://first.test","priority":18446744073709551616},
              {"name":"http","endpoint":"https://second.test","priority":18446744073709551615}]}
            """);
        await Assert.That(description.Select(new(["http"])).OriginalIndex).IsEqualTo(1);
    }

    private static CatalogDescription Parse(string json) => CatalogDescription.Parse(Encoding.UTF8.GetBytes(json));
}
