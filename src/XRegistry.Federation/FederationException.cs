// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Federation;

/// <summary>Transport-independent read-only federation outcomes.</summary>
public enum FederationErrorCode
{
    /// <summary>No selected supported access method.</summary>
    UnsupportedBinding,
    /// <summary>An unsupported Core, binding, or package version.</summary>
    UnsupportedVersion,
    /// <summary>An unsupported operation, representation, or parameter.</summary>
    UnsupportedOperation,
    /// <summary>The requested entity, Version, root, or match is absent.</summary>
    NotFound,
    /// <summary>Competing results require explicit disambiguation.</summary>
    Ambiguous,
    /// <summary>Access, authentication, trust, or traversal was denied.</summary>
    PolicyDenied,
    /// <summary>Exact size, digest, or authenticated identity verification failed.</summary>
    IntegrityError,
    /// <summary>Malformed or contradictory catalog, metadata, or containment data.</summary>
    InvalidPackage,
    /// <summary>The data cannot be read as the selected state.</summary>
    InconsistentSnapshot,
    /// <summary>A finite read or traversal budget was exhausted.</summary>
    LimitExceeded,
    /// <summary>The selected backing source is temporarily unavailable.</summary>
    Unavailable,
}

/// <summary>A failed federation operation, optionally retaining a safe Core diagnostic.</summary>
public sealed class FederationException : Exception
{
    /// <summary>Creates a failure. Messages and diagnostics must not contain credentials.</summary>
    public FederationException(FederationErrorCode code, string message, string? diagnostic = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Diagnostic = diagnostic;
    }

    /// <summary>The abstract federation outcome.</summary>
    public FederationErrorCode Code { get; }

    /// <summary>An optional underlying Core or transport diagnostic, without credentials.</summary>
    public string? Diagnostic { get; }

    /// <summary>The original HTTP status when the failure came from an HTTP source.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>Owned underlying Core problem details when supplied by an HTTP source; not safe for public logging.</summary>
    public JsonElement HttpProblemDetails { get; init; }
}
