using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryModelImportTests
{
    [Test]
    public async Task ResolvesIncludesWithLocalAndEarlierPrecedence()
    {
        var source = RegistryJson.Parse("""
            {"attributes":{"$includes":["first.json#/attributes","second.json#/attributes"],
             "local":{"type":"string","description":"local wins"}}}
            """);
        var resolver = new Documents(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://example.test/models/first.json"] =
                """{"attributes":{"local":{"type":"boolean"},"a":{"type":"integer","description":"first wins"}}}""",
            ["https://example.test/models/second.json"] =
                """{"attributes":{"a":{"type":"string"},"b":{"type":"boolean"}}}"""
        });
        var model = RegistryModel.Compile(source, new()
        {
            SourceUri = new Uri("https://example.test/models/root.json"),
            Resolver = resolver
        });
        await Assert.That(model.Attributes["local"].Type).IsEqualTo(RegistryValueType.String);
        await Assert.That(model.Attributes["a"].Type).IsEqualTo(RegistryValueType.Integer);
        await Assert.That(model.Attributes["a"].Description).IsEqualTo("first wins");
        await Assert.That(model.Attributes["b"].Type).IsEqualTo(RegistryValueType.Boolean);
        await Assert.That(model.Source.RootElement.GetProperty("attributes").GetProperty("$includes").GetArrayLength()).IsEqualTo(2);
        await Assert.That(model.EffectiveModel.RootElement.GetProperty("attributes").TryGetProperty("$includes", out _)).IsFalse();
    }

    [Test]
    public async Task ResolvesRelativeIncludesAgainstTheirOwnDocument()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""{"$include":"parts/first.json"}"""), new()
        {
            SourceUri = new Uri("https://example.test/root.json"),
            Resolver = new Documents(new(StringComparer.Ordinal)
            {
                ["https://example.test/parts/first.json"] = """{"attributes":{"$include":"../common.json#/a~1b~0c"}}""",
                ["https://example.test/common.json"] = """{"a/b~c":{"answer":"integer"}}"""
            })
        });
        await Assert.That(model.Attributes["answer"].Type).IsEqualTo(RegistryValueType.Integer);
    }

    [Test]
    public async Task ResolvesInDocumentIncludesWithoutAResolver()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"as":{"singular":"a","resources":{"docs":{"singular":"doc"}}},
              "bs":{"singular":"b","resources":{"docs":{"$include":"#/groups/as/resources/docs"}}}}}
            """));
        await Assert.That(model.Groups["bs"].Resources["docs"].Singular).IsEqualTo("doc");
        await Assert.That(model.Groups["bs"].Resources["docs"].HasDocument).IsTrue();
    }

    [Test]
    [Arguments("""{"$include":"https://example.test/model.json"}""", "model_resolution_required", "/$include")]
    [Arguments("""{"$include":"other.json"}""", "model_base_uri_required", "/$include")]
    [Arguments("""{"$include":"#"}""", "model_include_cycle", "/$include")]
    [Arguments("""{"$include":"#groups"}""", "invalid_json_pointer", "/$include")]
    [Arguments("""{"$include":"#/%ff"}""", "invalid_json_pointer", "/$include")]
    [Arguments("""{"$include":"#/a~2b"}""", "invalid_json_pointer", "/$include")]
    [Arguments("""{"$include":"#/missing"}""", "model_reference_not_found", "/$include")]
    [Arguments("""{"$include":"#/description","description":"text"}""", "model_error", "/$include")]
    [Arguments("""{"$include":1}""", "model_error", "/$include")]
    [Arguments("""{"$includes":"x"}""", "model_error", "/$includes")]
    [Arguments("""{"$include":"x","$includes":[]}""", "model_error", "")]
    public async Task RejectsUnresolvedOrInvalidIncludeConstructs(string source, string code, string path)
    {
        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(source)));
        await Assert.That(diagnostic.Code).IsEqualTo(code);
        await Assert.That(diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    public async Task RejectsIncludeCyclesAndBudgets()
    {
        var options = new RegistryModelCompilationOptions
        {
            SourceUri = new Uri("https://example.test/root.json"),
            Resolver = new Documents(new(StringComparer.Ordinal)
            {
                ["https://example.test/first.json"] = """{"$include":"second.json"}""",
                ["https://example.test/second.json"] = """{"attributes":{"answer":"integer"}}"""
            }),
            MaxIncludeDocuments = 2,
            MaxIncludeDepth = 2
        };
        var source = RegistryJson.Parse("""{"$include":"first.json"}""");
        await Assert.That(RegistryModel.Compile(source, options).Attributes["answer"].Type).IsEqualTo(RegistryValueType.Integer);
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(source, options with { MaxIncludeDocuments = 1 })).Code)
            .IsEqualTo("model_expansion_limit");
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(source, options with { MaxIncludeDepth = 1 })).Code)
            .IsEqualTo("model_expansion_limit");
        await Assert.That(RegistryModel.Compile(RegistryJson.Parse("{}"), new() { MaxExpandedNodes = 1 }).Groups.Count).IsEqualTo(0);
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse("""{"groups":{}}"""),
            new() { MaxExpandedNodes = 1 })).Code).IsEqualTo("model_expansion_limit");
        var cycle = options with
        {
            Resolver = new Documents(new(StringComparer.Ordinal)
            {
                ["https://example.test/first.json"] = """{"$include":"second.json"}""",
                ["https://example.test/second.json"] = """{"$include":"first.json"}"""
            })
        };
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(source, cycle)).Code).IsEqualTo("model_include_cycle");
    }

    [Test]
    public async Task ImportsTransitiveResourcesWithoutChangingSource()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{
              "first":{"singular":"one","resources":{"docs":{"singular":"doc","hasdocument":false}}},
              "second":{"singular":"two","ximportresources":["/first/docs"]},
              "third":{"singular":"three","ximportresources":["/second/docs"]}
            }}
            """));
        await Assert.That(model.Groups["third"].Resources["docs"].HasDocument).IsFalse();
        await Assert.That(model.Groups["third"].Attributes["docscount"].Type).IsEqualTo(RegistryValueType.UInteger);
        await Assert.That(model.Source.RootElement.GetProperty("groups").GetProperty("third")
            .GetProperty("ximportresources")[0].GetString()).IsEqualTo("/second/docs");
        await Assert.That(model.EffectiveModel.RootElement.GetProperty("groups").GetProperty("third")
            .TryGetProperty("ximportresources", out _)).IsFalse();
    }

    [Test]
    public async Task CrossGroupImportsOfLocallyDefinedTypesAreNotACircularDefinition()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{
              "as":{"singular":"a","resources":{"xs":{"singular":"item"}},"ximportresources":["/bs/ys"]},
              "bs":{"singular":"b","resources":{"ys":{"singular":"other"}},"ximportresources":["/as/xs"]}
            }}
            """));

        await Assert.That(model.Groups["as"].Resources.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(model.Groups["as"].Resources["ys"], model.Groups["bs"].Resources["ys"])).IsTrue();
        await Assert.That(ReferenceEquals(model.Groups["as"].Resources["xs"], model.Groups["bs"].Resources["xs"])).IsTrue();
    }

    [Test]
    [Arguments("""{"as":{"singular":"a","ximportresources":["/as/xs"]}}""", "model_error")]
    [Arguments("""{"as":{"singular":"a","ximportresources":["/missing/xs"]}}""", "model_reference_not_found")]
    [Arguments("""{"as":{"singular":"a","ximportresources":["/bs/xs"]},"bs":{"singular":"b","ximportresources":["/as/xs"]}}""", "model_import_cycle")]
    [Arguments("""{"as":{"singular":"a","ximportresources":["/bs/xs","/bs/xs"]},"bs":{"singular":"b","resources":{"xs":{"singular":"item"}}}}""", "model_error")]
    [Arguments("""{"as":{"singular":"a","resources":{"xs":{"singular":"item"}},"ximportresources":["/bs/xs"]},"bs":{"singular":"b","resources":{"xs":{"singular":"item"}}}}""", "model_error")]
    [Arguments("""{"as":{"singular":"a","resources":{"xs":{"singular":"same"}},"ximportresources":["/bs/ys"]},"bs":{"singular":"b","resources":{"ys":{"singular":"same"}}}}""", "model_error")]
    [Arguments("""{"as":{"singular":"a","ximportresources":[1]}}""", "model_error")]
    [Arguments("""{"as":{"singular":"a","ximportresources":"/bs/xs"}}""", "model_error")]
    public async Task InvalidImportGraphsHaveExplicitFailures(string groups, string code)
    {
        var source = RegistryJson.Parse("{\"groups\":" + groups + "}");
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(source)).Code).IsEqualTo(code);
    }

    [Test]
    public async Task ResourceImportDepthIsIndependentlyBounded()
    {
        var source = RegistryJson.Parse("""
            {"groups":{
              "as":{"singular":"a","ximportresources":["/bs/xs"]},
              "bs":{"singular":"b","ximportresources":["/cs/xs"]},
              "cs":{"singular":"c","resources":{"xs":{"singular":"item"}}}
            }}
            """);
        await Assert.That(RegistryModel.Compile(source, new() { MaxResourceImportDepth = 2 })
            .Groups["as"].Resources["xs"].Singular).IsEqualTo("item");
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(source,
            new() { MaxResourceImportDepth = 1 })).Code).IsEqualTo("model_expansion_limit");
    }

    internal sealed class Documents(Dictionary<string, string> documents) : IRegistryModelResolver
    {
        public RegistryJson Resolve(Uri documentUri) => RegistryJson.Parse(documents[documentUri.AbsoluteUri]);
    }
}
