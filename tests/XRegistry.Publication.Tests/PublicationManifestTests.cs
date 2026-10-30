// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Publication;

namespace XRegistry.Publication.Tests;

public class PublicationManifestTests
{
    private static readonly byte[] ValidManifest = """
        {
          "format": "xregistry-publication/1",
          "scope": "urn:example:release",
          "capture": "assembled",
          "completeness": "offline-complete",
          "registry": {
            "reference": {
              "registry": {
                "binding": "http",
                "endpoint": "https://publisher.example.test"
              },
              "version": "/publications/team/objects/registry-capture/versions/content"
            },
            "digest": "sha256:d7e50157645e98260f0fa754e6940c59ab5b4f2d36e79b8fa3b08130b4e7a3e6",
            "size": 790
          },
          "documents": [
            {
              "viewVersion": "/sets/manual/documents/intro/versions/7",
              "originRegistry": "https://author.example.test/registry",
              "originVersion": "/sets/manual/documents/intro/versions/7",
              "base": "https://author.example.test/manual/intro.txt",
              "content": {
                "reference": {
                  "registry": {
                    "binding": "http",
                    "endpoint": "https://publisher.example.test"
                  },
                  "version": "/sets/manual/documents/intro/versions/7"
                },
                "digest": "sha256:0a3fdd7ff022bd6a263d031e640fd0bc074fa877ed320a367784067b7a1d78e1",
                "size": 36
              }
            }
          ],
          "extensions": {
            "urn:example:manifest-extension": {
              "reference": {
                "registry": {
                  "binding": "opcua",
                  "endpoint": "opc.tcp://ua.example.test:4840",
                  "parameters": {
                    "registryroot": "nsu=urn:example:publication;s=Registry"
                  }
                },
                "version": "/publications/team/objects/extension/versions/content"
              },
              "digest": "sha256:9c5cc111ee6c5070d3c3cf23f2b83af84a05a42ff39fc89b5926d795617a5d69",
              "size": 24
            }
          },
          "requiredExtensions": ["urn:example:manifest-extension"]
        }
        """u8.ToArray();

    [Test]
    public async Task ParsesManifestAndRetainsItsExactBytes()
    {
        var manifest = PublicationManifest.Parse(ValidManifest);

        await Assert.That(manifest.Scope).IsEqualTo("urn:example:release");
        await Assert.That(manifest.Capture).IsEqualTo("assembled");
        await Assert.That(manifest.Completeness).IsEqualTo("offline-complete");
        await Assert.That(manifest.Documents.Count).IsEqualTo(1);
        await Assert.That(manifest.Documents[0].ViewVersion).IsEqualTo("/sets/manual/documents/intro/versions/7");
        await Assert.That(manifest.Documents[0].Content.Reference.Registry.Binding).IsEqualTo("http");
        await Assert.That(manifest.Extensions["urn:example:manifest-extension"].Reference.Registry.Binding).IsEqualTo("opcua");
        await Assert.That(manifest.RequiredExtensions.SequenceEqual(["urn:example:manifest-extension"])).IsTrue();
        await Assert.That(manifest.GetExactBytes().SequenceEqual(ValidManifest)).IsTrue();
        await Assert.That(manifest.SnapshotId).IsEqualTo(Convert.ToHexString(SHA256.HashData(ValidManifest)).ToLowerInvariant());

        var returned = manifest.GetExactBytes();
        returned[0] = (byte)'[';
        await Assert.That(manifest.GetExactBytes().SequenceEqual(ValidManifest)).IsTrue();
    }

    [Test]
    public async Task RejectsWrongFormatAndUnknownProperties()
    {
        var wrongFormat = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("xregistry-publication/1", "xregistry-publication/2", StringComparison.Ordinal));
        var wrongFormatError = await Assert.That(() => PublicationManifest.Parse(wrongFormat))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected format rejection.");
        await Assert.That(wrongFormatError.Code).IsEqualTo("invalid_string");
        await Assert.That(wrongFormatError.Path).IsEqualTo("/format");

        var unknown = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("\"capture\": \"assembled\",", "\"capture\": \"assembled\", \"unexpected\": true,", StringComparison.Ordinal));
        var unknownError = await Assert.That(() => PublicationManifest.Parse(unknown))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected unknown property rejection.");
        await Assert.That(unknownError.Code).IsEqualTo("unknown_property");
    }

    [Test]
    public async Task RejectsInvalidVersionXidsAndNonCanonicalDigests()
    {
        var invalidVersion = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("/sets/manual/documents/intro/versions/7", "/sets/manual/documents/intro", StringComparison.Ordinal));
        var versionError = await Assert.That(() => PublicationManifest.Parse(invalidVersion))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected Version XID rejection.");
        await Assert.That(versionError.Code).IsEqualTo("invalid_version_xid");

        var invalidDigest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("sha256:d7e501", "sha256:D7E501", StringComparison.Ordinal));
        var digestError = await Assert.That(() => PublicationManifest.Parse(invalidDigest))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected digest rejection.");
        await Assert.That(digestError.Code).IsEqualTo("invalid_digest");
    }

    [Test]
    public async Task RejectsDuplicateRequiredExtensionsAndNonIntegerSizes()
    {
        var duplicateExtension = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("[\"urn:example:manifest-extension\"]",
                "[\"urn:example:manifest-extension\", \"urn:example:manifest-extension\"]", StringComparison.Ordinal));
        var duplicateError = await Assert.That(() => PublicationManifest.Parse(duplicateExtension))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected duplicate extension rejection.");
        await Assert.That(duplicateError.Code).IsEqualTo("duplicate_required_extension");

        var nonIntegerSize = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("\"size\": 790", "\"size\": 790.5", StringComparison.Ordinal));
        var sizeError = await Assert.That(() => PublicationManifest.Parse(nonIntegerSize))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected byte size rejection.");
        await Assert.That(sizeError.Code).IsEqualTo("invalid_size");
    }

    [Test]
    public async Task RejectsCredentialedBindingsAndMissingRequiredExtensions()
    {
        var credentialedEndpoint = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("https://publisher.example.test", "https://user:secret@publisher.example.test", StringComparison.Ordinal));
        var endpointError = await Assert.That(() => PublicationManifest.Parse(credentialedEndpoint))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected credentialed endpoint rejection.");
        await Assert.That(endpointError.Code).IsEqualTo("credentialed_endpoint");

        var missingExtension = JsonNode.Parse(ValidManifest)!.AsObject();
        missingExtension.Remove("extensions");
        var missingError = await Assert.That(() => PublicationManifest.Parse(Encoding.UTF8.GetBytes(missingExtension.ToJsonString())))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected missing required extension rejection.");
        await Assert.That(missingError.Code).IsEqualTo("missing_required_extension");
    }

    [Test]
    public async Task EnforcesEntityIdentityUniquenessButKeepsVersionIdsCaseSensitive()
    {
        var duplicate = JsonNode.Parse(ValidManifest)!.AsObject();
        var duplicateDocuments = duplicate["documents"]!.AsArray();
        var entityCollision = duplicateDocuments[0]!.DeepClone();
        entityCollision["viewVersion"] = "/sets/MANUAL/documents/INTRO/versions/7";
        duplicateDocuments.Add(entityCollision);
        var duplicateError = await Assert.That(() =>
                PublicationManifest.Parse(Encoding.UTF8.GetBytes(duplicate.ToJsonString())))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected entity identity collision.");
        await Assert.That(duplicateError.Code).IsEqualTo("duplicate_view_version");

        var distinctVersions = JsonNode.Parse(ValidManifest)!.AsObject();
        var distinctDocuments = distinctVersions["documents"]!.AsArray();
        var upperVersion = distinctDocuments[0]!.DeepClone();
        upperVersion["viewVersion"] = "/sets/MANUAL/documents/INTRO/versions/A";
        distinctDocuments.Add(upperVersion);
        var lowerVersion = distinctDocuments[0]!.DeepClone();
        lowerVersion["viewVersion"] = "/sets/manual/documents/intro/versions/a";
        distinctDocuments.Add(lowerVersion);
        var parsed = PublicationManifest.Parse(Encoding.UTF8.GetBytes(distinctVersions.ToJsonString()));
        await Assert.That(parsed.Documents.Count).IsEqualTo(3);
    }

    [Test]
    public async Task VerifiesExactSizeAndDigestOfReferencedBytes()
    {
        var bytes = "abc"u8.ToArray();
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var access = new PublicationRegistryAccess("http", "https://publisher.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var descriptor = new PublicationByteDescriptor(
            new PublicationVersionReference(access, "/groups/main/resources/item/versions/v1"), digest, 3);

        PublicationIntegrity.Verify(bytes, descriptor);
        var sizeError = await Assert.That(() => PublicationIntegrity.Verify(bytes, descriptor with { Size = 2 }))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected size mismatch.");
        await Assert.That(sizeError.Code).IsEqualTo("size_mismatch");
        var digestError = await Assert.That(() => PublicationIntegrity.Verify(bytes, descriptor with { Digest = "sha256:" + new string('0', 64) }))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected digest mismatch.");
        await Assert.That(digestError.Code).IsEqualTo("digest_mismatch");
    }
}
