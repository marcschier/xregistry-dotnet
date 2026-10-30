// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Publication;

namespace XRegistry.Publication.Tests;

public class PublicationReadClientTests
{
    private const string ChannelResource = "/publications/team/channels/stable";
    private static readonly PublicationRegistryAccess Access = new("http", "https://publisher.example.test",
        new Dictionary<string, string>(StringComparer.Ordinal));

    [Test]
    public async Task OpenChannelReadsOneCoherentMetaAndSelectsExactBindingsAndVersions()
    {
        var fixture = Fixture.Create();
        var binding = fixture.Binding();
        var resolverCalls = new List<PublicationRegistryAccess>();
        var client = new PublicationReadClient((access, _) =>
        {
            resolverCalls.Add(access);
            return ValueTask.FromResult<IPublicationReadBinding>(binding);
        });

        var snapshot = await client.OpenChannelAsync(Access, ChannelResource);

        await Assert.That(binding.ChannelReadCount).IsEqualTo(1);
        await Assert.That(binding.OpenedVersions.SequenceEqual(new[]
        {
            fixture.ManifestDescriptor.Reference.Version,
            fixture.RegistryDescriptor.Reference.Version
        })).IsTrue();
        await Assert.That(binding.OpenedVersions[0]).IsEqualTo(fixture.ManifestDescriptor.Reference.Version);
        await Assert.That(binding.OpenedVersions[1]).IsEqualTo(fixture.RegistryDescriptor.Reference.Version);
        await Assert.That(resolverCalls.Count).IsEqualTo(3);
        await Assert.That(resolverCalls.All(access => PublicationReferenceComparer.ResourceReferencesEqual(
            access, ChannelResource, Access, ChannelResource))).IsTrue();
        await Assert.That(snapshot.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(snapshot.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
        await Assert.That(snapshot.OpenRecord.Channel!.Epoch).IsEqualTo(new BigInteger(7));
    }

    [Test]
    public async Task OpenChannelRejectsUnsupportedRequiredExtensionsBeforeReadingRegistry()
    {
        var fixture = Fixture.Create(requiredExtension: true);
        var binding = fixture.Binding();

        var error = await Assert.That(async () =>
                await fixture.Client(binding).OpenChannelAsync(Access, ChannelResource))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected unsupported extension rejection.");

        await Assert.That(error.Code).IsEqualTo("unsupported_required_extension");
        await Assert.That(error.Path).IsEqualTo("/requiredExtensions/0");
        await Assert.That(binding.OpenedVersions.SequenceEqual(
            [fixture.ManifestDescriptor.Reference.Version])).IsTrue();
    }

    [Test]
    public async Task OpenRecordRejectsUnsupportedRequiredExtensionsWithoutResolvingSources()
    {
        var fixture = Fixture.Create(requiredExtension: true);
        var sourceAccess = new PublicationRegistryAccess("http", "https://source.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var sourceSnapshot = fixture.ManifestDescriptor with
        {
            Reference = new PublicationVersionReference(sourceAccess,
                "/publications/source/snapshots/pinned/versions/manifest")
        };
        var openRecord = PublicationOpenRecord.Create("urn:example:release", fixture.ManifestDescriptor,
            fixture.RegistryDescriptor, "offline-complete",
            sources: [new(sourceAccess, sourceSnapshot)]);
        var binding = fixture.Binding();
        var requests = new List<PublicationRegistryAccess>();
        var client = new PublicationReadClient((access, _) =>
        {
            requests.Add(access);
            if (access.Endpoint == sourceAccess.Endpoint)
            {
                throw new InvalidOperationException("The unsupported extension must not trigger source resolution.");
            }
            return ValueTask.FromResult<IPublicationReadBinding>(binding);
        });

        var error = await Assert.That(async () => await client.OpenRecordAsync(openRecord))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected unsupported extension rejection.");

        await Assert.That(error.Code).IsEqualTo("unsupported_required_extension");
        await Assert.That(error.Path).IsEqualTo("/requiredExtensions/0");
        await Assert.That(binding.OpenedVersions.SequenceEqual(
            [fixture.ManifestDescriptor.Reference.Version])).IsTrue();
        await Assert.That(requests.Select(static request => request.Endpoint)
            .SequenceEqual([Access.Endpoint])).IsTrue();
    }

    [Test]
    public async Task ExactManifestAndRegistryByteAccessorsReturnIndependentCopies()
    {
        var fixture = Fixture.Create();
        var snapshot = await fixture.Client().OpenChannelAsync(Access, ChannelResource);
        var manifestCopy = snapshot.GetExactManifestBytes();
        var registryCopy = snapshot.GetExactRegistryBytes();

        manifestCopy[0] ^= 0xff;
        registryCopy[0] ^= 0xff;

        await Assert.That(snapshot.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(snapshot.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
    }

    [Test]
    public async Task OpenChannelRejectsASelectedBindingThatDoesNotMatchTheRequestedAccess()
    {
        var fixture = Fixture.Create();
        var wrongAccess = new PublicationRegistryAccess("http", "https://other.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var binding = fixture.Binding(wrongAccess);
        var client = new PublicationReadClient((_, _) =>
            ValueTask.FromResult<IPublicationReadBinding>(binding));

        var error = await Assert.That(async () => await client.OpenChannelAsync(Access, ChannelResource))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected binding mismatch.");
        await Assert.That(error.Code).IsEqualTo("binding_mismatch");
        await Assert.That(binding.ChannelReadCount).IsZero();
    }

    [Test]
    public async Task OpenChannelRejectsANonChannelResourceBeforeOpeningABinding()
    {
        var resolverCalls = 0;
        var client = new PublicationReadClient((_, _) =>
        {
            resolverCalls++;
            throw new InvalidOperationException("The resolver should not run for an invalid channel resource.");
        });

        var error = await Assert.That(async () => await client.OpenChannelAsync(Access, "/groups/g"))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected invalid channel rejection.");

        await Assert.That(error.Code).IsEqualTo("invalid_channel_resource");
        await Assert.That(resolverCalls).IsZero();
    }

    [Test]
    public async Task OpenChannelRejectsAManifestDescriptorBindingThatDoesNotMatchItsReference()
    {
        var fixture = Fixture.Create();
        var wrongAccess = new PublicationRegistryAccess("http", "https://other.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var resolverCalls = 0;
        var client = new PublicationReadClient((_, _) =>
        {
            resolverCalls++;
            return ValueTask.FromResult<IPublicationReadBinding>(
                fixture.Binding(resolverCalls == 2 ? wrongAccess : Access));
        });

        var error = await Assert.That(async () => await client.OpenChannelAsync(Access, ChannelResource))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected descriptor binding mismatch.");

        await Assert.That(error.Code).IsEqualTo("binding_mismatch");
        await Assert.That(resolverCalls).IsEqualTo(2);
    }

    [Test]
    public async Task OpenChannelRejectsShortLongAndDigestMismatchedDescriptorBodies()
    {
        foreach (var corrupt in new[] { "short", "long", "digest" })
        {
            var fixture = Fixture.Create();
            if (corrupt == "short")
            {
                fixture.Objects[fixture.ManifestDescriptor.Reference.Version] = fixture.ManifestBytes[..^1];
            }

            else if (corrupt == "long")
            {
                fixture.Objects[fixture.ManifestDescriptor.Reference.Version] = [.. fixture.ManifestBytes, 0x20];
            }
            else
            {
                fixture.ChannelMetadata = fixture.ChannelMetadata with
                {
                    Head = fixture.ManifestDescriptor with { Digest = "sha256:" + new string('0', 64) }
                };
            }

            var client = fixture.Client();
            var error = await Assert.That(async () => await client.OpenChannelAsync(Access, ChannelResource))
                .Throws<PublicationException>() ?? throw new InvalidOperationException($"Expected {corrupt} descriptor rejection.");
            await Assert.That(error.Code).IsEqualTo(corrupt == "digest" ? "digest_mismatch" : "size_mismatch");
        }
    }

    [Test]
    public async Task OpenRecordReopensPinnedDescriptorsWithoutReadingItsChannel()
    {
        var fixture = Fixture.Create();
        var binding = fixture.Binding();
        var initial = await fixture.Client(binding).OpenChannelAsync(Access, ChannelResource);

        var reopened = await fixture.Client(binding).OpenRecordAsync(initial.OpenRecord);

        await Assert.That(binding.ChannelReadCount).IsEqualTo(1);
        await Assert.That(binding.OpenedVersions.SequenceEqual(new[]
        {
            fixture.ManifestDescriptor.Reference.Version,
            fixture.RegistryDescriptor.Reference.Version,
            fixture.ManifestDescriptor.Reference.Version,
            fixture.RegistryDescriptor.Reference.Version
        })).IsTrue();
        await Assert.That(reopened.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
    }

    [Test]
    public async Task OpenChannelRejectsNegativeEpochAndChannelManifestScopeMismatch()
    {
        var negative = Fixture.Create();
        negative.ChannelMetadata = negative.ChannelMetadata with { Epoch = BigInteger.MinusOne };
        var negativeBinding = negative.Binding();
        var negativeError = await Assert.That(async () =>
                await negative.Client(negativeBinding).OpenChannelAsync(Access, ChannelResource))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected negative epoch rejection.");
        await Assert.That(negativeError.Code).IsEqualTo("invalid_channel_meta");
        await Assert.That(negativeBinding.ChannelReadCount).IsEqualTo(1);

        var mismatched = Fixture.Create();
        mismatched.ChannelMetadata = mismatched.ChannelMetadata with { Scope = "urn:example:other" };
        var scopeError = await Assert.That(async () =>
                await mismatched.Client().OpenChannelAsync(Access, ChannelResource))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected scope mismatch.");
        await Assert.That(scopeError.Code).IsEqualTo("channel_manifest_mismatch");
    }

    [Test]
    public async Task OpenChannelAcceptsZeroEpoch()
    {
        var fixture = Fixture.Create();
        fixture.ChannelMetadata = fixture.ChannelMetadata with { Epoch = BigInteger.Zero };

        var snapshot = await fixture.Client().OpenChannelAsync(Access, ChannelResource);

        await Assert.That(snapshot.OpenRecord.Channel!.Epoch).IsEqualTo(BigInteger.Zero);
    }

    [Test]
    public async Task PinnedDocumentAndExtensionReadsDoNotRereadChannelMetadata()
    {
        var fixture = Fixture.Create();
        var binding = fixture.Binding();
        var snapshot = await fixture.Client(binding).OpenChannelAsync(Access, ChannelResource);

        var document = await snapshot.ReadDocumentAsync("/groups/g/resources/r/versions/v1");
        var extension = await snapshot.ReadExtensionAsync("https://publisher.example.test/extensions/policy");

        await Assert.That(document.SequenceEqual(fixture.DocumentBytes)).IsTrue();
        await Assert.That(extension.SequenceEqual(fixture.ExtensionBytes)).IsTrue();
        await Assert.That(binding.ChannelReadCount).IsEqualTo(1);
        await Assert.That(binding.OpenedVersions).Contains(fixture.DocumentDescriptor.Reference.Version);
        await Assert.That(binding.OpenedVersions).Contains(fixture.ExtensionDescriptor.Reference.Version);
        await Assert.That(binding.OpenedVersions.Count).IsEqualTo(4);
    }

    [Test]
    public async Task OpenChannelReadsDescriptorsAcrossPartialStreamReads()
    {
        var fixture = Fixture.Create();
        var binding = fixture.Binding(maxReadSize: 3);

        var snapshot = await fixture.Client(binding).OpenChannelAsync(Access, ChannelResource);

        await Assert.That(snapshot.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(snapshot.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
        await Assert.That(binding.ChannelReadCount).IsEqualTo(1);
    }

    [Test]
    public async Task PinnedReadsRejectMissingLookupsAndOpenMaterializedSnapshotsWithSources()
    {
        var fixture = Fixture.Create();
        var snapshot = await fixture.Client().OpenChannelAsync(Access, ChannelResource);
        var missingDocument = await Assert.That(async () =>
                await snapshot.ReadDocumentAsync("/groups/g/resources/absent/versions/v1"))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected missing document.");
        await Assert.That(missingDocument.Code).IsEqualTo("object_not_found");
        var missingExtension = await Assert.That(async () =>
                await snapshot.ReadExtensionAsync("https://publisher.example.test/extensions/missing"))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected missing extension.");
        await Assert.That(missingExtension.Code).IsEqualTo("object_not_found");

        var sourceAccess = new PublicationRegistryAccess("http", "https://source.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var sourceSnapshot = fixture.ManifestDescriptor with
        {
            Reference = new PublicationVersionReference(sourceAccess, "/publications/source/snapshots/pinned/versions/manifest")
        };
        var source = new PublicationSnapshotSource(sourceAccess, sourceSnapshot);
        var withSources = PublicationOpenRecord.Create("urn:example:release", fixture.ManifestDescriptor,
            fixture.RegistryDescriptor, "offline-complete", sources: [source]);
        var sourceBinding = fixture.Binding();
        var opened = await fixture.Client(sourceBinding).OpenRecordAsync(withSources);

        await Assert.That(opened.OpenRecord.Sources.Count).IsEqualTo(1);
        await Assert.That(opened.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(opened.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
        await Assert.That(sourceBinding.ChannelReadCount).IsZero();
        await Assert.That(sourceBinding.OpenedVersions).Contains(fixture.ManifestDescriptor.Reference.Version);
        await Assert.That(sourceBinding.OpenedVersions).Contains(fixture.RegistryDescriptor.Reference.Version);
        await Assert.That(sourceBinding.OpenedVersions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task OpenChannelRejectsDescriptorSizesOutsideTheConfiguredBudget()
    {
        var fixture = Fixture.Create();
        var client = new PublicationReadClient((_, _) =>
            ValueTask.FromResult<IPublicationReadBinding>(fixture.Binding()), maxDocumentBytes: 4);

        var error = await Assert.That(async () => await client.OpenChannelAsync(Access, ChannelResource))
            .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected document limit rejection.");
        await Assert.That(error.Code).IsEqualTo("object_limit");

        var exactBudgetFixture = Fixture.Create();
        var exactBudgetClient = new PublicationReadClient((_, _) =>
            ValueTask.FromResult<IPublicationReadBinding>(exactBudgetFixture.Binding()),
            maxDocumentBytes: exactBudgetFixture.ManifestBytes.Length);
        var exactBudgetSnapshot = await exactBudgetClient.OpenChannelAsync(Access, ChannelResource);
        await Assert.That(exactBudgetSnapshot.GetExactManifestBytes()
            .SequenceEqual(exactBudgetFixture.ManifestBytes)).IsTrue();
    }

    private sealed class Fixture
    {
        private Fixture(byte[] manifestBytes, byte[] registryBytes, byte[] documentBytes, byte[] extensionBytes,
            PublicationByteDescriptor manifestDescriptor, PublicationByteDescriptor registryDescriptor,
            PublicationByteDescriptor documentDescriptor, PublicationByteDescriptor extensionDescriptor)
        {
            ManifestBytes = manifestBytes;
            RegistryBytes = registryBytes;
            DocumentBytes = documentBytes;
            ExtensionBytes = extensionBytes;
            ManifestDescriptor = manifestDescriptor;
            RegistryDescriptor = registryDescriptor;
            DocumentDescriptor = documentDescriptor;
            ExtensionDescriptor = extensionDescriptor;
            ChannelMetadata = new("urn:example:release", manifestDescriptor, new BigInteger(7));
            Objects = new(StringComparer.Ordinal)
            {
                [manifestDescriptor.Reference.Version] = manifestBytes,
                [registryDescriptor.Reference.Version] = registryBytes,
                [documentDescriptor.Reference.Version] = documentBytes,
                [extensionDescriptor.Reference.Version] = extensionBytes
            };
        }

        internal byte[] ManifestBytes { get; }
        internal byte[] RegistryBytes { get; }
        internal byte[] DocumentBytes { get; }
        internal byte[] ExtensionBytes { get; }
        internal PublicationByteDescriptor ManifestDescriptor { get; }
        internal PublicationByteDescriptor RegistryDescriptor { get; }
        internal PublicationByteDescriptor DocumentDescriptor { get; }
        internal PublicationByteDescriptor ExtensionDescriptor { get; }
        internal PublicationChannelMetadata ChannelMetadata { get; set; }
        internal Dictionary<string, byte[]> Objects { get; }

        internal static Fixture Create(bool requiredExtension = false)
        {
            var registry = Encoding.UTF8.GetBytes("""{"registryid":"captured"}""");
            var document = Encoding.UTF8.GetBytes("""{"payload":"pinned"}""");
            var extension = Encoding.UTF8.GetBytes("""{"policy":"exact"}""");
            var registryDescriptor = Descriptor("/publications/team/objects/registry/versions/content", registry);
            var documentDescriptor = Descriptor("/groups/g/resources/r/versions/v1", document);
            var extensionDescriptor = Descriptor("/publications/team/extensions/policy/versions/v1", extension);
            var root = new JsonObject
            {
                ["format"] = "xregistry-publication/1",
                ["scope"] = "urn:example:release",
                ["capture"] = "assembled",
                ["completeness"] = "offline-complete",
                ["registry"] = ToJson(registryDescriptor),
                ["documents"] = new JsonArray(new JsonObject
                {
                    ["viewVersion"] = "/groups/g/resources/r/versions/v1",
                    ["originRegistry"] = "https://origin.example.test",
                    ["originVersion"] = "/groups/g/resources/r/versions/v1",
                    ["base"] = "https://origin.example.test",
                    ["content"] = ToJson(documentDescriptor)
                }),
                ["extensions"] = new JsonObject
                {
                    ["https://publisher.example.test/extensions/policy"] = ToJson(extensionDescriptor)
                }
            };
            if (requiredExtension)
            {
                root["requiredExtensions"] = new JsonArray(
                    JsonValue.Create("https://publisher.example.test/extensions/policy"));
            }
            var manifestBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            var snapshotId = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            var manifestDescriptor = Descriptor($"/publications/team/snapshots/{snapshotId}/versions/manifest",
                manifestBytes);
            return new(manifestBytes, registry, document, extension, manifestDescriptor,
                registryDescriptor, documentDescriptor, extensionDescriptor);
        }

        internal FakeBinding Binding(PublicationRegistryAccess? access = null, int maxReadSize = int.MaxValue) =>
            new(access ?? Access, ChannelMetadata, Objects, maxReadSize);

        internal PublicationReadClient Client(FakeBinding? binding = null)
        {
            binding ??= Binding();
            return new((_, _) => ValueTask.FromResult<IPublicationReadBinding>(binding));
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

    private sealed class FakeBinding(PublicationRegistryAccess access, PublicationChannelMetadata metadata,
        IReadOnlyDictionary<string, byte[]> objects, int maxReadSize) : IPublicationReadBinding
    {
        internal int ChannelReadCount { get; private set; }
        internal List<string> OpenedVersions { get; } = [];
        public PublicationRegistryAccess Access { get; } = access;

        public ValueTask<PublicationChannelMetadata> ReadChannelMetadataAsync(string channelResource,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChannelReadCount++;
            if (channelResource != ChannelResource)
            {
                throw new InvalidOperationException("Unexpected channel selected by the test.");
            }
            return ValueTask.FromResult(metadata);
        }

        public ValueTask<Stream> OpenVersionDocumentAsync(string version, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedVersions.Add(version);
            var document = objects[version];
            Stream stream = maxReadSize == int.MaxValue
                ? new MemoryStream(document, writable: false)
                : new ChunkedReadStream(document, maxReadSize);
            return ValueTask.FromResult(stream);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ChunkedReadStream(byte[] document, int maxReadSize) : MemoryStream(document, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maxReadSize)], cancellationToken);
    }

    [Test]
    public async Task MaterializedReadsRetainUnavailableSourceWithoutResolvingOrOpeningIt()
    {
        var fixture = Fixture.Create();
        var sourceAccess = new PublicationRegistryAccess("http", "https://source.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "blue" });
        var sourceDescriptor = new PublicationByteDescriptor(new(sourceAccess,
            "/publications/source/snapshots/" + new string('b', 64) + "/versions/manifest"),
            "sha256:" + new string('b', 64), 37);
        var context = PublicationOpenRecord.Create("urn:example:release", fixture.ManifestDescriptor,
            fixture.RegistryDescriptor, "offline-complete",
            sources: [new(sourceAccess, sourceDescriptor)]);
        var binding = fixture.Binding();
        var requests = new List<PublicationVersionReference>();
        var resolutions = new List<PublicationRegistryAccess>();
        var client = new PublicationReadClient((access, _) =>
        {
            resolutions.Add(access);
            if (access.Endpoint != Access.Endpoint)
            {
                throw new InvalidOperationException("Constituent source resolution is forbidden.");
            }
            return ValueTask.FromResult<IPublicationReadBinding>(new RecordingBinding(binding, requests));
        });

        await Assert.That(fixture.Objects.ContainsKey(sourceDescriptor.Reference.Version)).IsFalse();
        var opened = await client.OpenRecordAsync(context);

        await Assert.That(opened.OpenRecord.Sources.Count).IsEqualTo(1);
        var retained = opened.OpenRecord.Sources[0];
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
        await Assert.That(retained.Snapshot.Size).IsEqualTo(new BigInteger(37));
        await Assert.That(opened.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(opened.GetExactRegistryBytes().SequenceEqual(fixture.RegistryBytes)).IsTrue();
        await Assert.That(requests.SequenceEqual(new[]
        {
            fixture.ManifestDescriptor.Reference, fixture.RegistryDescriptor.Reference
        })).IsTrue();
        await Assert.That(resolutions.Count).IsEqualTo(2);
        await Assert.That(binding.OpenedVersions.Count).IsEqualTo(2);

        var document = await opened.ReadDocumentAsync(fixture.DocumentDescriptor.Reference.Version);

        await Assert.That(document.SequenceEqual(fixture.DocumentBytes)).IsTrue();
        await Assert.That(requests.SequenceEqual(new[]
        {
            fixture.ManifestDescriptor.Reference, fixture.RegistryDescriptor.Reference,
            fixture.DocumentDescriptor.Reference
        })).IsTrue();
        await Assert.That(resolutions.Count).IsEqualTo(3);
        await Assert.That(resolutions.All(access => access.Binding == "http" &&
            access.Endpoint == "https://publisher.example.test" && access.Parameters.Count == 0)).IsTrue();
        await Assert.That(binding.OpenedVersions.Count).IsEqualTo(3);
        await Assert.That(binding.ChannelReadCount).IsZero();
    }

    private sealed class RecordingBinding(FakeBinding inner,
        List<PublicationVersionReference> requests) : IPublicationReadBinding
    {
        public PublicationRegistryAccess Access => inner.Access;

        public ValueTask<PublicationChannelMetadata> ReadChannelMetadataAsync(string channelResource,
            CancellationToken cancellationToken = default) =>
            inner.ReadChannelMetadataAsync(channelResource, cancellationToken);

        public ValueTask<Stream> OpenVersionDocumentAsync(string version,
            CancellationToken cancellationToken = default)
        {
            requests.Add(new(Access, version));
            return inner.OpenVersionDocumentAsync(version, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
