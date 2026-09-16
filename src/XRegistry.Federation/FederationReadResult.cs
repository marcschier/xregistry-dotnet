// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Federation;

/// <summary>Verified, independently owned document bytes. Every opened stream has an independent position and lifetime.</summary>
public sealed class FederationDocument
{
    private readonly byte[] bytes;

    /// <summary>Copies exact bytes without text conversion. Media type and document base remain separate.</summary>
    public FederationDocument(ReadOnlySpan<byte> bytes, string? contentType = null, string? documentBase = null,
        string? documentOrigin = null)
    {
        this.bytes = bytes.ToArray();
        ContentType = contentType;
        Base = documentBase;
        Origin = documentOrigin;
    }

    /// <summary>The exact byte count, including zero for present empty content.</summary>
    public long Length => bytes.LongLength;
    /// <summary>The Version's media type, if present.</summary>
    public string? ContentType { get; }
    /// <summary>The explicitly declared document base, never inferred from storage.</summary>
    public string? Base { get; }
    /// <summary>The explicitly declared original document location, if captured.</summary>
    public string? Origin { get; }
    /// <summary>Opens a new caller-owned non-writable stream that does not expose the backing array.</summary>
    public Stream OpenRead() => new MemoryStream(bytes, 0, bytes.Length, false, false);
}

/// <summary>A detached read result. Origin context never becomes a Core entity attribute.</summary>
public sealed class FederationReadResult
{
    private FederationReadResult(string selectedXid, NativeRegistryContext context, JsonElement metadata,
        FederationDocument? document, JsonElement externalDocument)
    {
        ArgumentNullException.ThrowIfNull(context);
        SelectedXid = selectedXid;
        Context = context;
        Metadata = metadata.ValueKind == JsonValueKind.Undefined ? default : metadata.Clone();
        Document = document;
        ExternalDocument = externalDocument.ValueKind == JsonValueKind.Undefined ? default : externalDocument.Clone();
    }

    /// <summary>The exact selected entity, collection, or Version (including the selected default Version).</summary>
    public string SelectedXid { get; }
    /// <summary>The retained source, revision, and root-byte evidence.</summary>
    public NativeRegistryContext Context { get; }
    /// <summary>A detached metadata envelope, or Undefined for a document result.</summary>
    public JsonElement Metadata { get; }
    /// <summary>Present verified bytes, or null for metadata or an external descriptor.</summary>
    public FederationDocument? Document { get; }
    /// <summary>An explicit external descriptor, or Undefined. It is never fetched by this package.</summary>
    public JsonElement ExternalDocument { get; }

    /// <summary>Creates metadata output. Adapters must honor the requested envelope and completeness contract.</summary>
    public static FederationReadResult FromMetadata(string selectedXid, NativeRegistryContext context, JsonElement metadata)
    {
        FederationJson.RequireObject(metadata);
        return new(selectedXid, context, metadata, null, default);
    }

    /// <summary>Creates a document result, retaining the exact selected Version separately from the request.</summary>
    public static FederationReadResult FromDocument(string selectedVersionXid, NativeRegistryContext context,
        FederationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(selectedVersionXid, context, default, document, default);
    }

    /// <summary>Returns an unfetched external descriptor. A locator is not a successful byte retrieval.</summary>
    public static FederationReadResult FromExternalDocument(string selectedVersionXid, NativeRegistryContext context,
        JsonElement descriptor)
    {
        FederationJson.RequireObject(descriptor);
        return new(selectedVersionXid, context, default, null, descriptor);
    }
}
