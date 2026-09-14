using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

internal static class Check
{
    internal static async Task Json(JsonElement actual, string expected) =>
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), JsonNode.Parse(expected))).IsTrue();

    internal static async Task<FederationException> Error(Func<Task> action, FederationErrorCode code)
    {
        var exception = await Assert.That(action).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected a federation failure.");
        await Assert.That(exception.Code).IsEqualTo(code);
        return exception;
    }

    internal static async Task<byte[]> Bytes(FederationDocument document)
    {
        await using var input = document.OpenRead();
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }
}
