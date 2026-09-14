using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;

namespace XRegistry;

/// <summary>The concrete core model value types (binary is recognized but lacks a normative attribute contract in the frozen prose).</summary>
public enum RegistryValueType
{
    /// <summary>Opaque, syntactically valid JSON.</summary>
    Any,
    /// <summary>A homogeneous array, with an explicit item definition.</summary>
    Array,
    /// <summary>A JSON boolean.</summary>
    Boolean,
    /// <summary>An exact JSON number.</summary>
    [SuppressMessage("Naming", "CA1720", Justification = "This is the normative xRegistry datatype name.")]
    Decimal,
    /// <summary>A mathematically integral signed JSON number.</summary>
    [SuppressMessage("Naming", "CA1720", Justification = "This is the normative xRegistry datatype name.")]
    Integer,
    /// <summary>A map with the core extended key grammar.</summary>
    Map,
    /// <summary>A modeled object.</summary>
    [SuppressMessage("Naming", "CA1720", Justification = "This is the normative xRegistry datatype name.")]
    Object,
    /// <summary>A Unicode string, including the empty string.</summary>
    [SuppressMessage("Naming", "CA1720", Justification = "This is the normative xRegistry datatype name.")]
    String,
    /// <summary>An RFC3339 timestamp.</summary>
    Timestamp,
    /// <summary>A mathematically integral nonnegative JSON number.</summary>
    [SuppressMessage("Naming", "CA1720", Justification = "This is the normative xRegistry datatype name.")]
    UInteger,
    /// <summary>An absolute or relative RFC3986 URI reference.</summary>
    Uri,
    /// <summary>An absolute RFC3986 URI.</summary>
    UriAbsolute,
    /// <summary>A relative RFC3986 URI reference.</summary>
    UriRelative,
    /// <summary>An RFC6570 URI template.</summary>
    UriTemplate,
    /// <summary>An absolute or relative URL reference.</summary>
    Url,
    /// <summary>An absolute URL.</summary>
    UrlAbsolute,
    /// <summary>A relative URL reference.</summary>
    UrlRelative,
    /// <summary>A model-typed registry entity reference; dangling references are legal.</summary>
    Xid,
    /// <summary>A reference to a Registry, Group, Resource or Version model type.</summary>
    XidType,
    /// <summary>A recognized derived-schema type with no defined core attribute serialization contract.</summary>
    Binary
}

/// <summary>An immutable attribute or collection-item definition. An item's <see cref="Name"/> is empty.</summary>
public sealed record RegistryAttributeDefinition
{
    internal RegistryAttributeDefinition(string name, RegistryValueType type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>Gets the attribute name, wildcard, or empty string for an item definition.</summary>
    public string Name { get; }
    /// <summary>Gets the declared value type.</summary>
    public RegistryValueType Type { get; internal init; }
    /// <summary>Gets the canonical lowercase type name.</summary>
    public string TypeName => ModelTypes.Name(Type);
    /// <summary>Gets the optional typed-reference target template.</summary>
    public string? Target { get; internal init; }
    /// <summary>Gets the canonical object name character set: strict or extended.</summary>
    public string NameCharset { get; internal init; } = "strict";
    /// <summary>Gets the optional human-readable description.</summary>
    public string? Description { get; internal init; }
    /// <summary>Gets the immutable, exact scalar enumeration values.</summary>
    public IReadOnlyList<JsonElement> EnumValues { get; internal init; } = Array.Empty<JsonElement>();
    /// <summary>Gets whether a nonempty enumeration is restrictive rather than advisory.</summary>
    public bool Strict { get; internal init; } = true;
    /// <summary>Gets the declared cross-Version equality obligation; compilation does not discharge it.</summary>
    public bool MatchVersions { get; internal init; }
    /// <summary>Gets whether ordinary client writes must ignore this attribute.</summary>
    public bool ReadOnly { get; internal init; }
    /// <summary>Gets whether a system attribute is immutable once set.</summary>
    public bool Immutable { get; internal init; }
    /// <summary>Gets whether a completed entity must have a non-null value.</summary>
    public bool Required { get; internal init; }
    /// <summary>Gets the owned non-null scalar default, or an Undefined element when no default exists.</summary>
    public JsonElement DefaultValue { get; internal init; }
    /// <summary>Gets the statically declared child attributes of an object.</summary>
    public IReadOnlyDictionary<string, RegistryAttributeDefinition> Attributes { get; internal init; } = ModelCollections.EmptyAttributes;
    /// <summary>Gets the item definition for an array or map.</summary>
    public RegistryAttributeDefinition? Item { get; internal init; }
    /// <summary>Gets case-insensitive discriminators and their conditional sibling definitions.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, RegistryAttributeDefinition>> IfValues { get; internal init; } =
        FrozenDictionary<string, IReadOnlyDictionary<string, RegistryAttributeDefinition>>.Empty;
    /// <summary>Gets whether this is a specification-defined attribute, rather than an extension.</summary>
    public bool IsSystemDefined { get; internal init; }
    internal bool IsIdentifier { get; init; }
    internal bool IsCollection { get; init; }
    internal bool IsDocument { get; init; }
    internal bool IsConstraints { get; init; }
}

/// <summary>Informational model annotations. Compatibility statements do not imply verified compatibility.</summary>
public sealed record RegistryModelAnnotations
{
    internal RegistryModelAnnotations() { }
    /// <summary>Gets the description.</summary>
    public string? Description { get; internal init; }
    /// <summary>Gets the documentation reference.</summary>
    public string? Documentation { get; internal init; }
    /// <summary>Gets the optional icon reference.</summary>
    public string? Icon { get; internal init; }
    /// <summary>Gets the declared model version.</summary>
    public string? ModelVersion { get; internal init; }
    /// <summary>Gets the unverified model compatibility reference.</summary>
    public string? ModelCompatibleWith { get; internal init; }
    /// <summary>Gets immutable model labels, whose nonempty keys are unrestricted strings.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; internal init; } = FrozenDictionary<string, string>.Empty;
}

/// <summary>An immutable Group constraint over a statically defined scalar Resource attribute.</summary>
public sealed record RegistryConstraint
{
    internal RegistryConstraint(string path, string resourceType, IReadOnlyList<string> attributePath)
    {
        Path = path;
        ResourceType = resourceType;
        AttributePath = attributePath;
    }

    /// <summary>Gets the original Resource-plus-dot-notation constraint key.</summary>
    public string Path { get; }
    /// <summary>Gets the Resource's plural type name.</summary>
    public string ResourceType { get; }
    /// <summary>Gets the parsed scalar attribute path (no maps, arrays or dynamic attributes).</summary>
    public IReadOnlyList<string> AttributePath { get; }
    /// <summary>Gets the overriding default, or Undefined when absent.</summary>
    public JsonElement DefaultValue { get; internal init; }
    /// <summary>Gets the allowed subset; an empty collection imposes no additional restriction.</summary>
    public IReadOnlyList<JsonElement> EnumValues { get; internal init; } = Array.Empty<JsonElement>();
    /// <summary>Gets the Group attribute dot path, or null when no equals aspect applies.</summary>
    public string? EqualsAttribute { get; internal init; }
    /// <summary>Gets the parsed Group attribute path, empty when no equals aspect applies.</summary>
    public IReadOnlyList<string> EqualsPath { get; internal init; } = Array.Empty<string>();
}

/// <summary>The structural projection chosen by a model's content-type map, not a document validation outcome.</summary>
public enum RegistryDocumentFormat
{
    /// <summary>Exact bytes represented using the resourcebase64 attribute.</summary>
    Binary,
    /// <summary>JSON, subject to actual document syntax validation by the caller.</summary>
    Json,
    /// <summary>A string, subject to actual character decoding by the caller.</summary>
    [SuppressMessage("Naming", "CA1720", Justification = "This is the normative xRegistry typemap value.")]
    String
}

/// <summary>An immutable, effective Resource type, including all structural controls and attribute planes.</summary>
public sealed record RegistryResourceDefinition
{
    internal RegistryResourceDefinition(string plural, string singular)
    {
        Plural = plural;
        Singular = singular;
    }

    /// <summary>Gets the plural collection name.</summary>
    public string Plural { get; }
    /// <summary>Gets the singular Resource name.</summary>
    public string Singular { get; }
    /// <summary>Gets informational annotations.</summary>
    public RegistryModelAnnotations Annotations { get; internal init; } = new();
    /// <summary>Gets the exact nonnegative retention count; zero means no stated limit.</summary>
    public BigInteger MaxVersions { get; internal init; }
    /// <summary>Gets whether the client may specify a new Version ID.</summary>
    public bool SetVersionId { get; internal init; } = true;
    /// <summary>Gets whether Versions have separate Documents, including possibly empty Documents.</summary>
    public bool HasDocument { get; internal init; } = true;
    /// <summary>Gets manual, createdat, modifiedat or semver. This record does not implement version ordering.</summary>
    public string VersionMode { get; internal init; } = "manual";
    /// <summary>Gets the single-root lifecycle obligation.</summary>
    public bool SingleVersionRoot { get; internal init; }
    /// <summary>Gets the external document-format validation obligation.</summary>
    public bool ValidateFormat { get; internal init; }
    /// <summary>Gets the external cross-Version compatibility validation obligation.</summary>
    public bool ValidateCompatibility { get; internal init; }
    /// <summary>Gets whether unsupported external format/compatibility validators must fail.</summary>
    public bool StrictValidation { get; internal init; }
    /// <summary>Gets effective case-insensitive content-type mappings, including implicit defaults.</summary>
    public IReadOnlyDictionary<string, RegistryDocumentFormat> TypeMap { get; internal init; } =
        FrozenDictionary<string, RegistryDocumentFormat>.Empty;
    /// <summary>Gets Version attributes, including specification-defined attributes.</summary>
    public IReadOnlyDictionary<string, RegistryAttributeDefinition> Attributes { get; internal init; } = ModelCollections.EmptyAttributes;
    /// <summary>Gets system-managed Resource projection attributes.</summary>
    public IReadOnlyDictionary<string, RegistryAttributeDefinition> ResourceAttributes { get; internal init; } = ModelCollections.EmptyAttributes;
    /// <summary>Gets Resource Meta attributes, including extensions.</summary>
    public IReadOnlyDictionary<string, RegistryAttributeDefinition> MetaAttributes { get; internal init; } = ModelCollections.EmptyAttributes;

    /// <summary>Looks up type/subtype only, case-insensitively; conflicting matches or no matches select binary.</summary>
    public RegistryDocumentFormat ResolveDocumentFormat(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var semicolon = contentType.IndexOf(';');
        var mediaType = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        RegistryDocumentFormat? result = null;
        foreach (var entry in TypeMap)
        {
            var star = entry.Key.IndexOf('*');
            var matches = star < 0
                ? string.Equals(entry.Key, mediaType, StringComparison.OrdinalIgnoreCase)
                : mediaType.Length >= entry.Key.Length - 1 &&
                  mediaType.StartsWith(entry.Key[..star], StringComparison.OrdinalIgnoreCase) &&
                  mediaType.EndsWith(entry.Key[(star + 1)..], StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                continue;
            }

            if (result.HasValue && result.Value != entry.Value)
            {
                return RegistryDocumentFormat.Binary;
            }

            result = entry.Value;
        }

        return result ?? RegistryDocumentFormat.Binary;
    }
}

/// <summary>An immutable effective Group type, including resolved local Resource imports and constraints.</summary>
public sealed record RegistryGroupDefinition
{
    internal RegistryGroupDefinition(string plural, string singular)
    {
        Plural = plural;
        Singular = singular;
    }
    /// <summary>Gets the plural collection name.</summary>
    public string Plural { get; }
    /// <summary>Gets the singular Group name.</summary>
    public string Singular { get; }
    /// <summary>Gets informational annotations.</summary>
    public RegistryModelAnnotations Annotations { get; internal init; } = new();
    /// <summary>Gets effective Group attributes.</summary>
    public IReadOnlyDictionary<string, RegistryAttributeDefinition> Attributes { get; internal init; } = ModelCollections.EmptyAttributes;
    /// <summary>Gets effective Resources; shared imported definitions are immutable.</summary>
    public IReadOnlyDictionary<string, RegistryResourceDefinition> Resources { get; internal init; } =
        FrozenDictionary<string, RegistryResourceDefinition>.Empty;
    /// <summary>Gets the Group type's scalar Resource constraints.</summary>
    public IReadOnlyDictionary<string, RegistryConstraint> Constraints { get; internal init; } =
        FrozenDictionary<string, RegistryConstraint>.Empty;
}

internal static class ModelCollections
{
    internal static readonly IReadOnlyDictionary<string, RegistryAttributeDefinition> EmptyAttributes =
        FrozenDictionary<string, RegistryAttributeDefinition>.Empty;
}

internal static class ModelTypes
{
    internal static string Name(RegistryValueType type) => type switch
    {
        RegistryValueType.Any => "any",
        RegistryValueType.Array => "array",
        RegistryValueType.Boolean => "boolean",
        RegistryValueType.Decimal => "decimal",
        RegistryValueType.Integer => "integer",
        RegistryValueType.Map => "map",
        RegistryValueType.Object => "object",
        RegistryValueType.String => "string",
        RegistryValueType.Timestamp => "timestamp",
        RegistryValueType.UInteger => "uinteger",
        RegistryValueType.Uri => "uri",
        RegistryValueType.UriAbsolute => "uriabsolute",
        RegistryValueType.UriRelative => "urirelative",
        RegistryValueType.UriTemplate => "uritemplate",
        RegistryValueType.Url => "url",
        RegistryValueType.UrlAbsolute => "urlabsolute",
        RegistryValueType.UrlRelative => "urlrelative",
        RegistryValueType.Xid => "xid",
        RegistryValueType.XidType => "xidtype",
        RegistryValueType.Binary => "binary",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    internal static RegistryValueType Parse(string name, string path) => name switch
    {
        "any" => RegistryValueType.Any,
        "array" => RegistryValueType.Array,
        "boolean" => RegistryValueType.Boolean,
        "decimal" => RegistryValueType.Decimal,
        "integer" => RegistryValueType.Integer,
        "map" => RegistryValueType.Map,
        "object" => RegistryValueType.Object,
        "string" => RegistryValueType.String,
        "timestamp" => RegistryValueType.Timestamp,
        "uinteger" => RegistryValueType.UInteger,
        "uri" => RegistryValueType.Uri,
        "uriabsolute" => RegistryValueType.UriAbsolute,
        "urirelative" => RegistryValueType.UriRelative,
        "uritemplate" => RegistryValueType.UriTemplate,
        "url" => RegistryValueType.Url,
        "urlabsolute" => RegistryValueType.UrlAbsolute,
        "urlrelative" => RegistryValueType.UrlRelative,
        "xid" => RegistryValueType.Xid,
        "xidtype" => RegistryValueType.XidType,
        "binary" => throw Diagnostics.Error("unsupported_model_type", path,
            "The frozen schema lists binary, but the normative prose defines no binary attribute contract."),
        _ => throw Diagnostics.Error("model_error", path, "Unknown model value type.")
    };

    internal static bool IsScalar(RegistryValueType type) =>
        type is not (RegistryValueType.Any or RegistryValueType.Array or RegistryValueType.Map or RegistryValueType.Object);
}
