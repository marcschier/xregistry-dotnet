// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class WotVersionResolverTests
{
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
}
