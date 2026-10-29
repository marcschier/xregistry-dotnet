// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;

namespace XRegistry.Models;

/// <summary>Identifies the kind of WoT Resource selected by a reference.</summary>
public enum WotReferenceTargetKind
{
    /// <summary>A Thing Model Resource.</summary>
    ThingModel,
    /// <summary>A Thing Description Resource.</summary>
    ThingDescription
}

/// <summary>Identifies the reference operator that determines target-kind and floor rules.</summary>
public enum WotReferenceOperator
{
    /// <summary>A Thing Model reference, including extension and composition references.</summary>
    ThingModel,
    /// <summary>A Thing Description reference, including extension and composition references.</summary>
    ThingDescription,
    /// <summary>A TD link with relation type that references a Thing Model.</summary>
    ThingModelTypeLink
}

/// <summary>Identifies the WoT kind named by an explicit minimum-version term.</summary>
public enum WotMinimumVersionKind
{
    /// <summary>A Thing Model minimum version.</summary>
    ThingModel,
    /// <summary>A Thing Description minimum version.</summary>
    ThingDescription
}

/// <summary>A Version candidate and the byte provenance returned by a WoT resolver.</summary>
public sealed record WotVersionCandidate(
    string VersionId,
    string ContentDigest,
    string Origin,
    string? AuthoredVersion = null);

/// <summary>The selected WoT Resource and its captured Core Version state.</summary>
public sealed record WotVersionResource(
    string WotId,
    WotReferenceTargetKind Kind,
    string? DefaultVersionId,
    bool DefaultVersionSticky,
    IReadOnlyList<WotVersionCandidate> Versions);

/// <summary>Inputs to the WoT Version selection algorithm after reference parsing and Resource lookup.</summary>
public sealed record WotVersionResolutionRequest(
    string TargetWotId,
    WotReferenceTargetKind TargetKind,
    WotReferenceOperator Operator,
    string? MinimumVersion = null,
    WotMinimumVersionKind? MinimumVersionKind = null,
    string? ReferencingThingDescriptionModelVersion = null,
    int ThingModelTypeLinkCount = 1,
    bool AllThingModelTypeLinksHaveMinimumVersion = true,
    string? VersionIdPin = null,
    string? DigestPin = null,
    bool AllowPrerelease = false,
    bool IsDocumentLocal = false,
    WotVersionCandidate? ContainingRevision = null);

/// <summary>The immutable identity and byte provenance of one resolved WoT Version.</summary>
public sealed record WotResolvedVersion(
    string WotId,
    string VersionId,
    string ContentDigest,
    string Origin);

/// <summary>Applies WoT floor, pin, sticky-default and prerelease rules to a selected Resource.</summary>
/// <remarks>
/// The caller resolves the reference URI, selects one Registry and Group scope, and supplies the
/// captured Resource Versions. This class does not acquire documents or resolve Registry bindings.
/// </remarks>
public static class WotVersionResolver
{
    /// <summary>Selects the Version required by a WoT reference from one captured Resource.</summary>
    /// <param name="resource">The Resource selected by the caller, or null when lookup found none.</param>
    /// <param name="request">The parsed reference and caller selection options.</param>
    /// <returns>The selected WoT identity, Version id, content digest and origin.</returns>
    /// <exception cref="RegistryException">A specified lookup, pin, floor or selection rule cannot be satisfied.</exception>
    public static WotResolvedVersion Resolve(WotVersionResource? resource, WotVersionResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        if (request.IsDocumentLocal)
        {
            return ResolveLocal(request);
        }

        if (resource is null || resource.Kind != request.TargetKind ||
            !StringComparer.Ordinal.Equals(resource.WotId, request.TargetWotId))
        {
            throw Error("not-found", "The requested WoT Resource does not exist in the selected Registry scope.");
        }

        ValidateResource(resource);

        if (request.VersionIdPin is not null && request.DigestPin is not null)
        {
            throw Error("invalid-pin", "A request cannot contain both a versionid pin and a content-digest pin.");
        }

        if (request.VersionIdPin is { } versionPin)
        {
            var pinned = resource.Versions.FirstOrDefault(version =>
                StringComparer.Ordinal.Equals(version.VersionId, versionPin));
            return pinned is null
                ? throw Error("not-found", "The pinned Version does not exist in the selected Resource.")
                : Result(resource.WotId, pinned);
        }

        if (request.DigestPin is { } digestPin)
        {
            var matching = resource.Versions.Where(version =>
                StringComparer.Ordinal.Equals(version.ContentDigest, digestPin)).ToArray();
            if (matching.Length == 0)
            {
                throw Error("pin-mismatch", "The selected Resource has no Version with the pinned content digest.");
            }

            var selected = Highest(matching);
            return Result(resource.WotId, selected);
        }

        var floor = DeriveFloor(request);
        if (resource.DefaultVersionSticky)
        {
            var sticky = resource.DefaultVersionId is null ? null : resource.Versions.FirstOrDefault(version =>
                StringComparer.Ordinal.Equals(version.VersionId, resource.DefaultVersionId));
            if (sticky is null)
            {
                throw Error("not-found", "The sticky default Version is absent from the selected Resource.");
            }

            var stickyVersion = ParseVersion(sticky.VersionId);
            CheckFloor(stickyVersion, floor, request.TargetKind);
            return Result(resource.WotId, sticky);
        }

        var eligible = new List<(WotVersionCandidate Candidate, WotSemanticVersion Version)>();
        foreach (var candidate in resource.Versions)
        {
            var version = ParseVersion(candidate.VersionId);
            if (version.IsPrerelease && floor?.IsPrerelease != true && !request.AllowPrerelease)
            {
                continue;
            }

            try
            {
                CheckFloor(version, floor, request.TargetKind);
                eligible.Add((candidate, version));
            }
            catch (RegistryException exception) when (exception.Diagnostic.Code == "unsatisfied")
            {
            }
        }

        if (eligible.Count == 0)
        {
            throw Error("unsatisfied", "The selected Resource has no Version that satisfies the reference.");
        }

        var best = eligible[0];
        foreach (var candidate in eligible.Skip(1))
        {
            if (candidate.Version.CompareTo(best.Version) > 0 ||
                candidate.Version.CompareTo(best.Version) == 0 &&
                StringComparer.Ordinal.Compare(candidate.Candidate.VersionId, best.Candidate.VersionId) > 0)
            {
                best = candidate;
            }
        }

        return Result(resource.WotId, best.Candidate);
    }

    private static void ValidateRequest(WotVersionResolutionRequest request)
    {
        if (!Enum.IsDefined(request.TargetKind) || !Enum.IsDefined(request.Operator) ||
            request.MinimumVersionKind is { } minimumKind && !Enum.IsDefined(minimumKind))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.TargetWotId is null)
        {
            throw new ArgumentException("A target WoT identifier is required.", nameof(request));
        }

        var expectedKind = request.Operator == WotReferenceOperator.ThingDescription
            ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel;
        if (request.TargetKind != expectedKind)
        {
            throw Error("invalid-floor", "The reference operator does not identify the selected WoT target kind.");
        }

        if (request.Operator == WotReferenceOperator.ThingModelTypeLink &&
            request.ThingModelTypeLinkCount < 1)
        {
            throw Error("invalid-floor", "A Thing Model type-link count must be positive.");
        }
    }

    private static void ValidateResource(WotVersionResource resource)
    {
        if (!Enum.IsDefined(resource.Kind) || resource.Versions is null)
        {
            throw Error("invalid-version", "The selected WoT Resource has an invalid captured version set.");
        }

        var versionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in resource.Versions)
        {
            if (candidate is null || string.IsNullOrEmpty(candidate.VersionId) ||
                !versionIds.Add(candidate.VersionId) ||
                !WotSemanticVersion.TryParse(candidate.VersionId, out _))
            {
                throw Error("invalid-version", "The selected WoT Resource contains an invalid or duplicate Version id.");
            }
        }
    }

    private static WotResolvedVersion ResolveLocal(WotVersionResolutionRequest request)
    {
        if (request.VersionIdPin is not null || request.DigestPin is not null)
        {
            throw Error("invalid-pin", "A document-local reference cannot carry a Registry Version pin.");
        }

        var revision = request.ContainingRevision ??
            throw Error("not-found", "The containing WoT revision is unavailable.");
        if (request.MinimumVersion is not null)
        {
            ValidateMinimumVersionKind(request);
            var floor = ParseFloor(request.MinimumVersion);
            if (revision.AuthoredVersion is null)
            {
                throw Error("unsatisfied", "The containing revision has no authored version that can satisfy the floor.");
            }

            var authoredVersion = ParseVersion(revision.AuthoredVersion);
            CheckFloor(authoredVersion, floor, request.TargetKind);
        }

        return Result(request.TargetWotId, revision);
    }

    private static WotSemanticVersion? DeriveFloor(WotVersionResolutionRequest request)
    {
        if (request.Operator == WotReferenceOperator.ThingModelTypeLink &&
            request.ReferencingThingDescriptionModelVersion is { } modelVersion &&
            request.ThingModelTypeLinkCount > 1 &&
            !request.AllThingModelTypeLinksHaveMinimumVersion)
        {
            throw Error("ambiguous-floor",
                "Every Thing Model type link requires tm:minVersion when a TD has several type links and version.model.");
        }

        if (request.MinimumVersion is not null)
        {
            ValidateMinimumVersionKind(request);
        }

        var explicitFloor = request.MinimumVersion is null ? null : ParseFloor(request.MinimumVersion);
        if (request.Operator != WotReferenceOperator.ThingModelTypeLink ||
            request.ReferencingThingDescriptionModelVersion is null)
        {
            return explicitFloor;
        }

        var implicitFloor = ParseFloor(request.ReferencingThingDescriptionModelVersion);
        if (explicitFloor is null || implicitFloor.CompareTo(explicitFloor) > 0)
        {
            return implicitFloor;
        }

        return explicitFloor;
    }

    private static void ValidateMinimumVersionKind(WotVersionResolutionRequest request)
    {
        if (request.MinimumVersionKind != (request.TargetKind == WotReferenceTargetKind.ThingModel
                ? WotMinimumVersionKind.ThingModel : WotMinimumVersionKind.ThingDescription))
        {
            throw Error("invalid-floor", "The minimum-version term does not match the referenced document kind.");
        }
    }

    private static WotSemanticVersion ParseFloor(string value)
    {
        var version = ParseVersion(value);
        if (version.IsGeneratedId)
        {
            throw Error("invalid-version", "A generated 0.0.0-N Version id cannot be used as a minimum-version floor.");
        }

        return version;
    }

    private static WotSemanticVersion ParseVersion(string value)
    {
        if (!WotSemanticVersion.TryParse(value, out var version))
        {
            throw Error("invalid-version", "The WoT Version is not SemVer 2.0.0 without build metadata.");
        }

        return version;
    }

    private static void CheckFloor(WotSemanticVersion version, WotSemanticVersion? floor,
        WotReferenceTargetKind targetKind)
    {
        if (floor is null)
        {
            return;
        }

        if (version.CompareTo(floor) < 0 ||
            targetKind == WotReferenceTargetKind.ThingDescription &&
            version.Major >= floor.Major + BigInteger.One)
        {
            throw Error("unsatisfied", "The selected Version is below the floor or outside the Thing Description major range.");
        }
    }

    private static WotVersionCandidate Highest(WotVersionCandidate[] versions)
    {
        var selected = versions[0];
        var selectedVersion = ParseVersion(selected.VersionId);
        foreach (var candidate in versions.Skip(1))
        {
            var parsed = ParseVersion(candidate.VersionId);
            var comparison = parsed.CompareTo(selectedVersion);
            if (comparison > 0 || comparison == 0 &&
                StringComparer.Ordinal.Compare(candidate.VersionId, selected.VersionId) > 0)
            {
                selected = candidate;
                selectedVersion = parsed;
            }
        }

        return selected;
    }

    private static WotResolvedVersion Result(string wotId, WotVersionCandidate candidate)
    {
        if (string.IsNullOrEmpty(candidate.ContentDigest) || string.IsNullOrEmpty(candidate.Origin))
        {
            throw Error("invalid-version", "The selected Version has no content digest or byte origin.");
        }

        return new(wotId, candidate.VersionId, candidate.ContentDigest, candidate.Origin);
    }

    private static RegistryException Error(string code, string message) => new(new(code, "", message));

    private sealed class WotSemanticVersion : IComparable<WotSemanticVersion>
    {
        private readonly string[]? _prerelease;

        private WotSemanticVersion(BigInteger major, BigInteger minor, BigInteger patch, string[]? prerelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            _prerelease = prerelease;
        }

        internal BigInteger Major { get; }
        private BigInteger Minor { get; }
        private BigInteger Patch { get; }
        internal bool IsPrerelease => _prerelease is not null;
        internal bool IsGeneratedId => Major.IsZero && Minor.IsZero && Patch.IsZero &&
            _prerelease is [{ } identifier] && IsNumeric(identifier);

        internal static bool TryParse(string value, out WotSemanticVersion version)
        {
            version = null!;
            if (value.Length is 0 or > 128 || value.Contains('+'))
            {
                return false;
            }

            var sections = value.Split('-', 2);
            var numbers = sections[0].Split('.');
            if (numbers.Length != 3 || numbers.Any(static number =>
                number.Length == 0 || !number.All(char.IsAsciiDigit) ||
                number.Length > 1 && number[0] == '0'))
            {
                return false;
            }

            string[]? prerelease = null;
            if (sections.Length == 2)
            {
                prerelease = sections[1].Split('.');
                if (prerelease.Any(static identifier => identifier.Length == 0 ||
                    !identifier.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-') ||
                    IsNumeric(identifier) && identifier.Length > 1 && identifier[0] == '0'))
                {
                    return false;
                }
            }

            version = new(
                BigInteger.Parse(numbers[0], CultureInfo.InvariantCulture),
                BigInteger.Parse(numbers[1], CultureInfo.InvariantCulture),
                BigInteger.Parse(numbers[2], CultureInfo.InvariantCulture),
                prerelease);
            return true;
        }

        public int CompareTo(WotSemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var comparison = Major.CompareTo(other.Major);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Minor.CompareTo(other.Minor);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = Patch.CompareTo(other.Patch);
            if (comparison != 0)
            {
                return comparison;
            }

            if (_prerelease is null || other._prerelease is null)
            {
                return _prerelease is null ? other._prerelease is null ? 0 : 1 : -1;
            }

            for (var index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
            {
                var left = _prerelease[index];
                var right = other._prerelease[index];
                var numericLeft = IsNumeric(left);
                var numericRight = IsNumeric(right);
                comparison = numericLeft && numericRight
                    ? BigInteger.Parse(left, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(right, CultureInfo.InvariantCulture))
                    : numericLeft != numericRight ? numericLeft ? -1 : 1 : StringComparer.Ordinal.Compare(left, right);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return _prerelease.Length.CompareTo(other._prerelease.Length);
        }

        private static bool IsNumeric(string value) =>
            value.Length != 0 && value.All(char.IsAsciiDigit);
    }
}
