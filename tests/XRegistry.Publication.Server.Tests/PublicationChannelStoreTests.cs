// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Publication;
using XRegistry.Publication.Server;
using XRegistry.Server;
using XRegistry.Storage.File;

namespace XRegistry.Publication.Server.Tests;

public class PublicationChannelStoreTests
{
    private const string Channel = "/publications/team/channels/stable";
    private static readonly PublicationRegistryAccess StoreRegistry = new("http",
        "https://publisher.example.test", new Dictionary<string, string>(StringComparer.Ordinal));

    [Test]
    public async Task PublishRetainsExactBytesAndAdvancesTheChannelEpoch()
    {
        var fixture = CreateFixture();
        var persistence = new InMemoryRegistryPersistence();
        var store = new PublicationChannelStore(persistence, StoreRegistry);

        var first = await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);
        await Assert.That(first.Channel!.Epoch).IsEqualTo(new System.Numerics.BigInteger(1));
        first.ValidateAgainstManifest(fixture.Manifest);
        first.ValidateSelectedHead(first.Snapshot);

        var opened = await new PublicationChannelStore(persistence, StoreRegistry).OpenChannelAsync(Channel);
        await Assert.That(opened.Channel!.Epoch).IsEqualTo(new System.Numerics.BigInteger(1));
        await Assert.That(opened.Snapshot.Digest).IsEqualTo(first.Snapshot.Digest);
    }

    [Test]
    public async Task StaleEpochIsRejectedWithoutMovingTheChannelHead()
    {
        var fixture = CreateFixture();
        var persistence = new InMemoryRegistryPersistence();
        var store = new PublicationChannelStore(persistence, StoreRegistry);
        var first = await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);

        var conflict = await Assert.That(async () =>
            {
                await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);
            })
            .Throws<PublicationChannelConflictException>()
            ?? throw new InvalidOperationException("Expected an epoch conflict.");
        await Assert.That(conflict.ExpectedEpoch).IsEqualTo(new System.Numerics.BigInteger(0));
        await Assert.That(conflict.ActualEpoch).IsEqualTo(new System.Numerics.BigInteger(1));

        var opened = await store.OpenChannelAsync(Channel);
        await Assert.That(opened.Channel!.Epoch).IsEqualTo(new System.Numerics.BigInteger(1));
        await Assert.That(opened.Snapshot.Digest).IsEqualTo(first.Snapshot.Digest);
    }

    [Test]
    public async Task ScopeCannotChangeAfterChannelCreation()
    {
        var fixture = CreateFixture();
        var persistence = new InMemoryRegistryPersistence();
        var store = new PublicationChannelStore(persistence, StoreRegistry);
        await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);

        var changed = CreateFixture("urn:example:other");
        var error = await Assert.That(async () =>
            {
                await store.PublishAsync(Channel, changed.ManifestBytes, changed.Objects, 1);
            })
            .Throws<PublicationException>()
            ?? throw new InvalidOperationException("Expected immutable-scope rejection.");
        await Assert.That(error.Code).IsEqualTo("scope_immutable");

        var opened = await store.OpenChannelAsync(Channel);
        await Assert.That(opened.Scope).IsEqualTo("urn:example:release");
        await Assert.That(opened.Channel!.Epoch).IsEqualTo(new System.Numerics.BigInteger(1));
    }

    [Test]
    public async Task AReferenceCannotBeReboundToDifferentBytes()
    {
        var fixture = CreateFixture();
        var changed = CreateFixture(registryBytes: "{\"registry\":false}"u8.ToArray());
        var persistence = new InMemoryRegistryPersistence();
        var store = new PublicationChannelStore(persistence, StoreRegistry);
        var first = await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);

        var error = await Assert.That(async () =>
            {
                await store.PublishAsync(Channel, changed.ManifestBytes, changed.Objects, 1);
            })
            .Throws<PublicationException>()
            ?? throw new InvalidOperationException("Expected immutable object conflict.");
        await Assert.That(error.Code).IsEqualTo("immutable_object_conflict");

        var opened = await store.OpenChannelAsync(Channel);
        await Assert.That(opened.Channel!.Epoch).IsEqualTo(new System.Numerics.BigInteger(1));
        await Assert.That(opened.Snapshot.Digest).IsEqualTo(first.Snapshot.Digest);
    }

    [Test]
    public async Task OfflineReplayReturnsEveryReferencedObjectByteForByte()
    {
        var fixture = CreateFixture(includeDocument: true);
        var persistence = new InMemoryRegistryPersistence();
        var store = new PublicationChannelStore(persistence, StoreRegistry);
        await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);

        var bundle = await store.ReplayOfflineBundleAsync(Channel);
        await Assert.That(bundle.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(bundle.Objects.Count).IsEqualTo(fixture.Objects.Count);
        foreach (var expected in fixture.Objects)
        {
            var actual = bundle.Objects.Single(item =>
                PublicationReferenceComparer.ReferencesEqual(item.Descriptor.Reference, expected.Descriptor.Reference));
            await Assert.That(actual.GetExactBytes().SequenceEqual(expected.GetExactBytes())).IsTrue();
        }

        var directory = Path.Combine(Path.GetTempPath(), "xregistry-publication-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            await bundle.WritePackageAsync(directory);
            var opened = await PublicationPackage.OpenDirectoryAsync(directory);
            var document = await opened.ReadDocumentAsync("/sets/manual/documents/intro/versions/7");
            await Assert.That(opened.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
            await Assert.That(document.SequenceEqual(fixture.Objects[^1].GetExactBytes())).IsTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task PackageWriterDoesNotOverwriteANonEmptyDestination()
    {
        var fixture = CreateFixture(includeDocument: true);
        var store = new PublicationChannelStore(new InMemoryRegistryPersistence(), StoreRegistry);
        await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);
        var bundle = await store.ReplayOfflineBundleAsync(Channel);
        var directory = Path.Combine(Path.GetTempPath(), "xregistry-publication-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sentinel = Path.Combine(directory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        try
        {
            await Assert.That(async () => await bundle.WritePackageAsync(directory)).Throws<IOException>();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("preserve");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task OfflineReplayDoesNotRequireOrReturnParentManifestBytes()
    {
        var fixture = CreateFixture(includeDocument: true, includeParent: true);
        var persistence = new InMemoryRegistryPersistence();
        var store = new PublicationChannelStore(persistence, StoreRegistry);

        await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);
        var bundle = await store.ReplayOfflineBundleAsync(Channel);

        await Assert.That(bundle.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
        await Assert.That(bundle.Objects.Count).IsEqualTo(fixture.Objects.Count);
        await Assert.That(bundle.Objects.Any(item =>
            PublicationReferenceComparer.ReferencesEqual(item.Descriptor.Reference,
                fixture.Manifest.Parent!.Reference))).IsFalse();
    }

    [Test]
    public async Task OfflineCompletePublicationRequiresAllReferencedBytesBeforeCommit()
    {
        var fixture = CreateFixture(includeDocument: true);
        var persistence = new InMemoryRegistryPersistence();
        var store = new PublicationChannelStore(persistence, StoreRegistry);
        var onlyRegistry = fixture.Objects.Where(item =>
            PublicationReferenceComparer.ReferencesEqual(item.Descriptor.Reference,
                fixture.Manifest.Registry.Reference)).ToArray();

        var error = await Assert.That(async () =>
            {
                await store.PublishAsync(Channel, fixture.ManifestBytes, onlyRegistry, 0);
            })
            .Throws<PublicationException>()
            ?? throw new InvalidOperationException("Expected missing offline content rejection.");
        await Assert.That(error.Code).IsEqualTo("bundle_object_missing");
        var missingChannel = await Assert.That(async () =>
            {
                await store.OpenChannelAsync(Channel);
            })
            .Throws<KeyNotFoundException>();
        await Assert.That(missingChannel).IsNotNull();
    }

    [Test]
    public async Task LinkedPublicationCannotBeReplayedAsAnOfflineBundle()
    {
        var fixture = CreateFixture("urn:example:release", "linked");
        var persistence = new InMemoryRegistryPersistence();
        var store = new PublicationChannelStore(persistence, StoreRegistry);
        await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);

        var error = await Assert.That(async () =>
            {
                await store.ReplayOfflineBundleAsync(Channel);
            })
            .Throws<PublicationException>()
            ?? throw new InvalidOperationException("Expected linked bundle rejection.");
        await Assert.That(error.Code).IsEqualTo("bundle_incomplete");
    }

    [Test]
    public async Task DurablePersistenceReopensTheChannelAndRetainedObjects()
    {
        var fixture = CreateFixture(includeDocument: true);
        var directory = Path.Combine(Path.GetTempPath(), "xregistry-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var fileStore = LocalFileStore.Initialize(directory))
            using (var persistence = new LocalRegistryPersistence(fileStore))
            {
                var store = new PublicationChannelStore(persistence, StoreRegistry);
                await store.PublishAsync(Channel, fixture.ManifestBytes, fixture.Objects, 0);
            }

            using var reopenedFileStore = LocalFileStore.Open(directory);
            using var reopenedPersistence = new LocalRegistryPersistence(reopenedFileStore);
            var bundle = await new PublicationChannelStore(reopenedPersistence, StoreRegistry)
                .ReplayOfflineBundleAsync(Channel);
            await Assert.That(bundle.GetExactManifestBytes().SequenceEqual(fixture.ManifestBytes)).IsTrue();
            await Assert.That(bundle.Objects.Count).IsEqualTo(fixture.Objects.Count);
            foreach (var expected in fixture.Objects)
            {
                var actual = bundle.Objects.Single(item =>
                    PublicationReferenceComparer.ReferencesEqual(item.Descriptor.Reference, expected.Descriptor.Reference));
                await Assert.That(actual.GetExactBytes().SequenceEqual(expected.GetExactBytes())).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Fixture CreateFixture(string scope = "urn:example:release",
        string completeness = "offline-complete", bool includeDocument = false, byte[]? registryBytes = null,
        bool includeParent = false)
    {
        registryBytes ??= "{\"registry\":true}"u8.ToArray();
        var registryDescriptor = Descriptor("/publications/team/objects/registry/versions/content", registryBytes,
            StoreRegistry);
        var objects = new List<PublicationStoredObject>
        {
            new(registryDescriptor, registryBytes)
        };
        var root = new JsonObject
        {
            ["format"] = "xregistry-publication/1",
            ["scope"] = scope,
            ["capture"] = "assembled",
            ["completeness"] = completeness,
            ["registry"] = DescriptorJson(registryDescriptor),
            ["documents"] = new JsonArray()
        };

        if (includeParent)
        {
            var parentManifest = new JsonObject
            {
                ["format"] = "xregistry-publication/1",
                ["scope"] = scope,
                ["capture"] = "assembled",
                ["completeness"] = "offline-complete",
                ["registry"] = DescriptorJson(registryDescriptor),
                ["documents"] = new JsonArray()
            };
            var parentBytes = Encoding.UTF8.GetBytes(parentManifest.ToJsonString());
            var parentId = Convert.ToHexString(SHA256.HashData(parentBytes)).ToLowerInvariant();
            root["parent"] = DescriptorJson(Descriptor(
                $"/publications/team/snapshots/{parentId}/versions/manifest", parentBytes, StoreRegistry));
        }

        if (includeDocument)
        {
            var documentBytes = "captured document bytes"u8.ToArray();
            var documentDescriptor = Descriptor("/publications/team/objects/documents/versions/intro",
                documentBytes, StoreRegistry);
            objects.Add(new(documentDescriptor, documentBytes));
            root["documents"]!.AsArray().Add((JsonNode)new JsonObject
            {
                ["viewVersion"] = "/sets/manual/documents/intro/versions/7",
                ["originRegistry"] = "https://author.example.test/registry",
                ["originVersion"] = "/sets/manual/documents/intro/versions/7",
                ["base"] = "https://author.example.test/manual/intro.txt",
                ["content"] = DescriptorJson(documentDescriptor)
            });
        }

        var manifestBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        var manifest = PublicationManifest.Parse(manifestBytes);
        return new(manifestBytes, manifest, objects);
    }

    private static PublicationByteDescriptor Descriptor(string version, byte[] bytes,
        PublicationRegistryAccess registry) =>
        new(new PublicationVersionReference(registry, version),
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length);

    private static JsonObject DescriptorJson(PublicationByteDescriptor descriptor)
    {
        var parameters = new JsonObject();
        foreach (var pair in descriptor.Reference.Registry.Parameters)
        {
            parameters[pair.Key] = pair.Value;
        }

        var registry = new JsonObject
        {
            ["binding"] = descriptor.Reference.Registry.Binding,
            ["endpoint"] = descriptor.Reference.Registry.Endpoint
        };
        if (parameters.Count != 0)
        {
            registry["parameters"] = parameters;
        }

        return new JsonObject
        {
            ["reference"] = new JsonObject
            {
                ["registry"] = registry,
                ["version"] = descriptor.Reference.Version
            },
            ["digest"] = descriptor.Digest,
            ["size"] = checked((long)descriptor.Size)
        };
    }

    private sealed record Fixture(byte[] ManifestBytes, PublicationManifest Manifest,
        IReadOnlyList<PublicationStoredObject> Objects);
}
