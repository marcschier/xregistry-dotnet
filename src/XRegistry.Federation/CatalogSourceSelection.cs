// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Federation;

/// <summary>One selected catalog-description Version and advertisement, separate from the described Registry and its Versions.</summary>
public sealed class FederationCatalogOrigin
{
    internal FederationCatalogOrigin(NativeRegistryContext catalogContext, string descriptionVersionXid,
        CatalogAdvertisement advertisement)
    {
        CatalogContext = catalogContext;
        DescriptionVersionXid = descriptionVersionXid;
        Advertisement = advertisement;
    }

    /// <summary>The catalog's own selected access context and revision, not the described Registry's revision.</summary>
    public NativeRegistryContext CatalogContext { get; }
    /// <summary>The exact catalog-relative Version XID of the selected description.</summary>
    public string DescriptionVersionXid { get; }
    /// <summary>The exact chosen advertisement, retaining explicit parameters, original order and implicit-HTTP provenance.</summary>
    public CatalogAdvertisement Advertisement { get; }
}

/// <summary>Opens one explicitly selected catalog-description Version through caller-authorized binding acquisition.</summary>
public static class CatalogSourceSelection
{
    /// <summary>Selects once, opens once and retains catalog provenance on every detached result without rewriting Core metadata.</summary>
    /// <remarks>
    /// The description must already have been read under the supplied catalog context. This operation does not crawl a catalog,
    /// repeat producer-owned composition, resolve descriptive relationships, or acquire the catalog itself.
    /// The caller's acquisition function owns destination authorization, credentials, deadlines and use of the supplied shared budget.
    /// It transfers ownership only when it returns a lease; a failure after that point disposes the acquired lease.
    /// </remarks>
    public static async ValueTask<FederationSourceLease> OpenAsync(CatalogDescription description,
        NativeRegistryContext catalogContext, string descriptionVersionXid, AdvertisementSelectionOptions selection,
        Func<CatalogAdvertisement, FederationReadBudget, CancellationToken, ValueTask<FederationSourceLease>> acquire,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(catalogContext);
        ArgumentNullException.ThrowIfNull(descriptionVersionXid);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(acquire);
        cancellationToken.ThrowIfCancellationRequested();
        budget ??= new();
        var parts = FederationSyntax.Xid(descriptionVersionXid);
        if (parts.Length != 6 || FederationJson.String(description.Data, "versionid") != parts[5] ||
            description.Data.TryGetProperty("xid", out var xid) &&
                (xid.ValueKind != JsonValueKind.String || !FederationSyntax.SameXid(xid.GetString()!, descriptionVersionXid)))
        {
            throw FederationJson.Invalid("The selected catalog-description Version does not match its retained XID.");
        }
        var hops = 1;
        budget.CheckHops(hops);
        for (var ancestor = catalogContext.CatalogOrigin; ancestor is not null; ancestor = ancestor.CatalogContext.CatalogOrigin)
        {
            budget.CheckHops(++hops);
        }
        budget.ChargeWork(description.Advertisements.Count + 1L);
        var advertisement = description.Select(selection);
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeSource();
        var lease = await acquire(advertisement, budget, cancellationToken).ConfigureAwait(false)
            ?? throw FederationJson.Invalid("The selected binding did not return an acquired source.");
        var transferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = lease.Source;
            var context = source.Context;
            if (context.Binding != advertisement.Name || !SameEndpoint(advertisement, context.Source))
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The acquired source does not describe the selected binding and Registry locator.");
            }
            if (source.Model is not { } model)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "Catalog acquisition must establish the described Registry's effective model.");
            }
            var origin = new FederationCatalogOrigin(catalogContext, descriptionVersionXid, advertisement);
            var wrapped = new SelectedSource(source, context, model, origin);
            var result = new FederationSourceLease(wrapped, lease.DisposeAsync, lease.CredentialStamp);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool SameEndpoint(CatalogAdvertisement advertisement, string actual)
    {
        if (advertisement.Name is not ("http" or "file" or "git" or "oci"))
        {
            return advertisement.Endpoint == actual;
        }
        var expectedUri = FederationJson.AbsoluteUri(advertisement.Endpoint);
        var actualUri = FederationJson.AbsoluteUri(actual);
        if (actualUri.Query.Length != 0 || actualUri.Fragment.Length != 0)
        {
            return false;
        }
        if (advertisement.Name is "http" or "file")
        {
            if (!expectedUri.AbsoluteUri.EndsWith('/')) { expectedUri = new Uri(expectedUri.AbsoluteUri + "/", UriKind.Absolute); }
            if (!actualUri.AbsoluteUri.EndsWith('/')) { actualUri = new Uri(actualUri.AbsoluteUri + "/", UriKind.Absolute); }
        }
        return expectedUri.Equals(actualUri);
    }

    private sealed class SelectedSource : IFederationReadSource
    {
        private readonly IFederationReadSource _source;
        private readonly NativeRegistryContext _sourceContext;
        private readonly NativeRegistryContext _context;
        private readonly RegistryModel _model;
        private readonly FederationResolutionOwner _owner;

        internal SelectedSource(IFederationReadSource source, NativeRegistryContext sourceContext, RegistryModel model,
            FederationCatalogOrigin origin)
        {
            _source = source;
            _sourceContext = sourceContext;
            _context = sourceContext with { CatalogOrigin = origin };
            _model = model;
            _owner = FederationCapabilities.GetResolutionOwner(source.Capabilities);
            CheckContext();
        }

        public NativeRegistryContext Context { get { CheckContext(); return _context; } }
        public JsonElement Capabilities { get { CheckContext(); return _source.Capabilities; } }
        public RegistryModel Model { get { CheckContext(); return _model; } }

        public async ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            CheckContext();
            var result = await _source.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckContext();
            if (result.Context != _sourceContext)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The selected binding returned a different source capture.");
            }
            return result.Document is { } document ? FederationReadResult.FromDocument(result.SelectedXid, _context, document) :
                result.ExternalDocument.ValueKind != JsonValueKind.Undefined
                    ? FederationReadResult.FromExternalDocument(result.SelectedXid, _context, result.ExternalDocument)
                    : FederationReadResult.FromMetadata(result.SelectedXid, _context, result.Metadata);
        }

        private void CheckContext()
        {
            if (_source.Context != _sourceContext || !ReferenceEquals(_source.Model, _model) ||
                FederationCapabilities.GetResolutionOwner(_source.Capabilities) != _owner)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The selected Registry capture, model or resolution owner changed after catalog acquisition.");
            }
        }
    }
}
