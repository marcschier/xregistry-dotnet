// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

public sealed partial class OciSnapshot
{
    private int reading;

    /// <summary>Reads native Core metadata or exact domain bytes under the selected immutable root.</summary>
    /// <remarks>
    /// Metadata views never fetch domain documents. External document acquisition is not implicit.
    /// Calls may be repeated sequentially under the same finite cumulative budget; reentry is rejected.
    /// </remarks>
    public async ValueTask<OciReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Enter(cancellationToken);
        try
        {
            return await ReadCoreAsync(request, returnExternalDescriptor: false, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref reading, 0); }
    }

    private async ValueTask<OciReadResult> ReadCoreAsync(FederationReadRequest request, bool returnExternalDescriptor,
        CancellationToken cancellationToken)
    {
        if (request.Representation != FederationRepresentation.DocumentView)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "Native OCI provides Core document view, not synthetic API navigation.");
        }
        var result = request.Operation == FederationOperation.Document
            ? await DocumentAsync(request, returnExternalDescriptor, cancellationToken).ConfigureAwait(false)
            : await MetadataAsync(request, cancellationToken).ConfigureAwait(false);
        session.CheckContext();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private void Enter(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Interlocked.CompareExchange(ref reading, 1, 0);
        ObjectDisposedException.ThrowIf(state == 2, this);
        if (state != 0)
        {
            throw new InvalidOperationException("An OCI snapshot permits only one active operation, including callbacks.");
        }
        try { session.CheckContext(); }
        catch (FederationException) { Volatile.Write(ref reading, 0); throw; }
    }

    private async ValueTask<OciReadResult> DocumentAsync(FederationReadRequest request, bool returnExternalDescriptor,
        CancellationToken cancellationToken)
    {
        var (record, manifest) = await ResolveDocumentVersionAsync(request.Target, cancellationToken).ConfigureAwait(false);
        var document = record.Data.GetProperty("document");
        var mode = OciJson.Text(document, "mode");
        if (mode == "metadata-only")
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "The captured Resource has no domain document.");
        }
        if (mode == "external")
        {
            if (!returnExternalDescriptor)
            {
                throw new FederationException(FederationErrorCode.Unavailable, "Uncaptured external content requires separately authorized acquisition.");
            }
            var resource = OciRecords.Resource(Model, OciFormat.Xid(record.Xid));
            var descriptor = new JsonObject
            {
                ["kind"] = "external",
                ["uri"] = OciJson.Text(record.Entity, resource.Singular + "url"),
            };
            return new(request.Target, record.Xid, Context,
                externalDocument: OciEncoding.Result(descriptor, session.Budget, cancellationToken));
        }
        session.Budget.CheckResultBytes(manifest.Layer!.Size);
        var bytes = await session.BytesAsync(manifest.Layer, cancellationToken).ConfigureAwait(false);
        var contentType = record.SemanticEntity.TryGetProperty("contenttype", out var mediaType) ? mediaType.GetString()! : "application/octet-stream";
        var domain = new FederationDocument(bytes, contentType,
            document.TryGetProperty("base", out var documentBase) ? documentBase.GetString() : null,
            document.TryGetProperty("origin", out var origin) ? origin.GetString() : null);
        return new(request.Target, record.Xid, Context, document: domain);
    }

    private async ValueTask<(OciRecord Record, OciNode Manifest)> ResolveDocumentVersionAsync(string target, CancellationToken cancellationToken)
    {
        var parts = OciFormat.Xid(target);
        var encoded = target[1..].Split('/');
        var owner = "/" + string.Join('/', encoded.Take(4));
        var state = await DocumentSourceAsync(owner, cancellationToken).ConfigureAwait(false);
        var versionId = parts.Length == 6 ? parts[5] : OciJson.Text(state.Meta.Entity, "defaultversionid");
        var requestedVersion = parts.Length != 6 ? null :
            OciFormat.SameXid(owner, state.Resource.Xid) ? target : state.Resource.Xid + "/versions/" + encoded[5];
        var (_, record, manifest) = await VersionAsync(state, versionId, cancellationToken, requestedVersion).ConfigureAwait(false);
        return (record, manifest);
    }

    /// <summary>Returns an explicitly requested external locator without acquiring its unpinned bytes.</summary>
    /// <remarks>
    /// This is not a successful domain-document read. The host must separately authorize any URI,
    /// redirect and credentials through its external-content policy. Snapshot bytes never authorize that fetch.
    /// </remarks>
    public async ValueTask<OciReadResult> ReadExternalDocumentDescriptorAsync(string target, CancellationToken cancellationToken = default)
    {
        var request = new FederationReadRequest(FederationOperation.Document, target);
        Enter(cancellationToken);
        try
        {
            var (record, _) = await ResolveDocumentVersionAsync(request.Target, cancellationToken).ConfigureAwait(false);
            if (OciJson.Text(record.Data.GetProperty("document"), "mode") != "external")
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation, "The selected Version has no external document descriptor.");
            }
            var resource = OciRecords.Resource(Model, OciFormat.Xid(record.Xid));
            var descriptor = new JsonObject
            {
                ["mode"] = "external",
                ["url"] = OciJson.Text(record.Entity, resource.Singular + "url"),
                ["contenttype"] = record.SemanticEntity.TryGetProperty("contenttype", out var type) ? type.GetString() : "application/octet-stream",
            };
            session.CheckContext();
            return new(target, record.Xid, Context,
                externalDocument: OciEncoding.Result(descriptor, session.Budget, cancellationToken));
        }
        finally { Volatile.Write(ref reading, 0); }
    }
}
