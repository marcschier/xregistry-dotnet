using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class ProducerModelValidationEvidenceTests
{
    private const string ModelText = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r",
          "validateformat":true,"validatecompatibility":true,"strictvalidation":true,
          "attributes":{"domain":{"type":"any"}}
        }}}}}
        """;

    [Test]
    public async Task NativeDocumentMetadataNeverInventsSuccessfulValidationEvidence()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelText));
        var source = new ProducerModelDataSource("native", model)
        {
            ProducerOwned = true,
            Representation = FederationRepresentation.DocumentView,
        };
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"format":"JsonSchema/draft/2020-12","domain":{"formatvalidated":"opaque","compatibilityvalidatedreason":"keep"}}""",
        });
        source.Entities["/gs/g/rs/item/meta"]["compatibility"] = "backward";
        var original = source.Entities["/gs/g/rs/item/versions/v1"].ToJsonString();
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [source.Registration()]);
        var path = RegistryPath.Parse("/gs/g/rs/item/versions/v1$details");

        var api = await view.ReadAsync(path);
        await Assert.That(api.Metadata.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(api.Metadata.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(api.Metadata.GetProperty("formatvalidatedreason").GetString())
            .IsEqualTo("The read-only producer received no captured format validation outcome.");
        await Assert.That(api.Metadata.GetProperty("compatibilityvalidatedreason").GetString())
            .IsEqualTo("The read-only producer received no captured compatibility validation outcome.");
        await Assert.That(api.Metadata.GetProperty("domain").GetProperty("formatvalidated").GetString()).IsEqualTo("opaque");
        var doc = await view.ReadAsync(path, new ProducerViewOptions { DocumentView = true });
        foreach (var name in new[] { "formatvalidated", "formatvalidatedreason", "compatibilityvalidated", "compatibilityvalidatedreason" })
        {
            await Assert.That(doc.Metadata.TryGetProperty(name, out _)).IsFalse();
        }
        await Assert.That(doc.Metadata.GetProperty("domain").GetProperty("compatibilityvalidatedreason").GetString()).IsEqualTo("keep");
        await Assert.That(source.Reads.Any(r => r.Operation == FederationOperation.Document)).IsFalse();
        await Assert.That(source.Entities["/gs/g/rs/item/versions/v1"].ToJsonString()).IsEqualTo(original);
    }

    [Test]
    public async Task CapturedApiValidationOutcomesRemainUnchangedWithoutDocumentRevalidation()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelText));
        var source = new ProducerModelDataSource("api", model) { ProducerOwned = true };
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"format":"custom/unknown","formatvalidated":true,"compatibilityvalidated":false,"compatibilityvalidatedreason":"Captured compatibility was not checked."}""",
        });
        source.Entities["/gs/g/rs/item/meta"]["compatibility"] = "backward";
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [source.Registration()]);
        var path = RegistryPath.Parse("/gs/g/rs/item/versions/v1$details");

        var result = await view.ReadAsync(path);
        await Assert.That(result.Metadata.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(result.Metadata.TryGetProperty("formatvalidatedreason", out _)).IsFalse();
        await Assert.That(result.Metadata.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        await Assert.That(result.Metadata.GetProperty("compatibilityvalidatedreason").GetString())
            .IsEqualTo("Captured compatibility was not checked.");
        await Assert.That(source.Reads.Count).IsEqualTo(1);
        var doc = await view.ReadAsync(path, new ProducerViewOptions { DocumentView = true });
        await Assert.That(doc.Metadata.TryGetProperty("formatvalidated", out _)).IsFalse();
        await Assert.That(doc.Metadata.TryGetProperty("compatibilityvalidated", out _)).IsFalse();
        await Assert.That(source.Reads.Any(r => r.Operation == FederationOperation.Document)).IsFalse();
    }

    [Test]
    [Arguments(false, false, true, true)]
    [Arguments(true, false, false, false)]
    [Arguments(true, true, true, false)]
    public async Task ValidationOutcomePresenceFollowsCoreControlsFormatAndCompatibility(
        bool validateFormat, bool validateCompatibility, bool hasFormat, bool hasCompatibility)
    {
        var node = JsonNode.Parse(ModelText)!.AsObject();
        var definition = node["groups"]!["gs"]!["resources"]!["rs"]!;
        definition["validateformat"] = validateFormat;
        definition["validatecompatibility"] = validateCompatibility;
        var model = RegistryModel.Compile(RegistryJson.Parse(node.ToJsonString()));
        var source = new ProducerModelDataSource("native", model) { Representation = FederationRepresentation.DocumentView };
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = hasFormat ? """{"format":"custom/1"}""" : "{}",
        });
        if (hasCompatibility) { source.Entities["/gs/g/rs/item/meta"]["compatibility"] = "backward"; }
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [source.Registration()]);

        var result = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));

        await Assert.That(result.Metadata.TryGetProperty("formatvalidated", out var format)).IsEqualTo(validateFormat && hasFormat);
        if (format.ValueKind != JsonValueKind.Undefined) { await Assert.That(format.GetBoolean()).IsFalse(); }
        await Assert.That(result.Metadata.TryGetProperty("compatibilityvalidated", out _))
            .IsEqualTo(validateCompatibility && hasFormat && hasCompatibility);
        await Assert.That(source.Reads.Any(r => r.Operation == FederationOperation.Document)).IsFalse();
    }
}
