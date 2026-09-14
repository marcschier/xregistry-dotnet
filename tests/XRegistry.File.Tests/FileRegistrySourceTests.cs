using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.File;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;
using IOFile = System.IO.File;

namespace XRegistry.File.Tests;

public class FileRegistrySourceTests
{
    [Test]
    public async Task LinkedOciFileSourceReturnsTheSharedUnfetchedDocumentDescriptor()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            FileIntegrationFixture.CopyFixture(root.FullName, FileRegistryLayout.OciLayout);
            await using var source = await FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(root.FullName),
                FileRegistryLayout.OciLayout, "linked");
            var result = await source.ReadAsync(new(FederationOperation.Document, "/imports/shared/files/alias/versions/v2"));
            await Assert.That(result.SelectedXid).IsEqualTo("/dirs/main/files/sample/versions/v2");
            await Assert.That(result.Context.Revision).IsEqualTo(FileIntegrationFixture.Linked);
            await Assert.That(result.Context.Source).IsEqualTo(FileIntegrationFixture.Uri(root.FullName).AbsoluteUri);
            await Assert.That(result.Document).IsNull();
            await Assert.That(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(result.ExternalDocument.GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse("""
                    {"kind":"external","uri":"https://documents.example.org/sample-v2.json"}
                    """))).IsTrue();
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task CatalogAdvertisementsRequireAnExplicitAuthorizedBoundary()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        var other = FileIntegrationFixture.CreateDirectory();
        try
        {
            FileIntegrationFixture.CopyFixture(root.FullName, FileRegistryLayout.OciLayout);
            var data = new System.Text.Json.Nodes.JsonObject
            {
                ["federationprofiles"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = "file",
                    ["endpoint"] = FileIntegrationFixture.Uri(root.FullName).AbsoluteUri,
                    ["parameters"] = new System.Text.Json.Nodes.JsonObject { ["layout"] = "oci-layout", ["reference"] = "offline" },
                }),
            };
            var advertisement = CatalogDescription.Parse(Encoding.UTF8.GetBytes(data.ToJsonString())).Advertisements[0];
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenProfileAsync(advertisement,
                FileIntegrationFixture.Uri(other.FullName)).AsTask(), FederationErrorCode.PolicyDenied);
            await using var source = await FileRegistrySource.OpenProfileAsync(advertisement, FileIntegrationFixture.Uri(root.FullName));
            await Assert.That(source.Context.Revision).IsEqualTo(FileIntegrationFixture.Offline);
            data["federationprofiles"]![0]!["parameters"]!["unsupported"] = true;
            var unsupported = CatalogDescription.Parse(Encoding.UTF8.GetBytes(data.ToJsonString())).Advertisements[0];
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenProfileAsync(unsupported,
                FileIntegrationFixture.Uri(root.FullName)).AsTask(), FederationErrorCode.UnsupportedOperation);
        }
        finally
        {
            root.Delete(recursive: true);
            other.Delete(recursive: true);
        }
    }

    [Test]
    public async Task FailedBootstrapClosesBothOwnedRootAndBoundaryAnchors()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var child = Directory.CreateDirectory(Path.Combine(root.FullName, "child"));
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(child.FullName),
                FileRegistryLayout.DocumentTree, authorizedRoot: FileIntegrationFixture.Uri(root.FullName)).AsTask(),
                FederationErrorCode.NotFound);
            var moved = Path.Combine(root.FullName, "moved");
            Directory.Move(child.FullName, moved);
            await Assert.That(Directory.Exists(moved)).IsTrue();
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments(FileRegistryLayout.DocumentTree, null, "/documents/main/assets/item", "7B2268656C6C6F223A22776F726C64227D0A")]
    [Arguments(FileRegistryLayout.OciLayout, "offline", "/dirs/main/files/sample", "7B2274797065223A22737472696E67227D0A")]
    public async Task FrozenLayoutsDispatchExplicitly(FileRegistryLayout layout, string? reference, string target, string hex)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            FileIntegrationFixture.CopyFixture(root.FullName, layout);
            await using var source = await FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(root.FullName), layout, reference);
            var result = await ((IFederationReadSource)source).ReadAsync(new(FederationOperation.Document, target));
            await Assert.That(await FileIntegrationFixture.Hex(result.Document!)).IsEqualTo(hex);
            await Assert.That(result.Context.Source).IsEqualTo(FileIntegrationFixture.Uri(root.FullName).AbsoluteUri);
            await Assert.That(result.Context.Binding).IsEqualTo("file");
            await Assert.That(source.Layout).IsEqualTo(layout);
            await Assert.That(source.Model.Groups.Count > 0).IsTrue();
            await Assert.That(FederationCapabilities.GetResolutionOwner(source.Capabilities)).IsEqualTo(FederationResolutionOwner.Consumer);
            if (layout == FileRegistryLayout.OciLayout)
            {
                await Assert.That(result.Context.Revision).IsEqualTo(FileIntegrationFixture.Offline);
                await Assert.That(result.Context.RequestedRevision).IsEqualTo("offline");
                await Assert.That(result.Context.IsImmutable).IsTrue();
            }
            else
            {
                await Assert.That(result.Context.RootSha256).IsEqualTo("7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf");
                await Assert.That(result.Context.IsImmutable).IsFalse();
            }
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments("document-tree", "not-allowed", FederationErrorCode.UnsupportedOperation)]
    [Arguments("document-tree", "", FederationErrorCode.UnsupportedOperation)]
    [Arguments("oci-layout", null, FederationErrorCode.InvalidPackage)]
    [Arguments("oci-layout", "", FederationErrorCode.InvalidPackage)]
    [Arguments("automatic", null, FederationErrorCode.UnsupportedOperation)]
    public async Task ReferenceRulesAndUnknownLayoutsFailBeforeReads(string layout, string? reference, FederationErrorCode code)
    {
        var missing = FileIntegrationFixture.Uri(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(missing, layout, reference).AsTask(), code);
    }

    [Test]
    public async Task SelectedLayoutNeverFallsBack()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            FileIntegrationFixture.CopyFixture(root.FullName, FileRegistryLayout.OciLayout);
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(root.FullName),
                FileRegistryLayout.DocumentTree).AsTask(), FederationErrorCode.NotFound);
            FileIntegrationFixture.CopyFixture(root.FullName, FileRegistryLayout.DocumentTree);
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(root.FullName),
                FileRegistryLayout.OciLayout, "absent").AsTask(), FederationErrorCode.NotFound);
            await using var mapping = await FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(root.FullName), "document-tree");
            var result = await mapping.ReadAsync(new(FederationOperation.Document, "/documents/main/assets/item"));
            await Assert.That(await FileIntegrationFixture.Hex(result.Document!)).IsEqualTo("7B2268656C6C6F223A22776F726C64227D0A");
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task AuthorizedBoundaryIsComponentAware()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var boundary = Directory.CreateDirectory(Path.Combine(root.FullName, "root"));
            var child = Directory.CreateDirectory(Path.Combine(boundary.FullName, "selected"));
            var other = Directory.CreateDirectory(Path.Combine(root.FullName, "root-other"));
            FileIntegrationFixture.CopyFixture(child.FullName, FileRegistryLayout.OciLayout);
            FileIntegrationFixture.CopyFixture(other.FullName, FileRegistryLayout.OciLayout);
            await using var source = await FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(child.FullName),
                FileRegistryLayout.OciLayout, "offline", FileIntegrationFixture.Uri(boundary.FullName));
            var result = await source.ReadAsync(new(FederationOperation.Entity, "/"));
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("registryid").GetString()).IsEqualTo("fixture-offline");
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(other.FullName),
                FileRegistryLayout.OciLayout, "offline", FileIntegrationFixture.Uri(boundary.FullName)).AsTask(),
                FederationErrorCode.PolicyDenied);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments("backslash")]
    [Arguments("empty-component")]
    [Arguments("dot")]
    [Arguments("encoded-parent")]
    [Arguments("bad-escape")]
    [Arguments("bad-utf8")]
    [Arguments("remote")]
    [Arguments("query")]
    [Arguments("fragment")]
    [Arguments("missing-scheme")]
    [Arguments("encoded-separator")]
    [Arguments("encoded-control")]
    [Arguments("credentials")]
    public async Task UnsafeOriginalLocatorsCannotBecomeNormalizedAuthorizedPaths(string mutation)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var endpoint = FileIntegrationFixture.Uri(root.FullName).AbsoluteUri;
            endpoint = mutation switch
            {
                "backslash" => endpoint + "a\\b",
                "empty-component" => endpoint + "/a",
                "dot" => endpoint + "./",
                "encoded-parent" => endpoint + "%2e%2e/",
                "bad-escape" => endpoint + "%zz/",
                "bad-utf8" => endpoint + "%C0%AE/",
                "remote" => "file://remote.example/registry/",
                "query" => endpoint + "?token=not-a-credential",
                "fragment" => endpoint + "#other",
                "missing-scheme" => root.FullName,
                "encoded-separator" => endpoint + "a%2fb",
                "encoded-control" => endpoint + "%00/",
                "credentials" => "file://user:password@localhost/C:/registry/",
                _ => throw new InvalidOperationException(),
            };
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(endpoint, "document-tree").AsTask(),
                FederationErrorCode.PolicyDenied);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task AuthorizedRootsAndSelectedRootsNeverFollowReparseOrSymbolicLinks()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        var outside = FileIntegrationFixture.CreateDirectory();
        var link = Path.Combine(root.FullName, "link");
        try
        {
            FileIntegrationFixture.CopyFixture(outside.FullName, FileRegistryLayout.OciLayout);
            await FileIntegrationFixture.CreateDirectoryLink(link, outside.FullName);
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(link),
                FileRegistryLayout.OciLayout, "offline", FileIntegrationFixture.Uri(root.FullName)).AsTask(),
                FederationErrorCode.PolicyDenied);
            await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(link),
                FileRegistryLayout.OciLayout, "offline", FileIntegrationFixture.Uri(link)).AsTask(),
                FederationErrorCode.PolicyDenied);
        }
        finally
        {
            if (Directory.Exists(link)) { Directory.Delete(link); }
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Test]
    public async Task OwnedSourceAndReturnedResultsHaveSeparateLifetimes()
    {
        var root = FileIntegrationFixture.CreateDirectory();
        var selected = Directory.CreateDirectory(Path.Combine(root.FullName, "selected"));
        try
        {
            FileIntegrationFixture.CopyFixture(selected.FullName, FileRegistryLayout.OciLayout);
            var source = await FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(selected.FullName),
                FileRegistryLayout.OciLayout, "offline", FileIntegrationFixture.Uri(root.FullName));
            var result = await source.ReadAsync(new(FederationOperation.Document, "/dirs/main/files/binary"));
            await source.DisposeAsync();
            await source.DisposeAsync();
            await Assert.That(async () => { await source.ReadAsync(new(FederationOperation.Entity, "/")); })
                .Throws<ObjectDisposedException>();
            Directory.Move(selected.FullName, Path.Combine(root.FullName, "moved"));
            await Assert.That(await FileIntegrationFixture.Hex(result.Document!)).IsEqualTo("00017F80FF0D0A");
            await Assert.That(result.Context.Revision).IsEqualTo(FileIntegrationFixture.Offline);
        }
        finally { root.Delete(recursive: true); }
    }
}

internal static class FileIntegrationFixture
{
    internal const string Offline = "sha256:c937f902c54ca9c63e510bab3b4ec07ec3775ad338ac030ac93d916a736efba6";
    internal const string Linked = "sha256:dff871378d2678ee851fb7d97bae5a6b69b05c3d668f224a2f97127e8e8dee88";

    internal static DirectoryInfo CreateDirectory() => Directory.CreateTempSubdirectory("xregistry-file-integration-");
    internal static Uri Uri(string path) => new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar);

    internal static string Corpus => Path.Combine(AppContext.BaseDirectory, "Oracle");
    internal static string OciFixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Oci");

    internal static void CopyFixture(string destination, FileRegistryLayout layout)
    {
        var source = layout == FileRegistryLayout.OciLayout ? OciFixture :
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            IOFile.Copy(file, target, overwrite: false);
        }
    }

    internal static async Task<string> Hex(FederationDocument document)
    {
        using var stream = document.OpenRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Convert.ToHexString(buffer.ToArray());
    }

    internal static async Task<FederationException> Error(Func<Task> action, FederationErrorCode expected)
    {
        var error = await Assert.That(action).Throws<FederationException>() ?? throw new InvalidOperationException("Expected failure.");
        await Assert.That(error.Code).IsEqualTo(expected);
        return error;
    }

    internal static async Task CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" +
            link.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" +
            target.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create test link.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        await Assert.That(process.ExitCode).IsEqualTo(0);
    }

    internal static async ValueTask<OciSnapshotPackage> Package(string reference = "offline", string? registryId = null)
    {
        using var entrypoint = JsonDocument.Parse(IOFile.ReadAllBytes(Path.Combine(OciFixture, "index.json")));
        var root = entrypoint.RootElement.GetProperty("manifests").EnumerateArray().Single(e =>
            e.GetProperty("annotations").GetProperty("org.opencontainers.image.ref.name").GetString() == reference);
        var records = new List<RegistryJson>();
        var documents = new Dictionary<string, FederationDocument>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Walk(root);
        return await OciSnapshotWriter.CreateAsync(new(records, documents));

        void Walk(JsonElement descriptor)
        {
            var digest = descriptor.GetProperty("digest").GetString()!;
            if (!visited.Add(digest)) { return; }
            var media = descriptor.GetProperty("mediaType").GetString();
            using var node = JsonDocument.Parse(IOFile.ReadAllBytes(Path.Combine(OciFixture, "blobs", "sha256", digest[7..])));
            if (media == "application/vnd.oci.image.index.v1+json")
            {
                foreach (var edge in node.RootElement.GetProperty("manifests").EnumerateArray()) { Walk(edge); }
                return;
            }
            var config = node.RootElement.GetProperty("config").GetProperty("digest").GetString()!;
            var record = RegistryJson.Parse(IOFile.ReadAllBytes(Path.Combine(OciFixture, "blobs", "sha256", config[7..])));
            if (registryId is not null && record.RootElement.GetProperty("kind").GetString() == "registry")
            {
                var changed = System.Text.Json.Nodes.JsonNode.Parse(record.RootElement.GetRawText())!;
                changed["entity"]!["registryid"] = registryId;
                record = RegistryJson.Parse(changed.ToJsonString());
            }
            records.Add(record);
            if (record.RootElement.TryGetProperty("document", out var document) && document.GetProperty("mode").GetString() == "embedded")
            {
                var layer = node.RootElement.GetProperty("layers")[0].GetProperty("digest").GetString()!;
                documents.Add(record.RootElement.GetProperty("entity").GetProperty("xid").GetString()!,
                    new FederationDocument(IOFile.ReadAllBytes(Path.Combine(OciFixture, "blobs", "sha256", layer[7..]))));
            }
        }
    }
}
