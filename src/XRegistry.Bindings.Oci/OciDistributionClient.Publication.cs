// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

public sealed partial class OciDistributionClient
{
    /// <summary>Uploads exact blob bytes with Distribution's POST/PUT upload protocol, including zero-byte documents.</summary>
    /// <remarks>Only upload-session locations inside the explicitly authorized repository are supported.</remarks>
    public async ValueTask PutBlobAsync(OciSnapshotObject blob, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (blob.IsManifest) { throw OciJson.Invalid("Indexes and manifests cannot be uploaded through the blob protocol."); }
        CheckUploadSize(blob);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Repository.UploadsUri);
            using var response = await SendAsync(request, deadline.Token).ConfigureAwait(false);
            Status(response);
            if (response.StatusCode != HttpStatusCode.Accepted || response.Headers.Location is null)
            {
                throw OciJson.Invalid("Distribution did not return an accepted upload-session location.");
            }
            var location = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location : new Uri(Repository.UploadsUri, response.Headers.Location);
            OciJson.AbsoluteUri(location.AbsoluteUri);
            if (!policy.IsOriginAllowed(location) || location.Fragment.Length != 0 ||
                !location.AbsolutePath.StartsWith(Repository.UploadsUri.AbsolutePath, StringComparison.Ordinal) ||
                location.AbsolutePath.Length == Repository.UploadsUri.AbsolutePath.Length)
            {
                throw new FederationException(FederationErrorCode.PolicyDenied,
                    "The upload-session destination is outside the explicitly authorized repository.");
            }
            if (location.Query.TrimStart('?').Split('&').Any(p =>
                Uri.UnescapeDataString(p.Split('=')[0]).Equals("digest", StringComparison.Ordinal)))
            {
                throw OciJson.Invalid("An upload-session location must not preselect a conflicting digest.");
            }
            var destination = new Uri(location.AbsoluteUri + (location.Query.Length == 0 ? "?" : "&") + "digest=" + blob.Digest);
            await PutAsync(destination, blob, "application/octet-stream", deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FederationException(FederationErrorCode.Unavailable,
                "The OCI blob publication deadline expired; the upload outcome may be unknown.", innerException: exception);
        }
    }

    /// <summary>Publishes an index or manifest at its exact digest using the manifests endpoint.</summary>
    public ValueTask PutManifestAsync(OciSnapshotObject manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsManifest) { throw OciJson.Invalid("Blob objects cannot be published at the manifests endpoint."); }
        return PublishManifestAsync(manifest.Digest, manifest, cancellationToken);
    }

    /// <summary>Publishes a prepared Registry root at one explicit reference. Use package.PublishAsync to guarantee closure-before-reference ordering.</summary>
    public ValueTask CommitReferenceAsync(string reference, OciSnapshotObject root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        OciFormat.Reference(reference);
        if (root.MediaType != OciFormat.Index || root.ArtifactType != OciFormat.Artifact("registry"))
        {
            throw OciJson.Invalid("A snapshot reference must select a Registry root index.");
        }
        if (OciFormat.IsDigest(reference) && reference != root.Digest) { throw OciObjectSession.Integrity(); }
        return PublishManifestAsync(reference, root, cancellationToken);
    }

    private async ValueTask PublishManifestAsync(string reference, OciSnapshotObject manifest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CheckUploadSize(manifest);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            await PutAsync(Repository.ManifestUri(reference), manifest, manifest.MediaType, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FederationException(FederationErrorCode.Unavailable,
                "The OCI manifest publication deadline expired; the reference outcome may be unknown.", innerException: exception);
        }
    }

    private async ValueTask PutAsync(Uri destination, OciSnapshotObject item, string mediaType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, destination);
        request.Content = new StreamContent(item.OpenRead());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        request.Content.Headers.ContentLength = item.Size;
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        Status(response);
        if (response.StatusCode != HttpStatusCode.Created) { throw OciJson.Invalid("Distribution did not acknowledge object publication with Created."); }
        item.VerifyDigestHeader(ResponseDigest(response));
    }

    private void CheckUploadSize(OciSnapshotObject item)
    {
        if (item.Size > options.MaxUploadBytes)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The OCI publication exceeds the exact upload byte budget.");
        }
    }
}
