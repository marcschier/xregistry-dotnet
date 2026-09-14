using System.Text;
using System.Text.Json;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

internal static class OciRecords
{
    private static readonly string[] Computed =
    [
        "self", "shortself", "meta", "metaurl", "versions", "versionsurl", "versionscount",
        "defaultversionurl", "formatvalidated", "compatibilityvalidated",
    ];

    internal static RegistryGroupDefinition Group(RegistryModel model, string[] parts) =>
        model.Groups.TryGetValue(parts[0], out var group) ? group : throw OciJson.Invalid("Unknown captured Group model type.");

    internal static RegistryResourceDefinition Resource(RegistryModel model, string[] parts) =>
        Group(model, parts).Resources.TryGetValue(parts[2], out var resource)
            ? resource : throw OciJson.Invalid("Unknown captured Resource model type.");

    internal static string[] Collections(RegistryModel model, string kind, string xid, bool alias = false)
    {
        IEnumerable<string> names = kind switch
        {
            "registry" => model.Groups.Keys,
            "group" => Group(model, OciFormat.Xid(xid)).Resources.Keys,
            "resource" => alias ? [] : ["versions"],
            _ => [],
        };
        return names.Select(n => (xid == "/" ? "" : xid) + "/" + n).Order(StringComparer.Ordinal).ToArray();
    }

    internal static OciRecord Validate(JsonElement data, string kind, string xid, RegistryModel model,
        FederationReadBudget budget, CancellationToken cancellationToken, JsonElement groupMetadata = default)
    {
        OciJson.Fields(data, "formatversion", "kind", "entity", "snapshot", "modelresolved", "document");
        if (OciJson.Integer(data, "formatversion") != 1)
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported OCI record format version.");
        }
        if (OciJson.Text(data, "kind") != kind) { throw OciJson.Invalid("Config and manifest kinds disagree."); }
        var entity = OciJson.Required(data, "entity");
        var storedXid = OciJson.Text(entity, "xid");
        if (!OciFormat.SameXid(storedXid, xid)) { throw OciJson.Invalid("Config identity does not match its descriptor XID."); }
        xid = storedXid;
        var parts = OciFormat.Xid(storedXid);
        var expectedParts = kind switch { "registry" => 0, "group" => 2, "resource" => 4, "meta" => 5, "version" => 6, _ => -1 };
        if (parts.Length != expectedParts) { throw OciJson.Invalid("The config kind does not match its typed XID."); }
        if (kind != "registry" && (data.TryGetProperty("snapshot", out _) || data.TryGetProperty("modelresolved", out _)) ||
            kind != "version" && data.TryGetProperty("document", out _))
        {
            throw OciJson.Invalid("Config fields are not valid for this entity kind.");
        }
        var group = parts.Length >= 2 ? Group(model, parts) : null;
        var resource = parts.Length >= 4 ? Resource(model, parts) : null;
        var identifier = kind == "registry" ? "registryid" : kind == "group" ? group!.Singular + "id" : resource!.Singular + "id";
        var id = OciJson.Text(entity, identifier);
        if (!RegistryId.IsValid(id) || kind == "group" && id != parts[1] ||
            kind is "resource" or "meta" or "version" && id != parts[3])
        {
            throw OciJson.Invalid("The config's singular identifier does not match its typed XID.");
        }
        var forbidden = Computed.Concat(Collections(model, kind, xid).SelectMany(p =>
        {
            var name = p[(p.LastIndexOf('/') + 1)..];
            return new[] { name, name + "url", name + "count" };
        })).ToHashSet(StringComparer.Ordinal);
        if (resource is { HasDocument: true })
        {
            forbidden.Add(resource.Singular);
            forbidden.Add(resource.Singular + "base64");
        }
        CheckStorageFields(entity, forbidden);

        if (kind == "resource")
        {
            OciJson.Fields(entity, identifier, "xid");
            return new(data, kind, xid);
        }
        if (kind == "meta" && entity.TryGetProperty("xref", out _))
        {
            OciJson.Fields(entity, identifier, "xid", "xref");
            var target = OciFormat.Xid(OciJson.Text(entity, "xref"));
            if (target.Length != 4 || !ReferenceEquals(resource, Resource(model, target)))
            {
                throw OciJson.Invalid("A local xref must retain the same captured Resource model type identity.");
            }
            return new(data, kind, xid);
        }
        foreach (var required in new[] { "epoch", "createdat", "modifiedat" }) { OciJson.Required(entity, required); }
        if (kind == "meta")
        {
            OciJson.Boolean(entity, "readonly");
            OciJson.Boolean(entity, "defaultversionsticky");
            if (!RegistryId.IsValid(OciJson.Text(entity, "defaultversionid")) ||
                OciJson.Text(entity, "defaultversionid") is "null" or "request")
            {
                throw OciJson.Invalid("Resource Meta must name a valid default Version.");
            }
        }
        if (kind == "version")
        {
            if (OciJson.Text(entity, "versionid") != parts[5] || !RegistryId.IsValid(OciJson.Text(entity, "ancestorid")))
            {
                throw OciJson.Invalid("Version identity or ancestry is malformed.");
            }
            OciJson.Boolean(entity, "isdefault");
            Document(data, resource!, entity);
        }
        IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions = kind switch
        {
            "registry" => model.Attributes,
            "group" => group!.Attributes,
            "meta" => resource!.MetaAttributes,
            "version" => resource!.Attributes,
            _ => throw OciJson.Invalid("Unsupported config kind."),
        };
        var storageDefinitions = definitions.Where(p => !forbidden.Contains(p.Key)).ToDictionary(StringComparer.Ordinal);
        var validatedEntity = entity;
        if (kind == "registry")
        {
            var metadata = OciJson.Copy(entity)!.AsObject();
            foreach (var name in new[] { "model", "modelsource", "capabilities" })
            {
                storageDefinitions.Remove(name);
                metadata.Remove(name);
            }
            validatedEntity = RegistryJson.Parse(metadata.ToJsonString(), OciJson.Limits(budget)).RootElement;
        }
        try
        {
            var validation = RegistryMetadataValidator.Validate(RegistryJson.FromElement(validatedEntity, OciJson.Limits(budget)), storageDefinitions,
                new()
                {
                    Model = model,
                    Resource = kind == "version" ? resource : null,
                    Group = group,
                    GroupMetadata = groupMetadata,
                    Limits = OciJson.Limits(budget),
                }, cancellationToken);
            if (validation.Obligations.Any(o => o.Kind == "target"))
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "A captured URI target constraint requires separately authorized target validation.");
            }
            CheckStorageFields(validation.Metadata.RootElement, forbidden);
            if (kind == "version") { Document(data, resource!, validation.Metadata.RootElement); }
            OciJson.Charge(validation.Metadata.RootElement, budget, cancellationToken);
            return new(data, kind, xid, validation.Metadata);
        }
        catch (RegistryException exception) { throw OciJson.CoreError(exception); }
    }

    internal static void RetainNormalized(OciRecord record, ref long retainedBytes, FederationReadBudget budget)
    {
        if (record.ValidatedMetadata is null) { return; }
        var size = Encoding.UTF8.GetByteCount(record.ValidatedMetadata.RootElement.GetRawText());
        if (size > budget.Limits.MaxTotalBytes - retainedBytes)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded,
                "Retained normalized OCI metadata exceeds its aggregate byte budget.");
        }
        retainedBytes += size;
    }

    private static void CheckStorageFields(JsonElement entity, HashSet<string> forbidden)
    {
        foreach (var property in entity.EnumerateObject())
        {
            if (forbidden.Contains(property.Name)) { throw OciJson.Invalid("A storage config contains computed navigation, nested entities or document bytes."); }
        }
    }

    private static void Document(JsonElement data, RegistryResourceDefinition resource, JsonElement entity)
    {
        var document = OciJson.Required(data, "document");
        OciJson.Fields(document, "mode", "base", "origin");
        var mode = OciJson.Text(document, "mode");
        var locatorName = resource.Singular + "url";
        if (mode is not ("embedded" or "external" or "metadata-only") ||
            (mode == "metadata-only") == resource.HasDocument ||
            (mode == "external") != (resource.HasDocument && entity.TryGetProperty(locatorName, out _)))
        {
            throw OciJson.Invalid("Version document mode, locator and model hasdocument disagree.");
        }
        if (entity.TryGetProperty("contenttype", out _)) { OciJson.Text(entity, "contenttype"); }
        foreach (var name in new[] { "base", "origin" })
        {
            if (document.TryGetProperty(name, out _))
            {
                if (mode != "embedded") { throw OciJson.Invalid("Only embedded documents retain base or origin."); }
                OciJson.AbsoluteUri(OciJson.Text(document, name));
            }
        }
        if (mode == "external") { OciJson.AbsoluteUri(OciJson.Text(entity, locatorName)); }
    }
}

internal sealed record OciRecord(JsonElement Data, string Kind, string Xid, RegistryJson? ValidatedMetadata = null)
{
    internal JsonElement Entity => Data.GetProperty("entity");
    internal JsonElement SemanticEntity => ValidatedMetadata?.RootElement ?? Entity;
    internal bool IsAlias => Kind == "meta" && Entity.TryGetProperty("xref", out _);
}

internal sealed record OciResourceState(OciNode Node, OciRecord Resource, OciRecord Meta);
