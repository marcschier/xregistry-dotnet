using System.Text.Json;

namespace XRegistry.Federation;

internal sealed class DocumentTreeModelValidation(RegistryModel model, FederationReadBudget budget)
{
    private readonly Dictionary<IReadOnlyDictionary<string, RegistryAttributeDefinition>,
        IReadOnlyDictionary<string, RegistryAttributeDefinition>> _definitions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TreeObject, JsonElement> _metadata = new(ReferenceEqualityComparer.Instance);

    internal JsonElement Metadata(TreeObject item) => _metadata[item];

    internal void Clear()
    {
        _definitions.Clear();
        _metadata.Clear();
    }

    internal void Validate(TreeObject item, JsonElement groupMetadata, CancellationToken cancellationToken)
    {
        if (item.Kind is "collection" or "resource" || item.Kind == "meta" && item.Entity.TryGetProperty("xref", out _))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parts = FederationSyntax.Xid(item.Xid);
        var group = parts.Length >= 2 ? DocumentTreeFormat.Group(model, parts) : null;
        var resource = parts.Length >= 4 ? DocumentTreeFormat.Resource(model, parts) : null;
        var declared = item.Kind switch
        {
            "registry" => model.Attributes,
            "group" => group!.Attributes,
            "meta" => resource!.MetaAttributes,
            "version" => resource!.Attributes,
            _ => throw FederationJson.Invalid("Unsupported mapping metadata kind."),
        };
        if (!_definitions.TryGetValue(declared, out var definitions))
        {
            var omitted = DocumentTreeFormat.OmittedAttributes(item.Kind, parts, model).ToHashSet(StringComparer.Ordinal);
            if (resource is { HasDocument: true })
            {
                omitted.Add(resource.Singular);
                omitted.Add(resource.Singular + "base64");
            }
            definitions = declared.Where(pair => !omitted.Contains(pair.Key) &&
                (item.Kind != "registry" || !IsConfiguration(pair.Key))).ToDictionary(StringComparer.Ordinal);
            _definitions.Add(declared, definitions);
        }
        budget.ChargeWork(definitions.Count);

        var limits = new RegistryJsonLimits
        {
            MaxBytes = budget.Limits.MaxObjectBytes,
            MaxDepth = budget.Limits.MaxJsonDepth,
            MaxNodes = (int)Math.Min(int.MaxValue, Math.Max(1, budget.Limits.MaxWork - budget.Work)),
        };
        try
        {
            var metadata = item.Kind == "registry" ? RegistryJson.Create(writer =>
            {
                writer.WriteStartObject();
                foreach (var property in item.Entity.EnumerateObject())
                {
                    if (!IsConfiguration(property.Name)) { property.WriteTo(writer); }
                }
                writer.WriteEndObject();
            }, limits) : RegistryJson.FromElement(item.Entity, limits);
            var validation = RegistryMetadataValidator.Validate(metadata, definitions, new()
            {
                Model = model,
                Group = group,
                Resource = item.Kind == "version" ? resource : null,
                GroupMetadata = groupMetadata,
                Limits = limits,
            }, cancellationToken);
            FederationJson.CountWork(validation.Metadata.RootElement, budget, cancellationToken);
            if (validation.Obligations.Any(obligation => obligation.Kind != "matchversions"))
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "A captured metadata constraint requires separately authorized target validation.");
            }
            _metadata.Add(item, validation.Metadata.RootElement);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
    }

    private static bool IsConfiguration(string name) => name is "model" or "modelsource" or "capabilities";
}
