// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>Evidence from an exhaustive walk of one selected descriptor closure, not from a selective lookup.</summary>
public sealed record OciValidationResult
{
    internal OciValidationResult(string rootDigest, string snapshotClass, int objects, int indexes,
        int manifests, int configs, int documents)
    {
        RootDigest = rootDigest;
        SnapshotClass = snapshotClass;
        Objects = objects;
        Indexes = indexes;
        Manifests = manifests;
        Configs = configs;
        Documents = documents;
    }
    /// <summary>The verified selected Registry root.</summary>
    public string RootDigest { get; }
    /// <summary>The verified linked or offline-complete claim.</summary>
    public string SnapshotClass { get; }
    /// <summary>The number of distinct content-addressed objects, excluding layout entrypoints and unrelated blobs.</summary>
    public int Objects { get; }
    /// <summary>The number of distinct structural indexes.</summary>
    public int Indexes { get; }
    /// <summary>The number of distinct metadata and Version manifests.</summary>
    public int Manifests { get; }
    /// <summary>The number of distinct verified config blobs.</summary>
    public int Configs { get; }
    /// <summary>The number of distinct embedded domain-document blobs; placeholders are not documents.</summary>
    public int Documents { get; }
}

public sealed partial class OciSnapshot
{
    /// <summary>Exhaustively verifies every required object, routing partition, model collection and Resource state.</summary>
    /// <remarks>Unrelated stored blobs and other advertised roots are outside this closure. Budget failure is never partial success.</remarks>
    public async ValueTask<OciValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        Enter(cancellationToken);
        try
        {
            var documents = new HashSet<string>(StringComparer.Ordinal);
            await WalkAsync(rootDescriptor, "registry", null, 1, new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false);
            session.CheckContext();
            cancellationToken.ThrowIfCancellationRequested();
            return new(RootDigest, SnapshotClass, session.ObjectCount,
                session.Nodes.Count(n => n.MediaType == OciFormat.Index),
                session.Nodes.Count(n => n.MediaType == OciFormat.Manifest), records.Count, documents.Count);

            async ValueTask WalkAsync(OciDescriptor descriptor, string kind, OciResourceState? owner,
                int depth, HashSet<string> active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.Budget.CheckDepth(depth);
                session.Budget.ChargeWork();
                if (!active.Add(descriptor.Digest)) { throw OciJson.Invalid("An entity containment cycle was detected."); }
                try
                {
                    var node = await session.NodeAsync(descriptor, kind, descriptor.Xid, cancellationToken).ConfigureAwait(false);
                    if (kind == "version")
                    {
                        var record = await RecordAsync(descriptor, kind, descriptor.Xid, cancellationToken).ConfigureAwait(false);
                        CheckDefault(owner!.Meta, record);
                        await session.BytesAsync(node.Layer!, cancellationToken).ConfigureAwait(false);
                        if (node.Layer!.Role == "document") { documents.Add(node.Layer.Digest); }
                        return;
                    }
                    var entity = await RecordAsync(node.Entries[0], kind, node.Xid, cancellationToken).ConfigureAwait(false);
                    var metadata = await session.NodeAsync(node.Entries[0], kind, node.Xid, cancellationToken).ConfigureAwait(false);
                    await session.BytesAsync(metadata.Layer!, cancellationToken).ConfigureAwait(false);
                    OciResourceState? state = null;
                    if (kind == "resource")
                    {
                        var meta = await RecordAsync(node.Entries[1], "meta", node.Xid + "/meta", cancellationToken).ConfigureAwait(false);
                        var manifest = await session.NodeAsync(node.Entries[1], "meta", node.Xid + "/meta", cancellationToken).ConfigureAwait(false);
                        await session.BytesAsync(manifest.Layer!, cancellationToken).ConfigureAwait(false);
                        state = new(node, entity, meta);
                    }
                    foreach (var collection in await DirectoryAsync(node, state?.Meta.IsAlias == true, cancellationToken).ConfigureAwait(false))
                    {
                        var entries = await EntriesAsync(collection, "collection", collection.Xid, cancellationToken).ConfigureAwait(false);
                        var childKind = kind == "registry" ? "group" : kind == "group" ? "resource" : "version";
                        foreach (var entry in entries)
                        {
                            await WalkAsync(entry, childKind, state, depth + 1, active).ConfigureAwait(false);
                        }
                        if (state is not null) { CheckVersionSet(state, entries.Select(e => records[GetNode(e).Config!.Digest]).ToArray(), cancellationToken); }
                    }
                }
                finally { active.Remove(descriptor.Digest); }
            }
        }
        finally { Volatile.Write(ref reading, 0); }
    }

    private void CheckVersionSet(OciResourceState state, OciRecord[] versions, CancellationToken cancellationToken)
    {
        var byId = versions.ToDictionary(v => OciJson.Text(v.Entity, "versionid"), StringComparer.Ordinal);
        var defaultId = OciJson.Text(state.Meta.Entity, "defaultversionid");
        if (!byId.ContainsKey(defaultId)) { throw OciJson.Invalid("Resource Meta names a missing default Version."); }
        var resource = OciRecords.Resource(Model, OciFormat.Xid(state.Resource.Xid));
        if (resource.MaxVersions > 0 && versions.Length > resource.MaxVersions)
        {
            throw OciJson.Invalid("Captured Version count exceeds the model's retention limit.");
        }
        if (resource.SingleVersionRoot && versions.Count(v => OciJson.Text(v.Entity, "ancestorid") == OciJson.Text(v.Entity, "versionid")) != 1)
        {
            throw OciJson.Invalid("The captured Resource requires exactly one ancestry root.");
        }
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckDefault(state.Meta, version);
            var active = new HashSet<string>(StringComparer.Ordinal);
            var id = OciJson.Text(version.Entity, "versionid");
            while (!resolved.Contains(id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.Budget.ChargeWork();
                if (!active.Add(id) || !byId.TryGetValue(id, out var current))
                {
                    throw OciJson.Invalid("Captured Version ancestry is missing or cyclic.");
                }
                var ancestor = OciJson.Text(current.Entity, "ancestorid");
                if (ancestor == id) { break; }
                id = ancestor;
            }
            resolved.UnionWith(active);
        }
        var metadata = versions.Select(v => v.SemanticEntity).ToArray();
        foreach (var attribute in resource.Attributes.Values) { Match(attribute, metadata); }

        void Match(RegistryAttributeDefinition definition, JsonElement[] values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Budget.ChargeWork(values.Length);
            var projected = values.Select(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty(definition.Name, out var value)
                ? value : default).ToArray();
            if (definition.MatchVersions && projected.Skip(1).Any(v => !Same(v, projected[0], definition.Type)))
            {
                throw OciJson.Invalid("Captured Versions violate a model matchversions constraint.");
            }
            foreach (var child in definition.Attributes.Values.Where(a => a.Name != "*")) { Match(child, projected); }
        }
    }

    private static bool Same(JsonElement left, JsonElement right, RegistryValueType type)
    {
        if (left.ValueKind == JsonValueKind.Undefined || right.ValueKind == JsonValueKind.Undefined)
        {
            return left.ValueKind == right.ValueKind;
        }
        if (type is RegistryValueType.Integer or RegistryValueType.UInteger or RegistryValueType.Decimal)
        {
            return RegistryNumber.FromElement(left).Equals(RegistryNumber.FromElement(right));
        }
        if (type == RegistryValueType.Timestamp)
        {
            // Core-normalized UTC values retain arbitrary fractional precision, as in DirectoryMapping.
            var first = left.GetString()!;
            var second = right.GetString()!;
            return first.AsSpan(0, 19).SequenceEqual(second.AsSpan(0, 19)) &&
                first.AsSpan(19, first.Length - 20).TrimEnd('0').TrimEnd('.')
                    .SequenceEqual(second.AsSpan(19, second.Length - 20).TrimEnd('0').TrimEnd('.'));
        }
        return OciJson.Equal(left, right);
    }
}
