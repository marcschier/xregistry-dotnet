using System.Collections.ObjectModel;
using System.Text.Json;

namespace XRegistry.Federation;

/// <summary>An already selected and caller-authorized source view, not a catalog advertisement awaiting execution.</summary>
public interface IFederationReadSource
{
    /// <summary>Gets fixed source/revision/access context for the operation.</summary>
    NativeRegistryContext Context { get; }
    /// <summary>Gets owned, enabled capabilities of this view, including resolution ownership.</summary>
    JsonElement Capabilities { get; }
    /// <summary>Gets the effective source model when needed to verify local alias type identity.</summary>
    RegistryModel? Model => null;
    /// <summary>Reads under that context; collection results use the complete directory-mapping envelope.</summary>
    ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default);
}

/// <summary>An origin-retaining selection. Collection members remain separate envelopes until the frontend materializes its view.</summary>
public sealed class ResourceFederationResult
{
    internal ResourceFederationResult(string target, FederationReadResult? selected, IEnumerable<FederationReadResult>? members = null)
    {
        Target = target;
        Selected = selected;
        Members = Array.AsReadOnly(members?.ToArray() ?? []);
    }

    /// <summary>The consumer-visible requested typed path.</summary>
    public string Target { get; }
    /// <summary>The selected-origin result for a singular read or label selection.</summary>
    public FederationReadResult? Selected { get; }
    /// <summary>The complete visible Resource set for an unselected collection, each retaining its own origin.</summary>
    public ReadOnlyCollection<FederationReadResult> Members { get; }
}

/// <summary>Local-first Resource-level federation over an explicit eligible source policy.</summary>
/// <remarks>Catalog discovery, type projection and frontend metadata materialization remain explicit host responsibilities.</remarks>
public sealed class ResourceFederationResolver
{
    private static readonly AsyncLocal<int> Depth = new();
    private readonly IFederationReadSource _local;
    private readonly IReadOnlyList<IFederationReadSource> _sources;
    private readonly Func<IFederationReadSource, string, bool> _compatible;
    private readonly bool _ordered;
    private readonly FederationReadBudget _budget;

    /// <summary>Creates a resolver with explicit source order and consumer-model compatibility policy.</summary>
    /// <param name="local">The local Registry view.</param>
    /// <param name="sources">Eligible already selected source contexts, not arbitrary catalog enumeration.</param>
    /// <param name="compatibleWithView">An explicit model/projection check before a nonlocal result is accepted.</param>
    /// <param name="sourcesAreOrdered">False requires ambiguity detection rather than choosing a competing source by enumeration order.</param>
    /// <param name="budget">Cumulative source/composition limits.</param>
    public ResourceFederationResolver(IFederationReadSource local, IReadOnlyList<IFederationReadSource> sources,
        Func<IFederationReadSource, string, bool> compatibleWithView,
        bool sourcesAreOrdered = true, FederationReadBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(compatibleWithView);
        _local = local;
        _sources = Array.AsReadOnly(sources.ToArray());
        _compatible = compatibleWithView;
        _ordered = sourcesAreOrdered;
        _budget = budget ?? new();
    }

    /// <summary>Resolves Resource/Meta/Version reads and Resource collections without filling missing selected-origin Versions.</summary>
    public async ValueTask<ResourceFederationResult> ReadAsync(
        FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var depth = Depth.Value;
        _budget.CheckHops(depth + 1);
        Depth.Value = depth + 1;
        try
        {
            var captures = new Dictionary<IFederationReadSource, SourceCapture>(ReferenceEqualityComparer.Instance);
            if (Capture(_local, captures).Owner == FederationResolutionOwner.Producer)
            {
                return new(request.Target, await ReadSourceAsync(_local, request, captures, cancellationToken).ConfigureAwait(false));
            }

            var contexts = new HashSet<NativeRegistryContext> { _local.Context };
            foreach (var source in _sources)
            {
                if (source is null || !contexts.Add(source.Context))
                {
                    throw FederationJson.Invalid("An explicit source policy has null or duplicate contexts.");
                }
                _budget.ChargeSource();
            }

            if (request.Operation == FederationOperation.Collection && request.Parts.Length == 3)
            {
                return await CollectionAsync(request, captures, cancellationToken).ConfigureAwait(false);
            }

            if (request.Parts.Length < 4)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "This resolver composes Resource units; Registry/Group view materialization is a separate host operation.");
            }

            var owner = "/" + string.Join('/', request.Parts.Take(4));
            var probe = new FederationReadRequest(FederationOperation.Entity, owner, representation: request.Representation);
            var local = await ProbeAsync(_local, probe, captures, cancellationToken).ConfigureAwait(false);
            if (local is not null)
            {
                return new(request.Target, request.Operation == FederationOperation.Entity && request.Target == owner
                    ? local : await ReadSourceAsync(_local, request, captures, cancellationToken).ConfigureAwait(false));
            }

            IFederationReadSource? selectedSource = null;
            FederationReadResult? selectedResource = null;
            foreach (var source in _sources)
            {
                var found = await ProbeAsync(source, probe, captures, cancellationToken).ConfigureAwait(false);
                if (found is null) { continue; }
                Compatible(source, owner, captures);
                if (selectedSource is not null)
                {
                    throw new FederationException(FederationErrorCode.Ambiguous, "Unordered source contexts compete for the same Resource.");
                }
                selectedSource = source;
                selectedResource = found;
                if (_ordered) { break; }
            }

            CheckCaptures(captures);
            if (selectedSource is null)
            {
                throw new FederationException(FederationErrorCode.NotFound, "The Resource is absent from every eligible context.");
            }

            var selected = request.Operation == FederationOperation.Entity && request.Target == owner
                ? selectedResource : await ReadSourceAsync(selectedSource, request, captures, cancellationToken).ConfigureAwait(false);
            CheckCaptures(captures);
            return new(request.Target, selected);
        }
        finally
        {
            Depth.Value = depth;
        }
    }

    private async ValueTask<ResourceFederationResult> CollectionAsync(
        FederationReadRequest request, Dictionary<IFederationReadSource, SourceCapture> captures, CancellationToken cancellationToken)
    {
        var visible = new Dictionary<string, (IFederationReadSource Source, FederationReadResult Result)>(StringComparer.Ordinal);
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { _local }.Concat(_sources))
        {
            var unfiltered = new FederationReadRequest(FederationOperation.Collection, request.Target, representation: request.Representation);
            var response = await ReadSourceAsync(source, unfiltered, captures, cancellationToken).ConfigureAwait(false);
            var envelope = response.Metadata;
            if (FederationJson.String(envelope, "kind") != "collection" ||
                !FederationSyntax.SameXid(FederationJson.String(envelope, "xid"), request.Target, true))
            {
                throw FederationJson.Invalid("A source returned a collection for another typed path.");
            }
            if (!envelope.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True ||
                !envelope.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded, "A source did not provide a complete collection.");
            }

            var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in entities.EnumerateObject())
            {
                _budget.ChargeWork();
                if (!sourceIds.Add(property.Name))
                {
                    throw FederationJson.Invalid("A source collection contains duplicate or case-colliding IDs.");
                }
                var xid = request.Target + "/" + property.Name;
                FederationSyntax.Xid(xid);
                var entity = property.Value;
                if (!FederationSyntax.SameXid(FederationJson.String(entity, "xid"), xid))
                {
                    throw FederationJson.Invalid("A collection member has a different typed identity.");
                }
                if (spellings.TryGetValue(xid, out var spelling) && spelling != xid)
                {
                    throw new FederationException(FederationErrorCode.Ambiguous, "The visible collection contains case-colliding Resource identities.");
                }
                spellings[xid] = xid;
                if (visible.TryGetValue(xid, out var prior))
                {
                    if (!_ordered && !ReferenceEquals(prior.Source, _local))
                    {
                        throw new FederationException(FederationErrorCode.Ambiguous, "Unordered sources have competing visible Resources.");
                    }
                    continue;
                }

                if (!ReferenceEquals(source, _local)) { Compatible(source, xid, captures); }
                visible.Add(xid, (source, FederationReadResult.FromMetadata(xid, response.Context, entity)));
            }
        }

        if (request.Selector is null)
        {
            CheckCaptures(captures);
            return new(request.Target, null, visible.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => entry.Value.Result));
        }

        var matches = new List<(IFederationReadSource Source, FederationReadResult Result)>();
        foreach (var member in visible.Values)
        {
            var metadata = member.Result.Metadata;
            JsonElement labels = default;
            if (metadata.TryGetProperty("labels", out var direct))
            {
                labels = direct;
            }
            else
            {
                var meta = await ReadSourceAsync(member.Source,
                    new(FederationOperation.Entity, member.Result.SelectedXid + "/meta"), captures, cancellationToken).ConfigureAwait(false);
                var entity = Entity(meta.Metadata);
                var ownerXid = member.Result.SelectedXid;
                if (entity.TryGetProperty("xref", out var alias))
                {
                    var model = member.Source.Model ?? throw new FederationException(FederationErrorCode.UnsupportedOperation,
                        "Alias label selection requires the source's effective model for type identity.");
                    var target = alias.GetString() ?? throw FederationJson.Invalid("An alias needs a local Resource XID.");
                    var originalParts = FederationSyntax.Xid(ownerXid);
                    var targetParts = FederationSyntax.Xid(target);
                    if (targetParts.Length != 4 || !ReferenceEquals(
                        DocumentTreeFormat.Resource(model, originalParts), DocumentTreeFormat.Resource(model, targetParts)))
                    {
                        throw FederationJson.Invalid("The alias does not target the same local Resource model type.");
                    }
                    try
                    {
                        meta = await ReadSourceAsync(member.Source,
                            new(FederationOperation.Entity, target + "/meta"), captures, cancellationToken).ConfigureAwait(false);
                    }
                    catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound)
                    {
                        continue;
                    }
                    entity = Entity(meta.Metadata);
                    if (entity.TryGetProperty("xref", out _)) { continue; }
                    ownerXid = target;
                }
                var version = FederationJson.String(entity, "defaultversionid");
                var selected = await ReadSourceAsync(member.Source,
                    new(FederationOperation.Entity, ownerXid + "/versions/" + version), captures, cancellationToken).ConfigureAwait(false);
                var versionEntity = Entity(selected.Metadata);
                if (versionEntity.TryGetProperty("labels", out var value)) { labels = value; }
            }
            if (request.Selector.Matches(labels)) { matches.Add(member); }
        }

        CheckCaptures(captures);
        if (matches.Count != 1)
        {
            throw new FederationException(matches.Count == 0 ? FederationErrorCode.NotFound : FederationErrorCode.Ambiguous,
                "The complete shadowed view did not contain exactly one literal-label match.");
        }

        return new(request.Target, matches[0].Result);
    }

    private void Compatible(IFederationReadSource source, string target, Dictionary<IFederationReadSource, SourceCapture> captures)
    {
        var compatible = _compatible(source, target);
        _ = Capture(source, captures);
        if (!compatible)
        {
            throw FederationJson.Invalid("The source model or projection is incompatible with the consumer view.");
        }
    }

    private async ValueTask<FederationReadResult?> ProbeAsync(
        IFederationReadSource source, FederationReadRequest request,
        Dictionary<IFederationReadSource, SourceCapture> captures, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ReadSourceAsync(source, request, captures, cancellationToken).ConfigureAwait(false);
            if (!FederationSyntax.SameXid(FederationJson.String(Entity(result.Metadata), "xid"), request.Target))
            {
                throw FederationJson.Invalid("A successful Resource probe returned a different identity.");
            }
            return result;
        }
        catch (FederationException exception) when (exception.Code == FederationErrorCode.NotFound)
        {
            return null;
        }
    }

    private async ValueTask<FederationReadResult> ReadSourceAsync(
        IFederationReadSource source, FederationReadRequest request,
        Dictionary<IFederationReadSource, SourceCapture> captures, CancellationToken cancellationToken)
    {
        _budget.ChargeRequest();
        var capture = Capture(source, captures);
        var result = await source.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = Capture(source, captures);
        if (result.Context != capture.Context)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The selected source or resolution owner changed during the operation.");
        }
        return result;
    }

    private static SourceCapture Capture(IFederationReadSource source, Dictionary<IFederationReadSource, SourceCapture> captures)
    {
        var current = new SourceCapture(source.Context, FederationCapabilities.GetResolutionOwner(source.Capabilities));
        if (captures.TryGetValue(source, out var previous))
        {
            if (current != previous)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The selected source or resolution owner changed between dependent reads.");
            }
            return previous;
        }
        captures.Add(source, current);
        return current;
    }

    private readonly record struct SourceCapture(NativeRegistryContext Context, FederationResolutionOwner Owner);

    private static void CheckCaptures(Dictionary<IFederationReadSource, SourceCapture> captures)
    {
        foreach (var source in captures.Keys)
        {
            _ = Capture(source, captures);
        }
    }

    private static JsonElement Entity(JsonElement metadata) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("entity", out var entity) ? entity : metadata;
}
