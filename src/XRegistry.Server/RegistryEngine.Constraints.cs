using System.Text.Json.Nodes;
using XRegistry.Models;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private readonly Dictionary<string, RegistryGroupDefinition> _constraintGroups = new(StringComparer.Ordinal);

        private void ScheduleGroupConstraintDependents(Entity group)
        {
            if (_modelChanged)
            {
                return;
            }

            var path = RegistryPath.Parse(group.Key);
            var definition = GroupForValidation(path);
            var changedConstraints = !ServerJson.Equal(group.Original["constraints"], group.Attributes["constraints"]);
            var affectedTypes = new HashSet<string>(StringComparer.Ordinal);
            if (changedConstraints)
            {
                affectedTypes.UnionWith(definition.Resources.Keys);
            }
            else
            {
                foreach (var constraint in definition.Constraints.Values)
                {
                    Work();
                    if (constraint.EqualsPath.Count != 0 &&
                        !ServerJson.Equal(At(group.Original, constraint.EqualsPath), At(group.Attributes, constraint.EqualsPath)))
                    {
                        affectedTypes.Add(constraint.ResourceType);
                    }
                }
            }

            if (!ServerJson.Equal(group.Original["envelope"], group.Attributes["envelope"]) ||
                !ServerJson.Equal(group.Original["protocol"], group.Attributes["protocol"]))
            {
                foreach (var resource in definition.Resources.Values)
                {
                    Work();
                    if (RegistryDomainRules.HasMessageGroupContract(definition, resource))
                    {
                        affectedTypes.Add(resource.Plural);
                    }
                }
            }

            foreach (var type in affectedTypes)
            {
                foreach (var resource in Children(group.Key + "/" + type))
                {
                    Work();
                    _resources.Add(resource.Key);
                }
            }
        }

        private JsonNode? At(JsonObject metadata, IReadOnlyList<string> path)
        {
            JsonNode? value = metadata;
            foreach (var part in path)
            {
                Work();
                value = value is JsonObject obj ? obj[part] : null;
            }

            return value;
        }

        private RegistryGroupDefinition GroupForValidation(RegistryPath path)
        {
            var key = ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!));
            if (_constraintGroups.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var definition = _model.Groups[path.GroupType!];
            var group = Require(key);
            if (group.Attributes["constraints"] is not JsonObject constraints || constraints.Count == 0)
            {
                return definition;
            }

            var source = ServerJson.Object(_model.EffectiveModel);
            var groupNode = source["groups"]![path.GroupType!]!.AsObject();
            var merged = groupNode["constraints"] as JsonObject ?? new JsonObject();
            if (merged.Parent is null)
            {
                groupNode["constraints"] = merged;
            }

            foreach (var constraint in constraints)
            {
                Work();
                var requested = (JsonObject)Map(constraint.Value, key + "/constraints").DeepClone();
                if (requested["enum"] is JsonArray { Count: 0 })
                {
                    requested.Remove("enum");
                }
                if (requested["equals"] is JsonValue emptyEquals && emptyEquals.TryGetValue<string>(out var equalsPath) &&
                    equalsPath.Length == 0)
                {
                    requested.Remove("equals");
                }
                var original = merged[constraint.Key] as JsonObject ?? new JsonObject();
                if (requested["enum"] is JsonArray requestedEnum && original["enum"] is JsonArray originalEnum &&
                    originalEnum.Count != 0 && (requestedEnum.Count == 0 ||
                        requestedEnum.Any(value => !originalEnum.Any(originalValue => ServerJson.Equal(value, originalValue)))))
                {
                    throw ServerErrors.With("invalid_attribute", key, "Group instance constraints cannot widen a model enumeration.", ("name", "constraints"));
                }

                if (requested["equals"] is { } equals && original["equals"] is { } modelEquals && !ServerJson.Equal(equals, modelEquals))
                {
                    throw ServerErrors.With("invalid_attribute", key, "A Group instance cannot replace an existing model equals constraint.", ("name", "constraints"));
                }

                foreach (var part in requested.Where(static property => property.Value is not null))
                {
                    original[part.Key] = part.Value!.DeepClone();
                }

                if (original.Parent is null)
                {
                    merged[constraint.Key] = original;
                }
            }

            try
            {
                var compiled = RegistryModel.Compile(ServerJson.Own(source, _engine._options.ModelCompilation.JsonLimits),
                    _engine._options.ModelCompilation with { Resolver = null, SourceUri = null });
                definition = compiled.Groups[path.GroupType!];
            }
            catch (RegistryException exception)
            {
                throw ServerErrors.Wrap(new RegistryException(new("invalid_attribute", key,
                    "The Group's constraints are invalid or widen its Resource model."), exception), "invalid_attribute", key, ("name", "constraints"));
            }

            _constraintGroups.Add(key, definition);
            return definition;
        }
    }
}
