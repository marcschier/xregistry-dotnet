using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciWriterTests
{
    [Test]
    [Arguments("offline", 4)]
    [Arguments("linked", 3)]
    public async Task WriterPreservesIndependentFixtureRecordsAndExactDocuments(string reference, int documents)
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load(), reference);
        var input = new OciSnapshotInput(capture.Records, capture.Documents);
        var package = await OciSnapshotWriter.CreateAsync(input, new() { MaxDescriptorsPerIndex = 2 });
        await Assert.That(package.Validation.Documents).IsEqualTo(documents);
        await Assert.That(package.Validation.Configs).IsEqualTo(25);
        await using var snapshot = await OciSnapshot.OpenAsync(package.CreateReader(), package.RootDigest);
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, "/imports/shared/files/alias"));
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"string\"}\n");
        var binary = await snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/binary"));
        await Assert.That(Convert.ToHexString(await Check.Bytes(binary.Document!))).IsEqualTo("00017F80FF0D0A");
        var empty = await snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/empty"));
        await Assert.That(empty.Document!.Length).IsEqualTo(0L);
        await Assert.That(snapshot.ResolvedModelSource.GetProperty("groups").GetProperty("imports").GetProperty("ximportresources")[0].GetString())
            .IsEqualTo("/dirs/files");
    }

    [Test]
    public async Task EquivalentInputsProduceIdenticalRootsAndInputOwnershipPreventsTornCaptures()
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load());
        var input = new OciSnapshotInput(capture.Records, capture.Documents);
        var reordered = capture.Records.AsEnumerable().Reverse().Select(record =>
        {
            var node = ReverseObjects(JsonNode.Parse(record.RootElement.GetRawText())!);
            return RegistryJson.Parse(node.ToJsonString());
        }).ToList();
        var equivalent = new OciSnapshotInput(reordered, capture.Documents);
        capture.Records.Clear();
        capture.Documents.Clear();
        var first = await OciSnapshotWriter.CreateAsync(input);
        var second = await OciSnapshotWriter.CreateAsync(equivalent);
        await Assert.That(first.RootDigest).IsEqualTo(second.RootDigest);
        await Assert.That(string.Join(",", first.Objects.Select(o => o.Digest)))
            .IsEqualTo(string.Join(",", second.Objects.Select(o => o.Digest)));
        await Assert.That(first.Validation.Documents).IsEqualTo(4);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("extra")]
    [Arguments("metadata-only")]
    public async Task WriterNeverSubstitutesPlaceholderBytesForMissingOrUnexpectedDocuments(string mutation)
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load());
        if (mutation == "missing") { capture.Documents.Remove(OciDocumentTests.Sample + "/versions/v1"); }
        else
        {
            capture.Documents.Add(mutation == "extra" ? "/dirs/main/files/missing/versions/v1" : "/dirs/main/notes/info/versions/v1",
                new FederationDocument("{}"u8));
        }
        await Check.Error(() => OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents)).AsTask(),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task PublisherCommitsReferenceOnlyAfterEveryBlobAndChildManifest()
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load());
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents));
        var sink = new RecordingPublisher();
        await package.PublishAsync(sink, "release");
        await Assert.That(sink.Reference).IsEqualTo("release");
        await Assert.That(sink.Calls[^1]).IsEqualTo("reference release");
        await Assert.That(sink.Calls[^2]).IsEqualTo("manifest " + package.RootDigest);
        var failed = new RecordingPublisher { FailAt = 4 };
        await Check.Error(() => package.PublishAsync(failed, "release").AsTask(), FederationErrorCode.Unavailable);
        await Assert.That(failed.Reference).IsNull();
        await Assert.That(failed.Calls.Count).IsEqualTo(4);
    }

    [Test]
    [Arguments("offline")]
    [Arguments("linked")]
    public async Task IndependentPythonOracleAcceptsProducedLayouts(string reference)
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load(), reference);
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents),
            new() { MaxDescriptorsPerIndex = 2 });
        await WithProducedLayoutAsync(package, reference, async folder =>
        {
            using var result = await RunPythonOracleAsync("Oracle", "validate", folder, reference);
            await Assert.That(result.RootElement.GetProperty("root").GetString()).IsEqualTo(package.RootDigest);
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IndependentCorrectedPythonOracleResolvesEscapedProducedDocument(bool encodedInput)
    {
        const string target = "/%64irs/%67roup%3aone/%66iles/%69tem%40stable/%76ersions/%76%3a1";
        const string canonical = "/dirs/group%3Aone/files/item%40stable/versions/v%3A1";
        var capture = GraphFixture.Capture(OciXidEncodingTests.RenamedFixture(encodedInput));
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents),
            new() { MaxDescriptorsPerIndex = 2 });
        await WithProducedLayoutAsync(package, "escaped", async folder =>
        {
            using var validation = await RunPythonOracleAsync("CorrectedOracle", "validate", folder, "escaped");
            await Assert.That(validation.RootElement.GetProperty("root").GetString()).IsEqualTo(package.RootDigest);
            using var result = await RunPythonOracleAsync("CorrectedOracle", "lookup", folder, "escaped",
                target, "--operation", "document");
            var document = result.RootElement;
            await Assert.That(document.GetProperty("snapshot").GetString()).IsEqualTo(package.RootDigest);
            await Assert.That(document.GetProperty("target").GetString()).IsEqualTo(target);
            await Assert.That(document.GetProperty("resolved").GetString()).IsEqualTo(canonical);
            await Assert.That(document.GetProperty("size").GetInt32()).IsEqualTo(18);
            await Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(document.GetProperty("base64").GetString()!)))
                .IsEqualTo("{\"type\":\"string\"}\n");
        });
    }

    private static async Task WithProducedLayoutAsync(OciSnapshotPackage package, string reference, Func<string, Task> verify)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "ProducedLayouts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "blobs", "sha256"));
        try
        {
            foreach (var item in package.Objects)
            {
                await using var input = item.OpenRead();
                await using var output = File.Create(Path.Combine(folder, "blobs", "sha256", item.Digest[7..]));
                await input.CopyToAsync(output);
            }
            await File.WriteAllBytesAsync(Path.Combine(folder, "oci-layout"), """{"imageLayoutVersion":"1.0.0"}"""u8.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(folder, "index.json"), await Check.Bytes(package.CreateLayoutEntryPoint(reference)));
            await verify(folder);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task<JsonDocument> RunPythonOracleAsync(
        string oracle, string command, string folder, string reference, params string[] arguments)
    {
        var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, oracle, "tools", "oci_examples.py"));
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(folder);
        start.ArgumentList.Add("--reference");
        start.ArgumentList.Add(reference);
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Independent Python oracle did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        var error = await stderr;
        await Assert.That(process.ExitCode).IsEqualTo(0).Because(error);
        return JsonDocument.Parse(await stdout);
    }

    private static JsonNode ReverseObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var property in obj.Reverse()) { result.Add(property.Key, property.Value is null ? null : ReverseObjects(property.Value)); }
            return result;
        }
        if (node is JsonArray array) { return new JsonArray(array.Select(n => n is null ? null : ReverseObjects(n)).ToArray()); }
        return node.DeepClone();
    }
}
