using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class SchemaObjectJsonSelectionTests
{
    [Test]
    public async Task JsonStructureWithoutSuffixUsesItsDeclaredRootAndNotTheFirstDefinition()
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonStructure/draft-04", """
            {"$schema":"https://json-structure.org/meta/core/v0/#","$id":"https://schemas.test/s","name":"S",
             "$root":"/definitions/Z","definitions":{"A":{"type":"string"},"Z":{"type":"int64"}}}
            """u8.ToArray(), "schema.json");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.JsonPointer).IsEqualTo("/definitions/Z");
        await Assert.That(result.Selection.Json!.Value.GetProperty("type").GetString()).IsEqualTo("int64");
    }

    [Test]
    [Arguments("")]
    [Arguments("#/definitions")]
    [Arguments("#/definitions/People")]
    [Arguments("#/name")]
    public async Task JsonStructureNamespacesAndMetadataAreNotConcreteTypes(string suffix)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonStructure/draft-04", """
            {"$schema":"https://json-structure.org/meta/core/v0/#","$id":"https://schemas.test/s","name":"S",
             "definitions":{"People":{"Employee":{"type":"string"}}}}
            """u8.ToArray(), "schema.json" + suffix);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_type");
    }

    [Test]
    public async Task JsonSelectionOwnsExplicitDependenciesAndPreservesTheSuppliedBaseUri()
    {
        const string dependency = """{"$defs":{"Code":{"type":"integer"}}}""";
        var dependencyBytes = Encoding.UTF8.GetBytes(dependency);
        var calls = new List<string>();
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", """
            {"$defs":{"Value":{"$ref":"dep.json#/$defs/Code"}}}
            """u8.ToArray(), "https://schemas.test/root.json#/$defs/Value", new()
        {
            Validation = new()
            {
                DocumentUri = new("https://schemas.test/root.json"),
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(dependencyBytes);
                },
            },
        });
        Array.Fill(dependencyBytes, (byte)'!');

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.DocumentUri!.OriginalString).IsEqualTo("https://schemas.test/root.json");
        await Assert.That(result.Selection.Json!.Value.GetProperty("$ref").GetString()).IsEqualTo("dep.json#/$defs/Code");
        await Assert.That(Encoding.UTF8.GetString(result.Selection.References["https://schemas.test/dep.json"].Span)).IsEqualTo(dependency);
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("https://schemas.test/dep.json");
    }

    [Test]
    [Arguments("#/missing/~2")]
    [Arguments("#/missing/also~")]
    public async Task MalformedPointerSegmentsAreInvalidEvenAfterAnUnmatchedPrefix(string fragment)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
            "{}"u8.ToArray(), "schema" + fragment);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.pointer");
    }

    [Test]
    public async Task RawUnpairedSurrogatesAreNotAcceptedInUriFragments()
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
            "{}"u8.ToArray(), "schema#/" + '\uD800');

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.uri");
    }

    [Test]
    public async Task AnExplicitEmptyDocumentReferenceDisambiguatesAnInMemoryFragment()
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
            """{"properties":{"v":{"type":"integer"}}}"""u8.ToArray(), "#/properties/v",
            new() { DocumentReference = "" });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.DocumentReference).IsEqualTo("");
        await Assert.That(result.Selection.JsonPointer).IsEqualTo("/properties/v");
        await Assert.That(result.Selection.Json!.Value.GetProperty("type").GetString()).IsEqualTo("integer");
    }

    [Test]
    public async Task JsonStructureUsesAnExactLocalDocumentReferenceWithColonIds()
    {
        const string reference = "#/schemagroups/team%3AA/schemas/person:prod/versions/v:1";
        const string schema = """
            {"$schema":"https://json-structure.org/meta/core/v0/#","$id":"https://schemas.test/person",
             "name":"Person","$root":"/definitions/People/Employee",
             "definitions":{"People":{"Employee":{"type":"object","properties":{"Id":{"type":"int64"}}}}}}
            """;
        var result = await SchemaObjectSelector.SelectAsync("JsonStructure/draft-04", Encoding.UTF8.GetBytes(schema),
            reference + "/definitions/People/Employee", new() { DocumentReference = reference });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.Kind).IsEqualTo(SchemaTypeKind.JsonStructure);
        await Assert.That(result.Selection.DocumentReference).IsEqualTo(reference);
        await Assert.That(result.Selection.JsonPointer).IsEqualTo("/definitions/People/Employee");
        await Assert.That(result.Selection.Json!.Value.GetProperty("properties").GetProperty("Id").GetProperty("type").GetString())
            .IsEqualTo("int64");
    }

    [Test]
    [Arguments("#/title")]
    [Arguments("#/default")]
    [Arguments("#/examples/0")]
    [Arguments("#/$defs")]
    [Arguments("#/properties")]
    [Arguments("#/properties/id/type")]
    [Arguments("#/$defs/Anything")]
    public async Task JsonPointerRejectsMetadataContainersAndNonObjectSchemas(string fragment)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", """
            {"title":"Event","default":{"type":"object"},"examples":[{"type":"object"}],
             "$defs":{"Anything":true},"properties":{"id":{"type":"string"}}}
            """u8.ToArray(), "schema.json" + fragment);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_type");
    }

    [Test]
    [Arguments("#/$defs/name%252Fpart", "/$defs/name%2Fpart", "string")]
    [Arguments("#/$defs/~01", "/$defs/~01", "integer")]
    [Arguments("#/$defs/caf%C3%A9", "/$defs/caf\u00e9", "number")]
    [Arguments("#/allOf/0", "/allOf/0", "boolean")]
    public async Task JsonPointerDecodesUtf8AndTildeEscapesExactlyOnce(string fragment, string expectedJsonPointer, string type)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12", """
            {"$defs":{"name%2Fpart":{"type":"string"},"~1":{"type":"integer"},"caf\u00e9":{"type":"number"}},
             "allOf":[{"type":"boolean"}]}
            """u8.ToArray(), "schema.json" + fragment);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.JsonPointer).IsEqualTo(expectedJsonPointer);
        await Assert.That(result.Selection.Json!.Value.GetProperty("type").GetString()).IsEqualTo(type);
    }

    [Test]
    [Arguments("#/%", "selection.uri")]
    [Arguments("#/%GG", "selection.uri")]
    [Arguments("#/%E9", "selection.uri")]
    [Arguments("#/%C0%AF", "selection.uri")]
    [Arguments("#/%ED%A0%80", "selection.uri")]
    [Arguments("#/%F4%90%80%80", "selection.uri")]
    [Arguments("#/$defs/~2", "selection.pointer")]
    [Arguments("#not-a-pointer", "selection.pointer")]
    [Arguments("##/anything", "selection.uri")]
    public async Task MalformedFragmentsAreInvalidNotMissing(string fragment, string code)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
            """{"$defs":{}}"""u8.ToArray(), "schema.json" + fragment);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("#/missing")]
    [Arguments("#/allOf/01")]
    [Arguments("#/allOf/-")]
    [Arguments("#/allOf/2")]
    [Arguments("#/allOf/2147483648")]
    public async Task NonResolvingPointersReturnNotFound(string fragment)
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
            """{"allOf":[{"type":"object"}]}"""u8.ToArray(), "schema.json" + fragment);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.NotFound);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_found");
    }

    [Test]
    public async Task FragmentOnlyRegistryReferencesRequireAcquisitionContext()
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
            "{}"u8.ToArray(), "#/schemagroups/team/schemas/id:version");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.document_reference_required");
    }

    [Test]
    public async Task RawDocumentIdentityIsNotNormalizedToMatchAnotherSpelling()
    {
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
            "{}"u8.ToArray(), "#/schemagroups/team%3aa/schemas/id/properties/id",
            new() { DocumentReference = "#/schemagroups/team%3Aa/schemas/id" });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.document_reference_mismatch");
    }

    [Test]
    public async Task JsonPointerSelectsEscapedDefinitionWithoutLosingItsReferenceContext()
    {
        const string schema = """
            {"$defs":{"a/b~c":{"type":"object","properties":{"next":{"$ref":"#/$defs/a~1b~0c"}}}}}
            """;
        var resolverCalls = 0;
        var result = await SchemaObjectSelector.SelectAsync("JsonSchema/draft/2020-12",
            Encoding.UTF8.GetBytes(schema), "https://schemas.test/event.json#/%24defs/a~1b~0c",
            new SchemaObjectSelectionOptions
            {
                Validation = new()
                {
                    ResolveReference = (_, _) =>
                    {
                        resolverCalls++;
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
                    },
                },
            });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.DocumentReference).IsEqualTo("https://schemas.test/event.json");
        await Assert.That(result.Selection.JsonPointer).IsEqualTo("/$defs/a~1b~0c");
        await Assert.That(result.Selection.Json!.Value.GetProperty("properties").GetProperty("next").GetProperty("$ref").GetString())
            .IsEqualTo("#/$defs/a~1b~0c");
        await Assert.That(Encoding.UTF8.GetString(result.Selection.Document.Span)).IsEqualTo(schema);
        await Assert.That(resolverCalls).IsEqualTo(0);
    }

    [Test]
    public async Task JsonSchemaRootSelectionOwnsTheDocumentAndPreservesRawIdentity()
    {
        const string schema = """{"type":"object","properties":{"id":{"type":"string"}}}""";
        var bytes = Encoding.UTF8.GetBytes(schema);
        var result = await SchemaObjectSelector.SelectAsync(
            "JsonSchema/draft/2020-12", bytes, "https://EXAMPLE.test/schemas/%7eone.json");
        Array.Fill(bytes, (byte)'!');

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        var selection = result.Selection!;
        await Assert.That(selection.Kind).IsEqualTo(SchemaTypeKind.JsonSchema);
        await Assert.That(selection.DocumentReference).IsEqualTo("https://EXAMPLE.test/schemas/%7eone.json");
        await Assert.That(selection.SchemaUri).IsEqualTo("https://EXAMPLE.test/schemas/%7eone.json");
        await Assert.That(selection.Format).IsEqualTo("JsonSchema/draft/2020-12");
        await Assert.That(selection.JsonPointer).IsEqualTo("");
        await Assert.That(selection.TypeName).IsNull();
        await Assert.That(selection.Json!.Value.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(selection.Json.Value.GetProperty("properties").GetProperty("id").GetProperty("type").GetString())
            .IsEqualTo("string");
        await Assert.That(Encoding.UTF8.GetString(selection.Document.Span)).IsEqualTo(schema);
        await Assert.That(selection.References.Count).IsEqualTo(0);
    }
}
