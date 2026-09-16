// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;

namespace XRegistry.Federation;

internal sealed record TreeReference(string Kind, string Xid, string Href, long Size, string Sha256);

internal sealed record TreeObject(string Kind, string Xid, JsonElement Data,
    IReadOnlyList<TreeReference> Children, TreeReference? Meta)
{
    internal JsonElement Entity => FederationJson.Required(Data, "entity");
}

internal static class DocumentTreeFormat
{
    internal static string XidPath(IEnumerable<string> parts) =>
        "/" + string.Join('/', parts.Select(Uri.EscapeDataString));

    private static string IdentityKey(string xid, bool collection) =>
        XidPath(FederationSyntax.Xid(xid, collection));

    private static readonly string[] s_forbidden =
    [
        "self", "shortself", "metaurl", "defaultversionurl", "formatvalidated",
        "formatvalidatedreason", "compatibilityvalidated", "compatibilityvalidatedreason",
    ];

    internal static TreeObject Parse(JsonElement data, RegistryModel? model)
    {
        if (FederationJson.String(data, "format") != "xregistry-document-tree")
        {
            throw FederationJson.Invalid("Unknown document-tree format.");
        }
        if (FederationJson.String(data, "formatversion") != "1")
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported document-tree format version.");
        }
        var kind = FederationJson.String(data, "kind");
        if (kind == "collection")
        {
            FederationJson.Fields(data, "format", "formatversion", "kind", "xid", "count", "entries");
            var xid = FederationJson.String(data, "xid");
            var parts = FederationSyntax.Xid(xid, true);
            var entries = References(FederationJson.Required(data, "entries"));
            if (Nonnegative(data, "count") != entries.Count)
            {
                throw FederationJson.Invalid("Index count disagrees with its complete entries array.");
            }
            var expectedKind = parts.Length switch { 1 => "group", 3 => "resource", _ => "version" };
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var child = FederationSyntax.Xid(entry.Xid);
                if (entry.Kind != expectedKind || child.Length != parts.Length + 1 ||
                    !child.Take(parts.Length).SequenceEqual(parts, StringComparer.Ordinal) || !ids.Add(child[^1]))
                {
                    throw FederationJson.Invalid("An index entry must be a unique immediate typed member.");
                }
            }
            return new(kind, xid, data, entries, null);
        }
        FederationJson.Fields(data, "format", "formatversion", "kind", "entity", "collections", "meta",
            "document", "snapshot", "resolvedmodelsource", "modelbase", "source");
        var entity = FederationJson.Required(data, "entity");
        var entityXid = FederationJson.String(entity, "xid");
        var entityParts = FederationSyntax.Xid(entityXid);
        var shape = entityParts.Length switch { 0 => "registry", 2 => "group", 4 => "resource", 5 => "meta", _ => "version" };
        if (kind != shape)
        {
            throw FederationJson.Invalid("Record kind and typed XID disagree.");
        }
        IReadOnlyList<TreeReference> children = data.TryGetProperty("collections", out var collections)
            ? References(collections) : [];
        TreeReference? meta = data.TryGetProperty("meta", out var metaValue) ? Reference(metaValue) : null;
        if (children.Any(child => child.Kind != "collection") || (meta is not null && meta.Kind != "meta"))
        {
            throw FederationJson.Invalid("Incorrect record reference role.");
        }
        if (kind is "registry" or "group" or "resource")
        {
            FederationJson.Required(data, "collections");
        }
        else if (data.TryGetProperty("collections", out _) || meta is not null)
        {
            throw FederationJson.Invalid("Meta and Version records have no containment references.");
        }
        if (kind != "registry")
        {
            Forbid(data, ["snapshot", "resolvedmodelsource", "modelbase", "source"]);
        }
        if (kind != "version")
        {
            Forbid(data, ["document"]);
        }
        if (kind != "resource" && meta is not null)
        {
            throw FederationJson.Invalid("Only a Resource record has a Meta reference.");
        }
        Forbid(entity, s_forbidden);
        if (entity.TryGetProperty("labels", out var labels))
        {
            FederationSyntax.Labels(labels);
        }
        if (entity.TryGetProperty("contenttype", out _))
        {
            FederationJson.String(entity, "contenttype");
        }
        if (kind == "registry")
        {
            RequireId(entity, "registryid", null);
            if (FederationJson.String(entity, "specversion") != "1.0-rc4")
            {
                throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported Core version.");
            }
            OrdinaryFields(entity);
            FederationJson.RequireObject(FederationJson.Required(entity, "modelsource"));
            var capabilities = FederationJson.Required(entity, "capabilities");
            var available = FederationJson.Required(capabilities, "available");
            FederationJson.RequireObject(available);
            var access = available.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!access.Contains("capabilities") || !access.Contains("entities") || !access.Contains("model"))
            {
                throw FederationJson.Invalid("Snapshot capabilities must declare Core capabilities, entity and model access.");
            }
            foreach (var capability in available.EnumerateObject())
            {
                FederationJson.RequireObject(capability.Value);
                if (Boolean(capability.Value, "mutable"))
                {
                    throw FederationJson.Invalid("Snapshot capabilities must not advertise mutations.");
                }
            }
            if (capabilities.TryGetProperty("flags", out var flags))
            {
                if (flags.ValueKind != JsonValueKind.Array || flags.EnumerateArray().Any(flag =>
                    flag.ValueKind != JsonValueKind.String || !(string.Equals(flag.GetString(), "doc", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(flag.GetString(), "inline", StringComparison.OrdinalIgnoreCase))))
                {
                    throw FederationJson.Invalid("A document-tree snapshot cannot advertise unsupported request flags.");
                }
            }
            if (capabilities.TryGetProperty("pagination", out _) && Boolean(capabilities, "pagination"))
            {
                throw FederationJson.Invalid("A directory mapping has complete indexes, not pagination.");
            }
            FederationCapabilities.GetResolutionOwner(capabilities);
            var snapshot = FederationJson.Required(data, "snapshot");
            FederationJson.Fields(snapshot, "scope", "completeness");
            if (FederationJson.String(snapshot, "scope") != "/" ||
                FederationJson.String(snapshot, "completeness") is not ("linked" or "offline-complete"))
            {
                throw FederationJson.Invalid("Invalid snapshot scope or completeness.");
            }
            foreach (var name in new[] { "resolvedmodelsource" })
            {
                if (data.TryGetProperty(name, out var value))
                {
                    FederationJson.RequireObject(value);
                }
            }
            if (data.TryGetProperty("modelbase", out _))
            {
                FederationJson.AbsoluteUri(FederationJson.String(data, "modelbase"));
            }
            if (data.TryGetProperty("source", out var source))
            {
                FederationJson.Fields(source, "uri", "revision");
                FederationJson.AbsoluteUri(FederationJson.String(source, "uri"));
                if (source.TryGetProperty("revision", out _))
                {
                    FederationJson.String(source, "revision");
                }
            }
        }
        else
        {
            ArgumentNullException.ThrowIfNull(model);
            var group = Group(model, entityParts);
            if (kind == "group")
            {
                RequireId(entity, group.Singular + "id", entityParts[1]);
                OrdinaryFields(entity);
            }
            else
            {
                var resource = Resource(model, entityParts);
                var idName = resource.Singular + "id";
                RequireId(entity, idName, entityParts[3]);
                if (resource.HasDocument)
                {
                    Forbid(entity, [resource.Singular, resource.Singular + "base64"]);
                }
                if (kind == "resource")
                {
                    FederationJson.Fields(entity, "xid", idName);
                    if (meta is null || !FederationSyntax.SameXid(meta.Xid, entityXid + "/meta"))
                    {
                        throw FederationJson.Invalid("A Resource requires its exact Meta reference.");
                    }
                }
                else if (kind == "meta")
                {
                    if (entity.TryGetProperty("xref", out _))
                    {
                        FederationJson.Fields(entity, "xid", idName, "xref");
                        var target = FederationSyntax.Xid(FederationJson.String(entity, "xref"));
                        if (target.Length != 4 || !ReferenceEquals(resource, Resource(model, target)))
                        {
                            throw FederationJson.Invalid("xref must name the same Resource model type in this Registry.");
                        }
                    }
                    else
                    {
                        OrdinaryFields(entity);
                        Boolean(entity, "readonly");
                        Boolean(entity, "defaultversionsticky");
                        RequireId(entity, "defaultversionid", null);
                    }
                }
                else
                {
                    OrdinaryFields(entity);
                    RequireId(entity, "versionid", entityParts[5]);
                    RequireId(entity, "ancestorid", null);
                    Boolean(entity, "isdefault");
                    ValidateDocument(FederationJson.Required(data, "document"), entity, resource);
                }
            }
        }
        if (model is not null)
        {
            ValidateCollections(kind, entityXid, entityParts, children, model);
            Forbid(entity, OmittedAttributes(kind, entityParts, model));
        }
        return new(kind, entityXid, data, children, meta);
    }

    internal static IEnumerable<string> OmittedAttributes(string kind, string[] parts, RegistryModel model)
    {
        foreach (var name in s_forbidden) { yield return name; }
        var childNames = kind switch
        {
            "registry" => model.Groups.Keys,
            "group" => Group(model, parts).Resources.Keys,
            _ => ["versions"],
        };
        foreach (var name in childNames)
        {
            yield return name;
            yield return name + "url";
            yield return name + "count";
        }
        if (kind == "resource") { yield return "meta"; }
    }

    internal static void ValidateCollections(string kind, string xid, string[] parts,
        IReadOnlyList<TreeReference> children, RegistryModel model)
    {
        if (kind is "registry" or "group")
        {
            var expected = (kind == "registry" ? model.Groups.Keys : Group(model, parts).Resources.Keys)
                .Select(name => (xid == "/" ? "" : xid) + "/" + name)
                .Select(value => IdentityKey(value, true))
                .Order(StringComparer.Ordinal).ToArray();
            if (!children.Select(child => IdentityKey(child.Xid, true)).Order(StringComparer.Ordinal)
                .SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw FederationJson.Invalid("Every modeled child collection requires exactly one reference.");
            }
        }
        else if (kind == "resource" &&
            (children.Count > 1 || (children.Count == 1 && !FederationSyntax.SameXid(children[0].Xid, xid + "/versions", true))))
        {
            throw FederationJson.Invalid("A Resource can contain only its exact Versions collection.");
        }
    }

    internal static RegistryGroupDefinition Group(RegistryModel model, string[] parts) =>
        parts.Length > 0 && model.Groups.TryGetValue(parts[0], out var group)
            ? group : throw FederationJson.Invalid("Unknown Group model type.");

    internal static RegistryResourceDefinition Resource(RegistryModel model, string[] parts) =>
        parts.Length >= 3 && Group(model, parts).Resources.TryGetValue(parts[2], out var resource)
            ? resource : throw FederationJson.Invalid("Unknown Resource model type.");

    internal static TreeReference Reference(JsonElement data)
    {
        FederationJson.Fields(data, "kind", "xid", "href", "size", "sha256");
        var kind = FederationJson.String(data, "kind");
        if (kind is not ("group" or "resource" or "version" or "meta" or "collection"))
        {
            throw FederationJson.Invalid("Invalid containment reference kind.");
        }
        var xid = FederationJson.String(data, "xid");
        FederationSyntax.Xid(xid, kind == "collection");
        return new(kind, xid, Path(data), Size(data), Digest(data));
    }

    internal static TreeReference LocalDocument(JsonElement data, string xid) =>
        new("document", xid, Path(data), Size(data), Digest(data));

    internal static bool Boolean(JsonElement data, string name)
    {
        var value = FederationJson.Required(data, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw FederationJson.Invalid($"Field '{name}' must be boolean.");
    }

    internal static BigInteger Nonnegative(JsonElement data, string name)
    {
        var value = FederationJson.Required(data, name);
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw FederationJson.Invalid($"Field '{name}' must be a nonnegative integer.");
        }
        var number = RegistryNumber.FromElement(value);
        if (!number.IsInteger || number.Significand.Sign < 0)
        {
            throw FederationJson.Invalid($"Field '{name}' must be a nonnegative integer.");
        }
        return number.ToBigInteger();
    }

    internal static void Forbid(JsonElement entity, IEnumerable<string> names)
    {
        if (names.Any(name => entity.TryGetProperty(name, out _)))
        {
            throw FederationJson.Invalid("Stored metadata contains a forbidden field.");
        }
    }

    private static ReadOnlyCollection<TreeReference> References(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            throw FederationJson.Invalid("References must form an array.");
        }
        var result = new List<TreeReference>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in data.EnumerateArray())
        {
            var reference = Reference(value);
            if (!identities.Add(IdentityKey(reference.Xid, reference.Kind == "collection")) ||
                result.Count != 0 && string.CompareOrdinal(result[^1].Xid, reference.Xid) >= 0)
            {
                throw FederationJson.Invalid("References must be unique and sorted by unsigned UTF-8 XID bytes.");
            }
            result.Add(reference);
        }
        return result.AsReadOnly();
    }

    private static string Path(JsonElement data)
    {
        var path = FederationJson.String(data, "href");
        FederationSyntax.StoragePath(path);
        return path;
    }

    private static long Size(JsonElement data)
    {
        var size = Nonnegative(data, "size");
        if (size > long.MaxValue)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "Object size cannot be represented.");
        }
        return (long)size;
    }

    private static string Digest(JsonElement data)
    {
        var digest = FederationJson.String(data, "sha256");
        if (digest.Length != 64 || !digest.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
        {
            throw FederationJson.Invalid("A SHA-256 digest requires 64 lowercase hexadecimal digits.");
        }
        return digest;
    }

    private static void RequireId(JsonElement entity, string name, string? expected)
    {
        var id = FederationJson.String(entity, name);
        if (!RegistryId.IsValid(id) || (expected is not null && id != expected))
        {
            throw FederationJson.Invalid("Model-named identifier and typed XID disagree.");
        }
    }

    private static void OrdinaryFields(JsonElement entity)
    {
        Nonnegative(entity, "epoch");
        foreach (var name in new[] { "createdat", "modifiedat" })
        {
            FederationJson.String(entity, name);
        }
    }

    private static void ValidateDocument(JsonElement descriptor, JsonElement entity, RegistryResourceDefinition resource)
    {
        var kind = FederationJson.String(descriptor, "kind");
        if (kind == "none")
        {
            FederationJson.Fields(descriptor, "kind");
            if (resource.HasDocument)
            {
                throw FederationJson.Invalid("Document state disagrees with hasdocument.");
            }
            return;
        }
        if (!resource.HasDocument)
        {
            throw FederationJson.Invalid("Document state disagrees with hasdocument.");
        }
        if (kind == "local")
        {
            FederationJson.Fields(descriptor, "kind", "href", "size", "sha256", "base", "origin");
            LocalDocument(descriptor, FederationJson.String(entity, "xid"));
            Forbid(entity, [resource.Singular + "url"]);
        }
        else if (kind == "external")
        {
            FederationJson.Fields(descriptor, "kind", "uri", "base", "size", "sha256");
            var uri = FederationJson.String(descriptor, "uri");
            FederationJson.UriReference(uri);
            if (FederationJson.String(entity, resource.Singular + "url") != uri)
            {
                throw FederationJson.Invalid("External document and metadata URI disagree.");
            }
            if (!Uri.TryCreate(uri, UriKind.Absolute, out _) && !descriptor.TryGetProperty("base", out _))
            {
                throw FederationJson.Invalid("A relative external document requires an explicit base.");
            }
            if (descriptor.TryGetProperty("size", out _) || descriptor.TryGetProperty("sha256", out _))
            {
                Size(descriptor);
                Digest(descriptor);
            }
        }
        else
        {
            throw FederationJson.Invalid("Unknown document descriptor kind.");
        }
        foreach (var name in new[] { "base", "origin" })
        {
            if (descriptor.TryGetProperty(name, out _))
            {
                FederationJson.AbsoluteUri(FederationJson.String(descriptor, name));
            }
        }
    }
}
