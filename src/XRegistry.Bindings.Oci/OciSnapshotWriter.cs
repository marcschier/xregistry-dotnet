// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>Deterministic native OCI construction from a caller's coherent, immutable capture.</summary>
public static class OciSnapshotWriter
{
    /// <summary>Builds leaves through root and exhaustively validates the same graph consumed by OciSnapshot before returning it.</summary>
    /// <remarks>No network, tar wrapping, compression, domain parsing or placeholder substitution is performed.</remarks>
    public static async ValueTask<OciSnapshotPackage> CreateAsync(OciSnapshotInput input, OciWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new FederationReadBudget(options.Limits);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var value in input.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeObject();
            var data = value.RootElement;
            var size = Encoding.UTF8.GetByteCount(data.GetRawText());
            budget.CheckObjectBytes(size);
            budget.ChargeBytes(size);
            OciJson.Charge(data, budget, cancellationToken);
            var entity = OciJson.Required(data, "entity");
            var xid = OciFormat.CanonicalXid(OciJson.Text(entity, "xid"));
            var normalized = OciJson.Copy(data)!.AsObject();
            normalized["entity"]!["xid"] = xid;
            if (OciJson.Text(data, "kind") == "meta" && entity.TryGetProperty("xref", out _))
            {
                normalized["entity"]!["xref"] = OciFormat.CanonicalXid(OciJson.Text(entity, "xref"));
            }
            data = OciJson.Parse(OciEncoding.Canonical(normalized, budget.Limits.MaxObjectBytes), budget, cancellationToken);
            if (!values.TryAdd(xid, data)) { throw OciJson.Invalid("Duplicate producer record XID."); }
        }
        if (!values.TryGetValue("/", out var root)) { throw OciJson.Invalid("Producer input requires one Registry record."); }
        var model = OciSnapshot.Bootstrap(root, budget);
        var records = new Dictionary<string, OciRecord>(StringComparer.Ordinal);
        long normalizedRecordBytes = 0;
        foreach (var value in values.Where(value => OciJson.Text(value.Value, "kind") != "version")
            .Concat(values.Where(value => OciJson.Text(value.Value, "kind") == "version")))
        {
            var kind = OciJson.Text(value.Value, "kind");
            JsonElement group = default;
            if (kind == "version")
            {
                var parts = OciFormat.Xid(value.Key);
                if (records.TryGetValue(OciFormat.FromParts(parts.Take(2)), out var owner)) { group = owner.SemanticEntity; }
            }
            var record = OciRecords.Validate(value.Value, kind, value.Key, model, budget, cancellationToken, group);
            OciRecords.RetainNormalized(record, ref normalizedRecordBytes, budget);
            records.Add(value.Key, record);
        }
        var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var embedded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.Values)
        {
            var parts = OciFormat.Xid(record.Xid);
            var expectedParts = record.Kind switch { "registry" => 0, "group" => 2, "resource" => 4, "meta" => 5, "version" => 6, _ => -1 };
            if (parts.Length != expectedParts) { throw OciJson.Invalid("Producer record kind and typed XID disagree."); }
            var parent = record.Kind switch
            {
                "registry" => null,
                "group" => "/",
                "resource" => OciFormat.FromParts(parts.Take(2)),
                "meta" or "version" => OciFormat.FromParts(parts.Take(4)),
                _ => null,
            };
            if (parent is not null && !records.ContainsKey(parent)) { throw OciJson.Invalid("A producer record is missing its structural parent."); }
            if (record.Kind is "group" or "resource" or "version" && !siblings.Add(record.Xid))
            {
                throw OciJson.Invalid("Producer sibling IDs collide under Core case-insensitive comparison.");
            }
            if (record.Kind == "resource" && !records.ContainsKey(record.Xid + "/meta"))
            {
                throw OciJson.Invalid("A producer Resource is missing its Meta record.");
            }
            if (record.Kind == "version" && OciJson.Text(record.Data.GetProperty("document"), "mode") == "embedded") { embedded.Add(record.Xid); }
        }
        var documents = new Dictionary<string, FederationDocument>(StringComparer.Ordinal);
        foreach (var item in input.Documents)
        {
            budget.ChargeWork();
            if (!documents.TryAdd(OciFormat.CanonicalXid(item.Key), item.Value))
            {
                throw OciJson.Invalid("Different producer document URI spellings identify the same Version.");
            }
        }
        if (!embedded.SetEquals(documents.Keys))
        {
            throw OciJson.Invalid("Producer document bytes must match exactly the embedded Version records.");
        }
        var builder = new Builder(records, model, documents, options, budget, cancellationToken);
        var rootObject = await builder.BuildAsync().ConfigureAwait(false);
        var objects = builder.Objects.ToArray();
        var snapshot = await OciSnapshot.OpenAsync(new OciSnapshotPackage.PackageReader(rootObject.Digest, objects),
            rootObject.Digest, budget, cancellationToken).ConfigureAwait(false);
        await using (snapshot.ConfigureAwait(false))
        {
            var validation = await snapshot.ValidateAsync(cancellationToken).ConfigureAwait(false);
            if (validation.Configs != records.Count) { throw OciJson.Invalid("The producer graph did not retain every input record."); }
            return new(rootObject, objects, validation);
        }
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, OciRecord> records;
        private readonly RegistryModel model;
        private readonly IReadOnlyDictionary<string, FederationDocument> documents;
        private readonly OciWriteOptions options;
        private readonly FederationReadBudget budget;
        private readonly CancellationToken cancellationToken;
        private readonly Dictionary<(bool Manifest, string Digest), OciSnapshotObject> objects = [];
        private readonly Dictionary<string, OciRecord[]> collections;
        internal List<OciSnapshotObject> Objects { get; } = [];

        internal Builder(Dictionary<string, OciRecord> records, RegistryModel model,
            IReadOnlyDictionary<string, FederationDocument> documents, OciWriteOptions options,
            FederationReadBudget budget, CancellationToken cancellationToken)
        {
            this.records = records;
            this.model = model;
            this.documents = documents;
            this.options = options;
            this.budget = budget;
            this.cancellationToken = cancellationToken;
            collections = records.Values.Where(r => r.Kind is "group" or "resource" or "version")
                .GroupBy(r => r.Xid[..r.Xid.LastIndexOf('/')], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Xid, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        }

        internal async ValueTask<OciSnapshotObject> BuildAsync()
        {
            var root = await EntityAsync("/", 1).ConfigureAwait(false);
            return objects[(true, root.Digest)];
        }

        private async ValueTask<OciDescriptor> EntityAsync(string xid, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            budget.CheckDepth(depth);
            var record = records[xid];
            if (record.Kind == "version") { return await ManifestAsync(record).ConfigureAwait(false); }
            var entries = new List<OciDescriptor> { Edge(await ManifestAsync(record).ConfigureAwait(false), "metadata", xid) };
            var alias = false;
            if (record.Kind == "resource")
            {
                var meta = records[xid + "/meta"];
                alias = meta.IsAlias;
                if (alias && collections.ContainsKey(xid + "/versions")) { throw OciJson.Invalid("An alias cannot store target Versions under its own identity."); }
                entries.Add(Edge(await ManifestAsync(meta).ConfigureAwait(false), "meta", meta.Xid));
            }
            var directory = new List<OciDescriptor>();
            foreach (var collection in OciRecords.Collections(model, record.Kind, xid, alias))
            {
                var children = new List<OciDescriptor>();
                foreach (var child in collections.GetValueOrDefault(collection) ?? [])
                {
                    children.Add(Edge(await EntityAsync(child.Xid, depth + 1).ConfigureAwait(false), "entity", child.Xid));
                }
                directory.Add(Edge(Route("collection", collection, children, "", "", 1), "collection", collection));
            }
            entries.Add(Edge(Route("collections", xid, directory, "", "", 1), "collections", xid));
            return PutNode(Node(record.Kind, xid, entries), options.MaxIndexBytes);
        }

        private async ValueTask<OciDescriptor> ManifestAsync(OciRecord record)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configBytes = OciEncoding.Canonical(OciJson.Copy(record.Data)!, budget.Limits.MaxObjectBytes);
            var config = Edge(Put(configBytes, OciFormat.Config), "config", record.Xid);
            OciDescriptor layer;
            if (record.Kind == "version" && OciJson.Text(record.Data.GetProperty("document"), "mode") == "embedded")
            {
                var document = documents[record.Xid];
                CheckDocumentContext(record, document);
                budget.CheckObjectBytes(document.Length);
                using var stream = document.OpenRead();
                var bytes = await OciObjectSession.ReadAsync(stream, budget, budget.Limits.MaxObjectBytes,
                    cancellationToken, document.Length).ConfigureAwait(false);
                layer = Edge(Put(bytes, OciFormat.Document), "document", record.Xid);
            }
            else { layer = Edge(Put("{}"u8.ToArray(), OciFormat.Empty), "empty", record.Xid); }
            var manifest = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["mediaType"] = OciFormat.Manifest,
                ["artifactType"] = OciFormat.Artifact(record.Kind == "version" ? "version" : "metadata"),
                ["annotations"] = Annotations(record.Kind, record.Xid),
                ["config"] = Descriptor(config),
                ["layers"] = new JsonArray(Descriptor(layer)),
            };
            return PutNode(manifest, budget.Limits.MaxObjectBytes);
        }

        private static void CheckDocumentContext(OciRecord record, FederationDocument document)
        {
            var state = record.Data.GetProperty("document");
            var type = record.SemanticEntity.TryGetProperty("contenttype", out var value) ? value.GetString() : "application/octet-stream";
            var baseUri = state.TryGetProperty("base", out value) ? value.GetString() : null;
            var origin = state.TryGetProperty("origin", out value) ? value.GetString() : null;
            if (document.ContentType is not null && document.ContentType != type ||
                document.Base is not null && document.Base != baseUri || document.Origin is not null && document.Origin != origin)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot, "Captured document context contradicts its Version metadata.");
            }
        }

        private OciDescriptor Route(string kind, string xid, List<OciDescriptor> entries, string lower, string upper, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.CheckDepth(depth);
            budget.ChargeWork(entries.Count + 1);
            if (entries.Count <= options.MaxDescriptorsPerIndex)
            {
                var leaf = Node(kind, xid, entries, "leaf", lower, upper);
                var bytes = TryEncode(leaf);
                if (bytes is not null) { return AcceptNode(leaf, bytes); }
            }
            if (entries.Count < 2)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded, "An individual routing entry cannot fit the exact index byte bound.");
            }
            var middle = entries.Count / 2;
            var boundary = entries[middle].Xid;
            var left = Route(kind, xid, entries.GetRange(0, middle), lower, boundary, depth + 1);
            var right = Route(kind, xid, entries.GetRange(middle, entries.Count - middle), boundary, upper, depth + 1);
            var branch = Node(kind, xid,
                [Edge(left, "shard", xid, lower, boundary), Edge(right, "shard", xid, boundary, upper)], "branch", lower, upper);
            return PutNode(branch, options.MaxIndexBytes);
        }

        private byte[]? TryEncode(JsonObject node)
        {
            try { return OciEncoding.Canonical(node, options.MaxIndexBytes); }
            catch (FederationException exception) when (exception.Code == FederationErrorCode.LimitExceeded) { return null; }
        }

        private OciDescriptor PutNode(JsonObject node, int maxBytes) => AcceptNode(node, OciEncoding.Canonical(node, maxBytes));

        private OciDescriptor AcceptNode(JsonObject node, byte[] bytes)
        {
            var parsed = OciFormat.Node(OciJson.Parse(bytes, budget, cancellationToken), bytes.Length);
            return Put(bytes, parsed.MediaType, parsed.ArtifactType);
        }

        private OciDescriptor Put(byte[] bytes, string mediaType, string? artifactType = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.CheckObjectBytes(bytes.Length);
            var item = new OciSnapshotObject(bytes, mediaType, artifactType);
            if (objects.TryAdd((item.IsManifest, item.Digest), item))
            {
                budget.ChargeObject();
                budget.ChargeBytes(bytes.Length);
                Objects.Add(item);
            }
            return new(mediaType, item.Digest, item.Size, artifactType, "", "");
        }
    }

    private static OciDescriptor Edge(OciDescriptor value, string role, string xid, string? lower = null, string? upper = null) =>
        value with { Role = role, Xid = xid, Lower = lower, Upper = upper };

    private static JsonObject Annotations(string kind, string xid) => new()
    {
        [OciFormat.Prefix + "version"] = "1",
        [OciFormat.Prefix + "kind"] = kind,
        [OciFormat.Prefix + "xid"] = xid,
    };

    private static JsonObject Node(string kind, string xid, List<OciDescriptor> entries,
        string? mode = null, string? lower = null, string? upper = null)
    {
        var annotations = Annotations(kind, xid);
        if (mode is not null)
        {
            annotations[OciFormat.Prefix + "mode"] = mode;
            annotations[OciFormat.Prefix + "lower"] = lower;
            annotations[OciFormat.Prefix + "upper"] = upper;
        }
        return new()
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = OciFormat.Index,
            ["artifactType"] = OciFormat.Artifact(kind),
            ["annotations"] = annotations,
            ["manifests"] = new JsonArray(entries.Select(e => (JsonNode)Descriptor(e)).ToArray()),
        };
    }

    private static JsonObject Descriptor(OciDescriptor descriptor)
    {
        var annotations = new JsonObject { [OciFormat.Prefix + "role"] = descriptor.Role, [OciFormat.Prefix + "xid"] = descriptor.Xid };
        if (descriptor.Lower is not null)
        {
            annotations[OciFormat.Prefix + "lower"] = descriptor.Lower;
            annotations[OciFormat.Prefix + "upper"] = descriptor.Upper;
        }
        var value = new JsonObject
        {
            ["mediaType"] = descriptor.MediaType,
            ["digest"] = descriptor.Digest,
            ["size"] = descriptor.Size,
            ["annotations"] = annotations,
        };
        if (descriptor.ArtifactType is not null) { value["artifactType"] = descriptor.ArtifactType; }
        return value;
    }
}
