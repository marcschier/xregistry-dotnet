using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

internal sealed class OciObjectSession(IOciObjectReader reader, FederationReadBudget budget)
{
    private readonly IOciObjectReader reader = reader;
    private readonly NativeRegistryContext selected = reader.Context;
    private readonly Dictionary<(bool Manifest, string Digest), byte[]> objects = [];
    private readonly Dictionary<string, OciNode> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(bool Manifest, string Digest), int> depths = [];
    internal FederationReadBudget Budget { get; } = budget;
    internal NativeRegistryContext Selected => selected;
    internal int ObjectCount => objects.Keys.Select(k => k.Digest).Distinct(StringComparer.Ordinal).Count();
    internal IEnumerable<OciNode> Nodes => nodes.Values;
    internal OciNode CachedNode(string digest) => nodes[digest];
    internal void Clear()
    {
        objects.Clear();
        nodes.Clear();
        depths.Clear();
    }

    internal void CheckContext()
    {
        if (reader.Context != selected)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected OCI acquisition context changed.");
        }
    }

    internal async ValueTask<(byte[] Bytes, string Digest)> RootAsync(string reference, OciDescriptor? descriptor,
        CancellationToken cancellationToken)
    {
        var bytes = await FetchAsync(reference, true, descriptor, true, cancellationToken).ConfigureAwait(false);
        var digest = Hash(bytes);
        if (OciFormat.IsDigest(reference) && reference != digest) { throw Integrity(); }
        objects[(true, digest)] = bytes;
        depths[(true, digest)] = 0;
        return (bytes, digest);
    }

    internal async ValueTask<byte[]> BytesAsync(OciDescriptor descriptor, CancellationToken cancellationToken)
    {
        CheckContext();
        cancellationToken.ThrowIfCancellationRequested();
        Budget.ChargeWork();
        Budget.CheckObjectBytes(descriptor.Size);
        var manifest = descriptor.MediaType is OciFormat.Index or OciFormat.Manifest;
        Budget.CheckDepth(depths.GetValueOrDefault((manifest, descriptor.Digest)));
        if (objects.TryGetValue((manifest, descriptor.Digest), out var cached))
        {
            if (cached.LongLength != descriptor.Size) { throw Integrity(); }
            return cached;
        }
        var bytes = await FetchAsync(descriptor.Digest, manifest, descriptor, false, cancellationToken).ConfigureAwait(false);
        objects.Add((manifest, descriptor.Digest), bytes);
        return bytes;
    }

    internal async ValueTask<OciNode> NodeAsync(OciDescriptor descriptor, string kind, string xid,
        CancellationToken cancellationToken)
    {
        var bytes = await BytesAsync(descriptor, cancellationToken).ConfigureAwait(false);
        if (!nodes.TryGetValue(descriptor.Digest, out var node))
        {
            node = OciFormat.Node(OciJson.Parse(bytes, Budget, cancellationToken), bytes.Length);
            nodes.Add(descriptor.Digest, node);
        }
        if (node.Kind != kind || !OciFormat.SameXid(node.Xid, xid, kind == "collection") || node.MediaType != descriptor.MediaType ||
            descriptor.ArtifactType is not null && descriptor.ArtifactType != node.ArtifactType)
        {
            throw OciJson.Invalid("An OCI descriptor and its referenced node disagree.");
        }
        var childDepth = depths.GetValueOrDefault((true, descriptor.Digest)) + 1;
        foreach (var child in node.Entries) { Register(child); }
        if (node.Config is not null) { Register(node.Config); }
        if (node.Layer is not null) { Register(node.Layer); }
        return node;

        void Register(OciDescriptor child)
        {
            var key = (child.MediaType is OciFormat.Index or OciFormat.Manifest, child.Digest);
            depths[key] = Math.Max(depths.GetValueOrDefault(key), childDepth);
        }
    }

    private async ValueTask<byte[]> FetchAsync(string reference, bool manifest, OciDescriptor? descriptor,
        bool root, CancellationToken cancellationToken)
    {
        CheckContext();
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor is not null)
        {
            Budget.CheckObjectBytes(descriptor.Size);
            if (descriptor.MediaType == OciFormat.Index && descriptor.Size > OciFormat.MaxIndexBytes)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded, "An index descriptor exceeds the exact index byte bound.");
            }
        }
        Budget.ChargeRequest();
        Budget.ChargeObject();
        byte[] bytes;
        try
        {
            var response = manifest
                ? await reader.OpenManifestAsync(reference, cancellationToken).ConfigureAwait(false)
                : await reader.OpenBlobAsync(reference, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                throw new FederationException(root ? FederationErrorCode.NotFound : FederationErrorCode.InvalidPackage,
                    root ? "The selected OCI root is absent." : "A required OCI descendant is absent.");
            }
            await using (response.ConfigureAwait(false))
            {
                if (response.ContentLength is { } length)
                {
                    Budget.CheckObjectBytes(length);
                    if (descriptor is not null && descriptor.Size != length) { throw Integrity(); }
                }
                bytes = await ReadAsync(response.Content, Budget,
                    descriptor?.MediaType == OciFormat.Index || root ? OciFormat.MaxIndexBytes : Budget.Limits.MaxObjectBytes,
                    cancellationToken, descriptor?.Size).ConfigureAwait(false);
                if (descriptor is not null && (bytes.LongLength != descriptor.Size || Hash(bytes) != descriptor.Digest))
                {
                    throw Integrity();
                }
                if (response.ContentLength is { } actualLength && bytes.LongLength != actualLength) { throw Integrity(); }
                if (root && OciFormat.IsDigest(reference) && Hash(bytes) != reference) { throw Integrity(); }
                VerifyHeader(bytes, response.ContentDigest);
                if (manifest && response.MediaType is not null &&
                    (!MediaTypeHeaderValue.TryParse(response.MediaType, out var mediaType) ||
                    !string.Equals(mediaType.MediaType, descriptor?.MediaType ?? OciFormat.Index, StringComparison.OrdinalIgnoreCase)))
                {
                    throw OciJson.Invalid("The manifest response media type disagrees with the selected descriptor.");
                }
            }
        }
        catch (IOException exception)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "OCI object I/O failed.", innerException: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "OCI object access was denied.", innerException: exception);
        }
        CheckContext();
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static FederationException Integrity() => new(FederationErrorCode.IntegrityError, "OCI object size or digest verification failed.");

    internal static void VerifyHeader(byte[] bytes, string? digest)
    {
        if (digest is null) { return; }
        if (digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            if (Hash(bytes) != digest) { throw Integrity(); }
        }
        else if (digest.StartsWith("sha512:", StringComparison.Ordinal))
        {
            if ("sha512:" + Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant() != digest) { throw Integrity(); }
        }
        else
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "Unsupported transport content-digest algorithm.");
        }
    }

    internal static async ValueTask<byte[]> ReadAsync(Stream stream, FederationReadBudget budget, int limit,
        CancellationToken cancellationToken, long? expectedSize = null)
    {
        var output = new ArrayBufferWriter<byte>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bound = Math.Min(Math.Min(limit, budget.Limits.MaxObjectBytes), expectedSize ?? long.MaxValue);
            var count = (int)Math.Min(8192L, bound - output.WrittenCount + 1);
            var read = await stream.ReadAsync(output.GetMemory(count)[..count], cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            budget.ChargeBytes(read);
            budget.CheckObjectBytes((long)output.WrittenCount + read);
            if (expectedSize is not null && (long)output.WrittenCount + read > expectedSize) { throw Integrity(); }
            if ((long)output.WrittenCount + read > limit)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded, "An OCI input exceeds its exact byte bound.");
            }
            output.Advance(read);
        }
        return output.WrittenSpan.ToArray();
    }
}
