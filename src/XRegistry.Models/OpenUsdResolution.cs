using System.Text.Json;

namespace XRegistry.Models;

/// <summary>An authoritative forward match with independently owned Resource metadata, not acquired artifact bytes.</summary>
public sealed class OpenUsdResolution
{
    private OpenUsdResolution(string assetIdentifier, string resourceId, RegistryJson metadata)
    {
        AssetIdentifier = assetIdentifier;
        ResourceId = resourceId;
        Metadata = metadata;
    }

    /// <summary>Gets the normalized authored identifier matched exactly against the returned metadata.</summary>
    public string AssetIdentifier { get; }

    /// <summary>Gets the exact assigned Resource ID within the caller's selected Group and Registry context.</summary>
    public string ResourceId { get; }

    /// <summary>Gets the independently owned, bounded metadata used to establish the match.</summary>
    public RegistryJson Metadata { get; }

    /// <summary>Resolves through an explicitly authorized metadata callback without acquiring artifact bytes.</summary>
    /// <remarks>
    /// The callback receives only a candidate ID, the remaining metadata byte allowance, and cancellation.
    /// It must use one authorized Group and consistent Registry context, return null only for an established
    /// absence, and propagate authorization, transport and incomplete-read failures. Returned UTF-8 memory
    /// must remain unchanged until this operation completes; it is validated and cloned, never retained.
    /// At most two probes are made, in candidate/fallback order. Invalid metadata and exhausted quotas fail
    /// explicitly; no match throws KeyNotFoundException. No URL, alias, Document or inverse mapping is followed.
    /// </remarks>
    public static async ValueTask<OpenUsdResolution> ResolveAsync(string authoredIdentifier,
        Func<string, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> readAuthorizedMetadata,
        OpenUsdResolutionLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readAuthorizedMetadata);
        ArgumentNullException.ThrowIfNull(limits);
        var identifier = OpenUsdIdentifiers.NormalizeAssetIdentifier(authoredIdentifier, limits.MaxSourceUtf8Bytes, cancellationToken);
        var candidate = OpenUsdIdentifiers.CreateSymbolicIdCandidate(identifier, limits.MaxSourceUtf8Bytes, cancellationToken);
        var id = candidate;
        var remaining = limits.MaxMetadataBytes;
        for (var probe = 0; probe < 2; probe++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (probe >= limits.MaxProbes)
            {
                throw new InvalidDataException("The OpenUSD resolution exceeds its metadata probe limit.");
            }
            if (remaining == 0)
            {
                throw new InvalidDataException("The OpenUSD resolution has exhausted its metadata byte limit.");
            }
            var bytes = await readAuthorizedMetadata(id, remaining, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is not null)
            {
                if (bytes.Value.Length > remaining)
                {
                    throw new InvalidDataException("The OpenUSD resolution exceeds its cumulative metadata byte limit.");
                }
                remaining -= bytes.Value.Length;
                var metadata = RegistryJson.Parse(bytes.Value.Span, new RegistryJsonLimits { MaxBytes = bytes.Value.Length });
                var root = metadata.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("usdassetid", out var returnedId) || returnedId.ValueKind != JsonValueKind.String ||
                    returnedId.GetString() != id ||
                    !root.TryGetProperty("assetidentifier", out var value) || value.ValueKind != JsonValueKind.String ||
                    value.GetString() is not string actual || actual.Length == 0 || actual.StartsWith("./", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("OpenUSD resolution requires correctly addressed Resource metadata with a normalized assetidentifier.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (actual == identifier) { return new(identifier, id, metadata); }
            }
            if (probe == 0)
            {
                id = OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate(identifier, limits.MaxSourceUtf8Bytes, cancellationToken);
                if (id == candidate) { break; }
            }
        }
        throw new KeyNotFoundException("No Resource at the permitted OpenUSD IDs matches the authoritative assetidentifier.");
    }
}
