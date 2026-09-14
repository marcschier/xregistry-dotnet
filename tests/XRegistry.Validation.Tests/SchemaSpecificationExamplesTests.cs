using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class SchemaSpecificationExamplesTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EveryPublishedProtobufSchemaExampleIsValid(int index)
    {
        var documents = ReadDocuments("schema.md");
        await Assert.That(documents.Length).IsEqualTo(4);
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", Encoding.UTF8.GetBytes(documents[index]));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid)
            .Because(string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ": " + diagnostic.Detail)));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OriginalProtobufExamplesRemainRejectedForTheUnmatchedBrace(int index)
    {
        var documents = ReadDocuments("original.md");
        await Assert.That(documents.Length).IsEqualTo(4);
        var result = await new BuiltInDocumentValidator().ValidateAsync("Protobuf/3", Encoding.UTF8.GetBytes(documents[index]));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("protobuf.brace");
    }

    private static string[] ReadDocuments(string file) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "SchemaExamples", file))
        .Select(static line => line.Trim())
        .Where(static line => line.StartsWith("\"schema\": \"", StringComparison.Ordinal))
        .Select(static line =>
        {
            using var document = JsonDocument.Parse(line["\"schema\": ".Length..].TrimEnd(','));
            return document.RootElement.GetString()!;
        }).ToArray();
}
