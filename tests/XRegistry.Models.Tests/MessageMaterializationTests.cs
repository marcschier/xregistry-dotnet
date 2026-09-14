using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class MessageMaterializationTests
{
    [Test]
    public async Task BaseDefinitionIsDeeplyOverlaidWithoutChangingTheAuthoredIdentity()
    {
        var original = new MessageDefinition(RegistryJson.Parse("""
            {"messageid":"child","basemessage":"https://base.example/messages/base",
             "extension":{"nested":{"override":"child","added":true},"replace":7}}
            """), new Uri("https://child.example/messages/child"), model: ExtensionModel());
        var baseline = new MessageDefinition(RegistryJson.Parse("""
            {"messageid":"base","extension":{"base":1,"nested":{"retained":"base","override":"base"},
             "replace":{"must":"disappear"}}}
            """), new Uri("https://base.example/messages/base"));
        var calls = 0;

        var result = await MessageDefinitionMaterializer.MaterializeAsync(original, (request, _) =>
        {
            calls++;
            if (request.ReferenceText != "https://base.example/messages/base" ||
                request.TargetUri.AbsoluteUri != "https://base.example/messages/base")
            {
                throw new InvalidOperationException("The materializer requested an unexpected definition.");
            }
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(baseline));
        });

        var expected = JsonNode.Parse("""
            {"messageid":"child","basemessage":"https://base.example/messages/base",
             "extension":{"base":1,"nested":{"retained":"base","override":"child","added":true},"replace":7}}
            """);
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(result.Metadata.RootElement.GetRawText()), expected)).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Definitions.Count).IsEqualTo(2);
        await Assert.That(result.Original.Location.AbsoluteUri).IsEqualTo("https://child.example/messages/child");
        await Assert.That(result.Original.Metadata.RootElement.GetProperty("extension").TryGetProperty("base", out _)).IsFalse();
        await Assert.That(result.Definitions[1].Location.AbsoluteUri).IsEqualTo("https://base.example/messages/base");
    }

    internal static RegistryModel ExtensionModel()
    {
        using var packaged = BuiltInRegistryModels.LoadSource(RegistryModelKind.Message);
        var source = JsonNode.Parse(packaged.RootElement.GetRawText())!.AsObject();
        source["groups"]!["messagegroups"]!["resources"]!["messages"]!["attributes"]!["extension"] =
            new JsonObject { ["type"] = "any" };
        return RegistryModel.Compile(RegistryJson.Parse(source.ToJsonString()));
    }
}
