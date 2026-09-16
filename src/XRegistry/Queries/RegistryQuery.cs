// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;

namespace XRegistry.Queries;

/// <summary>The shared Core filter grammar, model-aware facts, subtree evaluation and scalar ordering module.</summary>
public static partial class RegistryQuery
{
    /// <summary>Evaluates one complete already-authorized/shadowed logical view without owning its sources or cursors.</summary>
    /// <remarks>
    /// No storage or HTTP connection is created. Source metadata/defaults/membership must be stable
    /// through evaluation and projection. No query result can trigger lower-precedence fallback.
    /// Bad query syntax uses Core bad_filter/bad_sort/sort_noncollection/not_found diagnostics.
    /// Missing or inconsistent required facts fail as query_source_incomplete/invalid_query_source.
    /// Cancellation propagates; work/byte/count exhaustion is too_large and deadline exhaustion is
    /// server_busy. Hosts must retain their own current-authorization checks and opaque page state.
    /// </remarks>
    public static ValueTask<RegistryQuerySelection> EvaluateAsync(RegistryModel model, IRegistryQuerySource source,
        RegistryQueryRequest request, RegistryQueryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(request.Filters);
        budget.Spend();
        long characters = request.Sort?.Length ?? 0;
        if (request.Filters.Count > budget.Limits.MaxFilterExpressions)
        {
            throw Diagnostics.Error("too_large", request.Path.EscapedPath, "The filter parameter budget is exhausted.");
        }

        foreach (var filter in request.Filters)
        {
            ArgumentNullException.ThrowIfNull(filter);
            characters += filter.Length;
        }

        if (characters > budget.Limits.MaxQueryCharacters)
        {
            throw Diagnostics.Error("too_large", request.Path.EscapedPath, "The query text exceeds its character budget.");
        }

        budget.Spend(characters);
        var path = RegistryPath.Parse(QueryJson.Key(request.Path));
        if (path.GroupType is not null && (!model.Groups.TryGetValue(path.GroupType, out var group) ||
            path.ResourceType is not null && !group.Resources.ContainsKey(path.ResourceType)))
        {
            throw Diagnostics.Error("not_found", path.EscapedPath, "The query target is not defined by the model.");
        }

        if (path.Kind is not (RegistryPathKind.Registry or RegistryPathKind.Group or RegistryPathKind.Resource or
            RegistryPathKind.Meta or RegistryPathKind.Version) && !QueryJson.IsCollection(path.Kind))
        {
            throw Diagnostics.Error(request.Sort is null ? "bad_filter" : "sort_noncollection",
                path.EscapedPath, "This route is not a queryable entity or collection.");
        }

        var captured = request with { Filters = request.Filters.ToArray() };
        var plan = QueryPlan.Compile(captured, path, model, budget.Limits);
        return new Evaluation(model, source, captured.PublicRoot.AbsoluteUri.TrimEnd('/'), budget)
            .EvaluateAsync(path, captured, plan);
    }

    private sealed partial class Evaluation(RegistryModel model, IRegistryQuerySource source,
        string publicRoot, RegistryQueryBudget budget)
    {
        private readonly Dictionary<string, RegistryQueryEntity?> _entities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<RegistryPath>> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject?> _facts = new(StringComparer.Ordinal);

        internal async ValueTask<RegistryQuerySelection> EvaluateAsync(RegistryPath path, RegistryQueryRequest request, QueryPlan plan)
        {
            var collection = QueryJson.IsCollection(path.Kind);
            if (plan.ExcludeAll)
            {
                if (!collection)
                {
                    throw Diagnostics.Error("not_found", path.EscapedPath, "The entity is excluded by the filter.");
                }

                return new(path, model, publicRoot, [], new(StringComparer.Ordinal), budget);
            }

            var roots = collection ? await ChildrenAsync(path).ConfigureAwait(false) : new List<RegistryPath> { path };
            HashSet<string>? leaves = null;
            if (plan.Filters.Count != 0)
            {
                leaves = new(StringComparer.Ordinal);
                var rootMatches = false;
                foreach (var branch in plan.Filters)
                {
                    HashSet<string>? matches = null;
                    var directMatch = true;
                    foreach (var expression in branch)
                    {
                        var found = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var root in roots)
                        {
                            await MatchAsync(root, expression, 0, found).ConfigureAwait(false);
                        }

                        if (!collection && expression.Hierarchy.Length == 0 && !found.Contains(path.EscapedPath))
                        {
                            directMatch = false;
                        }

                        matches = matches is null ? found : Intersect(matches, found);
                    }

                    rootMatches |= directMatch;
                    leaves.UnionWith(matches!);
                }

                if (!collection && !rootMatches)
                {
                    throw Diagnostics.Error("not_found", path.EscapedPath, "The entity does not match the filter.");
                }
            }

            var selected = new List<RegistryPath>();
            foreach (var root in roots)
            {
                budget.Spend();
                if ((!collection || RegistryQuerySelection.Matches(root.EscapedPath, leaves, budget)) &&
                    await EntityAsync(root).ConfigureAwait(false) is not null)
                {
                    selected.Add(root);
                }
            }

            if (!collection && selected.Count == 0)
            {
                throw Diagnostics.Error("not_found", path.EscapedPath, "The entity is not visible in this query.");
            }

            if (plan.Sort is { } sort)
            {
                selected = await SortAsync(selected, sort).ConfigureAwait(false);
            }
            else if (collection && request.DefaultSortById)
            {
                Sort(selected, (left, right) =>
                {
                    budget.Spend();
                    return StringComparer.OrdinalIgnoreCase.Compare(QueryJson.LeafId(left.EscapedPath), QueryJson.LeafId(right.EscapedPath));
                });
            }

            return new(path, model, publicRoot, selected, leaves, budget);
        }

        private async ValueTask MatchAsync(RegistryPath path, QueryExpression expression, int depth, HashSet<string> leaves)
        {
            budget.Spend();
            if (await EntityAsync(path).ConfigureAwait(false) is null)
            {
                return;
            }

            if (depth < expression.Hierarchy.Length)
            {
                var collection = RegistryPath.Parse((path.Kind == RegistryPathKind.Registry ? "" : path.EscapedPath) +
                    "/" + expression.Hierarchy[depth]);
                foreach (var child in await ChildrenAsync(collection).ConfigureAwait(false))
                {
                    await MatchAsync(child, expression, depth + 1, leaves).ConfigureAwait(false);
                }

                return;
            }

            var facts = await FactsAsync(path, expression).ConfigureAwait(false);
            if (facts is not null)
            {
                var type = expression.TypeFor(facts);
                QueryComparison.ValidateLiteral(type, expression.Operator, expression.Value, budget.Limits.Json);
                if (QueryComparison.Matches(expression.Values(facts, budget), type,
                    expression.Operator, expression.Value, budget.Limits.Json, budget))
                {
                    leaves.Add(path.EscapedPath);
                }
            }
        }

        private HashSet<string> Intersect(HashSet<string> first, HashSet<string> second)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var left in first)
            {
                foreach (var right in second)
                {
                    budget.Spend();
                    if (QueryJson.Under(left, right))
                    {
                        result.Add(left);
                    }
                    else if (QueryJson.Under(right, left))
                    {
                        result.Add(right);
                    }
                }
            }

            return result;
        }

        private async ValueTask<List<RegistryPath>> SortAsync(List<RegistryPath> children, QueryExpression sort)
        {
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var types = new Dictionary<string, RegistryValueType?>(StringComparer.Ordinal);
            var allowed = new List<RegistryPath>();
            foreach (var path in children)
            {
                var facts = await FactsAsync(path, sort).ConfigureAwait(false);
                if (facts is null)
                {
                    continue;
                }

                var value = sort.Values(facts, budget).SingleOrDefault();
                var type = sort.TypeFor(facts);
                _ = QueryComparison.Compare(value, value, type, budget.Limits.Json, budget);
                values.Add(path.EscapedPath, value);
                types.Add(path.EscapedPath, type);
                allowed.Add(path);
            }

            Sort(allowed, (left, right) =>
            {
                var comparison = QueryComparison.Compare(values[left.EscapedPath], values[right.EscapedPath],
                    types[left.EscapedPath] == types[right.EscapedPath] ? types[left.EscapedPath] : null, budget.Limits.Json, budget);
                if (comparison == 0)
                {
                    comparison = StringComparer.OrdinalIgnoreCase.Compare(QueryJson.LeafId(left.EscapedPath), QueryJson.LeafId(right.EscapedPath));
                }

                return sort.Value == "desc" ? -Math.Sign(comparison) : comparison;
            });
            return allowed;
        }

        private static void Sort(List<RegistryPath> paths, Comparison<RegistryPath> comparison)
        {
            try
            {
                paths.Sort(comparison);
            }
            catch (InvalidOperationException exception) when (exception.InnerException is RegistryException or OperationCanceledException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private async ValueTask<RegistryQueryEntity?> EntityAsync(RegistryPath path, bool includeDocument = false)
        {
            var key = path.EscapedPath + (includeDocument ? "|document" : "");
            if (_entities.TryGetValue(key, out var known))
            {
                return known;
            }

            budget.SourceRead();
            var entity = await budget.ReadAsync(ct => source.ReadEntityAsync(path, includeDocument, ct)).ConfigureAwait(false);
            if (entity is not null)
            {
                var documentBytes = entity.Document?.Length ?? 0;
                if (documentBytes > budget.Limits.MaxDocumentBytes)
                {
                    throw Diagnostics.Error("too_large", path.EscapedPath, "The query Document exceeds its byte budget.");
                }

                var metadata = entity.Metadata.RootElement.GetRawText();
                budget.SourceBytes((long)Encoding.UTF8.GetByteCount(metadata) + documentBytes +
                    (entity.ShortSelf is null ? 0 : Encoding.UTF8.GetByteCount(entity.ShortSelf)) +
                    (entity.DefaultVersionId is null ? 0 : Encoding.UTF8.GetByteCount(entity.DefaultVersionId)));
                using var validated = RegistryJson.ParseDocument(Encoding.UTF8.GetBytes(metadata), budget.Limits.Json);
            }

            _entities.Add(key, entity);
            return entity;
        }

        private async ValueTask<List<RegistryPath>> ChildrenAsync(RegistryPath collection)
        {
            var key = collection.EscapedPath;
            if (_children.TryGetValue(key, out var known))
            {
                return known;
            }

            budget.Spend();
            var result = new List<RegistryPath>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var iterator = source.GetChildrenAsync(collection, budget.CancellationToken)
                .GetAsyncEnumerator(budget.CancellationToken);
            await using (iterator.ConfigureAwait(false))
            {
                while (await budget.ReadAsync(_ => iterator.MoveNextAsync()).ConfigureAwait(false))
                {
                    var child = iterator.Current ?? throw Diagnostics.Error("invalid_query_source", key, "A collection member path is missing.");
                    var canonical = QueryJson.Key(child);
                    budget.Member(canonical);
                    var prefix = key + "/";
                    if (child.IsDetails || !canonical.StartsWith(prefix, StringComparison.Ordinal) ||
                        canonical.AsSpan(prefix.Length).Contains('/') ||
                        (collection.Kind, child.Kind) is not ((RegistryPathKind.GroupCollection, RegistryPathKind.Group) or
                            (RegistryPathKind.ResourceCollection, RegistryPathKind.Resource) or
                            (RegistryPathKind.VersionCollection, RegistryPathKind.Version)) ||
                        !ids.Add(QueryJson.LeafId(canonical)))
                    {
                        throw Diagnostics.Error("invalid_query_source", key, "Collection members must have unique direct logical entity identities.");
                    }

                    result.Add(RegistryPath.Parse(canonical));
                }
            }

            _children.Add(key, result);
            return result;
        }
    }
}
