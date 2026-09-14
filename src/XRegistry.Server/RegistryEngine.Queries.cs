using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using XRegistry.Queries;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private QueryBudget? _queryBudget;
        private RegistryQuerySelection? _querySelection;
        private readonly HashSet<string> _readDependencies = new(StringComparer.Ordinal);
        private bool _readingQuerySource;
        private bool DocumentView => _flags.Doc && !_readingQuerySource;
        private QueryBudget QueryWork => _queryBudget ??= new(_engine._options.QueryLimits,
            _engine._options.TimeProvider, _ct, _engine.Limits);

        private async ValueTask<bool> CanReadAsync(RegistryPath path)
        {
            var permitted = _queryBudget is null
                ? await _engine._authorization.AuthorizeAsync(_context.Caller, RegistryAccess.Read, path, _ct).ConfigureAwait(false)
                : await _queryBudget.AuthorizeAsync(ct => _engine._authorization.AuthorizeAsync(_context.Caller, RegistryAccess.Read, path, ct)).ConfigureAwait(false);
            if (permitted && _queryBudget is not null)
            {
                _readDependencies.Add(ServerJson.Key(path));
                if (_readDependencies.Count > _engine._options.QueryLimits.MaxAuthorizationPaths)
                {
                    throw ServerErrors.Create("too_large", path.EscapedPath, "The query authorization-dependency budget is exhausted.");
                }
            }

            return permitted;
        }

        private async ValueTask RequireReadAsync(RegistryPath path)
        {
            if (!await CanReadAsync(path).ConfigureAwait(false))
            {
                throw ServerErrors.Create("forbidden", path.EscapedPath, "The caller is not authorized to read the requested projection.");
            }
        }

        private async ValueTask InitializeQueryAsync(RegistryPath path)
        {
            var paging = _capabilities.Pagination && IsCollection(path.Kind) &&
                _operation.Action is RegistryAction.Read or RegistryAction.Head;
            if (_flags.Filters.Count == 0 && _flags.Sort is null && !paging)
            {
                return;
            }

            QueryWork.Spend();
            _readDependencies.Add(ServerJson.Key(path));
            _readingQuerySource = true;
            try
            {
                _querySelection = await RegistryQuery.EvaluateAsync(_model, new QuerySource(this),
                    new(path, _engine.PublicRoot)
                    {
                        Filters = _flags.Filters,
                        Sort = _flags.Sort,
                        DefaultSortById = paging
                    }, QueryWork.Shared).ConfigureAwait(false);
            }
            catch (RegistryException exception) when (exception.Diagnostic.Code is "bad_filter" or "bad_sort")
            {
                var value = exception.Diagnostic.Code == "bad_sort" ? _flags.Sort ?? "" : string.Join(" | ", _flags.Filters);
                throw ServerErrors.Wrap(exception, exception.Diagnostic.Code, ServerJson.Key(path), ("value", value));
            }
            finally
            {
                _readingQuerySource = false;
            }
        }

        private async ValueTask<List<Entity>> VisibleChildrenAsync(string key)
        {
            var children = await AuthorizedChildrenAsync(key).ConfigureAwait(false);
            return _querySelection is null ? children :
                children.Where(entity => _querySelection.Includes(LogicalChild(RegistryPath.Parse(key), entity))).ToList();
        }

        private async ValueTask<List<Entity>> OrderedChildrenAsync(RegistryPath path, string pointer)
        {
            var children = await VisibleChildrenAsync(ServerJson.Key(path)).ConfigureAwait(false);
            if (pointer.Length != 0 || _querySelection is null ||
                _querySelection.Target.EscapedPath != ServerJson.Key(path))
            {
                return children;
            }

            var byPath = children.ToDictionary(entity => ServerJson.Key(LogicalChild(path, entity)), StringComparer.Ordinal);
            var ordered = new List<Entity>();
            foreach (var selected in _querySelection.RootPaths)
            {
                QueryWork.Spend();
                if (byPath.TryGetValue(selected.EscapedPath, out var entity))
                {
                    ordered.Add(entity);
                }
            }

            return ordered;
        }

        private string CollectionUrl(string collectionKey, int count) => _querySelection is null
            ? _engine.Url(collectionKey) : _querySelection.CollectionUrl(RegistryPath.Parse(collectionKey), count);

        private static RegistryPath LogicalChild(RegistryPath collection, Entity entity) =>
            RegistryPath.Parse(ServerJson.Key(collection) + "/" + entity.Key[(entity.Key.LastIndexOf('/') + 1)..]);
        private static string LeafId(string key) => Uri.UnescapeDataString(key[(key.LastIndexOf('/') + 1)..]);
        private static bool IsCollection(RegistryPathKind kind) =>
            kind is RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection;

        private sealed class QuerySource(Request request) : IRegistryQuerySource
        {
            public async ValueTask<RegistryQueryEntity?> ReadEntityAsync(RegistryPath path, bool includeDocument,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await request.CanReadAsync(path).ConfigureAwait(false))
                {
                    return null;
                }

                if (path.Kind is RegistryPathKind.Model or RegistryPathKind.ModelSource or RegistryPathKind.Capabilities)
                {
                    CheckAvailability(path, RegistryAction.Read, request._capabilities);
                    return new(path.Kind == RegistryPathKind.Model ? request._model.EffectiveModel :
                        path.Kind == RegistryPathKind.ModelSource ? request._modelSource : request._capabilities.Metadata);
                }

                Entity entity;
                JsonObject attributes;
                var dangling = false;
                string? defaultVersionId = null;
                if (path.ResourceId is not null)
                {
                    var source = request.Require(ResourceKey(path));
                    var resource = source.Attributes["xref"] is null ? source :
                        await request.ResolveCrossReferenceAsync(source).ConfigureAwait(false);
                    if (resource is null)
                    {
                        if (path.Kind == RegistryPathKind.Version)
                        {
                            return null;
                        }

                        entity = source;
                        dangling = true;
                    }
                    else if (path.Kind == RegistryPathKind.Version)
                    {
                        defaultVersionId = ServerJson.Text(resource.Attributes, "defaultversionid") ??
                            throw new InvalidDataException("The selected Resource is missing its default Version identity.");
                        var versionKey = resource.Key + "/versions/" + Uri.EscapeDataString(path.VersionId!.Value);
                        entity = request.Find(versionKey) ??
                            throw ServerErrors.Create("not_found", path.EscapedPath, "The selected Version does not exist.");
                        if (!await request.CanReadAsync(RegistryPath.Parse(versionKey)).ConfigureAwait(false))
                        {
                            return null;
                        }
                    }
                    else
                    {
                        entity = resource;
                    }

                    attributes = (JsonObject)entity.Attributes.DeepClone();
                    if (path.Kind is RegistryPathKind.Resource or RegistryPathKind.Meta && source.Attributes["xref"] is { } xref)
                    {
                        attributes["xref"] = xref.DeepClone();
                    }
                }
                else
                {
                    entity = request.Require(ServerJson.Key(path));
                    attributes = entity.Attributes;
                }

                ReadOnlyMemory<byte>? document = null;
                if (includeDocument && path.Kind == RegistryPathKind.Version &&
                    attributes[request.ResourceDefinition(path).Singular + "url"] is null)
                {
                    document = await request.ReadDocumentAsync(entity, cancellationToken).ConfigureAwait(false);
                }

                return new(ServerJson.Own(attributes, request._engine.Limits.Json))
                {
                    Document = document,
                    DefaultVersionId = defaultVersionId,
                    IsDanglingCrossReference = dangling,
                    ShortSelf = request._capabilities.ShortSelf ? request.ShortSelf(path) : null
                };
            }

            public async IAsyncEnumerable<RegistryPath> GetChildrenAsync(RegistryPath collection,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (var entity in await request.AuthorizedChildrenAsync(ServerJson.Key(collection)).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return LogicalChild(collection, entity);
                }
            }
        }
    }
}
