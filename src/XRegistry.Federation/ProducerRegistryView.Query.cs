// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Queries;

namespace XRegistry.Federation;

public sealed partial class ProducerRegistryView
{
    /// <summary>Uses the shared Core evaluator over already shadowed, origin-retaining raw planes, then projects the selection.</summary>
    /// <remarks>The caller owns the shared budget through projection; no query source or backend lease is retained afterwards.</remarks>
    public async ValueTask<ProducerRegistryResult> ReadQueryAsync(RegistryQueryRequest request, ProducerViewOptions options,
        RegistryQueryBudget budget)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(budget);
        if (request.PublicRoot.AbsoluteUri.TrimEnd('/') != _publicRoot.AbsoluteUri.TrimEnd('/'))
        {
            throw new ArgumentException("Query facts and presentation must use the same trusted PublicRoot.", nameof(request));
        }
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException("A producer view permits one active evaluation/projection.");
        }
        try
        {
            Resource(request.Path);
            var presentation = options.Capture(_model, request.Path);
            CheckCaptures(budget.CancellationToken);
            var source = new QuerySource(this, request.Path);
            var selected = await RegistryQuery.EvaluateAsync(_model, source, request, budget).ConfigureAwait(false);
            await budget.RunAsync(source.EnsureOwnerAsync).ConfigureAwait(false);
            var result = await new Presentation(this, presentation, selected).ReadAsync(request.Path, budget.CancellationToken).ConfigureAwait(false);
            budget.Spend(0);
            CheckCaptures(budget.CancellationToken);
            return result;
        }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private async ValueTask<Slot> QueryOwnerAsync(string owner, CancellationToken token)
    {
        var first = await _slots[0].GetAsync(token).ConfigureAwait(false);
        return first.Owner == FederationResolutionOwner.Producer ? first : await SelectAsync(owner, token).ConfigureAwait(false);
    }

    private async ValueTask<RegistryPath[]> QueryChildrenAsync(RegistryPath collection, CancellationToken token)
    {
        var target = LogicalPath(collection);
        SortedDictionary<string, Member> members;
        var first = await _slots[0].GetAsync(token).ConfigureAwait(false);
        if (collection.Kind == RegistryPathKind.VersionCollection)
        {
            var source = await QueryOwnerAsync(Owner(target), token).ConfigureAwait(false);
            var alias = await AliasAsync(source, Owner(target), token).ConfigureAwait(false);
            if (alias?.Dangling == true) { return []; }
            members = await VersionsAsync(source, alias?.Target ?? Owner(target), token).ConfigureAwait(false);
        }
        else if (first.Owner == FederationResolutionOwner.Producer)
        {
            members = Complete(first, target,
                await first.ReadAsync(new(FederationOperation.Collection, target), token).ConfigureAwait(false));
            if (collection.Kind == RegistryPathKind.ResourceCollection)
            {
                foreach (var id in members.Keys) { _owners.TryAdd(target + "/" + id, first); }
            }
        }
        else { members = await CollectionAsync(target, token).ConfigureAwait(false); }
        return members.Keys.Select(id => PathFor(target + "/" + id)).ToArray();
    }

    private sealed class QuerySource(ProducerRegistryView view, RegistryPath targetPath) : IRegistryQuerySource
    {
        private bool _ownerChecked;

        internal async ValueTask EnsureOwnerAsync(CancellationToken token)
        {
            if (_ownerChecked) { return; }
            if (targetPath.Kind is RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection)
            {
                await view.EnsureCollectionOwnerAsync(targetPath, token).ConfigureAwait(false);
            }
            _ownerChecked = true;
        }

        public async ValueTask<RegistryQueryEntity?> ReadEntityAsync(RegistryPath path, bool includeDocument,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
            view.Resource(path);
            if (path.Kind is RegistryPathKind.Model or RegistryPathKind.ModelSource or RegistryPathKind.Capabilities)
            {
                var configuration = await view.ReadBaseAsync(path, cancellationToken).ConfigureAwait(false);
                return new RegistryQueryEntity(RegistryJson.FromElement(configuration.Metadata, view.JsonLimits()));
            }
            var target = LogicalPath(path);
            if (path.Kind is RegistryPathKind.Registry or RegistryPathKind.Group)
            {
                var read = await view.ReadBaseAsync(path, cancellationToken).ConfigureAwait(false);
                var metadata = JsonNode.Parse(read.Metadata.GetRawText())!.AsObject();
                var children = path.Kind == RegistryPathKind.Registry ? view._model.Groups.Keys :
                    view._model.Groups[path.GroupType!].Resources.Keys;
                foreach (var name in children)
                {
                    metadata.Remove(name);
                    metadata.Remove(name + "url");
                    metadata.Remove(name + "count");
                }
                metadata.Remove("self");
                metadata.Remove("shortself");
                if (path.Kind == RegistryPathKind.Registry)
                {
                    metadata.Remove("model");
                    metadata.Remove("modelsource");
                    metadata.Remove("capabilities");
                }
                return new RegistryQueryEntity(RegistryJson.Parse(metadata.ToJsonString(), view.JsonLimits()));
            }
            if (path.Kind is not (RegistryPathKind.Resource or RegistryPathKind.Meta or RegistryPathKind.Version))
            {
                throw new RegistryException(new("invalid_query_source", path.EscapedPath, "A query entity path is required."));
            }
            var source = await view.QueryOwnerAsync(Owner(target), cancellationToken).ConfigureAwait(false);
            var alias = await view.AliasAsync(source, Owner(target), cancellationToken).ConfigureAwait(false);
            if (path.Kind is RegistryPathKind.Resource or RegistryPathKind.Meta)
            {
                JsonObject metadata;
                if (alias is not null)
                {
                    if (path.Kind == RegistryPathKind.Meta)
                    {
                        await view.ValidateAliasMetaAsync(alias, cancellationToken).ConfigureAwait(false);
                    }
                    metadata = alias.Dangling ? view.Dangling(alias, metaOnly: true) :
                        await view.AliasMetaAsync(alias, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var read = await source.ReadAsync(new(FederationOperation.Entity, Owner(target) + "/meta"), cancellationToken).ConfigureAwait(false);
                    metadata = await view.NormalizeAsync(source, Entity(read),
                        PathFor(Owner(target) + "/meta"), cancellationToken).ConfigureAwait(false);
                }
                return new RegistryQueryEntity(RegistryJson.Parse(metadata.ToJsonString(), view.JsonLimits()))
                {
                    IsDanglingCrossReference = alias?.Dangling == true
                };
            }
            if (alias?.Dangling == true) { throw NotFound(); }
            var version = alias is null
                ? await view.NormalizeAsync(source,
                    Entity(await source.ReadAsync(new(FederationOperation.Entity, target), cancellationToken).ConfigureAwait(false)),
                    PathFor(target, true), cancellationToken).ConfigureAwait(false)
                : await view.AliasMetadataAsync(alias, path, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte>? bytes = null;
            var resource = view.Resource(path)!;
            if (includeDocument && version[resource.Singular + "url"] is null)
            {
                if (!resource.HasDocument)
                {
                    throw new RegistryException(new("query_source_incomplete", path.EscapedPath, "This Resource type has no Document."));
                }
                var read = await view.ReadBaseAsync(PathFor(target), cancellationToken).ConfigureAwait(false);
                if (read.Document is { } document)
                {
                    view._budget.CheckObjectBytes(document.Length);
                    var owned = new byte[checked((int)document.Length)];
                    using var stream = document.OpenRead();
                    await stream.ReadExactlyAsync(owned, cancellationToken).ConfigureAwait(false);
                    bytes = owned;
                }
                else if (read.ExternalDocument.ValueKind == JsonValueKind.Object &&
                    read.ExternalDocument.TryGetProperty("kind", out var kind) && kind.GetString() == "external" &&
                    read.ExternalDocument.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
                {
                    version[resource.Singular + "url"] = uri.GetString();
                }
                else { throw new RegistryException(new("query_source_incomplete", path.EscapedPath, "Exact Document bytes or a descriptor are required.")); }
            }
            var unitMeta = await view.ReadUnitMetaAsync(
                source, alias?.Target ?? Owner(target), cancellationToken).ConfigureAwait(false);
            return new RegistryQueryEntity(RegistryJson.Parse(version.ToJsonString(), view.JsonLimits()))
            {
                Document = bytes,
                DefaultVersionId = RequiredString(unitMeta, "defaultversionid")
            };
        }

        public async IAsyncEnumerable<RegistryPath> GetChildrenAsync(RegistryPath collection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
            var paths = await view.QueryChildrenAsync(collection, cancellationToken).ConfigureAwait(false);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return path;
            }
        }
    }
}
