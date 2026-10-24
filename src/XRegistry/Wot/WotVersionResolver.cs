// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Wot;

/// <summary>A registry-independent snapshot of a Thing Model Version.</summary>
public sealed record WotVersionSnapshot(string VersionId, string VersionOrigin, string ContentDigest, string? ModelVersion = null);

/// <summary>A registry-independent snapshot of a WoT Resource and its Versions.</summary>
public sealed record WotResourceSnapshot(string WotId, bool DefaultVersionSticky, string? DefaultVersionId,
    IReadOnlyList<WotVersionSnapshot> Versions);

/// <summary>A normalized WoT reference and its optional minimum version.</summary>
public sealed record WotReferenceRequest(string Kind, string Target, string? MinimumVersion = null);

/// <summary>A normalized TD type link.</summary>
public sealed record WotTypeLink(string Target, string? MinimumVersion = null);

/// <summary>Version information needed to derive floors for TD type links.</summary>
public sealed record WotDocumentVersionContext(string? ModelVersion, IReadOnlyList<WotTypeLink> TypeLinks);

/// <summary>An optional caller pin; at most one of digest and version identifier may be supplied.</summary>
public sealed record WotVersionPin(string? Digest = null, string? VersionId = null);

/// <summary>The exact Version selected for a WoT reference.</summary>
public sealed record WotResolutionResult(string WotId, string VersionId, string ContentDigest, string Origin);

/// <summary>The version assignment and resulting Core default for a Thing Model publication.</summary>
public sealed record WotPublicationAssignment(string VersionId, string VersionOrigin, string? DefaultVersionId);

/// <summary>A domain error produced by WoT version resolution or publication assignment.</summary>
public sealed class WotVersionException(string code) : Exception($"WoT version operation failed: {code}.")
{
    /// <summary>Gets the stable domain error code, such as <c>not-found</c> or <c>invalid-version</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>Resolves WoT references and Thing Model publication versions over caller-supplied Resource snapshots.</summary>
/// <remarks>This component has no dependency on registry storage, transport or document acquisition.</remarks>
public static class WotVersionResolver
{
    /// <summary>Resolves a reference to one versioned Resource snapshot.</summary>
    public static WotResolutionResult Resolve(IEnumerable<WotResourceSnapshot> resources, WotReferenceRequest reference,
        string origin, WotDocumentVersionContext? document = null, WotVersionPin? pin = null, bool allowPrerelease = false)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ValidatePin(pin);
        var resource = FindResource(resources, reference.Target);
        WotVersionSnapshot selected;

        if (pin?.Digest is not null)
        {
            var matches = resource.Versions.Where(version => string.Equals(version.ContentDigest, pin.Digest, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0)
            {
                throw new WotVersionException("pin-mismatch");
            }

            selected = Highest(matches);
        }
        else if (pin?.VersionId is not null)
        {
            selected = resource.Versions.FirstOrDefault(version => string.Equals(version.VersionId, pin.VersionId, StringComparison.Ordinal))
                ?? throw new WotVersionException("not-found");
        }
        else
        {
            var floor = GetFloor(reference, document);
            if (resource.DefaultVersionSticky)
            {
                selected = resource.Versions.FirstOrDefault(version => string.Equals(version.VersionId, resource.DefaultVersionId, StringComparison.Ordinal))
                    ?? throw new WotVersionException("not-found");
            }
            else
            {
                var eligible = resource.Versions.Where(version =>
                {
                    var parsed = ParseStoredVersion(version.VersionId);
                    return !parsed.IsPrerelease || allowPrerelease || floor?.IsPrerelease == true;
                }).ToList();
                if (eligible.Count == 0)
                {
                    throw new WotVersionException("unsatisfied");
                }

                selected = Highest(eligible);
            }

            if (floor is not null && ParseStoredVersion(selected.VersionId).CompareTo(floor) < 0)
            {
                throw new WotVersionException("unsatisfied");
            }
        }

        return new(resource.WotId, selected.VersionId, selected.ContentDigest, origin);
    }

    /// <summary>Assigns the version identifier and post-publication default for a Thing Model write.</summary>
    public static WotPublicationAssignment AssignPublishedVersion(IEnumerable<WotResourceSnapshot> resources,
        string target, string? suppliedVersionId = null, string? modelVersion = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var resource = FindResource(resources, target);
        if (suppliedVersionId is not null && !IsCoreVersionId(suppliedVersionId))
        {
            throw new WotVersionException("invalid-version");
        }

        string assigned;
        string origin;
        if (modelVersion is not null && WotSemanticVersion.HasValidBuildMetadata(modelVersion))
        {
            throw new WotVersionException("invalid-version");
        }

        if (modelVersion is not null && WotSemanticVersion.TryParse(modelVersion, out _))
        {
            assigned = modelVersion;
            origin = "authored";
        }
        else
        {
            if (modelVersion is not null && WotSemanticVersion.IsSemVerSyntax(modelVersion))
            {
                throw new WotVersionException("invalid-version");
            }

            var occupied = resource.Versions.Select(version => version.VersionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var number = 1;
            while (occupied.Contains($"0.0.0-{number}"))
            {
                number++;
            }

            assigned = $"0.0.0-{number}";
            origin = "generated";
        }

        if (suppliedVersionId is not null && !string.Equals(suppliedVersionId, assigned, StringComparison.Ordinal))
        {
            throw new WotVersionException("version-mismatch");
        }

        var defaultVersionId = resource.DefaultVersionSticky
            ? resource.DefaultVersionId
            : Highest([.. resource.Versions, new(assigned, origin, string.Empty)]).VersionId;
        return new(assigned, origin, defaultVersionId);
    }

    private static WotResourceSnapshot FindResource(IEnumerable<WotResourceSnapshot> resources, string target) =>
        resources.FirstOrDefault(resource => string.Equals(resource.WotId, target, StringComparison.Ordinal))
        ?? throw new WotVersionException("not-found");

    private static WotSemanticVersion? GetFloor(WotReferenceRequest reference, WotDocumentVersionContext? document)
    {
        string? floor = reference.MinimumVersion;
        if (reference.Kind == "type" && document is not null)
        {
            ArgumentNullException.ThrowIfNull(document.TypeLinks);
            var matches = document.TypeLinks.Where(link => string.Equals(link.Target, reference.Target, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || !string.Equals(matches[0].MinimumVersion, floor, StringComparison.Ordinal))
            {
                throw new ArgumentException("A type reference must project exactly one matching TD type link.", nameof(reference));
            }

            if (document.ModelVersion is not null && document.TypeLinks.Count > 1 && document.TypeLinks.Any(link => link.MinimumVersion is null))
            {
                throw new WotVersionException("ambiguous-floor");
            }

            foreach (var link in document.TypeLinks)
            {
                if (link.MinimumVersion is not null)
                {
                    _ = ParseFloor(link.MinimumVersion);
                }
            }

            if (floor is null && document.TypeLinks.Count == 1)
            {
                floor = document.ModelVersion;
            }
        }

        return floor is null ? null : ParseFloor(floor);
    }

    private static WotSemanticVersion ParseFloor(string text)
    {
        var parsed = ParseStoredVersion(text);
        if (IsGeneratedId(text))
        {
            throw new WotVersionException("invalid-version");
        }

        return parsed;
    }

    private static WotSemanticVersion ParseStoredVersion(string text)
    {
        if (!WotSemanticVersion.TryParse(text, out var parsed))
        {
            throw new WotVersionException("invalid-version");
        }

        return parsed!;
    }

    private static WotVersionSnapshot Highest(List<WotVersionSnapshot> versions)
    {
        var highest = versions[0];
        var highestVersion = ParseStoredVersion(highest.VersionId);
        foreach (var candidate in versions.Skip(1))
        {
            var candidateVersion = ParseStoredVersion(candidate.VersionId);
            if (candidateVersion.CompareTo(highestVersion) > 0)
            {
                highest = candidate;
                highestVersion = candidateVersion;
            }
        }

        return highest;
    }

    private static void ValidatePin(WotVersionPin? pin)
    {
        if (pin?.Digest is not null && pin.VersionId is not null)
        {
            throw new ArgumentException("A resolution request may have only one pin.", nameof(pin));
        }
    }

    private static bool IsCoreVersionId(string value) => value.Length is > 0 and <= 128 &&
        (char.IsAsciiLetterOrDigit(value[0]) || value[0] == '_') &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '~' or ':' or '@' or '-') &&
        value is not ("null" or "request");

    private static bool IsGeneratedId(string value)
    {
        const string prefix = "0.0.0-";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var number = value[prefix.Length..];
        return number.Length > 0 && number[0] != '0' && number.All(char.IsAsciiDigit);
    }
}
