using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>A pinned, bounded, read-only native OCI snapshot over caller-owned object acquisition.</summary>
/// <remarks>One snapshot has one cumulative operation budget. Source ownership remains with the caller.</remarks>
public sealed partial class OciSnapshot : IAsyncDisposable, IFederationReadSource
{
    private readonly OciObjectSession session;
    private readonly OciDescriptor rootDescriptor;
    private readonly OciNode root;
    private readonly JsonElement registryRecord;

    private OciSnapshot(OciObjectSession session, OciDescriptor rootDescriptor, OciNode root,
        JsonElement registryRecord, RegistryModel model, string? reference)
    {
        this.session = session;
        this.rootDescriptor = rootDescriptor;
        this.root = root;
        this.registryRecord = registryRecord;
        Model = model;
        var selected = session.Selected;
        Context = new(selected.Binding, selected.Source, rootDescriptor.Digest, true, rootDescriptor.Digest[7..])
        {
            RequestedRevision = reference,
            RootPath = selected.RootPath,
        };
    }

    /// <summary>The exact selected root SHA-256 digest; never a tag or an xRegistry Version ID.</summary>
    public string RootDigest => rootDescriptor.Digest;
    /// <summary>The selected source plus immutable OCI root pin, not the original document base.</summary>
    public NativeRegistryContext Context { get; }
    /// <summary>The captured class: linked or offline-complete. Full validation is a separate operation.</summary>
    public string SnapshotClass => registryRecord.GetProperty("snapshot").GetString()!;
    /// <summary>The compiled captured model, including imported shared Resource type identities.</summary>
    public RegistryModel Model { get; }
    /// <summary>The original model source. Include URIs are provenance, not permission to fetch.</summary>
    public JsonElement ModelSource => registryRecord.GetProperty("entity").GetProperty("modelsource");
    /// <summary>The captured source after includes but before imports and implicit Core definitions.</summary>
    public JsonElement ResolvedModelSource => registryRecord.GetProperty("modelresolved");
    /// <summary>Enabled read-only capabilities, with the actual federation.resolution owner signal.</summary>
    public JsonElement Capabilities => registryRecord.GetProperty("entity").GetProperty("capabilities");

    /// <summary>Resolves an explicit Distribution reference exactly once, verifies it and bootstraps captured model metadata.</summary>
    public static ValueTask<OciSnapshot> OpenAsync(IOciObjectReader reader, string reference,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        OciFormat.Reference(reference);
        budget ??= new();
        budget.ChargeSource();
        return OpenCoreAsync(new(reader, budget), reference, null, reference, cancellationToken);
    }

    /// <summary>Selects from a standard OCI layout over a safe root-relative tree without depending on File.</summary>
    /// <remarks>
    /// The entrypoint is never treated as the Registry root. An omitted reference is a convenience
    /// selecting exactly one distinct eligible advertised root; native advertisements require an explicit reference.
    /// A digest may select a stored root not listed in index.json.
    /// </remarks>
    public static async ValueTask<OciSnapshot> OpenLayoutAsync(IDocumentTreeReader reader, string? reference = null,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reference is not null) { OciFormat.Reference(reference); }
        budget ??= new();
        budget.ChargeSource();
        var selected = reader.Context;
        var layout = OciJson.Parse(await OciLayout.BootstrapAsync(reader, "oci-layout", budget, cancellationToken).ConfigureAwait(false),
            budget, cancellationToken);
        if (OciJson.Text(layout, "imageLayoutVersion") != "1.0.0")
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported OCI image layout version.");
        }
        var indexBytes = await OciLayout.BootstrapAsync(reader, "index.json", budget, cancellationToken).ConfigureAwait(false);
        var index = OciJson.Parse(indexBytes, budget, cancellationToken);
        OciFormat.LayoutIndex(index, indexBytes.Length);
        OciDescriptor? descriptor = null;
        if (reference is null || !OciFormat.IsDigest(reference))
        {
            var matches = new List<JsonElement>();
            foreach (var entry in index.GetProperty("manifests").EnumerateArray())
            {
                OciJson.Object(entry);
                entry.TryGetProperty("annotations", out var annotations);
                if (reference is not null)
                {
                    if (annotations.ValueKind != JsonValueKind.Undefined &&
                        annotations.TryGetProperty("org.opencontainers.image.ref.name", out var tag) && tag.GetString() == reference)
                    {
                        matches.Add(entry);
                    }
                }
                else if (Eligible(entry)) { matches.Add(entry); }
            }
            if (reference is null) { matches = matches.DistinctBy(e => OciJson.Text(e, "digest"), StringComparer.Ordinal).ToList(); }
            if (matches.Count == 0) { throw new FederationException(FederationErrorCode.NotFound, "No selected Registry root is advertised."); }
            if (matches.Count != 1) { throw new FederationException(FederationErrorCode.Ambiguous, "The OCI layout selection is ambiguous."); }
            var match = matches[0];
            if (!Eligible(match)) { throw OciJson.Invalid("The selected layout entry is not a Registry root."); }
            if (match.TryGetProperty("annotations", out var selectedAnnotations)) { OciFormat.Annotations(selectedAnnotations); }
            var digest = OciJson.Text(match, "digest");
            OciFormat.Digest(digest);
            descriptor = new(OciFormat.Index, digest, OciJson.Integer(match, "size"),
                OciFormat.Artifact("registry"), "root", "/");
        }
        if (reader.Context != selected)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected layout context changed.");
        }
        return await OpenCoreAsync(new(new OciLayout(reader), budget), descriptor?.Digest ?? reference!,
            descriptor, reference, cancellationToken).ConfigureAwait(false);
    }

    private static bool Eligible(JsonElement entry) =>
        entry.TryGetProperty("mediaType", out var media) && media.ValueKind == JsonValueKind.String && media.GetString() == OciFormat.Index &&
        entry.TryGetProperty("artifactType", out var artifact) && artifact.ValueKind == JsonValueKind.String &&
        artifact.GetString() == OciFormat.Artifact("registry");

    private static async ValueTask<OciSnapshot> OpenCoreAsync(OciObjectSession session, string selection,
        OciDescriptor? selectedDescriptor, string? requestedReference, CancellationToken cancellationToken)
    {
        var (bytes, digest) = await session.RootAsync(selection, selectedDescriptor, cancellationToken).ConfigureAwait(false);
        var descriptor = new OciDescriptor(OciFormat.Index, digest, bytes.Length, OciFormat.Artifact("registry"), "root", "/");
        var root = await session.NodeAsync(descriptor, "registry", "/", cancellationToken).ConfigureAwait(false);
        var manifest = await session.NodeAsync(root.Entries[0], "registry", "/", cancellationToken).ConfigureAwait(false);
        var configBytes = await session.BytesAsync(manifest.Config!, cancellationToken).ConfigureAwait(false);
        var record = OciJson.Parse(configBytes, session.Budget, cancellationToken);
        var model = Bootstrap(record, session.Budget);
        var validated = OciRecords.Validate(record, "registry", "/", model, session.Budget, cancellationToken);
        var snapshot = new OciSnapshot(session, descriptor, root, record, model, requestedReference);
        snapshot.CacheRecord(manifest.Config!.Digest, validated);
        return snapshot;
    }

    internal static RegistryModel Bootstrap(JsonElement record, FederationReadBudget budget)
    {
        OciJson.Fields(record, "formatversion", "kind", "entity", "snapshot", "modelresolved");
        if (OciJson.Integer(record, "formatversion") != 1)
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported OCI record format version.");
        }
        if (OciJson.Text(record, "kind") != "registry" || OciJson.Text(record, "snapshot") is not ("linked" or "offline-complete"))
        {
            throw OciJson.Invalid("The root config must describe one linked or offline-complete Registry.");
        }
        var entity = OciJson.Required(record, "entity");
        if (OciJson.Text(entity, "xid") != "/" || !RegistryId.IsValid(OciJson.Text(entity, "registryid")))
        {
            throw OciJson.Invalid("The captured Registry identity is invalid.");
        }
        if (OciJson.Text(entity, "specversion") != "1.0-rc4")
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported captured Core version.");
        }
        var source = OciJson.Required(entity, "modelsource");
        var resolved = OciJson.Required(record, "modelresolved");
        var full = OciJson.Required(entity, "model");
        OciJson.Object(source);
        OciJson.Object(resolved);
        OciJson.Object(full);
        if (OciJson.Includes(full))
        {
            throw OciJson.Invalid("Captured model source material is unresolved or contradictory.");
        }
        var capabilities = OciJson.Required(entity, "capabilities");
        var available = OciJson.Required(capabilities, "available");
        OciJson.Object(available);
        var names = available.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.IsSupersetOf(["capabilities", "entities", "model"]) || names.Count != available.EnumerateObject().Count())
        {
            throw OciJson.Invalid("Snapshot capabilities require unique capabilities, entities and model availability.");
        }
        foreach (var item in available.EnumerateObject())
        {
            OciJson.Object(item.Value);
            if (OciJson.Boolean(item.Value, "mutable"))
            {
                throw OciJson.Invalid("An OCI snapshot cannot advertise mutable access.");
            }
        }
        foreach (var name in new[] { "flags", "mutable" })
        {
            if (capabilities.TryGetProperty(name, out var values) &&
                (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 0))
            {
                throw OciJson.Invalid("Native OCI does not advertise HTTP flags or mutations.");
            }
        }
        if (capabilities.TryGetProperty("pagination", out _) && OciJson.Boolean(capabilities, "pagination"))
        {
            throw OciJson.Invalid("Native OCI collections are complete, not HTTP pages.");
        }
        FederationCapabilities.GetResolutionOwner(capabilities);
        try
        {
            var options = new RegistryModelCompilationOptions
            {
                MaxIncludeDocuments = 0,
                MaxExpandedNodes = OciJson.Limits(budget).MaxNodes,
                JsonLimits = OciJson.Limits(budget),
            };
            var model = RegistryModel.CompileCaptured(RegistryJson.FromElement(source, OciJson.Limits(budget)),
                RegistryJson.FromElement(resolved, OciJson.Limits(budget)), options);
            OciJson.ValidateSourcePolicy(source, budget);
            RequireFullDefinitions(full, model.EffectiveModel.RootElement);
            var comparison = OciJson.Copy(full)!.AsObject();
            foreach (var group in model.Groups)
            {
                foreach (var resource in group.Value.Resources)
                {
                    var xref = comparison["groups"]![group.Key]!["resources"]![resource.Key]!["metaattributes"]!["xref"]!;
                    // The frozen OCI full-model projection narrows Core URL xref to local XID.
                    // Interpret imports using the real source model; never rewrite the captured result.
                    var declared = full.GetProperty("groups").GetProperty(group.Key).GetProperty("resources")
                        .GetProperty(resource.Key).GetProperty("metaattributes").GetProperty("xref");
                    if (declared.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "xid")
                    {
                        xref["type"] = "url";
                    }
                }
            }
            var fullModel = RegistryModel.Compile(RegistryJson.Parse(comparison.ToJsonString(), OciJson.Limits(budget)), options);
            if (!OciJson.Equal(model.EffectiveModel.RootElement, fullModel.EffectiveModel.RootElement))
            {
                throw OciJson.Invalid("The full model contradicts the captured resolved model source.");
            }
            return model;
        }
        catch (RegistryException exception) { throw OciJson.CoreError(exception); }
    }

    private static void RequireFullDefinitions(JsonElement full, JsonElement expected)
    {
        foreach (var property in expected.EnumerateObject())
        {
            if (property.Name is not ("attributes" or "groups" or "resources" or "resourceattributes" or "metaattributes" or "item"))
            {
                continue;
            }
            var actual = OciJson.Required(full, property.Name);
            OciJson.Object(actual);
            if (property.Name == "item")
            {
                RequireFullDefinitions(actual, property.Value);
                continue;
            }
            var keys = actual.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            if (!keys.SetEquals(property.Value.EnumerateObject().Select(p => p.Name)))
            {
                throw OciJson.Invalid("The captured full model must contain every resolved and Core definition.");
            }
            foreach (var definition in property.Value.EnumerateObject())
            {
                var value = actual.GetProperty(definition.Name);
                OciJson.Object(value);
                RequireFullDefinitions(value, definition.Value);
            }
        }
    }

    /// <summary>Releases session state. Caller-owned readers and independently owned results remain valid.</summary>
    public ValueTask DisposeAsync()
    {
        var state = Interlocked.CompareExchange(ref reading, 2, 0);
        if (state == 1) { throw new InvalidOperationException("An active OCI operation cannot be disposed."); }
        if (state == 0)
        {
            session.Clear();
            records.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
