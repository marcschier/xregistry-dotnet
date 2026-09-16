// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private readonly QueryCursorStore _queryCursors;

    private async ValueTask<RegistryResult> ContinueQueryAsync(RegistryOperation operation, RegistryOperationContext context,
        RequestFlags flags, CancellationToken cancellationToken)
    {
        using var budget = new QueryBudget(_options.QueryLimits, _options.TimeProvider, cancellationToken);
        var found = _queryCursors.Find(flags.Cursor!, ServerJson.Key(operation.Path), PublicRoot.AbsoluteUri.TrimEnd('/'),
            QueryBinding.Caller(context.Caller, budget));
        if (found.Cursor.CheckCapabilities)
        {
            using var configuration = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (LoadCapabilities(configuration, cancellationToken).Revision != found.Cursor.CapabilitiesRevision)
            {
                _queryCursors.Invalidate(found.Cursor);
                throw ServerErrors.Create("bad_cursor", operation.Path.EscapedPath, "The enabled capability profile changed; start a new query.");
            }
        }

        foreach (var dependency in found.Cursor.Dependencies)
        {
            var allowed = await budget.AuthorizeAsync(ct => _authorization.AuthorizeAsync(context.Caller, RegistryAccess.Read,
                RegistryPath.Parse(dependency), ct)).ConfigureAwait(false);
            if (!allowed)
            {
                _queryCursors.Invalidate(found.Cursor);
                throw ServerErrors.Create("forbidden", operation.Path.EscapedPath, "Authorization for the retained result set has been revoked.");
            }
        }

        _queryCursors.Check(found.Cursor);
        var result = found.Cursor.Result(found.Page, operation.Path, Limits.Json with { MaxBytes = Math.Min(Limits.MaxResponseBytes, _options.QueryLimits.MaxPageBytes) });
        if (context.PrepareResponseAsync is { } prepare)
        {
            await budget.PrepareAsync(ct => prepare(result, ct)).ConfigureAwait(false);
        }

        budget.Spend();
        _queryCursors.Check(found.Cursor);
        return result;
    }

    /// <summary>Describes a request with query parameters, including a model-independent retained-page continuation.</summary>
    public async ValueTask<RegistryRouteDescription> DescribeAsync(RegistryAction action, RegistryPath path,
        RegistryOperationContext context, IReadOnlyList<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        await AuthorizeRequestAsync(action, path, context, cancellationToken).ConfigureAwait(false);
        if (parameters.Any(static parameter => parameter.Key == "cursor"))
        {
            var flags = RequestFlags.Parse(new(action, path) { Parameters = parameters }, Limits);
            using var budget = new QueryBudget(_options.QueryLimits, _options.TimeProvider, cancellationToken);
            var page = _queryCursors.Find(flags.Cursor!, ServerJson.Key(path), PublicRoot.AbsoluteUri.TrimEnd('/'),
                QueryBinding.Caller(context.Caller, budget));
            return new(path, page.Cursor.ModelRevision, null, [RegistryAction.Read, RegistryAction.Head]);
        }

        return await DescribeCoreAsync(action, path, context, parameters, cancellationToken).ConfigureAwait(false);
    }

    private sealed partial class Request
    {
        private PreparedQueryCursor? _pendingCursor;

        private async ValueTask<RegistryResult> PreparePageAsync(RegistryPath path)
        {
            QueryWork.Spend();
            _readDependencies.Add(ServerJson.Key(path));
            var children = await OrderedChildrenAsync(path, "").ConfigureAwait(false);
            if (_querySelection is null)
            {
                children = children.OrderBy(static entity => LeafId(entity.Key), StringComparer.OrdinalIgnoreCase).ToList();
            }

            if (children.Count > _engine._options.QueryLimits.MaxCursorRecords)
            {
                throw ServerErrors.Create("too_large", path.EscapedPath, "The frozen result set exceeds its record budget.");
            }

            var maximum = Math.Min(_engine.Limits.MaxResponseBytes, _engine._options.QueryLimits.MaxPageBytes);
            var perPage = (int)Math.Min(_flags.Limit ?? ulong.MaxValue, (ulong)_engine._options.QueryLimits.MaxPageRecords);
            var pages = new List<FrozenQueryPage>();
            var current = new JsonObject();
            long currentBytes = 2;
            long bytes = 0;
            var total = 0;
            foreach (var entity in children)
            {
                QueryWork.Spend();
                var logical = LogicalChild(path, entity);
                if (!await IsReadablePageEntityAsync(logical).ConfigureAwait(false))
                {
                    continue;
                }

                var id = LeafId(entity.Key);
                var metadata = await RenderEntityAsync(logical, _flags.Inline, "/" + Pointer(id)).ConfigureAwait(false);
                var own = ServerJson.Own(metadata, _engine.Limits.Json with { MaxBytes = maximum });
                var itemBytes = PropertyNameSize(id) + System.Text.Encoding.UTF8.GetByteCount(own.RootElement.GetRawText());
                if (itemBytes + 2 > maximum)
                {
                    throw ServerErrors.Create("too_large", logical.EscapedPath, "One projected record cannot fit within the page byte budget.");
                }

                if (current.Count >= perPage || currentBytes + itemBytes + (current.Count == 0 ? 0 : 1) > maximum)
                {
                    Finish();
                }

                currentBytes += itemBytes + (current.Count == 0 ? 0 : 1);
                current[id] = metadata;
                total++;
                if (bytes + currentBytes > _engine._options.QueryLimits.MaxCursorBytes)
                {
                    throw ServerErrors.Create("too_large", path.EscapedPath, "The frozen result set exceeds its byte budget.");
                }
            }

            if (current.Count != 0 || pages.Count == 0)
            {
                Finish();
            }

            if (pages.Count == 1)
            {
                return new(RegistryResultKind.Success, path, RegistryJson.Parse(pages[0].Json, _engine.Limits.Json with { MaxBytes = maximum }))
                {
                    ContentType = "application/json; charset=utf-8",
                    Page = new((ulong)total, null, [])
                };
            }

            var candidate = new PreparedQueryCursor(ServerJson.Key(path), _engine.PublicRoot.AbsoluteUri.TrimEnd('/'),
                ModelRevision(_snapshot), QueryBinding.Caller(_context.Caller, QueryWork), QueryBinding.Query(_operation.Parameters, QueryWork),
                pages.ToArray(), _readDependencies.Order(StringComparer.Ordinal).ToArray(), total,
                _engine._options.TimeProvider.GetUtcNow() + _engine._options.QueryLimits.CursorLifetime,
                _engine._options.TimeProvider.GetTimestamp(), _capabilities.Revision,
                _engine._options.AllowCapabilityUpdates || _capabilities.Mutable("capabilities") || _snapshot.Find(CapabilitiesKey) is not null);
            if (candidate.ChargedBytes > _engine._options.QueryLimits.MaxCursorBytes)
            {
                throw ServerErrors.Create("too_large", path.EscapedPath, "The complete cursor metadata exceeds its retention budget.");
            }

            _pendingCursor = candidate;
            return candidate.Result(0, path, _engine.Limits.Json with { MaxBytes = maximum });

            void Finish()
            {
                QueryWork.Spend(current.Count + 1);
                if (pages.Count >= _engine._options.QueryLimits.MaxCursorTokens)
                {
                    throw ServerErrors.Create("too_large", path.EscapedPath, "The result requires too many retained page tokens.");
                }

                var json = ServerJson.Encode(current, maximum);
                _ = RegistryJson.Parse(json, _engine.Limits.Json with { MaxBytes = maximum });
                pages.Add(new(json, current.Count));
                bytes += json.Length;
                current = new JsonObject();
                currentBytes = 2;
            }
        }

        private async ValueTask<bool> IsReadablePageEntityAsync(RegistryPath path)
        {
            if (!await CanReadAsync(path).ConfigureAwait(false))
            {
                return false;
            }

            return path.Kind != RegistryPathKind.Resource || Require(ResourceKey(path)).Attributes["xref"] is not null ||
                await VersionForReadAsync(path).ConfigureAwait(false) is not null;
        }
    }
}
