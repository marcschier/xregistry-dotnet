using System.Text.Json;
using XRegistry.Bindings.Oci;
using XRegistry.Federation;

namespace XRegistry.Bindings.File;

/// <summary>An explicitly selected native File representation; never inferred from directory contents.</summary>
public enum FileRegistryLayout
{
    /// <summary>The directory-mapping document tree rooted at registry.json.</summary>
    DocumentTree,
    /// <summary>A standard OCI layout selected by an explicit tag or SHA-256 reference.</summary>
    OciLayout,
}

/// <summary>Owns a safe File tree and one explicitly selected read-only binding session.</summary>
/// <remarks>
/// Direct creation authorizes the supplied selected directory, optionally constrained by a caller-approved
/// boundary. Catalog creation requires that boundary explicitly. A catalog advertisement is not authorization.
/// Returned JSON and document bytes remain owned after source disposal. Concurrent operations are rejected.
/// </remarks>
public sealed class FileRegistrySource : IFederationReadSource, IAsyncDisposable
{
    private readonly FileRegistryRoot root;
    private readonly IFederationReadSource source;
    private readonly IAsyncDisposable session;
    private int state;

    private FileRegistrySource(FileRegistryLayout layout, FileRegistryRoot root, IFederationReadSource source,
        IAsyncDisposable session, RegistryModel model)
    {
        Layout = layout;
        this.root = root;
        this.source = source;
        this.session = session;
        Model = model;
    }

    /// <summary>The explicitly selected layout.</summary>
    public FileRegistryLayout Layout { get; }
    /// <inheritdoc />
    public NativeRegistryContext Context => source.Context;
    /// <inheritdoc />
    public JsonElement Capabilities => source.Capabilities;
    /// <summary>The captured compiled model, including imported Resource type identity.</summary>
    public RegistryModel Model { get; }

    /// <summary>Opens an explicitly authorized local directory using exactly the requested layout.</summary>
    /// <param name="endpoint">The selected local File locator.</param>
    /// <param name="layout">Explicit layout, never detection or fallback.</param>
    /// <param name="reference">Required for OCI; forbidden for document trees, even when empty.</param>
    /// <param name="authorizedRoot">An optional already-approved enclosing directory; absent authorizes only endpoint.</param>
    /// <param name="budget">One cumulative finite binding operation budget.</param>
    /// <param name="cancellationToken">Cancellation for bootstrap and acquisition.</param>
    public static async ValueTask<FileRegistrySource> OpenAsync(Uri endpoint, FileRegistryLayout layout,
        string? reference = null, Uri? authorizedRoot = null, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(layout))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "Unsupported explicit File layout.");
        }
        if (layout == FileRegistryLayout.DocumentTree && reference is not null)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "A document-tree File source forbids an OCI reference.");
        }
        if (layout == FileRegistryLayout.OciLayout && string.IsNullOrEmpty(reference))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage, "An OCI-layout File source requires an explicit reference.");
        }
        budget ??= new();
        var opened = FileRegistryRoot.Open(endpoint, authorizedRoot, budget.Limits.MaxObjectBytes);
        var transferred = false;
        try
        {
            FileRegistrySource result;
            if (layout == FileRegistryLayout.DocumentTree)
            {
                var mapping = await DirectoryMapping.OpenAsync(opened.Reader, budget, cancellationToken).ConfigureAwait(false);
                result = new(layout, opened, mapping, mapping, mapping.Model);
            }
            else
            {
                var snapshot = await OciSnapshot.OpenLayoutAsync(opened.Reader, reference, budget, cancellationToken).ConfigureAwait(false);
                result = new(layout, opened, snapshot, snapshot, snapshot.Model);
            }
            transferred = true;
            return result;
        }
        finally { if (!transferred) { opened.Dispose(); } }
    }

    /// <summary>Opens one exact wire-layout spelling: document-tree or oci-layout.</summary>
    public static ValueTask<FileRegistrySource> OpenAsync(Uri endpoint, string layout, string? reference = null,
        Uri? authorizedRoot = null, FederationReadBudget? budget = null, CancellationToken cancellationToken = default) =>
        OpenAsync(endpoint, ParseLayout(layout), reference, authorizedRoot, budget, cancellationToken);

    /// <summary>Validates original File URI spelling before System.Uri normalization can hide an alias.</summary>
    public static ValueTask<FileRegistrySource> OpenAsync(string endpoint, string layout, string? reference = null,
        Uri? authorizedRoot = null, FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        var selected = ParseLayout(layout);
        return OpenAsync(FileRegistryLocation.ParseUri(endpoint), selected, reference, authorizedRoot, budget, cancellationToken);
    }

    /// <summary>Consumes an already selected catalog entry only within an explicitly caller-authorized boundary.</summary>
    public static ValueTask<FileRegistrySource> OpenProfileAsync(CatalogAdvertisement advertisement, Uri authorizedRoot,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        ArgumentNullException.ThrowIfNull(authorizedRoot);
        if (advertisement.Name != "file")
        {
            throw new FederationException(FederationErrorCode.UnsupportedBinding, "The selected advertisement is not the File binding.");
        }
        var parameters = advertisement.Parameters;
        if (parameters.EnumerateObject().Any(p => p.Name is not ("layout" or "reference")))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "Unknown selected File profile parameter.");
        }
        if (!parameters.TryGetProperty("layout", out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage, "A File profile requires an explicit layout string.");
        }
        var layout = ParseLayout(value.GetString()!);
        var hasReference = parameters.TryGetProperty("reference", out var selector);
        if (layout == FileRegistryLayout.DocumentTree && hasReference)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "A document-tree File profile forbids a reference field.");
        }
        if (hasReference && selector.ValueKind != JsonValueKind.String)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage, "A File OCI reference must be a string.");
        }
        return OpenAsync(FileRegistryLocation.ParseUri(advertisement.Endpoint), layout,
            hasReference ? selector.GetString() : null, authorizedRoot, budget, cancellationToken);
    }

    /// <summary>Parses an explicit layout without accepting aliases or guessing from content.</summary>
    public static FileRegistryLayout ParseLayout(string layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout switch
        {
            "document-tree" => FileRegistryLayout.DocumentTree,
            "oci-layout" => FileRegistryLayout.OciLayout,
            _ => throw new FederationException(FederationErrorCode.UnsupportedOperation, "Unsupported explicit File layout."),
        };
    }

    /// <inheritdoc />
    public async ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var previous = Interlocked.CompareExchange(ref state, 1, 0);
        ObjectDisposedException.ThrowIf(previous == 2, this);
        if (previous != 0) { throw new InvalidOperationException("A File source permits only one active operation."); }
        try { return await source.ReadAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref state, 0); }
    }

    /// <summary>Closes the binding session, selected tree and authorization anchor, not previously returned results.</summary>
    public async ValueTask DisposeAsync()
    {
        var previous = Interlocked.CompareExchange(ref state, 2, 0);
        if (previous == 2) { return; }
        if (previous != 0) { throw new InvalidOperationException("An active File read cannot be disposed."); }
        try { await session.DisposeAsync().ConfigureAwait(false); }
        finally { root.Dispose(); }
    }
}
