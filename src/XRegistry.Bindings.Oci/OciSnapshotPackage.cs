using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>An independently owned, content-addressed output object. Opaque documents are never interpreted as manifests.</summary>
public sealed class OciSnapshotObject
{
    private readonly byte[] bytes;

    internal OciSnapshotObject(byte[] bytes, string mediaType, string? artifactType)
    {
        this.bytes = bytes;
        MediaType = mediaType;
        ArtifactType = artifactType;
        Digest = OciObjectSession.Hash(bytes);
    }
    /// <summary>SHA-256 over the exact output bytes.</summary>
    public string Digest { get; }
    /// <summary>The exact byte length, including zero for a real empty document.</summary>
    public long Size => bytes.LongLength;
    /// <summary>The object's OCI media type, not a domain content type.</summary>
    public string MediaType { get; }
    /// <summary>The index/manifest artifact type; absent for blobs.</summary>
    public string? ArtifactType { get; }
    /// <summary>Whether this object uses the Distribution manifests route.</summary>
    public bool IsManifest => MediaType is OciFormat.Index or OciFormat.Manifest;
    /// <summary>Opens an independently positioned, caller-owned, non-writable exact-byte stream.</summary>
    public Stream OpenRead() => new MemoryStream(bytes, 0, bytes.Length, false, false);
    internal void VerifyDigestHeader(string? digest) => OciObjectSession.VerifyHeader(bytes, digest);
}

/// <summary>A fully validated immutable producer graph, ready for explicit publication or safe layout composition.</summary>
public sealed class OciSnapshotPackage
{
    internal OciSnapshotPackage(OciSnapshotObject root, OciSnapshotObject[] objects, OciValidationResult validation)
    {
        Root = root;
        Objects = Array.AsReadOnly(objects);
        Validation = validation;
    }
    /// <summary>The selected Registry root object.</summary>
    public OciSnapshotObject Root { get; }
    /// <summary>The exact selected Registry root digest.</summary>
    public string RootDigest => Root.Digest;
    /// <summary>Owned route objects in deterministic, descendants-before-parents construction order.</summary>
    public ReadOnlyCollection<OciSnapshotObject> Objects { get; }
    /// <summary>The exhaustive shared-reader validation completed before this package became publishable.</summary>
    public OciValidationResult Validation { get; }
    /// <summary>Creates a no-I/O reader over these owned objects; manifest and blob routes remain distinct.</summary>
    public IOciObjectReader CreateReader() => new PackageReader(RootDigest, Objects);

    /// <summary>Creates a bounded standard layout entrypoint selecting only this root under the explicit reference.</summary>
    /// <remarks>The parent File host owns safe filesystem writes and atomic entrypoint replacement. Store objects first.</remarks>
    public FederationDocument CreateLayoutEntryPoint(string reference)
    {
        OciFormat.Reference(reference);
        if (OciFormat.IsDigest(reference) && reference != RootDigest) { throw OciObjectSession.Integrity(); }
        var entry = new JsonObject
        {
            ["mediaType"] = OciFormat.Index,
            ["artifactType"] = OciFormat.Artifact("registry"),
            ["digest"] = RootDigest,
            ["size"] = Root.Size,
        };
        if (!OciFormat.IsDigest(reference))
        {
            entry["annotations"] = new JsonObject { ["org.opencontainers.image.ref.name"] = reference };
        }
        var index = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = OciFormat.Index,
            ["manifests"] = new JsonArray(entry),
        };
        return new(OciEncoding.Canonical(index, OciFormat.MaxIndexBytes), OciFormat.Index);
    }

    /// <summary>Publishes every blob, then child manifests/indexes and root, and finally the explicitly selected reference.</summary>
    /// <remarks>A failure leaves at most unreferenced immutable objects; it never retries at another root or silently advances a tag.</remarks>
    public async ValueTask PublishAsync(IOciPublisher publisher, string reference, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        OciFormat.Reference(reference);
        if (OciFormat.IsDigest(reference) && reference != RootDigest) { throw OciObjectSession.Integrity(); }
        budget ??= new(OciWriteOptions.DefaultLimits);
        var context = publisher.Context;
        budget.ChargeSource();
        foreach (var item in Objects.Where(o => !o.IsManifest).Concat(Objects.Where(o => o.IsManifest)))
        {
            Check();
            budget.CheckObjectBytes(item.Size);
            budget.ChargeObject();
            budget.ChargeBytes(item.Size);
            budget.ChargeRequest();
            if (item.IsManifest) { await publisher.PutManifestAsync(item, cancellationToken).ConfigureAwait(false); }
            else { await publisher.PutBlobAsync(item, cancellationToken).ConfigureAwait(false); }
            Check();
        }
        budget.ChargeRequest();
        Check();
        await publisher.CommitReferenceAsync(reference, Root, cancellationToken).ConfigureAwait(false);
        Check();

        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (context != publisher.Context)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The authorized OCI publication destination changed.");
            }
        }
    }

    internal sealed class PackageReader : IOciObjectReader
    {
        private readonly Dictionary<(bool Manifest, string Digest), OciSnapshotObject> objects;
        internal PackageReader(string root, IEnumerable<OciSnapshotObject> objects)
        {
            this.objects = objects.ToDictionary(o => (o.IsManifest, o.Digest));
            Context = new("oci", "urn:xregistry:oci:" + root, root, true);
        }
        public NativeRegistryContext Context { get; }
        public ValueTask<OciObjectResponse?> OpenManifestAsync(string reference, CancellationToken cancellationToken = default) =>
            Read(reference, true, cancellationToken);
        public ValueTask<OciObjectResponse?> OpenBlobAsync(string digest, CancellationToken cancellationToken = default) =>
            Read(digest, false, cancellationToken);
        private ValueTask<OciObjectResponse?> Read(string digest, bool manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OciFormat.Digest(digest);
            return ValueTask.FromResult(objects.TryGetValue((manifest, digest), out var item)
                ? new OciObjectResponse(item.OpenRead(), manifest ? item.MediaType : null, contentLength: item.Size) : null);
        }
    }
}
