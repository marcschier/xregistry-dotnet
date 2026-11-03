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
    [Arguments(false, "1.0.0")]
    [Arguments(true, "1.0.0")]
    [Arguments(false, "2.0.0-alpha")]
    [Arguments(true, "2.0.0-alpha")]
    public async Task CloudEventsUsesTheCoreDefaultIncludingLowerAndPrereleaseStickyDefaults(
        bool sticky, string defaultId)
    {
        var source = new Source(true);
        source.Change = (request, metadata) =>
        {
            if (request.Target == source.Resource + "/meta")
            {
                metadata["entity"]!["defaultversionid"] = defaultId;
                metadata["entity"]!["defaultversionsticky"] = sticky;
            }
            if (request.Target == source.Resource + "/versions" && defaultId != "1.0.0")
            {
                metadata["entities"]![defaultId] = new JsonObject
                {
                    ["xid"] = request.Target + "/" + defaultId, ["versionid"] = defaultId,
                    ["wotid"] = Source.Identity
                };
            }
        };
        var query = WotCloudEventSourceQuery.Parse("wotid=urn:test:device:v1;vendor=%not-interpreted");
        var result = await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query);
        var bytes = defaultId == "1.0.0" ? Source.First : Source.Second;
        await Assert.That(result.Kind).IsEqualTo(WotReferenceTargetKind.ThingDescription);
        await Assert.That(result.ResourceXid).IsEqualTo(source.Resource);
        await Assert.That(result.Version.VersionId).IsEqualTo(defaultId);
        await Assert.That(result.Version.WotId).IsEqualTo(Source.Identity);
        await Assert.That(result.Version.Profile).IsEqualTo(WotVersionProfile.SemVer);
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(bytes));
        await Assert.That(result.Context).IsEqualTo(source.Context);
        await Assert.That(result.Version.Origin).IsEqualTo(source.Context.Source);
        using var stored = result.Document.OpenRead();
        using var copied = new MemoryStream();
        await stored.CopyToAsync(copied);
        await Assert.That(copied.ToArray().SequenceEqual(bytes)).IsTrue();
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/" + defaultId);
        await Assert.That(source.Reads.Count).IsEqualTo(4);
    }

    [Test]
    [Arguments("1.0.0", false)]
    [Arguments("2.0.0", false)]
    [Arguments("2.0.0-alpha", false)]
    [Arguments("1.0.0", true)]
    [Arguments("2.0.0", true)]
    [Arguments("2.0.0-alpha", true)]
    public async Task CloudEventsExactVersionsDoNotRequireADefaultOrApplyPrereleaseSelection(
        string version, bool missingDefault)
    {
        var source = new Source(true);
        source.Change = (request, metadata) =>
        {
            if (request.Target == source.Resource + "/meta" && missingDefault)
            {
                metadata["entity"]!.AsObject().Remove("defaultversionid");
            }
            if (request.Target == source.Resource + "/versions")
            {
                metadata["entities"]!["2.0.0-alpha"] = new JsonObject
                {
                    ["xid"] = request.Target + "/2.0.0-alpha", ["versionid"] = "2.0.0-alpha",
                    ["wotid"] = Source.Identity
                };
            }
        };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity, version));
        var result = await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query);
        await Assert.That(result.Version.VersionId).IsEqualTo(version);
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(version == "1.0.0" ? Source.First : Source.Second));
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/" + version);
    }

    [Test]
    [Arguments("2.0.0", "2.0.0", "second")]
    [Arguments(null, "1.0.0", "first")]
    public async Task CloudEventsSubjectSelectsFromTheSelectedPublishedViewVersion(
        string? pin, string selectedVersion, string expectedType)
    {
        var firstView = Encoding.UTF8.GetBytes(
            """{"id":"urn:test:device:v1","events":{"overheated":{"type":"first"}}}""");
        var secondView = Encoding.UTF8.GetBytes(
            """{"id":"urn:test:device:v1","events":{"overheated":{"type":"second"}}}""");
        var source = new Source(true) { FirstBytes = firstView, SecondBytes = secondView };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity, pin));
        var selectedBytes = pin is null ? firstView : secondView;

        var result = await WotRegistryResolver.AcquireCloudEventSubjectAsync(source, source.Collection,
            query, "#/events/overheated");

        await Assert.That(result.Value.GetProperty("type").GetString()).IsEqualTo(expectedType);
        await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo(selectedVersion);
        await Assert.That(result.ContainingDocument.Version.ContentDigest).IsEqualTo(Digest(selectedBytes));
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/" + selectedVersion);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
    }

    [Test]
    [Arguments("#/events/missing", "not-found")]
    [Arguments("", "invalid-fragment")]
    public async Task CloudEventsSubjectRejectsMissingOrInvalidPointers(string subject, string expectedCode)
    {
        var view = Encoding.UTF8.GetBytes("""{"id":"urn:test:device:v1","events":{"overheated":{"type":"test"}}}""");
        var source = new Source(true) { FirstBytes = view, SecondBytes = view };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.AcquireCloudEventSubjectAsync(source, source.Collection, query, subject))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected subject selection failure.");

        await Assert.That(failure.Diagnostic.Code).IsEqualTo(expectedCode);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/1.0.0");
    }

    [Test]
    public async Task CloudEventsSubjectUsesOneReadBudgetForVersionAcquisitionAndPointerSelection()
    {
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));
        var view = Encoding.UTF8.GetBytes("""{"id":"urn:test:device:v1","events":{"overheated":{"type":"test"}}}""");
        var acquisition = new Source(true) { FirstBytes = view, SecondBytes = view };
        var acquisitionBudget = new FederationReadBudget();
        await WotRegistryResolver.AcquireCloudEventSourceAsync(acquisition, acquisition.Collection, query,
            acquisitionBudget);

        var source = new Source(true) { FirstBytes = view, SecondBytes = view };
        var sharedBudget = new FederationReadBudget(new FederationReadLimits(maxWork: acquisitionBudget.Work));
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.AcquireCloudEventSubjectAsync(source, source.Collection, query,
                "#/events/overheated", sharedBudget))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected the shared work budget to be exhausted.");

        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
    }

    [Test]
    public async Task CloudEventsSubjectUsesCapturedWotIdWhenDocumentOmitsId()
    {
        var view = Encoding.UTF8.GetBytes("""{"events":{"overheated":{"type":"test"}}}""");
        var source = new Source(true) { FirstBytes = view, SecondBytes = view };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));

        var result = await WotRegistryResolver.AcquireCloudEventSubjectAsync(source, source.Collection, query,
            "#/events/overheated");

        await Assert.That(result.Value.GetProperty("type").GetString()).IsEqualTo("test");
        await Assert.That(result.ContainingDocument.Version.WotId).IsEqualTo(Source.Identity);
        await Assert.That(result.ContainingDocument.Document.Length).IsEqualTo(view.Length);
    }

    [Test]
    public async Task CloudEventsSubjectRejectsDocumentIdThatConflictsWithCapturedWotId()
    {
        var view = Encoding.UTF8.GetBytes("""{"id":"urn:test:other","events":{"overheated":{"type":"test"}}}""");
        var source = new Source(true) { FirstBytes = view, SecondBytes = view };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));

        var failure = await Assert.That(async () =>
            await WotRegistryResolver.AcquireCloudEventSubjectAsync(source, source.Collection, query,
                "#/events/overheated"))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected conflicting WoT identity rejection.");

        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(null, true)]
    [Arguments(null, false)]
    [Arguments("1.0.1", true)]
    [Arguments("1.0.1", false)]
    [Arguments("2.0.0+build", false)]
    [Arguments("../outside", false)]
    public async Task CloudEventsMissingVersionOrDefaultIsNotFoundWithoutFallback(string? version, bool missingDefault)
    {
        var source = new Source(true);
        source.Change = (request, metadata) =>
        {
            if (request.Target != source.Resource + "/meta") { return; }
            if (missingDefault) { metadata["entity"]!.AsObject().Remove("defaultversionid"); }
            else { metadata["entity"]!["defaultversionid"] = "absent"; }
        };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity, version));
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a missing TD selection.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("not-found");
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    public async Task CloudEventsVersionLookupKeepsCoreCaseSensitivity()
    {
        var source = new Source(true);
        source.Change = (request, metadata) =>
        {
            if (request.Target != source.Resource + "/versions") { return; }
            metadata["entities"]!["2.0.0-alpha"] = new JsonObject
            {
                ["xid"] = request.Target + "/2.0.0-alpha", ["versionid"] = "2.0.0-alpha",
                ["wotid"] = Source.Identity
            };
        };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity, "2.0.0-ALPHA"));
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a case-sensitive Version miss.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("not-found");
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CloudEventsLookupUsesExactIdentityAndCompleteVersionMetadata(int missing)
    {
        var source = new Source(true);
        source.Change = (request, metadata) =>
        {
            if (request.Target == (missing == 0 ? source.Collection : source.Resource + "/versions") && missing < 2)
            {
                metadata["entities"]!.AsObject().Clear();
            }
        };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(
            missing == 2 ? Source.Identity.ToUpperInvariant() : Source.Identity));
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected a scoped WoT miss.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("not-found");
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CloudEventsIncompleteCollectionsCannotProveAbsenceOrUniqueness(bool versions)
    {
        var source = new Source(true);
        source.Change = (request, metadata) =>
        {
            if (request.Target == (versions ? source.Resource + "/versions" : source.Collection))
            {
                metadata["complete"] = false;
            }
        };
        var failure = await CloudEventFailure(source);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    public async Task CloudEventsCompetingResourcesAreRejectedBeforeAcquiringAnyDocument()
    {
        var source = new Source(true);
        source.Change = (request, metadata) =>
        {
            if (request.Target != source.Collection) { return; }
            metadata["entities"]!["competitor"] = new JsonObject { ["xid"] = source.Collection + "/competitor" };
        };
        var failure = await CloudEventFailure(source);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Ambiguous);
        await Assert.That(source.Reads.Count).IsEqualTo(5);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task CloudEventsRejectsAliasesAndInconsistentVersionOrDefaultMetadata(int change)
    {
        var source = new Source(true);
        source.Change = (request, metadata) =>
        {
            if (request.Target == source.Resource + "/meta")
            {
                if (change == 0) { metadata["entity"]!["xref"] = source.Collection + "/other"; }
                if (change == 3) { metadata["entity"]!["defaultversionsticky"] = null; }
            }
            if (request.Target == source.Resource + "/versions")
            {
                if (change == 1) { metadata["entities"]!["1.0.0"]!["wotid"] = "urn:test:other"; }
                if (change == 2) { metadata["entities"]!["1.0.0"]!["versionid"] = "another"; }
                if (change == 4) { metadata["entities"]!["1.0.0"]!["xid"] = request.Target + "/another"; }
                if (change == 5) { metadata["xid"] = source.Collection + "/other/versions"; }
            }
        };
        var failure = await CloudEventFailure(source);
        await Assert.That(failure.Code).IsEqualTo(change == 0
            ? FederationErrorCode.UnsupportedOperation : FederationErrorCode.InvalidPackage);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(0);
    }

    [Test]
    [Arguments(FederationErrorCode.PolicyDenied)]
    [Arguments(FederationErrorCode.NotFound)]
    [Arguments(FederationErrorCode.Unavailable)]
    public async Task CloudEventsDocumentFailuresDoNotTryAnotherVersionOrSource(FederationErrorCode code)
    {
        var source = new Source(true) { DocumentFailure = code };
        var failure = await CloudEventFailure(source);
        await Assert.That(failure.Code).IsEqualTo(code);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/1.0.0");
    }

    [Test]
    public async Task CloudEventsMetadataDenialDoesNotBecomeNotFound()
    {
        var source = new Source(true);
        source.Change = (request, _) =>
        {
            if (request.Target == source.Resource + "/meta")
            {
                throw new FederationException(FederationErrorCode.PolicyDenied, "Metadata access denied.");
            }
        };
        var failure = await CloudEventFailure(source);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(source.Reads.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CloudEventsChangedContextOwnerOrModelRejectsDependentReads(int change)
    {
        var source = new Source(true);
        source.AfterRead = () =>
        {
            if (change == 0) { source.Context = new("test", "urn:test:changed"); }
            if (change == 1) { source.Capabilities = RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement; }
            if (change == 2) { source.Model = LoadModel(); }
        };
        var failure = await CloudEventFailure(source);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(source.Reads.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CloudEventsWrongDocumentOriginOrVersionCannotReturnBytes(bool wrongOrigin)
    {
        var source = new Source(true)
        {
            WrongDocumentOrigin = wrongOrigin, WrongDocumentPath = !wrongOrigin
        };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity, "2.0.0"));
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected substituted document rejection.");
        await Assert.That(failure.Code).IsEqualTo(wrongOrigin
            ? FederationErrorCode.InconsistentSnapshot : FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CloudEventsExternalDescriptorsAreNotFetched()
    {
        var source = new Source(true) { External = true };
        var failure = await CloudEventFailure(source);
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(source.Reads.Count).IsEqualTo(4);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task CloudEventsAcquisitionHonorsInclusiveSharedBudgetBoundaries(int bound)
    {
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));
        var reference = new Source(true);
        var observed = new FederationReadBudget();
        await WotRegistryResolver.AcquireCloudEventSourceAsync(reference, reference.Collection, query, observed);
        var limit = bound switch
        {
            0 => observed.Requests, 1 => observed.ObjectsRead, 2 => observed.BytesRead,
            3 => observed.Work, 4 => Source.First.Length, _ => Math.Max(reference.LargestMetadataBytes, Source.First.Length)
        };
        for (var delta = -1; delta <= 1; delta++)
        {
            var limits = bound switch
            {
                0 => new FederationReadLimits(maxRequests: limit + delta),
                1 => new FederationReadLimits(maxObjects: limit + delta),
                2 => new FederationReadLimits(maxTotalBytes: limit + delta),
                3 => new FederationReadLimits(maxWork: limit + delta),
                4 => new FederationReadLimits(maxResultBytes: checked((int)(limit + delta))),
                _ => new FederationReadLimits(maxObjectBytes: checked((int)(limit + delta)))
            };
            var source = new Source(true);
            if (delta < 0)
            {
                var failure = await CloudEventFailure(source, new(limits));
                await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            }
            else
            {
                var result = await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query, new(limits));
                await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.First));
            }
        }
    }

    [Test]
    public async Task CloudEventsRepeatedAcquisitionCannotResetTheSharedBudget()
    {
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));
        var reference = new Source(true);
        var observed = new FederationReadBudget();
        await WotRegistryResolver.AcquireCloudEventSourceAsync(reference, reference.Collection, query, observed);
        var source = new Source(true);
        var shared = new FederationReadBudget(new(maxRequests: observed.Requests));
        var result = await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query, shared);
        var failure = await CloudEventFailure(source, shared);
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.First));
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(source.Reads.Count).IsEqualTo(4);
        await Assert.That(shared.Requests).IsEqualTo(observed.Requests);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(4)]
    public async Task CloudEventsCancellationBeforeOrAfterUncooperativeReadsCannotReturnSuccess(int afterRead)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source(true);
        if (afterRead == 0) { cancellation.Cancel(); }
        else { source.AfterRead = () => { if (source.Reads.Count == afterRead) { cancellation.Cancel(); } }; }
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));
        await Assert.That(async () => await WotRegistryResolver.AcquireCloudEventSourceAsync(source,
            source.Collection, query, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(source.Reads.Count).IsEqualTo(afterRead);
    }

    [Test]
    public async Task CloudEventsDefaultChangesDoNotChangeAnExplicitVersionOrPreviouslyAcquiredBytes()
    {
        var source = new Source(true);
        var defaultId = "1.0.0";
        source.Change = (request, metadata) =>
        {
            if (request.Target == source.Resource + "/meta") { metadata["entity"]!["defaultversionid"] = defaultId; }
        };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));
        var first = await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query);
        defaultId = "2.0.0";
        var second = await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query);
        var pinned = await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection,
            WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity, "1.0.0")));
        await Assert.That(first.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(second.Version.VersionId).IsEqualTo("2.0.0");
        await Assert.That(pinned.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(pinned.Version.ContentDigest).IsEqualTo(first.Version.ContentDigest);
        using var retained = first.Document.OpenRead();
        await Assert.That("sha256:" + Convert.ToHexString(SHA256.HashData(retained)).ToLowerInvariant()).IsEqualTo(Digest(Source.First));
    }

    [Test]
    [Arguments(null, "1")]
    [Arguments("1", "1")]
    [Arguments("2", "2")]
    public async Task CloudEventsIncrementalSourcesUseExactCoreSelectionWithoutACompatibilityLineFloor(
        string? pin, string selected)
    {
        var source = new Source(true) { Model = WotRegistryProfiles.Compile(WotVersionProfile.Incremental) };
        source.Change = (request, metadata) =>
        {
            if (request.Target == source.Resource + "/meta") { metadata["entity"]!["defaultversionid"] = "1"; }
            if (request.Target != source.Resource + "/versions") { return; }
            var versions = metadata["entities"]!.AsObject();
            versions.Clear();
            foreach (var id in new[] { "1", "2" })
            {
                versions[id] = new JsonObject
                {
                    ["xid"] = request.Target + "/" + id, ["versionid"] = id,
                    ["wotid"] = Source.Identity, ["compatibilityline"] = "urn:test:line:" + id
                };
            }
        };
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity, pin));
        var result = await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection, query);
        await Assert.That(result.Version.Profile).IsEqualTo(WotVersionProfile.Incremental);
        await Assert.That(result.Version.VersionId).IsEqualTo(selected);
        await Assert.That(result.Version.CompatibilityLine).IsEqualTo("urn:test:line:" + selected);
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.Second));
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/" + selected);
    }

    [Test]
    public async Task CloudEventsRequiresAnExplicitTdCollectionAndCapturedModelBeforeReads()
    {
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));
        var source = new Source(true);
        await Assert.That(async () =>
            await WotRegistryResolver.AcquireCloudEventSourceAsync(source, "/thingdescriptiongroups", query))
            .Throws<ArgumentException>();
        var modelSource = new Source();
        var wrongKind = await CloudEventFailure(modelSource);
        await Assert.That(wrongKind.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        source.Model = null;
        var missingModel = await CloudEventFailure(source);
        await Assert.That(missingModel.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(source.Reads.Count + modelSource.Reads.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CloudEventsDirectoryAcquisitionRetainsAuthoredBytesAfterSourceDisposal()
    {
        var tree = CreateDirectoryTree(description: true, defaultVersionId: "1.0.0");
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var query = WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity));
        var result = await WotRegistryResolver.AcquireCloudEventSourceAsync(mapping, "/gs/tenant/models", query);
        await mapping.DisposeAsync();
        await Assert.That(result.Kind).IsEqualTo(WotReferenceTargetKind.ThingDescription);
        await Assert.That(result.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Source.First));
        using var retained = result.Document.OpenRead();
        await Assert.That("sha256:" + Convert.ToHexString(SHA256.HashData(retained)).ToLowerInvariant()).IsEqualTo(Digest(Source.First));
    }

    private static async Task<FederationException> CloudEventFailure(Source source, FederationReadBudget? budget = null) =>
        await Assert.That(async () => await WotRegistryResolver.AcquireCloudEventSourceAsync(source, source.Collection,
            WotCloudEventSourceQuery.Parse(WotCloudEventSourceQuery.Encode(Source.Identity)), budget))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected an explicit CloudEvents source failure.");

    private static readonly byte[] FragmentBytes = Encoding.UTF8.GetBytes("""
        {"id":"urn:test:device:v1","version":{"model":"2.0.0"},
        "properties":{"a/b":{"~name":42},"":null,"~1":true,"%2F":"once","a+b":"plus",
        "\u00e9":"unicode","#":"hash","01":"object-key","a b":"space","-":"dash"},
        "items":[{"x":1},false,null],"empty":[]}
        """);

    [Test]
    [Arguments("#/properties/a~1b/~0name", "42")]
    [Arguments("#%2Fproperties%2Fa~1b%2F~0name", "42")]
    [Arguments("#/properties/", "null")]
    [Arguments("#/properties/~01", "true")]
    [Arguments("#/properties/%252F", "\"once\"")]
    [Arguments("#/properties/a+b", "\"plus\"")]
    [Arguments("#/properties/%C3%A9", "\"unicode\"")]
    [Arguments("#/properties/%23", "\"hash\"")]
    [Arguments("#/properties/01", "\"object-key\"")]
    [Arguments("#/properties/a%20b", "\"space\"")]
    [Arguments("#/properties/-", "\"dash\"")]
    [Arguments("#/items/0/x", "1")]
    [Arguments("#/items/1", "false")]
    [Arguments("#/items/2", "null")]
    [Arguments("#/empty", "[]")]
    public async Task FragmentsDecodeOnceAndSelectExactJsonValuesWithoutChangingProvenance(string fragment, string expected)
    {
        var source = new Source { SecondBytes = FragmentBytes };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var reads = source.Reads.Count;
        var result = await WotRegistryResolver.SelectFragmentAsync(selected, fragment);
        await Assert.That(result.Value.GetRawText()).IsEqualTo(expected);
        await Assert.That(ReferenceEquals(result.ContainingDocument, selected)).IsTrue();
        await Assert.That(result.ContainingDocument.Version.ContentDigest).IsEqualTo(Digest(FragmentBytes));
        await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo("2.0.0");
        await Assert.That(result.ContainingDocument.Version.Origin).IsEqualTo(source.Context.Source);
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
    }

    [Test]
    public async Task RootAndNestedFragmentsRemainOwnedAfterNativeSourceDisposal()
    {
        var tree = CreateDirectoryTree(FragmentBytes);
        await using var mapping = await DirectoryMapping.OpenAsync(tree);
        var request = new WotVersionResolutionRequest(Source.Identity,
            WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel);
        var selected = await WotRegistryResolver.ResolveAsync(mapping, "/gs/tenant/models", request);
        await mapping.DisposeAsync();
        var reads = tree.Reads.Count;
        var root = await WotRegistryResolver.SelectFragmentAsync(selected, "#");
        var nested = await WotRegistryResolver.SelectFragmentAsync(selected, "#/items/0");
        await Assert.That(root.Value.GetProperty("id").GetString()).IsEqualTo(Source.Identity);
        await Assert.That(nested.Value.GetProperty("x").GetInt32()).IsEqualTo(1);
        await Assert.That(ReferenceEquals(root.ContainingDocument, nested.ContainingDocument)).IsTrue();
        await Assert.That(tree.Reads.Count).IsEqualTo(reads);
        using var content = selected.Document.OpenRead();
        await Assert.That("sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())
            .IsEqualTo(nested.ContainingDocument.Version.ContentDigest);
    }

    [Test]
    [Arguments("")]
    [Arguments("/properties")]
    [Arguments("#anchor")]
    [Arguments("##/properties")]
    [Arguments("#/%")]
    [Arguments("#/%0")]
    [Arguments("#/%GG")]
    [Arguments("#/% A")]
    [Arguments("#/%C3%28")]
    [Arguments("#/%C0%AF")]
    [Arguments("#/%ED%A0%80")]
    [Arguments("#/%FF")]
    [Arguments("#/raw space")]
    [Arguments("#/\u00e9")]
    [Arguments("#/a\\b")]
    [Arguments("#/a\nb")]
    [Arguments("#/a~")]
    [Arguments("#/a~2")]
    [Arguments("#/a%7E2")]
    public async Task InvalidFragmentSyntaxFailsBeforeParsingTheAcquiredDocument(string fragment)
    {
        var source = new Source { SecondBytes = [] };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.SelectFragmentAsync(selected, fragment)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected invalid fragment syntax.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-fragment");
    }

    [Test]
    [Arguments("#/absent")]
    [Arguments("#/Properties")]
    [Arguments("#/items/-")]
    [Arguments("#/items/01")]
    [Arguments("#/items/+1")]
    [Arguments("#/items/-1")]
    [Arguments("#/items/3")]
    [Arguments("#/items/2147483648")]
    [Arguments("#/items/%D9%A1")]
    [Arguments("#/items/1/x")]
    [Arguments("#/items/2/x")]
    [Arguments("#/empty/0")]
    public async Task MissingFragmentTargetsCannotFallbackOrChangeTheSelectedVersion(string fragment)
    {
        var source = new Source { SecondBytes = FragmentBytes };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var reads = source.Reads.Count;
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.SelectFragmentAsync(selected, fragment)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected an unresolved fragment.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("not-found");
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
        await Assert.That(selected.Version.VersionId).IsEqualTo("2.0.0");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task FragmentResultsAreBoundedByTheirOwnOutputAndCumulativeParsingWork(int bound)
    {
        var source = new Source { SecondBytes = FragmentBytes };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var baseline = new FederationReadBudget();
        var first = await WotRegistryResolver.SelectFragmentAsync(selected, "#/items/0/x", baseline);
        await Assert.That(first.Value.GetInt32()).IsEqualTo(1);
        await Assert.That(baseline.Requests).IsEqualTo(0L);
        await Assert.That(baseline.ObjectsRead).IsEqualTo(1L);
        await Assert.That(baseline.BytesRead).IsEqualTo((long)"#/items/0/x".Length);
        for (var delta = -1; delta <= 1; delta++)
        {
            var limits = bound switch
            {
                0 => new FederationReadLimits(maxWork: baseline.Work + delta),
                1 => new FederationReadLimits(maxDepth: 3 + delta),
                _ => new FederationReadLimits(maxTotalBytes: baseline.BytesRead + delta)
            };
            if (delta < 0)
            {
                var failure = await Assert.That(async () => await WotRegistryResolver.SelectFragmentAsync(
                    selected, "#/items/0/x", new(limits))).Throws<FederationException>()
                    ?? throw new InvalidOperationException("Expected an exhausted fragment budget.");
                await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            }
            else
            {
                var result = await WotRegistryResolver.SelectFragmentAsync(selected, "#/items/0/x", new(limits));
                await Assert.That(result.Value.GetInt32()).IsEqualTo(1);
            }
        }
        var tinyOutput = await WotRegistryResolver.SelectFragmentAsync(selected, "#/items/0/x",
            new(new(maxResultBytes: 1)));
        await Assert.That(tinyOutput.Value.GetRawText()).IsEqualTo("1");
        var tooLarge = await Assert.That(async () => await WotRegistryResolver.SelectFragmentAsync(
            selected, "#/items/0", new(new(maxResultBytes: 1)))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected a fragment result limit.");
        await Assert.That(tooLarge.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task FragmentOutputLimitsApplyToExactEncodedSelectedValueBytes(int delta)
    {
        var source = new Source { SecondBytes = FragmentBytes };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        const string fragment = "#/properties/a%20b";
        const string expected = "\"space\"";
        var budget = new FederationReadBudget(new(maxResultBytes: Encoding.UTF8.GetByteCount(expected) + delta));
        if (delta < 0)
        {
            var failure = await Assert.That(async () =>
                await WotRegistryResolver.SelectFragmentAsync(selected, fragment, budget)).Throws<FederationException>()
                ?? throw new InvalidOperationException("Expected an output-byte boundary failure.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var result = await WotRegistryResolver.SelectFragmentAsync(selected, fragment, budget);
            await Assert.That(result.Value.GetRawText()).IsEqualTo(expected);
            await Assert.That(result.ContainingDocument.Document.Length).IsGreaterThan((long)expected.Length);
        }
    }

    [Test]
    public async Task FragmentInputAndDocumentLimitsCannotBeBypassedByASmallSelectedValue()
    {
        var source = new Source { SecondBytes = FragmentBytes };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var documentFailure = await Assert.That(async () => await WotRegistryResolver.SelectFragmentAsync(
            selected, "#/items/0/x", new(new(maxObjectBytes: FragmentBytes.Length - 1))))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected the input-document limit.");
        await Assert.That(documentFailure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        var oversizedFragment = "#" + new string('/', FragmentBytes.Length);
        var inputBudget = new FederationReadBudget(new(maxObjectBytes: FragmentBytes.Length));
        var inputFailure = await Assert.That(async () =>
            await WotRegistryResolver.SelectFragmentAsync(selected, oversizedFragment, inputBudget))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected the input-fragment limit.");
        await Assert.That(inputFailure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(inputBudget.BytesRead).IsEqualTo(0L);
    }

    [Test]
    [Arguments("{")]
    [Arguments("""{"id":"urn:wrong","value":1}""")]
    [Arguments("""{"id":"urn:test:device:v1","value":1,"value":2}""")]
    public async Task FragmentSelectionValidatesTheCompleteDocumentBeforeReturningAMatch(string json)
    {
        var source = new Source { SecondBytes = Encoding.UTF8.GetBytes(json) };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var failure = await Assert.That(async () => await WotRegistryResolver.SelectFragmentAsync(selected, "#/value"))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected invalid retained JSON.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task FragmentCancellationAndRepeatedWorkDoNotReopenTheSource()
    {
        var source = new Source { SecondBytes = FragmentBytes };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var baseline = new FederationReadBudget();
        await WotRegistryResolver.SelectFragmentAsync(selected, "#/items/0/x", baseline);
        var shared = new FederationReadBudget(new(maxWork: baseline.Work));
        await WotRegistryResolver.SelectFragmentAsync(selected, "#/items/0/x", shared);
        var reads = source.Reads.Count;
        var failure = await Assert.That(async () =>
            await WotRegistryResolver.SelectFragmentAsync(selected, "#/items/0/x", shared))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected cumulative work exhaustion.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        var cancelledBudget = new FederationReadBudget();
        await Assert.That(async () => await WotRegistryResolver.SelectFragmentAsync(selected, "#",
            cancelledBudget, new CancellationToken(true))).Throws<OperationCanceledException>();
        await Assert.That(cancelledBudget.Work).IsEqualTo(0L);
        await Assert.That(cancelledBudget.BytesRead).IsEqualTo(0L);
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
    }

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
    public async Task LocalReferenceResolutionAppliesBaseLocalityFloorAndFragmentWithoutAnotherRead()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"version":{"model":"2.0.0"},"properties":{"status":{"type":"string"}}}""");
        var source = new Source { SecondBytes = bytes };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "2.0.0" });
        var request = source.Request with
        {
            MinimumVersion = "2.0.0",
            MinimumVersionKind = WotMinimumVersionKind.ThingModel
        };
        var reads = source.Reads.Count;
        var budget = new FederationReadBudget();

        var result = await WotRegistryResolver.ResolveLocalReferenceAsync(containing,
            "#/properties/status", new Uri("https://example.test/models/model.json"),
            new Uri("https://example.test/models/model.json"), request, budget);

        await Assert.That(result.Value.GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo("2.0.0");
        await Assert.That(result.ContainingDocument.Version.ContentDigest).IsEqualTo(Digest(bytes));
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
        await Assert.That(budget.Requests).IsEqualTo(0);
    }

    [Test]
    public async Task JsonLdReferenceResolutionUsesTheCapturedDocumentAndContextSet()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"ref":"#/properties/status","properties":{"status":{"type":"string"}}}""");
        var source = new Source { FirstBytes = bytes, SecondBytes = bytes };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "1.0.0" });
        var documentUri = new Uri("https://example.test/models/model.json");
        var reads = source.Reads.Count;

        var result = await WotRegistryResolver.ResolveReferenceAsync(source, source.Collection, containing,
            "", "ref", "#/properties/status", documentUri,
            new Dictionary<Uri, FederationDocument>(), source.Request);

        await Assert.That(result.Value.GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
    }

    [Test]
    public async Task LocalReferenceWithoutFragmentSelectsTheContainingDocument()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"properties":{"status":{"type":"string"}}}""");
        var source = new Source { FirstBytes = bytes };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "1.0.0" });
        var documentUri = new Uri("https://example.test/models/model.json");

        var result = await WotRegistryResolver.ResolveLocalReferenceAsync(containing,
            "", documentUri, documentUri, source.Request);

        await Assert.That(result.Value.GetProperty("properties").GetProperty("status")
            .GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(result.ContainingDocument.Version.VersionId).IsEqualTo("1.0.0");
    }

    [Test]
    public async Task LocalReferenceResolverRejectsExternalTargetsBeforeSourceReads()
    {
        var source = new Source();
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var reads = source.Reads.Count;

        await Assert.That(async () => await WotRegistryResolver.ResolveLocalReferenceAsync(containing,
            "other.json#/properties/status", new Uri("https://example.test/models/model.json"),
            new Uri("https://example.test/models/model.json"), source.Request))
            .Throws<ArgumentException>();

        await Assert.That(source.Reads.Count).IsEqualTo(reads);
    }

    [Test]
    public async Task ExternalReferenceResolutionSelectsWotIdAndAppliesFragmentUnderOneBudget()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"properties":{"status":{"type":"string"}}}""");
        var source = new Source { FirstBytes = bytes, SecondBytes = bytes };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        var reads = source.Reads.Count;
        var budget = new FederationReadBudget();

        var result = await WotRegistryResolver.ResolveReferenceAsync(source, source.Collection, containing,
            "urn:test:device:v1#/properties/status", new Uri("https://example.test/models/consumer.json"),
            new Uri("https://example.test/models/consumer.json"), source.Request, budget);

        await Assert.That(result.Value.GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(result.ContainingDocument.Version.WotId).IsEqualTo(Source.Identity);
        await Assert.That(result.ContainingDocument.Version.ContentDigest).IsEqualTo(Digest(bytes));
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(2);
        await Assert.That(source.Reads.Count).IsGreaterThan(reads);
        await Assert.That(budget.Requests).IsGreaterThan(0);
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
    public async Task LocalParsingUsesCapturedWotIdWhenDocumentOmitsId()
    {
        var source = new Source { SecondBytes = Encoding.UTF8.GetBytes("""{"version":{"model":"2.0.0"}}""") };
        var containing = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);

        var result = await WotRegistryResolver.ResolveLocalAsync(containing,
            source.Request with { IsDocumentLocal = true, MinimumVersion = "1.0.0" });

        await Assert.That(ReferenceEquals(result, containing)).IsTrue();
        await Assert.That(result.Version.WotId).IsEqualTo(Source.Identity);
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

    private static MemoryTreeReader CreateDirectoryTree(byte[]? secondBytes = null, bool description = false,
        string defaultVersionId = "2.0.0")
    {
        var tree = new MemoryTreeReader();
        const string resource = "/gs/tenant/models/assigned";
        var idName = description ? "thingdescriptionid" : "thingmodelid";
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
            var entity = Entity(resource + "/versions/" + id, idName, "assigned");
            entity["versionid"] = id;
            entity["ancestorid"] = "1.0.0";
            entity["isdefault"] = id == defaultVersionId;
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
        var meta = Entity(resource + "/meta", idName, "assigned");
        meta["readonly"] = false;
        meta["defaultversionid"] = defaultVersionId;
        meta["defaultversionsticky"] = false;
        var metaReference = Put("meta.json", new JsonObject { ["kind"] = "meta", ["entity"] = meta });
        var resourceReference = Put("resource.json", new JsonObject
        {
            ["kind"] = "resource", ["entity"] = new JsonObject { ["xid"] = resource, [idName] = "assigned" },
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
        if (description) { root["modelsource"]!["groups"]!["gs"]!["resources"]!["models"]!["singular"] = "thingdescription"; }
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
        internal int LargestMetadataBytes { get; private set; }
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
                var json = metadata.ToJsonString();
                LargestMetadataBytes = Math.Max(LargestMetadataBytes, Encoding.UTF8.GetByteCount(json));
                result = FederationReadResult.FromMetadata(request.Target, Context,
                    RegistryJson.Parse(json).RootElement);
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
            else if (path.Kind == RegistryPathKind.VersionCollection)
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
