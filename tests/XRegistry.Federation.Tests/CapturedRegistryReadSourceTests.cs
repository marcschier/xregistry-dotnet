// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using XRegistry.Publication;

namespace XRegistry.Federation.Tests;

public class CapturedRegistryReadSourceTests
{
    [Test]
    public async Task PublicationCaptureRetainsVerifiedAssociationsWithoutReopeningTheChannel()
    {
        var fixture = new Fixture();
        fixture.Root["sets"]!["team"]!["things"]!["a"]!["versions"]!["1.0.0"]!["contenttype"] = "application/tm+json";
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        var budget = new FederationReadBudget();
        var source = await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model, budget);
        await Assert.That(publication.ChannelReads).IsEqualTo(1);
        await Assert.That(publication.OpenedVersions.SequenceEqual([
            publication.ManifestDescriptor.Reference.Version, PublicationFixture.RegistryVersion,
            PublicationFixture.FirstVersion, PublicationFixture.SecondVersion])).IsTrue();
        await Assert.That(source.Context.Binding).IsEqualTo("publication");
        await Assert.That(source.Context.Source).IsEqualTo(opened.Manifest.Scope);
        await Assert.That(source.Context.Revision).IsEqualTo(opened.Manifest.SnapshotId);
        await Assert.That(source.Context.RootSha256).IsEqualTo(Digest(fixture.Bytes()));
        await Assert.That(source.GetExactRegistryBytes().SequenceEqual(opened.GetExactRegistryBytes())).IsTrue();
        await Assert.That(budget.BytesRead).IsEqualTo((long)fixture.Bytes().Length + Fixture.First.Length + Fixture.Second.Length);
        await Assert.That(budget.ObjectsRead).IsEqualTo(3L);
        await Assert.That(budget.Requests).IsEqualTo(2L);
        publication.FailOnRead = true;
        var document = await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource));
        await Assert.That(document.Document!.ContentType).IsEqualTo("application/tm+json");
        await Assert.That(document.Document.Base).IsEqualTo("https://example.test/relocated/model.json");
        await Assert.That(document.Document.Origin).IsNull();
        await Assert.That(opened.Manifest.Documents[0].OriginRegistry).IsEqualTo("urn:test:original-registry");
        await Assert.That(opened.Manifest.Documents[0].OriginVersion).IsEqualTo("/originals/site/models/model/versions/old");
        using var stream = document.Document.OpenRead();
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        await Assert.That(bytes.ToArray().SequenceEqual(Fixture.First)).IsTrue();
        var selected = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"));
        await Assert.That(selected.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(selected.Document.Base).IsEqualTo("https://example.test/relocated/model.json");
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(4);
        await Assert.That(publication.DisposedBindings).IsEqualTo(5);
    }

    [Test]
    [Arguments("missing-version")]
    [Arguments("resource")]
    [Arguments("size")]
    [Arguments("total-bytes")]
    [Arguments("objects")]
    [Arguments("model")]
    [Arguments("registry-size")]
    public async Task PublicationCaptureChecksGraphAndCumulativeLimitsBeforeAcquiringDocuments(string change)
    {
        var fixture = new Fixture();
        var publication = new PublicationFixture(fixture, manifest =>
        {
            if (change == "missing-version") { manifest["documents"]![0]!["viewVersion"] = Fixture.Resource + "/versions/9.0.0"; }
            if (change == "resource") { manifest["documents"]![0]!["viewVersion"] = "/missing/team/things/a/versions/1.0.0"; }
            if (change == "size") { manifest["documents"]![0]!["content"]!["size"] = 1_000_000; }
        });
        var opened = await publication.OpenAsync();
        var limits = new FederationReadLimits(
            maxTotalBytes: change == "total-bytes" ? fixture.Bytes().Length + Fixture.First.Length + Fixture.Second.Length - 1 : 64 * 1024 * 1024,
            maxObjects: change == "objects" ? 2 : 4096,
            maxObjectBytes: change == "registry-size" ? fixture.Bytes().Length - 1 : 100_000);
        var model = change == "model" ? RegistryModel.Compile(RegistryJson.Parse("""{"groups":{}}""")) : fixture.Model;
        var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, model, new(limits)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected pre-acquisition rejection.");
        await Assert.That(failure.Code).IsEqualTo(change is "total-bytes" or "objects" or "registry-size" or "size"
            ? FederationErrorCode.LimitExceeded : FederationErrorCode.InvalidPackage);
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
        await Assert.That(publication.ChannelReads).IsEqualTo(1);
    }

    [Test]
    [Arguments("size")]
    [Arguments("digest")]
    [Arguments("missing")]
    [Arguments("cancel")]
    public async Task PublicationCapturePropagatesByteFailuresAndNeverSubstitutesDocuments(string change)
    {
        var fixture = new Fixture();
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        if (change == "size") { publication.Objects[PublicationFixture.FirstVersion] = [1, 2, 3]; }
        if (change == "digest")
        {
            var changed = (byte[])Fixture.First.Clone();
            changed[0] = (byte)'X';
            publication.Objects[PublicationFixture.FirstVersion] = changed;
        }
        if (change == "missing") { publication.Objects.Remove(PublicationFixture.FirstVersion); }
        if (change == "cancel") { publication.OnOpen = _ => cancellation.Cancel(); }
        if (change == "cancel")
        {
            await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model,
                cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        }
        else
        {
            var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model))
                .Throws<PublicationException>() ?? throw new InvalidOperationException("Expected exact descriptor failure.");
            await Assert.That(failure.Code).IsEqualTo(change switch
            {
                "size" => "size_mismatch", "digest" => "digest_mismatch", _ => "object_not_found"
            });
        }
        await Assert.That(publication.ChannelReads).IsEqualTo(1);
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(3);
        await Assert.That(publication.OpenedVersions[^1]).IsEqualTo(PublicationFixture.FirstVersion);
        await Assert.That(publication.DisposedBindings).IsEqualTo(4);
    }

    [Test]
    [Arguments("bytes", -1)]
    [Arguments("bytes", 0)]
    [Arguments("bytes", 1)]
    [Arguments("objects", -1)]
    [Arguments("objects", 0)]
    [Arguments("objects", 1)]
    [Arguments("work", -1)]
    [Arguments("work", 0)]
    [Arguments("work", 1)]
    [Arguments("requests", -1)]
    [Arguments("requests", 0)]
    [Arguments("requests", 1)]
    public async Task PublicationCaptureReservationsHaveInclusiveCumulativeBoundaries(string limit, int delta)
    {
        var fixture = new Fixture();
        var baselinePublication = new PublicationFixture(fixture);
        var baseline = new FederationReadBudget();
        _ = await CapturedRegistryReadSource.FromPublicationAsync(await baselinePublication.OpenAsync(), fixture.Model, baseline);
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        var budget = new FederationReadBudget(new(
            maxTotalBytes: limit == "bytes" ? baseline.BytesRead + delta : 64 * 1024 * 1024,
            maxObjects: limit == "objects" ? baseline.ObjectsRead + delta : 4096,
            maxRequests: limit == "requests" ? baseline.Requests + delta : 4096,
            maxWork: limit == "work" ? baseline.Work + delta : 1_000_000));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model, budget))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected reservation exhaustion.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
            await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
        }
        else
        {
            var source = await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model, budget);
            await Assert.That(source.GetExactRegistryBytes().SequenceEqual(fixture.Bytes())).IsTrue();
            await Assert.That(budget.BytesRead).IsEqualTo(baseline.BytesRead);
            await Assert.That(budget.ObjectsRead).IsEqualTo(baseline.ObjectsRead);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
            await Assert.That(budget.Requests).IsEqualTo(baseline.Requests);
            await Assert.That(publication.OpenedVersions.Count).IsEqualTo(4);
        }
    }

    [Test]
    public async Task PublicationCaptureHonorsCancellationBeforeRetainingOrAcquiringBytes()
    {
        var fixture = new Fixture();
        var publication = new PublicationFixture(fixture);
        var opened = await publication.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var budget = new FederationReadBudget();
        await Assert.That(async () => await CapturedRegistryReadSource.FromPublicationAsync(opened, fixture.Model,
            budget, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(budget.BytesRead).IsEqualTo(0L);
        await Assert.That(budget.ObjectsRead).IsEqualTo(0L);
        await Assert.That(budget.Work).IsEqualTo(0L);
        await Assert.That(publication.OpenedVersions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CapturedReadsPreserveOriginalBytesMetadataAndResponseLocalNavigation()
    {
        var fixture = new Fixture();
        var bytes = fixture.Bytes();
        var original = (byte[])bytes.Clone();
        var budget = new FederationReadBudget();
        var source = fixture.Open(bytes, budget);
        Array.Fill(bytes, (byte)0);
        fixture.Root["registryid"] = "changed";
        fixture.Documents.Clear();
        var returned = source.GetExactRegistryBytes();
        await Assert.That(returned.SequenceEqual(original)).IsTrue();
        Array.Fill(returned, (byte)0);
        await Assert.That(source.GetExactRegistryBytes().SequenceEqual(original)).IsTrue();
        await Assert.That(source.Context.RootSha256).IsEqualTo(Digest(original));
        await Assert.That(source.Context.Revision).IsEqualTo("snapshot-1");
        await Assert.That(source.Context.RootPath).IsEqualTo("captured");
        await Assert.That(source.Context.RequestedRevision).IsEqualTo("stable");
        await Assert.That(ReferenceEquals(source.Model, fixture.Model)).IsTrue();

        var root = await source.ReadAsync(new(FederationOperation.Entity, "/"));
        await Assert.That(root.Metadata.GetProperty("entity").GetProperty("registryid").GetString()).IsEqualTo("captured");
        await Assert.That(root.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        var resource = await source.ReadAsync(new(FederationOperation.Entity, Fixture.Resource));
        var entity = resource.Metadata.GetProperty("entity");
        await Assert.That(entity.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/entity/versions/1.0.0");
        await Assert.That(entity.GetProperty("versions").GetProperty("2.0.0").GetProperty("self").GetString())
            .IsEqualTo("#/entity/versions/2.0.0");
        await Assert.That(entity.GetProperty("versions").GetProperty("1.0.0").GetProperty("epoch").GetUInt64())
            .IsEqualTo(71UL);
        await Assert.That(entity.GetProperty("versions").GetProperty("1.0.0").GetProperty("createdat").GetString())
            .IsEqualTo("2025-01-02T03:04:05Z");
        var meta = await source.ReadAsync(new(FederationOperation.Entity, Fixture.Resource + "/meta"));
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/related/defaultversion");
        await Assert.That(meta.Metadata.GetProperty("related").GetProperty("defaultversion").GetProperty("versionid").GetString())
            .IsEqualTo("1.0.0");
        var collection = await source.ReadAsync(new(FederationOperation.Collection, Fixture.Collection));
        await Assert.That(collection.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(collection.Metadata.GetProperty("entities").GetProperty("a").GetProperty("self").GetString())
            .IsEqualTo("#/entities/a");
        var model = await source.ReadAsync(new(FederationOperation.Model, "/"));
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(model.Metadata.GetProperty("modelsource").GetRawText()),
            JsonNode.Parse(fixture.Model.Source.RootElement.GetRawText()))).IsTrue();
        var capabilities = await source.ReadAsync(new(FederationOperation.Capabilities, "/"));
        await Assert.That(capabilities.Metadata.GetProperty("available").GetProperty("entities").GetProperty("mutable").GetBoolean()).IsFalse();
        await Assert.That(budget.Requests).IsEqualTo(0L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedDocumentsUseExactAssociationsAndTheRecordedDefaultWithoutRewriting(bool exact)
    {
        var fixture = new Fixture();
        var source = fixture.Open();
        var result = await source.ReadAsync(new(FederationOperation.Document,
            Fixture.Resource + (exact ? "/versions/2.0.0" : "")));
        await Assert.That(result.SelectedXid).IsEqualTo(Fixture.Resource + (exact ? "/versions/2.0.0" : "/versions/1.0.0"));
        var document = result.Document ?? throw new InvalidOperationException("Expected associated document bytes.");
        await Assert.That(document.Base).IsEqualTo("https://example.test/original/model.json");
        await Assert.That(document.Origin).IsEqualTo("urn:test:original-document");
        await Assert.That(document.ContentType).IsEqualTo("application/tm+json");
        using var stream = document.OpenRead();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        await Assert.That(copy.ToArray().SequenceEqual(exact ? Fixture.Second : Fixture.First)).IsTrue();
        await Assert.That(result.Context).IsEqualTo(source.Context);
    }

    [Test]
    public async Task CapturedGraphFeedsModelDerivedWotIdentityAndVersionResolutionWithoutLiveReads()
    {
        var fixture = new Fixture();
        var budget = new FederationReadBudget();
        var source = fixture.Open(budget: budget);
        var index = await WotCapturedUriIdentityIndex.CaptureAsync(
            [new(source, new Uri("https://example.test/definition"), WotCapturedUriIdentityKind.DocumentView,
                Fixture.Resource + "/versions/1.0.0")], budget);
        var reference = new WotAuthoredReferenceOccurrence("/data", "tm:ref", "tm:ref", "definition",
            WotReferenceUriResolver.Resolve("definition", new Uri("https://example.test/plan"), new Uri("https://example.test/plan")));
        var captured = index.ClassifyOrdinaryReference(reference, budget) ??
            throw new InvalidOperationException("Expected a captured Thing Model.");
        await Assert.That(captured.ResourceKind).IsEqualTo("thingmodel");
        await Assert.That(captured.VersionId).IsEqualTo("1.0.0");
        await Assert.That(captured.WotId).IsEqualTo(Fixture.WotId);
        var acquired = await WotRegistryResolver.ResolveAsync(source, Fixture.Collection,
            new(Fixture.WotId, WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel, VersionIdPin: "1.0.0"), budget);
        await Assert.That(acquired.Version.VersionId).IsEqualTo("1.0.0");
        await Assert.That(acquired.Version.ContentDigest).IsEqualTo("sha256:" + Digest(Fixture.First));
        await Assert.That(acquired.Document.Base).IsEqualTo("https://example.test/original/model.json");
        await Assert.That(source.GetExactRegistryBytes().SequenceEqual(fixture.Bytes())).IsTrue();
    }

    [Test]
    [Arguments("v1", true)]
    [Arguments("V1", true)]
    [Arguments("absent", false)]
    public async Task CapturedResourceLabelSelectionUsesItsCapturedDefaultVersion(string label, bool found)
    {
        var source = new Fixture().Open();
        var request = new FederationReadRequest(FederationOperation.Collection, Fixture.Collection, new("release", label));
        if (found)
        {
            var result = await source.ReadAsync(request);
            await Assert.That(result.SelectedXid).IsEqualTo(Fixture.Resource);
            await Assert.That(result.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        }
        else
        {
            var failure = await Assert.That(async () => await source.ReadAsync(request)).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected a missing captured label.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.NotFound);
        }
    }

    [Test]
    public async Task CapturedAliasesResolveOnlyTheirCapturedSameTypeTargetAndPreserveAliasMetadata()
    {
        var fixture = new Fixture();
        fixture.AddAlias();
        var source = fixture.Open();
        var metadata = await source.ReadAsync(new(FederationOperation.Entity, Fixture.Collection + "/alias"));
        await Assert.That(metadata.Metadata.GetProperty("entity").GetProperty("meta").GetProperty("xref").GetString())
            .IsEqualTo(Fixture.Resource);
        var document = await source.ReadAsync(new(FederationOperation.Document, Fixture.Collection + "/alias"));
        await Assert.That(document.SelectedXid).IsEqualTo(Fixture.Resource + "/versions/1.0.0");
        var failure = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Entity,
            Fixture.Collection + "/alias/versions/1.0.0"))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected undefined alias-Version document view.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        var ambiguous = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Collection,
            Fixture.Collection, new("release", "v1")))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected two visible Resources with the selected label.");
        await Assert.That(ambiguous.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    [Arguments("missing-map")]
    [Arguments("wrong-count")]
    [Arguments("external-navigation")]
    [Arguments("wrong-self")]
    [Arguments("wrong-xid")]
    [Arguments("wrong-id")]
    [Arguments("wrong-version")]
    [Arguments("missing-default")]
    [Arguments("case-collision")]
    [Arguments("duplicate-json")]
    [Arguments("model-mismatch")]
    [Arguments("source-mismatch")]
    [Arguments("missing-model")]
    [Arguments("mutable-capabilities")]
    [Arguments("bad-count-type")]
    [Arguments("bad-alias")]
    [Arguments("dangling-alias")]
    [Arguments("model-not-object")]
    [Arguments("shortself")]
    [Arguments("invalid-pointer-encoding")]
    [Arguments("invalid-pointer-utf8")]
    public async Task CapturedGraphsRejectIncompleteContradictoryOrUnretrievableState(string change)
    {
        var fixture = new Fixture();
        var resource = fixture.Root["sets"]!["team"]!["things"]!["a"]!.AsObject();
        switch (change)
        {
            case "missing-map": fixture.Root.Remove("sets"); break;
            case "wrong-count": fixture.Root["setscount"] = 2; break;
            case "external-navigation": fixture.Root["setsurl"] = "https://untrusted.invalid/sets"; break;
            case "wrong-self": resource["self"] = "#/sets/team/things/another"; break;
            case "wrong-xid": resource["xid"] = "/sets/team/things/another"; break;
            case "wrong-id": resource["thingmodelid"] = "another"; break;
            case "wrong-version": resource["versions"]!["1.0.0"]!["versionid"] = "2.0.0"; break;
            case "missing-default": resource["meta"]!["defaultversionid"] = "3.0.0"; break;
            case "case-collision": fixture.Root["sets"]!.AsObject()["TEAM"] = fixture.Root["sets"]!["team"]!.DeepClone(); fixture.Root["setscount"] = 2; break;
            case "model-mismatch": fixture.Root["model"] = new JsonObject(); break;
            case "source-mismatch": fixture.Root["modelsource"] = new JsonObject(); break;
            case "missing-model": fixture.Root.Remove("modelsource"); break;
            case "mutable-capabilities": fixture.Root["capabilities"]!["available"]!["entities"]!["mutable"] = true; break;
            case "bad-count-type": fixture.Root["setscount"] = "one"; break;
            case "bad-alias": fixture.AddAlias(); fixture.Root["sets"]!["team"]!["things"]!["alias"]!["meta"]!["xref"] = Fixture.Resource + "/versions/1.0.0"; break;
            case "dangling-alias": fixture.AddAlias(); fixture.Root["sets"]!["team"]!["things"]!["alias"]!["meta"]!["xref"] = Fixture.Collection + "/missing"; break;
            case "model-not-object": fixture.Root["model"] = 1; break;
            case "shortself": resource["shortself"] = "https://untrusted.invalid/short"; break;
            case "invalid-pointer-encoding": resource["self"] = "#/sets/team/things/%QQ"; break;
            case "invalid-pointer-utf8": resource["self"] = "#/sets/team/things/%FF"; break;
        }
        var bytes = fixture.Bytes();
        if (change == "duplicate-json")
        {
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"registryid\":\"captured\"",
                "\"registryid\":\"captured\",\"registryid\":\"other\"", StringComparison.Ordinal));
        }
        var failure = await Assert.That(() => fixture.Open(bytes)).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected invalid captured graph.");
        await Assert.That(failure.Code is FederationErrorCode.InvalidPackage or FederationErrorCode.NotFound).IsTrue();
    }

    [Test]
    [Arguments("a~b")]
    [Arguments("a:b")]
    [Arguments("a@b")]
    public async Task CapturedPointerTokensUseRfc6901AndUriFragmentEncoding(string id)
    {
        var fixture = new Fixture();
        fixture.RenameResource(id);
        var source = fixture.Open();
        var xid = Fixture.Collection + "/" + Uri.EscapeDataString(id);
        var result = await source.ReadAsync(new(FederationOperation.Collection, Fixture.Collection));
        var entity = result.Metadata.GetProperty("entities").GetProperty(id);
        var token = Uri.EscapeDataString(id.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entities/" + token);
        await Assert.That(entity.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/entities/" + token + "/versions/1.0.0");
        var version = await source.ReadAsync(new(FederationOperation.Entity, xid + "/versions/1.0.0"));
        await Assert.That(version.Metadata.GetProperty("entity").GetProperty("self").GetString()).IsEqualTo("#/entity");
        var document = await source.ReadAsync(new(FederationOperation.Document, xid));
        await Assert.That(document.SelectedXid).IsEqualTo(xid + "/versions/1.0.0");
    }

    [Test]
    [Arguments("a/b")]
    [Arguments("a%b")]
    [Arguments("a%2Fb")]
    [Arguments("a b")]
    [Arguments("\u03b1")]
    public async Task CapturedCollectionIdsUseCoreGrammarRatherThanArbitraryJsonPointerKeys(string id)
    {
        var fixture = new Fixture();
        fixture.RenameResource(id);
        var failure = await Assert.That(() => fixture.Open()).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected an invalid Core collection ID.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CapturedGraphAcceptsOmittedOptionalCollectionHintsAndPresentEmptyDocumentBytes()
    {
        var fixture = new Fixture();
        foreach (var item in new[]
        {
            (fixture.Root, "sets"), (fixture.Root["sets"]!["team"]!.AsObject(), "things"),
            (fixture.Root["sets"]!["team"]!["things"]!["a"]!.AsObject(), "versions")
        })
        {
            item.Item1.Remove(item.Item2 + "count");
            item.Item1.Remove(item.Item2 + "url");
        }
        fixture.Documents[Fixture.Resource + "/versions/1.0.0"] = new([], "application/octet-stream");
        var source = fixture.Open();
        var document = await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource));
        await Assert.That(document.Document!.Length).IsEqualTo(0L);
        var collection = await source.ReadAsync(new(FederationOperation.Collection, Fixture.Collection));
        await Assert.That(collection.Metadata.GetProperty("entities").GetProperty("a").TryGetProperty("versionscount", out _)).IsFalse();
    }

    [Test]
    public async Task CapturedMapKeysSupplyOmittedEntityIdAttributes()
    {
        var fixture = new Fixture();
        var group = fixture.Root["sets"]!["team"]!.AsObject();
        group.Remove("setid");
        var resource = group["things"]!["a"]!.AsObject();
        resource.Remove("thingmodelid");
        resource["meta"]!.AsObject().Remove("thingmodelid");
        foreach (var version in resource["versions"]!.AsObject())
        {
            version.Value!.AsObject().Remove("versionid");
            version.Value.AsObject().Remove("thingmodelid");
        }
        var source = fixture.Open();
        var result = await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource));
        await Assert.That(result.SelectedXid).IsEqualTo(Fixture.Resource + "/versions/1.0.0");
    }

    [Test]
    public async Task CapturedEmptyGraphIsCompleteAndOpaqueDomainUrlsAreUnchanged()
    {
        var fixture = new Fixture();
        fixture.Root["sets"] = new JsonObject();
        fixture.Root["setscount"] = 0;
        fixture.Root["advisory"] = JsonNode.Parse("""{"self":"https://untrusted.invalid/no-fetch","tm:ref":"other"}""");
        fixture.Documents.Clear();
        var source = fixture.Open();
        var result = await source.ReadAsync(new(FederationOperation.Collection, "/sets"));
        await Assert.That(result.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(result.Metadata.GetProperty("entities").EnumerateObject().Any()).IsFalse();
        var root = await source.ReadAsync(new(FederationOperation.Entity, "/"));
        await Assert.That(root.Metadata.GetProperty("entity").GetProperty("advisory").GetProperty("self").GetString())
            .IsEqualTo("https://untrusted.invalid/no-fetch");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    public async Task CapturedResultByteLimitIsIndependentOfTheInputObjectLimit(int delta)
    {
        var fixture = new Fixture();
        var result = await fixture.Open().ReadAsync(new(FederationOperation.Entity, "/"));
        var length = Encoding.UTF8.GetByteCount(result.Metadata.GetRawText());
        var source = fixture.Open(budget: new(new(maxObjectBytes: fixture.Bytes().Length, maxResultBytes: length + delta)));
        if (delta < 0)
        {
            var failure = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Entity, "/")))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a bounded metadata result.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var actual = await source.ReadAsync(new(FederationOperation.Entity, "/"));
            await Assert.That(actual.Metadata.GetProperty("entity").GetProperty("registryid").GetString()).IsEqualTo("captured");
        }
    }

    [Test]
    [Arguments("resource")]
    [Arguments("missing-version")]
    [Arguments("duplicate-escaped-version")]
    [Arguments("metadata-only")]
    public async Task CapturedDocumentAssociationsMustNameUniquePresentDocumentBearingExactVersions(string change)
    {
        var fixture = new Fixture();
        var document = fixture.Documents[Fixture.Resource + "/versions/1.0.0"];
        switch (change)
        {
            case "resource": fixture.Documents[Fixture.Resource] = document; break;
            case "missing-version": fixture.Documents[Fixture.Resource + "/versions/9.0.0"] = document; break;
            case "duplicate-escaped-version": fixture.Documents[Fixture.Resource + "/versions/%31.0.0"] = document; break;
            case "metadata-only":
                var model = JsonNode.Parse(fixture.Model.Source.RootElement.GetRawText())!;
                model["groups"]!["sets"]!["resources"]!["things"]!["hasdocument"] = false;
                fixture.Model = RegistryModel.Compile(RegistryJson.Parse(model.ToJsonString()));
                fixture.Root["modelsource"] = model;
                break;
        }
        var failure = await Assert.That(() => fixture.Open()).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected invalid document association.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task MissingCapturedBytesNeverUseAnotherVersionOrAnUnrecordedLocation()
    {
        var fixture = new Fixture();
        fixture.Documents.Remove(Fixture.Resource + "/versions/1.0.0");
        var source = fixture.Open();
        var failure = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected missing default bytes.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.NotFound);
        var exact = await source.ReadAsync(new(FederationOperation.Document, Fixture.Resource + "/versions/2.0.0"));
        await Assert.That(exact.SelectedXid).IsEqualTo(Fixture.Resource + "/versions/2.0.0");
    }

    [Test]
    public async Task CapturedSourceChecksRevisionCommitmentUnsupportedViewsAndCancellation()
    {
        var fixture = new Fixture();
        var failure = await Assert.That(() => fixture.Open(context: new("capture", "urn:test:snapshot", "snapshot-1", true, new('0', 64))))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected a root commitment mismatch.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.IntegrityError);
        await Assert.That(() => fixture.Open(context: new("capture", "urn:test:snapshot")))
            .Throws<ArgumentException>();
        var source = fixture.Open();
        var unsupported = await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Entity, "/",
            representation: FederationRepresentation.ApiView))).Throws<FederationException>() ??
            throw new InvalidOperationException("Expected unsupported API view.");
        await Assert.That(unsupported.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var budget = new FederationReadBudget();
        await Assert.That(() => fixture.Open(budget: budget, token: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(budget.Work).IsEqualTo(0L);
        await Assert.That(budget.BytesRead).IsEqualTo(0L);
        await Assert.That(async () => await source.ReadAsync(new(FederationOperation.Entity, "/"), cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("bytes", -1)]
    [Arguments("bytes", 0)]
    [Arguments("bytes", 1)]
    [Arguments("objects", -1)]
    [Arguments("objects", 0)]
    [Arguments("objects", 1)]
    [Arguments("work", -1)]
    [Arguments("work", 0)]
    [Arguments("work", 1)]
    [Arguments("object", -1)]
    [Arguments("object", 0)]
    [Arguments("object", 1)]
    public async Task CapturedInputLimitsHaveInclusiveBoundaries(string limit, int delta)
    {
        var fixture = new Fixture();
        var baseline = new FederationReadBudget();
        _ = fixture.Open(budget: baseline);
        var limits = new FederationReadLimits(
            maxTotalBytes: limit == "bytes" ? baseline.BytesRead + delta : 64 * 1024 * 1024,
            maxObjects: limit == "objects" ? baseline.ObjectsRead + delta : 4096,
            maxWork: limit == "work" ? baseline.Work + delta : 1_000_000,
            maxObjectBytes: limit == "object" ? fixture.Bytes().Length + delta : 16 * 1024 * 1024);
        if (delta < 0)
        {
            var failure = await Assert.That(() => fixture.Open(budget: new(limits))).Throws<FederationException>() ??
                throw new InvalidOperationException("Expected captured input limit exhaustion.");
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
        else
        {
            var budget = new FederationReadBudget(limits);
            var source = fixture.Open(budget: budget);
            await Assert.That(source.GetExactRegistryBytes().SequenceEqual(fixture.Bytes())).IsTrue();
            await Assert.That(budget.BytesRead).IsEqualTo(baseline.BytesRead);
            await Assert.That(budget.ObjectsRead).IsEqualTo(baseline.ObjectsRead);
            await Assert.That(budget.Work).IsEqualTo(baseline.Work);
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class PublicationFixture
    {
        internal const string RegistryVersion = "/captures/team/objects/registry/versions/fixed";
        internal const string FirstVersion = "/captures/team/objects/one/versions/fixed";
        internal const string SecondVersion = "/captures/team/objects/two/versions/fixed";
        private static readonly PublicationRegistryAccess Access = new("http", "https://publisher.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        internal Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        internal List<string> OpenedVersions { get; } = [];
        internal int ChannelReads { get; private set; }
        internal int DisposedBindings { get; private set; }
        internal PublicationByteDescriptor ManifestDescriptor { get; }
        internal bool FailOnRead { get; set; }
        internal Action<string>? OnOpen { get; set; }

        internal PublicationFixture(Fixture fixture, Action<JsonObject>? change = null)
        {
            var registry = fixture.Bytes();
            Objects[RegistryVersion] = registry;
            Objects[FirstVersion] = Fixture.First;
            Objects[SecondVersion] = Fixture.Second;
            var manifest = new JsonObject
            {
                ["format"] = "xregistry-publication/1", ["scope"] = "urn:test:scope",
                ["capture"] = "assembled", ["completeness"] = "linked",
                ["registry"] = Descriptor(RegistryVersion, registry),
                ["documents"] = new JsonArray(
                    Association("1.0.0", FirstVersion, Fixture.First),
                    Association("2.0.0", SecondVersion, Fixture.Second))
            };
            change?.Invoke(manifest);
            var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            var manifestXid = "/publications/team/snapshots/" + Digest(manifestBytes) + "/versions/manifest";
            ManifestDescriptor = new(new(Access, manifestXid), "sha256:" + Digest(manifestBytes), manifestBytes.Length);
            Objects[manifestXid] = manifestBytes;
        }

        internal ValueTask<PublicationOpenedSnapshot> OpenAsync() =>
            new PublicationReadClient((_, _) => ValueTask.FromResult<IPublicationReadBinding>(new Binding(this)))
                .OpenChannelAsync(Access, "/publications/team/channels/stable");

        private static JsonObject Association(string version, string storage, byte[] bytes) => new()
        {
            ["viewVersion"] = Fixture.Resource + "/versions/" + version,
            ["originRegistry"] = "urn:test:original-registry",
            ["originVersion"] = "/originals/site/models/model/versions/old",
            ["base"] = "https://example.test/relocated/model.json", ["content"] = Descriptor(storage, bytes)
        };

        private static JsonObject Descriptor(string version, byte[] bytes) => new()
        {
            ["reference"] = new JsonObject
            {
                ["registry"] = new JsonObject { ["binding"] = Access.Binding, ["endpoint"] = Access.Endpoint, ["parameters"] = new JsonObject() },
                ["version"] = version
            },
            ["digest"] = "sha256:" + Digest(bytes), ["size"] = bytes.Length
        };

        private sealed class Binding(PublicationFixture owner) : IPublicationReadBinding
        {
            public PublicationRegistryAccess Access => PublicationFixture.Access;
            public ValueTask<PublicationChannelMetadata> ReadChannelMetadataAsync(string channelResource, CancellationToken cancellationToken = default)
            {
                if (owner.FailOnRead) { throw new InvalidOperationException("The selected channel must not reopen."); }
                owner.ChannelReads++;
                return ValueTask.FromResult(new PublicationChannelMetadata("urn:test:scope", owner.ManifestDescriptor, 1));
            }
            public ValueTask<Stream> OpenVersionDocumentAsync(string version, CancellationToken cancellationToken = default)
            {
                if (owner.FailOnRead) { throw new InvalidOperationException("Retained document reads must not reopen a binding."); }
                owner.OpenedVersions.Add(version);
                owner.OnOpen?.Invoke(version);
                if (!owner.Objects.TryGetValue(version, out var bytes))
                {
                    throw new PublicationException("object_not_found", "", "The exact stored Version is unavailable.");
                }
                return ValueTask.FromResult<Stream>(new MemoryStream(bytes, false));
            }
            public ValueTask DisposeAsync()
            {
                owner.DisposedBindings++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Fixture
    {
        internal const string Collection = "/sets/team/things";
        internal const string Resource = Collection + "/a";
        internal const string WotId = "urn:test:device:v1";
        internal static readonly byte[] First = Encoding.UTF8.GetBytes(" {\"id\":\"urn:test:device:v1\",\"version\":{\"model\":\"1.0.0\"},\"properties\":{\"a\":{\"type\":\"number\"}}}\r\n");
        internal static readonly byte[] Second = Encoding.UTF8.GetBytes("{\"id\":\"urn:test:device:v1\",\"version\":{\"model\":\"2.0.0\"}}\n");
        internal RegistryModel Model { get; set; } = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"sets":{"singular":"set","resources":{"things":{"singular":"thingmodel","versionmode":"semver","singleversionroot":true}}}}}
            """));
        internal JsonObject Root { get; }
        internal Dictionary<string, FederationDocument> Documents { get; } = new(StringComparer.Ordinal)
        {
            [Resource + "/versions/1.0.0"] = new(First, "application/tm+json", "https://example.test/original/model.json", "urn:test:original-document"),
            [Resource + "/versions/2.0.0"] = new(Second, "application/tm+json", "https://example.test/original/model.json", "urn:test:original-document")
        };

        internal Fixture()
        {
            Root = new()
            {
                ["registryid"] = "captured", ["specversion"] = "1.0-rc4", ["xid"] = "/", ["self"] = "#",
                ["modelsource"] = JsonNode.Parse(Model.Source.RootElement.GetRawText()),
                ["capabilities"] = JsonNode.Parse("""
                    {"available":{"entities":{"mutable":false},"model":{"mutable":false},"capabilities":{"mutable":false}},
                    "federation":{"resolution":"producer"}}
                    """),
                ["setscount"] = 1, ["setsurl"] = "#/sets", ["sets"] = new JsonObject
                {
                    ["team"] = new JsonObject
                    {
                        ["setid"] = "team", ["xid"] = "/sets/team", ["self"] = "#/sets/team",
                        ["thingscount"] = 1, ["thingsurl"] = "#/sets/team/things", ["things"] = new JsonObject
                        {
                            ["a"] = new JsonObject
                            {
                                ["thingmodelid"] = "a", ["xid"] = Resource, ["self"] = "#/sets/team/things/a",
                                ["metaurl"] = "#/sets/team/things/a/meta",
                                ["meta"] = new JsonObject
                                {
                                    ["thingmodelid"] = "a", ["xid"] = Resource + "/meta", ["self"] = "#/sets/team/things/a/meta",
                                    ["defaultversionid"] = "1.0.0", ["defaultversionsticky"] = false,
                                    ["defaultversionurl"] = "#/sets/team/things/a/versions/1.0.0"
                                },
                                ["versionscount"] = 2, ["versionsurl"] = "#/sets/team/things/a/versions",
                                ["versions"] = new JsonObject { ["1.0.0"] = Version("1.0.0"), ["2.0.0"] = Version("2.0.0") }
                            }
                        }
                    }
                }
            };
        }

        internal void AddAlias()
        {
            var group = Root["sets"]!["team"]!;
            group["thingscount"] = 2;
            group["things"]!["alias"] = new JsonObject
            {
                ["thingmodelid"] = "alias", ["xid"] = Collection + "/alias", ["self"] = "#/sets/team/things/alias",
                ["metaurl"] = "#/sets/team/things/alias/meta",
                ["meta"] = new JsonObject
                {
                    ["thingmodelid"] = "alias", ["xid"] = Collection + "/alias/meta",
                    ["self"] = "#/sets/team/things/alias/meta", ["xref"] = Resource
                }
            };
        }

        internal void RenameResource(string id)
        {
            var map = Root["sets"]!["team"]!["things"]!.AsObject();
            var resource = map["a"]!.AsObject();
            map.Remove("a");
            map[id] = resource;
            var xid = Collection + "/" + Uri.EscapeDataString(id);
            var pointer = "#/sets/team/things/" + Uri.EscapeDataString(
                id.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            resource["thingmodelid"] = id;
            resource["xid"] = xid;
            resource["self"] = pointer;
            resource["metaurl"] = pointer + "/meta";
            resource["versionsurl"] = pointer + "/versions";
            resource["meta"]!["thingmodelid"] = id;
            resource["meta"]!["xid"] = xid + "/meta";
            resource["meta"]!["self"] = pointer + "/meta";
            resource["meta"]!["defaultversionurl"] = pointer + "/versions/1.0.0";
            foreach (var version in resource["versions"]!.AsObject())
            {
                version.Value!["thingmodelid"] = id;
                version.Value!["xid"] = xid + "/versions/" + version.Key;
                version.Value!["self"] = pointer + "/versions/" + version.Key;
                var document = Documents[Resource + "/versions/" + version.Key];
                Documents.Remove(Resource + "/versions/" + version.Key);
                Documents[xid + "/versions/" + version.Key] = document;
            }
        }

        internal byte[] Bytes() => Encoding.UTF8.GetBytes(Root.ToJsonString());
        internal CapturedRegistryReadSource Open(byte[]? bytes = null, FederationReadBudget? budget = null,
            NativeRegistryContext? context = null, CancellationToken token = default) =>
            new(bytes ?? Bytes(), Model, context ?? new("capture", "urn:test:snapshot", "snapshot-1", true)
            { RootPath = "captured", RequestedRevision = "stable" }, Documents, budget, token);

        private static JsonObject Version(string id) => new()
        {
            ["thingmodelid"] = "a", ["versionid"] = id, ["xid"] = Resource + "/versions/" + id,
            ["self"] = "#/sets/team/things/a/versions/" + id, ["wotid"] = WotId,
            ["epoch"] = 71, ["createdat"] = "2025-01-02T03:04:05Z",
            ["labels"] = new JsonObject { ["release"] = id == "1.0.0" ? "v1" : "v2" }
        };
    }
}
