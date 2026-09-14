using System.Buffers;
using System.Text.Json;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

internal static class OciFormat
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");
    internal const string Index = "application/vnd.oci.image.index.v1+json";
    internal const string Manifest = "application/vnd.oci.image.manifest.v1+json";
    internal const string Config = "application/vnd.xregistry.entity.v1+json";
    internal const string Document = "application/vnd.xregistry.document.v1";
    internal const string Empty = "application/vnd.oci.empty.v1+json";
    internal const string EmptyDigest = "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";
    internal const string Prefix = "io.xregistry.oci.";
    internal const int MaxIndexBytes = 1_048_576;
    internal const int MaxDescriptors = 256;

    internal static string Artifact(string kind) => "application/vnd.xregistry." + kind + ".v1+json";
    internal static string Annotation(JsonElement value, string name) =>
        OciJson.Text(OciJson.Required(value, "annotations"), Prefix + name, true);

    internal static bool IsDigest(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(HexDigits) < 0;

    internal static void Digest(string value)
    {
        if (!IsDigest(value)) { throw OciJson.Invalid("A lowercase SHA-256 digest is required."); }
    }

    internal static void Reference(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (IsDigest(value)) { return; }
        if (value.Length is < 1 or > 128 || !(char.IsAsciiLetterOrDigit(value[0]) || value[0] == '_') ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')))
        {
            throw OciJson.Invalid("An explicit Distribution tag or lowercase SHA-256 reference is required.");
        }
    }

    internal static string[] Xid(string xid, bool collection = false)
    {
        if (xid == "/" && !collection) { return []; }
        if (xid.Length > 8192)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The typed XID exceeds its path budget.");
        }
        if (!xid.StartsWith('/')) { throw OciJson.Invalid("A typed Core XID is required."); }
        var encoded = xid[1..].Split('/');
        if (encoded.Length > 6) { throw OciJson.Invalid("A typed Core XID has too many components."); }
        string[] parts;
        try { parts = encoded.Select(part => RegistryId.ParseEscaped(part).Value).ToArray(); }
        catch (RegistryException exception) { throw OciJson.CoreError(exception); }
        if (!Name(parts[0]) || parts.Length >= 3 && !Name(parts[2]) ||
            parts.Length == 6 && parts[5] is "null" or "request")
        {
            throw OciJson.Invalid("The XID has invalid Core type or identifier syntax.");
        }
        var valid = collection ? parts.Length is 1 or 3 || parts.Length == 5 && parts[4] == "versions" :
            parts.Length is 2 or 4 || parts.Length == 5 && parts[4] == "meta" || parts.Length == 6 && parts[4] == "versions";
        if (!valid) { throw OciJson.Invalid("The XID does not have the required entity or collection shape."); }
        return parts;
    }

    internal static string CanonicalXid(string value, bool collection = false) =>
        FromParts(Xid(value, collection));

    internal static string FromParts(IEnumerable<string> parts) =>
        "/" + string.Join('/', parts.Select(Uri.EscapeDataString));

    internal static string SerializedPrefix(string xid, int componentCount) =>
        "/" + string.Join('/', xid[1..].Split('/').Take(componentCount));

    internal static bool SameXid(string left, string right, bool collection = false) =>
        Xid(left, collection).SequenceEqual(Xid(right, collection), StringComparer.Ordinal);

    private static void RequireCanonicalXid(string value, bool collection = false)
    {
        if (!StringComparer.Ordinal.Equals(value, CanonicalXid(value, collection)))
        {
            throw OciJson.Invalid("Noncanonical OCI identity or routing key; explicitly migrate or regenerate the draft layout.");
        }
    }

    private static bool Name(string value) => value.Length is >= 1 and <= 57 &&
        (value[0] is >= 'a' and <= 'z' || value[0] == '_') &&
        value.All(c => c is >= 'a' and <= 'z' || char.IsAsciiDigit(c) || c == '_');

    internal static void Annotations(JsonElement value, bool internalEdge = false, bool profile = true)
    {
        OciJson.Object(value);
        foreach (var item in value.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.String ||
                profile && item.Name.StartsWith(Prefix, StringComparison.Ordinal) &&
                item.Name[Prefix.Length..] is not ("version" or "kind" or "xid" or "role" or "mode" or "lower" or "upper"))
            {
                throw OciJson.Invalid("An OCI annotation is malformed or unknown to profile version 1.");
            }
            if (!profile) { continue; }
            if (internalEdge && item.Name == "org.opencontainers.image.ref.name")
            {
                throw OciJson.Invalid("Tags are not permitted on internal descriptor edges.");
            }
            var text = item.Value.GetString();
            if (item.Name == Prefix + "role" &&
                text is not ("root" or "metadata" or "meta" or "collections" or "collection" or "entity" or "shard" or "config" or "document" or "empty") ||
                item.Name == Prefix + "mode" && text is not ("leaf" or "branch"))
            {
                throw OciJson.Invalid("A defined OCI annotation has an invalid value.");
            }
            if (item.Name == Prefix + "version" && text != "1")
            {
                throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported OCI annotation format version.");
            }
            if (item.Name == Prefix + "kind" && text is not ("registry" or "group" or "resource" or "meta" or "version" or "collections" or "collection"))
            {
                throw new FederationException(FederationErrorCode.UnsupportedBinding, "Unsupported required OCI artifact kind.");
            }
        }
    }

    internal static OciDescriptor Descriptor(JsonElement value)
    {
        OciJson.Fields(value, "mediaType", "digest", "size", "artifactType", "annotations");
        var mediaType = OciJson.Text(value, "mediaType");
        var digest = OciJson.Text(value, "digest");
        Digest(digest);
        var size = OciJson.Integer(value, "size");
        var annotations = OciJson.Required(value, "annotations");
        Annotations(annotations, true);
        var role = Annotation(value, "role");
        var xid = Annotation(value, "xid");
        if (role is not ("metadata" or "meta" or "collections" or "collection" or "entity" or "shard" or "config" or "document" or "empty"))
        {
            throw OciJson.Invalid("An internal descriptor has an invalid role.");
        }
        var collection = role == "collection" ||
            role == "shard" && xid != "/" && xid.Count(character => character == '/') is 1 or 3 or 5;
        RequireCanonicalXid(xid, collection);
        string? artifact = value.TryGetProperty("artifactType", out _) ? OciJson.Text(value, "artifactType") : null;
        if (artifact is not null && mediaType is not (Index or Manifest))
        {
            throw OciJson.Invalid("Blob descriptors cannot carry artifactType.");
        }
        var lower = annotations.TryGetProperty(Prefix + "lower", out _) ? Annotation(value, "lower") : null;
        var upper = annotations.TryGetProperty(Prefix + "upper", out _) ? Annotation(value, "upper") : null;
        if (role == "shard" ? lower is null || upper is null : lower is not null || upper is not null)
        {
            throw OciJson.Invalid("Only shard edges carry both range bounds.");
        }
        if (lower is { Length: > 0 }) { RequireCanonicalXid(lower, !collection); }
        if (upper is { Length: > 0 }) { RequireCanonicalXid(upper, !collection); }
        return new(mediaType, digest, size, artifact, role, xid, lower, upper);
    }

    internal static void LayoutIndex(JsonElement value, int size)
    {
        if (OciJson.Integer(value, "schemaVersion") != 2 ||
            value.TryGetProperty("mediaType", out _) && OciJson.Text(value, "mediaType") != Index)
        {
            throw OciJson.Invalid("The OCI layout entrypoint must be a standard image index.");
        }
        IndexEntries(value, size, false);
        if (value.TryGetProperty("annotations", out var annotations)) { Annotations(annotations, profile: false); }
        foreach (var entry in value.GetProperty("manifests").EnumerateArray())
        {
            var mediaType = OciJson.Text(entry, "mediaType");
            if (!MediaType(mediaType)) { throw OciJson.Invalid("A layout descriptor must carry a valid media type."); }
            OciJson.Integer(entry, "size");
            var digest = OciJson.Text(entry, "digest");
            var colon = digest.IndexOf(':');
            if (colon <= 0 || colon == digest.Length - 1 ||
                digest[..colon].Split(['+', '.', '_', '-']).Any(p => p.Length == 0 ||
                    p.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c))) ||
                digest[(colon + 1)..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('=' or '_' or '-')))
            {
                throw OciJson.Invalid("A layout descriptor digest has invalid OCI syntax.");
            }
            if (entry.TryGetProperty("artifactType", out _) && !MediaType(OciJson.Text(entry, "artifactType")))
            {
                throw OciJson.Invalid("A layout descriptor has an invalid artifact media type.");
            }
            if (entry.TryGetProperty("annotations", out annotations)) { Annotations(annotations, profile: false); }
        }
    }

    private static bool MediaType(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length is >= 1 and <= 127 && char.IsAsciiLetterOrDigit(p[0]) &&
            p.All(c => char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-'));
    }

    internal static OciNode Node(JsonElement value, int size)
    {
        var mediaType = OciJson.Text(value, "mediaType");
        if (OciJson.Integer(value, "schemaVersion") != 2) { throw OciJson.Invalid("OCI schemaVersion must be 2."); }
        var annotations = OciJson.Required(value, "annotations");
        Annotations(annotations);
        if (Annotation(value, "version") != "1")
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported OCI profile version.");
        }
        var kind = Annotation(value, "kind");
        var xid = Annotation(value, "xid");
        if (kind is not ("registry" or "group" or "resource" or "meta" or "version" or "collections" or "collection"))
        {
            throw new FederationException(FederationErrorCode.UnsupportedBinding, "Unsupported required OCI artifact kind.");
        }
        RequireCanonicalXid(xid, kind == "collection");
        var artifact = OciJson.Text(value, "artifactType");
        if (mediaType == Index)
        {
            OciJson.Fields(value, "schemaVersion", "mediaType", "artifactType", "annotations", "manifests");
            if (kind is "meta" or "version" || artifact != Artifact(kind)) { throw OciJson.Invalid("Index kind and artifactType disagree."); }
            var entries = IndexEntries(value, size);
            var node = new OciNode(kind, xid, mediaType, artifact, entries, null, null,
                kind is "collection" or "collections" ? Annotation(value, "mode") : null,
                kind is "collection" or "collections" ? Annotation(value, "lower") : null,
                kind is "collection" or "collections" ? Annotation(value, "upper") : null);
            if (kind is "registry" or "group" or "resource") { EntityControls(node); }
            else { Routing(node); }
            return node;
        }
        if (mediaType != Manifest) { throw OciJson.Invalid("An index or manifest must use its standard OCI media type."); }
        OciJson.Fields(value, "schemaVersion", "mediaType", "artifactType", "annotations", "config", "layers");
        if (kind is "collection" or "collections" || artifact != Artifact(kind == "version" ? "version" : "metadata"))
        {
            throw OciJson.Invalid("Manifest kind and artifactType disagree.");
        }
        var config = Descriptor(OciJson.Required(value, "config"));
        Require(config, "config", xid, Config);
        var layers = OciJson.Required(value, "layers");
        if (layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() != 1)
        {
            throw OciJson.Invalid("A profile manifest requires exactly one layer.");
        }
        var layer = Descriptor(layers[0]);
        if (layer.Role == "empty") { EmptyLayer(layer, xid); }
        else if (kind == "version") { Require(layer, "document", xid, Document); }
        else { throw OciJson.Invalid("Metadata manifests require an unused layer."); }
        return new(kind, xid, mediaType, artifact, [], config, layer, null, null, null);
    }

    internal static OciDescriptor[] IndexEntries(JsonElement value, int size, bool profile = true)
    {
        if (size > MaxIndexBytes)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "An index exceeds 1,048,576 exact encoded bytes.");
        }
        var entries = OciJson.Required(value, "manifests");
        if (entries.ValueKind != JsonValueKind.Array) { throw OciJson.Invalid("An OCI index requires a manifests array."); }
        if (entries.GetArrayLength() > MaxDescriptors)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "An index exceeds 256 descriptors.");
        }
        return profile ? entries.EnumerateArray().Select(Descriptor).ToArray() : [];
    }

    internal static void Require(OciDescriptor descriptor, string role, string xid, string mediaType, bool collection = false)
    {
        collection |= role == "collection";
        if (descriptor.Role != role || !SameXid(descriptor.Xid, xid, collection) || descriptor.MediaType != mediaType)
        {
            throw OciJson.Invalid("A descriptor role, XID or mediaType disagrees with its containment edge.");
        }
    }

    internal static void EmptyLayer(OciDescriptor descriptor, string xid)
    {
        Require(descriptor, "empty", xid, Empty);
        if (descriptor.Size != 2 || descriptor.Digest != EmptyDigest) { throw OciJson.Invalid("An unused layer must name the exact two-byte OCI empty JSON blob."); }
    }

    private static void EntityControls(OciNode node)
    {
        Xid(node.Xid);
        var count = node.Kind == "resource" ? 3 : 2;
        if (node.Entries.Length != count) { throw OciJson.Invalid("Entity indexes require exactly two or three ordered controls."); }
        Require(node.Entries[0], "metadata", node.Xid, Manifest);
        if (count == 3) { Require(node.Entries[1], "meta", node.Xid + "/meta", Manifest); }
        Require(node.Entries[^1], "collections", node.Xid, Index);
    }

    internal static void Key(string key, string kind, string xid)
    {
        var parts = Xid(key, kind == "collections");
        if (kind == "collections")
        {
            var owner = Xid(xid);
            if (parts.Length != owner.Length + 1 || !parts.Take(owner.Length).SequenceEqual(owner, StringComparer.Ordinal))
            {
                throw OciJson.Invalid("A directory entry is outside its owning entity.");
            }
        }
        else
        {
            var owner = Xid(xid, true);
            if (parts.Length != owner.Length + 1 || !parts.Take(owner.Length).SequenceEqual(owner, StringComparer.Ordinal))
            {
                throw OciJson.Invalid("A collection entry is not an immediate typed child.");
            }
        }
    }

    internal static bool Inside(string key, string lower, string upper) =>
        (lower.Length == 0 || string.CompareOrdinal(key, lower) >= 0) &&
        (upper.Length == 0 || string.CompareOrdinal(key, upper) < 0);

    private static void Range(string kind, string xid, string lower, string upper)
    {
        if (lower.Length != 0) { RequireCanonicalXid(lower, kind == "collections"); Key(lower, kind, xid); }
        if (upper.Length != 0) { RequireCanonicalXid(upper, kind == "collections"); Key(upper, kind, xid); }
        if (lower.Length != 0 && upper.Length != 0 && string.CompareOrdinal(lower, upper) >= 0)
        {
            throw OciJson.Invalid("A finite routing range must increase strictly.");
        }
    }

    private static void Routing(OciNode node)
    {
        if (node.Kind == "collections") { Xid(node.Xid); }
        else { Xid(node.Xid, true); }
        Range(node.Kind, node.Xid, node.Lower!, node.Upper!);
        if (node.Mode == "leaf")
        {
            if (node.Entries.Length == 0 && (node.Lower!.Length != 0 || node.Upper!.Length != 0))
            {
                throw OciJson.Invalid("An empty routing index must be an unsharded whole-space leaf.");
            }
            string? previous = null;
            foreach (var entry in node.Entries)
            {
                Key(entry.Xid, node.Kind, node.Xid);
                var mediaType = node.Kind == "collection" && Xid(node.Xid, true).Length == 5 ? Manifest : Index;
                Require(entry, node.Kind == "collections" ? "collection" : "entity", entry.Xid, mediaType);
                if (!Inside(entry.Xid, node.Lower!, node.Upper!) ||
                    previous is not null && string.CompareOrdinal(previous, entry.Xid) >= 0)
                {
                    throw OciJson.Invalid("Leaf keys must be unique, strictly sorted and inside their range.");
                }
                previous = entry.Xid;
            }
        }
        else if (node.Mode == "branch")
        {
            if (node.Entries.Length < 2) { throw OciJson.Invalid("A branch requires at least two nonempty shards."); }
            var previousUpper = node.Lower!;
            for (var i = 0; i < node.Entries.Length; i++)
            {
                var entry = node.Entries[i];
                Require(entry, "shard", node.Xid, Index, node.Kind == "collection");
                Range(node.Kind, node.Xid, entry.Lower!, entry.Upper!);
                if (entry.Lower != previousUpper || i > 0 && entry.Lower!.Length == 0 ||
                    i < node.Entries.Length - 1 && entry.Upper!.Length == 0)
                {
                    throw OciJson.Invalid("Shard intervals must form one ordered partition without gaps or overlaps.");
                }
                previousUpper = entry.Upper!;
            }
            if (previousUpper != node.Upper) { throw OciJson.Invalid("The final shard must end at its parent's upper bound."); }
        }
        else { throw OciJson.Invalid("A routing index must be a leaf or branch."); }
    }
}

internal sealed record OciDescriptor(string MediaType, string Digest, long Size, string? ArtifactType,
    string Role, string Xid, string? Lower = null, string? Upper = null);

internal sealed record OciNode(string Kind, string Xid, string MediaType, string ArtifactType,
    OciDescriptor[] Entries, OciDescriptor? Config, OciDescriptor? Layer,
    string? Mode, string? Lower, string? Upper);
