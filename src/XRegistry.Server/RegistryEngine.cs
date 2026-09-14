using System.Text.Json.Nodes;

namespace XRegistry.Server;

/// <summary>A bounded, transport-independent core registry with atomic persistence and explicit host policies.</summary>
public sealed partial class RegistryEngine : IRegistryEngine
{
    private const string ModelKey = "$modelsource";
    private readonly RegistryEngineOptions _options;
    private readonly IRegistryPersistence _persistence;
    private readonly IRegistryAuthorizationPolicy _authorization;
    private readonly RegistryJson _initialRoot;
    private ModelCache? _modelCache;
    private int _activeOperations;
    private int _activeExternalCalls;

    /// <summary>Creates an engine. The caller retains ownership of persistence and every injected service.</summary>
    public RegistryEngine(RegistryEngineOptions options, IRegistryPersistence persistence, IRegistryAuthorizationPolicy authorization)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(options.PublicRoot);
        ArgumentNullException.ThrowIfNull(options.Model);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        ArgumentNullException.ThrowIfNull(options.Limits);
        ArgumentNullException.ThrowIfNull(options.QueryLimits);
        if (!options.PublicRoot.IsAbsoluteUri || options.PublicRoot.Scheme is not ("https" or "http") ||
            options.PublicRoot.UserInfo.Length != 0 || options.PublicRoot.Query.Length != 0 || options.PublicRoot.Fragment.Length != 0)
        {
            throw new ArgumentException("PublicRoot must be an absolute HTTP(S) URL without credentials, query, or fragment.", nameof(options));
        }

        options.Limits.Validate();
        options.QueryLimits.Validate();
        _ = RegistryId.Parse(options.RegistryId);
        _options = options with { PublicRoot = new Uri(options.PublicRoot.AbsoluteUri.TrimEnd('/')) };
        _persistence = persistence;
        _authorization = authorization;
        _queryCursors = new(options.QueryLimits, options.TimeProvider);
        var root = ServerJson.Object(options.InitialMetadata);
        root["registryid"] = options.RegistryId;
        root["specversion"] = "1.0-rc4";
        root["self"] = PublicRoot.AbsoluteUri.TrimEnd('/');
        root["xid"] = "/";
        root["epoch"] ??= 0;
        var now = ServerJson.Timestamp(options.TimeProvider);
        root["createdat"] ??= now;
        root["modifiedat"] ??= now;
        AddCollectionLinks(root, options.Model.Groups.Keys, "");
        var initial = RegistryMetadataValidator.Validate(ServerJson.Own(root, options.Limits.Json), options.Model.Attributes,
            new() { Model = options.Model, Limits = options.Limits.Json });
        if (initial.Obligations.Count != 0)
        {
            throw new ArgumentException("InitialMetadata has external validation obligations; submit it through an authorized engine operation instead.", nameof(options));
        }

        _initialRoot = initial.Metadata;
    }

    /// <summary>Gets the trusted advertised root, never derived from an incoming request.</summary>
    public Uri PublicRoot => _options.PublicRoot;
    /// <summary>Gets the immutable operation limits used by the HTTP adapter as well.</summary>
    public RegistryLimits Limits => _options.Limits;
    /// <summary>Gets whether the underlying persistence prohibits mutations.</summary>
    public bool IsReadOnly => _persistence.IsReadOnly;

    /// <inheritdoc />
    public async ValueTask<RegistryResult> ExecuteAsync(RegistryOperation operation, RegistryOperationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        if (Interlocked.Increment(ref _activeOperations) > Limits.MaxConcurrentOperations)
        {
            Interlocked.Decrement(ref _activeOperations);
            throw ServerErrors.Create("server_busy", operation.Path.EscapedPath, "The concurrent operation budget is exhausted.");
        }

        try
        {
            return await ExecuteCoreAsync(operation, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
        }
    }

    private async ValueTask<RegistryResult> ExecuteCoreAsync(RegistryOperation operation, RegistryOperationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await AuthorizeRequestAsync(operation.Action, operation.Path, context, cancellationToken).ConfigureAwait(false);
        if (operation.Parameters.Any(static parameter => parameter.Key == "cursor"))
        {
            var queryFlags = RequestFlags.Parse(operation, Limits);
            return await ContinueQueryAsync(operation, context, queryFlags, cancellationToken).ConfigureAwait(false);
        }

        if (operation.Metadata is { } metadata)
        {
            _ = RegistryJson.FromElement(metadata.RootElement, Limits.Json);
        }

        using var snapshot = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (operation.ExpectedModelRevision is { } expected && expected != StateRevision(snapshot))
        {
            throw new RegistryConcurrencyException();
        }

        var compiled = LoadModel(snapshot);
        var model = compiled.Model;
        var capabilities = LoadCapabilities(snapshot, cancellationToken);
        var allowed = Allowed(operation.Path, model);
        if (!allowed.Contains(operation.Action))
        {
            var detailsRequired = operation.Action == RegistryAction.Patch && operation.Path.ResourceType is not null &&
                operation.Path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version &&
                !operation.Path.IsDetails && model.Groups[operation.Path.GroupType!].Resources[operation.Path.ResourceType].HasDocument;
            throw ServerErrors.With(detailsRequired ? "details_required" : "action_not_supported", ServerJson.Key(operation.Path),
                detailsRequired ? "PATCH requires the metadata view for document-bearing entities." : "This action is not supported on this route.",
                ("action", ActionName(operation.Action)));
        }

        CheckAvailability(operation.Path, operation.Action, capabilities);
        if (operation.Action == RegistryAction.Options)
        {
            var result = new RegistryResult(RegistryResultKind.Success, operation.Path) { AllowedActions = EffectiveAllowed(operation.Path, allowed, capabilities) };
            if (context.PrepareResponseAsync is { } prepare)
            {
                await prepare(result, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        using var request = new Request(this, operation, context, snapshot, model, compiled.Source, capabilities, cancellationToken);
        return await request.ExecuteAsync().ConfigureAwait(false);
    }

    /// <summary>Authorizes the requested action and returns the model-aware route shape before body decoding.</summary>
    public async ValueTask<RegistryRouteDescription> DescribeAsync(RegistryAction action, RegistryPath path,
        RegistryOperationContext context, CancellationToken cancellationToken = default)
        => await DescribeCoreAsync(action, path, context, [], cancellationToken).ConfigureAwait(false);

    private async ValueTask<RegistryRouteDescription> DescribeCoreAsync(RegistryAction action, RegistryPath path,
        RegistryOperationContext context, IReadOnlyList<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken)
    {
        await AuthorizeRequestAsync(action, path, context, cancellationToken).ConfigureAwait(false);
        using var snapshot = await _persistence.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var model = LoadModel(snapshot).Model;
        var allowed = Allowed(path, model);
        var capabilities = LoadCapabilities(snapshot, cancellationToken);
        _ = RequestFlags.Parse(new(action, path) { Parameters = parameters }, Limits, capabilities);
        CheckAvailability(path, allowed.Contains(action) ? action : RegistryAction.Read, capabilities);
        return new(path, StateRevision(snapshot), path.ResourceType is null ? null :
            model.Groups[path.GroupType!].Resources[path.ResourceType], EffectiveAllowed(path, allowed, capabilities));
    }

    private static string ModelRevision(IRegistrySnapshot snapshot) =>
        snapshot.Find(ModelKey)?.Metadata.RootElement.GetProperty("revision").GetString() ?? "initial";

    private string StateRevision(IRegistrySnapshot snapshot)
    {
        var capabilities = snapshot.Find(CapabilitiesKey);
        return capabilities is null && !_options.AllowCapabilityUpdates ? ModelRevision(snapshot) :
            ModelRevision(snapshot) + ":caps:" + (capabilities?.Metadata.RootElement.GetProperty("revision").GetString() ?? "initial");
    }

    /// <summary>Checks route-level access before an HTTP adapter consumes a request body. ExecuteAsync rechecks it.</summary>
    public ValueTask AuthorizeRequestAsync(RegistryAction action, RegistryPath path, RegistryOperationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(context);
        var write = action is not (RegistryAction.Read or RegistryAction.Head or RegistryAction.Options);
        if (write && IsReadOnly)
        {
            throw ServerErrors.Create("readonly", ServerJson.Key(path), "The persistence backend is read-only.");
        }

        var access = !write ? RegistryAccess.Read : path.Kind switch
        {
            RegistryPathKind.ModelSource => RegistryAccess.UpdateModel,
            RegistryPathKind.Capabilities => RegistryAccess.UpdateCapabilities,
            _ => action == RegistryAction.Delete ? RegistryAccess.Delete : RegistryAccess.Update
        };
        return AuthorizeAsync(context, path, access, cancellationToken);
    }

    private ModelCache LoadModel(IRegistrySnapshot snapshot)
    {
        var stored = snapshot.Find(ModelKey);
        if (stored is null)
        {
            return new("initial", _options.Model, _options.Model.Source);
        }

        var revision = stored.Metadata.RootElement.GetProperty("revision").GetString()!;
        var cache = Volatile.Read(ref _modelCache);
        if (cache?.Revision == revision)
        {
            return cache;
        }

        if (!stored.Metadata.RootElement.TryGetProperty("compiledsource", out var frozen))
        {
            throw new InvalidDataException("The persisted model has no frozen compiled source.");
        }

        RegistryModel model;
        try
        {
            model = RegistryModel.Compile(RegistryJson.FromElement(frozen, _options.ModelCompilation.JsonLimits),
                _options.ModelCompilation with { Resolver = null, SourceUri = null });
        }
        catch (RegistryException exception)
        {
            throw new InvalidDataException("The persisted compiled model is invalid.", exception);
        }

        cache = new(revision, model, RegistryJson.FromElement(stored.Metadata.RootElement.GetProperty("source"), _options.ModelCompilation.JsonLimits));
        Volatile.Write(ref _modelCache, cache);
        return cache;
    }

    private List<RegistryAction> Allowed(RegistryPath path, RegistryModel model)
    {
        if (path.GroupType is { } groupType && !model.Groups.ContainsKey(groupType))
        {
            throw ServerErrors.With("unknown_group_type", ServerJson.Key(path), "The Group type is not defined by the effective model.",
                ("name", groupType));
        }

        if (path.ResourceType is { } resourceType && !model.Groups[path.GroupType!].Resources.ContainsKey(resourceType))
        {
            throw ServerErrors.With("unknown_resource_type", ServerJson.Key(path), "The Resource type is not defined by the effective model.",
                ("group", path.GroupType!), ("name", resourceType));
        }

        var actions = new List<RegistryAction> { RegistryAction.Read, RegistryAction.Head, RegistryAction.Options };
        if (IsReadOnly)
        {
            return actions;
        }

        switch (path.Kind)
        {
            case RegistryPathKind.Registry:
            case RegistryPathKind.Group:
            case RegistryPathKind.Resource:
                actions.AddRange([RegistryAction.Replace, RegistryAction.Patch, RegistryAction.Post]);
                if (path.Kind != RegistryPathKind.Registry)
                {
                    actions.Add(RegistryAction.Delete);
                }

                break;
            case RegistryPathKind.GroupCollection:
            case RegistryPathKind.ResourceCollection:
            case RegistryPathKind.VersionCollection:
                actions.AddRange([RegistryAction.Patch, RegistryAction.Post, RegistryAction.Delete]);
                break;
            case RegistryPathKind.Meta:
            case RegistryPathKind.Version:
                actions.AddRange([RegistryAction.Replace, RegistryAction.Patch]);
                if (path.Kind == RegistryPathKind.Version)
                {
                    actions.Add(RegistryAction.Delete);
                }

                break;
            case RegistryPathKind.ModelSource:
                actions.Add(RegistryAction.Replace);
                break;
            case RegistryPathKind.Capabilities when _options.AllowCapabilityUpdates:
                actions.AddRange([RegistryAction.Replace, RegistryAction.Patch]);
                break;
        }

        if (path.ResourceType is { } type && model.Groups[path.GroupType!].Resources[type].HasDocument &&
            path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version && !path.IsDetails)
        {
            actions.Remove(RegistryAction.Patch);
        }

        return actions;
    }

    private async ValueTask AuthorizeAsync(RegistryOperationContext context, RegistryPath path, RegistryAccess access,
        CancellationToken cancellationToken)
    {
        var authenticated = context.Caller.Identities.Any(static identity => identity.IsAuthenticated);
        if (!authenticated && (access != RegistryAccess.Read || !_options.AllowAnonymousReads))
        {
            throw ServerErrors.Create("unauthorized", path.EscapedPath, "Authentication is required.");
        }

        if (!await _authorization.AuthorizeAsync(context.Caller, access, path, cancellationToken).ConfigureAwait(false))
        {
            throw ServerErrors.Create("forbidden", path.EscapedPath, "The caller is not authorized for this action.");
        }
    }

    private void AddCollectionLinks(JsonObject metadata, IEnumerable<string> names, string parent)
    {
        foreach (var name in names)
        {
            metadata[name + "url"] = Url(parent + "/" + name);
            metadata[name + "count"] = 0;
        }
    }

    private string Url(string key, bool details = false) =>
        PublicRoot.AbsoluteUri.TrimEnd('/') + (key == "/" ? "" : key) + (details ? "$details" : "");

    private static string ActionName(RegistryAction action) => action switch
    {
        RegistryAction.Read => "GET",
        RegistryAction.Head => "HEAD",
        RegistryAction.Options => "OPTIONS",
        RegistryAction.Replace => "PUT",
        RegistryAction.Patch => "PATCH",
        RegistryAction.Post => "POST",
        RegistryAction.Delete => "DELETE",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private async ValueTask<T> ExternalCallAsync<T>(Func<CancellationToken, ValueTask<T>> action, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _activeExternalCalls) > Limits.MaxConcurrentExternalCalls)
        {
            Interlocked.Decrement(ref _activeExternalCalls);
            throw ServerErrors.Create("server_busy", path, "The concurrent external-work budget is exhausted.");
        }

        var task = Task.Run(async () =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(Limits.ValidationTimeout, _options.TimeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                return await action(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeExternalCalls);
            }
        }, CancellationToken.None);
        try
        {
            return await task.WaitAsync(Limits.ValidationTimeout, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new RegistryException(new("server_busy", path, "The external validation/compilation time budget was exhausted."), exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RegistryException(new("server_busy", path, "The external validation/compilation call did not complete."), exception);
        }
    }

    private sealed record ModelCache(string Revision, RegistryModel Model, RegistryJson Source);
}
