using System.Text.Json;

namespace XRegistry.Federation;

/// <summary>
/// Explicitly discharges one URI target obligation in its authorized source context.
/// False denies publication. The callback must honor cancellation and charge its
/// own work/I/O to the supplied budget; no default network target resolver exists.
/// </summary>
public delegate ValueTask<bool> ProducerTargetValidator(ProducerTargetValidationContext context, CancellationToken cancellationToken);

/// <summary>Owned validation facts and a bounded, source-local metadata dependency reader.</summary>
public sealed class ProducerTargetValidationContext
{
    private readonly Func<RegistryPath, ValueTask<JsonElement>> _read;
    private readonly Dictionary<string, Task<JsonElement>> _reads = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task<JsonElement>? _lastRead;
    private bool _active = true;

    internal ProducerTargetValidationContext(string sourceName, NativeRegistryContext sourceContext,
        RegistryPath entityPath, RegistryModel model, RegistryJson metadata, RegistryValidationObligation obligation,
        FederationReadBudget budget, Func<RegistryPath, ValueTask<JsonElement>> read)
    {
        SourceName = sourceName;
        SourceContext = sourceContext;
        EntityPath = entityPath;
        Model = model;
        Metadata = metadata;
        Obligation = obligation;
        Budget = budget;
        _read = read;
    }

    /// <summary>Gets the explicitly registered source name.</summary>
    public string SourceName { get; }
    /// <summary>Gets the captured source identity, not a discovered alternate origin.</summary>
    public NativeRegistryContext SourceContext { get; }
    /// <summary>Gets the source-local entity whose normalized metadata is being checked.</summary>
    public RegistryPath EntityPath { get; }
    /// <summary>Gets the effective model used to validate the metadata shape.</summary>
    public RegistryModel Model { get; }
    /// <summary>Gets normalized owned metadata; arbitrary domain URLs have not been rebased.</summary>
    public RegistryJson Metadata { get; }
    /// <summary>Gets the precise Core target obligation and JSON Pointer.</summary>
    public RegistryValidationObligation Obligation { get; }
    /// <summary>Gets the operation-wide work/read budget.</summary>
    public FederationReadBudget Budget { get; }

    /// <summary>
    /// Reads only metadata from this same source with the enclosing operation's
    /// cancellation token. Each dependency is retained for access rechecks.
    /// Reads must be awaited, cannot overlap, and cannot outlive the callback.
    /// </summary>
    public ValueTask<JsonElement> ReadMetadataAsync(RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_gate)
        {
            if (!_active) { throw new InvalidOperationException("The target-policy context is no longer active."); }
            Budget.ChargeWork();
            if (_lastRead is { IsCompleted: false }) { throw new InvalidOperationException("Target-policy reads cannot overlap."); }
            if (!_reads.TryGetValue(path.EscapedPath, out var pending))
            {
                pending = _read(path).AsTask();
                _reads.Add(path.EscapedPath, pending);
            }
            _lastRead = pending;
            return new(pending);
        }
    }

    internal async ValueTask CompleteAsync()
    {
        Task<JsonElement>[] pending;
        lock (_gate)
        {
            _active = false;
            pending = _reads.Values.ToArray();
            _reads.Clear();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}

public sealed partial class ProducerRegistryView
{
    private async ValueTask ValidateTargetsAsync(Slot source, RegistryPath path,
        RegistryMetadataValidationResult metadata, CancellationToken token)
    {
        foreach (var obligation in metadata.Obligations)
        {
            token.ThrowIfCancellationRequested();
            _budget.ChargeWork();
            if (obligation.Kind == "matchversions") { continue; }
            if (obligation.Kind != "target" || source.TargetValidator is not { } validator)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "This source needs an explicit policy for its external metadata target obligation.", "target_policy_required");
            }
            var context = new ProducerTargetValidationContext(source.Name, source.Context, path, _model,
                metadata.Metadata, obligation, _budget, ReadDependencyAsync);
            bool accepted;
            CheckCaptures(token);
            try { accepted = await validator(context, token).ConfigureAwait(false); }
            finally { await context.CompleteAsync().ConfigureAwait(false); }
            CheckCaptures(token);
            token.ThrowIfCancellationRequested();
            _budget.ChargeWork();
            if (!accepted)
            {
                throw new FederationException(FederationErrorCode.PolicyDenied,
                    "The explicit source target policy did not discharge the metadata obligation.", "target_policy_denied");
            }
        }

        async ValueTask<JsonElement> ReadDependencyAsync(RegistryPath dependency)
        {
            token.ThrowIfCancellationRequested();
            _budget.ChargeWork();
            if (dependency.Kind is not (RegistryPathKind.Registry or RegistryPathKind.Group or RegistryPathKind.Resource or
                RegistryPathKind.Meta or RegistryPathKind.Version))
            {
                throw Unsupported("Target policies may read typed metadata entities, not collections, Documents or administrative planes.");
            }
            Resource(dependency);
            var target = LogicalPath(dependency);
            var read = await source.ReadPolicyAsync(new(FederationOperation.Entity, target), token).ConfigureAwait(false);
            var entity = Entity(read);
            CheckEntityIdentity(entity, dependency);
            TrackModelDependency(source, PathFor(target));
            return Element(entity);
        }
    }
}
