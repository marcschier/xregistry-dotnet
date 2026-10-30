// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class WotIncrementalVersionResolverTests
{
    private const string Line = "urn:example:line:a";
    private const string OtherLine = "urn:example:line:b";

    [Test]
    [Arguments(WotReferenceTargetKind.ThingModel)]
    [Arguments(WotReferenceTargetKind.ThingDescription)]
    public async Task HighestRevisionUsesNumericOrderingWithinTheExactLineWithoutMajorCaps(WotReferenceTargetKind kind)
    {
        var resource = Resource(kind, Candidate("9"), Candidate("10"), Candidate("99", OtherLine),
            Candidate("184467440737095516160"));
        var result = WotVersionResolver.Resolve(resource, Request(kind) with
        {
            MinimumVersion = "10", ReferencingThingDescriptionModelVersion = "not-a-semver"
        });
        await Assert.That(result.VersionId).IsEqualTo("184467440737095516160");
        await Assert.That(result.Profile).IsEqualTo(WotVersionProfile.Incremental);
        await Assert.That(result.CompatibilityLine).IsEqualTo(Line);
        await Assert.That(result.Origin).IsEqualTo("origin-a");
        await Assert.That(result.ContentDigest).IsEqualTo("sha256:184467440737095516160");
    }

    [Test]
    [Arguments(null)]
    [Arguments("41")]
    [Arguments("42")]
    public async Task OptionalFloorIncludesTheExactBoundaryAndGeneratedRevisions(string? floor)
    {
        var result = WotVersionResolver.Resolve(Resource(Candidate("42")), Request() with { MinimumVersion = floor });
        await Assert.That(result.VersionId).IsEqualTo("42");
    }

    [Test]
    public async Task FloorImmediatelyAboveTheOnlyRevisionFailsWithoutAnotherLineFallback()
    {
        await Error(Resource(Candidate("42"), Candidate("100", OtherLine)),
            Request() with { MinimumVersion = "43" }, "unsatisfied");
    }

    [Test]
    [Arguments(null)]
    [Arguments("1")]
    public async Task UnpinnedSelectionAlwaysRequiresAnExplicitLine(string? floor)
    {
        await Error(Resource(Candidate("1")), Request() with { CompatibilityLine = null, MinimumVersion = floor },
            "compatibility-required");
    }

    [Test]
    [Arguments("")]
    [Arguments("relative")]
    [Arguments("urn:example:bad line")]
    public async Task MalformedLineSelectorsFailExplicitly(string line)
    {
        await Error(Resource(Candidate("1")), Request() with { CompatibilityLine = line }, "invalid-line");
    }

    [Test]
    public async Task LineIdentityIsCaseSensitiveAndLinelessRevisionsAreNotAutomaticCandidates()
    {
        await Error(Resource(Candidate("99", "urn:example:line:A"), Candidate("100", null)),
            Request(), "unsatisfied");
        var pinned = WotVersionResolver.Resolve(Resource(Candidate("100", null)),
            Request() with { VersionIdPin = "100" });
        await Assert.That(pinned.VersionId).IsEqualTo("100");
        await Assert.That(pinned.CompatibilityLine).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("01")]
    [Arguments("-1")]
    [Arguments("+1")]
    [Arguments("1.0")]
    [Arguments("1.0.0")]
    [Arguments(" 1")]
    [Arguments("1 ")]
    [Arguments("\u0661")]
    public async Task FloorsAndCapturedIdsMustBeCanonicalPositiveIntegers(string value)
    {
        await Error(Resource(Candidate("1")), Request() with { MinimumVersion = value }, "invalid-version");
        await Error(Resource(Candidate(value)), Request(), "invalid-version");
    }

    [Test]
    public async Task CoreLengthBoundaryIsAcceptedWithoutFixedWidthRollover()
    {
        var maximum = new string('9', 128);
        var result = WotVersionResolver.Resolve(Resource(Candidate(maximum)),
            Request() with { MinimumVersion = maximum });
        await Assert.That(result.VersionId).IsEqualTo(maximum);
        await Error(Resource(Candidate(maximum)), Request() with { MinimumVersion = maximum + "9" }, "invalid-version");
        await Error(Resource(Candidate(maximum + "9")), Request(), "invalid-version");
    }

    [Test]
    public async Task StickyDefaultMustMeetTheLineAndFloorRatherThanFallingForward()
    {
        var resource = Resource(Candidate("41"), Candidate("42"), Candidate("99", OtherLine)) with
        {
            DefaultVersionId = "41", DefaultVersionSticky = true
        };
        var selected = WotVersionResolver.Resolve(resource, Request() with { MinimumVersion = "41" });
        await Assert.That(selected.VersionId).IsEqualTo("41");
        await Error(resource, Request() with { MinimumVersion = "42" }, "unsatisfied");
        await Error(resource with { DefaultVersionId = "99" }, Request(), "unsatisfied");
        await Error(resource with { DefaultVersionId = "missing" }, Request(), "not-found");
        await Error(resource with { DefaultVersionId = null }, Request(), "not-found");
    }

    [Test]
    public async Task NonStickyCoreDefaultDoesNotOverrideNumericConstrainedSelection()
    {
        var resource = Resource(Candidate("9"), Candidate("10")) with
        {
            DefaultVersionId = "9", DefaultVersionSticky = false
        };
        await Assert.That(WotVersionResolver.SelectVersionId(resource, Request())).IsEqualTo("10");
    }

    [Test]
    public async Task ExplicitAndDigestPinsOverrideSelectionSyntaxButDigestTiesUseNumericOrder()
    {
        var resource = Resource(Candidate("9", null) with { ContentDigest = "sha256:same" },
            Candidate("10", OtherLine) with { ContentDigest = "sha256:same" }, Candidate("100")) with
        {
            DefaultVersionId = "missing", DefaultVersionSticky = true
        };
        var request = Request() with { MinimumVersion = "not numeric", CompatibilityLine = "not a URI" };
        var explicitPin = WotVersionResolver.Resolve(resource, request with { VersionIdPin = "9" });
        var digestPin = WotVersionResolver.Resolve(resource, request with { DigestPin = "sha256:same" });
        await Assert.That(explicitPin.VersionId).IsEqualTo("9");
        await Assert.That(explicitPin.CompatibilityLine).IsNull();
        await Assert.That(digestPin.VersionId).IsEqualTo("10");
        await Assert.That(digestPin.CompatibilityLine).IsEqualTo(OtherLine);
        await Error(resource, request with { DigestPin = "sha256:missing" }, "pin-mismatch");
        await Error(resource, request with { VersionIdPin = "missing" }, "not-found");
        await Error(resource, request with { VersionIdPin = "9", DigestPin = "sha256:same" }, "invalid-pin");
    }

    [Test]
    public async Task TypeLinksDoNotInheritSemVerFloorsOrMultipleLinkAmbiguity()
    {
        var request = Request() with
        {
            Operator = WotReferenceOperator.ThingModelTypeLink,
            ReferencingThingDescriptionModelVersion = "99.0.0",
            ThingModelTypeLinkCount = 3, AllThingModelTypeLinksHaveMinimumVersion = false
        };
        await Assert.That(WotVersionResolver.Resolve(Resource(Candidate("1")), request).VersionId).IsEqualTo("1");
    }

    [Test]
    public async Task ContainingIncrementalRevisionUsesItsCapturedNumberAndLineNotItsAuthoredString()
    {
        var containing = Candidate("42") with
        {
            Profile = WotVersionProfile.Incremental, AuthoredVersion = "arbitrary"
        };
        var request = Request() with { IsDocumentLocal = true, ContainingRevision = containing, MinimumVersion = "42" };
        var result = WotVersionResolver.Resolve(null, request);
        await Assert.That(result.VersionId).IsEqualTo("42");
        await Assert.That(result.Profile).IsEqualTo(WotVersionProfile.Incremental);
        await Assert.That(result.CompatibilityLine).IsEqualTo(Line);
        await Error(null, request with { MinimumVersion = "43" }, "unsatisfied");
        await Error(null, request with { CompatibilityLine = OtherLine }, "unsatisfied");
        await Error(null, request with { CompatibilityLine = null }, "compatibility-required");
        await Error(null, request with { VersionIdPin = "42" }, "invalid-pin");
    }

    [Test]
    public async Task UnknownProfilesAndConflictingKindsNeverUseSemVerFallback()
    {
        await Error(Resource(Candidate("1")) with { Profile = (WotVersionProfile)99 }, Request(), "unknown-profile");
        await Error(null, Request() with
        {
            IsDocumentLocal = true, ContainingRevision = Candidate("1") with { Profile = (WotVersionProfile)99 }
        }, "unknown-profile");
        await Error(Resource(Candidate("1")), Request() with
        {
            MinimumVersion = "1", MinimumVersionKind = WotMinimumVersionKind.ThingDescription
        }, "invalid-floor");
        await Error(Resource(Candidate("1")), Request() with
        {
            Operator = WotReferenceOperator.ThingDescription
        }, "invalid-floor");
    }

    [Test]
    public async Task SemVerTargetsRejectLineSelectionExceptForRawPins()
    {
        var resource = Resource(Candidate("1.0.0", null)) with { Profile = WotVersionProfile.SemVer };
        await Error(resource, Request(), "invalid-line");
        var pinned = WotVersionResolver.Resolve(resource, Request() with { VersionIdPin = "1.0.0" });
        await Assert.That(pinned.Profile).IsEqualTo(WotVersionProfile.SemVer);
        await Assert.That(pinned.VersionId).IsEqualTo("1.0.0");
    }

    [Test]
    public async Task EmptyDuplicateInvalidLineAndMissingProvenanceStatesCannotProduceSuccess()
    {
        await Error(null, Request(), "not-found");
        await Error(Resource(), Request(), "unsatisfied");
        await Error(Resource(Candidate("1"), Candidate("1")), Request(), "invalid-version");
        await Error(Resource(Candidate("1", "relative")), Request(), "invalid-line");
        var missingProvenance = Resource(Candidate("1") with { ContentDigest = "", Origin = "" });
        await Assert.That(WotVersionResolver.SelectVersionId(missingProvenance, Request())).IsEqualTo("1");
        await Error(missingProvenance, Request(), "invalid-version");
    }

    private static WotVersionCandidate Candidate(string id, string? line = Line) =>
        new(id, "sha256:" + id, "origin-a") { CompatibilityLine = line };

    private static WotVersionResource Resource(params WotVersionCandidate[] versions) =>
        Resource(WotReferenceTargetKind.ThingModel, versions);

    private static WotVersionResource Resource(WotReferenceTargetKind kind, params WotVersionCandidate[] versions) =>
        new("urn:example:pump", kind, null, false, versions) { Profile = WotVersionProfile.Incremental };

    private static WotVersionResolutionRequest Request(WotReferenceTargetKind kind = WotReferenceTargetKind.ThingModel) =>
        new("urn:example:pump", kind, kind == WotReferenceTargetKind.ThingModel ?
            WotReferenceOperator.ThingModel : WotReferenceOperator.ThingDescription) { CompatibilityLine = Line };

    private static async Task Error(WotVersionResource? resource, WotVersionResolutionRequest request, string code)
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resource, request)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected " + code);
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
    }
}
