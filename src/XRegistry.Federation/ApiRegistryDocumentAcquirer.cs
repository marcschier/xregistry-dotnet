// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>An exact API Version's format, independently owned bytes and captured source evidence.</summary>
public sealed class ApiAcquiredDocument
{
    internal ApiAcquiredDocument(string versionXid, string format, string digest,
        NativeRegistryContext context, FederationDocument document)
    {
        VersionXid = versionXid;
        Format = format;
        ContentDigest = digest;
        Context = context;
        Document = document;
    }

    /// <summary>Gets the exact selected Version XID, not a mutable Resource/default selector.</summary>
    public string VersionXid { get; }
    /// <summary>Gets the complete format identifier with its authored spelling.</summary>
    public string Format { get; }
    /// <summary>Gets the SHA-256 digest of acquired bytes, not an advisory metadata digest.</summary>
    public string ContentDigest { get; }
    /// <summary>Gets the source context without asserting live-read snapshot isolation.</summary>
    public NativeRegistryContext Context { get; }
    /// <summary>Gets owned bytes and separately captured media type, base and original location.</summary>
    public FederationDocument Document { get; }
}

/// <summary>Acquires an explicitly selected API Version without dependency retrieval or API invocation.</summary>
public static class ApiRegistryDocumentAcquirer
{
    /// <summary>Reads exact Version metadata and document bytes from one caller-authorized source.</summary>
    /// <remarks>
    /// The selected model must identify an API Group and document-bearing API Resource.
    /// Missing Versions and external descriptors never fall back to defaults or another source.
    /// The result does not claim format validity, frozen dependencies or an inferred retrieval base.
    /// </remarks>
    public static async ValueTask<ApiAcquiredDocument> AcquireAsync(IFederationReadSource source,
        string versionXid, FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(versionXid);
        cancellationToken.ThrowIfCancellationRequested();
        var request = new FederationReadRequest(FederationOperation.Entity, versionXid);
        if (request.Parts.Length != 6)
        {
            throw new ArgumentException("An exact API Version XID is required.", nameof(versionXid));
        }
        budget ??= new();
        var reads = new CapturedFederationReadScope(source, budget, cancellationToken);
        RequireApi(reads, request);
        return await AcquireVersionAsync(reads, request, budget, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Captures a Resource's Core default and acquires that exact Version from the same source.</summary>
    /// <remarks>Aliases are not resolved. An observed default change fails without retry or fallback.</remarks>
    public static async ValueTask<ApiAcquiredDocument> AcquireDefaultAsync(IFederationReadSource source,
        string resourceXid, FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resourceXid);
        cancellationToken.ThrowIfCancellationRequested();
        var resource = new FederationReadRequest(FederationOperation.Entity, resourceXid);
        if (resource.Parts.Length != 4)
        {
            throw new ArgumentException("An API Resource XID is required.", nameof(resourceXid));
        }
        budget ??= new();
        var reads = new CapturedFederationReadScope(source, budget, cancellationToken);
        RequireApi(reads, resource);
        var metaRequest = new FederationReadRequest(FederationOperation.Entity, resourceXid + "/meta");
        var before = await reads.MetadataAsync(metaRequest).ConfigureAwait(false);
        var defaultId = DefaultVersion(before, metaRequest.Target, resource.Parts[3]);
        var version = new FederationReadRequest(FederationOperation.Entity,
            resourceXid + "/versions/" + Uri.EscapeDataString(defaultId));
        var acquired = await AcquireVersionAsync(reads, version, budget, cancellationToken).ConfigureAwait(false);
        var after = await reads.MetadataAsync(metaRequest).ConfigureAwait(false);
        if (DefaultVersion(after, metaRequest.Target, resource.Parts[3]) != defaultId)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "The API Resource default changed during acquisition.");
        }
        reads.Check();
        return acquired;
    }

    private static string DefaultVersion(System.Text.Json.JsonElement metadata, string metaXid, string apiId)
    {
        var entity = metadata.TryGetProperty("entity", out var nested) ? nested : metadata;
        if (!FederationSyntax.SameXid(FederationJson.String(entity, "xid"), metaXid) ||
            FederationJson.String(entity, "apiid") != apiId)
        {
            throw FederationJson.Invalid("API Resource Meta identifies a different entity.");
        }
        if (entity.TryGetProperty("xref", out _))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "An API alias must be resolved to its authorized source before default acquisition.");
        }
        var id = FederationJson.String(entity, "defaultversionid");
        if (id.Length == 0)
        {
            throw FederationJson.Invalid("API Resource Meta has an empty default Version identity.");
        }
        return id;
    }

    private static void RequireApi(CapturedFederationReadScope reads, FederationReadRequest request)
    {
        if (!reads.Model.Groups.TryGetValue(request.Parts[0], out var group) || group.Singular != "apigroup" ||
            !group.Resources.TryGetValue(request.Parts[2], out var resource) ||
            resource.Singular != "api" || !resource.HasDocument)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The selected Version is not a document-bearing API Resource in the captured model.");
        }
    }

    private static async ValueTask<ApiAcquiredDocument> AcquireVersionAsync(CapturedFederationReadScope reads,
        FederationReadRequest request, FederationReadBudget budget, CancellationToken cancellationToken)
    {
        var versionXid = request.Target;
        var metadata = await reads.MetadataAsync(request).ConfigureAwait(false);
        var entity = metadata.TryGetProperty("entity", out var nested) ? nested : metadata;
        if (!FederationSyntax.SameXid(FederationJson.String(entity, "xid"), versionXid) ||
            FederationJson.String(entity, "versionid") != request.Parts[5] ||
            FederationJson.String(entity, "apiid") != request.Parts[3])
        {
            throw FederationJson.Invalid("API Version metadata identifies a different entity.");
        }
        var format = FederationJson.String(entity, "format");
        try
        {
            RegistryDomainRules.ValidateApiVersionMetadata(entity, RegistryJson.Parse("{}").RootElement,
                cancellationToken);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
        var result = await reads.ReadAsync(new(FederationOperation.Document, versionXid)).ConfigureAwait(false);
        var document = result.Document ?? throw new FederationException(FederationErrorCode.Unavailable,
            "The selected API Version has no acquired bytes; external descriptors are not fetched.");
        if (entity.TryGetProperty("contenttype", out _) &&
            !string.Equals(FederationJson.String(entity, "contenttype"), document.ContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new FederationException(FederationErrorCode.IntegrityError,
                "The acquired API document media type differs from its selected metadata.");
        }
        budget.CheckObjectBytes(document.Length);
        budget.CheckResultBytes(document.Length);
        budget.ChargeBytes(document.Length);
        budget.ChargeObject();
        budget.ChargeWork(document.Length);
        using var stream = document.OpenRead();
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        reads.Check();
        return new(versionXid, format, "sha256:" + Convert.ToHexString(digest).ToLowerInvariant(),
            reads.Context, document);
    }
}
