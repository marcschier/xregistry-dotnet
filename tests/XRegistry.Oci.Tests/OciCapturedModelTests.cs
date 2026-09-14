using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciCapturedModelTests
{
    [Test]
    [Arguments("""{"$include":"https://never-fetch.invalid/model","description":"authored"}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model","groups":{}}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model#/%ff"}""")]
    [Arguments("""{"$include":"https://never-fetch.invalid/model#/%GG"}""")]
    public async Task OriginalSourceContradictionsAndMalformedPointersFailBeforeReadingEntityContent(string original)
    {
        var tree = MemoryLayout.Load();
        GraphFixture.Record(tree, "/", record => record["entity"]!["modelsource"] = JsonNode.Parse(original));

        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.InvalidPackage);
        await Assert.That(tree.Reads.Count).IsEqualTo(5);
    }
}
