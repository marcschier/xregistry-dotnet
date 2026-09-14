namespace XRegistry.Validation;

internal static class SchemaTypeNames
{
    internal static string Select(string selector, IEnumerable<(string Name, bool Selectable)> declarations,
        bool allowAbsolute, ValidationContext context)
    {
        context.Work(selector.Length, "$/selector");
        var absolute = allowAbsolute && selector.StartsWith('.');
        var name = absolute ? selector[1..] : selector;
        if (!name.Split('.').All(SchemaJson.Identifier))
        {
            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.name",
                "A type selector must be a dot-separated declaration name.");
        }

        var qualified = absolute || name.Contains('.', StringComparison.Ordinal);
        string? match = null;
        var wrongKind = false;
        var ambiguous = false;
        foreach (var declaration in declarations)
        {
            context.Work(declaration.Name.Length + name.Length, "$/selector");
            if (declaration.Name.Equals(name, StringComparison.Ordinal))
            {
                if (!declaration.Selectable)
                {
                    SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Invalid, "selection.not_type",
                        "The exact named declaration is not an admissible concrete type.");
                }
                return declaration.Name;
            }
            if (qualified) { continue; }
            var candidate = declaration.Name[(declaration.Name.LastIndexOf('.') + 1)..];
            if (!candidate.Equals(name, StringComparison.Ordinal))
            {
                continue;
            }
            if (!declaration.Selectable)
            {
                wrongKind = true;
            }
            else if (match is null)
            {
                match = declaration.Name;
            }
            else
            {
                ambiguous = true;
            }
        }
        if (ambiguous)
        {
            SchemaObjectSelectionFailure.Fail(SchemaObjectSelectionStatus.Ambiguous, "selection.ambiguous",
                "The short name matches multiple declarations; supply a fully qualified name.");
        }
        if (match is null)
        {
            SchemaObjectSelectionFailure.Fail(wrongKind ? SchemaObjectSelectionStatus.Invalid : SchemaObjectSelectionStatus.NotFound,
                wrongKind ? "selection.not_type" : "selection.not_found",
                wrongKind ? "The named declaration is not an admissible concrete type." : "The named declaration is not present in the supplied Document.");
        }
        return match;
    }
}
