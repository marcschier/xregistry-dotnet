// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

internal sealed class RecordingPublisher : IOciPublisher
{
    internal Dictionary<(bool Manifest, string Digest), byte[]> Objects { get; } = [];
    internal List<string> Calls { get; } = [];
    internal int FailAt { get; set; } = -1;
    internal string? Reference { get; private set; }
    public NativeRegistryContext Context { get; set; } = new("oci", "oci://publication.example.org/team/snapshot");

    public ValueTask PutBlobAsync(OciSnapshotObject blob, CancellationToken cancellationToken = default) =>
        PutAsync(blob, false, cancellationToken);

    public ValueTask PutManifestAsync(OciSnapshotObject manifest, CancellationToken cancellationToken = default) =>
        PutAsync(manifest, true, cancellationToken);

    private async ValueTask PutAsync(OciSnapshotObject item, bool manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((manifest ? "manifest " : "blob ") + item.Digest);
        if (Calls.Count == FailAt) { throw new FederationException(FederationErrorCode.Unavailable, "Injected publication failure."); }
        await using var input = item.OpenRead();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, cancellationToken);
        var bytes = output.ToArray();
        if (GraphFixture.Hash(bytes) != item.Digest || bytes.LongLength != item.Size) { throw new InvalidOperationException("Publication bytes are not content-addressed."); }
        if (manifest)
        {
            var parsed = JsonNode.Parse(bytes)!;
            if (parsed["manifests"] is JsonArray entries)
            {
                foreach (var entry in entries) { Require(entry!, true); }
            }
            else
            {
                Require(parsed["config"]!, false);
                foreach (var layer in parsed["layers"]!.AsArray()) { Require(layer!, false); }
            }
        }
        Objects[(manifest, item.Digest)] = bytes;
    }

    private void Require(JsonNode descriptor, bool manifest)
    {
        var digest = descriptor["digest"]!.GetValue<string>();
        if (!Objects.TryGetValue((manifest, digest), out var bytes) || bytes.LongLength != descriptor["size"]!.GetValue<long>())
        {
            throw new InvalidOperationException("A manifest was published before its ordinary descriptor closure.");
        }
    }

    public ValueTask CommitReferenceAsync(string reference, OciSnapshotObject root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("reference " + reference);
        if (Calls.Count == FailAt) { throw new FederationException(FederationErrorCode.Unavailable, "Injected reference failure."); }
        if (!Objects.ContainsKey((true, root.Digest))) { throw new InvalidOperationException("The selected root must already be published."); }
        Reference = reference;
        return ValueTask.CompletedTask;
    }
}
