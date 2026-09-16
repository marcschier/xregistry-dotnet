// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Federation;

/// <summary>One explicitly authorized, lazily opened source in an ordered producer view.</summary>
public sealed class FederationSourceRegistration
{
    /// <summary>Creates a registration. Each invocation must return a fresh, exclusively owned session.</summary>
    public FederationSourceRegistration(string name, FederationRepresentation representation,
        Func<FederationReadBudget, CancellationToken, ValueTask<FederationSourceLease>> open,
        Func<CancellationToken, ValueTask<string>>? currentCredentialStamp = null,
        ProducerTargetValidator? validateTarget = null)
    {
        Name = RegistryId.Parse(name).Value;
        if (!Enum.IsDefined(representation)) { throw new ArgumentOutOfRangeException(nameof(representation)); }
        ArgumentNullException.ThrowIfNull(open);
        Representation = representation;
        Open = open;
        CurrentCredentialStamp = currentCredentialStamp;
        ValidateTarget = validateTarget;
    }

    /// <summary>A credential-free policy/provenance name, not a Core entity attribute.</summary>
    public string Name { get; }
    /// <summary>The explicitly selected source representation; no unsupported-view fallback is performed.</summary>
    public FederationRepresentation Representation { get; }
    /// <summary>The authorized acquisition callback, invoked at most once per view session.</summary>
    public Func<FederationReadBudget, CancellationToken, ValueTask<FederationSourceLease>> Open { get; }
    /// <summary>Returns the current opaque credential/access-profile fingerprint for retained-read checks, never the secret itself.</summary>
    public Func<CancellationToken, ValueTask<string>>? CurrentCredentialStamp { get; }
    /// <summary>
    /// Explicit source-context validation for URI target obligations. Include mutable
    /// policy state in the credential/access-profile stamp used for retained reads.
    /// </summary>
    public ProducerTargetValidator? ValidateTarget { get; }
}

/// <summary>An exclusively owned read session and its complete disposal action.</summary>
public sealed class FederationSourceLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;
    private int _disposed;

    /// <summary>Creates a lease; the action must dispose all readers/transports owned by this source.</summary>
    public FederationSourceLease(IFederationReadSource source, Func<ValueTask> dispose, string credentialStamp = "")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dispose);
        Source = source;
        _dispose = dispose;
        ArgumentNullException.ThrowIfNull(credentialStamp);
        if (credentialStamp.Length > 256 || credentialStamp.Any(char.IsControl))
        {
            throw new ArgumentException("A credential stamp must be bounded and opaque.", nameof(credentialStamp));
        }
        CredentialStamp = credentialStamp;
    }

    /// <summary>The selected source, which must not be shared with another active lease.</summary>
    public IFederationReadSource Source { get; }
    /// <summary>The fingerprint captured with this lease's credentials, not a token or Core attribute.</summary>
    public string CredentialStamp { get; }
    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? _dispose() : ValueTask.CompletedTask;
}

/// <summary>Provenance retained separately from the Core metadata document.</summary>
public sealed record FederationViewOrigin(string Name, NativeRegistryContext Context, string CredentialStamp = "");

/// <summary>A source-local path used by a capture, for current retained-page authorization checks.</summary>
public sealed record FederationViewDependency(string Source, RegistryPath Path);

/// <summary>A complete, detached producer response and optional exact Document or external descriptor.</summary>
public sealed class ProducerRegistryResult
{
    internal ProducerRegistryResult(string selectedXid, JsonElement metadata, FederationDocument? document,
        JsonElement external, IEnumerable<FederationViewOrigin> origins, IEnumerable<FederationViewDependency> dependencies)
    {
        SelectedXid = selectedXid;
        Metadata = metadata.ValueKind == JsonValueKind.Undefined ? default : metadata.Clone();
        Document = document;
        ExternalDocument = external.ValueKind == JsonValueKind.Undefined ? default : external.Clone();
        Origins = Array.AsReadOnly(origins.ToArray());
        Dependencies = Array.AsReadOnly(dependencies.ToArray());
    }

    /// <summary>The selected logical entity or Version, not a mutable fallback selector.</summary>
    public string SelectedXid { get; }
    /// <summary>The Core HTTP metadata representation, without a binding envelope or provenance attributes.</summary>
    public JsonElement Metadata { get; }
    /// <summary>Present exact bytes, including an empty Document.</summary>
    public FederationDocument? Document { get; }
    /// <summary>An unfetched descriptor, never interchangeable with empty Document bytes.</summary>
    public JsonElement ExternalDocument { get; }
    /// <summary>Whether this result carries domain bytes or an external Document redirect rather than Core metadata.</summary>
    public bool IsDocument => Document is not null || ExternalDocument.ValueKind != JsonValueKind.Undefined;
    /// <summary>Contributing authorized source contexts, outside the Core metadata.</summary>
    public ReadOnlyCollection<FederationViewOrigin> Origins { get; }
    /// <summary>Captured source-local authorization dependencies, separate from the Core representation.</summary>
    public ReadOnlyCollection<FederationViewDependency> Dependencies { get; }
}

/// <summary>Bounded request-scoped producer materialization over ordered, caller-authorized sources.</summary>
/// <remarks>
/// Ordinary Registry/Group metadata is first-source-wins; complete membership is composed.
/// Resources, Meta and every Version/Document remain one source unit. There is no catalog discovery,
/// shared cache, immutable HTTP claim, native-view fallback, or concurrent reuse of a source session.
/// Core API/document views and one-hop source-local aliases are materialized without changing origin meaning.
/// Unsupported cross-source alias projections and model obligations fail explicitly.
/// </remarks>
public sealed partial class ProducerRegistryView : IAsyncDisposable
{
    private static readonly RegistryJson s_capabilities = RegistryJson.Parse("""
        {"available":{"entities":{"mutable":false},"model":{"mutable":false},
        "modelsource":{"mutable":false},"capabilities":{"mutable":false}},
        "flags":["binary","collections","doc","filter","inline","sort"],"pagination":false,"shortself":false,"specversions":["1.0-rc4"],
        "federation":{"resolution":"producer"}}
        """);
    private readonly RegistryModel _model;
    private readonly Uri _publicRoot;
    private readonly string _registryId;
    private readonly FederationReadBudget _budget;
    private readonly JsonElement _capabilities;
    private readonly Slot[] _slots;
    private readonly Dictionary<string, Slot> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<string, Member>> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FederationViewOrigin> _origins = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target), FederationViewDependency> _dependencies = [];
    private int _reading;
    private bool _disposed;

    /// <summary>Creates a finite operation; authorization must precede constructing its eligible source list.</summary>
    public ProducerRegistryView(RegistryModel model, Uri publicRoot, string registryId,
        IReadOnlyList<FederationSourceRegistration> sources, FederationReadBudget? budget = null, JsonElement? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(publicRoot);
        ArgumentNullException.ThrowIfNull(sources);
        if (!publicRoot.IsAbsoluteUri || publicRoot.Scheme is not ("http" or "https") ||
            publicRoot.UserInfo.Length != 0 || publicRoot.Query.Length != 0 || publicRoot.Fragment.Length != 0)
        {
            throw new ArgumentException("An explicit credential-free public HTTP root is required.", nameof(publicRoot));
        }
        if (sources.Count is < 1 or > 8 || sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
        {
            throw new ArgumentException("One to eight uniquely named, ordered sources are required.", nameof(sources));
        }
        _model = model;
        _publicRoot = new Uri(publicRoot.AbsoluteUri.TrimEnd('/'));
        _registryId = RegistryId.Parse(registryId).Value;
        _budget = budget ?? new();
        _capabilities = capabilities?.Clone() ?? Capabilities;
        if (FederationCapabilities.GetResolutionOwner(_capabilities) != FederationResolutionOwner.Producer)
        {
            throw new ArgumentException("The enabled view capabilities must retain producer ownership.", nameof(capabilities));
        }
        _slots = sources.Select(source => new Slot(this, source)).ToArray();
    }

    /// <summary>The enabled bounded read-only producer profile; unsupported flags are not advertised.</summary>
    public static JsonElement Capabilities => s_capabilities.RootElement;

    /// <summary>Checks model-defined route membership without opening a source.</summary>
    public RegistryResourceDefinition? Resource(RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.GroupType is null) { return null; }
        if (!_model.Groups.TryGetValue(path.GroupType, out var group)) { throw NotFound(); }
        if (path.ResourceType is null) { return null; }
        return group.Resources.TryGetValue(path.ResourceType, out var resource) ? resource : throw NotFound();
    }

    /// <summary>Reads one Core API path. Documents are selected by the model and literal $details, not Content-Type.</summary>
    public ValueTask<ProducerRegistryResult> ReadAsync(RegistryPath path, CancellationToken cancellationToken = default) =>
        ReadAsync(path, new ProducerViewOptions(), cancellationToken);

    /// <summary>Materializes requested Core representations over the already ordered, source-pinned view.</summary>
    public async ValueTask<ProducerRegistryResult> ReadAsync(RegistryPath path, ProducerViewOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException("A producer view permits one active read.");
        }
        try
        {
            Resource(path);
            var captured = options.Capture(_model, path);
            CheckCaptures(cancellationToken);
            var result = captured.IsDefault && path.Kind != RegistryPathKind.Registry
                ? await ReadBaseAsync(path, cancellationToken).ConfigureAwait(false)
                : await new Presentation(this, captured).ReadAsync(path, cancellationToken).ConfigureAwait(false);
            CheckCaptures(cancellationToken);
            return result;
        }
        catch (RegistryException exception) { throw FederationJson.FromCore(exception); }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private async ValueTask<ProducerRegistryResult> ReadBaseAsync(RegistryPath path, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = Resource(path);
            if (path.Kind == RegistryPathKind.Model) { return Result("/", _model.EffectiveModel.RootElement); }
            if (path.Kind == RegistryPathKind.ModelSource) { return Result("/", _model.Source.RootElement); }
            if (path.Kind == RegistryPathKind.Capabilities) { return Result("/", _capabilities); }
            if (path.Kind is RegistryPathKind.Export or RegistryPathKind.CapabilitiesOffered or RegistryPathKind.Discovery)
            {
                throw Unsupported("This producer profile does not implement this administrative view.");
            }
            var target = LogicalPath(path);
            var collection = path.Kind is RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection;
            var document = resource?.HasDocument == true && !path.IsDetails &&
                path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version;
            var first = await _slots[0].GetAsync(cancellationToken).ConfigureAwait(false);
            if (collection) { await EnsureCollectionOwnerAsync(path, cancellationToken).ConfigureAwait(false); }
            if (first.Owner == FederationResolutionOwner.Producer)
            {
                return await DirectAsync(first, path, target, collection, document, cancellationToken).ConfigureAwait(false);
            }
            if (path.ResourceId is not null)
            {
                var owner = await SelectAsync(Owner(target), cancellationToken).ConfigureAwait(false);
                var alias = await AliasAsync(owner, Owner(target), cancellationToken).ConfigureAwait(false);
                if (alias is not null) { return await ReadAliasAsync(alias, path, document, cancellationToken).ConfigureAwait(false); }
            }
            if (document)
            {
                var selected = await SelectAsync(Owner(target), cancellationToken).ConfigureAwait(false);
                var read = await selected.ReadAsync(new(FederationOperation.Document, target), cancellationToken).ConfigureAwait(false);
                CheckDocumentIdentity(target, read.SelectedXid);
                var metadata = await selected.ReadAsync(new(FederationOperation.Entity, read.SelectedXid), cancellationToken).ConfigureAwait(false);
                var value = Entity(metadata);
                var selectedPath = PathFor(read.SelectedXid, details: true);
                value = await NormalizeAsync(selected, value, selectedPath, cancellationToken).ConfigureAwait(false);
                return Result(read.SelectedXid, Element(value), read.Document, read.ExternalDocument);
            }
            if (collection)
            {
                var values = path.Kind == RegistryPathKind.VersionCollection
                    ? await VersionsAsync(await SelectAsync(Owner(target), cancellationToken).ConfigureAwait(false), Owner(target), cancellationToken).ConfigureAwait(false)
                    : await CollectionAsync(target, cancellationToken).ConfigureAwait(false);
                var map = new JsonObject();
                foreach (var item in values)
                {
                    _budget.ChargeWork();
                    var memberPath = PathFor(target + "/" + item.Key, details: true);
                    map[item.Key] = await MaterializeAsync(item.Value.Slot, item.Value.Result, memberPath, cancellationToken).ConfigureAwait(false);
                }
                return Result(target, Element(map));
            }
            if (path.Kind is RegistryPathKind.Registry or RegistryPathKind.Group)
            {
                var selected = path.Kind == RegistryPathKind.Registry ? first : await SelectGroupAsync(target, cancellationToken).ConfigureAwait(false);
                var raw = await selected.ReadAsync(new(FederationOperation.Entity, target), cancellationToken).ConfigureAwait(false);
                return Result(target, Element(await MaterializeAsync(selected, raw, path, cancellationToken).ConfigureAwait(false)));
            }
            var source = await SelectAsync(Owner(target), cancellationToken).ConfigureAwait(false);
            var resolver = new ResourceFederationResolver(source, [], static (_, _) => true, budget: _budget);
            var resolved = await resolver.ReadAsync(new(FederationOperation.Entity, target), cancellationToken).ConfigureAwait(false);
            return Result(target, Element(await MaterializeAsync(source,
                resolved.Selected ?? throw Invalid("The selected Resource returned no entity."), path, cancellationToken).ConfigureAwait(false)));
        }
        catch (RegistryException exception) { throw FederationJson.FromCore(exception); }
    }

    private async ValueTask<ProducerRegistryResult> DirectAsync(Slot source, RegistryPath path, string target,
        bool collection, bool document, CancellationToken token)
    {
        var operation = document ? FederationOperation.Document : collection ? FederationOperation.Collection : FederationOperation.Entity;
        if (document || path.ResourceId is not null && source.Representation == FederationRepresentation.DocumentView)
        {
            var alias = await AliasAsync(source, Owner(target), token).ConfigureAwait(false);
            if (alias is not null) { return await ReadAliasAsync(alias, path, document, token).ConfigureAwait(false); }
        }
        var result = await source.ReadAsync(new(operation, target), token).ConfigureAwait(false);
        if (document)
        {
            CheckDocumentIdentity(target, result.SelectedXid);
            var metadata = await source.ReadAsync(new(FederationOperation.Entity, result.SelectedXid), token).ConfigureAwait(false);
            return Result(result.SelectedXid, Element(await NormalizeAsync(source, Entity(metadata),
                PathFor(result.SelectedXid, true), token).ConfigureAwait(false)),
                result.Document, result.ExternalDocument);
        }
        if (collection)
        {
            var map = new JsonObject();
            foreach (var item in Complete(source, target, result))
            {
                var memberPath = PathFor(target + "/" + item.Key, true);
                var entity = Entity(item.Value.Result);
                if (memberPath.Kind == RegistryPathKind.Resource && HasAlias(entity))
                {
                    var alias = await AliasAsync(source, LogicalPath(memberPath), token).ConfigureAwait(false)
                        ?? throw Invalid("A producer alias lost its source-local Meta.");
                    map[item.Key] = await AliasMetadataAsync(alias, memberPath, token).ConfigureAwait(false);
                }
                else
                {
                    var projection = await CapturedProjectionAsync(source, entity, memberPath, token).ConfigureAwait(false);
                    map[item.Key] = await NormalizeAsync(source, projection, memberPath, token).ConfigureAwait(false);
                }
            }
            return Result(target, Element(map));
        }
        if (path.Kind is RegistryPathKind.Resource or RegistryPathKind.Meta && HasAlias(Entity(result)))
        {
            var alias = await AliasAsync(source, Owner(target), token).ConfigureAwait(false);
            if (alias is not null) { return await ReadAliasAsync(alias, path, false, token).ConfigureAwait(false); }
        }
        var projected = await CapturedProjectionAsync(source, Entity(result), path, token).ConfigureAwait(false);
        return Result(target, Element(await NormalizeAsync(source, projected, path, token).ConfigureAwait(false)));
    }

    private async ValueTask<JsonObject> CapturedProjectionAsync(Slot source, JsonObject value, RegistryPath path, CancellationToken token)
    {
        CheckEntityIdentity(value, path);
        if (path.Kind != RegistryPathKind.Resource || value.ContainsKey("versionid")) { return value; }
        RejectAlias(value);
        var target = LogicalPath(path);
        if (value["meta"] is not JsonObject rawMeta || value["versions"] is not JsonObject versions ||
            value["versionscount"] is not JsonValue countValue || !countValue.TryGetValue<int>(out var count) || count != versions.Count)
        {
            throw Invalid("A native producer Resource needs its complete captured Meta and Version set.");
        }
        var meta = await NormalizeAsync(source, rawMeta.DeepClone().AsObject(), PathFor(target + "/meta"), token).ConfigureAwait(false);
        var selectedId = RequiredString(meta, "defaultversionid");
        JsonObject? selected = null;
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in versions)
        {
            _budget.ChargeWork();
            RegistryId.Parse(entry.Key);
            if (!identifiers.Add(entry.Key) || entry.Value is not JsonObject version)
            {
                throw Invalid("A native producer Version set contains ambiguous members.");
            }
            var normalized = await NormalizeAsync(source, version.DeepClone().AsObject(),
                PathFor(target + "/versions/" + entry.Key, true), token).ConfigureAwait(false);
            if (normalized["isdefault"] is not JsonValue flag || !flag.TryGetValue<bool>(out var isDefault) || isDefault != (entry.Key == selectedId))
            {
                throw Invalid("A captured producer Version contradicts its default selection.");
            }
            if (entry.Key == selectedId) { selected = normalized; }
        }
        if (selected is null) { throw Invalid("The captured producer default Version is absent."); }
        foreach (var property in selected)
        {
            if (property.Key is not ("self" or "xid" or "shortself")) { value[property.Key] = property.Value?.DeepClone(); }
        }
        return value;
    }

    private async ValueTask<JsonObject> MaterializeAsync(Slot source, FederationReadResult result, RegistryPath path, CancellationToken token)
    {
        var target = LogicalPath(path);
        var value = Entity(result);
        CheckEntityIdentity(value, path);
        if (path.Kind is RegistryPathKind.Registry or RegistryPathKind.Group)
        {
            var names = path.Kind == RegistryPathKind.Registry ? _model.Groups.Keys : _model.Groups[path.GroupType!].Resources.Keys;
            foreach (var name in names)
            {
                var collection = (target == "/" ? "" : target) + "/" + name;
                var members = await CollectionAsync(collection, token).ConfigureAwait(false);
                value[name + "count"] = members.Count;
            }
        }
        else if (path.Kind == RegistryPathKind.Resource)
        {
            var alias = await AliasAsync(source, target, token).ConfigureAwait(false);
            if (alias is not null) { return await AliasMetadataAsync(alias, path, token).ConfigureAwait(false); }
            var metaRead = await source.ReadAsync(new(FederationOperation.Entity, target + "/meta"), token).ConfigureAwait(false);
            var meta = await NormalizeAsync(source, Entity(metaRead), PathFor(target + "/meta"), token).ConfigureAwait(false);
            var defaultId = RequiredString(meta, "defaultversionid");
            var versions = await VersionsAsync(source, target, token).ConfigureAwait(false);
            if (!versions.TryGetValue(defaultId, out var selected)) { throw Invalid("The selected Resource default Version is absent."); }
            var selectedVersion = await NormalizeAsync(source, Entity(selected.Result),
                PathFor(target + "/versions/" + defaultId, true), token).ConfigureAwait(false);
            foreach (var item in selectedVersion)
            {
                if (item.Key is not ("self" or "xid" or "shortself")) { value[item.Key] = item.Value?.DeepClone(); }
            }
            value["versionscount"] = versions.Count;
            value.Remove("meta");
        }
        return await NormalizeAsync(source, value, path, token).ConfigureAwait(false);
    }

    private async ValueTask<SortedDictionary<string, Member>> VersionsAsync(Slot source, string owner, CancellationToken token)
    {
        var key = source.Name + ":" + owner + "/versions";
        if (_collections.TryGetValue(key, out var cached)) { return cached; }
        var resource = Entity(await source.ReadAsync(new(FederationOperation.Entity, owner), token).ConfigureAwait(false));
        var ownerPath = PathFor(owner);
        var definition = Resource(ownerPath)!;
        var modelChecks = MatchingAttributes(definition, token).Count != 0 ||
            _model.Groups[ownerPath.GroupType!].Constraints.Values.Any(c => c.ResourceType == definition.Plural);
        FederationReadResult result;
        if (resource["versions"] is JsonObject captured &&
            (source.Representation == FederationRepresentation.DocumentView || modelChecks))
        {
            if (resource["versionscount"] is not JsonValue count || !count.TryGetValue<int>(out var length) || length != captured.Count)
            {
                throw Invalid("A captured Version set disagrees with its complete count.");
            }
            result = FederationReadResult.FromMetadata(owner + "/versions", source.Context,
                RegistryJson.Parse(new JsonObject { ["complete"] = true, ["entities"] = captured.DeepClone() }.ToJsonString(), JsonLimits()).RootElement);
        }
        else
        {
            result = await source.ReadAsync(new(FederationOperation.Collection, owner + "/versions"), token).ConfigureAwait(false);
        }
        var values = Complete(source, owner + "/versions", result);
        var meta = await ModelMetaAsync(source, owner, token).ConfigureAwait(false);
        var defaultId = RequiredString(meta, "defaultversionid");
        if (!values.ContainsKey(defaultId)) { throw Invalid("The complete Version set has no declared default."); }
        if (resource["versionscount"] is JsonValue advertised &&
            (!advertised.TryGetValue<int>(out var expectedCount) || expectedCount != values.Count))
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected Resource's complete Version count changed.");
        }
        var metadata = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var value = await NormalizeAsync(source, Entity(item.Value.Result),
                PathFor(owner + "/versions/" + item.Key, true), token, checkUnit: false).ConfigureAwait(false);
            if (value["isdefault"] is not JsonValue state || !state.TryGetValue<bool>(out var isDefault) || isDefault != (item.Key == defaultId))
            {
                throw Invalid("The Version default state contradicts the selected Resource Meta.");
            }
            metadata.Add(item.Key, value);
            TrackModelDependency(source, PathFor(owner + "/versions/" + item.Key));
        }
        CheckMatchingSet(Resource(PathFor(owner))!, metadata.Values, token);
        _modelVersions.Add((source, owner), metadata);
        _collections.Add(key, values);
        return values;
    }

    private async ValueTask<SortedDictionary<string, Member>> CollectionAsync(string target, CancellationToken token)
    {
        if (_collections.TryGetValue(target, out var cached)) { return cached; }
        var parts = FederationSyntax.Xid(target, true);
        var values = new SortedDictionary<string, Member>(StringComparer.Ordinal);
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in _slots)
        {
            var source = await candidate.GetAsync(token).ConfigureAwait(false);
            if (parts.Length == 3)
            {
                try { await source.ReadAsync(new(FederationOperation.Entity, "/" + parts[0] + "/" + parts[1]), token).ConfigureAwait(false); }
                catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound) { continue; }
            }
            var result = await source.ReadAsync(new(FederationOperation.Collection, target), token).ConfigureAwait(false);
            foreach (var item in Complete(source, target, result))
            {
                _budget.ChargeWork();
                if (spellings.TryGetValue(item.Key, out var prior) && prior != item.Key)
                {
                    throw new FederationException(FederationErrorCode.Ambiguous, "The visible collection contains case-colliding identities.");
                }
                spellings[item.Key] = item.Key;
                if (values.TryAdd(item.Key, item.Value) && parts.Length == 3)
                {
                    _owners.TryAdd(target + "/" + item.Key, source);
                }
            }
        }
        _collections.Add(target, values);
        return values;
    }

    private SortedDictionary<string, Member> Complete(Slot source, string target, FederationReadResult result)
    {
        var envelope = result.Metadata;
        if (!FederationSyntax.SameXid(result.SelectedXid, target, collection: true) ||
            !envelope.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True ||
            !envelope.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "A complete typed collection is required before membership or counts.");
        }
        var map = new SortedDictionary<string, Member>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in entities.EnumerateObject())
        {
            _budget.ChargeWork();
            RegistryId.Parse(member.Name);
            var xid = target + "/" + member.Name;
            if (!ids.Add(member.Name) || member.Value.ValueKind != JsonValueKind.Object ||
                !member.Value.TryGetProperty("xid", out var actual) || actual.ValueKind != JsonValueKind.String ||
                !FederationSyntax.SameXid(actual.GetString()!, xid))
            {
                throw Invalid("A complete collection has duplicate or different member identities.");
            }
            var item = FederationReadResult.FromMetadata(xid, result.Context, member.Value);
            source.Seed(xid, item);
            map.Add(member.Name, new Member(source, item));
        }
        return map;
    }

    private async ValueTask<Slot> SelectAsync(string owner, CancellationToken token)
    {
        if (_owners.TryGetValue(owner, out var cached)) { return cached; }
        foreach (var candidate in _slots)
        {
            var source = await candidate.GetAsync(token).ConfigureAwait(false);
            try
            {
                var found = await source.ReadAsync(new(FederationOperation.Entity, owner), token).ConfigureAwait(false);
                var entity = Entity(found);
                CheckEntityIdentity(entity, PathFor(owner));
                _owners.Add(owner, source);
                return source;
            }
            catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound) { }
        }
        throw NotFound();
    }

    private async ValueTask<Slot> SelectGroupAsync(string target, CancellationToken token)
    {
        foreach (var candidate in _slots)
        {
            var source = await candidate.GetAsync(token).ConfigureAwait(false);
            try { await source.ReadAsync(new(FederationOperation.Entity, target), token).ConfigureAwait(false); return source; }
            catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound) { }
        }
        throw NotFound();
    }

    private async ValueTask EnsureCollectionOwnerAsync(RegistryPath collection, CancellationToken token)
    {
        var path = collection.Kind switch
        {
            RegistryPathKind.GroupCollection => RegistryPath.Parse("/"),
            RegistryPathKind.ResourceCollection => RegistryPath.ForGroup(collection.GroupType!, collection.GroupId!),
            RegistryPathKind.VersionCollection => RegistryPath.ForResource(
                collection.GroupType!, collection.GroupId!, collection.ResourceType!, collection.ResourceId!),
            _ => throw Invalid("A typed collection is required.")
        };
        var target = LogicalPath(path);
        var first = await _slots[0].GetAsync(token).ConfigureAwait(false);
        var source = first.Owner == FederationResolutionOwner.Producer || path.Kind == RegistryPathKind.Registry ? first :
            path.Kind == RegistryPathKind.Group ? await SelectGroupAsync(target, token).ConfigureAwait(false) :
            await SelectAsync(target, token).ConfigureAwait(false);
        var owner = await source.ReadAsync(new(FederationOperation.Entity, target), token).ConfigureAwait(false);
        CheckEntityIdentity(Entity(owner), path);
    }

    private async ValueTask<JsonObject> NormalizeAsync(Slot source, JsonObject entity, RegistryPath path, CancellationToken token,
        bool checkUnit = true)
    {
        _budget.ChargeWork();
        var target = LogicalPath(path);
        CheckEntityIdentity(entity, path);
        RejectAlias(entity);
        var resource = Resource(path);
        IEnumerable<string> collections = path.Kind switch
        {
            RegistryPathKind.Registry => _model.Groups.Keys,
            RegistryPathKind.Group => _model.Groups[path.GroupType!].Resources.Keys,
            RegistryPathKind.Resource => ["versions"],
            _ => []
        };
        foreach (var name in collections)
        {
            entity.Remove(name);
            entity[name + "url"] = Url((target == "/" ? "" : target) + "/" + name);
        }
        entity.Remove("shortself");
        entity["self"] = Url(target, resource?.HasDocument == true && path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version);
        if (path.Kind == RegistryPathKind.Registry)
        {
            if (RequiredString(entity, "specversion") != "1.0-rc4") { throw Invalid("The producer requires Core 1.0-rc4."); }
            entity["registryid"] = _registryId;
            entity["modelsource"] = JsonNode.Parse(_model.Source.RootElement.GetRawText());
            entity["model"] = JsonNode.Parse(_model.EffectiveModel.RootElement.GetRawText());
            entity["capabilities"] = JsonNode.Parse(_capabilities.GetRawText());
        }
        else if (path.Kind == RegistryPathKind.Resource)
        {
            entity.Remove("meta");
            entity["metaurl"] = Url(target + "/meta");
        }
        else if (path.Kind == RegistryPathKind.Meta)
        {
            entity["defaultversionurl"] = Url(Owner(target) + "/versions/" + RequiredString(entity, "defaultversionid"), resource!.HasDocument);
        }
        var definitions = path.Kind switch
        {
            RegistryPathKind.Registry => _model.Attributes,
            RegistryPathKind.Group => _model.Groups[path.GroupType!].Attributes,
            RegistryPathKind.Meta => resource!.MetaAttributes,
            RegistryPathKind.Version => resource!.Attributes,
            RegistryPathKind.Resource => Merge(resource!.Attributes, resource.ResourceAttributes),
            _ => throw Unsupported("The selected route is not an entity.")
        };
        var group = path.GroupType is null ? null : _model.Groups[path.GroupType];
        var versioned = path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version;
        var constrained = versioned && group!.Constraints.Values.Any(c => c.ResourceType == resource!.Plural);
        var groupMetadata = constrained ? await GroupContextAsync(path, token).ConfigureAwait(false) : default;
        RegistryMetadataValidationResult normalized;
        try
        {
            if (constrained) { ChargeGroupConstraintWork(group!, resource!, token); }
            var input = RegistryJson.Parse(entity.ToJsonString(), JsonLimits());
            FederationJson.CountWork(input.RootElement, _budget, token);
            _budget.ChargeWork(definitions.Count);
            normalized = RegistryMetadataValidator.Validate(input,
                definitions, new RegistryMetadataValidationOptions
                {
                    Model = _model,
                    Limits = ModelJsonLimits(),
                    Group = constrained ? group : null,
                    Resource = constrained ? resource : null,
                    GroupMetadata = groupMetadata,
                }, token);
            FederationJson.CountWork(normalized.Metadata.RootElement, _budget, token);
        }
        catch (RegistryException exception) { throw FederationJson.FromCore(exception); }
        await ValidateTargetsAsync(source, path, normalized, token).ConfigureAwait(false);
        var output = JsonNode.Parse(normalized.Metadata.RootElement.GetRawText())!.AsObject();
        if (versioned) { await ValidationEvidenceAsync(source, output, path, token).ConfigureAwait(false); }
        if (versioned && checkUnit && (constrained || MatchingAttributes(resource!, token).Count != 0))
        {
            await VersionsAsync(source, Owner(target), token).ConfigureAwait(false);
            var id = path.VersionId?.Value ?? RequiredString(output, "versionid");
            if (!_modelVersions[(source, Owner(target))].TryGetValue(id, out var member))
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected Version is absent from its own complete Resource.");
            }
            CheckMatchingSet(resource!, [member, output], token);
        }
        return output;
    }

    private void CheckEntityIdentity(JsonObject entity, RegistryPath path)
    {
        if (!FederationSyntax.SameXid(RequiredString(entity, "xid"), LogicalPath(path)))
        {
            throw Invalid("An entity returned a different typed identity.");
        }
        if (path.Kind == RegistryPathKind.Group &&
            RequiredString(entity, _model.Groups[path.GroupType!].Singular + "id") != path.GroupId!.Value ||
            path.ResourceId is not null && RequiredString(entity, Resource(path)!.Singular + "id") != path.ResourceId.Value ||
            path.VersionId is not null && RequiredString(entity, "versionid") != path.VersionId.Value)
        {
            throw Invalid("An entity's raw ID attributes disagree with its typed identity.");
        }
    }

    private RegistryJsonLimits JsonLimits() => new()
    {
        MaxBytes = _budget.Limits.MaxResultBytes,
        MaxDepth = _budget.Limits.MaxJsonDepth,
        MaxNodes = (int)Math.Min(int.MaxValue, _budget.Limits.MaxWork)
    };

    private JsonElement Element(JsonObject value)
    {
        var json = RegistryJson.Parse(value.ToJsonString(), JsonLimits());
        _budget.CheckResultBytes(Encoding.UTF8.GetByteCount(json.RootElement.GetRawText()));
        return json.RootElement;
    }

    private ProducerRegistryResult Result(string xid, JsonElement metadata, FederationDocument? document = null, JsonElement external = default) =>
        new(xid, metadata, document, external, _origins.Values.OrderBy(origin => origin.Name, StringComparer.Ordinal),
            _dependencies.Values.OrderBy(dependency => dependency.Source, StringComparer.Ordinal)
                .ThenBy(dependency => dependency.Path.EscapedPath, StringComparer.Ordinal));

    private static Dictionary<string, RegistryAttributeDefinition> Merge(
        IReadOnlyDictionary<string, RegistryAttributeDefinition> version, IReadOnlyDictionary<string, RegistryAttributeDefinition> resource)
    {
        var merged = new Dictionary<string, RegistryAttributeDefinition>(version, StringComparer.Ordinal);
        foreach (var entry in resource) { merged[entry.Key] = entry.Value; }
        return merged;
    }

    private string Url(string target, bool details = false) =>
        _publicRoot.AbsoluteUri + (target == "/" ? "" : "/" + string.Join('/', target[1..].Split('/')
            .Select(segment => Uri.EscapeDataString(RegistryId.ParseEscaped(segment).Value)))) +
        (details ? "$details" : "");

    private static JsonObject Entity(FederationReadResult value)
    {
        var data = value.Metadata;
        if (data.ValueKind != JsonValueKind.Object) { throw Invalid("The source returned no entity metadata."); }
        if (!data.TryGetProperty("xid", out _) && data.TryGetProperty("entity", out var inner)) { data = inner; }
        if (data.ValueKind != JsonValueKind.Object) { throw Invalid("The entity envelope is malformed."); }
        return JsonNode.Parse(data.GetRawText())!.AsObject();
    }

    private static string RequiredString(JsonObject value, string name) =>
        value[name] is JsonValue item && item.TryGetValue<string>(out var text) && text.Length != 0
            ? text : throw Invalid("Required entity identity or selection metadata is missing.");

    private static void RejectAlias(JsonObject value)
    {
        if (value.ContainsKey("xref") || value["meta"] is JsonObject meta && meta.ContainsKey("xref"))
        {
            throw Unsupported("Cross-reference Resources need an explicit alias projection and are not enabled by this bounded view.");
        }
    }

    private static void CheckDocumentIdentity(string requested, string selected)
    {
        var expected = FederationSyntax.Xid(requested);
        var actual = FederationSyntax.Xid(selected);
        if (actual.Length != 6 || !actual.Take(4).SequenceEqual(expected.Take(4), StringComparer.Ordinal) ||
            expected.Length == 6 && !actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected Document escaped its retained Resource or Version.");
        }
    }

    private static RegistryPath PathFor(string target, bool details = false)
    {
        var parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        return RegistryPath.Parse(target + (details && parts is 4 or 6 ? "$details" : ""));
    }

    private static string LogicalPath(RegistryPath path)
    {
        if (path.Kind == RegistryPathKind.Registry) { return "/"; }
        var result = "/" + path.GroupType;
        if (path.GroupId is not null) { result += "/" + path.GroupId.Value; }
        if (path.ResourceType is not null) { result += "/" + path.ResourceType; }
        if (path.ResourceId is not null) { result += "/" + path.ResourceId.Value; }
        if (path.Kind == RegistryPathKind.Meta) { result += "/meta"; }
        if (path.Kind is RegistryPathKind.VersionCollection or RegistryPathKind.Version) { result += "/versions"; }
        if (path.VersionId is not null) { result += "/" + path.VersionId.Value; }
        return result;
    }

    private static string Owner(string target)
    {
        var path = RegistryPath.Parse(target);
        if (path.ResourceId is null) { throw Invalid("A Resource identity is required for source ownership."); }
        return "/" + path.GroupType + "/" + path.GroupId!.Value + "/" + path.ResourceType + "/" + path.ResourceId.Value;
    }
    private static FederationException Invalid(string message) => new(FederationErrorCode.InvalidPackage, message);
    private static FederationException NotFound() => new(FederationErrorCode.NotFound, "The exact entity is absent from the eligible view.");
    private static FederationException Unsupported(string message) => new(FederationErrorCode.UnsupportedOperation, message);

    private void CheckCaptures(CancellationToken token)
    {
        foreach (var slot in _slots)
        {
            token.ThrowIfCancellationRequested();
            slot.CheckRetainedCapture();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _reading) != 0) { throw new InvalidOperationException("Cancel and await the active read before disposing the view."); }
        if (_disposed) { return; }
        _disposed = true;
        List<Exception>? failures = null;
        foreach (var slot in Enumerable.Reverse(_slots))
        {
            try { await slot.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or FederationException)
            {
                (failures ??= []).Add(exception);
            }
        }
        _owners.Clear();
        _aliases.Clear();
        _aliasModelChecks.Clear();
        _collections.Clear();
        _origins.Clear();
        _dependencies.Clear();
        _groupContexts.Clear();
        _modelVersions.Clear();
        _modelMeta.Clear();
        _matchingAttributes.Clear();
        if (failures is not null) { throw new AggregateException("One or more owned federation sessions could not be disposed.", failures); }
    }

    private sealed record Member(Slot Slot, FederationReadResult Result);

    private sealed class Slot(ProducerRegistryView view, FederationSourceRegistration registration) : IFederationReadSource, IAsyncDisposable
    {
        private FederationSourceLease? _lease;
        private NativeRegistryContext? _context;
        private RegistryModel? _model;
        private bool _captureReady;
        private readonly Dictionary<(FederationOperation, string), FederationReadResult> _cache = [];
        private readonly HashSet<(FederationOperation, string)> _directReads = [];
        internal string Name => registration.Name;
        internal FederationRepresentation Representation => registration.Representation;
        internal ProducerTargetValidator? TargetValidator => registration.ValidateTarget;
        internal FederationResolutionOwner Owner { get; private set; }
        public NativeRegistryContext Context => _context ?? throw new InvalidOperationException("Open the source before using its context.");
        public RegistryModel? Model => _model;
        public JsonElement Capabilities => _lease?.Source.Capabilities ?? throw new InvalidOperationException("Open the source first.");

        internal async ValueTask<Slot> GetAsync(CancellationToken token)
        {
            if (_lease is not null) { Check(); return this; }
            view._budget.ChargeSource();
            _lease = await registration.Open(view._budget, token).ConfigureAwait(false)
                ?? throw Invalid("The source factory returned no owned session.");
            _context = _lease.Source.Context;
            _model = _lease.Source.Model;
            if (_model is null ||
                !JsonNode.DeepEquals(JsonNode.Parse(_model.Source.RootElement.GetRawText()), JsonNode.Parse(view._model.Source.RootElement.GetRawText())) ||
                !JsonNode.DeepEquals(JsonNode.Parse(_model.EffectiveModel.RootElement.GetRawText()), JsonNode.Parse(view._model.EffectiveModel.RootElement.GetRawText())))
            {
                throw Invalid("The source model/import identity is incompatible with the configured view model.");
            }
            Owner = FederationCapabilities.GetResolutionOwner(_lease.Source.Capabilities);
            _captureReady = true;
            return this;
        }

        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default) =>
            ReadAsync(request, requireDirect: false, cancellationToken);

        internal ValueTask<FederationReadResult> ReadPolicyAsync(FederationReadRequest request, CancellationToken cancellationToken) =>
            ReadAsync(request, requireDirect: true, cancellationToken);

        private async ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request,
            bool requireDirect, CancellationToken cancellationToken)
        {
            if (request.Selector is not null) { throw Unsupported("This bounded view does not enable selectors."); }
            await GetAsync(cancellationToken).ConfigureAwait(false);
            Check();
            var key = (request.Operation, request.Target);
            if (_cache.TryGetValue(key, out var existing) && (!requireDirect || _directReads.Contains(key))) { return existing; }
            view._budget.ChargeRequest();
            var result = await _lease!.Source.ReadAsync(
                new(request.Operation, request.Target, representation: registration.Representation), cancellationToken).ConfigureAwait(false);
            Check();
            if (result.Context != Context) { throw Invalid("The read returned a different source context."); }
            view._origins.TryAdd(Name, new(Name, Context, _lease.CredentialStamp));
            view._dependencies.TryAdd((Name, request.Target), new(Name, PathFor(request.Target)));
            if (result.Metadata.ValueKind != JsonValueKind.Undefined)
            {
                var raw = RegistryJson.FromElement(result.Metadata, view.JsonLimits());
                view._budget.ChargeBytes(Encoding.UTF8.GetByteCount(raw.RootElement.GetRawText()));
            }
            if (result.Document is { } document)
            {
                view._budget.CheckObjectBytes(document.Length);
                view._budget.ChargeBytes(document.Length);
            }
            if (result.ExternalDocument.ValueKind != JsonValueKind.Undefined)
            {
                view._budget.ChargeBytes(Encoding.UTF8.GetByteCount(result.ExternalDocument.GetRawText()));
            }
            view._budget.ChargeObject();
            _cache[key] = result;
            _directReads.Add(key);
            return result;
        }

        internal void Seed(string xid, FederationReadResult result)
        {
            if (_cache.ContainsKey((FederationOperation.Entity, xid))) { return; }
            view._budget.ChargeObject();
            var envelope = new JsonObject { ["entity"] = JsonNode.Parse(result.Metadata.GetRawText()) };
            _cache.Add((FederationOperation.Entity, xid),
                FederationReadResult.FromMetadata(xid, result.Context, RegistryJson.Parse(envelope.ToJsonString()).RootElement));
        }

        private void Check()
        {
            if (!_captureReady || _lease!.Source.Context != Context || !ReferenceEquals(_lease.Source.Model, _model) ||
                FederationCapabilities.GetResolutionOwner(_lease.Source.Capabilities) != Owner)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot, "Source, model or ownership changed inside the capture.");
            }
        }

        internal void CheckRetainedCapture()
        {
            if (_lease is not null) { Check(); }
        }

        public async ValueTask DisposeAsync()
        {
            _cache.Clear();
            _directReads.Clear();
            if (_lease is not null) { await _lease.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
