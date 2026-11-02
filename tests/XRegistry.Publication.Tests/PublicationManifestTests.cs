// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    [Arguments("790.0", "790")]
    [Arguments("79e1", "790")]
    [Arguments("7900e-1", "790")]
    [Arguments("7.90e2", "790")]
    [Arguments("-0", "0")]
    [Arguments("-0.000e999", "0")]
    [Arguments("9007199254740993e0", "9007199254740993")]
    [Arguments("340282366920938463463374607431768211456.0", "340282366920938463463374607431768211456")]
    public async Task DescriptorSizesUseExactUnsignedIntegerValuesWithoutRewritingManifestBytes(
        string token, string expected)
    {
        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("\"size\": 790", "\"size\": " + token, StringComparison.Ordinal));

        var manifest = PublicationManifest.Parse(bytes);

        await Assert.That(manifest.Registry.Size).IsEqualTo(BigInteger.Parse(expected, CultureInfo.InvariantCulture));
        await Assert.That(manifest.GetExactBytes().SequenceEqual(bytes)).IsTrue();
        await Assert.That(manifest.SnapshotId)
            .IsEqualTo(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Test]
    [Arguments("-1")]
    [Arguments("-0.1")]
    [Arguments("790.01")]
    [Arguments("7901e-1")]
    [Arguments("\"790\"")]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("[]")]
    [Arguments("{}")]
    public async Task DescriptorSizesRejectNegativeFractionalAndNonNumericValues(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidManifest)
            .Replace("\"size\": 790", "\"size\": " + token, StringComparison.Ordinal));

        var error = await Assert.That(() => PublicationManifest.Parse(bytes))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected invalid byte size.");

        await Assert.That(error.Code).IsEqualTo("invalid_size");
        await Assert.That(error.Path).IsEqualTo("/size");
    }

    [Test]
    public async Task ManifestDescriptorNumbersHonorCallerLimitsAtEveryDescriptorLocation()
    {
        var json = JsonNode.Parse(ValidManifest)!.AsObject();
        json["registry"]!["size"] = JsonNode.Parse("1e10001");
        json["parent"] = json["registry"]!.DeepClone();
        json["documents"]![0]!["content"]!["size"] = JsonNode.Parse("1e10001");
        json["extensions"]!["urn:example:manifest-extension"]!["size"] = JsonNode.Parse("1e10001");
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());

        var error = await Assert.That(() => PublicationManifest.Parse(bytes))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected numeric limit rejection.");
        await Assert.That(error.Diagnostic.Code).IsEqualTo("number_limit");

        var limits = new RegistryJsonLimits { MaxNumberExponent = 10001 };
        var manifest = PublicationManifest.Parse(bytes, limits);
        var expected = BigInteger.Pow(10, 10001);
        await Assert.That(manifest.Registry.Size).IsEqualTo(expected);
        await Assert.That(manifest.Parent!.Size).IsEqualTo(expected);
        await Assert.That(manifest.Documents[0].Content.Size).IsEqualTo(expected);
        await Assert.That(manifest.Extensions["urn:example:manifest-extension"].Size).IsEqualTo(expected);
        await Assert.That(manifest.GetExactBytes().SequenceEqual(bytes)).IsTrue();

        var descriptor = PublicationByteDescriptor.Parse(RegistryJson.Parse(
            json["registry"]!.ToJsonString(), limits).RootElement, limits);
        await Assert.That(descriptor.Size).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/reference")]
    [Arguments("/digest")]
    [Arguments("/size")]
    [Arguments("/reference/registry")]
    [Arguments("/reference/version")]
    [Arguments("/reference/registry/binding")]
    [Arguments("/reference/registry/endpoint")]
    public async Task DescriptorsRejectMissingRequiredMembersInsteadOfSubstitutingDefaults(string path)
    {
        var descriptor = JsonNode.Parse(ValidManifest)!["registry"]!.DeepClone();
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = descriptor;
        foreach (var segment in segments[..^1])
        {
            parent = parent[segment]!;
        }
        parent.AsObject().Remove(segments[^1]);
        using var document = JsonDocument.Parse(descriptor.ToJsonString());

        var error = await Assert.That(() => PublicationByteDescriptor.Parse(document.RootElement))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected missing descriptor field.");

        await Assert.That(error.Code).IsEqualTo("missing_property");
        await Assert.That(error.Path).IsEqualTo(path);
    }

    [Test]
    public async Task DescriptorsRejectUrlOnlyObjectsRatherThanInventingVersionReferences()
    {
        using var document = JsonDocument.Parse("""{"url":"https://publisher.example.test/document"}""");

        var error = await Assert.That(() => PublicationByteDescriptor.Parse(document.RootElement))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected URL-only descriptor rejection.");

        await Assert.That(error.Code).IsEqualTo("unknown_property");
        await Assert.That(error.Path).IsEqualTo("/url");
    }

    [Test]
    [Arguments("sha256:", 63, 'a')]
    [Arguments("sha256:", 65, 'a')]
    [Arguments("sha256:", 64, 'A')]
    [Arguments("sha256:", 64, 'g')]
    [Arguments("SHA256:", 64, 'a')]
    [Arguments("sha512:", 64, 'a')]
    public async Task DescriptorDigestsRejectWrongLengthCaseAlphabetAndAlgorithm(string prefix, int count, char digit)
    {
        var descriptor = JsonNode.Parse(ValidManifest)!["registry"]!.DeepClone();
        descriptor["digest"] = prefix + new string(digit, count);
        using var document = JsonDocument.Parse(descriptor.ToJsonString());

        var error = await Assert.That(() => PublicationByteDescriptor.Parse(document.RootElement))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected noncanonical digest rejection.");

        await Assert.That(error.Code).IsEqualTo("invalid_digest");
        await Assert.That(error.Path).IsEqualTo("/digest");
    }

    [Test]
    [Arguments("git", null)]
    [Arguments("git", "")]
    [Arguments("git", "main")]
    [Arguments("git", "refs/heads/main")]
    [Arguments("git", "refs/tags/release-1")]
    [Arguments("git", "HEAD~1")]
    [Arguments("oci", null)]
    [Arguments("oci", "")]
    [Arguments("oci", "latest")]
    [Arguments("file", null)]
    [Arguments("file", "")]
    [Arguments("file", "release-1")]
    public async Task PublicationReferencesRejectMutableOrMissingCaptureSelectors(string binding, string? selector)
    {
        var descriptor = CaptureDescriptor(binding, selector);
        using var document = JsonDocument.Parse(descriptor.ToJsonString());

        var error = await Assert.That(() => PublicationByteDescriptor.Parse(document.RootElement))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected unpinned publication reference.");

        await Assert.That(error.Code).IsEqualTo("unpinned_reference");
        await Assert.That(error.Path).IsEqualTo(
            "/reference/registry/parameters/" + (binding == "git" ? "revision" : "reference"));
    }

    [Test]
    [Arguments(39, 'a')]
    [Arguments(41, 'a')]
    [Arguments(63, 'a')]
    [Arguments(65, 'a')]
    [Arguments(40, 'g')]
    [Arguments(64, 'g')]
    public async Task PublicationGitPinsRejectIncompleteAndNonHexadecimalObjectIds(int length, char digit)
    {
        using var document = JsonDocument.Parse(CaptureDescriptor("git", new string(digit, length)).ToJsonString());

        var error = await Assert.That(() => PublicationByteDescriptor.Parse(document.RootElement))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected invalid Git object ID.");

        await Assert.That(error.Code).IsEqualTo("unpinned_reference");
        await Assert.That(error.Path).IsEqualTo("/reference/registry/parameters/revision");
    }

    [Test]
    [Arguments(40, 'a')]
    [Arguments(40, 'A')]
    [Arguments(64, 'a')]
    [Arguments(64, 'A')]
    public async Task PublicationGitPinsRetainCompleteSha1AndSha256ObjectIds(int length, char digit)
    {
        var selector = new string(digit, length);
        using var document = JsonDocument.Parse(CaptureDescriptor("git", selector).ToJsonString());

        var descriptor = PublicationByteDescriptor.Parse(document.RootElement);

        await Assert.That(descriptor.Reference.Registry.Parameters["revision"]).IsEqualTo(selector);
    }

    [Test]
    [Arguments("oci")]
    [Arguments("file")]
    public async Task PublicationOciPinsRequireTheBindingsCanonicalSha256Digest(string binding)
    {
        var selector = "sha256:" + new string('a', 64);
        using var valid = JsonDocument.Parse(CaptureDescriptor(binding, selector).ToJsonString());
        var parsed = PublicationByteDescriptor.Parse(valid.RootElement);
        await Assert.That(parsed.Reference.Registry.Parameters["reference"]).IsEqualTo(selector);

        foreach (var invalid in new[]
        {
            "sha256:" + new string('a', 63), "sha256:" + new string('a', 65),
            "sha256:" + new string('A', 64), "sha256:" + new string('g', 64),
            "SHA256:" + new string('a', 64), "sha512:" + new string('a', 64)
        })
        {
            using var document = JsonDocument.Parse(CaptureDescriptor(binding, invalid).ToJsonString());
            var error = await Assert.That(() => PublicationByteDescriptor.Parse(document.RootElement))
                .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected invalid OCI digest pin.");
            await Assert.That(error.Code).IsEqualTo("unpinned_reference");
            await Assert.That(error.Path).IsEqualTo("/reference/registry/parameters/reference");
        }
    }

    [Test]
    public async Task PublicationPinRulesDoNotInventSelectorsForDocumentTreesOrUnknownBindings()
    {
        var tree = CaptureDescriptor("file", null);
        tree["reference"]!["registry"]!["parameters"]!["layout"] = "document-tree";
        using var treeDocument = JsonDocument.Parse(tree.ToJsonString());
        var treeDescriptor = PublicationByteDescriptor.Parse(treeDocument.RootElement);
        await Assert.That(treeDescriptor.Reference.Registry.Parameters.ContainsKey("reference")).IsFalse();

        var extension = CaptureDescriptor("git", "vendor-selection");
        extension["reference"]!["registry"]!["binding"] = "vendor";
        using var extensionDocument = JsonDocument.Parse(extension.ToJsonString());
        var extensionDescriptor = PublicationByteDescriptor.Parse(extensionDocument.RootElement);
        await Assert.That(extensionDescriptor.Reference.Registry.Binding).IsEqualTo("vendor");
        await Assert.That(extensionDescriptor.Reference.Registry.Parameters["revision"]).IsEqualTo("vendor-selection");
    }

    private static JsonNode CaptureDescriptor(string binding, string? selector)
    {
        var descriptor = JsonNode.Parse(ValidManifest)!["registry"]!.DeepClone();
        var access = descriptor["reference"]!["registry"]!;
        access["binding"] = binding;
        access["endpoint"] = binding switch
        {
            "git" => "https://git.example.test/captures.git",
            "oci" => "oci://registry.example.test/captures",
            _ => "file:///D:/captured"
        };
        var parameters = new JsonObject();
        if (binding == "file")
        {
            parameters["layout"] = "oci-layout";
        }
        if (selector is not null)
        {
            parameters[binding == "git" ? "revision" : "reference"] = selector;
        }
        access["parameters"] = parameters;
        return descriptor;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SourceSnapshotCaptureRejectsAbsentOrEmptyMandatoryProvenance(bool emptyArray)
    {
        var json = JsonNode.Parse(ValidManifest)!.AsObject();
        json["capture"] = "source-snapshot";
        if (emptyArray)
        {
            json["requiredExtensions"] = new JsonArray();
        }
        else
        {
            json.Remove("requiredExtensions");
        }
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());

        var error = await Assert.That(() => PublicationManifest.Parse(bytes))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected missing capture provenance.");

        await Assert.That(error.Code).IsEqualTo("missing_capture_provenance");
        await Assert.That(error.Path).IsEqualTo("/requiredExtensions");
    }

    [Test]
    [Arguments("assembled")]
    [Arguments("live-observation")]
    [Arguments("source-snapshot")]
    public async Task CaptureClassesPreserveIncludedMandatoryExtensionsWithoutInventingTheirVocabulary(string capture)
    {
        var json = JsonNode.Parse(ValidManifest)!.AsObject();
        json["capture"] = capture;
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());

        var manifest = PublicationManifest.Parse(bytes);

        await Assert.That(manifest.Capture).IsEqualTo(capture);
        await Assert.That(manifest.RequiredExtensions.SequenceEqual(["urn:example:manifest-extension"])).IsTrue();
        await Assert.That(manifest.Extensions.ContainsKey(manifest.RequiredExtensions[0])).IsTrue();
        await Assert.That(manifest.GetExactBytes().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    [Arguments("assembled")]
    [Arguments("live-observation")]
    public async Task NonAtomicCaptureClassesDoNotRequireAnInventedProvenanceExtension(string capture)
    {
        var json = JsonNode.Parse(ValidManifest)!.AsObject();
        json["capture"] = capture;
        json.Remove("requiredExtensions");
        json.Remove("extensions");

        var manifest = PublicationManifest.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));

        await Assert.That(manifest.Capture).IsEqualTo(capture);
        await Assert.That(manifest.RequiredExtensions.Count).IsZero();
        await Assert.That(manifest.Extensions.Count).IsZero();
    }

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
