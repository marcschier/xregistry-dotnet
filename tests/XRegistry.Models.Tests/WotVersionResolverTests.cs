// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class WotVersionResolverTests
{
    [Test]
    public async Task VersionSelectionDoesNotClaimUnacquiredByteProvenance()
    {
        var resource = Resource(("1.0.0", ""));
        var request = Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel);
        await Assert.That(WotVersionResolver.SelectVersionId(resource, request)).IsEqualTo("1.0.0");
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resource, request))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected missing byte provenance.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    public async Task SelectsHighestStableThingModelAtOrAboveItsFloor()
    {
        var result = WotVersionResolver.Resolve(Resource(
            ("1.0.0", "sha256:a"),
            ("1.4.0-rc.1", "sha256:b"),
            ("1.3.0", "sha256:c"),
            ("2.0.0", "sha256:d")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "1.1.0", minimumVersionKind: WotMinimumVersionKind.ThingModel));

        await Assert.That(result.VersionId).IsEqualTo("2.0.0");
    }

    [Test]
    public async Task ThingDescriptionFloorCapsSelectionAtTheNextMajor()
    {
        var result = WotVersionResolver.Resolve(Resource(WotReferenceTargetKind.ThingDescription,
            ("2.2.0", "sha256:a"),
            ("3.0.0", "sha256:b")),
            Request(WotReferenceTargetKind.ThingDescription, WotReferenceOperator.ThingDescription,
                minimumVersion: "2.1.0", minimumVersionKind: WotMinimumVersionKind.ThingDescription));

        await Assert.That(result.VersionId).IsEqualTo("2.2.0");
    }

    [Test]
    public async Task PinsBypassFloorsAndDigestPinsChooseTheHighestMatchingVersion()
    {
        var resource = Resource(
            ("1.0.0", "sha256:same"),
            ("2.0.0", "sha256:same"),
            ("3.0.0", "sha256:other"));

        var versionPin = WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "3.0.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                versionIdPin: "1.0.0"));
        var digestPin = WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "3.0.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                digestPin: "sha256:same"));

        await Assert.That(versionPin.VersionId).IsEqualTo("1.0.0");
        await Assert.That(digestPin.VersionId).IsEqualTo("2.0.0");
    }

    [Test]
    public async Task MultipleThingModelTypeLinksRequireEveryExplicitFloor()
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(Resource(("1.0.0", "sha256:a")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                referencingThingDescriptionModelVersion: "2.0.0", thingModelTypeLinkCount: 2,
                allThingModelTypeLinksHaveMinimumVersion: false))).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected an ambiguous-floor diagnostic.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("ambiguous-floor");
    }

    [Test]
    public async Task StickyDefaultMustSatisfyTheDerivedFloor()
    {
        var resource = ResourceWithDefault(WotReferenceTargetKind.ThingModel, "1.0.0", true,
            ("1.0.0", "sha256:a"),
            ("2.0.0", "sha256:b"));

        var failure = await Assert.That(() => WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "1.1.0", minimumVersionKind: WotMinimumVersionKind.ThingModel)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an unsatisfied-floor diagnostic.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    public async Task PrereleaseSelectionRequiresOptInUnlessTheFloorIsPrerelease()
    {
        var resource = Resource(
            ("1.9.0", "sha256:a"),
            ("2.0.0-rc.1", "sha256:b"),
            ("2.0.0-beta.2", "sha256:c"));
        var stable = WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel));
        var optedIn = WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, allowPrerelease: true));
        var prereleaseFloor = WotVersionResolver.Resolve(Resource(("2.0.0-rc.1", "sha256:b")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "2.0.0-beta.1", minimumVersionKind: WotMinimumVersionKind.ThingModel));

        await Assert.That(stable.VersionId).IsEqualTo("1.9.0");
        await Assert.That(optedIn.VersionId).IsEqualTo("2.0.0-rc.1");
        await Assert.That(prereleaseFloor.VersionId).IsEqualTo("2.0.0-rc.1");
    }

    [Test]
    public async Task LocalReferenceUsesContainingRevisionAuthoredVersion()
    {
        var containing = new WotVersionCandidate("0.0.0-1", "sha256:local", "registry-a", "2.1.0");
        var result = WotVersionResolver.Resolve(null,
            Request(WotReferenceTargetKind.ThingDescription, WotReferenceOperator.ThingDescription,
                minimumVersion: "2.0.0", minimumVersionKind: WotMinimumVersionKind.ThingDescription,
                isDocumentLocal: true, containingRevision: containing));

        await Assert.That(result.VersionId).IsEqualTo("0.0.0-1");
        await Assert.That(result.Origin).IsEqualTo("registry-a");
    }

    [Test]
    public async Task GeneratedVersionIdCannotBeUsedAsFloor()
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(Resource(("1.0.0", "sha256:a")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "0.0.0-1", minimumVersionKind: WotMinimumVersionKind.ThingModel)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an invalid-version diagnostic.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    public async Task MissingResourceAndDigestPinReturnTheirSpecificDiagnostics()
    {
        var missing = await Assert.That(() => WotVersionResolver.Resolve(null,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a missing-resource diagnostic.");
        var pinMismatch = await Assert.That(() => WotVersionResolver.Resolve(Resource(("1.0.0", "sha256:a")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, digestPin: "sha256:missing")))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a digest-pin diagnostic.");

        await Assert.That(missing.Diagnostic.Code).IsEqualTo("not-found");
        await Assert.That(pinMismatch.Diagnostic.Code).IsEqualTo("pin-mismatch");
    }

    [Test]
    public async Task StickyDefaultWinsAndVersionPinOverridesStickyDefaultAndFloor()
    {
        var resource = ResourceWithDefault(WotReferenceTargetKind.ThingModel, "1.1.0", true,
            ("1.0.0", "sha256:a"),
            ("1.1.0", "sha256:b"),
            ("1.2.0", "sha256:c"));
        var sticky = WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "1.0.0", minimumVersionKind: WotMinimumVersionKind.ThingModel));
        var pinned = WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "9.0.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                versionIdPin: "1.0.0"));

        await Assert.That(sticky.VersionId).IsEqualTo("1.1.0");
        await Assert.That(pinned.VersionId).IsEqualTo("1.0.0");
    }

    [Test]
    public async Task ThingModelTypeLinkUsesTheGreaterImplicitAndExplicitFloor()
    {
        var resource = Resource(
            ("1.0.0", "sha256:a"),
            ("1.1.0", "sha256:b"),
            ("1.2.0", "sha256:c"));
        var implicitOnly = WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                referencingThingDescriptionModelVersion: "1.1.0"));
        var selected = WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                minimumVersion: "1.0.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                referencingThingDescriptionModelVersion: "1.1.0"));

        await Assert.That(implicitOnly.VersionId).IsEqualTo("1.2.0");
        await Assert.That(selected.VersionId).IsEqualTo("1.2.0");
    }

    [Test]
    public async Task StickyTypeLinkMustMeetTheGreaterImplicitFloor()
    {
        var resource = ResourceWithDefault(WotReferenceTargetKind.ThingModel, "1.0.0", true,
            ("1.0.0", "sha256:a"),
            ("1.1.0", "sha256:b"));
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                minimumVersion: "1.0.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                referencingThingDescriptionModelVersion: "1.1.0")))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the sticky choice to fail.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    public async Task NoEligibleVersionDoesNotDowngradeBelowTheFloor()
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(Resource(
                ("1.0.0", "sha256:a"), ("1.1.0", "sha256:b")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "1.2.0")))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an unsatisfied-floor diagnostic.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    public async Task MultipleTypeLinksRaiseEveryExplicitFloorToTheTdModelVersion()
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(Resource(
                ("1.0.0", "sha256:a"), ("1.1.0", "sha256:b")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                minimumVersion: "1.0.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                referencingThingDescriptionModelVersion: "1.2.0", thingModelTypeLinkCount: 2)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the implicit floor to apply.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    public async Task VersionPinBypassesAmbiguousTypeLinksAndInvalidImplicitFloor()
    {
        var selected = WotVersionResolver.Resolve(Resource(("1.1.0", "sha256:a")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                referencingThingDescriptionModelVersion: "not-semver", thingModelTypeLinkCount: 2,
                allThingModelTypeLinksHaveMinimumVersion: false, versionIdPin: "1.1.0"));

        await Assert.That(selected.VersionId).IsEqualTo("1.1.0");
    }

    [Test]
    public async Task UnflooredThingDescriptionReferenceCanSelectAcrossMajorVersions()
    {
        var selected = WotVersionResolver.Resolve(Resource(WotReferenceTargetKind.ThingDescription,
            ("1.9.0", "sha256:a"), ("2.1.0", "sha256:b")),
            Request(WotReferenceTargetKind.ThingDescription, WotReferenceOperator.ThingDescription));

        await Assert.That(selected.VersionId).IsEqualTo("2.1.0");
    }

    [Test]
    public async Task MajorZeroThingDescriptionFloorCapsSelectionBelowOne()
    {
        var selected = WotVersionResolver.Resolve(Resource(WotReferenceTargetKind.ThingDescription,
            ("0.9.4", "sha256:a"), ("1.0.0", "sha256:b")),
            Request(WotReferenceTargetKind.ThingDescription, WotReferenceOperator.ThingDescription,
                minimumVersion: "0.8.0", minimumVersionKind: WotMinimumVersionKind.ThingDescription));

        await Assert.That(selected.VersionId).IsEqualTo("0.9.4");
    }

    [Test]
    public async Task LocalThingModelReferenceUsesContainingRevisionAndEnforcesItsFloor()
    {
        var containing = new WotVersionCandidate("1.2.0", "sha256:local", "registry-a", "1.2.0");
        var selected = WotVersionResolver.Resolve(null,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "1.2.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                isDocumentLocal: true, containingRevision: containing));
        var unsatisfied = await Assert.That(() => WotVersionResolver.Resolve(null,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "1.3.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                isDocumentLocal: true, containingRevision: containing)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the containing revision floor to fail.");

        await Assert.That(selected.VersionId).IsEqualTo("1.2.0");
        await Assert.That(unsatisfied.Diagnostic.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    public async Task LocalThingDescriptionReferenceEnforcesItsMajorCapAndRequiresAuthoredVersionForFloor()
    {
        var nextMajor = await Assert.That(() => WotVersionResolver.Resolve(null,
            Request(WotReferenceTargetKind.ThingDescription, WotReferenceOperator.ThingDescription,
                minimumVersion: "1.1.0", minimumVersionKind: WotMinimumVersionKind.ThingDescription,
                isDocumentLocal: true,
                containingRevision: new("2.0.0", "sha256:local", "registry-a", "2.0.0"))))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the TD major cap to fail.");
        var missingAuthoredVersion = await Assert.That(() => WotVersionResolver.Resolve(null,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "1.1.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                isDocumentLocal: true,
                containingRevision: new("0.0.0-1", "sha256:local", "registry-a"))))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a missing authored version failure.");
        var invalidAuthoredVersion = await Assert.That(() => WotVersionResolver.Resolve(null,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "1.1.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
                isDocumentLocal: true,
                containingRevision: new("0.0.0-1", "sha256:local", "registry-a", "v1.2"))))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an invalid authored version failure.");

        await Assert.That(nextMajor.Diagnostic.Code).IsEqualTo("unsatisfied");
        await Assert.That(missingAuthoredVersion.Diagnostic.Code).IsEqualTo("unsatisfied");
        await Assert.That(invalidAuthoredVersion.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    public async Task ThingModelFloorTermOnThingDescriptionReferenceIsInvalid()
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(
            Resource(WotReferenceTargetKind.ThingDescription, ("1.2.0", "sha256:a")),
            Request(WotReferenceTargetKind.ThingDescription, WotReferenceOperator.ThingDescription,
                minimumVersion: "1.2.0", minimumVersionKind: WotMinimumVersionKind.ThingModel)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an invalid-floor diagnostic.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-floor");
    }

    [Test]
    public async Task GeneratedPrereleaseVersionsUseNumericOrderingWhenEnabled()
    {
        var selected = WotVersionResolver.Resolve(Resource(
            ("0.0.0-2", "sha256:a"), ("0.0.0-10", "sha256:b")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, allowPrerelease: true));

        await Assert.That(selected.VersionId).IsEqualTo("0.0.0-10");
    }

    [Test]
    [Arguments("")]
    [Arguments("1.0")]
    [Arguments("01.2.3")]
    [Arguments("1.0.0+build")]
    [Arguments("0.0.0-1")]
    [Arguments(">=1.0.0")]
    [Arguments("\u0661.0.0")]
    [Arguments("1.0.0-01")]
    [Arguments("1.0.0-")]
    public async Task InvalidExplicitFloorReportsInvalidVersion(string floor)
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(Resource(("1.0.0", "sha256:a")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: floor, minimumVersionKind: WotMinimumVersionKind.ThingModel)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an invalid-version diagnostic.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    public async Task ZeroNumericPrereleaseIsNotAPositiveGeneratedFloor()
    {
        var selected = WotVersionResolver.Resolve(Resource(("0.0.0-1", "sha256:a")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
                minimumVersion: "0.0.0-0", minimumVersionKind: WotMinimumVersionKind.ThingModel));

        await Assert.That(selected.VersionId).IsEqualTo("0.0.0-1");
    }

    [Test]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(129)]
    public async Task FloorLengthIncludesTheCoreIdBoundary(int length)
    {
        var floor = "1.0.0-" + new string('a', length - 6);
        var resource = Resource(("1.0.0", "sha256:a"));
        var request = Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel,
            minimumVersion: floor, minimumVersionKind: WotMinimumVersionKind.ThingModel);
        if (length <= 128)
        {
            var selected = WotVersionResolver.Resolve(resource, request);
            await Assert.That(selected.VersionId).IsEqualTo("1.0.0");
        }
        else
        {
            var failure = await Assert.That(() => WotVersionResolver.Resolve(resource, request))
                .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an overlong floor failure.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
        }
    }

    [Test]
    [Arguments("not-semver")]
    [Arguments("1.2.3+build")]
    [Arguments("0.0.0-1")]
    public async Task InvalidImplicitTypeLinkFloorReportsInvalidVersion(string modelVersion)
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(Resource(("1.0.0", "sha256:a")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                referencingThingDescriptionModelVersion: modelVersion)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an invalid implicit-floor failure.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    [Arguments(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, WotMinimumVersionKind.ThingDescription)]
    [Arguments(WotReferenceTargetKind.ThingDescription, WotReferenceOperator.ThingDescription, WotMinimumVersionKind.ThingModel)]
    public async Task ExplicitFloorTermMustMatchTheTargetKind(WotReferenceTargetKind kind,
        WotReferenceOperator operation, WotMinimumVersionKind floorKind)
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(Resource(kind, ("1.2.0", "sha256:a")),
            Request(kind, operation, minimumVersion: "1.0.0", minimumVersionKind: floorKind)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an invalid-floor diagnostic.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-floor");
    }

    [Test]
    [Arguments("1.3.0", "1.1.0")]
    [Arguments("1.1.0", "1.3.0")]
    public async Task StickyTypeLinkEnforcesWhicheverFloorIsGreater(string explicitFloor, string implicitFloor)
    {
        var resource = ResourceWithDefault(WotReferenceTargetKind.ThingModel, "1.2.0", true,
            ("1.2.0", "sha256:a"), ("1.4.0", "sha256:b"));
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                minimumVersion: explicitFloor, minimumVersionKind: WotMinimumVersionKind.ThingModel,
                referencingThingDescriptionModelVersion: implicitFloor)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the sticky choice to fail.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    [Arguments(null, "1.3.0")]
    [Arguments("1.3.0", null)]
    public async Task StickyTypeLinkEnforcesTheOnlySuppliedFloor(string? explicitFloor, string? implicitFloor)
    {
        var resource = ResourceWithDefault(WotReferenceTargetKind.ThingModel, "1.2.0", true,
            ("1.2.0", "sha256:a"), ("1.4.0", "sha256:b"));
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resource,
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                minimumVersion: explicitFloor, minimumVersionKind: WotMinimumVersionKind.ThingModel,
                referencingThingDescriptionModelVersion: implicitFloor)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the sole floor to apply.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    public async Task MultipleTypeLinksWithoutModelVersionDoNotRequireFloors()
    {
        var selected = WotVersionResolver.Resolve(Resource(("1.0.0", "sha256:a"), ("1.4.0", "sha256:b")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                thingModelTypeLinkCount: 2, allThingModelTypeLinksHaveMinimumVersion: false));

        await Assert.That(selected.VersionId).IsEqualTo("1.4.0");
    }

    [Test]
    public async Task MultipleTypeLinksWithoutModelVersionStillEnforceTheirExplicitFloor()
    {
        var resource = Resource(("1.0.0", "sha256:a"), ("1.4.0", "sha256:b"));
        var request = Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
            minimumVersion: "1.2.0", minimumVersionKind: WotMinimumVersionKind.ThingModel,
            thingModelTypeLinkCount: 2, allThingModelTypeLinksHaveMinimumVersion: false);
        var selected = WotVersionResolver.Resolve(resource, request);
        var failure = await Assert.That(() =>
                WotVersionResolver.Resolve(resource, request with { MinimumVersion = "1.5.0" }))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected the explicit floor to apply.");

        await Assert.That(selected.VersionId).IsEqualTo("1.4.0");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    [Arguments("1.0.0", "1.2.0")]
    [Arguments("not-semver", "also-not-semver")]
    public async Task AmbiguousTypeLinksTakePrecedenceOverInvalidFloors(string explicitFloor, string implicitFloor)
    {
        var failure = await Assert.That(() => WotVersionResolver.Resolve(Resource(("1.0.0", "sha256:a")),
            Request(WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink,
                minimumVersion: explicitFloor, minimumVersionKind: WotMinimumVersionKind.ThingModel,
                referencingThingDescriptionModelVersion: implicitFloor, thingModelTypeLinkCount: 2,
                allThingModelTypeLinksHaveMinimumVersion: false)))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected ambiguity before floor validation.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("ambiguous-floor");
    }

    private static WotVersionResource Resource(params (string VersionId, string Digest)[] versions) =>
        ResourceWithDefault(WotReferenceTargetKind.ThingModel, null, false, versions);

    private static WotVersionResource Resource(WotReferenceTargetKind kind,
        params (string VersionId, string Digest)[] versions) =>
        ResourceWithDefault(kind, null, false, versions);

    private static WotVersionResource ResourceWithDefault(WotReferenceTargetKind kind,
        string? defaultVersionId, bool defaultVersionSticky,
        params (string VersionId, string Digest)[] versions) =>
        new("urn:example:thing", kind, defaultVersionId, defaultVersionSticky,
            versions.Select(static version => new WotVersionCandidate(
                version.VersionId, version.Digest, "registry-a")).ToArray());

    private static WotVersionResolutionRequest Request(
        WotReferenceTargetKind targetKind,
        WotReferenceOperator operation,
        string? minimumVersion = null,
        WotMinimumVersionKind? minimumVersionKind = null,
        string? referencingThingDescriptionModelVersion = null,
        int thingModelTypeLinkCount = 1,
        bool allThingModelTypeLinksHaveMinimumVersion = true,
        string? versionIdPin = null,
        string? digestPin = null,
        bool allowPrerelease = false,
        bool isDocumentLocal = false,
        WotVersionCandidate? containingRevision = null) =>
        new("urn:example:thing", targetKind, operation, minimumVersion, minimumVersionKind,
            referencingThingDescriptionModelVersion, thingModelTypeLinkCount,
            allThingModelTypeLinksHaveMinimumVersion, versionIdPin, digestPin, allowPrerelease,
            isDocumentLocal, containingRevision);

    [Test]
    [Arguments("001-floor-met")]
    [Arguments("002-floor-unmet")]
    [Arguments("004-digest-pin")]
    [Arguments("005-digest-pin-mismatch")]
    [Arguments("008-sticky-above-floor")]
    [Arguments("009-prerelease-excluded")]
    [Arguments("013-implicit-type-floor")]
    [Arguments("015-multiple-types-partly-specified")]
    [Arguments("017-deprecated-successor")]
    [Arguments("020-generated-floor-rejected")]
    [Arguments("024-not-found")]
    [Arguments("026-pin-overrides-floor-and-sticky")]
    [Arguments("027-generated-opt-in")]
    [Arguments("043-single-type-greater-floor-sticky")]
    [Arguments("046-multiple-types-greater-model-floor")]
    [Arguments("049-pin-bypasses-type-floor-derivation")]
    [Arguments("050-td-caret-within-major")]
    [Arguments("052-td-unfloored-crosses-majors")]
    [Arguments("055-td-prerelease-floor")]
    [Arguments("057-td-subthing-floor")]
    [Arguments("063-tm-minversion-on-td-reference-invalid-floor")]
    [Arguments("064-local-tm-ref-uses-containing-revision")]
    [Arguments("065-local-tm-ref-below-floor")]
    [Arguments("066-local-td-ref-next-major")]
    [Arguments("067-digest-pin-duplicate-highest-version")]
    [Arguments("068-local-tm-ref-missing-authored-version")]
    [Arguments("069-local-tm-ref-invalid-authored-version")]
    public async Task CanonicalSelectionVector(string vectorId)
    {
        var vector = XRegistry.WotResolution.Testing.WotResolutionVectors.Selection(vectorId);
        if (vector.ExpectedError is { } expectedError)
        {
            var failure = await Assert.That(() =>
                    WotVersionResolver.Resolve(vector.Resource, vector.Request))
                .Throws<RegistryException>()
                ?? throw new InvalidOperationException($"Vector '{vectorId}' expected diagnostic '{expectedError}'.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(expectedError);
            return;
        }

        var result = WotVersionResolver.Resolve(vector.Resource, vector.Request);
        await Assert.That(result.WotId).IsEqualTo(vector.ExpectedResult.GetProperty("wotid").GetString());
        await Assert.That(result.VersionId).IsEqualTo(vector.ExpectedResult.GetProperty("versionid").GetString());
        await Assert.That(result.ContentDigest).IsEqualTo(vector.ExpectedResult.GetProperty("contentdigest").GetString());
    }
}
