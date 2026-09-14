using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

public sealed partial class OciSnapshot
{
    private async ValueTask<OciReadResult> MetadataAsync(FederationReadRequest request, CancellationToken cancellationToken)
    {
        JsonObject value;
        string selected = request.Target;
        var pointer = "";
        if (request.Operation == FederationOperation.Model)
        {
            value = new()
            {
                ["model"] = OciJson.Copy(registryRecord.GetProperty("entity").GetProperty("model")),
                ["modelsource"] = OciJson.Copy(ModelSource),
                ["resolvedmodelsource"] = OciJson.Copy(ResolvedModelSource),
            };
        }
        else if (request.Operation == FederationOperation.Capabilities)
        {
            value = OciJson.Copy(Capabilities)!.AsObject();
        }
        else if (request.Operation == FederationOperation.Collection)
        {
            var (descriptor, kind, state) = await CollectionAsync(request.Target, cancellationToken).ConfigureAwait(false);
            selected = descriptor.Xid;
            var entries = await EntriesAsync(descriptor, "collection", request.Target, cancellationToken).ConfigureAwait(false);
            if (request.Selector is { } selector)
            {
                OciDescriptor? match = null;
                foreach (var entry in entries)
                {
                    var labels = await SelectionLabelsAsync(entry, kind, state, cancellationToken).ConfigureAwait(false);
                    if (!selector.Matches(labels)) { continue; }
                    if (match is not null)
                    {
                        throw new FederationException(FederationErrorCode.Ambiguous, "More than one entity matches the literal label.");
                    }
                    match = entry;
                }
                if (match is null) { throw new FederationException(FederationErrorCode.NotFound, "No entity matches the literal label."); }
                selected = match.Xid;
                value = await MaterializeAsync(match, kind, "", 1, state, cancellationToken).ConfigureAwait(false);
                selected = value["xid"]!.GetValue<string>();
            }
            else
            {
                value = new();
                foreach (var entry in entries)
                {
                    var id = OciFormat.Xid(entry.Xid)[^1];
                    value.Add(id, await MaterializeAsync(entry, kind, "/" + OciJson.PointerToken(id), 1, state, cancellationToken).ConfigureAwait(false));
                }
                if (state is not null) { CheckVersionSet(state, entries.Select(e => records[GetNode(e).Config!.Digest]).ToArray(), cancellationToken); }
            }
        }
        else
        {
            var parts = OciFormat.Xid(request.Target);
            if (parts.Length == 6)
            {
                var owner = OciFormat.SerializedPrefix(request.Target, 4);
                var state = await ResourceStateAsync(owner, cancellationToken).ConfigureAwait(false);
                var (descriptor, _, _) = await VersionAsync(state, parts[5], cancellationToken, request.Target).ConfigureAwait(false);
                value = await MaterializeAsync(descriptor, "version", "", 1, state, cancellationToken).ConfigureAwait(false);
                selected = value["xid"]!.GetValue<string>();
            }
            else
            {
                var xid = parts.Length == 5 ? OciFormat.SerializedPrefix(request.Target, 4) : request.Target;
                var (descriptor, node) = await EntityNodeAsync(xid, cancellationToken).ConfigureAwait(false);
                value = await MaterializeAsync(descriptor, node.Kind, "", 1, null, cancellationToken).ConfigureAwait(false);
                if (parts.Length == 5) { pointer = "/meta"; }
                selected = (parts.Length == 5 ? value["meta"]! : value)["xid"]!.GetValue<string>();
            }
        }
        return new(request.Target, selected, Context, OciEncoding.Result(value, session.Budget, cancellationToken), pointer);
    }

    private OciNode GetNode(OciDescriptor descriptor) => session.CachedNode(descriptor.Digest);

    private async ValueTask<JsonObject> MaterializeAsync(OciDescriptor descriptor, string kind, string pointer,
        int depth, OciResourceState? owner, CancellationToken cancellationToken)
    {
        session.Budget.CheckDepth(depth);
        session.Budget.ChargeWork();
        if (kind == "version")
        {
            var version = await RecordAsync(descriptor, kind, descriptor.Xid, cancellationToken).ConfigureAwait(false);
            CheckDefault(owner!.Meta, version);
            var metadata = CopyMetadata(version);
            metadata["self"] = "#" + pointer;
            return metadata;
        }
        var node = await session.NodeAsync(descriptor, kind, descriptor.Xid, cancellationToken).ConfigureAwait(false);
        var record = await RecordAsync(node.Entries[0], kind, node.Xid, cancellationToken).ConfigureAwait(false);
        var result = CopyMetadata(record);
        result["self"] = "#" + pointer;
        OciResourceState? state = null;
        if (kind == "resource")
        {
            var meta = await RecordAsync(node.Entries[1], "meta", node.Xid + "/meta", cancellationToken).ConfigureAwait(false);
            state = new(node, record, meta);
            var metaResult = CopyMetadata(meta);
            metaResult["self"] = "#" + pointer + "/meta";
            if (!meta.IsAlias)
            {
                metaResult["defaultversionurl"] = "#" + pointer + "/versions/" + OciJson.PointerToken(OciJson.Text(meta.Entity, "defaultversionid"));
            }
            result["meta"] = metaResult;
            result["metaurl"] = "#" + pointer + "/meta";
        }
        var collections = await DirectoryAsync(node, state?.Meta.IsAlias == true, cancellationToken).ConfigureAwait(false);
        foreach (var collection in collections)
        {
            var name = OciFormat.Xid(collection.Xid, true)[^1];
            var entries = await EntriesAsync(collection, "collection", collection.Xid, cancellationToken).ConfigureAwait(false);
            var childKind = kind == "registry" ? "group" : kind == "group" ? "resource" : "version";
            var map = new JsonObject();
            var collectionPointer = pointer + "/" + OciJson.PointerToken(name);
            foreach (var child in entries)
            {
                var id = OciFormat.Xid(child.Xid)[^1];
                map.Add(id, await MaterializeAsync(child, childKind, collectionPointer + "/" + OciJson.PointerToken(id),
                    depth + 1, state, cancellationToken).ConfigureAwait(false));
            }
            result[name] = map;
            result[name + "count"] = entries.Count;
            result[name + "url"] = "#" + collectionPointer;
            if (state is not null)
            {
                CheckVersionSet(state, entries.Select(e => records[GetNode(e).Config!.Digest]).ToArray(), cancellationToken);
            }
        }
        return result;
    }

    private static JsonObject CopyMetadata(OciRecord record)
    {
        var result = OciJson.Copy(record.SemanticEntity)!.AsObject();
        foreach (var property in record.Entity.EnumerateObject())
        {
            // Keep captured null metadata unless Core supplied an effective default for that field.
            if (property.Value.ValueKind == JsonValueKind.Null && !result.ContainsKey(property.Name))
            {
                result[property.Name] = null;
            }
        }
        if (record.Kind == "registry")
        {
            foreach (var name in new[] { "model", "modelsource", "capabilities" })
            {
                result[name] = OciJson.Copy(record.Entity.GetProperty(name));
            }
        }
        return result;
    }

    private async ValueTask<List<OciDescriptor>> DirectoryAsync(OciNode node, bool alias, CancellationToken cancellationToken)
    {
        var entries = await EntriesAsync(node.Entries[^1], "collections", node.Xid, cancellationToken).ConfigureAwait(false);
        if (!entries.Select(e => OciFormat.CanonicalXid(e.Xid, true)).Order(StringComparer.Ordinal)
            .SequenceEqual(OciRecords.Collections(Model, node.Kind, node.Xid, alias)
                .Select(value => OciFormat.CanonicalXid(value, true)).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw OciJson.Invalid("The directory must contain exactly the captured model's collections, including empty collections.");
        }
        return entries;
    }

    private async ValueTask<(OciDescriptor Descriptor, string Kind, OciResourceState? State)> CollectionAsync(
        string xid, CancellationToken cancellationToken)
    {
        var parts = OciFormat.Xid(xid, true);
        var owner = parts.Length == 1 ? "/" : xid[..xid.LastIndexOf('/')];
        var (_, node) = await EntityNodeAsync(owner, cancellationToken).ConfigureAwait(false);
        OciResourceState? state = null;
        if (parts.Length == 5)
        {
            state = await ResourceStateAsync(owner, cancellationToken).ConfigureAwait(false);
            if (state.Meta.IsAlias)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "Alias Versions cannot be expanded in Core document view.", "cannot_doc_xref");
            }
        }
        return (await ChildCollectionAsync(node, xid, cancellationToken).ConfigureAwait(false),
            parts.Length == 1 ? "group" : parts.Length == 3 ? "resource" : "version", state);
    }

    private async ValueTask<JsonElement> SelectionLabelsAsync(OciDescriptor descriptor, string kind,
        OciResourceState? owner, CancellationToken cancellationToken)
    {
        OciRecord record;
        if (kind == "resource")
        {
            OciResourceState state;
            try { state = await DocumentSourceAsync(descriptor.Xid, cancellationToken).ConfigureAwait(false); }
            catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound) { return default; }
            (_, record, _) = await VersionAsync(state, OciJson.Text(state.Meta.Entity, "defaultversionid"), cancellationToken).ConfigureAwait(false);
        }
        else if (kind == "version")
        {
            record = await RecordAsync(descriptor, kind, descriptor.Xid, cancellationToken).ConfigureAwait(false);
            CheckDefault(owner!.Meta, record);
        }
        else
        {
            var node = await session.NodeAsync(descriptor, kind, descriptor.Xid, cancellationToken).ConfigureAwait(false);
            record = await RecordAsync(node.Entries[0], kind, descriptor.Xid, cancellationToken).ConfigureAwait(false);
        }
        return record.SemanticEntity.TryGetProperty("labels", out var labels) ? labels : default;
    }
}
