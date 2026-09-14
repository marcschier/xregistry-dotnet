using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.File;
using XRegistry.Federation;

namespace XRegistry.File.Tests;

public class FileBindingCompletionTests
{
    [Test]
    [Arguments("broken%GG")]
    [Arguments("broken%2")]
    [Arguments("broken%")]
    [Arguments("broken%C0%AF")]
    public async Task OriginalMalformedEscapesCannotSelectAnExistingLiteralPercentDirectory(string name)
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-completion-");
        try
        {
            var selected = Directory.CreateDirectory(Path.Combine(root.FullName, name));
            var escaped = new Uri(selected.FullName + Path.DirectorySeparatorChar).AbsoluteUri;
            var malformed = new Uri(escaped.Replace("%25", "%", StringComparison.Ordinal));
            var error = await Assert.That(() =>
            {
                using var reader = FileDocumentTreeReader.Open(malformed);
            }).Throws<FederationException>()
                ?? throw new InvalidOperationException("Expected original File URI escape rejection.");

            await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task EscapedLiteralPercentNamesAreDecodedOnceWithoutTraversal()
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-percent-");
        try
        {
            var selected = Directory.CreateDirectory(Path.Combine(root.FullName, "%2e%2e"));
            System.IO.File.WriteAllBytes(Path.Combine(selected.FullName, "value.bin"), [7, 8, 9]);
            var locator = new Uri(new Uri(root.FullName + Path.DirectorySeparatorChar).AbsoluteUri + "%252e%252e/");
            await Assert.That(locator.AbsoluteUri.Contains("%252e%252e", StringComparison.Ordinal)).IsTrue();
            using var reader = FileDocumentTreeReader.Open(locator);
            using var bytes = await reader.OpenReadAsync("value.bin");
            await Assert.That(bytes!.ReadByte()).IsEqualTo(7);
            await Assert.That(bytes.ReadByte()).IsEqualTo(8);
            await Assert.That(bytes.ReadByte()).IsEqualTo(9);
            await Assert.That(bytes.ReadByte()).IsEqualTo(-1);
            await Assert.That(reader.Context.Source).IsEqualTo(locator.AbsoluteUri);
            await Assert.That(reader.Context.IsImmutable).IsFalse();
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task NativeUriConstructionCannotHideAnEncodedParentAlias()
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-native-alias-");
        try
        {
            var locator = new Uri(Path.Combine(root.FullName, "%2e%2e") + Path.DirectorySeparatorChar);
            var error = await Assert.That(() =>
            {
                using var reader = FileDocumentTreeReader.Open(locator);
            }).Throws<FederationException>()
                ?? throw new InvalidOperationException("Expected original encoded-parent rejection.");
            await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task ForeignNativePathFormsFailAsUnsupportedBeforeOpeningAnotherRoot()
    {
        var locator = new Uri(OperatingSystem.IsWindows()
            ? "file:///var/xregistry-not-a-windows-drive/"
            : "file:///D:/xregistry-not-a-posix-root/");
        var error = await Assert.That(() =>
        {
            using var reader = FileDocumentTreeReader.Open(locator);
        }).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected foreign native path rejection.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await FileIntegrationFixture.Error(() => FileRegistrySource.OpenAsync(locator,
            FileRegistryLayout.DocumentTree).AsTask(), FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task LegacyDriveBarSpellingCannotBecomeAValidNativeDrive()
    {
        var error = await Assert.That(() =>
        {
            using var reader = FileDocumentTreeReader.Open(new Uri("file:///C|/registry/"));
        }).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected legacy drive spelling rejection.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    [Arguments("COM0", false)]
    [Arguments("COM1", true)]
    [Arguments("COM9.txt", true)]
    [Arguments("COM10", false)]
    [Arguments("NUL.data", true)]
    public async Task WindowsReservedNameRulesDoNotRejectAdjacentOrdinaryNames(string name, bool reserved)
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-device-boundary-");
        try
        {
            var locator = new Uri(new Uri(root.FullName + Path.DirectorySeparatorChar).AbsoluteUri + Uri.EscapeDataString(name) + "/");
            if (OperatingSystem.IsWindows() && reserved)
            {
                var error = await Assert.That(() =>
                {
                    using var reader = FileDocumentTreeReader.Open(locator);
                }).Throws<FederationException>() ?? throw new InvalidOperationException("Expected a reserved-name rejection.");
                await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
            }
            else
            {
                var selected = Directory.CreateDirectory(Path.Combine(root.FullName, name));
                System.IO.File.WriteAllBytes(Path.Combine(selected.FullName, "value.bin"), [42]);
                using var reader = FileDocumentTreeReader.Open(locator);
                using var stream = await reader.OpenReadAsync("value.bin");
                await Assert.That(stream!.ReadByte()).IsEqualTo(42);
                await Assert.That(stream.ReadByte()).IsEqualTo(-1);
            }
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    [Arguments(FileRegistryLayout.DocumentTree)]
    [Arguments(FileRegistryLayout.OciLayout)]
    public async Task BothFileLayoutsKeepProducerOwnershipAndSeparateLocatorFromContentPin(FileRegistryLayout layout)
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-producer-view-");
        try
        {
            FileIntegrationFixture.CopyFixture(root.FullName, layout);
            if (layout == FileRegistryLayout.DocumentTree)
            {
                var path = Path.Combine(root.FullName, "registry.json");
                var record = JsonNode.Parse(System.IO.File.ReadAllBytes(path))!;
                record["entity"]!["capabilities"]!["federation"] = new JsonObject { ["resolution"] = "producer" };
                System.IO.File.WriteAllBytes(path, Encoding.UTF8.GetBytes(record.ToJsonString()));
            }
            else { SetOciProducerOwnership(root.FullName); }
            var endpoint = FileIntegrationFixture.Uri(root.FullName);
            using var reader = FileDocumentTreeReader.Open(endpoint);
            await Assert.That(reader.Context.IsImmutable).IsFalse();
            await using var source = await FileRegistrySource.OpenAsync(endpoint, layout,
                layout == FileRegistryLayout.OciLayout ? "offline" : null);
            var context = source.Context;
            var capabilities = await source.ReadAsync(new(FederationOperation.Capabilities, "/"));
            var model = await source.ReadAsync(new(FederationOperation.Model, "/"));
            await Assert.That(FederationCapabilities.GetResolutionOwner(capabilities.Metadata)).IsEqualTo(FederationResolutionOwner.Producer);
            await Assert.That(context.Source).IsEqualTo(endpoint.AbsoluteUri);
            await Assert.That(context.Binding).IsEqualTo("file");
            await Assert.That(source.Layout).IsEqualTo(layout);
            await Assert.That(capabilities.Context).IsEqualTo(context);
            await Assert.That(model.Context).IsEqualTo(context);
            await Assert.That(source.Model.Groups.Count > 0).IsTrue();
            await Assert.That(context.RootSha256!.Length).IsEqualTo(64);
            await Assert.That(context.IsImmutable).IsEqualTo(layout == FileRegistryLayout.OciLayout);
            await Assert.That(context.RequestedRevision).IsEqualTo(layout == FileRegistryLayout.OciLayout ? "offline" : null);
            await Assert.That(context.Revision).IsEqualTo(layout == FileRegistryLayout.OciLayout ? "sha256:" + context.RootSha256 : null);
        }
        finally { root.Delete(recursive: true); }
    }

    [Test]
    public async Task LocalOciPublicationPreservesUnknownStringAnnotationsIncludingEmptyKeys()
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-oci-annotations-");
        try
        {
            var package = await FileIntegrationFixture.Package();
            var endpoint = FileIntegrationFixture.Uri(root.FullName);
            await FileOciLayout.PublishAsync(package, endpoint, "original");
            var path = Path.Combine(root.FullName, "index.json");
            var index = JsonNode.Parse(System.IO.File.ReadAllBytes(path))!;
            index["annotations"] = new JsonObject { [""] = "", ["example.note"] = "retain" };
            index["manifests"]![0]!["annotations"]![""] = "ordinary";
            System.IO.File.WriteAllBytes(path, Encoding.UTF8.GetBytes(index.ToJsonString()));

            await FileOciLayout.PublishAsync(package, endpoint, "additional");

            var result = JsonNode.Parse(System.IO.File.ReadAllBytes(path))!;
            await Assert.That(result["annotations"]![""]!.GetValue<string>()).IsEqualTo("");
            await Assert.That(result["annotations"]!["example.note"]!.GetValue<string>()).IsEqualTo("retain");
            await Assert.That(result["manifests"]![0]!["annotations"]![""]!.GetValue<string>()).IsEqualTo("ordinary");
            await Assert.That(result["manifests"]!.AsArray().Count).IsEqualTo(2);
        }
        finally { root.Delete(recursive: true); }
    }

    private static void SetOciProducerOwnership(string root)
    {
        var indexPath = Path.Combine(root, "index.json");
        var index = JsonNode.Parse(System.IO.File.ReadAllBytes(indexPath))!;
        var selected = index["manifests"]!.AsArray().Single(entry =>
            entry!["annotations"]!["org.opencontainers.image.ref.name"]!.GetValue<string>() == "offline")!;
        var registry = Read(selected);
        var metadata = registry["manifests"]![0]!;
        var manifest = Read(metadata);
        var config = manifest["config"]!;
        var record = Read(config);
        record["entity"]!["capabilities"]!["federation"] = new JsonObject { ["resolution"] = "producer" };
        Write(config, record);
        Write(metadata, manifest);
        Write(selected, registry);
        System.IO.File.WriteAllBytes(indexPath, Encoding.UTF8.GetBytes(index.ToJsonString()));

        JsonNode Read(JsonNode descriptor) => JsonNode.Parse(System.IO.File.ReadAllBytes(
            Path.Combine(root, "blobs", "sha256", descriptor["digest"]!.GetValue<string>()[7..])))!;

        void Write(JsonNode descriptor, JsonNode value)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            System.IO.File.WriteAllBytes(Path.Combine(root, "blobs", "sha256", hash), bytes);
            descriptor["digest"] = "sha256:" + hash;
            descriptor["size"] = bytes.Length;
        }
    }
}
