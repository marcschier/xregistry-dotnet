// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Federation.Tests;

public class WotRegistryResolverTests
{
    [Test]
    public async Task LocalRevisionSurvivesNativeSourceDisposalWithoutReopeningOrRechargingBytes()
    {
        var bytes = LocalBytes("""{"model":"2.0.0"}""");
        var tree = CreateDirectoryTree(bytes);
        var budget = new FederationReadBudget(new(maxSources: 1));
        await using var mapping = await DirectoryMapping.OpenAsync(tree, budget);
        var request = new WotVersionResolutionRequest(Source.Identity,
            WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel);
        var containing = await WotRegistryResolver.ResolveAsync(mapping, "/gs/tenant/models", request, budget);
        await mapping.DisposeAsync();
        var bytesBefore = budget.BytesRead;
        var requestsBefore = budget.Requests;
        var objectsBefore = budget.ObjectsRead;
        var workBefore = budget.Work;
        var readsBefore = tree.Reads.Count;
        var local = await WotRegistryResolver.ResolveLocalAsync(containing,
            request with { IsDocumentLocal = true, MinimumVersion = "1.0.0" }, budget);

        await Assert.That(ReferenceEquals(local, containing)).IsTrue();
        await Assert.That(local.Kind).IsEqualTo(WotReferenceTargetKind.ThingModel);
        await Assert.That(local.Version.ContentDigest).IsEqualTo(Digest(bytes));
        await Assert.That(budget.BytesRead).IsEqualTo(bytesBefore);
        await Assert.That(budget.Requests).IsEqualTo(requestsBefore);
        await Assert.That(budget.ObjectsRead).IsEqualTo(objectsBefore);
        await Assert.That(budget.Work).IsEqualTo(workBefore + 4);
        await Assert.That(tree.Reads.Count).IsEqualTo(readsBefore);
    }

    [Test]
    [Arguments(false, "2.0.0", null)]
    [Arguments(false, "1.0.0", null)]
    [Arguments(false, "0.1.0", null)]
    [Arguments(false, "2.0.0-alpha", null)]
    [Arguments(false, "2.0.1", "unsatisfied")]
    [Arguments(true, "2.0.0", null)]
    [Arguments(true, "2.0.0-alpha", null)]
    [Arguments(true, "1.0.0", "unsatisfied")]
    [Arguments(true, "0.1.0", "unsatisfied")]
    [Arguments(true, "2.0.1", "unsatisfied")]
    public async Task LocalFloorsReadTheKindSpecificAuthoredVersionAndKeepTheTdMajorCap(
        bool description, string floor, string? expectedError)
    {
        var source = new Source(description)
        {
            SecondBytes = LocalBytes(description
                ? """{"instance":"2.0.0","model":"invalid"}"""
                : """{"model":"2.0.0","instance":"invalid"}""")
        };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "2.0.0" });
        var request = source.Request with { IsDocumentLocal = true, MinimumVersion = floor };
        var reads = source.Reads.Count;
        if (expectedError is null)
        {
            var result = await WotRegistryResolver.ResolveLocalAsync(containing, request);
            await Assert.That(ReferenceEquals(result, containing)).IsTrue();
            await Assert.That(result.Version.VersionId).IsEqualTo("2.0.0");
        }
        else
        {
            var failure = await Assert.That(async () =>
                await WotRegistryResolver.ResolveLocalAsync(containing, request)).Throws<RegistryException>()
                ?? throw new InvalidOperationException("Expected a local floor failure.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(expectedError);
        }
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
    }

    [Test]
    public async Task LocalReferencesCannotSelectAnotherVersionOrTrustCallerAuthoredVersionClaims()
    {
        var source = new Source
        {
            FirstBytes = LocalBytes("""{"model":"1.0.0","instance":"9.0.0"}"""),
            SecondBytes = LocalBytes("""{"model":"2.0.0"}""")
        };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "1.0.0" });
        var request = source.Request with
        {
            IsDocumentLocal = true,
            MinimumVersion = "2.0.0",
            ContainingRevision = new("9.0.0", "sha256:forged", "urn:forged", "9.0.0")
        };
        var reads = source.Reads.Count;
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.ResolveLocalAsync(containing, request)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected the retained revision to fail its floor.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
        var withoutFloor = await WotRegistryResolver.ResolveLocalAsync(containing, request with { MinimumVersion = null });
        await Assert.That(ReferenceEquals(withoutFloor, containing)).IsTrue();
        await Assert.That(withoutFloor.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(withoutFloor.Version.ContentDigest).IsEqualTo(Digest(source.FirstBytes));
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
        await Assert.That(source.Reads.Any(read => read.Operation == FederationOperation.Document &&
            read.Target.EndsWith("/2.0.0", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(null, "unsatisfied")]
    [Arguments("{}", "unsatisfied")]
    [Arguments("""{"instance":"2.0.0"}""", "unsatisfied")]
    [Arguments("""{"model":"vendor-release"}""", "invalid-version")]
    [Arguments("""{"model":""}""", "invalid-version")]
    [Arguments("""{"model":2}""", "invalid-version")]
    [Arguments("""{"model":null}""", "invalid-version")]
    public async Task LocalFloorsRejectMissingOrInvalidAuthoredVersions(string? version, string error)
    {
        var source = new Source { SecondBytes = LocalBytes(version) };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveLocalAsync(containing,
            source.Request with { IsDocumentLocal = true, MinimumVersion = "1.0.0" })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected an authored-version failure.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(error);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalFloorsPreserveWrongKindAndInvalidFloorDiagnostics(bool description)
    {
        var source = new Source(description)
        {
            SecondBytes = LocalBytes("""{"model":"2.0.0","instance":"2.0.0"}""")
        };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "2.0.0" });
        var request = source.Request with
        {
            IsDocumentLocal = true, MinimumVersion = "1.0.0",
            MinimumVersionKind = description ? WotMinimumVersionKind.ThingModel : WotMinimumVersionKind.ThingDescription
        };
        var wrongKind = await Assert.That(async () =>
            await WotRegistryResolver.ResolveLocalAsync(containing, request)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a wrong-kind floor.");
        await Assert.That(wrongKind.Diagnostic.Code).IsEqualTo("invalid-floor");
        foreach (var floor in new[] { ">=1.0.0", "1.0.0+build", "0.0.0-1" })
        {
            var invalid = await Assert.That(async () => await WotRegistryResolver.ResolveLocalAsync(containing,
                request with { MinimumVersion = floor, MinimumVersionKind = null })).Throws<RegistryException>()
                ?? throw new InvalidOperationException("Expected invalid floor syntax.");
            await Assert.That(invalid.Diagnostic.Code).IsEqualTo("invalid-version");
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("""{"model":"vendor-release"}""")]
    public async Task LocalReferencesWithoutFloorsDoNotRequireAnAuthoredSemVer(string? version)
    {
        var source = new Source { SecondBytes = LocalBytes(version) };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var result = await WotRegistryResolver.ResolveLocalAsync(containing,
            source.Request with { IsDocumentLocal = true });
        await Assert.That(ReferenceEquals(result, containing)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalPinsAreRejectedBeforeParsingOrReturningAnyDocument(bool digest)
    {
        var source = new Source { SecondBytes = [] };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var budget = new FederationReadBudget();
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveLocalAsync(containing,
            source.Request with
            {
                IsDocumentLocal = true,
                MinimumVersion = "invalid",
                VersionIdPin = digest ? null : "2.0.0",
                DigestPin = digest ? containing.Version.ContentDigest : null
            }, budget)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a forbidden local pin.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-pin");
        await Assert.That(budget.Work).IsEqualTo(0L);
    }

    [Test]
    [Arguments("[]")]
    [Arguments("{}")]
    [Arguments("""{"id":"urn:other"}""")]
    [Arguments("""{"id":"urn:test:device:v1","id":"urn:test:device:v1"}""")]
    [Arguments("""{"id":"urn:test:device:v1","version":{"model":"2.0.0","model":"3.0.0"}}""")]
    [Arguments("""{"id":"urn:test:device:v1","version":null}""")]
    [Arguments("{")]
    public async Task LocalParsingRejectsMalformedOrMisidentifiedContainingDocuments(string document)
    {
        var source = new Source { SecondBytes = Encoding.UTF8.GetBytes(document) };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveLocalAsync(containing,
            source.Request with { IsDocumentLocal = true, MinimumVersion = "1.0.0" }))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected invalid retained JSON.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(0, -1)]
    [Arguments(0, 0)]
    [Arguments(0, 1)]
    [Arguments(1, -1)]
    [Arguments(1, 0)]
    [Arguments(1, 1)]
    [Arguments(2, -1)]
    [Arguments(2, 0)]
    [Arguments(2, 1)]
    [Arguments(3, -1)]
    [Arguments(3, 0)]
    [Arguments(3, 1)]
    public async Task LocalParsingAndOutputBudgetsHaveInclusiveExactBoundaries(int bound, int delta)
    {
        var bytes = LocalBytes("""{"model":"2.0.0"}""");
        var source = new Source { SecondBytes = bytes };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var limits = bound switch
        {
            0 => new FederationReadLimits(maxObjectBytes: bytes.Length + delta),
            1 => new FederationReadLimits(maxResultBytes: bytes.Length + delta),
            2 => new FederationReadLimits(maxWork: 4 + delta),
            _ => new FederationReadLimits(maxJsonDepth: 2 + delta)
        };
        var budget = new FederationReadBudget(limits);
        var request = source.Request with { IsDocumentLocal = true, MinimumVersion = "2.0.0" };
        if (delta < 0)
        {
            var failure = await Assert.That(async () =>
                await WotRegistryResolver.ResolveLocalAsync(containing, request, budget)).Throws<FederationException>()
                ?? throw new InvalidOperationException("Expected the exhausted local bound to fail.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var result = await WotRegistryResolver.ResolveLocalAsync(containing, request, budget);
            await Assert.That(ReferenceEquals(result, containing)).IsTrue();
            await Assert.That(budget.Work).IsEqualTo(4L);
        }
        await Assert.That(budget.Requests).IsEqualTo(0L);
        await Assert.That(budget.BytesRead).IsEqualTo(0L);
    }

    [Test]
    public async Task RepeatedLocalParsingChargesCumulativeWorkAndCancellationPrecedesParsing()
    {
        var source = new Source { SecondBytes = LocalBytes("""{"model":"2.0.0"}""") };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var request = source.Request with { IsDocumentLocal = true };
        var budget = new FederationReadBudget(new(maxWork: 4));
        var first = await WotRegistryResolver.ResolveLocalAsync(containing, request, budget);
        await Assert.That(ReferenceEquals(first, containing)).IsTrue();
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.ResolveLocalAsync(containing, request, budget)).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected cumulative work exhaustion.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        var cancelledBudget = new FederationReadBudget();
        await Assert.That(async () => await WotRegistryResolver.ResolveLocalAsync(containing, request,
            cancelledBudget, new CancellationToken(true))).Throws<OperationCanceledException>();
        await Assert.That(cancelledBudget.Work).IsEqualTo(0L);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LocalResolutionRequiresTheContainingIdentityKindAndParsedLocalOperator(int change)
    {
        var source = new Source { SecondBytes = LocalBytes(null) };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var request = source.Request with { IsDocumentLocal = true };
        request = change switch
        {
            0 => request with { IsDocumentLocal = false },
            1 => request with { Operator = WotReferenceOperator.ThingModelTypeLink },
            2 => request with { TargetWotId = "urn:other" },
            _ => request with
            {
                TargetKind = WotReferenceTargetKind.ThingDescription,
                Operator = WotReferenceOperator.ThingDescription
            }
        };
        if (change < 2)
        {
            await Assert.That(async () =>
                await WotRegistryResolver.ResolveLocalAsync(containing, request)).Throws<ArgumentException>();
        }
        else
        {
            var failure = await Assert.That(async () =>
                await WotRegistryResolver.ResolveLocalAsync(containing, request)).Throws<RegistryException>()
                ?? throw new InvalidOperationException("Expected an incompatible local target.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo(change == 2 ? "not-found" : "invalid-floor");
        }
    }

    private static byte[] LocalBytes(string? version) => Encoding.UTF8.GetBytes(
        "{\"id\":\"" + Source.Identity + "\"" + (version is null ? "" : ",\"version\":" + version) + "}");

    [Test]
    public async Task NativeDirectoryMappingResolvesWithoutInventingAResourceProjection()
    {
        var tree = CreateDirectoryTree();
        var budget = new FederationReadBudget(new(maxSources: 1));
        await using var mapping = await DirectoryMapping.OpenAsync(tree, budget);
        var result = await WotRegistryResolver.ResolveAsync(mapping, "/gs/tenant/models",
            new(Source.Identity, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel), budget);
        await mapping.DisposeAsync();
        await Assert.That(result.Version.VersionId).IsEqualTo("2.0.0");
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.Second));
        await Assert.That(result.Version.Origin).IsEqualTo(tree.Context.Source);
        await Assert.That(tree.Reads.Contains("second.bin")).IsTrue();
        await Assert.That(tree.Reads.Contains("first.bin")).IsFalse();
        using var content = result.Document.OpenRead();
        await Assert.That(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())
            .IsEqualTo(result.Version.ContentDigest["sha256:".Length..]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcquiresTheSelectedScopedVersionAndHashesItsExactBytes(bool description)
    {
        var source = new Source(description);
        var request = source.Request with { MinimumVersion = "1.0.0" };
        var result = await WotRegistryResolver.ResolveAsync(source, source.Collection, request);

        await Assert.That(result.ResourceXid).IsEqualTo(source.Resource);
        await Assert.That(result.Version.WotId).IsEqualTo(Source.Identity);
        await Assert.That(result.Version.VersionId).IsEqualTo(description ? "1.0.0" : "2.0.0");
        await Assert.That(result.Version.Origin).IsEqualTo("urn:test:authorized");
        await Assert.That(result.Context).IsEqualTo(source.Context);
        using var stream = result.Document.OpenRead();
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        var expected = description ? Source.First : Source.Second;
        await Assert.That(output.ToArray().SequenceEqual(expected)).IsTrue();
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(expected));
        await Assert.That(source.Reads.Where(read => read.Operation == FederationOperation.Document)
            .Select(read => read.Target).SequenceEqual([source.Resource + "/versions/" + result.Version.VersionId])).IsTrue();
        await Assert.That(source.Reads.All(read => read.Target.StartsWith(source.Collection, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ExactVersionPinsBypassFloorsWithoutAcquiringAnotherVersion()
    {
        var source = new Source();
        var result = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "1.0.0", MinimumVersion = "not-a-version" });
        await Assert.That(result.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.First));
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/1.0.0");
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
    }

    [Test]
    public async Task DigestPinsHashActualBytesAndChooseTheHighestDuplicate()
    {
        var source = new Source { SecondBytes = Source.First };
        source.Change = (_, metadata) =>
        {
            if (metadata["entity"] is JsonObject meta) { meta["defaultversionsticky"] = true; }
        };
        var result = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { DigestPin = Digest(Source.First), MinimumVersion = "invalid-floor" });
        await Assert.That(result.Version.VersionId).IsEqualTo("2.0.0");
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.First));
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(2);
    }

    [Test]
    public async Task ResultBudgetDoesNotLimitNonmatchingDigestCandidateInputs()
    {
        var source = new Source { SecondBytes = new byte[100] };
        var result = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { DigestPin = Digest(Source.First) },
            new(new FederationReadLimits(maxResultBytes: Source.First.Length)));
        await Assert.That(result.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(result.Document.Length).IsEqualTo((long)Source.First.Length);
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.First));
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(2);
    }

    [Test]
    public async Task AdvisoryDigestsCannotSatisfyAPinWithoutMatchingBytes()
    {
        var source = new Source();
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { DigestPin = "sha256:advisory" })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a byte-pin mismatch.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("pin-mismatch");
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(2);
    }

    [Test]
    public async Task PresentEmptyDocumentHasTheWellKnownEmptySha256()
    {
        var source = new Source { SecondBytes = [] };
        var result = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        await Assert.That(result.Document.Length).IsEqualTo(0L);
        await Assert.That(result.Version.ContentDigest)
            .IsEqualTo("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidOrMissingPinsCannotCauseDocumentAcquisition(bool both)
    {
        var source = new Source();
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "9.0.0", DigestPin = both ? Digest(Source.First) : null }))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a pin failure.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(both ? "invalid-pin" : "not-found");
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    public async Task StickyFloorFailureDoesNotAcquireOrDowngrade()
    {
        var source = new Source();
        source.Change = (_, metadata) =>
        {
            if (metadata["entity"] is JsonObject meta) { meta["defaultversionsticky"] = true; }
        };
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { MinimumVersion = "2.0.0" })).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a sticky floor failure.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    public async Task CompetingResourceMatchesAreRejectedBeforeVersionSelection()
    {
        var source = new Source();
        source.Change = (request, metadata) =>
        {
            if (request.Target != source.Collection) { return; }
            metadata["entities"]!["competitor"] = new JsonObject
            {
                ["xid"] = source.Collection + "/competitor", ["wotid"] = Source.Identity
            };
        };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Ambiguous);
        await Assert.That(source.Reads.Count).IsEqualTo(5);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyOrDifferentlyCasedIdentitiesAreNotFound(bool empty)
    {
        var source = new Source();
        if (empty) { source.Change = (_, metadata) => (metadata["entities"] as JsonObject)?.Clear(); }
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { TargetWotId = empty ? Source.Identity : Source.Identity.ToUpperInvariant() }))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a scoped miss.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("not-found");
        await Assert.That(source.Reads.Count).IsEqualTo(empty ? 1 : 3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IncompleteCollectionsNeverEstablishAbsenceOrUniqueness(bool versions)
    {
        var source = new Source();
        source.Change = (request, metadata) =>
        {
            if (request.Target == (versions ? source.Resource + "/versions" : source.Collection))
            {
                metadata["complete"] = false;
            }
        };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    [Arguments(FederationErrorCode.PolicyDenied)]
    [Arguments(FederationErrorCode.NotFound)]
    [Arguments(FederationErrorCode.Unavailable)]
    public async Task AcquisitionFailuresDoNotRetryOtherVersionsOrSuccessors(FederationErrorCode code)
    {
        var source = new Source { DocumentFailure = code };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(code);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/2.0.0");
    }

    [Test]
    public async Task ExternalDescriptorsAreNotAcquiredAsDocuments()
    {
        var source = new Source { External = true };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(source.Reads.Count).IsEqualTo(5);
    }

    [Test]
    public async Task MetadataPolicyDenialCannotBecomeAScopedMiss()
    {
        var source = new Source();
        source.Change = (request, _) =>
        {
            if (request.Target == source.Resource + "/meta")
            {
                throw new FederationException(FederationErrorCode.PolicyDenied, "Metadata access denied.");
            }
        };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(source.Reads.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AliasesAndMalformedStickyFlagsFailWithoutAcquisition(bool alias)
    {
        var source = new Source();
        source.Change = (request, metadata) =>
        {
            if (request.Target != source.Resource + "/meta") { return; }
            if (alias) { metadata["entity"]!["xref"] = source.Collection + "/other"; }
            else { metadata["entity"]!["defaultversionsticky"] = null; }
        };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(alias
            ? FederationErrorCode.UnsupportedOperation : FederationErrorCode.InvalidPackage);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ChangedContextOwnerOrModelAbortsDependentReads(int change)
    {
        var source = new Source();
        source.AfterRead = () =>
        {
            if (change == 0) { source.Context = new("test", "urn:test:substituted"); }
            if (change == 1) { source.Capabilities = RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement; }
            if (change == 2) { source.Model = LoadModel(); }
        };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(source.Reads.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WrongDocumentOriginOrSelectedVersionIsRejected(bool wrongOrigin)
    {
        var source = new Source { WrongDocumentOrigin = wrongOrigin, WrongDocumentPath = !wrongOrigin };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(wrongOrigin
            ? FederationErrorCode.InconsistentSnapshot : FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task WrongCollectionMemberOrVersionIdentityIsRejected(int change)
    {
        var source = new Source();
        source.Change = (request, metadata) =>
        {
            if (request.Target == source.Collection)
            {
                if (change == 0) { metadata["xid"] = "/thingmodelgroups/other/thingmodels"; }
                if (change == 1) { metadata["entities"]!["assigned"]!["xid"] = source.Collection + "/different"; }
                if (change == 4)
                {
                    metadata["entities"]!["ASSIGNED"] = metadata["entities"]!["assigned"]!.DeepClone();
                    metadata["entities"]!["ASSIGNED"]!["xid"] = source.Collection + "/ASSIGNED";
                }
            }
            if (request.Target == source.Resource + "/versions")
            {
                if (change == 2) { metadata["entities"]!["1.0.0"]!["wotid"] = "urn:test:another"; }
                if (change == 3) { metadata["entities"]!["1.0.0"]!["versionid"] = "3.0.0"; }
            }
        };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequestAndObjectBudgetsAcceptTheExactBoundaryAndRejectOneBelow(bool objects)
    {
        var success = new Source();
        var exact = new FederationReadBudget(objects
            ? new FederationReadLimits(maxObjects: 5) : new FederationReadLimits(maxRequests: 5));
        var selected = await WotRegistryResolver.ResolveAsync(success, success.Collection, success.Request, exact);
        await Assert.That(selected.Version.VersionId).IsEqualTo("2.0.0");
        var limited = new Source();
        var budget = new FederationReadBudget(objects
            ? new FederationReadLimits(maxObjects: 4) : new FederationReadLimits(maxRequests: 4));
        var failure = await FederationFailure(limited, limited.Request, budget);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(objects ? budget.ObjectsRead : budget.Requests).IsEqualTo(4L);
    }

    [Test]
    public async Task ByteBudgetsAcceptTheExactTotalAndRejectOneByteBelow()
    {
        var reference = new Source();
        var observed = new FederationReadBudget();
        await WotRegistryResolver.ResolveAsync(reference, reference.Collection, reference.Request, observed);
        var exact = new Source();
        var result = await WotRegistryResolver.ResolveAsync(exact, exact.Collection, exact.Request,
            new(new FederationReadLimits(maxTotalBytes: observed.BytesRead)));
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.Second));
        var limited = new Source();
        var failure = await FederationFailure(limited, limited.Request,
            new(new FederationReadLimits(maxTotalBytes: observed.BytesRead - 1)));
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WorkAndResultBudgetsAcceptTheExactBoundaryAndRejectOneBelow(bool work)
    {
        var reference = new Source();
        var observed = new FederationReadBudget();
        await WotRegistryResolver.ResolveAsync(reference, reference.Collection, reference.Request, observed);
        var exact = new Source();
        await WotRegistryResolver.ResolveAsync(exact, exact.Collection, exact.Request,
            new(work ? new FederationReadLimits(maxWork: observed.Work)
                : new FederationReadLimits(maxResultBytes: Source.Second.Length)));
        var limited = new Source();
        var failure = await FederationFailure(limited, limited.Request,
            new(work ? new FederationReadLimits(maxWork: observed.Work - 1)
                : new FederationReadLimits(maxResultBytes: Source.Second.Length - 1)));
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(5)]
    public async Task CancellationBeforeOrAfterAnUncooperativeReadCannotReturnSuccess(int afterRead)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source();
        if (afterRead > 0) { source.AfterRead = () => { if (source.Reads.Count == afterRead) { cancellation.Cancel(); } }; }
        else { cancellation.Cancel(); }
        await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(source.Reads.Count).IsEqualTo(afterRead);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnsupportedModelsFailBeforeAnyRead(bool missing)
    {
        var source = new Source { Model = missing ? null : RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"thingmodelgroups":{"singular":"thingmodelgroup","resources":{"thingmodels":{"singular":"thingmodel"}}}}}""")) };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(source.Reads.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task NonSemVerResourceControlsAndUnknownProfilesFailBeforeReads(int change)
    {
        var model = JsonNode.Parse(LoadModel().Source.RootElement.GetRawText())!.AsObject();
        var resource = model["groups"]!["thingmodelgroups"]!["resources"]!["thingmodels"]!;
        if (change == 0) { resource["versionmode"] = "manual"; }
        if (change == 1) { resource["singular"] = "asset"; }
        if (change == 2) { resource["hasdocument"] = false; }
        if (change >= 3)
        {
            model["attributes"] ??= new JsonObject();
            model["attributes"]!["wotversionprofile"] = new JsonObject
            {
                ["type"] = "string", ["required"] = true, ["readonly"] = change == 3,
                ["default"] = change == 3 ? "incremental" : "semver"
            };
        }
        var source = new Source { Model = RegistryModel.Compile(RegistryJson.Parse(model.ToJsonString())) };
        var failure = await FederationFailure(source, source.Request);
        await Assert.That(failure.Code).IsEqualTo(change >= 3
            ? FederationErrorCode.UnsupportedVersion : FederationErrorCode.UnsupportedOperation);
        await Assert.That(source.Reads.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalReferencesAndUnscopedCollectionsCannotPerformRegistryReads(bool local)
    {
        var source = new Source();
        await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source,
            local ? source.Collection : "/thingmodelgroups",
            source.Request with { IsDocumentLocal = local })).Throws<ArgumentException>();
        await Assert.That(source.Reads.Count).IsEqualTo(0);
    }

    private static async Task<FederationException> FederationFailure(Source source,
        WotVersionResolutionRequest request, FederationReadBudget? budget = null) =>
        await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source, source.Collection, request, budget))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an explicit source failure.");

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static RegistryModel LoadModel()
    {
        using var document = BuiltInRegistryModels.LoadSource(RegistryModelKind.Wot);
        return RegistryModel.Compile(RegistryJson.FromElement(document.RootElement));
    }

    private static MemoryTreeReader CreateDirectoryTree(byte[]? secondBytes = null)
    {
        var tree = new MemoryTreeReader();
        const string resource = "/gs/tenant/models/assigned";
        JsonNode Put(string file, JsonObject record)
        {
            record["format"] = "xregistry-document-tree";
            record["formatversion"] = "1";
            var bytes = Encoding.UTF8.GetBytes(record.ToJsonString());
            tree.Files.Add(file, bytes);
            return new JsonObject
            {
                ["kind"] = record["kind"]!.DeepClone(),
                ["xid"] = (record["xid"] ?? record["entity"]!["xid"])!.DeepClone(),
                ["href"] = file, ["size"] = bytes.Length,
                ["sha256"] = Digest(bytes)["sha256:".Length..]
            };
        }
        static JsonObject Entity(string xid, string idName, string id) => new()
        {
            ["xid"] = xid, [idName] = id, ["epoch"] = 1,
            ["createdat"] = "2026-09-04T00:00:00Z", ["modifiedat"] = "2026-09-04T00:00:00Z"
        };
        var versionReferences = new JsonArray();
        foreach (var (id, file, bytes) in new[] { ("1.0.0", "first.bin", Source.First), ("2.0.0", "second.bin", secondBytes ?? Source.Second) })
        {
            tree.Files.Add(file, bytes);
            var entity = Entity(resource + "/versions/" + id, "thingmodelid", "assigned");
            entity["versionid"] = id;
            entity["ancestorid"] = "1.0.0";
            entity["isdefault"] = id == "2.0.0";
            entity["wotid"] = Source.Identity;
            entity["contenttype"] = "application/json";
            versionReferences.Add(Put(id + ".json", new JsonObject
            {
                ["kind"] = "version", ["entity"] = entity,
                ["document"] = new JsonObject
                {
                    ["kind"] = "local", ["href"] = file, ["size"] = bytes.Length,
                    ["sha256"] = Digest(bytes)["sha256:".Length..]
                }
            }));
        }
        var versions = Put("versions.json", new JsonObject
        {
            ["kind"] = "collection", ["xid"] = resource + "/versions",
            ["count"] = 2, ["entries"] = versionReferences
        });
        var meta = Entity(resource + "/meta", "thingmodelid", "assigned");
        meta["readonly"] = false;
        meta["defaultversionid"] = "2.0.0";
        meta["defaultversionsticky"] = false;
        var metaReference = Put("meta.json", new JsonObject { ["kind"] = "meta", ["entity"] = meta });
        var resourceReference = Put("resource.json", new JsonObject
        {
            ["kind"] = "resource", ["entity"] = new JsonObject { ["xid"] = resource, ["thingmodelid"] = "assigned" },
            ["meta"] = metaReference, ["collections"] = new JsonArray(versions)
        });
        var resources = Put("resources.json", new JsonObject
        {
            ["kind"] = "collection", ["xid"] = "/gs/tenant/models",
            ["count"] = 1, ["entries"] = new JsonArray(resourceReference)
        });
        var group = Put("group.json", new JsonObject
        {
            ["kind"] = "group", ["entity"] = Entity("/gs/tenant", "gid", "tenant"),
            ["collections"] = new JsonArray(resources)
        });
        var groups = Put("groups.json", new JsonObject
        {
            ["kind"] = "collection", ["xid"] = "/gs", ["count"] = 1, ["entries"] = new JsonArray(group)
        });
        var root = Entity("/", "registryid", "wot");
        root["specversion"] = "1.0-rc4";
        root["capabilities"] = JsonNode.Parse("""
            {"available":{"capabilities":{"mutable":false},"entities":{"mutable":false},"model":{"mutable":false}}}
            """);
        root["modelsource"] = JsonNode.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"models":{"singular":"thingmodel",
            "versionmode":"semver","singleversionroot":true,
            "attributes":{"wotid":{"type":"uri","required":true,"matchversions":true}}}}}}}
            """);
        Put("registry.json", new JsonObject
        {
            ["kind"] = "registry", ["entity"] = root, ["collections"] = new JsonArray(groups),
            ["snapshot"] = new JsonObject { ["scope"] = "/", ["completeness"] = "offline-complete" }
        });
        return tree;
    }

    private sealed class Source : IFederationReadSource
    {
        internal const string Identity = "urn:test:device:v1";
        internal static readonly byte[] First = Encoding.UTF8.GetBytes(" { \"revision\": 1 }\r\n");
        internal static readonly byte[] Second = Encoding.UTF8.GetBytes("{\"revision\":2}\n");
        private static readonly RegistryModel CapturedModel = LoadModel();

        internal Source(bool description = false)
        {
            Collection = description ? "/thingdescriptiongroups/tenant/thingdescriptions" : "/thingmodelgroups/tenant/thingmodels";
            Request = new(Identity, description ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel,
                description ? WotReferenceOperator.ThingDescription : WotReferenceOperator.ThingModel);
        }

        internal string Collection { get; }
        internal string Resource => Collection + "/assigned";
        internal WotVersionResolutionRequest Request { get; }
        internal List<FederationReadRequest> Reads { get; } = [];
        internal byte[] FirstBytes { get; init; } = First;
        internal byte[] SecondBytes { get; init; } = Second;
        internal Action<FederationReadRequest, JsonObject>? Change { get; set; }
        internal Action? AfterRead { get; set; }
        internal FederationErrorCode? DocumentFailure { get; init; }
        internal bool External { get; init; }
        internal bool WrongDocumentOrigin { get; init; }
        internal bool WrongDocumentPath { get; init; }
        public NativeRegistryContext Context { get; set; } = new("test", "urn:test:authorized");
        public JsonElement Capabilities { get; set; } = RegistryJson.Parse("{}").RootElement;
        public RegistryModel? Model { get; set; } = CapturedModel;

        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            Reads.Add(request);
            FederationReadResult result;
            if (request.Operation == FederationOperation.Document)
            {
                if (DocumentFailure is { } failure) { throw new FederationException(failure, "Selected source failure."); }
                var context = WrongDocumentOrigin ? new NativeRegistryContext("test", "urn:test:other") : Context;
                var target = WrongDocumentPath ? Resource + "/versions/1.0.0" : request.Target;
                result = External ? FederationReadResult.FromExternalDocument(target, context,
                    RegistryJson.Parse("""{"kind":"external","uri":"https://untrusted.invalid/document"}""").RootElement)
                    : FederationReadResult.FromDocument(target, context, new FederationDocument(
                        request.Target.EndsWith("/1.0.0", StringComparison.Ordinal) ? FirstBytes : SecondBytes,
                        "application/json", documentOrigin: "urn:test:advisory-original-location"));
            }
            else
            {
                var metadata = Metadata(request);
                Change?.Invoke(request, metadata);
                result = FederationReadResult.FromMetadata(request.Target, Context,
                    RegistryJson.Parse(metadata.ToJsonString()).RootElement);
            }
            AfterRead?.Invoke();
            return ValueTask.FromResult(result);
        }

        private JsonObject Metadata(FederationReadRequest request)
        {
            var path = RegistryPath.Parse(request.Target);
            if (path.Kind == RegistryPathKind.Version && path.VersionId!.Value == "1.0.0")
            {
                return new JsonObject
                {
                    ["entity"] = new JsonObject
                    {
                        ["xid"] = request.Target, ["versionid"] = "1.0.0", ["wotid"] = Identity
                    }
                };
            }
            if (path.Kind == RegistryPathKind.Meta)
            {
                return new JsonObject
                {
                    ["entity"] = new JsonObject
                    {
                        ["xid"] = request.Target, ["defaultversionid"] = "1.0.0",
                        ["defaultversionsticky"] = false,
                        ["deprecated"] = new JsonObject { ["alternative"] = Collection + "/successor" }
                    }
                };
            }
            var entities = new JsonObject();
            if (request.Target == Collection)
            {
                entities["assigned"] = new JsonObject { ["xid"] = Resource };
            }
            else if (request.Target == Resource + "/versions")
            {
                foreach (var id in new[] { "1.0.0", "2.0.0" })
                {
                    entities[id] = new JsonObject
                    {
                        ["xid"] = request.Target + "/" + id, ["versionid"] = id,
                        ["wotid"] = Identity, ["contentdigest"] = "sha256:advisory"
                    };
                }
            }
            else { throw new InvalidOperationException("Unexpected source read."); }
            return new JsonObject
            {
                ["kind"] = "collection", ["xid"] = request.Target, ["complete"] = true, ["entities"] = entities
            };
        }
    }
}
