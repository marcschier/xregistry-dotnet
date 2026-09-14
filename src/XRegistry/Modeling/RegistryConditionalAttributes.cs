using System.Text.Json;

namespace XRegistry;

internal static class RegistryConditionalAttributes
{
    internal static Dictionary<string, RegistryAttributeDefinition> Resolve(
        IReadOnlyDictionary<string, RegistryAttributeDefinition> declared,
        Func<RegistryAttributeDefinition, int, JsonElement> runtimeValue, string path,
        Action<RegistryAttributeDefinition, int>? adding = null)
    {
        var definitions = new Dictionary<string, RegistryAttributeDefinition>(declared, StringComparer.Ordinal);
        var pending = new Queue<(RegistryAttributeDefinition Definition, int Depth)>(
            declared.Values.Select(static definition => (definition, 0)));
        while (pending.TryDequeue(out var next))
        {
            var value = runtimeValue(next.Definition, next.Depth);
            if (!ModelCompiler.HasValue(value) || next.Definition.IfValues.Count == 0)
            {
                continue;
            }

            var discriminator = ScalarValues.Text(value);
            if (next.Definition.IfValues.TryGetValue(discriminator, out var branch))
            {
                foreach (var sibling in branch.Values)
                {
                    adding?.Invoke(sibling, next.Depth + 1);
                    if (!definitions.TryAdd(sibling.Name, sibling))
                    {
                        throw Diagnostics.Error("invalid_attribute", Diagnostics.At(path, sibling.Name),
                            "Active conditional definitions conflict.");
                    }

                    pending.Enqueue((sibling, next.Depth + 1));
                }
            }
        }

        return definitions;
    }
}
