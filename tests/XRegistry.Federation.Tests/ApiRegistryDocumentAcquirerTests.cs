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

public class ApiRegistryDocumentAcquirerTests
{
    [Test]
    [Arguments("1", false)]
    [Arguments("preview:@", true)]
    public async Task ApiDefaultAcquisitionUsesTheDeclaredCoreVersionWithoutSemverSelection(string id, bool sticky)
    {
        var source = new Source();
        var version = "/apigroups/g/apis/a/versions/" + Uri.EscapeDataString(id);
        source.SelectedPath = version;
        source.Metadata["xid"] = version;
        source.Metadata["versionid"] = id;
        source.Meta["defaultversionid"] = id;
        source.Meta["defaultversionsticky"] = sticky;
        var budget = new FederationReadBudget();
        var acquired = await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/a", budget);
        await Assert.That(acquired.VersionXid).IsEqualTo(version);
        await Assert.That(acquired.Format).IsEqualTo("openapi/3.1");
        await Assert.That(budget.Requests).IsEqualTo(4L);
        await Assert.That(budget.ObjectsRead).IsEqualTo(4L);
        await Assert.That(source.Reads.Select(r => r.Target).SequenceEqual(
            ["/apigroups/g/apis/a/meta", version, version, "/apigroups/g/apis/a/meta"])).IsTrue();
    }

    [Test]
    public async Task ApiDefaultAcquisitionRejectsChangedSelectionWithoutRetry()
    {
        var source = new Source { ChangeDefault = true };
        var error = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/a"))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected default race rejection.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(source.Reads.Count).IsEqualTo(4);
        await Assert.That(source.Reads.Count(r => r.Operation == FederationOperation.Document)).IsEqualTo(1);
        await Assert.That(source.Reads.Any(r => r.Target.EndsWith("/versions/2", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("xid", "/apigroups/g/apis/other/meta", FederationErrorCode.InvalidPackage)]
    [Arguments("apiid", "other", FederationErrorCode.InvalidPackage)]
    [Arguments("defaultversionid", "", FederationErrorCode.InvalidPackage)]
    [Arguments("xref", "/apigroups/g/apis/other", FederationErrorCode.UnsupportedOperation)]
    public async Task ApiDefaultMetadataFailuresPreventVersionReads(string field, string value, FederationErrorCode code)
    {
        var source = new Source();
        source.Meta[field] = value;
        var error = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/a"))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected Meta rejection.");
        await Assert.That(error.Code).IsEqualTo(code);
        await Assert.That(source.Reads.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ApiDefaultAcquisitionSharesCumulativeBudgetsAndDoesNotReturnBeforeRecheck()
    {
        var baseline = new FederationReadBudget();
        await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(new Source(), "/apigroups/g/apis/a", baseline);
        var exact = new FederationReadBudget(new(maxRequests: 4, maxObjects: 4,
            maxTotalBytes: baseline.BytesRead, maxWork: baseline.Work));
        var acquired = await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(new Source(), "/apigroups/g/apis/a", exact);
        await Assert.That(acquired.VersionXid).IsEqualTo(Source.Path);
        await Assert.That(exact.BytesRead).IsEqualTo(baseline.BytesRead);
        var source = new Source();
        var budget = new FederationReadBudget(new(maxRequests: 3));
        var error = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/a", budget))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected recheck budget rejection.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(source.Reads.Count).IsEqualTo(3);
        var second = new Source();
        await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(second, "/apigroups/g/apis/a", budget))
            .Throws<FederationException>();
        await Assert.That(second.Reads).IsEmpty();
    }

    [Test]
    [Arguments("read", FederationErrorCode.NotFound)]
    [Arguments("denied", FederationErrorCode.PolicyDenied)]
    [Arguments("external", FederationErrorCode.Unavailable)]
    public async Task ApiDefaultAcquisitionDoesNotReplaceTheDeclaredUnavailableVersion(
        string failure, FederationErrorCode code)
    {
        var source = new Source { Failure = failure };
        var error = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/a"))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected selected Version failure.");
        await Assert.That(error.Code).IsEqualTo(code);
        await Assert.That(source.Reads.Count).IsEqualTo(failure == "read" ? 2 : 3);
        await Assert.That(source.Reads.Where(r => !r.Target.EndsWith("/meta", StringComparison.Ordinal))
            .All(r => r.Target == Source.Path)).IsTrue();
    }

    [Test]
    public async Task ApiDefaultAcquisitionRejectsVersionPathsAndPropagatesCancellation()
    {
        var source = new Source();
        await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, Source.Path)).Throws<ArgumentException>();
        await Assert.That(source.Reads).IsEmpty();
        using var cancellation = new CancellationTokenSource();
        var cancelled = new Source { Cancellation = cancellation };
        await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(cancelled, "/apigroups/g/apis/a",
                cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(cancelled.Reads.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ApiAcquisitionDoesNotInventFormatValidationOrDocumentBasesForPresentEmptyBytes()
    {
        var source = new Source { InputBytes = [], DocumentBase = null, DocumentOrigin = null };
        source.Metadata["format"] = "Thrift";
        source.Metadata.Remove("contenttype");
        var acquired = await ApiRegistryDocumentAcquirer.AcquireAsync(source, Source.Path);
        await Assert.That(acquired.Format).IsEqualTo("Thrift");
        await Assert.That(acquired.Document.Length).IsEqualTo(0L);
        await Assert.That(acquired.Document.Base).IsNull();
        await Assert.That(acquired.Document.Origin).IsNull();
        await Assert.That(acquired.ContentDigest).IsEqualTo(
            "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        await Assert.That(source.Reads.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ExactApiAcquisitionRetainsBytesFormatDigestAndDeclaredOriginAfterSourceDisposal()
    {
        var source = new Source();
        var result = await ApiRegistryDocumentAcquirer.AcquireAsync(source, Source.Path);
        await Assert.That(result.VersionXid).IsEqualTo(Source.Path);
        await Assert.That(result.Format).IsEqualTo("openapi/3.1");
        await Assert.That(result.ContentDigest).IsEqualTo(
            "sha256:" + Convert.ToHexString(SHA256.HashData(Source.Bytes)).ToLowerInvariant());
        await Assert.That(result.Context).IsEqualTo(source.Context);
        await Assert.That(result.Document.ContentType).IsEqualTo("application/json");
        await Assert.That(result.Document.Base).IsEqualTo("https://authored.example/apis/");
        await Assert.That(result.Document.Origin).IsEqualTo("https://authored.example/apis/entry.json");
        await Assert.That(source.Reads.Select(r => r.Operation)
            .SequenceEqual([FederationOperation.Entity, FederationOperation.Document])).IsTrue();
        await Assert.That(source.Reads.All(r => r.Target == Source.Path)).IsTrue();
        source.Disposed = true;
        Array.Fill(source.InputBytes, (byte)'!');
        using var stream = result.Document.OpenRead();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        await Assert.That(memory.ToArray().SequenceEqual(Source.Bytes)).IsTrue();
    }

    [Test]
    [Arguments("read", FederationErrorCode.NotFound)]
    [Arguments("denied", FederationErrorCode.PolicyDenied)]
    [Arguments("unavailable", FederationErrorCode.Unavailable)]
    [Arguments("external", FederationErrorCode.Unavailable)]
    public async Task MissingDeniedAndExternalApiDocumentsNeverSelectADefaultOrAnotherSource(
        string failure, FederationErrorCode expected)
    {
        var source = new Source { Failure = failure };
        var error = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireAsync(source, Source.Path))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected acquisition failure.");
        await Assert.That(error.Code).IsEqualTo(expected);
        await Assert.That(source.Reads.All(r => r.Target == Source.Path)).IsTrue();
        await Assert.That(source.Reads.Count).IsEqualTo(failure == "read" ? 1 : 2);
    }

    [Test]
    [Arguments("xid", "/apigroups/g/apis/a/versions/2")]
    [Arguments("versionid", "2")]
    [Arguments("apiid", "other")]
    [Arguments("format", "")]
    [Arguments("format", "OpenAPI/3.1/preview")]
    public async Task ApiMetadataIdentityAndFormatFailuresPreventDocumentReads(string field, string value)
    {
        var source = new Source();
        source.Metadata[field] = value;
        var error = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireAsync(source, Source.Path))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected metadata rejection.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(source.Reads.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("context")]
    [Arguments("model")]
    [Arguments("owner")]
    [Arguments("resultcontext")]
    [Arguments("resultpath")]
    [Arguments("media")]
    public async Task ApiAcquisitionRejectsChangedCaptureOrContradictoryDocumentEvidence(string change)
    {
        var source = new Source { Change = change };
        var error = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireAsync(source, Source.Path))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected capture rejection.");
        await Assert.That(error.Code).IsEqualTo(change == "resultpath" ? FederationErrorCode.InvalidPackage
            : change == "media" ? FederationErrorCode.IntegrityError : FederationErrorCode.InconsistentSnapshot);
        await Assert.That(source.Reads.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ApiAcquisitionRejectsResourceDefaultSelectorsAndNonApiModelsBeforeReading()
    {
        var source = new Source();
        await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireAsync(source, "/apigroups/g/apis/a"))
            .Throws<ArgumentException>();
        source.Model = BuiltInRegistryModels.Compile(RegistryModelKind.Schema);
        var error = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireAsync(source, Source.Path))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected model rejection.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(source.Reads).IsEmpty();
    }

    [Test]
    public async Task ApiAcquisitionHasInclusiveCumulativeBudgetsAndCannotResetThemBetweenCalls()
    {
        var source = new Source();
        var baseline = new FederationReadBudget();
        await ApiRegistryDocumentAcquirer.AcquireAsync(source, Source.Path, baseline);
        await Assert.That(baseline.Requests).IsEqualTo(2L);
        await Assert.That(baseline.ObjectsRead).IsEqualTo(2L);
        await Assert.That(baseline.BytesRead).IsEqualTo(source.MetadataLength + Source.Bytes.Length);
        var exact = new FederationReadBudget(new(maxRequests: 2, maxObjects: 2,
            maxTotalBytes: baseline.BytesRead, maxWork: baseline.Work,
            maxObjectBytes: (int)Math.Max(source.MetadataLength, Source.Bytes.Length),
            maxResultBytes: Source.Bytes.Length));
        var result = await ApiRegistryDocumentAcquirer.AcquireAsync(new Source(), Source.Path, exact);
        await Assert.That(result.Document.Length).IsEqualTo((long)Source.Bytes.Length);
        var repeated = new Source();
        var exhausted = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireAsync(repeated, Source.Path, exact))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected cumulative exhaustion.");
        await Assert.That(exhausted.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(repeated.Reads).IsEmpty();

        foreach (var limits in new[]
        {
            new FederationReadLimits(maxRequests: 1),
            new FederationReadLimits(maxObjects: 1),
            new FederationReadLimits(maxTotalBytes: baseline.BytesRead - 1),
            new FederationReadLimits(maxWork: baseline.Work - 1),
            new FederationReadLimits(maxObjectBytes: (int)source.MetadataLength - 1),
            new FederationReadLimits(maxResultBytes: Source.Bytes.Length - 1)
        })
        {
            var error = await Assert.That(async () =>
                await ApiRegistryDocumentAcquirer.AcquireAsync(new Source(), Source.Path, new(limits)))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected boundary rejection.");
            await Assert.That(error.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ApiAcquisitionPropagatesCancellationBeforeAndDuringSourceReads(bool during)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source { Cancellation = during ? cancellation : null };
        if (!during) { cancellation.Cancel(); }
        await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireAsync(source, Source.Path, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(source.Reads.Count).IsEqualTo(during ? 1 : 0);
    }

    private sealed class Source : IFederationReadSource
    {
        internal const string Path = "/apigroups/g/apis/a/versions/1";
        internal static byte[] Bytes => """{"openapi":"3.1.1","paths":{}}"""u8.ToArray();
        internal byte[] InputBytes { get; init; } = Bytes;
        internal string? DocumentBase { get; init; } = "https://authored.example/apis/";
        internal string? DocumentOrigin { get; init; } = "https://authored.example/apis/entry.json";
        internal JsonObject Metadata { get; } = new()
        {
            ["xid"] = Path, ["apiid"] = "a", ["versionid"] = "1",
            ["format"] = "openapi/3.1", ["contenttype"] = "application/json"
        };
        internal long MetadataLength => Encoding.UTF8.GetByteCount(Metadata.ToJsonString());
        internal List<FederationReadRequest> Reads { get; } = [];
        internal string SelectedPath { get; set; } = Path;
        internal bool ChangeDefault { get; init; }
        internal JsonObject Meta { get; } = new()
        {
            ["xid"] = "/apigroups/g/apis/a/meta", ["apiid"] = "a", ["defaultversionid"] = "1"
        };
        internal string? Failure { get; init; }
        internal string? Change { get; init; }
        internal bool Disposed { get; set; }
        internal CancellationTokenSource? Cancellation { get; init; }
        public NativeRegistryContext Context { get; private set; } = new("test", "urn:test:api");
        public RegistryModel? Model { get; set; } = BuiltInRegistryModels.Compile(RegistryModelKind.Api);
        public JsonElement Capabilities { get; private set; } = RegistryJson.Parse("{}").RootElement;

        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Reads.Add(request);
            Cancellation?.Cancel();
            if (request.Target == "/apigroups/g/apis/a/meta")
            {
                if (ChangeDefault && Reads.Count > 1) { Meta["defaultversionid"] = "2"; }
                return ValueTask.FromResult(FederationReadResult.FromMetadata(request.Target, Context,
                    RegistryJson.Parse(Meta.ToJsonString()).RootElement));
            }
            if (request.Operation == FederationOperation.Entity)
            {
                if (Failure == "read") { throw new FederationException(FederationErrorCode.NotFound, "Missing Version."); }
                return ValueTask.FromResult(FederationReadResult.FromMetadata(SelectedPath, Context,
                    RegistryJson.Parse(Metadata.ToJsonString()).RootElement));
            }
            if (Failure is "denied" or "unavailable")
            {
                throw new FederationException(Failure == "denied" ? FederationErrorCode.PolicyDenied
                    : FederationErrorCode.Unavailable, "Selected document cannot be read.");
            }
            if (Failure == "external")
            {
                return ValueTask.FromResult(FederationReadResult.FromExternalDocument(Path, Context,
                    RegistryJson.Parse("""{"href":"https://must-not-fetch.invalid/api.json"}""").RootElement));
            }
            if (Change == "context") { Context = new("test", "urn:test:changed"); }
            if (Change == "model") { Model = BuiltInRegistryModels.Compile(RegistryModelKind.Api); }
            if (Change == "owner")
            {
                Capabilities = RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement;
            }
            return ValueTask.FromResult(FederationReadResult.FromDocument(
                Change == "resultpath" ? "/apigroups/g/apis/a/versions/2" : SelectedPath,
                Change == "resultcontext" ? new("test", "urn:test:other") : Context,
                new FederationDocument(InputBytes, Change == "media" ? "text/plain" : "application/json",
                    DocumentBase, DocumentOrigin)));
        }
    }
}
