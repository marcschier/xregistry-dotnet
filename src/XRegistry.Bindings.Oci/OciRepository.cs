// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

/// <summary>A credential-free native OCI locator, distinct from both a reference and the Registry XID namespace.</summary>
public sealed partial class OciRepository
{
    private OciRepository(string endpoint, string name, Uri origin)
    {
        Endpoint = endpoint;
        Name = name;
        Origin = origin;
    }

    /// <summary>The original oci:// repository locator, without a tag or digest.</summary>
    public string Endpoint { get; }
    /// <summary>The Distribution repository name, retaining all namespace components.</summary>
    public string Name { get; }
    /// <summary>The HTTPS Distribution origin. Native OCI never falls back to plaintext.</summary>
    public Uri Origin { get; }

    /// <summary>Validates the exact endpoint authority and Distribution repository grammar without URI-decoding its path.</summary>
    public static OciRepository Parse(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Length > 4096)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The OCI repository locator exceeds its URI budget.");
        }
        var uri = OciJson.AbsoluteUri(endpoint);
        if (uri.Scheme != "oci" || uri.Host.Length == 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw OciJson.Invalid("An OCI endpoint requires a host and repository, without a query, fragment or reference.");
        }
        var start = endpoint.IndexOf('/', endpoint.IndexOf("://", StringComparison.Ordinal) + 3);
        var repository = start < 0 ? "" : endpoint[(start + 1)..];
        if (!RepositoryName().IsMatch(repository))
        {
            throw OciJson.Invalid("The endpoint path is not a Distribution repository name.");
        }
        return new(endpoint, repository, new Uri("https://" + uri.Authority + "/", UriKind.Absolute));
    }

    /// <summary>Parses one explicitly selected version-1 advertisement; unknown parameters are unsupported, not ignored.</summary>
    public static OciSelectedReference ParseProfile(ReadOnlyMemory<byte> utf8Json, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        budget ??= new();
        var profile = OciJson.Parse(utf8Json.Span, budget, cancellationToken);
        if (OciJson.Text(profile, "name") != "oci")
        {
            throw new FederationException(FederationErrorCode.UnsupportedBinding, "The selected advertisement is not native OCI.");
        }
        var parameters = OciJson.Required(profile, "parameters");
        OciJson.Object(parameters);
        if (parameters.EnumerateObject().Any(p => p.Name != "reference"))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "OCI version 1 defines only the reference parameter.");
        }
        if (profile.TryGetProperty("priority", out _)) { OciJson.Integer(profile, "priority"); }
        var reference = OciJson.Text(parameters, "reference");
        OciFormat.Reference(reference);
        return new(Parse(OciJson.Text(profile, "endpoint")), reference);
    }

    /// <summary>Builds the native manifests endpoint for an explicit tag or SHA-256 digest.</summary>
    public Uri ManifestUri(string reference)
    {
        OciFormat.Reference(reference);
        return new(Origin, "v2/" + Name + "/manifests/" + reference);
    }

    /// <summary>Builds the native blobs endpoint for a validated SHA-256 digest.</summary>
    public Uri BlobUri(string digest)
    {
        OciFormat.Digest(digest);
        return new(Origin, "v2/" + Name + "/blobs/" + digest);
    }

    internal Uri UploadsUri => new(Origin, "v2/" + Name + "/blobs/uploads/");

    [GeneratedRegex(@"\A[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RepositoryName();
}

/// <summary>An explicit repository/reference selection; a tag becomes immutable only after verified root acquisition.</summary>
public sealed record OciSelectedReference
{
    /// <summary>Creates a validated reference scoped to one repository.</summary>
    public OciSelectedReference(OciRepository repository, string reference)
    {
        ArgumentNullException.ThrowIfNull(repository);
        OciFormat.Reference(reference);
        Repository = repository;
        Reference = reference;
    }
    /// <summary>The selected repository locator.</summary>
    public OciRepository Repository { get; }
    /// <summary>The exact mutable tag or immutable SHA-256 selector.</summary>
    public string Reference { get; }
}
