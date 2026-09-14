using System.Text.Json.Nodes;

namespace XRegistry.Queries;

internal sealed record QueryEntityShape(RegistryPathKind Kind, RegistryModel Model,
    RegistryGroupDefinition? Group, RegistryResourceDefinition? Resource)
{
    internal static QueryEntityShape For(RegistryPath path, RegistryModel model) => new(path.Kind switch
    {
        RegistryPathKind.Export => RegistryPathKind.Registry,
        RegistryPathKind.GroupCollection => RegistryPathKind.Group,
        RegistryPathKind.ResourceCollection => RegistryPathKind.Resource,
        RegistryPathKind.VersionCollection => RegistryPathKind.Version,
        _ => path.Kind
    }, model, path.GroupType is null ? null : model.Groups[path.GroupType],
        path.ResourceType is null ? null : model.Groups[path.GroupType!].Resources[path.ResourceType]);

    internal QueryEntityShape? Child(string name)
    {
        if (Kind == RegistryPathKind.Registry && Model.Groups.TryGetValue(name, out var group))
        {
            return new(RegistryPathKind.Group, Model, group, null);
        }

        if (Kind == RegistryPathKind.Group && Group!.Resources.TryGetValue(name, out var resource))
        {
            return new(RegistryPathKind.Resource, Model, Group, resource);
        }

        return Kind == RegistryPathKind.Resource && name == "versions"
            ? new(RegistryPathKind.Version, Model, Group, Resource) : null;
    }

    internal IReadOnlyDictionary<string, RegistryAttributeDefinition> Attributes()
    {
        if (Kind == RegistryPathKind.Resource)
        {
            var definitions = new Dictionary<string, RegistryAttributeDefinition>(Resource!.Attributes, StringComparer.Ordinal);
            foreach (var attribute in Resource.ResourceAttributes)
            {
                definitions[attribute.Key] = attribute.Value;
            }

            return definitions;
        }

        return Kind switch
        {
            RegistryPathKind.Registry => Model.Attributes,
            RegistryPathKind.Group => Group!.Attributes,
            RegistryPathKind.Meta => Resource!.MetaAttributes,
            RegistryPathKind.Version => Resource!.Attributes,
            _ => throw Diagnostics.Error("bad_filter", "", "This route does not represent a queryable entity collection.")
        };
    }
}

internal sealed class QueryExpression
{
    private QueryExpression(string[] hierarchy, QueryEntityShape shape, QueryStep[] attributes,
        RegistryValueType? type, bool known, string operation, string? value)
    {
        Hierarchy = hierarchy;
        Shape = shape;
        Attributes = attributes;
        Type = type;
        Known = known;
        Operator = operation;
        Value = value;
    }

    internal string[] Hierarchy { get; }
    internal QueryEntityShape Shape { get; }
    internal QueryStep[] Attributes { get; }
    internal RegistryValueType? Type { get; }
    internal bool Known { get; }
    internal string Operator { get; }
    internal string? Value { get; }

    internal static QueryExpression Compile(string text, QueryEntityShape shape,
        RegistryQueryEvaluationLimits limits, bool sort = false)
    {
        var code = sort ? "bad_sort" : "bad_filter";
        var parsed = QueryPath.Expression(text, code);
        var steps = QueryPath.Parse(parsed.Path, limits, code).Steps;
        var hierarchy = new List<string>();
        var position = 0;
        while (position < steps.Count - 1 && steps[position].Kind == QueryStepKind.Property &&
            shape.Child(steps[position].Name) is { } child)
        {
            if (sort)
            {
                throw Diagnostics.Error("bad_sort", "", "Sorting cannot traverse a nested Registry collection.");
            }

            hierarchy.Add(steps[position].Name);
            shape = child;
            position++;
        }

        var attributes = steps.Skip(position).ToArray();
        if (attributes[0].Kind is QueryStepKind.Index or QueryStepKind.AnyIndex or QueryStepKind.AnyProperty)
        {
            throw Diagnostics.Error(code, "", "The entity attribute must be named; Registry IDs and collection-name wildcards are not query path segments.");
        }

        var (known, type) = Resolve(shape, attributes);
        if (sort)
        {
            if (parsed.Operator.Length != 0 && (parsed.Operator != "=" || parsed.Value is not ("asc" or "desc")) ||
                !known || type is RegistryValueType.Object or RegistryValueType.Array or RegistryValueType.Map or RegistryValueType.Binary ||
                attributes.Any(static part => part.Kind is QueryStepKind.AnyIndex or QueryStepKind.AnyProperty))
            {
                throw Diagnostics.Error("bad_sort", "", "Sorting requires one model-known scalar projection and an asc/desc direction.");
            }
        }
        else
        {
            QueryComparison.ValidateLiteral(type, parsed.Operator, parsed.Value, limits.Json);
        }

        return new(hierarchy.ToArray(), shape, attributes, type, known, parsed.Operator, parsed.Value);
    }

    internal List<JsonNode?> Values(JsonObject metadata, RegistryQueryBudget budget)
    {
        var values = new List<JsonNode?> { metadata };
        foreach (var step in Attributes)
        {
            var next = new List<JsonNode?>();
            foreach (var value in values)
            {
                budget.Spend();
                switch (step.Kind)
                {
                    case QueryStepKind.Property:
                        next.Add(value is JsonObject obj ? obj[step.Name] : null);
                        break;
                    case QueryStepKind.Index:
                        next.Add(value is JsonArray array && step.Index < array.Count ? array[step.Index] : null);
                        break;
                    case QueryStepKind.AnyProperty:
                        if (value is JsonObject map)
                        {
                            budget.Spend(map.Count);
                            next.AddRange(map.Select(static pair => pair.Value));
                        }

                        break;
                    case QueryStepKind.AnyIndex:
                        if (value is JsonArray list)
                        {
                            budget.Spend(list.Count);
                            next.AddRange(list);
                        }

                        break;
                }
            }

            values = next;
        }

        return values;
    }

    internal RegistryValueType? TypeFor(JsonObject metadata)
    {
        if (Type is not null and not RegistryValueType.Any)
        {
            return Type;
        }

        var definitions = Shape.Attributes();
        JsonNode? value = metadata;
        RegistryAttributeDefinition? definition = null;
        var start = 0;
        if (Shape.Kind == RegistryPathKind.Resource && Attributes.Length > 1 && Attributes[0].Name == "meta")
        {
            definitions = Shape.Resource!.MetaAttributes;
            value = metadata["meta"];
            start = 1;
        }

        for (var index = start; index < Attributes.Length; index++)
        {
            var step = Attributes[index];
            if (step.Kind is QueryStepKind.AnyIndex or QueryStepKind.AnyProperty)
            {
                return Type;
            }

            if (index == start || definition?.Type == RegistryValueType.Object)
            {
                definitions = index == start ? definitions : definition!.Attributes;
                definition = step.Kind == QueryStepKind.Property ? Active(definitions, step.Name, value as JsonObject) : null;
            }
            else if (definition?.Type == RegistryValueType.Map && step.Kind == QueryStepKind.Property ||
                definition?.Type == RegistryValueType.Array && step.Kind == QueryStepKind.Index)
            {
                definition = definition.Item;
            }
            else
            {
                return Type;
            }

            value = step.Kind == QueryStepKind.Property ? (value as JsonObject)?[step.Name] :
                value is JsonArray array && step.Index < array.Count ? array[step.Index] : null;
        }

        return definition?.Type ?? Type;
    }

    private static RegistryAttributeDefinition? Active(IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions,
        string name, JsonObject? metadata)
    {
        if (definitions.TryGetValue(name, out var declared))
        {
            return declared;
        }

        if (metadata is not null)
        {
            foreach (var condition in definitions.Values.Where(static definition => definition.IfValues.Count != 0))
            {
                if (metadata[condition.Name] is not { } discriminator)
                {
                    continue;
                }

                var text = discriminator.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? discriminator.GetValue<string>() : discriminator.ToJsonString();
                if (condition.IfValues.TryGetValue(text, out var branch) && Active(branch, name, metadata) is { } active)
                {
                    return active;
                }
            }
        }

        return definitions.GetValueOrDefault("*");
    }

    private static (bool Known, RegistryValueType? Type) Resolve(QueryEntityShape shape, QueryStep[] path)
    {
        var definitions = shape.Attributes();
        var position = 0;
        if (shape.Kind == RegistryPathKind.Resource && path.Length > 1 && path[0].Name == "meta")
        {
            definitions = shape.Resource!.MetaAttributes;
            position++;
        }

        var current = Candidates(definitions, path[position]);
        for (position++; position < path.Length; position++)
        {
            var step = path[position];
            var next = new List<RegistryAttributeDefinition>();
            foreach (var definition in current)
            {
                if (definition.Type == RegistryValueType.Any)
                {
                    next.Add(definition);
                }
                else if (definition.Type == RegistryValueType.Object && step.Kind is QueryStepKind.Property or QueryStepKind.AnyProperty)
                {
                    next.AddRange(Candidates(definition.Attributes, step));
                }
                else if (definition.Item is { } item &&
                    (definition.Type == RegistryValueType.Map && step.Kind is QueryStepKind.Property or QueryStepKind.AnyProperty ||
                     definition.Type == RegistryValueType.Array && step.Kind is QueryStepKind.Index or QueryStepKind.AnyIndex))
                {
                    next.Add(item);
                }
            }

            current = next;
        }

        return (current.Count != 0, current.Count != 0 && current.All(item => item.Type == current[0].Type) ? current[0].Type : null);
    }

    private static List<RegistryAttributeDefinition> Candidates(IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions, QueryStep step)
    {
        var result = new List<RegistryAttributeDefinition>();
        if (step.Kind == QueryStepKind.AnyProperty)
        {
            result.AddRange(definitions.Values);
        }
        else if (step.Kind == QueryStepKind.Property)
        {
            if (definitions.TryGetValue(step.Name, out var definition))
            {
                result.Add(definition);
            }

            foreach (var condition in definitions.Values.SelectMany(static definition => definition.IfValues.Values))
            {
                result.AddRange(Candidates(condition, step));
            }

            if (result.Count == 0 && definitions.TryGetValue("*", out var wildcard))
            {
                result.Add(wildcard);
            }
        }

        return result;
    }
}

internal sealed record QueryPlan(IReadOnlyList<IReadOnlyList<QueryExpression>> Filters, bool ExcludeAll, QueryExpression? Sort)
{
    internal static QueryPlan Compile(RegistryQueryRequest request, RegistryPath path, RegistryModel model,
        RegistryQueryEvaluationLimits limits)
    {
        var shape = QueryEntityShape.For(path, model);
        var branches = new List<IReadOnlyList<QueryExpression>>();
        var count = 0;
        var exclude = request.Filters.Contains("excludeall", StringComparer.Ordinal);
        if (exclude && (request.Filters.Count != 1 || request.Filters[0] != "excludeall"))
        {
            throw Diagnostics.Error("bad_filter", path.EscapedPath, "excludeall must be the only filter expression.");
        }

        if (!exclude)
        {
            foreach (var branch in request.Filters)
            {
                var conjunction = new List<QueryExpression>();
                foreach (var expression in QueryPath.SplitAnd(branch))
                {
                    if (expression == "excludeall")
                    {
                        throw Diagnostics.Error("bad_filter", path.EscapedPath, "excludeall cannot be combined with other filters.");
                    }

                    if (++count > limits.MaxFilterExpressions)
                    {
                        throw Diagnostics.Error("too_large", path.EscapedPath, "The filter expression budget is exhausted.");
                    }

                    conjunction.Add(QueryExpression.Compile(expression, shape, limits));
                }

                branches.Add(conjunction.AsReadOnly());
            }
        }

        QueryExpression? sort = null;
        if (request.Sort is not null)
        {
            if (path.Kind is not (RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection))
            {
                throw Diagnostics.Error("sort_noncollection", path.EscapedPath, "Sorting is only defined for a direct entity collection result.");
            }

            sort = QueryExpression.Compile(request.Sort, shape, limits, sort: true);
        }

        return new(branches.AsReadOnly(), exclude, sort);
    }
}
