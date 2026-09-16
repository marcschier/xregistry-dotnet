// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Federation;

/// <summary>A bounded, read-only directory-mapping session over an explicitly selected virtual tree.</summary>
/// <remarks>The caller owns the reader. One session is one cumulative operation budget; concurrent reads are rejected.</remarks>
public sealed partial class DirectoryMapping : IAsyncDisposable, IFederationReadSource
{
    private readonly IDocumentTreeReader _reader;
    private readonly NativeRegistryContext _selectedContext;
    private readonly FederationReadBudget _budget;
    private readonly Dictionary<string, TreeReference> _allocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeObject> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _documents = new(StringComparer.Ordinal);
    private readonly TreeObject _root;
    private readonly RegistryModel _model;
    private readonly DocumentTreeModelValidation _modelValidation;
    private int _reading;
    private bool _disposed;

    private DirectoryMapping(IDocumentTreeReader reader, NativeRegistryContext selected,
        FederationReadBudget budget, TreeObject root, RegistryModel model, string rootHash,
        DocumentTreeModelValidation modelValidation)
    {
        _reader = reader;
        _selectedContext = selected;
        _budget = budget;
        _root = root;
        _model = model;
        _modelValidation = modelValidation;
        Context = new(selected.Binding, selected.Source, selected.Revision, selected.IsImmutable, rootHash)
        {
            RootPath = selected.RootPath,
            RequestedRevision = selected.RequestedRevision
        };
        _objects.Add("registry.json", root);
        _allocations.Add("registry.json", new("registry", "/", "registry.json", 0, rootHash));
    }

    /// <summary>Gets the selected source/revision and exact root-byte hash without asserting publisher authenticity.</summary>
    public NativeRegistryContext Context { get; }
    /// <summary>Gets the captured, enabled capabilities of this source view.</summary>
    public JsonElement Capabilities => _root.Entity.GetProperty("capabilities");
    /// <summary>Gets the captured effective model, retaining shared Resource type identities.</summary>
    public RegistryModel Model => _model;

    /// <summary>Captures registry.json once and compiles only its captured, include-resolved model material.</summary>
    public static async ValueTask<DirectoryMapping> OpenAsync(IDocumentTreeReader reader,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        budget ??= new();
        var selected = reader.Context;
        budget.ChargeSource();
        var bytes = await ReadBytesAsync(reader, selected, "registry.json", budget, true, cancellationToken).ConfigureAwait(false);
        var data = FederationJson.Parse(bytes, budget, cancellationToken);
        var root = DocumentTreeFormat.Parse(data, null);
        var source = FederationJson.Required(root.Entity, "modelsource");
        var hasIncludes = ContainsIncludes(source);
        var resolved = data.TryGetProperty("resolvedmodelsource", out var value) ? value : default;
        if (hasIncludes && (resolved.ValueKind != JsonValueKind.Object || !data.TryGetProperty("modelbase", out _)))
        {
            throw FederationJson.Invalid("Captured includes require a resolved model source and explicit original base.");
        }

        RegistryModel model;
        try
        {
            var limits = new RegistryJsonLimits
            {
                MaxBytes = budget.Limits.MaxObjectBytes,
                MaxDepth = budget.Limits.MaxJsonDepth,
                MaxNodes = (int)Math.Max(1, Math.Min(int.MaxValue, budget.Limits.MaxWork - budget.Work)),
            };
            var options = new RegistryModelCompilationOptions
            {
                SourceUri = data.TryGetProperty("modelbase", out var modelBase) ? new Uri(modelBase.GetString()!) : null,
                MaxIncludeDocuments = 0,
                MaxExpandedNodes = limits.MaxNodes,
                JsonLimits = limits,
            };
            model = RegistryModel.CompileCaptured(RegistryJson.FromElement(source, limits),
                RegistryJson.FromElement(resolved.ValueKind == JsonValueKind.Undefined ? source : resolved, limits),
                options, cancellationToken);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }

        if (root.Entity.TryGetProperty("model", out var full) && !EqualJson(full, model.EffectiveModel.RootElement))
        {
            throw FederationJson.Invalid("Captured full model contradicts the effective model.");
        }

        root = DocumentTreeFormat.Parse(data, model);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (selected.RootSha256 is not null && selected.RootSha256 != digest)
        {
            throw new FederationException(FederationErrorCode.IntegrityError, "The root bytes do not match the selected root commitment.");
        }

        var validation = new DocumentTreeModelValidation(model, budget);
        validation.Validate(root, default, cancellationToken);
        return new DirectoryMapping(reader, selected, budget, root, model, digest, validation);
    }

    /// <summary>Reads an exact entity, complete collection, document, model, or capabilities without implicit external acquisition.</summary>
    public async ValueTask<FederationReadResult> ReadAsync(
        FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Enter();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckContext();
            if (request.Representation != FederationRepresentation.DocumentView)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation, "Directory mappings do not invent an HTTP API view.");
            }

            if (request.Operation == FederationOperation.Model)
            {
                var result = new JsonObject { ["modelsource"] = Copy(_root.Entity.GetProperty("modelsource")) };
                foreach (var name in new[] { "model" })
                {
                    if (_root.Entity.TryGetProperty(name, out var value))
                    {
                        result[name] = Copy(value);
                    }
                }

                foreach (var name in new[] { "resolvedmodelsource", "modelbase" })
                {
                    if (_root.Data.TryGetProperty(name, out var value))
                    {
                        result[name] = Copy(value);
                    }
                }

                return Metadata("/", result, cancellationToken);
            }

            if (request.Operation == FederationOperation.Capabilities)
            {
                return Metadata("/", Copy(_root.Entity.GetProperty("capabilities")).AsObject(), cancellationToken);
            }

            if (request.Parts.Length >= 3)
            {
                var resource = DocumentTreeFormat.Resource(_model, request.Parts);
                if (request.Operation == FederationOperation.Document && !resource.HasDocument)
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation, "This Resource type has no domain Document.");
                }
            }
            else if (request.Parts.Length > 0)
            {
                DocumentTreeFormat.Group(_model, request.Parts);
            }

            if (request.Operation == FederationOperation.Document)
            {
                return await ReadDocumentAsync(request.Target, false, cancellationToken).ConfigureAwait(false);
            }

            var item = await LocateAsync(request.Target, cancellationToken,
                request.Operation == FederationOperation.Collection).ConfigureAwait(false);
            if (request.Operation == FederationOperation.Collection)
            {
                var members = await CollectionMembersAsync(item, cancellationToken).ConfigureAwait(false);
                if (request.Parts.Length == 5)
                {
                    var owner = await LocateAsync(DocumentTreeFormat.XidPath(request.Parts.Take(4)), cancellationToken).ConfigureAwait(false);
                    var state = await VersionStateAsync(owner, item, cancellationToken).ConfigureAwait(false);
                    await ValidateVersionSetAsync(state, members, cancellationToken).ConfigureAwait(false);
                }
                if (request.Selector is not null)
                {
                    var matches = new List<TreeObject>();
                    foreach (var member in members)
                    {
                        var effective = await EffectiveLabelsAsync(member, cancellationToken).ConfigureAwait(false);
                        if (request.Selector.Matches(effective))
                        {
                            matches.Add(member);
                        }
                    }

                    if (matches.Count != 1)
                    {
                        throw new FederationException(matches.Count == 0 ? FederationErrorCode.NotFound : FederationErrorCode.Ambiguous,
                            "The complete collection did not have exactly one literal-label match.");
                    }

                    item = matches[0];
                }
                else
                {
                    var entities = new JsonObject();
                    foreach (var member in members)
                    {
                        var id = FederationSyntax.Xid(member.Xid)[^1];
                        entities[id] = await MaterializeAsync(member, "/entities/" + PointerToken(id), 1, cancellationToken).ConfigureAwait(false);
                    }

                    return Metadata(item.Xid, new JsonObject
                    {
                        ["kind"] = "collection",
                        ["xid"] = item.Xid,
                        ["complete"] = true,
                        ["entities"] = entities
                    }, cancellationToken);
                }
            }

            var envelope = new JsonObject
            {
                ["kind"] = item.Kind,
                ["entity"] = await MaterializeAsync(item, "/entity", 1, cancellationToken).ConfigureAwait(false)
            };
            if (item.Kind == "meta" && !item.Entity.TryGetProperty("xref", out _))
            {
                var resourceXid = DocumentTreeFormat.XidPath(FederationSyntax.Xid(item.Xid).Take(4));
                var id = FederationJson.String(item.Entity, "defaultversionid");
                var version = await LocateAsync(resourceXid + "/versions/" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
                envelope["related"] = new JsonObject
                {
                    ["defaultversion"] = await MaterializeAsync(version, "/related/defaultversion", 1, cancellationToken).ConfigureAwait(false)
                };
                envelope["entity"]!["defaultversionurl"] = "#/related/defaultversion";
            }

            if (item.Kind == "registry")
            {
                envelope["snapshot"] = Copy(_root.Data.GetProperty("snapshot"));
                if (_root.Data.TryGetProperty("source", out var source))
                {
                    envelope["source"] = Copy(source);
                }
            }

            CheckContext();
            return Metadata(item.Xid, envelope, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    private async ValueTask<TreeObject> LocateAsync(string target, CancellationToken cancellationToken, bool collectionTarget = false)
    {
        if (target == "/")
        {
            return _root;
        }

        var parts = FederationSyntax.Xid(target, collectionTarget);
        var current = _root;
        for (var index = 0; index < parts.Length; index += 2)
        {
            _budget.CheckDepth(index + 1);
            if (current.Kind == "resource")
            {
                var meta = await GetMetaAsync(current, cancellationToken).ConfigureAwait(false);
                if (parts[index] == "meta")
                {
                    return meta;
                }

                if (meta.Entity.TryGetProperty("xref", out _))
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation,
                        "Alias Version metadata is not available in document view.", "cannot_doc_xref");
                }
            }

            var collectionXid = (current.Xid == "/" ? "" : current.Xid) + "/" + Uri.EscapeDataString(parts[index]);
            var reference = current.Children.FirstOrDefault(child => FederationSyntax.SameXid(child.Xid, collectionXid, true))
                ?? throw FederationJson.Invalid("A modeled collection is absent from its parent record.");
            var collection = await LoadAsync(reference, cancellationToken).ConfigureAwait(false);
            if (current.Kind == "resource")
            {
                await ValidateDefaultAsync(current, collection, cancellationToken).ConfigureAwait(false);
            }

            if (index + 1 == parts.Length)
            {
                return collection;
            }

            var childXid = collectionXid + "/" + Uri.EscapeDataString(parts[index + 1]);
            var child = collection.Children.FirstOrDefault(entry => FederationSyntax.SameXid(entry.Xid, childXid))
                ?? throw new FederationException(FederationErrorCode.NotFound, "The requested exact XID is absent.");
            var parent = current;
            current = await LoadAsync(child, cancellationToken).ConfigureAwait(false);
            if (current.Kind == "version")
            {
                await ValidateSelectedVersionAsync(parent, collection, current, cancellationToken).ConfigureAwait(false);
            }
        }

        return current;
    }

    private async ValueTask<TreeObject> GetMetaAsync(TreeObject resource, CancellationToken cancellationToken)
    {
        var meta = await LoadAsync(resource.Meta ?? throw FederationJson.Invalid("Resource Meta is absent."),
            cancellationToken).ConfigureAwait(false);
        var alias = meta.Entity.TryGetProperty("xref", out _);
        if (resource.Children.Count != (alias ? 0 : 1))
        {
            throw FederationJson.Invalid("Resource Version containment contradicts its alias state.");
        }

        return meta;
    }

    private async ValueTask ValidateDefaultAsync(TreeObject resource, TreeObject versions, CancellationToken cancellationToken)
    {
        await VersionStateAsync(resource, versions, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<List<TreeObject>> CollectionMembersAsync(TreeObject collection, CancellationToken cancellationToken)
    {
        var result = new List<TreeObject>();
        foreach (var child in collection.Children)
        {
            result.Add(await LoadAsync(child, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    private async ValueTask<JsonElement> EffectiveLabelsAsync(TreeObject item, CancellationToken cancellationToken)
    {
        if (item.Kind == "resource")
        {
            var meta = await GetMetaAsync(item, cancellationToken).ConfigureAwait(false);
            if (meta.Entity.TryGetProperty("xref", out var alias))
            {
                try
                {
                    item = await LocateAsync(alias.GetString()!, cancellationToken).ConfigureAwait(false);
                }
                catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound)
                {
                    return default;
                }

                meta = await GetMetaAsync(item, cancellationToken).ConfigureAwait(false);
                if (meta.Entity.TryGetProperty("xref", out _))
                {
                    return default;
                }
            }

            item = await LocateAsync(item.Xid + "/versions/" +
                Uri.EscapeDataString(FederationJson.String(meta.Entity, "defaultversionid")), cancellationToken).ConfigureAwait(false);
        }

        return item.Entity.TryGetProperty("labels", out var labels) ? labels : default;
    }

    private async ValueTask<FederationReadResult> ReadDocumentAsync(
        string target, bool followedAlias, CancellationToken cancellationToken)
    {
        var parts = FederationSyntax.Xid(target);
        var resourceXid = DocumentTreeFormat.XidPath(parts.Take(4));
        var resource = await LocateAsync(resourceXid, cancellationToken).ConfigureAwait(false);
        var meta = await GetMetaAsync(resource, cancellationToken).ConfigureAwait(false);
        if (meta.Entity.TryGetProperty("xref", out var reference))
        {
            if (followedAlias)
            {
                throw new FederationException(FederationErrorCode.NotFound, "A second alias hop has no retrievable Document.");
            }

            var next = reference.GetString()! + (parts.Length == 6 ? "/versions/" + Uri.EscapeDataString(parts[5]) : "");
            return await ReadDocumentAsync(next, true, cancellationToken).ConfigureAwait(false);
        }

        var versionXid = parts.Length == 6 ? target : resourceXid + "/versions/" +
            Uri.EscapeDataString(FederationJson.String(meta.Entity, "defaultversionid"));
        var version = await LocateAsync(versionXid, cancellationToken).ConfigureAwait(false);
        var descriptor = version.Data.GetProperty("document");
        var kind = FederationJson.String(descriptor, "kind");
        if (kind == "none")
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "The Version has no Document by model.");
        }

        if (kind == "external")
        {
            return FederationReadResult.FromExternalDocument(version.Xid, Context, descriptor);
        }

        var document = DocumentTreeFormat.LocalDocument(descriptor, version.Xid);
        var bytes = await LoadDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        string? Field(string name) => descriptor.TryGetProperty(name, out var value) ? value.GetString() : null;
        var contentType = version.Entity.TryGetProperty("contenttype", out var content) ? content.GetString() : null;
        return FederationReadResult.FromDocument(version.Xid, Context, new FederationDocument(bytes,
            contentType ?? "application/octet-stream", Field("base"), Field("origin")));
    }

    private async ValueTask<JsonObject> MaterializeAsync(
        TreeObject item, string pointer, int depth, CancellationToken cancellationToken)
    {
        _budget.CheckDepth(depth);
        _budget.ChargeWork();
        cancellationToken.ThrowIfCancellationRequested();
        var entity = Copy(item.Entity).AsObject();
        entity["self"] = "#" + pointer;
        if (item.Kind == "resource")
        {
            var meta = await GetMetaAsync(item, cancellationToken).ConfigureAwait(false);
            entity["meta"] = await MaterializeAsync(meta, pointer + "/meta", depth + 1, cancellationToken).ConfigureAwait(false);
            entity["metaurl"] = "#" + pointer + "/meta";
            if (meta.Entity.TryGetProperty("xref", out _))
            {
                return entity;
            }

            var defaultId = FederationJson.String(meta.Entity, "defaultversionid");
            entity["meta"]!["defaultversionurl"] = "#" + pointer + "/versions/" + PointerToken(defaultId);
        }

        foreach (var child in item.Children)
        {
            var collection = await LoadAsync(child, cancellationToken).ConfigureAwait(false);
            if (item.Kind == "resource")
            {
                await ValidateDefaultAsync(item, collection, cancellationToken).ConfigureAwait(false);
            }

            var name = FederationSyntax.Xid(collection.Xid, true)[^1];
            var collectionPointer = pointer + "/" + PointerToken(name);
            var map = new JsonObject();
            var members = await CollectionMembersAsync(collection, cancellationToken).ConfigureAwait(false);
            if (item.Kind == "resource")
            {
                var state = await VersionStateAsync(item, collection, cancellationToken).ConfigureAwait(false);
                await ValidateVersionSetAsync(state, members, cancellationToken).ConfigureAwait(false);
            }
            foreach (var value in members)
            {
                var id = FederationSyntax.Xid(value.Xid)[^1];
                map[id] = await MaterializeAsync(value, collectionPointer + "/" + PointerToken(id),
                    depth + 1, cancellationToken).ConfigureAwait(false);
            }

            entity[name] = map;
            entity[name + "url"] = "#" + collectionPointer;
            entity[name + "count"] = map.Count;
        }

        return entity;
    }

    private async ValueTask<TreeObject> LoadAsync(TreeReference reference, CancellationToken cancellationToken)
    {
        Register(reference);
        if (_objects.TryGetValue(reference.Href, out var cached))
        {
            CheckContext();
            return cached;
        }

        var bytes = await ReadBytesAsync(_reader, _selectedContext, reference.Href, _budget, false, cancellationToken).ConfigureAwait(false);
        Verify(reference, bytes);
        var result = DocumentTreeFormat.Parse(FederationJson.Parse(bytes, _budget, cancellationToken), _model);
        if (result.Kind != reference.Kind ||
            !FederationSyntax.SameXid(result.Xid, reference.Xid, reference.Kind == "collection"))
        {
            throw FederationJson.Invalid("A referenced object's kind or XID disagrees with its descriptor.");
        }
        if (result.Kind == "version" && FederationJson.String(result.Data.GetProperty("document"), "kind") == "external" &&
            FederationJson.String(_root.Data.GetProperty("snapshot"), "completeness") == "offline-complete")
        {
            throw FederationJson.Invalid("An offline-complete snapshot contains an external-only Document.");
        }

        JsonElement groupMetadata = default;
        if (result.Kind == "version")
        {
            var group = await LocateAsync(DocumentTreeFormat.XidPath(FederationSyntax.Xid(result.Xid).Take(2)),
                cancellationToken).ConfigureAwait(false);
            groupMetadata = _modelValidation.Metadata(group);
        }
        _modelValidation.Validate(result, groupMetadata, cancellationToken);
        _objects.Add(reference.Href, result);
        return result;
    }

    private async ValueTask<byte[]> LoadDocumentAsync(TreeReference reference, CancellationToken cancellationToken)
    {
        Register(reference);
        if (_documents.TryGetValue(reference.Href, out var cached))
        {
            CheckContext();
            return cached;
        }

        var bytes = await ReadBytesAsync(_reader, _selectedContext, reference.Href, _budget, false, cancellationToken).ConfigureAwait(false);
        Verify(reference, bytes);
        _documents.Add(reference.Href, bytes);
        return bytes;
    }

    private void Register(TreeReference reference)
    {
        _budget.CheckObjectBytes(reference.Size);
        if (_allocations.TryGetValue(reference.Href, out var prior))
        {
            if (prior.Href != reference.Href || prior.Size != reference.Size || prior.Sha256 != reference.Sha256 ||
                prior.Kind != reference.Kind || reference.Kind != "document" &&
                !FederationSyntax.SameXid(prior.Xid, reference.Xid, reference.Kind == "collection"))
            {
                throw FederationJson.Invalid("One storage path has conflicting byte, role, case, or entity allocations.");
            }
        }
        else
        {
            _allocations.Add(reference.Href, reference);
        }
    }

    private static async ValueTask<byte[]> ReadBytesAsync(
        IDocumentTreeReader reader, NativeRegistryContext context, string path, FederationReadBudget budget,
        bool root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.Context != context)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected source context changed.");
        }

        budget.ChargeRequest();
        budget.ChargeObject();
        var bytes = new ArrayBufferWriter<byte>();
        using (var stream = await reader.OpenReadAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (stream is null)
            {
                throw new FederationException(root ? FederationErrorCode.NotFound : FederationErrorCode.InvalidPackage,
                    root ? "The mapping root is absent." : "A referenced mapping object is missing.");
            }

            while (true)
            {
                var next = Math.Min(8192, budget.Limits.MaxObjectBytes - bytes.WrittenCount + 1);
                var count = await stream.ReadAsync(bytes.GetMemory(next)[..next], cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                budget.CheckObjectBytes((long)bytes.WrittenCount + count);
                budget.ChargeBytes(count);
                bytes.Advance(count);
            }
        }

        if (reader.Context != context)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected source changed during a read.");
        }

        return bytes.WrittenSpan.ToArray();
    }

    private static void Verify(TreeReference reference, byte[] bytes)
    {
        if (bytes.LongLength != reference.Size ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FederationException(FederationErrorCode.IntegrityError, "An object's exact size or SHA-256 differs from its descriptor.");
        }
    }

    private FederationReadResult Metadata(string xid, JsonObject value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var owned = RegistryJson.Create(writer => value.WriteTo(writer), new RegistryJsonLimits
            {
                MaxBytes = _budget.Limits.MaxResultBytes,
                MaxDepth = _budget.Limits.MaxJsonDepth,
                MaxNodes = (int)Math.Max(1, Math.Min(int.MaxValue, _budget.Limits.MaxWork - _budget.Work)),
            });
            FederationJson.CountWork(owned.RootElement, _budget, cancellationToken);
            return FederationReadResult.FromMetadata(xid, Context, owned.RootElement);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
    }

    private void CheckContext()
    {
        if (_reader.Context != _selectedContext)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected source context changed.");
        }
    }

    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException("A directory-mapping session permits only one active read.");
        }
    }

    private static JsonNode Copy(JsonElement value) => JsonNode.Parse(value.GetRawText())!;
    private static bool EqualJson(JsonElement left, JsonElement right) => JsonNode.DeepEquals(Copy(left), Copy(right));
    private static string PointerToken(string value) => Uri.EscapeDataString(value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal));

    private static bool ContainsIncludes(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property =>
            property.Name is "$include" or "$includes" || ContainsIncludes(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsIncludes),
        _ => false
    };

    /// <summary>Releases cached bytes without disposing the caller's reader or invalidating detached results.</summary>
    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _reading) != 0)
        {
            throw new InvalidOperationException("Complete or cancel the active read before disposing this session.");
        }

        _disposed = true;
        _objects.Clear();
        _documents.Clear();
        _versionStates.Clear();
        _matchingAttributes.Clear();
        _modelValidation.Clear();
        _allocations.Clear();
        return ValueTask.CompletedTask;
    }
}
