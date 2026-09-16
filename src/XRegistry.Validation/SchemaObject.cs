// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Xml.Linq;

namespace XRegistry.Validation;

/// <summary>A concrete schema declaration together with its owned source Document and reference context.</summary>
public sealed class SchemaObject
{
    private readonly XElement? _xml;

    internal SchemaObject(string format, string schemaUri, string documentReference,
        ReadOnlyMemory<byte> document, Uri? documentUri, SchemaTypeKind kind,
        string? typeName, string? pointer, JsonElement? json,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> references, string? xpath = null, XElement? xml = null)
    {
        Format = format;
        SchemaUri = schemaUri;
        DocumentReference = documentReference;
        Document = document;
        DocumentUri = documentUri;
        Kind = kind;
        TypeName = typeName;
        JsonPointer = pointer;
        Json = json?.Clone();
        XPath = xpath;
        _xml = xml;
        References = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(
            new Dictionary<string, ReadOnlyMemory<byte>>(references, StringComparer.Ordinal));
    }

    /// <summary>Gets the original format discriminator.</summary>
    public string Format { get; }

    /// <summary>Gets the exact raw URI that selected this declaration.</summary>
    public string SchemaUri { get; }

    /// <summary>Gets the exact raw identity of the supplied Document, without the selector.</summary>
    public string DocumentReference { get; }

    /// <summary>Gets owned, unchanged source bytes, including all definitions, packages and imports.</summary>
    public ReadOnlyMemory<byte> Document { get; }

    /// <summary>Gets the explicitly supplied base URI used by the shared reference-validation policy.</summary>
    public Uri? DocumentUri { get; }

    /// <summary>Gets the selected declaration kind.</summary>
    public SchemaTypeKind Kind { get; }

    /// <summary>Gets the fully qualified Avro/Protobuf name or expanded XML name, when applicable.</summary>
    public string? TypeName { get; }

    /// <summary>Gets the decoded RFC 6901 pointer without '#', empty for the root, or null for non-JSON formats.</summary>
    public string? JsonPointer { get; }

    /// <summary>Gets an owned JSON node; its references remain relative to <see cref="Document"/>.</summary>
    public JsonElement? Json { get; }

    /// <summary>Gets the decoded structural XPath, or null when no XML selector was supplied.</summary>
    public string? XPath { get; }

    /// <summary>
    /// Gets a fresh, independently mutable copy of the selected XML declaration,
    /// including inherited namespace bindings. The complete Document remains authoritative.
    /// </summary>
    public XElement? Xml => _xml is null ? null : new XElement(_xml);

    /// <summary>Gets owned dependency bytes keyed by the exact explicit resolver requests.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> References { get; }
}
