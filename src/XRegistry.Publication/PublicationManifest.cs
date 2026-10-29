// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace XRegistry.Publication;

/// <summary>A parsed publication manifest that retains its original JSON bytes.</summary>
public sealed class PublicationManifest
{
    private readonly byte[] _exactBytes;

    private PublicationManifest(byte[] exactBytes, string scope, string capture, string completeness,
        PublicationByteDescriptor registry, PublicationByteDescriptor? parent,
        IReadOnlyList<PublicationDocument> documents,
        IReadOnlyDictionary<string, PublicationByteDescriptor> extensions,
        IReadOnlyList<string> requiredExtensions)
    {
        _exactBytes = exactBytes;
        Scope = scope;
        Capture = capture;
        Completeness = completeness;
        Registry = registry;
        Parent = parent;
        Documents = documents;
        Extensions = extensions;
        RequiredExtensions = requiredExtensions;
        SnapshotId = Convert.ToHexString(SHA256.HashData(exactBytes)).ToLowerInvariant();
    }

    /// <summary>Gets the immutable publication scope without URI normalization.</summary>
    public string Scope { get; }

    /// <summary>Gets the source capture method declared by the manifest.</summary>
    public string Capture { get; }

    /// <summary>Gets the declared completeness level.</summary>
    public string Completeness { get; }

    /// <summary>Gets the captured Registry JSON descriptor.</summary>
    public PublicationByteDescriptor Registry { get; }

    /// <summary>Gets the optional preparation predecessor descriptor.</summary>
    public PublicationByteDescriptor? Parent { get; }

    /// <summary>Gets the exact document associations in manifest order.</summary>
    public IReadOnlyList<PublicationDocument> Documents { get; }

    /// <summary>Gets extension descriptors keyed by their original URI strings.</summary>
    public IReadOnlyDictionary<string, PublicationByteDescriptor> Extensions { get; }

    /// <summary>Gets the required extension URIs in manifest order.</summary>
    public IReadOnlyList<string> RequiredExtensions { get; }

    /// <summary>Gets the lower-case SHA-256 hex identifier of the exact manifest bytes.</summary>
    public string SnapshotId { get; }

    /// <summary>Returns an independently owned copy of the exact source JSON.</summary>
    public byte[] GetExactBytes() => (byte[])_exactBytes.Clone();

    /// <summary>Checks that a selected descriptor names this manifest's immutable Snapshot Resource.</summary>
    public void ValidateSnapshotDescriptor(PublicationByteDescriptor descriptor)
    {
        PublicationIntegrity.Verify(_exactBytes, descriptor);
        var path = RegistryPath.Parse(descriptor.Reference.Version);
        if (path.Kind != RegistryPathKind.Version ||
            !string.Equals(path.ResourceType, "snapshots", StringComparison.Ordinal) ||
            !string.Equals(path.ResourceId!.Value, SnapshotId, StringComparison.Ordinal) ||
            !string.Equals(path.VersionId!.Value, "manifest", StringComparison.Ordinal))
        {
            throw PublicationJson.Error("snapshot_identity_mismatch", "",
                "The manifest descriptor must select its digest-named snapshot's manifest Version.");
        }
    }

    /// <summary>Parses and validates the manifest's structural contract.</summary>
    /// <exception cref="PublicationException">The JSON is not a supported publication manifest.</exception>
    /// <exception cref="RegistryException">The input JSON is malformed or exceeds its configured limits.</exception>
    public static PublicationManifest Parse(ReadOnlySpan<byte> exactJson, RegistryJsonLimits? limits = null)
    {
        var exactBytes = exactJson.ToArray();
        using var document = RegistryJson.ParseDocument(exactBytes, limits);
        var root = document.RootElement;
        PublicationJson.RequireObject(root, "");
        PublicationJson.RequireOnly(root, "", "format", "scope", "capture", "completeness",
            "registry", "documents", "parent", "extensions", "requiredExtensions");
        PublicationJson.RequireString(root, "format", "", "xregistry-publication/1");

        var scope = PublicationJson.RequireUri(root, "scope", "");
        var capture = PublicationJson.RequireOneOf(root, "capture", "", "assembled", "source-snapshot", "live-observation");
        var completeness = PublicationJson.RequireOneOf(root, "completeness", "", "linked", "offline-complete");
        var registry = PublicationByteDescriptor.Parse(PublicationJson.RequireProperty(root, "registry", ""));
        var parent = root.TryGetProperty("parent", out var parentValue) ? PublicationByteDescriptor.Parse(parentValue) : null;
        var documents = ParseDocuments(root);
        var extensions = ParseExtensions(root);
        var requiredExtensions = ParseRequiredExtensions(root);
        foreach (var requiredExtension in requiredExtensions)
        {
            if (!extensions.ContainsKey(requiredExtension))
            {
                throw PublicationJson.Error("missing_required_extension", "/requiredExtensions",
                    "Every required extension must have a descriptor in extensions.");
            }
        }

        return new PublicationManifest(exactBytes, scope, capture, completeness, registry, parent,
            Array.AsReadOnly(documents), new ReadOnlyDictionary<string, PublicationByteDescriptor>(extensions),
            Array.AsReadOnly(requiredExtensions));
    }

    private static PublicationDocument[] ParseDocuments(JsonElement root)
    {
        var array = PublicationJson.RequireArray(PublicationJson.RequireProperty(root, "documents", ""), "/documents");
        var documents = new PublicationDocument[array.GetArrayLength()];
        var viewVersions = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var path = $"/documents/{index}";
            PublicationJson.RequireObject(item, path);
            PublicationJson.RequireOnly(item, path, "viewVersion", "originRegistry", "originVersion", "base", "content");
            var viewVersion = PublicationJson.RequireVersion(PublicationJson.RequireString(item, "viewVersion", path), path + "/viewVersion");
            if (!viewVersions.Add(PublicationJson.GetVersionIdentity(viewVersion)))
            {
                throw PublicationJson.Error("duplicate_view_version", path + "/viewVersion",
                    "A captured Version XID may appear only once.");
            }

            var originRegistry = PublicationJson.RequireUri(item, "originRegistry", path);
            var originVersion = PublicationJson.RequireVersion(PublicationJson.RequireString(item, "originVersion", path), path + "/originVersion");
            var baseUri = PublicationJson.RequireUri(item, "base", path);
            var content = PublicationByteDescriptor.Parse(PublicationJson.RequireProperty(item, "content", path));
            documents[index++] = new PublicationDocument(viewVersion, originRegistry, originVersion, baseUri, content);
        }

        return documents;
    }

    private static Dictionary<string, PublicationByteDescriptor> ParseExtensions(JsonElement root)
    {
        var result = new Dictionary<string, PublicationByteDescriptor>(StringComparer.Ordinal);
        if (!root.TryGetProperty("extensions", out var extensions))
        {
            return result;
        }

        PublicationJson.RequireObject(extensions, "/extensions");
        foreach (var property in extensions.EnumerateObject())
        {
            PublicationJson.RequireAbsoluteUri(property.Name, "/extensions");
            result.Add(property.Name, PublicationByteDescriptor.Parse(property.Value));
        }

        return result;
    }

    private static string[] ParseRequiredExtensions(JsonElement root)
    {
        if (!root.TryGetProperty("requiredExtensions", out var required))
        {
            return [];
        }

        var array = PublicationJson.RequireArray(required, "/requiredExtensions");
        var result = new string[array.GetArrayLength()];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in array.EnumerateArray())
        {
            var path = $"/requiredExtensions/{index}";
            var uri = PublicationJson.RequireAbsoluteUri(value, path);
            if (!seen.Add(uri))
            {
                throw PublicationJson.Error("duplicate_required_extension", path, "A required extension URI may appear only once.");
            }

            result[index++] = uri;
        }

        return result;
    }

}

/// <summary>A Registry binding and exact endpoint spelling used by a publication reference.</summary>
public sealed record PublicationRegistryAccess(string Binding, string Endpoint,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>An exact Registry Version reference.</summary>
public sealed record PublicationVersionReference(PublicationRegistryAccess Registry, string Version);

/// <summary>A Version reference paired with the expected digest and encoded byte length.</summary>
public sealed record PublicationByteDescriptor(PublicationVersionReference Reference, string Digest, BigInteger Size)
{
    /// <summary>Parses a byte descriptor from a manifest value.</summary>
    public static PublicationByteDescriptor Parse(JsonElement element)
    {
        PublicationJson.RequireObject(element, "");
        PublicationJson.RequireOnly(element, "", "reference", "digest", "size");
        var reference = ParseVersionReference(PublicationJson.RequireProperty(element, "reference", ""));
        var digest = PublicationJson.RequireNonEmptyString(element, "digest", "");
        if (!PublicationJson.IsSha256Digest(digest))
        {
            throw PublicationJson.Error("invalid_digest", "/digest", "A lower-case sha256 digest with 64 hexadecimal digits is required.");
        }

        var sizeElement = PublicationJson.RequireProperty(element, "size", "");
        if (sizeElement.ValueKind != JsonValueKind.Number ||
            !BigInteger.TryParse(sizeElement.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size < BigInteger.Zero)
        {
            throw PublicationJson.Error("invalid_size", "/size", "A non-negative integer byte size is required.");
        }

        return new PublicationByteDescriptor(reference, digest, size);
    }

    internal static PublicationVersionReference ParseVersionReference(JsonElement element)
    {
        PublicationJson.RequireObject(element, "/reference");
        PublicationJson.RequireOnly(element, "/reference", "registry", "version");
        var registry = ParseRegistryAccess(PublicationJson.RequireProperty(element, "registry", "/reference"));
        var version = PublicationJson.RequireVersion(PublicationJson.RequireString(element, "version", "/reference"), "/reference/version");
        return new PublicationVersionReference(registry, version);
    }

    internal static PublicationRegistryAccess ParseRegistryAccess(JsonElement element)
    {
        PublicationJson.RequireObject(element, "/reference/registry");
        PublicationJson.RequireOnly(element, "/reference/registry", "binding", "endpoint", "parameters");
        var binding = PublicationJson.RequireNonEmptyString(element, "binding", "/reference/registry");
        var endpoint = PublicationJson.RequireCredentialFreeEndpoint(
            PublicationJson.RequireProperty(element, "endpoint", "/reference/registry"),
            "/reference/registry/endpoint");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("parameters", out var values))
        {
            PublicationJson.RequireObject(values, "/reference/registry/parameters");
            foreach (var property in values.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw PublicationJson.Error("invalid_parameter", "/reference/registry/parameters/" + property.Name,
                        "Binding parameters must be strings.");
                }

                parameters.Add(property.Name, property.Value.GetString()!);
            }
        }

        return new PublicationRegistryAccess(binding, endpoint,
            new ReadOnlyDictionary<string, string>(parameters));
    }
}

/// <summary>Checks exact byte length and SHA-256 against a publication descriptor.</summary>
public static class PublicationIntegrity
{
    /// <summary>Rejects bytes whose exact length or digest differs from the descriptor.</summary>
    public static void Verify(ReadOnlySpan<byte> bytes, PublicationByteDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Size != bytes.Length)
        {
            throw PublicationJson.Error("size_mismatch", "", "The retrieved bytes do not match the descriptor size.");
        }

        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, descriptor.Digest, StringComparison.Ordinal))
        {
            throw PublicationJson.Error("digest_mismatch", "", "The retrieved bytes do not match the descriptor digest.");
        }
    }
}

/// <summary>A captured document Version and its original source identity and base URI.</summary>
public sealed record PublicationDocument(string ViewVersion, string OriginRegistry,
    string OriginVersion, string Base, PublicationByteDescriptor Content);

/// <summary>A structural or semantic error in a publication value.</summary>
public sealed class PublicationException : FormatException
{
    /// <summary>Creates a publication failure with a stable code and JSON pointer.</summary>
    public PublicationException(string code, string path, string message)
        : base(message)
    {
        Code = code;
        Path = path;
    }

    /// <summary>Gets the stable machine-readable error code.</summary>
    public string Code { get; }

    /// <summary>Gets the JSON pointer to the invalid value.</summary>
    public string Path { get; }
}

internal static class PublicationJson
{
    internal static void RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Error("expected_object", path, "A JSON object is required.");
        }
    }

    internal static JsonElement RequireArray(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error("expected_array", path, "A JSON array is required.");
        }

        return value;
    }

    internal static JsonElement RequireProperty(JsonElement value, string name, string path)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            throw Error("missing_property", path + "/" + Escape(name), "A required property is missing.");
        }

        return property;
    }

    internal static string RequireNonEmptyString(JsonElement value, string name, string path)
    {
        var property = RequireProperty(value, name, path);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw Error("invalid_string", path + "/" + Escape(name), "A non-empty string is required.");
        }

        return property.GetString()!;
    }

    internal static string RequireOneOf(JsonElement value, string name, string path, params string[] choices)
    {
        var text = RequireNonEmptyString(value, name, path);
        if (!choices.Contains(text, StringComparer.Ordinal))
        {
            throw Error("invalid_value", path + "/" + Escape(name), "The value is not supported.");
        }

        return text;
    }

    internal static string RequireResource(string value, string path)
    {
        try
        {
            if (RegistryPath.Parse(value).Kind == RegistryPathKind.Resource)
            {
                return value;
            }
        }
        catch (RegistryException)
        {
        }

        throw Error("invalid_resource_xid", path, "A root-relative Resource XID is required.");
    }

    internal static string RequireString(JsonElement value, string name, string path, string? exact = null)
    {
        var property = RequireProperty(value, name, path);
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } text ||
            (exact is not null && !string.Equals(text, exact, StringComparison.Ordinal)))
        {
            throw Error("invalid_string", path + "/" + Escape(name),
                exact is null ? "A string is required." : $"The value must be '{exact}'.");
        }

        return text;
    }

    internal static string RequireUri(JsonElement value, string name, string path) =>
        RequireAbsoluteUri(RequireString(value, name, path), path + "/" + Escape(name));

    internal static string RequireAbsoluteUri(string value, string path)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw Error("invalid_uri", path, "An absolute URI is required.");
        }

        return value;
    }

    internal static string RequireCredentialFreeEndpoint(JsonElement value, string path)
    {
        var endpoint = RequireAbsoluteUri(value, path);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0)
        {
            throw Error("credentialed_endpoint", path, "A credential-free absolute endpoint URI is required.");
        }

        return endpoint;
    }

    internal static string RequireAbsoluteUri(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw Error("invalid_uri", path, "An absolute URI string is required.");
        }

        return RequireAbsoluteUri(text, path);
    }

    internal static string GetVersionIdentity(string version)
    {
        var path = RegistryPath.Parse(version);
        return IdentityPart(path.GroupType!, fold: true) +
            IdentityPart(path.GroupId!.Value, fold: true) +
            IdentityPart(path.ResourceType!, fold: true) +
            IdentityPart(path.ResourceId!.Value, fold: true) +
            IdentityPart(path.VersionId!.Value, fold: false);
    }

    internal static string GetResourceIdentity(string resource)
    {
        var path = RegistryPath.Parse(resource);
        if (path.Kind != RegistryPathKind.Resource)
        {
            throw Error("invalid_resource_xid", "", "A root-relative Resource XID is required.");
        }

        return IdentityPart(path.GroupType!, fold: true) +
            IdentityPart(path.GroupId!.Value, fold: true) +
            IdentityPart(path.ResourceType!, fold: true) +
            IdentityPart(path.ResourceId!.Value, fold: true);
    }

    internal static BigInteger RequireNonNegativeInteger(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !BigInteger.TryParse(value.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
        {
            throw Error("invalid_integer", path, "A non-negative integer is required.");
        }

        return integer;
    }

    internal static string RequireVersion(string value, string path)
    {
        try
        {
            if (RegistryPath.Parse(value).Kind == RegistryPathKind.Version)
            {
                return value;
            }
        }
        catch (RegistryException)
        {
        }

        throw Error("invalid_version_xid", path, "A root-relative Version XID is required.");
    }

    internal static bool IsSha256Digest(string digest) =>
        digest.Length == 71 && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
        digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;

    internal static void RequireOnly(JsonElement value, string path, params string[] allowed)
    {
        var allowedNames = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name))
            {
                throw Error("unknown_property", path + "/" + Escape(property.Name), "The property is not supported here.");
            }
        }
    }

    internal static PublicationException Error(string code, string path, string message) => new(code, path, message);

    private static string Escape(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string IdentityPart(string value, bool fold)
    {
        var normalized = fold ? value.ToUpperInvariant() : value;
        return normalized.Length.ToString(CultureInfo.InvariantCulture) + ":" + normalized + "|";
    }
}
