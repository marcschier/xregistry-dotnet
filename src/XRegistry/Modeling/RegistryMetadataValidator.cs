// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry;

/// <summary>Distinguishes submitted attributes from a complete, server-populated entity.</summary>
public enum RegistryMetadataMode
{
    /// <summary>Enforces required values and applies defaults after the host has populated generated attributes.</summary>
    CompleteEntity,
    /// <summary>Preserves null deletion requests and ignores ordinary readonly input, without filling missing values.</summary>
    ClientInput
}

/// <summary>An explicit state-dependent constraint not discharged by object shape validation.</summary>
public sealed record RegistryValidationObligation(string Kind, string Path, string Description);

/// <summary>Context used to check an entity without implicitly loading other entities or remote schemas.</summary>
public sealed record RegistryMetadataValidationOptions
{
    /// <summary>Gets whether the input is submitted metadata or a complete entity.</summary>
    public RegistryMetadataMode Mode { get; init; }
    /// <summary>Gets the effective model for XID and model-type validation.</summary>
    public RegistryModel? Model { get; init; }
    /// <summary>Gets the owning Group definition when validating Version constraints.</summary>
    public RegistryGroupDefinition? Group { get; init; }
    /// <summary>Gets the Resource definition when validating Version constraints.</summary>
    public RegistryResourceDefinition? Resource { get; init; }
    /// <summary>Gets the owning Group's metadata for equals constraints; Undefined means unavailable.</summary>
    public JsonElement GroupMetadata { get; init; }
    /// <summary>Gets retained top-level PATCH metadata for ClientInput conditional resolution; Undefined means unavailable.</summary>
    /// <remarks>This context is not returned or merged into submitted nested objects. Explicit values and null deletions take precedence, except for readonly attributes.</remarks>
    public JsonElement RetainedMetadata { get; init; }
    /// <summary>Gets bounded input/output and node-validation budgets.</summary>
    public RegistryJsonLimits Limits { get; init; } = new();
}

/// <summary>Normalized, owned metadata with explicit cross-entity obligations still belonging to the host.</summary>
public sealed class RegistryMetadataValidationResult
{
    internal RegistryMetadataValidationResult(RegistryJson metadata, List<RegistryValidationObligation> obligations)
    {
        Metadata = metadata;
        Obligations = obligations.AsReadOnly();
    }

    /// <summary>Gets normalized metadata; timestamps are UTC and completed-entity defaults are populated.</summary>
    public RegistryJson Metadata { get; }
    /// <summary>Gets constraints requiring state or external identity the object validator does not own.</summary>
    public ReadOnlyCollection<RegistryValidationObligation> Obligations { get; }
}

/// <summary>Checks model-defined metadata shapes without performing lifecycle, storage, or remote resolution.</summary>
public static class RegistryMetadataValidator
{
    /// <summary>Normalizes and validates one modeled object, throwing exact-pointer diagnostics on invalid input.</summary>
    /// <remarks>
    /// ClientInput ignores readonly fields, including epoch; hosts must extract binding-specific preconditions
    /// and identity guards from the original request first. CompleteEntity validation does not waive
    /// returned matchversions/target obligations. System Meta compatibility modes are case-insensitive;
    /// ordinary extension string enumerations remain case-sensitive. Timestamp enumerations compare
    /// normalized instants without truncating fractional seconds. Specification-defined name, format,
    /// documentation and icon values must be nonempty; ordinary extension strings may be empty.
    /// Scalar attribute names plus values are limited to 4096 UTF-8 bytes before binding-specific escaping.
    /// This is not an aggregate or collection-item limit and does not apply to any values or inline Documents.
    /// </remarks>
    public static RegistryMetadataValidationResult Validate(
        RegistryJson metadata, IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions,
        RegistryMetadataValidationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(definitions);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.Limits);
        options.Limits.Validate();
        if (!Enum.IsDefined(options.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The metadata validation mode is not defined.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (metadata.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw Diagnostics.Error("invalid_attribute", "", "An entity must be a JSON object.");
        }

        var bounded = RegistryJson.FromElement(metadata.RootElement, options.Limits);
        var retained = options.RetainedMetadata;
        if (retained.ValueKind != JsonValueKind.Undefined)
        {
            if (retained.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Retained metadata must be an object.", nameof(options));
            }

            retained = RegistryJson.FromElement(retained, options.Limits).RootElement;
        }

        var input = JsonNode.Parse(bounded.RootElement.GetRawText())!.AsObject();
        var validator = new Validator(options, cancellationToken);
        validator.ApplyConstraintDefaults(input);
        var output = validator.Object(input, definitions, "", false, 1, retained);
        validator.CheckConstraints(output);
        var normalized = RegistryJson.Create(writer => output.WriteTo(writer), options.Limits);
        return new(normalized, validator.Obligations);
    }

    /// <summary>Checks only the owning Group's constraints against already-populated Resource or Version metadata.</summary>
    /// <remarks>
    /// Both metadata inputs must be JSON objects. The host supplies the retained and derived values to check.
    /// The Resource definition must be an actual member of the Group, including shared imported definitions.
    /// This method does not apply defaults, normalize values, validate unrelated attributes, mutate inputs, or resolve references.
    /// </remarks>
    public static void ValidateGroupConstraints(
        RegistryJson metadata, RegistryGroupDefinition group, RegistryResourceDefinition resource,
        JsonElement groupMetadata, RegistryJsonLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(resource);
        if (!group.Resources.TryGetValue(resource.Plural, out var member) || !ReferenceEquals(member, resource))
        {
            throw new ArgumentException("The Resource definition must belong to the supplied Group.", nameof(resource));
        }
        limits ??= new();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (metadata.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw Diagnostics.Error("invalid_attribute", "", "An entity must be a JSON object.");
        }

        if (groupMetadata.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Group metadata must be an object.", nameof(groupMetadata));
        }

        var bounded = RegistryJson.FromElement(metadata.RootElement, limits);
        var options = new RegistryMetadataValidationOptions
        {
            Group = group,
            Resource = resource,
            GroupMetadata = RegistryJson.FromElement(groupMetadata, limits).RootElement,
            Limits = limits
        };
        var input = JsonNode.Parse(bounded.RootElement.GetRawText(),
            documentOptions: new JsonDocumentOptions { MaxDepth = limits.MaxDepth })!.AsObject();
        new Validator(options, cancellationToken).CheckConstraints(input);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class Validator(RegistryMetadataValidationOptions options, CancellationToken cancellationToken)
    {
        private int _work;
        internal List<RegistryValidationObligation> Obligations { get; } = [];
        private bool Submitted => options.Mode == RegistryMetadataMode.ClientInput;

        private void Work(int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_work > options.Limits.MaxNodes || depth > options.Limits.MaxDepth)
            {
                throw Diagnostics.Error("node_limit", "", "The metadata validation budget was exceeded.");
            }
        }

        internal JsonObject Object(JsonObject input, IReadOnlyDictionary<string, RegistryAttributeDefinition> declared,
            string path, bool extended, int depth, JsonElement retained = default)
        {
            Work(depth);
            var definitions = RegistryConditionalAttributes.Resolve(declared, (definition, _) =>
            {
                Work(depth);
                if (Submitted && retained.ValueKind == JsonValueKind.Object && definition.IfValues.Count != 0 &&
                    definition.Name != "*")
                {
                    if (!definition.ReadOnly && input.TryGetPropertyValue(definition.Name, out var submitted))
                    {
                        return submitted is null ? definition.DefaultValue :
                            JsonSerializer.SerializeToElement(submitted, RegistryMetadataJsonContext.Default.JsonNode);
                    }

                    return retained.TryGetProperty(definition.Name, out var previous) && ModelCompiler.HasValue(previous)
                        ? previous : definition.DefaultValue;
                }

                if (definition.Name == "*" || Submitted && definition.ReadOnly)
                {
                    return default;
                }

                input.TryGetPropertyValue(definition.Name, out var value);
                if (value is null && !Submitted && ModelCompiler.HasValue(definition.DefaultValue))
                {
                    value = JsonNode.Parse(definition.DefaultValue.GetRawText());
                    input[definition.Name] = value;
                }

                if (value is null || definition.IfValues.Count == 0)
                {
                    return default;
                }

                return JsonSerializer.SerializeToElement(value, RegistryMetadataJsonContext.Default.JsonNode);
            }, path);

            var output = new JsonObject();
            foreach (var property in input)
            {
                var at = Diagnostics.At(path, property.Key);
                if (!definitions.TryGetValue(property.Key, out var definition))
                {
                    if (!RegistryNames.IsAttribute(property.Key, extended) ||
                        !definitions.TryGetValue("*", out definition))
                    {
                        throw Diagnostics.Error("unknown_attribute", at, "The attribute is not allowed by the effective model.");
                    }
                }

                if (Submitted && definition.ReadOnly)
                {
                    continue;
                }

                if (property.Value is null)
                {
                    if (Submitted)
                    {
                        output[property.Key] = null;
                    }
                    else if (definition.Required)
                    {
                        throw Diagnostics.Error("invalid_attribute", at, "A required attribute has no value.");
                    }

                    continue;
                }

                output[property.Key] = Value(property.Value, definition, at, depth + 1, property.Key);
            }

            if (!Submitted)
            {
                foreach (var definition in definitions.Values)
                {
                    if (definition.Name != "*" && definition.Required &&
                        (!output.TryGetPropertyValue(definition.Name, out var value) || value is null))
                    {
                        throw Diagnostics.Error("invalid_attribute", Diagnostics.At(path, definition.Name),
                            "The completed entity is missing a required value.");
                    }
                }
            }

            return output;
        }

        private JsonNode Value(JsonNode node, RegistryAttributeDefinition definition, string path, int depth,
            string? attributeName = null)
        {
            Work(depth);
            if (definition.Type == RegistryValueType.Any)
            {
                return node.DeepClone();
            }

            if (definition.Type == RegistryValueType.Binary)
            {
                throw Diagnostics.Error("unsupported_type", path, "The pinned core prose does not define binary attribute serialization.");
            }

            if (definition.Type == RegistryValueType.Object)
            {
                if (node is not JsonObject obj)
                {
                    throw Diagnostics.Error("invalid_attribute", path, "The attribute must be an object.");
                }

                return Object(obj, definition.Attributes, path, definition.NameCharset == "extended", depth);
            }

            if (definition.Type is RegistryValueType.Array or RegistryValueType.Map)
            {
                var item = definition.Item ?? throw Diagnostics.Error("model_error", path, "A collection item definition is absent.");
                if (definition.Type == RegistryValueType.Array && node is JsonArray array)
                {
                    var result = new JsonArray();
                    for (var index = 0; index < array.Count; index++)
                    {
                        var childPath = Diagnostics.At(path, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        if (array[index] is not { } child)
                        {
                            throw Diagnostics.Error("invalid_attribute", childPath, "Null is not a typed array item.");
                        }

                        result.Add(Value(child, item, childPath, depth + 1));
                    }

                    return result;
                }

                if (definition.Type == RegistryValueType.Map && node is JsonObject map)
                {
                    var result = new JsonObject();
                    foreach (var property in map)
                    {
                        var at = Diagnostics.At(path, property.Key);
                        if (!RegistryNames.IsAttribute(property.Key, extended: true) || property.Value is null)
                        {
                            throw Diagnostics.Error("invalid_attribute", at, "A map needs valid nonempty keys and typed non-null values.");
                        }

                        result[property.Key] = Value(property.Value, item, at, depth + 1);
                    }

                    return result;
                }

                throw Diagnostics.Error("invalid_attribute", path, "The attribute does not match its array/map type.");
            }

            if (node is not JsonValue scalar || !scalar.TryGetValue<JsonElement>(out var value))
            {
                throw Diagnostics.Error("invalid_attribute", path, "The attribute has the wrong scalar type or is outside its allowed enumeration.");
            }

            var maximumBytes = attributeName is not null &&
                !(definition.IsDocument && definition.Type == RegistryValueType.String)
                ? ScalarValues.MaxAttributeBytes - Encoding.UTF8.GetByteCount(attributeName)
                : int.MaxValue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (text is not null && maximumBytes != int.MaxValue && !ScalarValues.FitsString(text, maximumBytes))
            {
                throw Diagnostics.Error("invalid_attribute", path, "The scalar attribute name and value exceed 4096 UTF-8 bytes.");
            }

            if (SystemAttributes.RequiresNonEmptyString(definition) && text is { Length: 0 })
            {
                throw Diagnostics.Error("invalid_attribute", path, "The specification-defined attribute must not be empty.");
            }

            if (!ScalarValues.Matches(definition.Type, value, out var nonStringBytes, text) || !ScalarValues.Allowed(definition, value))
            {
                throw Diagnostics.Error("invalid_attribute", path, "The attribute has the wrong scalar type or is outside its allowed enumeration.");
            }

            if (value.ValueKind != JsonValueKind.String && nonStringBytes > maximumBytes)
            {
                throw Diagnostics.Error("invalid_attribute", path, "The scalar attribute name and value exceed 4096 UTF-8 bytes.");
            }

            if (definition.IsIdentifier && !RegistryId.IsValid(value.GetString()!))
            {
                throw Diagnostics.Error("invalid_attribute", path, "The identifier does not satisfy the core grammar.");
            }

            if (definition.MatchVersions)
            {
                Obligations.Add(new("matchversions", path, "The host must compare this scalar across all Versions."));
            }

            CheckTarget(value, definition, path);
            if (definition.Type == RegistryValueType.Timestamp &&
                ScalarValues.TryTimestamp(value.GetString()!, out var normalized))
            {
                return JsonValue.Create(normalized)!;
            }

            return node.DeepClone();
        }

        private void CheckTarget(JsonElement value, RegistryAttributeDefinition definition, string path)
        {
            if (definition.Type is not (RegistryValueType.Xid or RegistryValueType.XidType))
            {
                if (definition.Target is not null &&
                    UriSyntax.IsReference(value.GetString()!, out var absolute) && !absolute)
                {
                    Obligations.Add(new("target", path, "The host must verify the URI target under its authorized Registry context."));
                }

                return;
            }

            if (options.Model is null)
            {
                Obligations.Add(new("target", path, "An effective model is required to check the referenced type."));
                return;
            }

            var text = value.GetString()!;
            if (definition.Type == RegistryValueType.XidType)
            {
                var type = ModelPaths.ParseType(text, path);
                CheckModelType(type.Group, type.Resource, path);
                return;
            }

            var xid = RegistryPath.Parse(text);
            CheckModelType(xid.GroupType, xid.ResourceType, path);
            if (definition.Target is { } template)
            {
                var target = ModelPaths.ParseType(template, path, true);
                var matchesKind = target.Versions switch
                {
                    "required" => xid.Kind == RegistryPathKind.Version,
                    "optional" => xid.Kind is RegistryPathKind.Resource or RegistryPathKind.Version,
                    _ => xid.Kind == (target.Resource is null ? RegistryPathKind.Group : RegistryPathKind.Resource)
                };
                if (xid.GroupType != target.Group || xid.ResourceType != target.Resource || !matchesKind)
                {
                    throw Diagnostics.Error("invalid_attribute", path, "The XID points at a different model type.");
                }
            }
        }

        private void CheckModelType(string? group, string? resource, string path)
        {
            if (group is not null && (!options.Model!.Groups.TryGetValue(group, out var definition) ||
                resource is not null && !definition.Resources.ContainsKey(resource)))
            {
                throw Diagnostics.Error("invalid_attribute", path, "The referenced Registry type is undefined.");
            }
        }

        internal void ApplyConstraintDefaults(JsonObject input)
        {
            if (Submitted || options.Group is null || options.Resource is null)
            {
                return;
            }

            foreach (var constraint in options.Group.Constraints.Values.Where(constraint =>
                constraint.ResourceType == options.Resource.Plural && ModelCompiler.HasValue(constraint.DefaultValue)))
            {
                var owner = input;
                for (var index = 0; index + 1 < constraint.AttributePath.Count; index++)
                {
                    if (owner[constraint.AttributePath[index]] is not JsonObject child)
                    {
                        owner = null;
                        break;
                    }

                    owner = child;
                }

                if (owner is not null && owner[constraint.AttributePath[^1]] is null)
                {
                    owner[constraint.AttributePath[^1]] = JsonNode.Parse(constraint.DefaultValue.GetRawText());
                }
            }
        }

        internal void CheckConstraints(JsonObject output)
        {
            if (Submitted || options.Group is null || options.Resource is null)
            {
                return;
            }

            foreach (var constraint in options.Group.Constraints.Values)
            {
                Work(1);
                if (constraint.ResourceType != options.Resource.Plural)
                {
                    continue;
                }

                var value = At(output, constraint.AttributePath);
                var actual = value is null ? default :
                    JsonSerializer.SerializeToElement(value, RegistryMetadataJsonContext.Default.JsonNode);
                var definition = options.Resource.Attributes[constraint.AttributePath[0]];
                foreach (var component in constraint.AttributePath.Skip(1))
                {
                    Work(1);
                    definition = definition.Attributes[component];
                }
                var path = "/" + string.Join('/', constraint.AttributePath.Select(name =>
                    name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
                if (value is not null && constraint.EnumValues.Count > 0 &&
                    !constraint.EnumValues.Any(allowed =>
                    {
                        Work(1);
                        return ScalarValues.Equal(definition.Type, allowed, actual);
                    }))
                {
                    throw Diagnostics.Error("constraint_failure", path, "The value is not allowed by the owning Group constraint.");
                }

                if (constraint.EqualsAttribute is null)
                {
                    continue;
                }

                if (options.GroupMetadata.ValueKind == JsonValueKind.Undefined)
                {
                    Obligations.Add(new("equals", path, "The owning Group value must be checked by the host."));
                    continue;
                }

                var expected = options.GroupMetadata;
                foreach (var part in constraint.EqualsPath)
                {
                    Work(1);
                    if (expected.ValueKind != JsonValueKind.Object || !expected.TryGetProperty(part, out expected))
                    {
                        expected = default;
                        break;
                    }
                }

                if (ModelCompiler.HasValue(expected))
                {
                    Work(1);
                    if (value is null || !ScalarValues.Equal(definition.Type, expected, actual))
                    {
                        throw Diagnostics.Error("constraint_failure", path, "The value differs from its owning Group's equals constraint.");
                    }
                }
            }
        }

        private JsonNode? At(JsonObject root, IReadOnlyList<string> parts)
        {
            JsonNode? value = root;
            foreach (var part in parts)
            {
                Work(1);
                value = value is JsonObject obj ? obj[part] : null;
            }

            return value;
        }
    }
}
