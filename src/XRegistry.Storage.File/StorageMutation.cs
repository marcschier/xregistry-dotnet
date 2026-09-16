// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Storage.File;

/// <summary>A requested opaque-key metadata change. Preparation copies the supplied JSON.</summary>
public sealed class StorageMutation
{
    private StorageMutation(string key, ReadOnlyMemory<byte> metadataJson, bool delete, Stream? document = null, bool preserveDocument = false)
    {
        Key = key;
        MetadataJson = metadataJson;
        IsDelete = delete;
        Document = document;
        PreserveDocument = preserveDocument;
    }

    /// <summary>Gets the opaque ordinal key; it is never used to construct a path.</summary>
    public string Key { get; }

    /// <summary>Requests replacement of one metadata record, without a Document.</summary>
    public static StorageMutation Put(string key, ReadOnlyMemory<byte> metadataJson) => new(key, metadataJson, delete: false);

    /// <summary>Replaces metadata while retaining the existing immutable Document reference, without restaging its bytes.</summary>
    public static StorageMutation PutPreservingDocument(string key, ReadOnlyMemory<byte> metadataJson) =>
        new(key, metadataJson, delete: false, preserveDocument: true);

    /// <summary>Replaces metadata and stages exact bytes from a caller-owned, readable Document stream.</summary>
    public static StorageMutation Put(string key, ReadOnlyMemory<byte> metadataJson, Stream document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(key, metadataJson, delete: false, document);
    }

    /// <summary>Requests deletion of one record. Deleting an absent key still participates in the committed generation.</summary>
    public static StorageMutation Delete(string key) => new(key, default, delete: true);

    internal ReadOnlyMemory<byte> MetadataJson { get; }
    internal bool IsDelete { get; }
    internal Stream? Document { get; }
    internal bool PreserveDocument { get; }
}
