using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.File;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;
using IOFile = System.IO.File;

namespace XRegistry.File.Tests;

public class FileOciLayoutPublicationTests
{
    [Test]
    public async Task UnrelatedEntryMetadataAndTopLevelAnnotationsArePreservedWithoutAcquisition()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "stable");
            var path = Path.Combine(root.FullName, "index.json");
            var index = JsonNode.Parse(IOFile.ReadAllBytes(path))!;
            var other = new JsonObject
            {
                ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
                ["artifactType"] = "application/example",
                ["digest"] = "sha256:" + new string('a', 64),
                ["size"] = 999,
                ["annotations"] = new JsonObject { ["org.opencontainers.image.ref.name"] = "unrelated", ["example.note"] = "preserve" },
            };
            index["manifests"]!.AsArray().Add((JsonNode)other);
            index["annotations"] = new JsonObject { ["example.layout"] = "retained" };
            var expected = other.ToJsonString();
            IOFile.WriteAllText(path, index.ToJsonString());
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "added");
            var result = JsonNode.Parse(IOFile.ReadAllBytes(path))!;
            await Assert.That(result["manifests"]!.AsArray().Count).IsEqualTo(3);
            await Assert.That(result["manifests"]![1]!.ToJsonString()).IsEqualTo(expected);
            await Assert.That(result["annotations"]!["example.layout"]!.GetValue<string>()).IsEqualTo("retained");
            await Assert.That(IOFile.Exists(Path.Combine(root.FullName, "blobs", "sha256", new string('a', 64)))).IsFalse();
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task OutputByteLimitIsCheckedAfterMergeAndBeforeAnyReferenceUpdate()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "a");
            var path = Path.Combine(root.FullName, "index.json");
            var before = IOFile.ReadAllBytes(path);
            await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                new string('r', 128), options: new() { MaxEntryPointBytes = before.Length }).AsTask(), FederationErrorCode.LimitExceeded);
            await Assert.That(Convert.ToHexString(IOFile.ReadAllBytes(path))).IsEqualTo(Convert.ToHexString(before));
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments("[]")]
    [Arguments("{\"schemaVersion\":2,\"schemaVersion\":2,\"manifests\":[]}")]
    [Arguments("{\"schemaVersion\":2,\"manifests\":[{\"mediaType\":\"application/example\",\"digest\":\"sha256:aa\",\"size\":-1}]}")]
    public async Task MalformedExistingControlsNeverBecomeFreshLayouts(string badIndex)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            IOFile.WriteAllText(Path.Combine(root.FullName, "oci-layout"), """{"imageLayoutVersion":"1.0.0"}""");
            var path = Path.Combine(root.FullName, "index.json");
            IOFile.WriteAllText(path, badIndex);
            await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                "release").AsTask(), FederationErrorCode.InvalidPackage);
            await Assert.That(IOFile.ReadAllText(path)).IsEqualTo(badIndex);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments("index.json")]
    [Arguments("oci-layout")]
    public async Task ObservedControlChangesBeforeCommitCannotBeOverwritten(string changedName)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "stable");
            var path = Path.Combine(root.FullName, changedName);
            var before = IOFile.ReadAllText(path);
            var changed = before + " ";
            await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                "new", options: new()
                {
                    Checkpoint = (stage, _) =>
                    {
                        if (stage == FileOciLayoutPublicationStage.BeforeReferenceCommit) { IOFile.WriteAllText(path, changed); }
                        return ValueTask.CompletedTask;
                    },
                }).AsTask(), FederationErrorCode.InconsistentSnapshot);
            await Assert.That(IOFile.ReadAllText(path)).IsEqualTo(changed);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task PrecancelledPublicationAndExpiredCheckpointCannotAcknowledgeAReference()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.That(async () =>
            {
                await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                "release", cancellationToken: cancelled.Token);
            }).Throws<OperationCanceledException>();
            await Assert.That(Directory.EnumerateFileSystemEntries(root.FullName).Any()).IsFalse();
            var failure = await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package,
                FileIntegrationFixture.Uri(root.FullName), "release", options: new()
                {
                    Timeout = TimeSpan.FromMilliseconds(100),
                    Checkpoint = async (_, cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                }).AsTask(), FederationErrorCode.Unavailable);
            await Assert.That(failure.Diagnostic).IsEqualTo("publication_timeout");
            await Assert.That(IOFile.Exists(Path.Combine(root.FullName, "index.json"))).IsFalse();
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "release");
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task PublicationOutsideTheApprovedBoundaryHasNoFilesystemSideEffects()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        var outside = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(outside.FullName),
                "release", FileIntegrationFixture.Uri(root.FullName)).AsTask(), FederationErrorCode.PolicyDenied);
            await Assert.That(Directory.EnumerateFileSystemEntries(outside.FullName).Any()).IsFalse();
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ExistingReferencesArePreservedAndSameReferenceMovesAtomically()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var first = await FileIntegrationFixture.Package();
            var linked = await FileIntegrationFixture.Package("linked");
            var updated = await FileIntegrationFixture.Package(registryId: "updated-offline");
            await FileOciLayout.PublishAsync(first, FileIntegrationFixture.Uri(root.FullName), "stable");
            await using var pinned = await FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(root.FullName),
                FileRegistryLayout.OciLayout, "stable");
            var added = await FileOciLayout.PublishAsync(linked, FileIntegrationFixture.Uri(root.FullName), "linked");
            await Assert.That(added.ObjectsReused > 0).IsTrue();
            await FileOciLayout.PublishAsync(updated, FileIntegrationFixture.Uri(root.FullName), "stable");
            using var index = JsonDocument.Parse(IOFile.ReadAllBytes(Path.Combine(root.FullName, "index.json")));
            await Assert.That(index.RootElement.GetProperty("manifests").GetArrayLength()).IsEqualTo(2);
            using var tree = FileDocumentTreeReader.Open(FileIntegrationFixture.Uri(root.FullName));
            await using var current = await OciSnapshot.OpenLayoutAsync(tree, "stable");
            await using var other = await OciSnapshot.OpenLayoutAsync(tree, "linked");
            await Assert.That(current.RootDigest).IsEqualTo(updated.RootDigest);
            await Assert.That(other.RootDigest).IsEqualTo(linked.RootDigest);
            var old = await pinned.ReadAsync(new(FederationOperation.Entity, "/"));
            await Assert.That(old.Metadata.GetProperty("entity").GetProperty("registryid").GetString()).IsEqualTo("fixture-offline");
            var latest = await current.ReadAsync(new(FederationOperation.Entity, "/"));
            await Assert.That(latest.Value.GetProperty("registryid").GetString()).IsEqualTo("updated-offline");
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task OneDirectoryHasOneActiveWriter()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var package = await FileIntegrationFixture.Package();
            var first = FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "first",
                options: new()
                {
                    Checkpoint = async (stage, cancellationToken) =>
                    {
                        if (stage != FileOciLayoutPublicationStage.WriterLocked) { return; }
                        acquired.SetResult();
                        await release.Task.WaitAsync(cancellationToken);
                    },
                }).AsTask();
            await acquired.Task;
            try
            {
                var failure = await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package,
                    FileIntegrationFixture.Uri(root.FullName), "second").AsTask(), FederationErrorCode.Unavailable);
                await Assert.That(failure.Diagnostic).IsEqualTo("writer_busy");
                await Assert.That(IOFile.Exists(Path.Combine(root.FullName, "index.json"))).IsFalse();
            }
            finally { release.SetResult(); }
            await first;
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "second");
        }
        finally
        {
            release.TrySetResult();
            root.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments(FileOciLayoutPublicationStage.BeforeReferenceCommit, false)]
    [Arguments(FileOciLayoutPublicationStage.ReferenceReplaced, true)]
    [Arguments(FileOciLayoutPublicationStage.ReferenceDurable, true)]
    public async Task InjectedFailuresRetainOldSelectionOrReportUnknownAcknowledgement(FileOciLayoutPublicationStage stop, bool unknown)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var first = await FileIntegrationFixture.Package();
            var updated = await FileIntegrationFixture.Package(registryId: "replacement");
            await FileOciLayout.PublishAsync(first, FileIntegrationFixture.Uri(root.FullName), "stable");
            var failure = await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(updated,
                FileIntegrationFixture.Uri(root.FullName), "stable", options: new()
                {
                    Checkpoint = (stage, _) => stage == stop
                        ? ValueTask.FromException(new IOException("Injected publication checkpoint failure."))
                        : ValueTask.CompletedTask,
                }).AsTask(), FederationErrorCode.Unavailable);
            await Assert.That(failure.Diagnostic == "publication_ack_unknown").IsEqualTo(unknown);
            using var tree = FileDocumentTreeReader.Open(FileIntegrationFixture.Uri(root.FullName));
            await using var selected = await OciSnapshot.OpenLayoutAsync(tree, "stable");
            await Assert.That(selected.RootDigest).IsEqualTo(unknown ? updated.RootDigest : first.RootDigest);
            await selected.ValidateAsync();
            await Assert.That(Directory.EnumerateFiles(root.FullName, ".xregistry-oci-*.tmp", SearchOption.AllDirectories).Any()).IsFalse();
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationBeforeAndAfterReferenceReplacementHasDistinctOutcomes(bool afterReplacement)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var package = await FileIntegrationFixture.Package();
            var options = new FileOciLayoutPublicationOptions
            {
                Checkpoint = (stage, _) =>
                {
                    if (stage == (afterReplacement ? FileOciLayoutPublicationStage.ReferenceReplaced :
                        FileOciLayoutPublicationStage.BeforeReferenceCommit)) { cancellation.Cancel(); }
                    return ValueTask.CompletedTask;
                },
            };
            if (afterReplacement)
            {
                var failure = await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package,
                    FileIntegrationFixture.Uri(root.FullName), "release", options: options, cancellationToken: cancellation.Token).AsTask(),
                    FederationErrorCode.Unavailable);
                await Assert.That(failure.Diagnostic).IsEqualTo("publication_ack_unknown");
            }
            else
            {
                await Assert.That(async () =>
                {
                    await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                    "release", options: options, cancellationToken: cancellation.Token);
                }).Throws<OperationCanceledException>();
            }
            await Assert.That(IOFile.Exists(Path.Combine(root.FullName, "index.json"))).IsEqualTo(afterReplacement);
            await Assert.That(Directory.EnumerateFiles(root.FullName, ".xregistry-oci-*.tmp", SearchOption.AllDirectories).Any()).IsFalse();
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task EntryPointDescriptorLimitPreservesAll256EntriesAndRejectsTheNextReference()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "stable");
            var path = Path.Combine(root.FullName, "index.json");
            var index = JsonNode.Parse(IOFile.ReadAllBytes(path))!;
            var entries = index["manifests"]!.AsArray();
            for (var i = 1; i < 256; i++)
            {
                var entry = entries[0]!.DeepClone();
                entry["annotations"]!["org.opencontainers.image.ref.name"] = "retained-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                entries.Add(entry);
            }
            IOFile.WriteAllText(path, index.ToJsonString());
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "stable");
            var before = IOFile.ReadAllBytes(path);
            await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                "overflow").AsTask(), FederationErrorCode.LimitExceeded);
            await Assert.That(Convert.ToHexString(IOFile.ReadAllBytes(path))).IsEqualTo(Convert.ToHexString(before));
            using var result = JsonDocument.Parse(before);
            await Assert.That(result.RootElement.GetProperty("manifests").GetArrayLength()).IsEqualTo(256);
            await Assert.That(result.RootElement.GetProperty("manifests")[255].GetProperty("annotations")
                .GetProperty("org.opencontainers.image.ref.name").GetString()).IsEqualTo("retained-255");
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments(1_048_576, false)]
    [Arguments(1_048_577, true)]
    public async Task ExistingEntryPointExactByteLimitIncludesWhitespace(int size, bool exceeds)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "stable");
            var path = Path.Combine(root.FullName, "index.json");
            var json = IOFile.ReadAllBytes(path);
            var padded = new byte[size];
            json.CopyTo(padded, 0);
            padded.AsSpan(json.Length).Fill((byte)' ');
            padded[^1] = (byte)'\n';
            IOFile.WriteAllBytes(path, padded);
            if (exceeds)
            {
                await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                    "stable").AsTask(), FederationErrorCode.LimitExceeded);
                await Assert.That(new FileInfo(path).Length).IsEqualTo(size);
            }
            else
            {
                var receipt = await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "stable");
                await Assert.That(receipt.Context.Revision).IsEqualTo(package.RootDigest);
                await Assert.That(new FileInfo(path).Length < size).IsTrue();
            }
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task DuplicateSelectedTagsAreAmbiguousRatherThanSilentlyRepaired()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "stable");
            var path = Path.Combine(root.FullName, "index.json");
            var index = JsonNode.Parse(IOFile.ReadAllBytes(path))!;
            index["manifests"]!.AsArray().Add(index["manifests"]![0]!.DeepClone());
            var before = index.ToJsonString();
            IOFile.WriteAllText(path, before);
            await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                "stable").AsTask(), FederationErrorCode.Ambiguous);
            await Assert.That(IOFile.ReadAllText(path)).IsEqualTo(before);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task FreshLayoutPublishesExactBytesAndDurableReference()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            var stages = new List<FileOciLayoutPublicationStage>();
            var result = await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "release",
                options: new()
                {
                    Checkpoint = (stage, _) =>
                    {
                        stages.Add(stage);
                        if (stage == FileOciLayoutPublicationStage.BeforeReferenceCommit &&
                            IOFile.Exists(Path.Combine(root.FullName, "index.json")))
                        {
                            throw new InvalidOperationException("The selected entrypoint was published before its commit stage.");
                        }
                        return ValueTask.CompletedTask;
                    },
                });
            await Assert.That(result.Context.Revision).IsEqualTo(package.RootDigest);
            await Assert.That(result.Context.Source).IsEqualTo(FileIntegrationFixture.Uri(root.FullName).AbsoluteUri);
            await Assert.That(result.Context.RequestedRevision).IsEqualTo("release");
            await Assert.That(stages.IndexOf(FileOciLayoutPublicationStage.ObjectsDurable) <
                stages.IndexOf(FileOciLayoutPublicationStage.ReferenceReplaced)).IsTrue();
            await Assert.That(stages[^1]).IsEqualTo(FileOciLayoutPublicationStage.ReferenceDurable);
            using var tree = FileDocumentTreeReader.Open(FileIntegrationFixture.Uri(root.FullName));
            await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "release");
            var closure = await snapshot.ValidateAsync();
            await Assert.That(closure.Objects).IsEqualTo(package.Validation.Objects);
            await Assert.That(closure.Documents).IsEqualTo(4);
            var binary = await snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/binary"));
            await Assert.That(await FileIntegrationFixture.Hex(binary.Document!)).IsEqualTo("00017F80FF0D0A");
            var empty = await snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/empty"));
            await Assert.That(empty.Document!.Length).IsEqualTo(0L);
            var defaultDocument = await snapshot.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/sample"));
            await Assert.That(await FileIntegrationFixture.Hex(defaultDocument.Document!)).IsEqualTo("7B2274797065223A22737472696E67227D0A");
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExistingCorruptDigestBytesAreNeverOverwrittenOrPublished(bool sameSize)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package();
            var blobs = Directory.CreateDirectory(Path.Combine(root.FullName, "blobs", "sha256"));
            var corrupt = Path.Combine(blobs.FullName, sameSize ?
                "47a8404dd5bb287e70354f4aa0f7bf250e4fefe66a72cf2ac5475a802fd45968" :
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
            IOFile.WriteAllBytes(corrupt, sameSize ? [0, 1, 0x7f, 0x80, 0xfe, 0x0d, 0x0a] : [1, 2, 3]);
            await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                "release").AsTask(), FederationErrorCode.IntegrityError);
            await Assert.That(Convert.ToHexString(IOFile.ReadAllBytes(corrupt))).IsEqualTo(sameSize ? "00017F80FE0D0A" : "010203");
            await Assert.That(IOFile.Exists(Path.Combine(root.FullName, "index.json"))).IsFalse();
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task PublicationNeverFollowsAReparseBlobDirectory()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        var outside = FileIntegrationFixture.CreateDirectory();
        var link = Path.Combine(root.FullName, "blobs");
        try
        {
            await FileIntegrationFixture.CreateDirectoryLink(link, outside.FullName);
            var package = await FileIntegrationFixture.Package();
            await FileIntegrationFixture.Error(() => FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName),
                "release").AsTask(), FederationErrorCode.PolicyDenied);
            await Assert.That(Directory.EnumerateFileSystemEntries(outside.FullName).Any()).IsFalse();
            await Assert.That(IOFile.Exists(Path.Combine(root.FullName, "index.json"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(link)) { Directory.Delete(link); }
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments("offline")]
    [Arguments("linked")]
    public async Task IndependentPythonOracleAcceptsActuallyPublishedLayout(string fixtureReference)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var package = await FileIntegrationFixture.Package(fixtureReference);
            await FileOciLayout.PublishAsync(package, FileIntegrationFixture.Uri(root.FullName), "published");
            var start = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[]
            {
                "-B", Path.Combine(FileIntegrationFixture.Corpus, "tools", "oci_examples.py"),
                "validate", root.FullName, "--reference", "published",
            }) { start.ArgumentList.Add(argument); }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The independent oracle did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            await Assert.That(process.ExitCode).IsEqualTo(0).Because(await stderr);
            using var report = JsonDocument.Parse(await stdout);
            await Assert.That(report.RootElement.GetProperty("root").GetString()).IsEqualTo(package.RootDigest);
            await Assert.That(report.RootElement.GetProperty("documents").GetInt32()).IsEqualTo(fixtureReference == "offline" ? 4 : 3);
        }
        finally { root.Delete(recursive: true); }
    }
}
