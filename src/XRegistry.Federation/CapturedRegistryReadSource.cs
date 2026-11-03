// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Federation;

/// <summary>Reads a complete captured Core document-view graph and explicitly associated exact documents without live acquisition.</summary>
/// <remarks>
/// The caller verifies publication descriptors and supplies the captured compiled model and authorized snapshot context.
/// This checks containment, model agreement and navigation, not domain closure or every Core metadata constraint.
/// Original bytes are retained independently; reads do not import, update or regenerate Registry state.
/// </remarks>
public sealed class CapturedRegistryReadSource : IFederationReadSource
{
    private sealed record Entity(string Xid, string[] Parts, string Pointer, JsonElement Value);
    private readonly Dictionary<string, Entity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entity[]> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FederationDocument> _documents = new(StringComparer.Ordinal);
    private readonly byte[] _registryBytes;
    private readonly FederationReadBudget _budget;
    private readonly JsonElement _root;

    /// <summary>Captures owned metadata and document associations under one immutable snapshot identity.</summary>
    public CapturedRegistryReadSource(ReadOnlyMemory<byte> registryJson, RegistryModel model,
        NativeRegistryContext context, IReadOnlyDictionary<string, FederationDocument> documents,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.IsImmutable)
        {
            throw new ArgumentException("A captured graph requires an immutable retained snapshot context.", nameof(context));
        }
        Model = model;
        _budget = budget ?? new();
        _budget.ChargeSource();
        _budget.CheckObjectBytes(registryJson.Length);
        _budget.ChargeBytes(registryJson.Length);
        _budget.ChargeObject();
        _registryBytes = registryJson.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(_registryBytes)).ToLowerInvariant();
        if (context.RootSha256 is not null && context.RootSha256 != digest)
        {
            throw new FederationException(FederationErrorCode.IntegrityError, "Captured Registry bytes disagree with the selected root commitment.");
        }
        Context = new(context.Binding, context.Source, context.Revision, true, digest)
        {
            RootPath = context.RootPath, RequestedRevision = context.RequestedRevision, CatalogOrigin = context.CatalogOrigin
        };
        _root = FederationJson.Parse(_registryBytes, _budget, cancellationToken);
        if (FederationJson.String(_root, "specversion") != "1.0-rc4")
        {
            throw new FederationException(FederationErrorCode.UnsupportedVersion, "Unsupported captured Core version.");
        }
        try { _ = RegistryId.Parse(FederationJson.String(_root, "registryid")); }
        catch (RegistryException exception) { throw FederationJson.FromCore(exception); }
        var hasModel = _root.TryGetProperty("model", out var fullModel);
        var hasSource = _root.TryGetProperty("modelsource", out var modelSource);
        if (!hasModel && !hasSource ||
            hasModel && !Equal(fullModel, model.EffectiveModel.RootElement) ||
            hasSource && !Equal(modelSource, model.Source.RootElement))
        {
            throw FederationJson.Invalid("The captured Registry model material disagrees with the supplied compiled model.");
        }
        Capabilities = FederationJson.Required(_root, "capabilities");
        FederationJson.RequireObject(Capabilities);
        var available = FederationJson.Required(Capabilities, "available");
        FederationJson.RequireObject(available);
        foreach (var capability in available.EnumerateObject())
        {
            if (FederationJson.Required(capability.Value, "mutable").ValueKind != JsonValueKind.False)
            {
                throw FederationJson.Invalid("Captured view capabilities must not advertise mutations.");
            }
        }
        _ = FederationCapabilities.GetResolutionOwner(Capabilities);
        IndexEntity(_root, [], "", cancellationToken);
        foreach (var member in _entities.Values.Where(member => member.Parts.Length == 5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _budget.ChargeWork();
            if (member.Value.TryGetProperty("xref", out var xref))
            {
                var target = DocumentTreeFormat.XidPath(FederationSyntax.Xid(xref.GetString()!));
                var targetMeta = GetEntity(target + "/meta");
                if (targetMeta.Value.TryGetProperty("xref", out _))
                {
                    throw FederationJson.Invalid("A captured alias target cannot itself be an alias.");
                }
            }
        }
        foreach (var (xid, document) in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _budget.ChargeWork();
            ArgumentNullException.ThrowIfNull(document);
            var parts = FederationSyntax.Xid(xid);
            var key = DocumentTreeFormat.XidPath(parts);
            if (parts.Length != 6 || !_entities.ContainsKey(key) ||
                !DocumentTreeFormat.Resource(model, parts).HasDocument || !_documents.TryAdd(key, document))
            {
                throw FederationJson.Invalid("A document association must uniquely identify a captured document-bearing exact Version.");
            }
            _budget.CheckObjectBytes(document.Length);
            _budget.ChargeBytes(document.Length);
            _budget.ChargeObject();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public NativeRegistryContext Context { get; }
    /// <inheritdoc />
    public RegistryModel Model { get; }
    /// <inheritdoc />
    public JsonElement Capabilities { get; }

    /// <summary>Returns the original captured Registry representation, not a serialization of a read result.</summary>
    public byte[] GetExactRegistryBytes() => (byte[])_registryBytes.Clone();

    /// <inheritdoc />
    public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        _budget.ChargeWork();
        if (request.Representation != FederationRepresentation.DocumentView)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "A captured document view does not supply a binding API view.");
        }
        var key = DocumentTreeFormat.XidPath(request.Parts);
        if (request.Parts.Length >= 5 && request.Parts[4] == "versions" &&
            GetEntity(DocumentTreeFormat.XidPath(request.Parts[..4]) + "/meta").Value.TryGetProperty("xref", out _))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "Alias-Version document view is not defined.");
        }
        if (request.Operation == FederationOperation.Document)
        {
            return ValueTask.FromResult(ReadDocument(key, request.Parts, cancellationToken));
        }
        JsonObject envelope;
        if (request.Operation == FederationOperation.Capabilities)
        {
            envelope = Copy(Capabilities);
        }
        else if (request.Operation == FederationOperation.Model)
        {
            envelope = new();
            foreach (var name in new[] { "model", "modelsource" })
            {
                if (_root.TryGetProperty(name, out var value)) { envelope[name] = Copy(value); }
            }
        }
        else if (request.Operation == FederationOperation.Collection)
        {
            if (!_collections.TryGetValue(key, out var members)) { throw NotFound(); }
            if (request.Selector is not null)
            {
                var matches = members.Where(member =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _budget.ChargeWork();
                    var value = SelectionMetadata(member);
                    return request.Selector.Matches(value.TryGetProperty("labels", out var labels) ? labels : default);
                }).ToArray();
                if (matches.Length != 1)
                {
                    throw new FederationException(matches.Length == 0 ? FederationErrorCode.NotFound : FederationErrorCode.Ambiguous,
                        "The captured collection does not have exactly one literal-label match.");
                }
                return ValueTask.FromResult(EntityResult(matches[0], cancellationToken));
            }
            var entities = new JsonObject();
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _budget.ChargeWork();
                entities[member.Parts[^1]] = Materialize(member,
                    "/entities/" + DocumentViewNavigation.PointerToken(member.Parts[^1]), cancellationToken);
            }
            envelope = new() { ["kind"] = "collection", ["xid"] = key, ["complete"] = true, ["entities"] = entities };
        }
        else
        {
            return ValueTask.FromResult(EntityResult(GetEntity(key), cancellationToken));
        }
        return ValueTask.FromResult(Metadata(key, envelope, cancellationToken));
    }

    private void IndexEntity(JsonElement value, string[] parts, string pointer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _budget.ChargeWork();
        _budget.CheckDepth(parts.Length);
        FederationJson.RequireObject(value);
        var xid = DocumentTreeFormat.XidPath(parts);
        if (!FederationSyntax.SameXid(FederationJson.String(value, "xid"), xid) || value.TryGetProperty("shortself", out _))
        {
            throw FederationJson.Invalid("Captured entity identity disagrees with its containment or contains API-only shortself.");
        }
        CheckNavigation(value, "self", pointer, token);
        var item = new Entity(xid, parts, pointer, value);
        if (!_entities.TryAdd(xid, item)) { throw FederationJson.Invalid("Duplicate captured entity identity."); }
        var idName = parts.Length switch
        {
            2 => Model.Groups[parts[0]].Singular + "id",
            >= 4 => DocumentTreeFormat.Resource(Model, parts).Singular + "id",
            _ => "registryid"
        };
        if (parts.Length > 0 && value.TryGetProperty(idName, out _) &&
                FederationJson.String(value, idName) != parts[parts.Length == 2 ? 1 : 3] ||
            parts.Length == 6 && value.TryGetProperty("versionid", out _) &&
                FederationJson.String(value, "versionid") != parts[5])
        {
            throw FederationJson.Invalid("Captured entity ID attributes disagree with its containment.");
        }
        if (parts.Length == 4)
        {
            var meta = FederationJson.Required(value, "meta");
            IndexEntity(meta, [.. parts, "meta"], pointer + "/meta", token);
            CheckNavigation(value, "metaurl", pointer + "/meta", token);
            if (meta.TryGetProperty("xref", out _))
            {
                var target = FederationSyntax.Xid(FederationJson.String(meta, "xref"));
                if (target.Length != 4 || target[0] != parts[0] || target[2] != parts[2])
                {
                    throw FederationJson.Invalid("A captured alias must identify a Resource of the same model type.");
                }
                if (value.TryGetProperty("versions", out var aliases))
                {
                    FederationJson.RequireObject(aliases);
                    if (aliases.EnumerateObject().Any())
                    {
                        throw FederationJson.Invalid("A captured alias must not supply its own Versions.");
                    }
                }
                return;
            }
        }
        IEnumerable<string> names = parts.Length switch
        {
            0 => Model.Groups.Keys,
            2 => Model.Groups[parts[0]].Resources.Keys,
            4 => ["versions"],
            _ => []
        };
        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();
            var map = FederationJson.Required(value, name);
            FederationJson.RequireObject(map);
            var members = map.EnumerateObject().ToArray();
            if (value.TryGetProperty(name + "count", out var countValue) &&
                (countValue.ValueKind != JsonValueKind.Number || !countValue.TryGetUInt64(out var count) || count != (ulong)members.Length))
            {
                throw FederationJson.Invalid("A captured collection is incomplete or disagrees with its count.");
            }
            var collectionPointer = pointer + "/" + DocumentViewNavigation.PointerToken(name);
            if (value.TryGetProperty(name + "url", out _)) { CheckNavigation(value, name + "url", collectionPointer, token); }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexed = new List<Entity>();
            foreach (var member in members)
            {
                if (!FederationSyntax.Id(member.Name) || !ids.Add(member.Name))
                {
                    throw FederationJson.Invalid("Captured collection IDs are invalid or collide under Core uniqueness rules.");
                }
                string[] childParts = [.. parts, name, member.Name];
                IndexEntity(member.Value, childParts,
                    collectionPointer + "/" + DocumentViewNavigation.PointerToken(member.Name), token);
                indexed.Add(GetEntity(DocumentTreeFormat.XidPath(childParts)));
            }
            _collections.Add(DocumentTreeFormat.XidPath([.. parts, name]), indexed.ToArray());
        }
        if (parts.Length == 4)
        {
            var meta = GetEntity(xid + "/meta").Value;
            var defaultId = FederationJson.String(meta, "defaultversionid");
            if (!_entities.ContainsKey(xid + "/versions/" + Uri.EscapeDataString(defaultId)))
            {
                throw FederationJson.Invalid("Captured default-Version metadata is absent from the complete Version set.");
            }
            CheckNavigation(meta, "defaultversionurl", pointer + "/versions/" + DocumentViewNavigation.PointerToken(defaultId), token);
        }
    }

    private FederationReadResult ReadDocument(string key, string[] parts, CancellationToken token)
    {
        if (!DocumentTreeFormat.Resource(Model, parts).HasDocument)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "This captured Resource type has no document.");
        }
        if (parts.Length == 4)
        {
            var meta = GetEntity(key + "/meta").Value;
            if (meta.TryGetProperty("xref", out var xref))
            {
                var target = FederationSyntax.Xid(xref.GetString()!);
                if (target.Length != 4) { throw FederationJson.Invalid("A captured alias must identify a Resource."); }
                key = DocumentTreeFormat.XidPath(target);
                meta = GetEntity(key + "/meta").Value;
                if (meta.TryGetProperty("xref", out _))
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation, "A captured alias target cannot itself be an alias.");
                }
            }
            key += "/versions/" + Uri.EscapeDataString(FederationJson.String(meta, "defaultversionid"));
        }
        _ = GetEntity(key);
        if (!_documents.TryGetValue(key, out var document)) { throw NotFound(); }
        _budget.CheckResultBytes(document.Length);
        token.ThrowIfCancellationRequested();
        return FederationReadResult.FromDocument(key, Context, document);
    }

    private JsonElement SelectionMetadata(Entity member)
    {
        if (member.Parts.Length != 4) { return member.Value; }
        var owner = member.Xid;
        var meta = GetEntity(owner + "/meta").Value;
        if (meta.TryGetProperty("xref", out var xref))
        {
            owner = DocumentTreeFormat.XidPath(FederationSyntax.Xid(xref.GetString()!));
            meta = GetEntity(owner + "/meta").Value;
        }
        return GetEntity(owner + "/versions/" + Uri.EscapeDataString(FederationJson.String(meta, "defaultversionid"))).Value;
    }

    private FederationReadResult EntityResult(Entity member, CancellationToken token)
    {
        var entity = Copy(member.Value);
        JsonObject? related = null;
        if (member.Parts.Length == 5 && entity.ContainsKey("defaultversionurl"))
        {
            var owner = DocumentTreeFormat.XidPath(member.Parts[..4]);
            var version = GetEntity(owner + "/versions/" + Uri.EscapeDataString(FederationJson.String(member.Value, "defaultversionid")));
            related = new() { ["defaultversion"] = Materialize(version, "/related/defaultversion", token) };
            entity.Remove("defaultversionurl");
        }
        DocumentViewNavigation.Rebase(entity, member.Parts, Model, "/entity", member.Pointer, fragmentBudget: _budget,
            cancellationToken: token);
        if (related is not null) { entity["defaultversionurl"] = "#/related/defaultversion"; }
        var envelope = new JsonObject { ["kind"] = Kind(member.Parts.Length), ["entity"] = entity };
        if (related is not null) { envelope["related"] = related; }
        return Metadata(member.Xid, envelope, token);
    }

    private JsonObject Materialize(Entity member, string pointer, CancellationToken token)
    {
        var copy = Copy(member.Value);
        DocumentViewNavigation.Rebase(copy, member.Parts, Model, pointer, member.Pointer, fragmentBudget: _budget,
            cancellationToken: token);
        return copy;
    }

    private FederationReadResult Metadata(string xid, JsonObject envelope, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        _budget.CheckResultBytes(bytes.Length);
        JsonElement data;
        try
        {
            data = RegistryJson.Parse(bytes, new RegistryJsonLimits
            {
                MaxBytes = _budget.Limits.MaxResultBytes, MaxDepth = _budget.Limits.MaxJsonDepth,
                MaxNodes = (int)Math.Min(int.MaxValue, _budget.Limits.MaxWork)
            }).RootElement;
        }
        catch (RegistryException exception) { throw FederationJson.FromCore(exception); }
        FederationJson.CountWork(data, _budget, token);
        token.ThrowIfCancellationRequested();
        return FederationReadResult.FromMetadata(xid, Context, data);
    }

    private Entity GetEntity(string xid) => _entities.TryGetValue(xid, out var value) ? value : throw NotFound();
    private static FederationException NotFound() => new(FederationErrorCode.NotFound, "The captured view has no selected metadata or associated document.");
    private JsonObject Copy(JsonElement value) => JsonNode.Parse(value.GetRawText(),
        documentOptions: new() { MaxDepth = _budget.Limits.MaxJsonDepth })!.AsObject();
    private bool Equal(JsonElement left, JsonElement right) =>
        left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object && JsonNode.DeepEquals(Copy(left), Copy(right));
    private void CheckNavigation(JsonElement value, string name, string pointer, CancellationToken token)
    {
        if (DocumentViewNavigation.DecodeFragment(FederationJson.String(value, name), _budget, token) != pointer)
        {
            throw FederationJson.Invalid("Captured navigation must identify its included target without retrieval.");
        }
    }
    private static string Kind(int length) => length switch { 0 => "registry", 2 => "group", 4 => "resource", 5 => "meta", _ => "version" };
}
