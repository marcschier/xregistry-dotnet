using System.Text;

namespace XRegistry;

internal static class ModelPaths
{
    internal static (string? Group, string? Resource, string? Versions) ParseType(string text, string path, bool target = false)
    {
        if (text == "/" && !target)
        {
            return (null, null, null);
        }

        var optional = text.EndsWith("[/versions]", StringComparison.Ordinal);
        var body = optional ? text[..^11] : text;
        var parts = body.StartsWith('/') ? body[1..].Split('/') : [];
        if (parts.Length is < 1 or > 3 || !RegistryNames.IsAttribute(parts[0], maxLength: 57) ||
            parts.Length >= 2 && !RegistryNames.IsAttribute(parts[1], maxLength: 57) ||
            parts.Length == 3 && parts[2] != "versions" ||
            optional && (!target || parts.Length != 2))
        {
            throw Diagnostics.Error("model_error", path, "Invalid model type reference or target template.");
        }

        return (parts[0], parts.Length >= 2 ? parts[1] : null,
            optional ? "optional" : parts.Length == 3 ? "required" : null);
    }

    internal static IReadOnlyList<string> ParseAttributePath(string text, string path)
    {
        var parts = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '.')
            {
                index++;
                if (index == text.Length || text[index] is '.' or '[')
                {
                    throw Diagnostics.Error("model_error", path, "Invalid scalar attribute dot path.");
                }
            }

            string name;
            if (text[index] == '[')
            {
                index++;
                if (index >= text.Length || text[index] is not ('\'' or '"'))
                {
                    throw Diagnostics.Error("model_error", path, "Constraint paths cannot traverse arrays.");
                }

                var quote = text[index++];
                var builder = new StringBuilder();
                while (index < text.Length && text[index] != quote)
                {
                    var character = text[index++];
                    if (character == '\\')
                    {
                        if (index >= text.Length || text[index] != quote && text[index] != '\\')
                        {
                            throw Diagnostics.Error("model_error", path, "Invalid quoted attribute path escape.");
                        }

                        character = text[index++];
                    }

                    builder.Append(character);
                }

                if (index + 1 >= text.Length || text[index++] != quote || text[index++] != ']')
                {
                    throw Diagnostics.Error("model_error", path, "Unterminated quoted attribute path.");
                }

                name = builder.ToString();
            }
            else
            {
                var start = index;
                while (index < text.Length && text[index] is not ('.' or '['))
                {
                    index++;
                }

                name = text[start..index];
                if (!RegistryNames.IsAttribute(name))
                {
                    throw Diagnostics.Error("model_error", path, "Special attribute names require bracket quoting.");
                }
            }

            if (!RegistryNames.IsAttribute(name, extended: true) && !RegistryNames.IsAttribute(name))
            {
                throw Diagnostics.Error("model_error", path, "Invalid attribute name in a constraint path.");
            }

            parts.Add(name);
            if (index < text.Length && text[index] is not ('.' or '['))
            {
                throw Diagnostics.Error("model_error", path, "Invalid scalar attribute path operator.");
            }
        }

        if (parts.Count == 0)
        {
            throw Diagnostics.Error("model_error", path, "A scalar attribute path is required.");
        }

        return parts.AsReadOnly();
    }

    internal static RegistryAttributeDefinition ResolveAttribute(
        IReadOnlyDictionary<string, RegistryAttributeDefinition> attributes, IReadOnlyList<string> parts, string path)
    {
        RegistryAttributeDefinition? result = null;
        for (var index = 0; index < parts.Count; index++)
        {
            if (!attributes.TryGetValue(parts[index], out result))
            {
                throw Diagnostics.Error("model_error", path, "The constraint must reference a statically defined attribute.");
            }

            if (index + 1 < parts.Count)
            {
                if (result.Type != RegistryValueType.Object)
                {
                    throw Diagnostics.Error("model_error", path, "Constraint paths may only traverse objects.");
                }

                attributes = result.Attributes;
            }
        }

        if (result is null || !ModelTypes.IsScalar(result.Type))
        {
            throw Diagnostics.Error("model_error", path, "A constraint must reference a scalar attribute.");
        }

        return result;
    }
}
