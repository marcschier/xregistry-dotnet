// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Publication;

namespace XRegistry.Publication.Tests;

public class PublicationPackageTests
{
    [Test]
    public async Task OfflinePackageOpensPinnedSnapshotAndReadsExactDocumentBytesLocally()
    {
        var fixture = Fixture.Create();
        await using var directory = await PackageDirectory.CreateAsync(fixture);

        var snapshot = await PublicationPackage.OpenDirectoryAsync(directory.PackagePath);
        var document = await snapshot.ReadDocumentAsync(Fixture.DocumentVersion);

        await Assert.That(snapshot.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(snapshot.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
        await Assert.That(document.SequenceEqual(fixture.DocumentBytes)).IsTrue();
    }

    [Test]
    public async Task OfflinePackageOpensMaterializedSnapshotWithPinnedSourceProvenance()
    {
        var fixture = Fixture.Create();
        var sourceAccess = new PublicationRegistryAccess("http", "https://source.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var sourceSnapshot = fixture.ManifestDescriptor with
        {
            Reference = new PublicationVersionReference(sourceAccess, "/publications/source/snapshots/pinned/versions/manifest")
        };
        var context = PublicationOpenRecord.Create(fixture.Context.Scope, fixture.ManifestDescriptor,
            fixture.RegistryDescriptor, fixture.Context.Completeness,
            sources: [new PublicationSnapshotSource(sourceAccess, sourceSnapshot)]);
        var package = PublicationPackage.Create(context, fixture.Package.Objects);
        await using var directory = await PackageDirectory.CreateAsync(fixture, package);

        var snapshot = await PublicationPackage.OpenDirectoryAsync(directory.PackagePath);
        var document = await snapshot.ReadDocumentAsync(Fixture.DocumentVersion);

        await Assert.That(snapshot.OpenRecord.Sources.Count).IsEqualTo(1);
        await Assert.That(snapshot.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(document.SequenceEqual(fixture.DocumentBytes)).IsTrue();
    }

    [Test]
    public async Task OfflineCompletePackageRejectsMissingContentAndTamperedContent()
    {
        var missing = Fixture.Create(includeDocument: false);
        await using var missingDirectory = await PackageDirectory.CreateAsync(missing);
        var missingError = await Assert.That(async () =>
                await missing.Package.OpenSnapshotAsync(missingDirectory.PackagePath))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected missing package object rejection.");
        await Assert.That(missingError.Code).IsEqualTo("bundle_object_missing");

        var tampered = Fixture.Create();
        await using var tamperedDirectory = await PackageDirectory.CreateAsync(tampered);
        await File.WriteAllBytesAsync(Path.Combine(tamperedDirectory.PackagePath, "objects", "document.bin"),
            Encoding.UTF8.GetBytes("tampered"));
        var tamperedError = await Assert.That(async () =>
                await tampered.Package.OpenSnapshotAsync(tamperedDirectory.PackagePath))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected digest rejection.");
        await Assert.That(tamperedError.Code).IsEqualTo("size_mismatch");
    }

    [Test]
    public async Task LinkedPackageReportsAnAbsentOptionalDocumentAsUnavailableWithoutNetworkFallback()
    {
        var fixture = Fixture.Create(completeness: "linked", includeDocument: false);
        await using var directory = await PackageDirectory.CreateAsync(fixture);

        var snapshot = await fixture.Package.OpenSnapshotAsync(directory.PackagePath);
        var error = await Assert.That(async () => await snapshot.ReadDocumentAsync(Fixture.DocumentVersion))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected unavailable local object.");

        await Assert.That(error.Code).IsEqualTo("unavailable");
        await Assert.That(snapshot.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
    }

    [Test]
    public async Task PackageRejectsTraversalAndDuplicateEquivalentReferences()
    {
        var fixture = Fixture.Create();
        var reference = fixture.DocumentDescriptor.Reference;

        foreach (var unsafePath in new[]
                 {
                     "../document.bin", "/document.bin", "objects\\document.bin",
                     "objects/%2e%2e/document.bin", "objects/./document.bin", "objects/file.tar.gz"
                 })
        {
            var pathError = await Assert.That(() => PublicationPackage.Create(fixture.Context,
                    [new(fixture.ManifestDescriptor.Reference, "manifest.json"),
                        new(fixture.RegistryDescriptor.Reference, "registry.json"),
                        new(reference, unsafePath)]))
                .Throws<PublicationException>() ?? throw new InvalidOperationException($"Expected unsafe path rejection: {unsafePath}");
            await Assert.That(pathError.Code).IsEqualTo("invalid_package_path");
        }

        var duplicateError = await Assert.That(() => PublicationPackage.Create(fixture.Context,
                [new(fixture.ManifestDescriptor.Reference, "manifest.json"),
                    new(fixture.RegistryDescriptor.Reference, "registry.json"),
                    new(reference, "document.bin"),
                    new(reference with { Registry = new PublicationRegistryAccess("http",
                        "https://publisher.example.test", new Dictionary<string, string>()) }, "another.bin")]))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected duplicate reference rejection.");
        await Assert.That(duplicateError.Code).IsEqualTo("duplicate_reference");
    }

    [Test]
    public async Task PackageRejectsSymbolicLinksThatResolveOutsideItsDirectory()
    {
        var fixture = Fixture.Create();
        await using var directory = await PackageDirectory.CreateAsync(fixture, writeObjects: false);
        var outsidePath = Path.Combine(directory.RootPath, "outside");
        Directory.CreateDirectory(outsidePath);
        await File.WriteAllBytesAsync(Path.Combine(outsidePath, "manifest.json"), fixture.ManifestBytes);
        await File.WriteAllBytesAsync(Path.Combine(outsidePath, "registry.json"),
            fixture.RegistryBytes);
        await File.WriteAllBytesAsync(Path.Combine(outsidePath, "document.bin"),
            fixture.DocumentBytes);
        await CreateDirectoryLinkAsync(Path.Combine(directory.PackagePath, "objects"), outsidePath);

        var error = await Assert.That(async () =>
                await fixture.Package.OpenSnapshotAsync(directory.PackagePath))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected symbolic-link escape rejection.");

        await Assert.That(error.Code).IsEqualTo("path_escape");
    }

    private static async ValueTask CreateDirectoryLinkAsync(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var link = linkPath.Replace("'", "''", StringComparison.Ordinal);
        var target = targetPath.Replace("'", "''", StringComparison.Ordinal);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link}' -Target '{target}' | Out-Null");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell to create a test junction.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Could not create the test directory junction: {error}");
        }
    }

    [Test]
    public async Task PackageEnforcesDocumentAndAggregateByteLimitsAtTheirBoundaries()
    {
        var fixture = Fixture.Create();
        await using var directory = await PackageDirectory.CreateAsync(fixture);

        var totalLimitError = await Assert.That(async () =>
                await fixture.Package.OpenSnapshotAsync(directory.PackagePath, maxTotalBytes: 1))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected aggregate byte limit rejection.");
        await Assert.That(totalLimitError.Code).IsEqualTo("object_limit");

        var objectLimitError = await Assert.That(async () =>
                await fixture.Package.OpenSnapshotAsync(directory.PackagePath,
                    maxDocumentBytes: fixture.ManifestBytes.Length - 1))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected per-object limit rejection.");
        await Assert.That(objectLimitError.Code).IsEqualTo("object_limit");

        var objectCountError = await Assert.That(async () =>
                await fixture.Package.OpenSnapshotAsync(directory.PackagePath, maxObjects: 2))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected object-count limit rejection.");
        await Assert.That(objectCountError.Code).IsEqualTo("object_limit");

        var pathDepthError = await Assert.That(async () =>
                await fixture.Package.OpenSnapshotAsync(directory.PackagePath, maxPathDepth: 1))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected path-depth limit rejection.");
        await Assert.That(pathDepthError.Code).IsEqualTo("path_limit");

        var exactLimitSnapshot = await fixture.Package.OpenSnapshotAsync(directory.PackagePath,
            maxDocumentBytes: fixture.ManifestBytes.Length,
            maxTotalBytes: fixture.ManifestBytes.Length + fixture.RegistryBytes.Length + fixture.DocumentBytes.Length,
            maxObjects: 3, maxPathDepth: 2);
        await Assert.That(exactLimitSnapshot.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
    }

    private sealed class Fixture
    {
        internal const string DocumentVersion = "/groups/g/resources/r/versions/v1";
        private static readonly PublicationRegistryAccess Access = new("http",
            "https://publisher.example.test", new Dictionary<string, string>(StringComparer.Ordinal));

        private Fixture(byte[] manifestBytes, byte[] registryBytes, byte[] documentBytes,
            PublicationByteDescriptor manifestDescriptor, PublicationByteDescriptor registryDescriptor,
            PublicationByteDescriptor documentDescriptor, PublicationOpenRecord context,
            PublicationPackage package, bool includeDocument)
        {
            ManifestBytes = manifestBytes;
            RegistryBytes = registryBytes;
            DocumentBytes = documentBytes;
            ManifestDescriptor = manifestDescriptor;
            RegistryDescriptor = registryDescriptor;
            DocumentDescriptor = documentDescriptor;
            Context = context;
            Package = package;
            IncludeDocument = includeDocument;
        }

        internal byte[] ManifestBytes { get; }
        internal byte[] RegistryBytes { get; }
        internal byte[] DocumentBytes { get; }
        internal PublicationByteDescriptor ManifestDescriptor { get; }
        internal PublicationByteDescriptor RegistryDescriptor { get; }
        internal PublicationByteDescriptor DocumentDescriptor { get; }
        internal PublicationOpenRecord Context { get; }
        internal PublicationPackage Package { get; }
        internal bool IncludeDocument { get; }

        internal static Fixture Create(string completeness = "offline-complete", bool includeDocument = true)
        {
            var registryBytes = Encoding.UTF8.GetBytes("""{"registryid":"captured"}""");
            var documentBytes = Encoding.UTF8.GetBytes("""{"payload":"pinned"}""");
            var registryDescriptor = Descriptor("/publications/team/objects/registry/versions/content", registryBytes);
            var documentDescriptor = Descriptor(DocumentVersion, documentBytes);
            var manifest = new JsonObject
            {
                ["format"] = "xregistry-publication/1",
                ["scope"] = "urn:example:release",
                ["capture"] = "assembled",
                ["completeness"] = completeness,
                ["registry"] = ToJson(registryDescriptor),
                ["documents"] = new JsonArray(new JsonObject
                {
                    ["viewVersion"] = DocumentVersion,
                    ["originRegistry"] = "https://origin.example.test",
                    ["originVersion"] = DocumentVersion,
                    ["base"] = "https://origin.example.test",
                    ["content"] = ToJson(documentDescriptor)
                })
            };
            var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            var manifestId = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            var manifestDescriptor = Descriptor($"/publications/team/snapshots/{manifestId}/versions/manifest",
                manifestBytes);
            var context = PublicationOpenRecord.Create("urn:example:release", manifestDescriptor,
                registryDescriptor, completeness);
            var mappings = new List<PublicationPackageObject>
            {
                new(manifestDescriptor.Reference, "objects/manifest.json"),
                new(registryDescriptor.Reference, "objects/registry.json")
            };
            if (includeDocument)
            {
                mappings.Add(new(documentDescriptor.Reference, "objects/document.bin"));
            }

            var package = PublicationPackage.Create(context, mappings);
            return new(manifestBytes, registryBytes, documentBytes, manifestDescriptor, registryDescriptor,
                documentDescriptor, context, package, includeDocument);
        }

        private static PublicationByteDescriptor Descriptor(string version, byte[] bytes) =>
            new(new(Access, version),
                "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length);

        private static JsonObject ToJson(PublicationByteDescriptor descriptor) => new()
        {
            ["reference"] = new JsonObject
            {
                ["registry"] = new JsonObject
                {
                    ["binding"] = descriptor.Reference.Registry.Binding,
                    ["endpoint"] = descriptor.Reference.Registry.Endpoint
                },
                ["version"] = descriptor.Reference.Version
            },
            ["digest"] = descriptor.Digest,
            ["size"] = (long)descriptor.Size
        };
    }

    private sealed class PackageDirectory : IAsyncDisposable
    {
        private PackageDirectory(DirectoryInfo root)
        {
            RootPath = root.FullName;
            PackagePath = Path.Combine(RootPath, "package");
            Directory.CreateDirectory(PackagePath);
        }

        internal string RootPath { get; }
        internal string PackagePath { get; }

        internal static async ValueTask<PackageDirectory> CreateAsync(Fixture fixture,
            PublicationPackage? package = null, bool writeObjects = true)
        {
            var result = new PackageDirectory(Directory.CreateTempSubdirectory("xregistry-publication-package-"));
            await File.WriteAllBytesAsync(Path.Combine(result.PackagePath, "publication-package.json"),
                (package ?? fixture.Package).GetExactBytes());
            if (writeObjects)
            {
                await WriteObjectAsync(result.PackagePath, "objects/manifest.json", fixture.ManifestBytes);
                await WriteObjectAsync(result.PackagePath, "objects/registry.json", fixture.RegistryBytes);
                if (fixture.IncludeDocument)
                {
                    await WriteObjectAsync(result.PackagePath, "objects/document.bin", fixture.DocumentBytes);
                }
            }

            return result;
        }

        private static async ValueTask WriteObjectAsync(string root, string relativePath, byte[] bytes)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                var objectDirectory = new DirectoryInfo(Path.Combine(PackagePath, "objects"));
                if (objectDirectory.LinkTarget is not null)
                {
                    objectDirectory.Delete();
                }

                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task OfflineMaterializedReadsRetainSourceWhoseObjectIsAbsentFromPackage()
    {
        var fixture = Fixture.Create();
        var sourceAccess = new PublicationRegistryAccess("http", "https://source.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "blue" });
        var sourceDescriptor = new PublicationByteDescriptor(new(sourceAccess,
            "/publications/source/snapshots/" + new string('b', 64) + "/versions/manifest"),
            "sha256:" + new string('b', 64), 37);
        var context = PublicationOpenRecord.Create(fixture.Context.Scope, fixture.ManifestDescriptor,
            fixture.RegistryDescriptor, fixture.Context.Completeness,
            sources: [new(sourceAccess, sourceDescriptor)]);
        var package = PublicationPackage.Create(context, fixture.Package.Objects);
        await using var directory = await PackageDirectory.CreateAsync(fixture, package);

        await Assert.That(package.Objects.Count).IsEqualTo(3);
        await Assert.That(package.Objects.Any(item =>
            item.Reference.Version == sourceDescriptor.Reference.Version)).IsFalse();
        var expectedVersions = new[]
        {
            fixture.ManifestDescriptor.Reference.Version, fixture.RegistryDescriptor.Reference.Version,
            fixture.DocumentDescriptor.Reference.Version
        };
        await Assert.That(package.Objects.Select(item => item.Reference.Version).Order(StringComparer.Ordinal)
            .SequenceEqual(expectedVersions.Order(StringComparer.Ordinal))).IsTrue();

        // Only the three materialized objects fit the offline package's exact budget.
        var snapshot = await PublicationPackage.OpenDirectoryAsync(directory.PackagePath, maxObjects: 3,
            maxTotalBytes: fixture.ManifestBytes.Length + fixture.RegistryBytes.Length + fixture.DocumentBytes.Length);
        var document = await snapshot.ReadDocumentAsync(Fixture.DocumentVersion);

        await Assert.That(snapshot.OpenRecord.Sources.Count).IsEqualTo(1);
        var retained = snapshot.OpenRecord.Sources[0];
        await Assert.That(retained.Registry.Binding).IsEqualTo("http");
        await Assert.That(retained.Registry.Endpoint).IsEqualTo("https://source.example.test");
        await Assert.That(retained.Registry.Parameters.Count).IsEqualTo(1);
        await Assert.That(retained.Registry.Parameters["tenant"]).IsEqualTo("blue");
        await Assert.That(retained.Snapshot.Reference.Registry.Binding).IsEqualTo("http");
        await Assert.That(retained.Snapshot.Reference.Registry.Endpoint).IsEqualTo("https://source.example.test");
        await Assert.That(retained.Snapshot.Reference.Registry.Parameters.Count).IsEqualTo(1);
        await Assert.That(retained.Snapshot.Reference.Registry.Parameters["tenant"]).IsEqualTo("blue");
        await Assert.That(retained.Snapshot.Reference.Version)
            .IsEqualTo("/publications/source/snapshots/" + new string('b', 64) + "/versions/manifest");
        await Assert.That(retained.Snapshot.Digest).IsEqualTo("sha256:" + new string('b', 64));
        await Assert.That(retained.Snapshot.Size).IsEqualTo(new System.Numerics.BigInteger(37));
        await Assert.That(snapshot.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(snapshot.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
        await Assert.That(document.SequenceEqual(fixture.DocumentBytes)).IsTrue();
        await Assert.That(Directory.GetFiles(Path.Combine(directory.PackagePath, "objects")).Length).IsEqualTo(3);
    }
}
