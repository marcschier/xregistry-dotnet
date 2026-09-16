// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

internal sealed class OciLayout(IDocumentTreeReader tree) : IOciObjectReader
{
    public NativeRegistryContext Context => tree.Context;

    public ValueTask<OciObjectResponse?> OpenManifestAsync(string reference, CancellationToken cancellationToken = default) =>
        OpenBlobAsync(reference, cancellationToken);

    public async ValueTask<OciObjectResponse?> OpenBlobAsync(string digest, CancellationToken cancellationToken = default)
    {
        OciFormat.Digest(digest);
        var stream = await tree.OpenReadAsync("blobs/sha256/" + digest[7..], cancellationToken).ConfigureAwait(false);
        return stream is null ? null : new(stream);
    }

    internal static async ValueTask<byte[]> BootstrapAsync(IDocumentTreeReader tree, string path,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        var context = tree.Context;
        budget.ChargeRequest();
        budget.ChargeObject();
        byte[] bytes;
        try
        {
            var stream = await tree.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                throw new FederationException(FederationErrorCode.InvalidPackage, "A required OCI layout entrypoint is absent.");
            }
            await using (stream.ConfigureAwait(false))
            {
                bytes = await OciObjectSession.ReadAsync(stream, budget, OciFormat.MaxIndexBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException exception)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "OCI layout I/O failed.", innerException: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "OCI layout access was denied.", innerException: exception);
        }
        if (context != tree.Context)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The layout context changed during selection.");
        }
        return bytes;
    }
}
