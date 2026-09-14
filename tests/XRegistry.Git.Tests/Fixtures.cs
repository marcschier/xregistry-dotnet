using System.Text.Json;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Git.Tests;

internal static class Fixtures
{
    internal static string Id(GitHashAlgorithm algorithm, string name) => ObjectField(algorithm, name, "oid");

    internal static byte[] Content(GitHashAlgorithm algorithm, string name) => Convert.FromHexString(ObjectField(algorithm, name, "content"));

    internal static byte[] Loose(GitHashAlgorithm algorithm, string name) => Convert.FromHexString(ObjectField(algorithm, name, "loose"));

    internal static byte[] Pack(GitHashAlgorithm algorithm, string name)
    {
        using var document = Open();
        return Convert.FromHexString(Format(document, algorithm).GetProperty("packs").GetProperty(name).GetProperty("hex").GetString()!);
    }

    internal static byte[] Delta(GitHashAlgorithm algorithm, string name = "delta")
    {
        using var document = Open();
        return Convert.FromHexString(Format(document, algorithm).GetProperty(name).GetString()!);
    }

    private static string ObjectField(GitHashAlgorithm algorithm, string name, string field)
    {
        using var document = Open();
        return Format(document, algorithm).GetProperty("objects").GetProperty(name).GetProperty(field).GetString()!;
    }

    private static JsonElement Format(JsonDocument document, GitHashAlgorithm algorithm)
        => document.RootElement.GetProperty("formats").GetProperty(algorithm == GitHashAlgorithm.Sha1 ? "sha1" : "sha256");

    private static JsonDocument Open()
    {
        using var stream = typeof(Fixtures).Assembly.GetManifestResourceStream("XRegistry.Git.Tests.Fixtures.git-fixtures.json")
            ?? throw new InvalidOperationException("The independent Git fixture resource is missing.");
        return JsonDocument.Parse(stream);
    }
}
