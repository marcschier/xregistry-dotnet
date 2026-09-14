namespace XRegistry.Federation;

/// <summary>Core response representation options; filtering, sorting and paging require a separate shared query plan.</summary>
public sealed record ProducerViewOptions
{
    /// <summary>Uses Core document view, not a domain Document body.</summary>
    public bool DocumentView { get; init; }
    /// <summary>Forces base64 only when a domain Document is actually inlined.</summary>
    public bool Binary { get; init; }
    /// <summary>Returns only top-level Registry/Group collections and implies wildcard inline.</summary>
    public bool Collections { get; init; }
    /// <summary>Model-relative Core inline paths, never entity IDs or an alternate query grammar.</summary>
    public IReadOnlyList<string> Inline { get; init; } = [];

    internal bool IsDefault => !DocumentView && !Binary && !Collections && Inline.Count == 0;

    internal ProducerViewOptions Capture(RegistryModel model, RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(Inline);
        if (Inline.Count > 64 || Inline.Sum(value => value?.Length ?? 0) > 8192)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The inline expression budget was exceeded.");
        }
        if (Collections && path.Kind is not (RegistryPathKind.Registry or RegistryPathKind.Group))
        {
            throw Invalid("bad_flag", "collections applies only to Registry and Group entities.");
        }
        var inline = Inline.ToArray();
        if (Collections) { inline = [.. inline, "*"]; }
        foreach (var expression in inline)
        {
            if (string.IsNullOrEmpty(expression)) { throw Invalid("bad_inline", "An inline path must name an inlineable attribute."); }
            var kind = path.Kind;
            var group = path.GroupType is null ? null : model.Groups.GetValueOrDefault(path.GroupType);
            var resource = path.ResourceType is null ? null : group?.Resources.GetValueOrDefault(path.ResourceType);
            var segments = expression.Split('.');
            for (var index = 0; index < segments.Length; index++)
            {
                var name = segments[index];
                var leaf = index == segments.Length - 1;
                if (name == "*" && leaf) { break; }
                if (kind == RegistryPathKind.Registry && model.Groups.TryGetValue(name, out group))
                {
                    kind = RegistryPathKind.Group;
                    continue;
                }
                if (kind is RegistryPathKind.Group or RegistryPathKind.GroupCollection &&
                    group is not null && group.Resources.TryGetValue(name, out resource))
                {
                    kind = RegistryPathKind.Resource;
                    continue;
                }
                if (kind is RegistryPathKind.Resource or RegistryPathKind.ResourceCollection && name == "versions")
                {
                    kind = RegistryPathKind.Version;
                    continue;
                }
                if (leaf && (kind == RegistryPathKind.Registry && name is "model" or "modelsource" or "capabilities" ||
                    kind is RegistryPathKind.Resource or RegistryPathKind.ResourceCollection && name == "meta" ||
                    kind is RegistryPathKind.Resource or RegistryPathKind.ResourceCollection or RegistryPathKind.Version or RegistryPathKind.VersionCollection &&
                    resource?.HasDocument == true && name == resource.Singular))
                {
                    continue;
                }
                throw Invalid("bad_inline", "The inline path is unknown, non-inlineable, or traverses an entity ID.");
            }
        }
        return this with { Inline = Array.AsReadOnly(inline.Distinct(StringComparer.Ordinal).ToArray()) };
    }

    internal static bool Includes(IReadOnlyList<string> inline, string name, bool configuration = false) =>
        inline.Any(value => value == name || value.StartsWith(name + ".", StringComparison.Ordinal) || !configuration && value == "*");

    internal static string[] Child(IReadOnlyList<string> inline, string name) =>
        inline.Where(value => value == "*" || value.StartsWith(name + ".", StringComparison.Ordinal))
            .Select(value => value == "*" ? "*" : value[(name.Length + 1)..]).ToArray();

    private static FederationException Invalid(string code, string message) =>
        new(FederationErrorCode.UnsupportedOperation, message, code);
}
