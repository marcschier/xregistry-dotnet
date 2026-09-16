// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;

namespace XRegistry.Queries;

/// <summary>An ordered identity selection over the input logical view, without copying or changing Resource origins.</summary>
/// <remarks>
/// RootPaths contains direct collection members, or the requested entity. Includes applies subtree
/// selection to nested collection members; it does not authorize reads, imply inline, or select writes.
/// Hosts project their own pinned entities and freeze pages only after selection and projection.
/// This object retains no source, Document or backend lease. Includes/CollectionUrl charge the supplied
/// budget, which must remain alive through projection; RootPaths is independently usable afterwards.
/// </remarks>
public sealed class RegistryQuerySelection
{
    private readonly RegistryModel _model;
    private readonly string _publicRoot;
    private readonly HashSet<string>? _leaves;
    private readonly RegistryQueryBudget _budget;

    internal RegistryQuerySelection(RegistryPath target, RegistryModel model, string publicRoot,
        IEnumerable<RegistryPath> roots, HashSet<string>? leaves, RegistryQueryBudget budget)
    {
        Target = target;
        _model = model;
        _publicRoot = publicRoot;
        RootPaths = Array.AsReadOnly(roots.ToArray());
        _leaves = leaves;
        _budget = budget;
    }

    /// <summary>Gets the canonical logical target, without a representation suffix.</summary>
    public RegistryPath Target { get; }
    /// <summary>Gets matching direct roots in the requested order, preserving their logical identities.</summary>
    public ReadOnlyCollection<RegistryPath> RootPaths { get; }

    /// <summary>Tests whether a logical collection member is in the selected subtree, never a sibling outside Target.</summary>
    public bool Includes(RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _budget.Spend();
        var key = QueryJson.Key(path);
        if (!QueryJson.Under(key, Target.EscapedPath))
        {
            return false;
        }

        if (key == Target.EscapedPath && !QueryJson.IsCollection(Target.Kind))
        {
            return RootPaths.Count != 0;
        }

        return Matches(key, _leaves, _budget);
    }

    /// <summary>Builds a trusted API collection URL reproducing the selected subtree, with bounded query text.</summary>
    /// <remarks>
    /// visibleCount is the number of currently authorized, included direct members being projected.
    /// An empty filtered collection uses excludeall. Doc-view JSON Pointers and opaque page links
    /// remain the host's responsibility. This method never rewrites arbitrary domain URLs.
    /// </remarks>
    public string CollectionUrl(RegistryPath collection, int visibleCount)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentOutOfRangeException.ThrowIfNegative(visibleCount);
        if (!QueryJson.IsCollection(collection.Kind))
        {
            throw new ArgumentException("A direct entity collection is required.", nameof(collection));
        }

        var key = QueryJson.Key(collection);
        if (!QueryJson.Under(key, Target.EscapedPath))
        {
            throw new ArgumentException("The collection must be within the selected target.", nameof(collection));
        }

        _budget.Spend();
        var url = _publicRoot + key;
        if (_leaves is null)
        {
            return url;
        }

        if (visibleCount == 0)
        {
            return url + "?filter=excludeall";
        }

        var filters = new List<string>();
        var characters = 0;
        foreach (var leaf in _leaves)
        {
            _budget.Spend();
            if (QueryJson.Under(key, leaf))
            {
                return url;
            }

            if (!QueryJson.Under(leaf, key))
            {
                continue;
            }

            var entity = RegistryPath.Parse(leaf);
            var clauses = new List<string>();
            var prefix = "";
            if (collection.Kind == RegistryPathKind.GroupCollection)
            {
                clauses.Add(_model.Groups[entity.GroupType!].Singular + "id=" + entity.GroupId!.Value);
                prefix = entity.ResourceType + ".";
            }

            if (entity.ResourceId is not null && collection.Kind != RegistryPathKind.VersionCollection)
            {
                var definition = _model.Groups[entity.GroupType!].Resources[entity.ResourceType!];
                clauses.Add(prefix + definition.Singular + "id=" + entity.ResourceId.Value);
                prefix += "versions.";
            }

            if (entity.VersionId is not null)
            {
                clauses.Add(prefix + "versionid=" + entity.VersionId.Value);
            }

            var filter = "filter=" + Uri.EscapeDataString(string.Join(',', clauses));
            characters = checked(characters + filter.Length + (filters.Count == 0 ? 0 : 1));
            if (characters > _budget.Limits.MaxQueryCharacters)
            {
                throw Diagnostics.Error("too_large", key, "The exact nested collection filter link exceeds the query budget.");
            }

            filters.Add(filter);
        }

        if (filters.Count == 0)
        {
            throw new ArgumentException("The nonzero projected count contradicts the selected subtree.", nameof(visibleCount));
        }

        return url + "?" + string.Join('&', filters.Distinct(StringComparer.Ordinal));
    }

    internal static bool Matches(string key, HashSet<string>? leaves, RegistryQueryBudget budget)
    {
        if (leaves is null)
        {
            return true;
        }

        foreach (var leaf in leaves)
        {
            budget.Spend();
            if (QueryJson.Under(key, leaf) || QueryJson.Under(leaf, key))
            {
                return true;
            }
        }

        return false;
    }
}
