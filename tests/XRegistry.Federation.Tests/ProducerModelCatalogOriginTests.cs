using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Queries;

namespace XRegistry.Federation.Tests;

public class ProducerModelCatalogOriginTests
{
    [Test]
    public async Task CatalogOriginSurvivesProducerMetadataDocumentAndQueryResultsOutsideCoreMetadata()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","attributes":{"domain":{"type":"any"}}}}}}}
            """));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"domain":{"catalogorigin":"opaque","revision":"domain-revision"}}""",
        });
        var description = CatalogDescription.Parse("""
            {"versionid":"description-v2","registryid":"description-id","federationprofiles":[
              {"name":"http","endpoint":"https://selected.test/registry"},
              {"name":"http","endpoint":"https://never-open.test/registry","priority":2}]}
            """u8.ToArray());
        var catalog = new NativeRegistryContext("git", "https://catalog.test/repository", "catalog-commit", true);
        const string descriptionXid = "/categories/public/registries/example/versions/description-v2";
        CatalogAdvertisement? selectedAdvertisement = null;
        NativeRegistryContext? acquiredContext = null;
        var acquisitions = 0;
        var releases = 0;
        var registration = new FederationSourceRegistration("catalog-selected", FederationRepresentation.ApiView, async (budget, token) =>
        {
            var lease = await CatalogSourceSelection.OpenAsync(description, catalog, descriptionXid, new(["http"]),
                (advertisement, _, _) =>
                {
                    acquisitions++;
                    selectedAdvertisement = advertisement;
                    return ValueTask.FromResult(new FederationSourceLease(source,
                        () => { releases++; return ValueTask.CompletedTask; }, "access-profile-v1"));
                }, budget, token);
            acquiredContext = lease.Source.Context;
            return lease;
        });
        var root = new Uri("https://view.test/registry");
        await using (var view = new ProducerRegistryView(model, root, "view", [registration]))
        {
            var api = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
            var doc = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"),
                new ProducerViewOptions { DocumentView = true });
            var document = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1"));
            using var queryBudget = new RegistryQueryBudget();
            var query = await view.ReadQueryAsync(new RegistryQueryRequest(RegistryPath.Parse("/gs/g/rs"), root),
                new ProducerViewOptions(), queryBudget);

            foreach (var result in new[] { api, doc, document, query })
            {
                var origin = result.Origins.Single();
                await Assert.That(origin.Context).IsEqualTo(acquiredContext);
                await Assert.That(origin.Context.Revision).IsNull();
                await Assert.That(origin.Context.CatalogOrigin!.CatalogContext.Revision).IsEqualTo("catalog-commit");
                await Assert.That(origin.Context.CatalogOrigin.DescriptionVersionXid).IsEqualTo(descriptionXid);
                await Assert.That(ReferenceEquals(origin.Context.CatalogOrigin.Advertisement, selectedAdvertisement)).IsTrue();
                await Assert.That(origin.Context.CatalogOrigin.Advertisement.Endpoint).IsEqualTo("https://selected.test/registry");
                await Assert.That(origin.CredentialStamp).IsEqualTo("access-profile-v1");
                await Assert.That(result.Metadata.TryGetProperty("CatalogOrigin", out _)).IsFalse();
                await Assert.That(result.Metadata.TryGetProperty("catalogorigin", out _)).IsFalse();
            }
            await Assert.That(api.Metadata.GetProperty("domain").GetProperty("catalogorigin").GetString()).IsEqualTo("opaque");
            await Assert.That(doc.Metadata.GetProperty("domain").GetProperty("revision").GetString()).IsEqualTo("domain-revision");
            await Assert.That(document.SelectedXid).IsEqualTo("/gs/g/rs/item/versions/v1");
            using var stream = document.Document!.OpenRead();
            var bytes = new byte[5];
            await stream.ReadExactlyAsync(bytes);
            await Assert.That(Convert.ToHexString(bytes)).IsEqualTo("00FF0D0A41");
            await Assert.That(acquisitions).IsEqualTo(1);
            await Assert.That(releases).IsEqualTo(0);
        }
        await Assert.That(releases).IsEqualTo(1);
    }
}
