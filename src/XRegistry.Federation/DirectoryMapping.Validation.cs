// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Federation;

/// <summary>Evidence from a complete traversal of a selected mapping's internal file references.</summary>
public sealed record DirectoryMappingValidationResult
{
    internal DirectoryMappingValidationResult(string rootSha256, string snapshotClass, int metadataObjects, int documents)
    {
        RootSha256 = rootSha256;
        SnapshotClass = snapshotClass;
        MetadataObjects = metadataObjects;
        Documents = documents;
    }

    /// <summary>Gets the exact captured root serialization's SHA-256, not publisher authentication.</summary>
    public string RootSha256 { get; }
    /// <summary>Gets the verified linked or offline-complete internal-closure class.</summary>
    public string SnapshotClass { get; }
    /// <summary>Gets the number of distinct reachable metadata files, including registry.json.</summary>
    public int MetadataObjects { get; }
    /// <summary>Gets the number of distinct reachable local domain files, including zero-byte files.</summary>
    public int Documents { get; }
}

public sealed partial class DirectoryMapping
{
    /// <summary>Verifies all referenced metadata, Version state and local Document bytes without materializing a response tree.</summary>
    /// <remarks>
    /// Unreferenced files, external catalog links and links inside Documents are outside the closure.
    /// External-only descriptors are allowed only for linked snapshots and are never fetched.
    /// A failed or limited traversal returns no partial validation result.
    /// </remarks>
    public async ValueTask<DirectoryMappingValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckContext();
            await WalkAsync(_root, 1).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckContext();
            return new(Context.RootSha256!, FederationJson.String(_root.Data.GetProperty("snapshot"), "completeness"),
                _objects.Count, _documents.Count);
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }

        async ValueTask WalkAsync(TreeObject item, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _budget.CheckDepth(depth);
            _budget.ChargeWork();
            if (item.Kind == "version")
            {
                var descriptor = item.Data.GetProperty("document");
                if (FederationJson.String(descriptor, "kind") == "local")
                {
                    await LoadDocumentAsync(DocumentTreeFormat.LocalDocument(descriptor, item.Xid),
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            if (item.Kind == "resource")
            {
                await GetMetaAsync(item, cancellationToken).ConfigureAwait(false);
            }
            foreach (var reference in item.Children)
            {
                var collection = await LoadAsync(reference, cancellationToken).ConfigureAwait(false);
                if (collection.Children.Count != 0) { _budget.CheckDepth(depth + 1); }
                var members = await CollectionMembersAsync(collection, cancellationToken).ConfigureAwait(false);
                if (item.Kind == "resource")
                {
                    var state = await VersionStateAsync(item, collection, cancellationToken).ConfigureAwait(false);
                    await ValidateVersionSetAsync(state, members, cancellationToken).ConfigureAwait(false);
                }
                foreach (var member in members)
                {
                    await WalkAsync(member, depth + 1).ConfigureAwait(false);
                }
            }
        }
    }
}
