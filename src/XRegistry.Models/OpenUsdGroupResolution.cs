namespace XRegistry.Models;

/// <summary>The caller-selected OpenUSD Group collection, never inferred from a name.</summary>
public enum OpenUsdGroupKind
{
    /// <summary>An Asset Container Group, whose legal Core name remains its verbatim ID.</summary>
    AssetContainer,
    /// <summary>A Schema Plugin Group, whose ID uses symbolic construction.</summary>
    SchemaPlugin,
}

/// <summary>An owned, authoritative Group-name match without artifact or metadata acquisition policy.</summary>
public sealed class OpenUsdGroupResolution
{
    private OpenUsdGroupResolution(string name, string groupId, OpenUsdGroupKind kind, RegistryJson metadata)
    {
        Name = name;
        GroupId = groupId;
        Kind = kind;
        Metadata = metadata;
    }

    /// <summary>Gets the exact authoritative Group name supplied by the caller.</summary>
    public string Name { get; }
    /// <summary>Gets the exact assigned Group ID in the caller's selected collection.</summary>
    public string GroupId { get; }
    /// <summary>Gets the explicitly selected Group kind.</summary>
    public OpenUsdGroupKind Kind { get; }
    /// <summary>Gets independently owned metadata proving the exact ID/name match.</summary>
    public RegistryJson Metadata { get; }

    /// <summary>Resolves a Group through explicit, bounded metadata reads, without fetching artifacts.</summary>
    /// <remarks>
    /// The callback must use one authorized collection and consistent Registry context.
    /// Null means established absence; all access, transport and incomplete-read errors must propagate.
    /// Symbolic Groups probe the candidate and sole fallback, including after candidate absence.
    /// A legal Core Asset Container name has only its verbatim location. Group names are not asset
    /// identifiers: leading ./ is not removed. Parsing retains Core UTF-8, duplicate-key, depth and
    /// node limits, with at most two parses under the cumulative metadata byte budget.
    /// </remarks>
    public static async ValueTask<OpenUsdGroupResolution> ResolveAsync(string name, OpenUsdGroupKind kind,
        Func<string, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> readAuthorizedMetadata,
        OpenUsdResolutionLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(readAuthorizedMetadata);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        cancellationToken.ThrowIfCancellationRequested();
        var verbatim = kind == OpenUsdGroupKind.AssetContainer && RegistryId.IsValid(name);
        if (verbatim && name.Length > limits.MaxSourceUtf8Bytes)
        {
            throw new InvalidDataException("The OpenUSD Group name exceeds its source byte limit.");
        }
        var candidate = verbatim ? name :
            OpenUsdIdentifiers.CreateSymbolicIdCandidate(name, limits.MaxSourceUtf8Bytes, cancellationToken);
        var id = candidate;
        var idAttribute = kind == OpenUsdGroupKind.AssetContainer ? "usdassetgroupid" : "usdschemaplugingroupid";
        var remaining = limits.MaxMetadataBytes;
        for (var probe = 0; probe < (verbatim ? 1 : 2); probe++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (probe >= limits.MaxProbes)
            {
                throw new InvalidDataException("The OpenUSD Group lookup exceeds its metadata probe limit.");
            }
            if (remaining == 0)
            {
                throw new InvalidDataException("The OpenUSD Group lookup exhausted its metadata byte limit.");
            }
            var bytes = await readAuthorizedMetadata(id, remaining, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is not null)
            {
                if (bytes.Value.Length > remaining)
                {
                    throw new InvalidDataException("The OpenUSD Group lookup exceeds its cumulative metadata byte limit.");
                }
                remaining -= bytes.Value.Length;
                var metadata = RegistryJson.Parse(bytes.Value.Span, new RegistryJsonLimits { MaxBytes = bytes.Value.Length });
                var root = metadata.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !root.TryGetProperty(idAttribute, out var returnedId) || returnedId.ValueKind != System.Text.Json.JsonValueKind.String ||
                    returnedId.GetString() != id ||
                    !root.TryGetProperty("name", out var returnedName) || returnedName.ValueKind != System.Text.Json.JsonValueKind.String ||
                    returnedName.GetString() is not { Length: > 0 } actual)
                {
                    throw new InvalidDataException("OpenUSD Group resolution requires the exact addressed ID and a nonempty authoritative name.");
                }
                if (actual == name) { return new(name, id, kind, metadata); }
            }
            if (verbatim) { break; }
            if (probe == 0)
            {
                id = OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate(name, limits.MaxSourceUtf8Bytes, cancellationToken);
                if (id == candidate) { break; }
            }
        }
        throw new KeyNotFoundException("No permitted OpenUSD Group location matches the exact authoritative name.");
    }
}
