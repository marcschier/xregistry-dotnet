// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Federation;

public sealed partial class DirectoryMapping
{
    private readonly Dictionary<string, VersionState> _versionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<RegistryResourceDefinition, IReadOnlyList<MatchingAttribute>> _matchingAttributes =
        new(ReferenceEqualityComparer.Instance);

    private async ValueTask<VersionState> VersionStateAsync(
        TreeObject resource, TreeObject versions, CancellationToken cancellationToken)
    {
        var key = resource.Xid;
        if (_versionStates.TryGetValue(key, out var cached)) { return cached; }
        var meta = await GetMetaAsync(resource, cancellationToken).ConfigureAwait(false);
        var definition = DocumentTreeFormat.Resource(_model, FederationSyntax.Xid(resource.Xid));
        var entries = new Dictionary<string, TreeReference>(StringComparer.Ordinal);
        foreach (var reference in versions.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _budget.ChargeWork();
            entries.Add(FederationSyntax.Xid(reference.Xid)[^1], reference);
        }
        var defaultId = FederationJson.String(meta.Entity, "defaultversionid");
        if (entries.Count == 0 || !entries.ContainsKey(defaultId))
        {
            throw FederationJson.Invalid("The Resource default Version is missing from its complete index.");
        }
        if (definition.MaxVersions > 0 && entries.Count > definition.MaxVersions)
        {
            throw FederationJson.Invalid("The complete Version index exceeds the captured model's retention limit.");
        }
        var state = new VersionState(versions, meta, definition, entries);
        _versionStates.Add(key, state);
        return state;
    }

    private async ValueTask ValidateSelectedVersionAsync(TreeObject resource, TreeObject index, TreeObject version,
        CancellationToken cancellationToken)
    {
        var state = await VersionStateAsync(resource, index, cancellationToken).ConfigureAwait(false);
        if (state.Complete) { return; }
        if (state.Definition.SingleVersionRoot || MatchingAttributes(state.Definition, cancellationToken).Count != 0)
        {
            var versions = await CollectionMembersAsync(index, cancellationToken).ConfigureAwait(false);
            await ValidateVersionSetAsync(state, versions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ValidateAncestryAsync(state, version, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ValidateAncestryAsync(
        VersionState state, TreeObject version, CancellationToken cancellationToken)
    {
        var path = new List<string>();
        var active = new HashSet<string>(StringComparer.Ordinal);
        var current = version;
        var tailDepth = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _budget.ChargeWork();
            CheckDefault(state.Meta, current);
            var id = FederationJson.String(current.Entity, "versionid");
            if (state.Depths.TryGetValue(id, out tailDepth)) { break; }
            if (!active.Add(id)) { throw FederationJson.Invalid("Captured Version ancestry contains a cycle."); }
            path.Add(id);
            _budget.CheckDepth(path.Count);
            var ancestor = FederationJson.String(current.Entity, "ancestorid");
            if (ancestor == id) { break; }
            if (!state.Entries.TryGetValue(ancestor, out var reference))
            {
                throw FederationJson.Invalid("Captured Version ancestry names an absent Version.");
            }
            _budget.CheckDepth(path.Count + 1);
            current = await LoadAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        for (var index = path.Count - 1; index >= 0; index--)
        {
            _budget.CheckDepth(++tailDepth);
            state.Depths.Add(path[index], tailDepth);
        }
    }

    private async ValueTask ValidateVersionSetAsync(VersionState state, List<TreeObject> versions,
        CancellationToken cancellationToken)
    {
        if (state.Complete) { return; }
        if (versions.Count != state.Index.Children.Count)
        {
            throw FederationJson.Invalid("Version-state validation requires the complete index.");
        }
        var roots = 0;
        foreach (var version in versions)
        {
            await ValidateAncestryAsync(state, version, cancellationToken).ConfigureAwait(false);
            if (FederationJson.String(version.Entity, "ancestorid") == FederationJson.String(version.Entity, "versionid"))
            {
                roots++;
            }
        }
        if (state.Definition.SingleVersionRoot && roots != 1)
        {
            throw FederationJson.Invalid("The captured Resource requires exactly one Version ancestry root.");
        }
        var matching = MatchingAttributes(state.Definition, cancellationToken);
        if (matching.Count != 0 && versions.Count > 1)
        {
            var metadata = versions.Select(version => JsonNode.Parse(_modelValidation.Metadata(version).GetRawText())!.AsObject()).ToArray();
            foreach (var attribute in matching)
            {
                var expected = At(metadata[0], attribute.Path);
                for (var index = 1; index < metadata.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _budget.ChargeWork();
                    if (!SameScalar(expected, At(metadata[index], attribute.Path), attribute.Definition.Type))
                    {
                        throw FederationJson.Invalid("Captured Versions violate a model matchversions constraint.");
                    }
                }
            }
        }
        state.Complete = true;
    }

    private IReadOnlyList<MatchingAttribute> MatchingAttributes(
        RegistryResourceDefinition resource, CancellationToken cancellationToken)
    {
        if (_matchingAttributes.TryGetValue(resource, out var cached)) { return cached; }
        var result = new List<MatchingAttribute>();
        Visit(resource.Attributes, []);
        _matchingAttributes.Add(resource, result);
        return result;

        void Visit(IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions, string[] prefix)
        {
            foreach (var definition in definitions.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _budget.ChargeWork();
                if (definition.MatchVersions) { result.Add(new(definition, [.. prefix, definition.Name])); }
                if (definition.Type == RegistryValueType.Object)
                {
                    Visit(definition.Attributes, [.. prefix, definition.Name]);
                }
            }
        }
    }

    private JsonNode? At(JsonObject metadata, IReadOnlyList<string> path)
    {
        JsonNode? value = metadata;
        foreach (var name in path)
        {
            _budget.ChargeWork();
            value = value is JsonObject owner ? owner[name] : null;
        }
        return value;
    }

    private static bool SameScalar(JsonNode? first, JsonNode? second, RegistryValueType type)
    {
        if (first is null || second is null) { return first is null && second is null; }
        if (type is RegistryValueType.Integer or RegistryValueType.UInteger or RegistryValueType.Decimal)
        {
            return RegistryNumber.Parse(first.ToJsonString()).Equals(RegistryNumber.Parse(second.ToJsonString()));
        }
        if (type == RegistryValueType.Timestamp)
        {
            var left = first.GetValue<string>();
            var right = second.GetValue<string>();
            return left[..19] == right[..19] &&
                left[19..^1].TrimEnd('0').TrimEnd('.') == right[19..^1].TrimEnd('0').TrimEnd('.');
        }
        return JsonNode.DeepEquals(first, second);
    }

    private static void CheckDefault(TreeObject meta, TreeObject version)
    {
        if (DocumentTreeFormat.Boolean(version.Entity, "isdefault") !=
            (FederationJson.String(version.Entity, "versionid") == FederationJson.String(meta.Entity, "defaultversionid")))
        {
            throw FederationJson.Invalid("Version isdefault contradicts Resource Meta.");
        }
    }

    private sealed record MatchingAttribute(RegistryAttributeDefinition Definition, string[] Path);

    private sealed class VersionState(TreeObject index, TreeObject meta, RegistryResourceDefinition definition,
        IReadOnlyDictionary<string, TreeReference> entries)
    {
        internal TreeObject Index { get; } = index;
        internal TreeObject Meta { get; } = meta;
        internal RegistryResourceDefinition Definition { get; } = definition;
        internal IReadOnlyDictionary<string, TreeReference> Entries { get; } = entries;
        internal Dictionary<string, int> Depths { get; } = new(StringComparer.Ordinal);
        internal bool Complete { get; set; }
    }
}
