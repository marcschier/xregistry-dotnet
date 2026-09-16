// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>A separately authorized producer destination. It is not a mutable federation source.</summary>
/// <remarks>Methods complete only when that object/reference is available. No automatic mutation retry is implied.</remarks>
public interface IOciPublisher
{
    /// <summary>The fixed, credential-free authorized destination context.</summary>
    NativeRegistryContext Context { get; }
    /// <summary>Stores an exact verified config, document or placeholder using the blob protocol.</summary>
    ValueTask PutBlobAsync(OciSnapshotObject blob, CancellationToken cancellationToken = default);
    /// <summary>Stores an index or manifest by digest after its ordinary descriptor descendants exist.</summary>
    ValueTask PutManifestAsync(OciSnapshotObject manifest, CancellationToken cancellationToken = default);
    /// <summary>Creates or moves the explicitly supplied reference only after the entire selected root closure is available.</summary>
    ValueTask CommitReferenceAsync(string reference, OciSnapshotObject root, CancellationToken cancellationToken = default);
}
