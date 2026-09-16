// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using XRegistry;
using XRegistry.Bindings.File;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;
using XRegistry.Server;
using XRegistry.Storage.File;

namespace EmbeddingConsumer;

internal static class EmbeddingRuntimeCases
{
    private const string BinaryHex = "00FF0D0A0180007B2278223A317DFE";
    private const string Offline = "sha256:c937f902c54ca9c63e510bab3b4ec07ec3775ad338ac030ac93d916a736efba6";
    private const string Linked = "sha256:dff871378d2678ee851fb7d97bae5a6b69b05c3d668f224a2f97127e8e8dee88";

    internal static async Task RunAsync(EmbeddingReport report, string fixtures, string work, RegistryModel model)
    {
        fixtures = Path.GetFullPath(fixtures);
        work = Path.GetFullPath(work);
        Program.Require(Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any(), "fresh controlled runtime work directory");
        await report.CaseAsync("storage-file-initialize-commit-reopen",
            () => StorageAsync(report, Path.Combine(work, "store"))).ConfigureAwait(false);
        await report.CaseAsync("storage-file-adapter-preserves-document",
            () => StorageAdapterAsync(report, Path.Combine(work, "adapter-store"))).ConfigureAwait(false);
        var boundary = Directory.CreateDirectory(Path.Combine(work, "file-boundary")).FullName;
        var mapping = Path.Combine(boundary, "mapping");
        var oci = Path.Combine(boundary, "oci");
        CopyTree(Path.Combine(fixtures, "mapping"), mapping);
        CopyTree(Path.Combine(fixtures, "oci", "layout"), oci);
        await report.CaseAsync("file-document-tree-authorized-read",
            () => FileMappingAsync(report, mapping, boundary, work)).ConfigureAwait(false);
        await report.CaseAsync("file-oci-layout-explicit-read",
            () => FileOciAsync(report, oci, boundary)).ConfigureAwait(false);
        await report.CaseAsync("git-sha256-verified-pack-tree-blob",
            () => GitAsync(report, Path.Combine(fixtures, "git-fixtures.json"))).ConfigureAwait(false);
        using var expected = JsonDocument.Parse(System.IO.File.ReadAllBytes(Path.Combine(fixtures, "oci", "expected.json")));
        await report.CaseAsync("oci-offline-frozen-closure",
            () => OciAsync(report, oci, "offline", expected.RootElement)).ConfigureAwait(false);
        await report.CaseAsync("oci-linked-frozen-closure",
            () => OciAsync(report, oci, "linked", expected.RootElement)).ConfigureAwait(false);
        await report.CaseAsync("federation-live-http-source", () => FederationAsync(report, model)).ConfigureAwait(false);
        await report.CaseAsync("native-dependencies-no-git-backend", () =>
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                report.NativeModules.Add(module.ModuleName);
                if (module.ModuleName.Contains("sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    using var bytes = System.IO.File.OpenRead(module.FileName);
                    report.RuntimeFacts["sqliteModuleSha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                }
            }
            var git = report.NativeModules.Count(static name =>
                name.StartsWith("libgit2", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("git2.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("git.exe", StringComparison.OrdinalIgnoreCase));
            var sqlite = report.NativeModules.Count(static name => name.Contains("sqlite", StringComparison.OrdinalIgnoreCase));
            Program.Require(git == 0 && sqlite >= 1, "real native SQLite, with no native Git backend loaded");
            report.RuntimeEvidence["gitNativeModules"] = git;
            report.RuntimeEvidence["sqliteNativeModules"] = sqlite;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await report.CaseAsync("federation-escaped-identities", () => EscapedFederationAsync(report, model)).ConfigureAwait(false);
    }

    private static FileStoreLimits StorageLimits() => new()
    {
        MaxRecords = 64, MaxMutations = 16, MaxMetadataBytesPerRecord = 64 * 1024,
        MaxMetadataBytes = 1024 * 1024, MaxDocumentBytes = 64 * 1024,
        MaxDocumentReferences = 16, MaxReferencedDocumentBytes = 1024 * 1024,
        MaxBlobFiles = 32, MaxBlobBytes = 2 * 1024 * 1024,
        MaxTemporaryFiles = 16, MaxTemporaryBytes = 1024 * 1024,
        MaxReadSnapshots = 8, MaxOpenDocumentStreams = 8, MaxDatabaseBytes = 8 * 1024 * 1024
    };

    private static async Task StorageAsync(EmbeddingReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        var bytes = Convert.FromHexString(BinaryHex);
        string digest;
        using (var store = LocalFileStore.Initialize(directory, StorageLimits()))
        {
            Program.Require(store.ReadGeneration() == 0, "new durable store generation");
            using var input = new MemoryStream(bytes, writable: false);
            using var empty = new MemoryStream();
            using (var candidate = await store.PrepareAsync(0,
                [StorageMutation.Put("records/item", """{"revision":1}"""u8.ToArray(), input),
                 StorageMutation.Put("records/empty", "{}"u8.ToArray(), empty),
                 StorageMutation.Put("records/no-document", "{}"u8.ToArray())]).ConfigureAwait(false))
            {
                Program.Require(input.CanRead && empty.CanRead && store.ReadGeneration() == 0, "durable staging retains caller streams and has not committed");
                Program.Require(store.Commit(candidate) == 1, "durable publication");
            }
            using (var before = store.ReadSnapshot("records/item"))
            {
                digest = before.Records.Single().Document!.Sha256;
                using var content = before.OpenDocument("records/item");
                await RequireBytesAsync(content, bytes).ConfigureAwait(false);
            }
            using var preserve = await store.PrepareAsync(1,
                [StorageMutation.PutPreservingDocument("records/item", """{"revision":2}"""u8.ToArray())]).ConfigureAwait(false);
            Program.Require(store.Commit(preserve) == 2, "metadata-only durable publication preserves its Document");
        }
        using var reopened = LocalFileStore.Open(directory, StorageLimits());
        using var snapshot = reopened.ReadSnapshot();
        var item = snapshot.Records.Single(static record => record.Key == "records/item");
        Program.Require(snapshot.Generation == 2 && item.Metadata.GetProperty("revision").GetInt32() == 2 &&
            item.Document!.Sha256 == digest && item.Document.Length == bytes.Length, "reopen retains preserved metadata and exact Document identity");
        using var document = snapshot.OpenDocument("records/item");
        await RequireBytesAsync(document, bytes).ConfigureAwait(false);
        using var emptyDocument = snapshot.OpenDocument("records/empty");
        Program.Require(emptyDocument.ReadByte() == -1 &&
            snapshot.Records.Single(static record => record.Key == "records/empty").Document!.Length == 0 &&
            snapshot.Records.Single(static record => record.Key == "records/no-document").Document is null, "durable empty versus absent Documents");
        report.RuntimeEvidence["storageReopenedGeneration"] = reopened.ReadGeneration();
        report.RuntimeFacts["storageDocumentSha256"] = digest;
    }

    private static async Task StorageAdapterAsync(EmbeddingReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        using var store = LocalFileStore.Initialize(directory, StorageLimits());
        using var adapter = new LocalRegistryPersistence(store, ownsStore: false, maxSnapshots: 4);
        var bytes = Convert.FromHexString(BinaryHex);
        using var input = new MemoryStream(bytes, writable: false);
        using (var candidate = await adapter.PrepareAsync(0,
            [RegistryMutation.PutDocument("application/item", RegistryJson.Parse("""{"revision":1}"""), input)]).ConfigureAwait(false))
        {
            Program.Require(await candidate.CommitAsync().ConfigureAwait(false) == 1 && input.CanRead, "actual LocalRegistryPersistence publication");
        }
        using var first = await adapter.ReadSnapshotAsync().ConfigureAwait(false);
        using var sameGeneration = await adapter.ReadSnapshotAsync().ConfigureAwait(false);
        Program.Require(first.Generation == sameGeneration.Generation &&
            first.Find("application/item")!.Metadata.RootElement.GetProperty("revision").GetInt32() == 1, "stable generation-indexed reads");
        using var oldDocument = first.OpenDocument("application/item");
        using (var candidate = await adapter.PrepareAsync(1,
            [RegistryMutation.Put("application/item", RegistryJson.Parse("""{"revision":2}"""))]).ConfigureAwait(false))
        {
            Program.Require(await candidate.CommitAsync().ConfigureAwait(false) == 2, "adapter uses Document-preserving metadata replacement");
        }
        using var updated = await adapter.ReadSnapshotAsync().ConfigureAwait(false);
        Program.Require(updated.Generation == 2 && updated.Find("application/item")!.Metadata.RootElement.GetProperty("revision").GetInt32() == 2 &&
            first.Find("application/item")!.Metadata.RootElement.GetProperty("revision").GetInt32() == 1, "old and new generation images remain independent");
        using var preserved = updated.OpenDocument("application/item");
        await RequireBytesAsync(preserved, bytes).ConfigureAwait(false);
        first.Dispose();
        sameGeneration.Dispose();
        updated.Dispose();
        adapter.Dispose();
        Program.Require(store.ReadGeneration() == 2, "the caller still owns the underlying file store");
        store.Dispose();
        await RequireBytesAsync(oldDocument, bytes).ConfigureAwait(false);
        report.RuntimeEvidence["storageAdapterGeneration"] = 2;
    }

    private static FederationReadBudget ReadBudget() => new(new FederationReadLimits(
        maxObjectBytes: 1024 * 1024, maxTotalBytes: 8 * 1024 * 1024, maxRequests: 512,
        maxObjects: 256, maxWork: 2_000_000, maxSources: 4, maxHops: 8,
        maxDepth: 64, maxJsonDepth: 64, maxResultBytes: 2 * 1024 * 1024));

    private static async Task FileMappingAsync(EmbeddingReport report, string directory, string boundary, string work)
    {
        var other = Directory.CreateDirectory(Path.Combine(work, "different-boundary")).FullName;
        await ExpectFederationAsync(() => FileRegistrySource.OpenAsync(FileUri(directory),
            FileRegistryLayout.DocumentTree, authorizedRoot: FileUri(other), budget: ReadBudget()).AsTask(),
            FederationErrorCode.PolicyDenied).ConfigureAwait(false);
        await ExpectFederationAsync(() => FileRegistrySource.OpenAsync(FileUri(directory), "automatic",
            authorizedRoot: FileUri(boundary), budget: ReadBudget()).AsTask(), FederationErrorCode.UnsupportedOperation).ConfigureAwait(false);
        var source = await FileRegistrySource.OpenAsync(FileUri(directory), FileRegistryLayout.DocumentTree,
            authorizedRoot: FileUri(boundary), budget: ReadBudget()).ConfigureAwait(false);
        FederationReadResult result;
        try
        {
            result = await ((IFederationReadSource)source).ReadAsync(new(FederationOperation.Document,
                "/documents/main/assets/item")).ConfigureAwait(false);
            Program.Require(!source.Context.IsImmutable &&
                source.Context.RootSha256 == "7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf" &&
                result.Context.Source == FileUri(directory).AbsoluteUri, "explicit authorized File mapping context");
        }
        finally { await source.DisposeAsync().ConfigureAwait(false); }
        using var bytes = result.Document!.OpenRead();
        await RequireBytesAsync(bytes, """{"hello":"world"}"""u8.ToArray().Concat(new byte[] { 10 }).ToArray()).ConfigureAwait(false);
        report.RuntimeEvidence["fileMappingDocumentBytes"] = result.Document.Length;
        report.RuntimeFacts["fileMappingRootSha256"] = result.Context.RootSha256!;
    }

    private static async Task FileOciAsync(EmbeddingReport report, string directory, string boundary)
    {
        await ExpectFederationAsync(() => FileRegistrySource.OpenAsync(FileUri(directory), FileRegistryLayout.OciLayout,
            authorizedRoot: FileUri(boundary), budget: ReadBudget()).AsTask(), FederationErrorCode.InvalidPackage).ConfigureAwait(false);
        var source = await FileRegistrySource.OpenAsync(FileUri(directory), FileRegistryLayout.OciLayout, "offline",
            FileUri(boundary), ReadBudget()).ConfigureAwait(false);
        FederationReadResult result;
        try
        {
            result = await ((IFederationReadSource)source).ReadAsync(new(FederationOperation.Document, "/dirs/main/files/sample")).ConfigureAwait(false);
            Program.Require(source.Layout == FileRegistryLayout.OciLayout && result.Context.IsImmutable &&
                result.Context.Revision == Offline && result.Context.RequestedRevision == "offline" &&
                result.SelectedXid == "/dirs/main/files/sample/versions/v1", "explicit File OCI selection and default Version");
        }
        finally { await source.DisposeAsync().ConfigureAwait(false); }
        using var document = result.Document!.OpenRead();
        await RequireBytesAsync(document, "{\"type\":\"string\"}\n"u8.ToArray()).ConfigureAwait(false);
        report.RuntimeEvidence["fileOciDocumentBytes"] = result.Document.Length;
    }

    private static async Task GitAsync(EmbeddingReport report, string fixture)
    {
        using var manifest = JsonDocument.Parse(System.IO.File.ReadAllBytes(fixture));
        var sha256 = manifest.RootElement.GetProperty("formats").GetProperty("sha256");
        var pack = sha256.GetProperty("packs").GetProperty("snapshot");
        var encoded = Convert.FromHexString(pack.GetProperty("hex").GetString()!);
        var digest = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
        Program.Require(digest == pack.GetProperty("sha256").GetString() && encoded.Length == 794,
            "reference-Git-verified independent pack bytes");
        var limits = new GitReadLimits
        {
            MaxEncodedBytes = encoded.Length, MaxCompressedObjectBytes = 64 * 1024,
            MaxObjects = 32, MaxObjectBytes = 64 * 1024, MaxTotalDecompressedBytes = 256 * 1024,
            MaxInflateSymbols = 256 * 1024, MaxDeflateBlocks = 128, MaxDeltaObjects = 32,
            MaxDeltaDepth = 8, MaxDeltaWorkBytes = 256 * 1024, MaxDeltaInstructions = 4096,
            MaxRecordHeaderBytes = 4096, MaxRecordHeaders = 64, MaxTreeEntries = 32,
            MaxTreeNameBytes = 128, MaxTreeDepth = 8, MaxTagDepth = 4,
            MaxTraversalObjects = 64, MaxTraversalEntries = 128
        };
        using var stream = new MemoryStream(encoded, writable: false);
        var objects = GitObjectReader.ReadPack(stream, GitHashAlgorithm.Sha256, limits);
        var expectedObjects = sha256.GetProperty("objects");
        var snapshot = GitSnapshot.Open(objects,
            GitObjectId.Parse(GitHashAlgorithm.Sha256, expectedObjects.GetProperty("nestedTag").GetProperty("oid").GetString()!), limits);
        Program.Require(objects.Objects.Count == 11 &&
            snapshot.CommitId.ToString() == expectedObjects.GetProperty("commit").GetProperty("oid").GetString() &&
            snapshot.RootTreeId.ToString() == expectedObjects.GetProperty("root").GetProperty("oid").GetString(),
            "verified Git objects, peeled commit and root tree");
        var bytes = Convert.FromHexString("00FF0D0A62696E6172790A");
        Program.Require(snapshot.ReadBlob("nested/raw.bin").Content.SequenceEqual(bytes) &&
            snapshot.ReadBlob("empty").Length == 0, "exact independent Git tree/blob content");
        var tree = new GitDocumentTreeReader(snapshot, new Uri("https://git-fixture.invalid/repository"), "");
        using var document = await tree.OpenReadAsync("nested/raw.bin").ConfigureAwait(false);
        await RequireBytesAsync(document!, bytes).ConfigureAwait(false);
        Program.Require(tree.Context.IsImmutable && tree.Context.Revision == snapshot.CommitId.ToString(),
            "Git document-tree adapter retains the verified pin without acquiring a network source");
        report.RuntimeEvidence["gitVerifiedObjects"] = objects.Objects.Count;
        report.RuntimeEvidence["gitDocumentBytes"] = bytes.Length;
        report.RuntimeFacts["gitPackSha256"] = digest;
        report.RuntimeFacts["gitCommitSha256"] = snapshot.CommitId.ToString();
    }

    private static async Task OciAsync(EmbeddingReport report, string directory, string reference, JsonElement expected)
    {
        using var fileTree = FileDocumentTreeReader.Open(FileUri(directory), maxFileBytes: 1024 * 1024);
        var tree = new CountingTree(fileTree);
        var snapshot = await OciSnapshot.OpenLayoutAsync(tree, reference, ReadBudget()).ConfigureAwait(false);
        try
        {
            var closure = await snapshot.ValidateAsync().ConfigureAwait(false);
            var inventory = expected.GetProperty("roots").GetProperty(reference);
            Program.Require(closure.RootDigest == inventory.GetProperty("digest").GetString() &&
                closure.Objects == inventory.GetProperty("objects").GetInt32() &&
                closure.Indexes == inventory.GetProperty("indexes").GetInt32() &&
                closure.Manifests == inventory.GetProperty("manifests").GetInt32() &&
                closure.Configs == inventory.GetProperty("records").GetInt32() &&
                closure.Documents == inventory.GetProperty("documents").GetInt32() &&
                tree.Reads == closure.Objects + 2, "entire independent frozen OCI closure, not a selective read");
            var prefix = reference == "offline" ? "ociOffline" : "ociLinked";
            report.RuntimeEvidence[prefix + "Objects"] = closure.Objects;
            report.RuntimeEvidence[prefix + "Indexes"] = closure.Indexes;
            report.RuntimeEvidence[prefix + "Manifests"] = closure.Manifests;
            report.RuntimeEvidence[prefix + "Configs"] = closure.Configs;
            report.RuntimeEvidence[prefix + "Documents"] = closure.Documents;
            report.RuntimeFacts[prefix + "Root"] = closure.RootDigest;
            if (reference == "offline")
            {
                foreach (var item in expected.GetProperty("documents").EnumerateArray())
                {
                    var result = await snapshot.ReadAsync(new(FederationOperation.Document, item.GetProperty("xid").GetString()!)).ConfigureAwait(false);
                    using var content = result.Document!.OpenRead();
                    await RequireBytesAsync(content, Convert.FromBase64String(item.GetProperty("base64").GetString()!)).ConfigureAwait(false);
                    Program.Require(result.Document.Length == item.GetProperty("size").GetInt64(), "independent OCI Document length");
                }
            }
            else
            {
                var result = await ((IFederationReadSource)snapshot).ReadAsync(
                    new(FederationOperation.Document, "/dirs/main/files/sample/versions/v2")).ConfigureAwait(false);
                Program.Require(result.Document is null &&
                    result.ExternalDocument.GetProperty("uri").GetString() == "https://documents.example.org/sample-v2.json" &&
                    snapshot.RootDigest == Linked, "linked OCI returns an unfetched external descriptor, not invented bytes");
            }
        }
        finally { await snapshot.DisposeAsync().ConfigureAwait(false); }
    }

    private static async Task FederationAsync(EmbeddingReport report, RegistryModel model)
    {
        using var store = new ApplicationRecordStore();
        using var persistence = new ApplicationRegistryPersistence(store);
        using var readOnly = new ApplicationRegistryPersistence(store, readOnly: true);
        var host = await EmbeddingHost.StartAsync(model, persistence, readOnly,
            new EmbeddingAuthorizationPolicy(), advertiseBoundRoot: true).ConfigureAwait(false);
        try
        {
            using var writer = host.Client("writer");
            using (var group = await writer.SendAsync(HttpMethod.Put, "workspaces/federated",
                """{"location":"federation-fixture"}"""u8.ToArray(), "application/json").ConfigureAwait(false))
            {
                Program.Require(group.StatusCode == HttpStatusCode.Created, "federation host seed metadata: " + group.StatusCode);
            }
            var bytes = Convert.FromHexString(BinaryHex);
            using var input = new MemoryStream(bytes, writable: false);
            using (var asset = await writer.SendDocumentAsync(HttpMethod.Put, "workspaces/federated/artifacts/item",
                input, "application/octet-stream").ConfigureAwait(false))
            {
                Program.Require(asset.StatusCode == HttpStatusCode.Created, "federation host seed Document");
            }
            var start = host.HttpRequests;
            var budget = ReadBudget();
            var endpoint = new Uri(host.Origin, "/registry/");
            var description = CatalogDescription.Parse(System.Text.Encoding.UTF8.GetBytes(RegistryJson.Create(json =>
            {
                json.WriteStartObject();
                json.WriteString("versionid", "catalog-v2");
                json.WriteStartArray("federationprofiles");
                json.WriteStartObject();
                json.WriteString("name", "http");
                json.WriteString("endpoint", endpoint.AbsoluteUri);
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
            }).RootElement.GetRawText()));
            HttpFederationReadSource? openedSource = null;
            var lease = await CatalogSourceSelection.OpenAsync(description,
                new("git", "https://catalog.example/native.git", "catalog-commit", true),
                "/categories/native/registries/example/versions/catalog-v2", new(["http"]),
                async (advertisement, shared, token) =>
                {
                    var opened = await HttpFederationReadSource.OpenAsync(new Uri(advertisement.Endpoint),
                        host.TransportOptions("reader"), new HttpFederationReadOptions
                        {
                            Limits = shared.Limits, Timeout = TimeSpan.FromSeconds(30)
                        }, shared, token).ConfigureAwait(false);
                    openedSource = opened;
                    return new(opened, () => { opened.Dispose(); return ValueTask.CompletedTask; });
                }, budget).ConfigureAwait(false);
            var source = openedSource ?? throw new InvalidOperationException("Catalog acquisition did not open its selected HTTP source.");
            FederationReadResult result;
            try
            {
                var selected = lease.Source;
                var metadata = await selected.ReadAsync(new(FederationOperation.Entity, "/workspaces/federated",
                    representation: FederationRepresentation.ApiView)).ConfigureAwait(false);
                Program.Require(metadata.Metadata.GetProperty("entity").GetProperty("location").GetString() == "federation-fixture",
                    "real HTTP federation metadata envelope");
                result = await selected.ReadAsync(new(FederationOperation.Document,
                    "/workspaces/federated/artifacts/item")).ConfigureAwait(false);
                Program.Require(source.SpecVersion == "1.0-rc4" && source.HasCapabilitiesEvidence &&
                    !source.Context.IsImmutable && source.Context.Revision is null &&
                    result.SelectedXid == "/workspaces/federated/artifacts/item/versions/1" &&
                    source.Observations.Count >= 6 && budget.Requests >= 6 &&
                    result.Context.CatalogOrigin is { } catalogOrigin &&
                    catalogOrigin.DescriptionVersionXid == "/categories/native/registries/example/versions/catalog-v2" &&
                    catalogOrigin.CatalogContext.Revision == "catalog-commit" &&
                    catalogOrigin.Advertisement.Endpoint == endpoint.AbsoluteUri,
                    "real source interface, separate catalog/content Versions and honest live-read context");
            }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
            using var body = result.Document!.OpenRead();
            await RequireBytesAsync(body, bytes).ConfigureAwait(false);
            report.RuntimeEvidence["federationHttpRequests"] = host.HttpRequests - start;
            report.RuntimeEvidence["federationDocumentBytes"] = result.Document.Length;
            var beforeRejected = host.HttpRequests;
            await ExpectFederationAsync(() => HttpFederationReadSource.OpenAsync(new Uri(host.Origin, "/registry/"),
                host.TransportOptions("reader"), new HttpFederationReadOptions { RequireImmutableSnapshot = true }).AsTask(),
                FederationErrorCode.UnsupportedOperation).ConfigureAwait(false);
            Program.Require(host.HttpRequests == beforeRejected, "immutable live-HTTP requirements fail before dispatch");
        }
        finally { await host.DisposeAsync().ConfigureAwait(false); }
    }

    private static Uri FileUri(string directory) =>
        new(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

    private static async Task EscapedFederationAsync(EmbeddingReport report, RegistryModel model)
    {
        const string groupXid = "/workspaces/group%3Aone";
        const string resourceXid = groupXid + "/artifacts/item%40stable";
        const string versionXid = resourceXid + "/versions/v%3A1";
        Program.Require(RegistryId.ParseEscaped("group%3Aone").Value == "group:one" &&
            RegistryId.ParseEscaped("%69tem%40stable").Value == "item@stable" &&
            RegistryId.ParseEscaped("v%3A1").Value == "v:1", "public Core one-pass escaped ID parsing");
        using var store = new ApplicationRecordStore();
        using var persistence = new ApplicationRegistryPersistence(store);
        using var readOnly = new ApplicationRegistryPersistence(store, readOnly: true);
        var host = await EmbeddingHost.StartAsync(model, persistence, readOnly,
            new EmbeddingAuthorizationPolicy(), advertiseBoundRoot: true).ConfigureAwait(false);
        try
        {
            using var writer = host.Client("writer");
            using (var group = await writer.SendAsync(HttpMethod.Put, groupXid[1..],
                """{"location":"reserved-id-fixture"}"""u8.ToArray(), "application/json").ConfigureAwait(false))
            {
                Program.Require(group.StatusCode == HttpStatusCode.Created, "escaped Group creation");
            }
            var expectedBytes = Convert.FromHexString(BinaryHex);
            using var input = new MemoryStream(expectedBytes, writable: false);
            using (var version = await writer.SendDocumentAsync(HttpMethod.Put, versionXid[1..], input,
                "application/octet-stream").ConfigureAwait(false))
            {
                Program.Require(version.StatusCode == HttpStatusCode.Created && input.CanRead, "escaped explicit Version creation");
            }
            var before = host.HttpRequests;
            using var source = await HttpFederationReadSource.OpenAsync(new Uri(host.Origin, "/registry/"),
                host.TransportOptions("reader"), new HttpFederationReadOptions
                {
                    Limits = ReadBudget().Limits, Timeout = TimeSpan.FromSeconds(30)
                }).ConfigureAwait(false);
            IFederationReadSource selected = source;
            string[] groups =
            [
                "/workspaces/group:one",
                groupXid,
                "/workspaces/%67roup%3Aone"
            ];
            string[] resources =
            [
                "/workspaces/group:one/artifacts/item@stable",
                resourceXid,
                "/workspaces/%67roup%3Aone/artifacts/%69tem%40stable"
            ];
            string[] versions =
            [
                "/workspaces/group:one/artifacts/item@stable/versions/v:1",
                versionXid,
                "/workspaces/%67roup%3Aone/artifacts/%69tem%40stable/versions/%76%3A1"
            ];
            var metadataForms = 0;
            foreach (var target in groups)
            {
                var result = await selected.ReadAsync(new(FederationOperation.Entity, target,
                    representation: FederationRepresentation.ApiView)).ConfigureAwait(false);
                var entity = result.Metadata.GetProperty("entity");
                Program.Require(entity.GetProperty("xid").GetString() == groupXid &&
                    entity.GetProperty("workspaceid").GetString() == "group:one" &&
                    entity.GetProperty("location").GetString() == "reserved-id-fixture",
                    "Group identity is compared decoded without rewriting its wire XID");
                metadataForms++;
            }
            foreach (var target in resources)
            {
                var result = await selected.ReadAsync(new(FederationOperation.Entity, target,
                    representation: FederationRepresentation.ApiView)).ConfigureAwait(false);
                var entity = result.Metadata.GetProperty("entity");
                Program.Require(entity.GetProperty("xid").GetString() == resourceXid &&
                    entity.GetProperty("artifactid").GetString() == "item@stable",
                    "Resource raw/reserved/unreserved URI forms retain one identity and the wire XID");
                metadataForms++;
            }
            foreach (var target in versions)
            {
                var result = await selected.ReadAsync(new(FederationOperation.Entity, target,
                    representation: FederationRepresentation.ApiView)).ConfigureAwait(false);
                var entity = result.Metadata.GetProperty("entity");
                Program.Require(entity.GetProperty("xid").GetString() == versionXid &&
                    entity.GetProperty("artifactid").GetString() == "item@stable" &&
                    entity.GetProperty("versionid").GetString() == "v:1",
                    "Version raw/reserved/unreserved URI forms preserve wire metadata");
                metadataForms++;
            }
            var documentForms = 0;
            foreach (var target in resources.Concat(versions))
            {
                var result = await selected.ReadAsync(new(FederationOperation.Document, target)).ConfigureAwait(false);
                var identity = RegistryPath.Parse(result.SelectedXid);
                Program.Require(identity.GroupId!.Value == "group:one" && identity.ResourceId!.Value == "item@stable" &&
                    identity.VersionId!.Value == "v:1", "default and explicit Document reads select the exact decoded Version identity");
                using var document = result.Document!.OpenRead();
                await RequireBytesAsync(document, expectedBytes).ConfigureAwait(false);
                documentForms++;
            }
            string[] invalidTargets =
            [
                "/workspaces/group%GGone/artifacts/item%40stable",
                "/workspaces/group%253Aone/artifacts/item%40stable",
                "/workspaces/group%3Aone/artifacts/item%2Fstable",
                "/workspaces/group%3Aone/artifacts/item%5Cstable",
                "/workspaces/group%3Aone/artifacts/%2569tem",
                "/workspaces/group%3Aone/artifacts/item%40stable/versions/v%253A1"
            ];
            var beforeInvalid = host.HttpRequests;
            foreach (var target in invalidTargets)
            {
                await ExpectFederationAsync(() => selected.ReadAsync(new(FederationOperation.Entity, target)).AsTask(),
                    FederationErrorCode.InvalidPackage).ConfigureAwait(false);
            }
            Program.Require(host.HttpRequests == beforeInvalid, "malformed, double-decoded and separator IDs fail before HTTP dispatch");
            report.RuntimeEvidence["federationEscapedMetadataForms"] = metadataForms;
            report.RuntimeEvidence["federationEscapedDocumentForms"] = documentForms;
            report.RuntimeEvidence["federationEscapedDocumentBytes"] = documentForms * expectedBytes.Length;
            report.RuntimeEvidence["federationEscapedInvalidTargets"] = invalidTargets.Length;
            report.RuntimeEvidence["federationEscapedHttpRequests"] = host.HttpRequests - before;
            report.RuntimeFacts["federationEscapedWireVersionXid"] = versionXid;
        }
        finally { await host.DisposeAsync().ConfigureAwait(false); }
    }

    private static async Task RequireBytesAsync(Stream source, byte[] expected)
    {
        using var destination = new MemoryStream();
        await source.CopyToAsync(destination).ConfigureAwait(false);
        Program.Require(destination.ToArray().AsSpan().SequenceEqual(expected), "exact independent Document bytes");
    }

    private static async Task ExpectFederationAsync(Func<Task> action, FederationErrorCode expected)
    {
        try { await action().ConfigureAwait(false); }
        catch (FederationException exception)
        {
            Program.Require(exception.Code == expected, "explicit federation policy/selection failure");
            return;
        }
        throw new InvalidOperationException("Expected federation rejection: " + expected);
    }

    private static void CopyTree(string source, string destination)
    {
        Program.Require(!Directory.Exists(destination), "new controlled fixture destination");
        Directory.CreateDirectory(destination);
        Copy(source, destination, 0);
        static void Copy(string from, string to, int depth)
        {
            Program.Require(depth <= 16, "fixture copy depth");
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                var attributes = System.IO.File.GetAttributes(entry);
                Program.Require((attributes & FileAttributes.ReparsePoint) == 0, "fixture copy never follows links");
                var target = Path.Combine(to, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    Copy(entry, target, depth + 1);
                }
                else
                {
                    Program.Require(new FileInfo(entry).Length <= 1024 * 1024, "fixture file byte limit");
                    System.IO.File.Copy(entry, target, overwrite: false);
                }
            }
        }
    }

    private sealed class CountingTree(IDocumentTreeReader reader) : IDocumentTreeReader
    {
        internal int Reads { get; private set; }
        public NativeRegistryContext Context => reader.Context;
        public ValueTask<Stream?> OpenReadAsync(string rootRelativePath, CancellationToken cancellationToken = default)
        {
            Reads++;
            return reader.OpenReadAsync(rootRelativePath, cancellationToken);
        }
    }
}
