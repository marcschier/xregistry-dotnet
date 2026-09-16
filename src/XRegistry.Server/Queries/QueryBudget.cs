// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Queries;

namespace XRegistry.Server;

internal sealed class QueryBudget : IDisposable
{
    internal QueryBudget(RegistryQueryLimits limits, TimeProvider clock, CancellationToken cancellationToken,
        RegistryLimits? registryLimits = null)
    {
        registryLimits ??= new();
        Shared = new(new()
        {
            MaxFilterExpressions = limits.MaxFilterExpressions,
            MaxPathSegments = limits.MaxPathSegments,
            MaxQueryCharacters = registryLimits.MaxQueryCharacters,
            MaxWork = limits.MaxWork,
            MaxEntities = limits.MaxEntities,
            MaxFactBytes = limits.MaxFactBytes,
            MaxSourceReads = registryLimits.MaxEntityOperations,
            MaxCollectionMembers = registryLimits.MaxEntityOperations,
            MaxSourceBytes = registryLimits.MaxWorkingSetBytes,
            MaxDocumentBytes = registryLimits.MaxDocumentBytes,
            MaxDuration = limits.MaxDuration,
            Json = registryLimits.Json
        }, clock, cancellationToken);
    }

    internal RegistryQueryBudget Shared { get; }
    internal void Spend(long count = 1) => Shared.Spend(count);
    internal ValueTask<bool> AuthorizeAsync(Func<CancellationToken, ValueTask<bool>> decision) => Shared.RunAsync(decision);
    internal ValueTask PrepareAsync(Func<CancellationToken, ValueTask> prepare) => Shared.RunAsync(prepare);
    public void Dispose() => Shared.Dispose();
}
