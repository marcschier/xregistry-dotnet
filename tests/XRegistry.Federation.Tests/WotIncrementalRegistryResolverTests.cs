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

public class WotIncrementalRegistryResolverTests
{
    private const string Identity = "urn:example:pump";
    private const string Line = "urn:example:line:a";
    private const string OtherLine = "urn:example:line:b";
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes(
        """{ "id": "urn:example:pump", "version": { "model": "vendor", "instance": 5 }, "value": 42 }""");

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IncrementalNativeAcquisitionUsesTheCapturedProfileAndExactNumericLineSelection(bool description)
    {
        var source = new Source(description);
        var result = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { MinimumVersion = "10" });
        await Assert.That(result.Version.VersionId).IsEqualTo("10");
        await Assert.That(result.Version.Profile).IsEqualTo(WotVersionProfile.Incremental);
        await Assert.That(result.Version.CompatibilityLine).IsEqualTo(Line);
        await Assert.That(result.Version.ContentDigest).IsEqualTo(Digest(Bytes));
        await Assert.That(result.Version.Origin).IsEqualTo(source.Context.Source);
        await Assert.That(result.ResourceXid).IsEqualTo(source.Resource);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
        await Assert.That(source.Reads.Last().Target).IsEqualTo(source.Resource + "/versions/10");
        using var content = result.Document.OpenRead();
        using var output = new MemoryStream();
        await content.CopyToAsync(output);
        await Assert.That(output.ToArray().SequenceEqual(Bytes)).IsTrue();
    }

    [Test]
    [Arguments("9", "10")]
    [Arguments("999", null)]
    public async Task StickyConflictsDoNotAcquireAnotherRevision(string defaultId, string? floor)
    {
        var source = new Source { DefaultId = defaultId, Sticky = true };
        await Error(source, source.Request with { MinimumVersion = floor }, "unsatisfied");
        await Assert.That(source.Reads.Any(read => read.Operation == FederationOperation.Document)).IsFalse();
    }

    [Test]
    public async Task RawPinsBypassLineAndFloorChoiceAndDigestTiesChooseTheHighestNumber()
    {
        var source = new Source { Sticky = true, DefaultId = "missing" };
        var request = source.Request with { CompatibilityLine = "invalid line", MinimumVersion = "not numeric" };
        var explicitPin = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            request with { VersionIdPin = "9" });
        await Assert.That(explicitPin.Version.VersionId).IsEqualTo("9");
        await Assert.That(explicitPin.Version.CompatibilityLine).IsEqualTo(Line);
        source.Reads.Clear();
        var digestPin = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            request with { DigestPin = Digest(Bytes) });
        await Assert.That(digestPin.Version.VersionId).IsEqualTo("999");
        await Assert.That(digestPin.Version.CompatibilityLine).IsEqualTo(OtherLine);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(3);
    }

    [Test]
    public async Task RetainedLocalResolutionUsesCapturedNumberAndLineDespiteAuthoredFieldsAndForgedInputs()
    {
        var source = new Source();
        var budget = new FederationReadBudget();
        var acquired = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request, budget);
        var reads = source.Reads.Count;
        var chargedBytes = budget.BytesRead;
        var objects = budget.ObjectsRead;
        source.Stopped = true;
        var request = source.Request with
        {
            IsDocumentLocal = true, MinimumVersion = "10",
            ContainingRevision = new("999", "sha256:forged", "urn:forged", "999")
            {
                Profile = WotVersionProfile.Incremental, CompatibilityLine = OtherLine
            }
        };
        var local = await WotRegistryResolver.ResolveLocalAsync(acquired, request, budget);
        await Assert.That(ReferenceEquals(local, acquired)).IsTrue();
        await Assert.That(local.Version.VersionId).IsEqualTo("10");
        await Assert.That(local.Version.CompatibilityLine).IsEqualTo(Line);
        await Assert.That(budget.BytesRead).IsEqualTo(chargedBytes);
        await Assert.That(budget.ObjectsRead).IsEqualTo(objects);
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveLocalAsync(acquired,
            request with { MinimumVersion = "11" }, budget)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected a fixed-revision failure.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("unsatisfied");
        var fragment = await WotRegistryResolver.SelectFragmentAsync(local, "#/value", budget);
        await Assert.That(fragment.Value.GetInt32()).IsEqualTo(42);
        await Assert.That(fragment.ContainingDocument.Version.ContentDigest).IsEqualTo(Digest(Bytes));
        await Assert.That(source.Reads.Count).IsEqualTo(reads);
    }

    [Test]
    public async Task LinelessRevisionsRemainExactlyPinnableButCannotMatchAutomaticLineSelection()
    {
        var source = new Source { FirstLine = null };
        var selected = await WotRegistryResolver.ResolveAsync(source, source.Collection, source.Request);
        await Assert.That(selected.Version.VersionId).IsEqualTo("10");
        var pin = await WotRegistryResolver.ResolveAsync(source, source.Collection,
            source.Request with { VersionIdPin = "9", CompatibilityLine = null });
        await Assert.That(pin.Version.VersionId).IsEqualTo("9");
        await Assert.That(pin.Version.CompatibilityLine).IsNull();
        await Error(source, source.Request with { CompatibilityLine = null }, "compatibility-required");
        await Error(source, source.Request with { CompatibilityLine = "urn:example:line:missing" }, "unsatisfied");
    }

    [Test]
    public async Task EqualNumbersAcrossAuthorizedOriginsDoNotIdentifyTheSameRevision()
    {
        var first = new Source();
        var second = new Source { Context = new("test", "urn:example:origin-b") };
        var a = await WotRegistryResolver.ResolveAsync(first, first.Collection, first.Request);
        var b = await WotRegistryResolver.ResolveAsync(second, second.Collection, second.Request);
        await Assert.That(a.Version.VersionId).IsEqualTo(b.Version.VersionId);
        await Assert.That(a.Version.ContentDigest).IsEqualTo(b.Version.ContentDigest);
        await Assert.That(a.Version.Origin).IsEqualTo("urn:example:origin-a");
        await Assert.That(b.Version.Origin).IsEqualTo("urn:example:origin-b");
        await Assert.That(a.Version == b.Version).IsFalse();
    }

    [Test]
    public async Task DigestCandidateDenialCannotFallBackToAnotherMatchingRevision()
    {
        var source = new Source { DeniedId = "9" };
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source,
            source.Collection, source.Request with { DigestPin = Digest(Bytes) })).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected the denied candidate to prevent selection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Document)).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownCapturedProfilesFailBeforeAnySourceRead()
    {
        var source = new Source();
        var model = JsonNode.Parse(source.Model!.Source.RootElement.GetRawText())!.AsObject();
        model["attributes"]!["wotversionprofile"]!["default"] = "future";
        model["attributes"]!["wotversionprofile"]!["enum"] = new JsonArray(JsonValue.Create("future"));
        source.Model = RegistryModel.Compile(RegistryJson.Parse(model.ToJsonString()));
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source,
            source.Collection, source.Request)).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected profile discovery failure.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
        await Assert.That(source.Reads.Count).IsEqualTo(0);
    }

    private static async Task Error(Source source, WotVersionResolutionRequest request, string code)
    {
        var failure = await Assert.That(async () => await WotRegistryResolver.ResolveAsync(source,
            source.Collection, request)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("Expected " + code);
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
    }

    private static string Digest(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Source : IFederationReadSource
    {
        internal Source(bool description = false)
        {
            Collection = description ? "/thingdescriptiongroups/g/thingdescriptions" : "/thingmodelgroups/g/thingmodels";
            Request = new(Identity, description ? WotReferenceTargetKind.ThingDescription : WotReferenceTargetKind.ThingModel,
                description ? WotReferenceOperator.ThingDescription : WotReferenceOperator.ThingModel)
            {
                CompatibilityLine = Line
            };
        }

        internal string Collection { get; }
        internal string Resource => Collection + "/native";
        internal WotVersionResolutionRequest Request { get; }
        internal List<FederationReadRequest> Reads { get; } = [];
        internal string DefaultId { get; init; } = "9";
        internal bool Sticky { get; init; }
        internal string? FirstLine { get; init; } = Line;
        internal string? DeniedId { get; init; }
        internal bool Stopped { get; set; }
        public NativeRegistryContext Context { get; init; } = new("test", "urn:example:origin-a");
        public JsonElement Capabilities { get; } = RegistryJson.Parse("{}").RootElement;
        public RegistryModel? Model { get; set; } = WotRegistryProfiles.Compile(WotVersionProfile.Incremental);

        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopped) { throw new InvalidOperationException("The selected source is no longer available."); }
            Reads.Add(request);
            var path = RegistryPath.Parse(request.Target);
            if (request.Operation == FederationOperation.Document)
            {
                if (path.VersionId!.Value == DeniedId)
                {
                    throw new FederationException(FederationErrorCode.PolicyDenied, "This captured Version is denied.");
                }
                return ValueTask.FromResult(FederationReadResult.FromDocument(request.Target, Context,
                    new FederationDocument(Bytes, "application/json")));
            }
            JsonObject entity = new() { ["xid"] = request.Target };
            JsonObject envelope;
            if (path.Kind == RegistryPathKind.Meta)
            {
                entity["defaultversionid"] = DefaultId;
                entity["defaultversionsticky"] = Sticky;
                envelope = new() { ["entity"] = entity };
            }
            else if (path.Kind == RegistryPathKind.Version)
            {
                entity["versionid"] = path.VersionId!.Value;
                entity["wotid"] = Identity;
                envelope = new() { ["entity"] = entity };
            }
            else
            {
                var entities = new JsonObject();
                if (request.Target == Collection)
                {
                    entities["native"] = new JsonObject { ["xid"] = Resource };
                }
                else if (request.Target == Resource + "/versions")
                {
                    foreach (var id in new[] { "9", "10", "999" })
                    {
                        var version = new JsonObject
                        {
                            ["xid"] = request.Target + "/" + id, ["versionid"] = id, ["wotid"] = Identity,
                            ["contentdigest"] = "sha256:untrusted-advisory"
                        };
                        var line = id == "999" ? OtherLine : id == "9" ? FirstLine : Line;
                        if (line is not null) { version["compatibilityline"] = line; }
                        entities[id] = version;
                    }
                }
                else { throw new InvalidOperationException("Unexpected source read."); }
                envelope = new() { ["kind"] = "collection", ["xid"] = request.Target, ["complete"] = true, ["entities"] = entities };
            }
            return ValueTask.FromResult(FederationReadResult.FromMetadata(request.Target, Context,
                RegistryJson.Parse(envelope.ToJsonString()).RootElement));
        }
    }
}
