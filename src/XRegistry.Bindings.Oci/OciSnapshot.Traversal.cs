using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

public sealed partial class OciSnapshot
{
    private readonly Dictionary<string, OciRecord> records = new(StringComparer.Ordinal);
    private long normalizedRecordBytes;

    private void CacheRecord(string digest, OciRecord record)
    {
        OciRecords.RetainNormalized(record, ref normalizedRecordBytes, session.Budget);
        records.Add(digest, record);
    }

    private async ValueTask<OciRecord> RecordAsync(OciDescriptor descriptor, string kind, string xid,
        CancellationToken cancellationToken)
    {
        var manifest = await session.NodeAsync(descriptor, kind, xid, cancellationToken).ConfigureAwait(false);
        if (manifest.Config is null) { throw OciJson.Invalid("An entity config requires a metadata or Version manifest."); }
        if (!records.TryGetValue(manifest.Config.Digest, out var record))
        {
            var bytes = await session.BytesAsync(manifest.Config, cancellationToken).ConfigureAwait(false);
            System.Text.Json.JsonElement groupMetadata = default;
            if (kind == "version")
            {
                var parts = OciFormat.Xid(xid);
                var group = OciRecords.Group(Model, parts);
                if (group.Constraints.Values.Any(c => c.ResourceType == parts[2] && c.EqualsAttribute is not null))
                {
                    var groupXid = OciFormat.SerializedPrefix(xid, 2);
                    var (_, node) = await EntityNodeAsync(groupXid, cancellationToken).ConfigureAwait(false);
                    groupMetadata = (await RecordAsync(node.Entries[0], "group", groupXid, cancellationToken).ConfigureAwait(false)).SemanticEntity;
                }
            }
            record = OciRecords.Validate(OciJson.Parse(bytes, session.Budget, cancellationToken), kind, xid,
                Model, session.Budget, cancellationToken, groupMetadata);
            CacheRecord(manifest.Config.Digest, record);
        }
        if (record.Kind != kind || !OciFormat.SameXid(record.Xid, xid)) { throw OciJson.Invalid("A config is reused with contradictory identity."); }
        if (kind == "version")
        {
            var embedded = OciJson.Text(record.Data.GetProperty("document"), "mode") == "embedded";
            if (embedded != (manifest.Layer!.Role == "document") ||
                SnapshotClass == "offline-complete" && OciJson.Text(record.Data.GetProperty("document"), "mode") == "external")
            {
                throw OciJson.Invalid("Version mode, layer and snapshot completeness disagree.");
            }
        }
        return record;
    }

    private async ValueTask<OciNode> RoutingAsync(OciDescriptor descriptor, string kind, string xid,
        int depth, CancellationToken cancellationToken)
    {
        session.Budget.CheckDepth(depth);
        var node = await session.NodeAsync(descriptor, kind, xid, cancellationToken).ConfigureAwait(false);
        if (node.Lower != (descriptor.Lower ?? "") || node.Upper != (descriptor.Upper ?? "") ||
            descriptor.Role == "shard" && node.Entries.Length == 0)
        {
            throw OciJson.Invalid("Routing node bounds or nonempty-shard requirement disagree with the parent.");
        }
        if (kind == "collections" && node.Mode == "leaf")
        {
            var entityKind = xid == "/" ? "registry" : OciFormat.Xid(xid).Length == 2 ? "group" : "resource";
            var expected = OciRecords.Collections(Model, entityKind, xid)
                .Select(value => OciFormat.CanonicalXid(value, true)).ToHashSet(StringComparer.Ordinal);
            if (node.Entries.Any(e => !expected.Contains(OciFormat.CanonicalXid(e.Xid, true))))
            {
                throw OciJson.Invalid("A directory contains a collection not declared by its model.");
            }
        }
        return node;
    }

    private async ValueTask<OciDescriptor> FindAsync(OciDescriptor descriptor, string kind, string xid, string key,
        CancellationToken cancellationToken)
    {
        OciFormat.Key(key, kind, xid);
        var selected = await FindEncodedKeyAsync(descriptor, kind, xid,
            OciFormat.CanonicalXid(key, kind == "collections"), cancellationToken).ConfigureAwait(false);
        return selected ?? throw new FederationException(FederationErrorCode.NotFound, "The exact key is absent from the selected collection.");
    }

    private async ValueTask<OciDescriptor?> FindEncodedKeyAsync(OciDescriptor descriptor, string kind, string xid,
        string key, CancellationToken cancellationToken)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 1; ; depth++)
        {
            if (!active.Add(descriptor.Digest)) { throw OciJson.Invalid("A routing cycle is not a valid OCI snapshot."); }
            var node = await RoutingAsync(descriptor, kind, xid, depth, cancellationToken).ConfigureAwait(false);
            if (node.Mode == "leaf")
            {
                session.Budget.ChargeWork(node.Entries.Length);
                OciDescriptor? match = null;
                foreach (var entry in node.Entries)
                {
                    if (!StringComparer.Ordinal.Equals(entry.Xid, key)) { continue; }
                    if (match is not null) { throw OciJson.Invalid("Different stored URI keys identify the same collection member."); }
                    match = entry;
                }
                return match;
            }
            descriptor = node.Entries.Single(e => OciFormat.Inside(key, e.Lower!, e.Upper!));
        }
    }

    private async ValueTask<List<OciDescriptor>> EntriesAsync(OciDescriptor descriptor, string kind, string xid,
        CancellationToken cancellationToken)
    {
        var result = new List<OciDescriptor>();
        await WalkAsync(descriptor, 1, new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false);
        if (result.Select(e => OciFormat.CanonicalXid(e.Xid, kind == "collections"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
        {
            throw OciJson.Invalid("Collection sibling IDs must be unique under Core case-insensitive comparison.");
        }
        return result;

        async ValueTask WalkAsync(OciDescriptor current, int depth, HashSet<string> active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.Budget.ChargeWork();
            if (!active.Add(current.Digest)) { throw OciJson.Invalid("An OCI routing cycle was detected."); }
            try
            {
                var node = await RoutingAsync(current, kind, xid, depth, cancellationToken).ConfigureAwait(false);
                if (node.Mode == "leaf")
                {
                    session.Budget.ChargeWork(node.Entries.Length);
                    result.AddRange(node.Entries);
                }
                else
                {
                    foreach (var shard in node.Entries)
                    {
                        var before = result.Count;
                        await WalkAsync(shard, depth + 1, active).ConfigureAwait(false);
                        if (result.Count == before) { throw OciJson.Invalid("Every shard must contain an eventual entry."); }
                    }
                }
            }
            finally { active.Remove(current.Digest); }
        }
    }

    private async ValueTask<OciDescriptor> ChildCollectionAsync(OciNode node, string collection,
        CancellationToken cancellationToken)
    {
        if (!OciRecords.Collections(Model, node.Kind, node.Xid).Any(value => OciFormat.SameXid(value, collection, true)))
        {
            throw OciJson.Invalid("A collection is not declared by the captured model.");
        }
        try
        {
            return await FindAsync(node.Entries[^1], "collections", node.Xid, collection, cancellationToken).ConfigureAwait(false);
        }
        catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound)
        {
            throw OciJson.Invalid("A model-declared collection is missing from the directory.");
        }
    }

    private async ValueTask<(OciDescriptor Descriptor, OciNode Node)> EntityNodeAsync(string xid,
        CancellationToken cancellationToken)
    {
        var parts = OciFormat.Xid(xid);
        if (parts.Length is not (0 or 2 or 4)) { throw OciJson.Invalid("An entity index must be a Registry, Group or Resource."); }
        if (parts.Length >= 2) { OciRecords.Group(Model, parts); }
        if (parts.Length == 4) { OciRecords.Resource(Model, parts); }
        var descriptor = rootDescriptor;
        var node = root;
        var serializedParts = xid[1..].Split('/');
        for (var offset = 0; offset < parts.Length; offset += 2)
        {
            var collection = "/" + string.Join('/', serializedParts.Take(offset + 1));
            var child = "/" + string.Join('/', serializedParts.Take(offset + 2));
            var directory = await ChildCollectionAsync(node, collection, cancellationToken).ConfigureAwait(false);
            descriptor = await FindAsync(directory, "collection", collection, child, cancellationToken).ConfigureAwait(false);
            node = await session.NodeAsync(descriptor, offset == 0 ? "group" : "resource", child, cancellationToken).ConfigureAwait(false);
        }
        return (descriptor, node);
    }

    private async ValueTask<OciResourceState> ResourceStateAsync(string xid, CancellationToken cancellationToken)
    {
        var (_, node) = await EntityNodeAsync(xid, cancellationToken).ConfigureAwait(false);
        if (node.Kind != "resource") { throw OciJson.Invalid("A Resource is required."); }
        var record = await RecordAsync(node.Entries[0], "resource", xid, cancellationToken).ConfigureAwait(false);
        var meta = await RecordAsync(node.Entries[1], "meta", xid + "/meta", cancellationToken).ConfigureAwait(false);
        if (meta.IsAlias)
        {
            var entries = await EntriesAsync(node.Entries[^1], "collections", xid, cancellationToken).ConfigureAwait(false);
            if (entries.Count != 0) { throw OciJson.Invalid("An xref Resource must have an empty directory."); }
        }
        return new(node, record, meta);
    }

    private async ValueTask<(OciDescriptor Descriptor, OciRecord Record, OciNode Manifest)> VersionAsync(
        OciResourceState state, string versionId, CancellationToken cancellationToken, string? serializedXid = null)
    {
        if (state.Meta.IsAlias)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "Alias Versions cannot be expanded in Core document view.", "cannot_doc_xref");
        }
        var collection = state.Resource.Xid + "/versions";
        var versions = await ChildCollectionAsync(state.Node, collection, cancellationToken).ConfigureAwait(false);
        var xid = collection + "/" + Uri.EscapeDataString(versionId);
        if (serializedXid is not null)
        {
            if (!OciFormat.SameXid(serializedXid, xid)) { throw OciJson.Invalid("The requested Version has contradictory identity."); }
            xid = serializedXid;
        }
        OciDescriptor descriptor;
        try { descriptor = await FindAsync(versions, "collection", collection, xid, cancellationToken).ConfigureAwait(false); }
        catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound &&
            OciJson.Text(state.Meta.Entity, "defaultversionid") == versionId)
        {
            throw OciJson.Invalid("Resource Meta names a missing default Version.");
        }
        var record = await RecordAsync(descriptor, "version", xid, cancellationToken).ConfigureAwait(false);
        CheckDefault(state.Meta, record);
        var manifest = await session.NodeAsync(descriptor, "version", xid, cancellationToken).ConfigureAwait(false);
        return (descriptor, record, manifest);
    }

    private static void CheckDefault(OciRecord meta, OciRecord version)
    {
        if (OciJson.Boolean(version.Entity, "isdefault") !=
            (OciJson.Text(meta.Entity, "defaultversionid") == OciJson.Text(version.Entity, "versionid")))
        {
            throw OciJson.Invalid("Version isdefault disagrees with Resource Meta.");
        }
    }

    private async ValueTask<OciResourceState> DocumentSourceAsync(string xid, CancellationToken cancellationToken)
    {
        var state = await ResourceStateAsync(xid, cancellationToken).ConfigureAwait(false);
        if (!state.Meta.IsAlias) { return state; }
        var target = OciJson.Text(state.Meta.Entity, "xref");
        var next = await ResourceStateAsync(target, cancellationToken).ConfigureAwait(false);
        if (next.Meta.IsAlias)
        {
            throw new FederationException(FederationErrorCode.NotFound, "A document cannot follow a second local xref hop.");
        }
        return next;
    }
}
