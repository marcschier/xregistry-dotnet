// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Federation;

public sealed partial class ProducerRegistryView
{
    private readonly Dictionary<string, JsonElement> _groupContexts = new(StringComparer.Ordinal);
    private readonly Dictionary<(Slot Source, string Owner), Dictionary<string, JsonObject>> _modelVersions = [];
    private readonly Dictionary<(Slot Source, string Owner), JsonObject> _modelMeta = [];
    private readonly Dictionary<RegistryResourceDefinition, IReadOnlyList<MatchingAttribute>> _matchingAttributes =
        new(ReferenceEqualityComparer.Instance);
    private sealed record MatchingAttribute(RegistryAttributeDefinition Definition, string[] Path);

    private async ValueTask<JsonObject> ModelMetaAsync(Slot source, string owner, CancellationToken token)
    {
        if (_modelMeta.TryGetValue((source, owner), out var cached)) { return cached; }
        var raw = await ReadUnitMetaAsync(source, owner, token).ConfigureAwait(false);
        var metadata = await NormalizeAsync(source, raw, PathFor(owner + "/meta"), token).ConfigureAwait(false);
        TrackModelDependency(source, PathFor(owner + "/meta"));
        _modelMeta.Add((source, owner), metadata);
        return metadata;
    }

    private async ValueTask ValidationEvidenceAsync(Slot source, JsonObject metadata, RegistryPath path, CancellationToken token)
    {
        var definition = Resource(path)!;
        if (source.Representation == FederationRepresentation.DocumentView)
        {
            metadata.Remove("formatvalidated");
            metadata.Remove("formatvalidatedreason");
            metadata.Remove("compatibilityvalidated");
            metadata.Remove("compatibilityvalidatedreason");
        }
        if (metadata["format"] is null) { return; }
        if (definition.ValidateFormat && metadata["formatvalidated"] is null)
        {
            metadata["formatvalidated"] = false;
            metadata["formatvalidatedreason"] = "The read-only producer received no captured format validation outcome.";
        }
        if (definition.ValidateCompatibility && metadata["compatibilityvalidated"] is null)
        {
            var meta = await ModelMetaAsync(source, Owner(LogicalPath(path)), token).ConfigureAwait(false);
            if (meta["compatibility"] is not null)
            {
                metadata["compatibilityvalidated"] = false;
                metadata["compatibilityvalidatedreason"] = "The read-only producer received no captured compatibility validation outcome.";
            }
        }
    }

    private async ValueTask<JsonElement> GroupContextAsync(RegistryPath path, CancellationToken token)
    {
        var groupPath = RegistryPath.ForGroup(path.GroupType!, path.GroupId!);
        var target = LogicalPath(groupPath);
        if (_groupContexts.TryGetValue(target, out var cached)) { return cached; }
        var first = await _slots[0].GetAsync(token).ConfigureAwait(false);
        var source = first.Owner == FederationResolutionOwner.Producer ? first :
            await SelectGroupAsync(target, token).ConfigureAwait(false);
        var raw = await source.ReadPolicyAsync(new(FederationOperation.Entity, target), token).ConfigureAwait(false);
        var normalized = await NormalizeAsync(source, Entity(raw), groupPath, token).ConfigureAwait(false);
        var metadata = Element(normalized);
        TrackModelDependency(source, groupPath);
        _groupContexts.Add(target, metadata);
        return metadata;
    }

    private void TrackModelDependency(Slot source, RegistryPath path)
    {
        var target = LogicalPath(path);
        _dependencies.TryAdd((source.Name, target), new(source.Name, PathFor(target)));
    }

    private RegistryJsonLimits ModelJsonLimits() => JsonLimits() with
    {
        MaxNodes = (int)Math.Min(int.MaxValue, Math.Max(1, _budget.Limits.MaxWork - _budget.Work)),
    };

    private void ChargeGroupConstraintWork(RegistryGroupDefinition group, RegistryResourceDefinition resource, CancellationToken token)
    {
        foreach (var constraint in group.Constraints.Values)
        {
            token.ThrowIfCancellationRequested();
            _budget.ChargeWork(1);
            if (constraint.ResourceType == resource.Plural)
            {
                _budget.ChargeWork(2L * constraint.AttributePath.Count + constraint.EnumValues.Count + constraint.EqualsPath.Count + 2);
            }
        }
    }

    private IReadOnlyList<MatchingAttribute> MatchingAttributes(RegistryResourceDefinition resource, CancellationToken token)
    {
        if (_matchingAttributes.TryGetValue(resource, out var cached)) { return cached; }
        var result = new List<MatchingAttribute>();
        Visit(resource.Attributes, []);
        _matchingAttributes.Add(resource, result);
        return result;

        void Visit(IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions, string[] prefix)
        {
            _budget.CheckDepth(prefix.Length + 1);
            foreach (var definition in definitions.Values)
            {
                token.ThrowIfCancellationRequested();
                _budget.ChargeWork();
                if (definition.MatchVersions) { result.Add(new(definition, [.. prefix, definition.Name])); }
                if (definition.Type == RegistryValueType.Object) { Visit(definition.Attributes, [.. prefix, definition.Name]); }
            }
        }
    }

    private void CheckMatchingSet(RegistryResourceDefinition resource, IEnumerable<JsonObject> versions, CancellationToken token)
    {
        var attributes = MatchingAttributes(resource, token);
        JsonObject? first = null;
        foreach (var version in versions)
        {
            token.ThrowIfCancellationRequested();
            _budget.ChargeWork();
            if (first is null) { first = version; continue; }
            foreach (var attribute in attributes)
            {
                var left = ModelValueAt(first, attribute.Path);
                var right = ModelValueAt(version, attribute.Path);
                _budget.ChargeWork();
                if (!SameModelScalar(left, right, attribute.Definition.Type))
                {
                    throw new FederationException(FederationErrorCode.InvalidPackage,
                        "The selected Resource's Versions violate a static matchversions rule.", "matchversions_failure");
                }
            }
        }
    }

    private JsonNode? ModelValueAt(JsonObject value, IReadOnlyList<string> path)
    {
        JsonNode? node = value;
        foreach (var part in path)
        {
            _budget.ChargeWork();
            node = node is JsonObject parent ? parent[part] : null;
        }
        return node;
    }

    private static bool SameModelScalar(JsonNode? left, JsonNode? right, RegistryValueType type)
    {
        if (left is null || right is null) { return left is null && right is null; }
        if (type is RegistryValueType.Decimal or RegistryValueType.Integer or RegistryValueType.UInteger)
        {
            return RegistryNumber.Parse(left.ToJsonString()).Equals(RegistryNumber.Parse(right.ToJsonString()));
        }
        if (type == RegistryValueType.Timestamp)
        {
            var first = left.GetValue<string>();
            var second = right.GetValue<string>();
            return first.AsSpan(0, 19).SequenceEqual(second.AsSpan(0, 19)) &&
                first.AsSpan(19, first.Length - 20).TrimEnd('0').TrimEnd('.')
                    .SequenceEqual(second.AsSpan(19, second.Length - 20).TrimEnd('0').TrimEnd('.'));
        }
        return JsonNode.DeepEquals(left, right);
    }
}
