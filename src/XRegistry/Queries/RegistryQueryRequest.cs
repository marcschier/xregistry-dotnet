namespace XRegistry.Queries;

/// <summary>Transport-independent effective query parameters over one logical target.</summary>
public sealed record RegistryQueryRequest
{
    /// <summary>Creates a query relative to a trusted, credential-free HTTP root, optionally containing a mount path.</summary>
    public RegistryQueryRequest(RegistryPath path, Uri publicRoot)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(publicRoot);
        if (!publicRoot.IsAbsoluteUri || publicRoot.Scheme is not ("http" or "https") ||
            publicRoot.UserInfo.Length != 0 || publicRoot.Query.Length != 0 || publicRoot.Fragment.Length != 0 ||
            publicRoot.AbsoluteUri.Length > 8192)
        {
            throw new ArgumentException("A bounded trusted HTTP root without credentials, query or fragment is required.", nameof(publicRoot));
        }

        Path = path;
        PublicRoot = publicRoot;
    }

    /// <summary>Gets the entity or direct collection target; $details does not change logical query facts.</summary>
    public RegistryPath Path { get; }
    /// <summary>Gets the trusted root used only for generated navigation facts and collection links.</summary>
    public Uri PublicRoot { get; }
    /// <summary>Gets ordered filter parameter values: commas AND expressions; separate values OR branches.</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];
    /// <summary>Gets the one scalar sort path with optional =asc or =desc, without URI encoding.</summary>
    public string? Sort { get; init; }
    /// <summary>Gets whether a collection without an explicit sort uses ID ascending, as required for paging.</summary>
    public bool DefaultSortById { get; init; }
}
