// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Validation;

public static partial class ApiOperationDocumentResolver
{
    private static async ValueTask<ApiOperationResolutionResult?> CollectOperationMatchesAsync(
        OperationLocation location, string operationId, bool includeCallbacks, List<OperationLocation> matches,
        ReferenceContext references, ValidationContext budget, HashSet<string> active, int depth,
        CancellationToken cancellationToken)
    {
        budget.Work(path: location.Pointer);
        if (depth > Capabilities.MaxReferenceDepth)
        {
            return Failure(ApiOperationResolutionStatus.Indeterminate, "limit.callback_depth",
                "The callback operation traversal depth limit was exceeded.", location.Pointer);
        }
        var identity = location.Source.Identity + "#" + location.Pointer;
        if (!active.Add(identity))
        {
            return Failure(ApiOperationResolutionStatus.Indeterminate, "dependency.cycle",
                "A callback operation cycle prevents operationId selection.", location.Pointer);
        }
        if (location.Operation.TryGetProperty("operationId", out var id))
        {
            if (id.ValueKind != JsonValueKind.String)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.operation_id",
                    "An operationId must be a string.", location.Pointer + "/operationId");
            }
            if (string.Equals(id.GetString(), operationId, StringComparison.Ordinal) && matches.Count < 2)
            {
                matches.Add(location);
            }
        }
        if (includeCallbacks && location.Operation.TryGetProperty("callbacks", out var callbacks))
        {
            if (callbacks.ValueKind != JsonValueKind.Object)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.callbacks",
                    "Operation callbacks must be an object.", location.Pointer + "/callbacks");
            }
            foreach (var callback in callbacks.EnumerateObject())
            {
                budget.Work(path: location.Pointer);
                var failure = await CollectCallbackMatchesAsync(callback.Value, location.Source,
                    location.Pointer + "/callbacks/" + Escape(callback.Name), operationId, matches,
                    references, budget, active, depth + 1, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
            }
        }
        active.Remove(identity);
        return null;
    }

    private static async ValueTask<ApiOperationResolutionResult?> CollectCallbackMatchesAsync(
        JsonElement callback, ReferencedDocument source, string pointer, string operationId,
        List<OperationLocation> matches, ReferenceContext references, ValidationContext budget,
        HashSet<string> active, int depth, CancellationToken cancellationToken)
    {
        var referencePath = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            budget.Work(path: pointer);
            if (callback.ValueKind != JsonValueKind.Object)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "document.callback",
                    "A Callback Object or reference target must be an object.", pointer);
            }
            if (!callback.TryGetProperty("$ref", out var reference)) { break; }
            if (reference.ValueKind != JsonValueKind.String)
            {
                return Failure(ApiOperationResolutionStatus.Invalid, "dependency.callback",
                    "A Callback reference must be a string.", pointer + "/$ref");
            }
            if (referencePath.Count >= Capabilities.MaxReferenceDepth)
            {
                return Failure(ApiOperationResolutionStatus.Indeterminate, "limit.reference_depth",
                    "The Callback reference-chain depth limit was exceeded.", pointer + "/$ref");
            }
            var target = await references.ResolveAsync(source, reference.GetString()!, pointer + "/$ref",
                cancellationToken).ConfigureAwait(false);
            if (target.Failure is not null) { return target.Failure; }
            var resolved = target.Target!;
            if (!referencePath.Add(resolved.Document.Identity + "#" + resolved.Pointer))
            {
                return Failure(ApiOperationResolutionStatus.Indeterminate, "dependency.cycle",
                    "A Callback reference cycle prevents operationId selection.", pointer + "/$ref");
            }
            callback = resolved.Value;
            source = resolved.Document;
            pointer = resolved.Pointer;
        }
        foreach (var expression in callback.EnumerateObject())
        {
            budget.Work(path: pointer);
            if (expression.Name.StartsWith("x-", StringComparison.Ordinal)) { continue; }
            var pathItem = await ResolvePathItemAsync(expression.Value, source, pointer + "/" + Escape(expression.Name),
                references, new(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            if (pathItem.Failure is not null) { return pathItem.Failure; }
            foreach (var operation in pathItem.Operations.Values)
            {
                var failure = await CollectOperationMatchesAsync(operation, operationId, true, matches,
                    references, budget, active, depth, cancellationToken).ConfigureAwait(false);
                if (failure is not null) { return failure; }
            }
        }
        return null;
    }
}
