// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class CatalogSourceSelectionTests
{
    private const string DescriptionXid = "/categories/public/registries/schemas/versions/catalog-v2";
    private static readonly NativeRegistryContext Catalog = new("git", "https://catalog.example/repo", "catalog-commit", true);

    [Test]
    public async Task OpeningASelectedDescriptionRetainsAllThreeIdentityScopesOutsideCoreMetadata()
    {
        var description = Description();
        var source = new Source();
        var dispatches = 0;
        var releases = 0;
        await using (var lease = await CatalogSourceSelection.OpenAsync(description, Catalog, DescriptionXid, new(["http"]),
            (advertisement, _, _) =>
            {
                dispatches++;
                return ValueTask.FromResult(new FederationSourceLease(source,
                    () => { releases++; return ValueTask.CompletedTask; }, "credential-generation"));
            }))
        {
            await Assert.That(dispatches).IsEqualTo(1);
            await Assert.That(lease.CredentialStamp).IsEqualTo("credential-generation");
            await Assert.That(lease.Source.Context.CatalogOrigin!.DescriptionVersionXid).IsEqualTo(DescriptionXid);
            await Assert.That(lease.Source.Context.CatalogOrigin.CatalogContext.Revision).IsEqualTo("catalog-commit");
            await Assert.That(lease.Source.Context.CatalogOrigin.Advertisement.OriginalIndex).IsEqualTo(0);
            await Assert.That(lease.Source.Context.Revision).IsNull();
            var result = await lease.Source.ReadAsync(new(FederationOperation.Document, "/documents/g/schemas/s"));
            await Assert.That(result.SelectedXid).IsEqualTo("/documents/g/schemas/s/versions/content-v3");
            await Assert.That(result.Context).IsEqualTo(lease.Source.Context);
            await Assert.That(result.Metadata.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            await Assert.That(await DirectoryMappingTests.ReadHex(result.Document!)).IsEqualTo("00FF");
            await Assert.That(releases).IsEqualTo(0);
        }
        await Assert.That(releases).IsEqualTo(1);
    }

    [Test]
    public async Task APolicyDenialCannotOpenOrFallBackToAnotherAdvertisement()
    {
        var opened = 0;
        var failure = await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), Catalog,
            DescriptionXid, new(["http"], accessPolicy: _ => AdvertisementAccess.Deny), (_, _, _) =>
            {
                opened++;
                throw new InvalidOperationException("Denied selection cannot open a source.");
            })).Throws<FederationException>() ?? throw new InvalidOperationException("Expected policy denial.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(opened).IsEqualTo(0);
    }

    [Test]
    public async Task ASelectedAcquisitionFailureNeverTriesTheNextAdvertisement()
    {
        var opened = 0;
        var failure = await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), Catalog,
            DescriptionXid, new(["http"]), (_, _, _) =>
            {
                opened++;
                throw new FederationException(FederationErrorCode.IntegrityError, "Selected acquisition failed.");
            })).Throws<FederationException>() ?? throw new InvalidOperationException("Expected the selected failure.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.IntegrityError);
        await Assert.That(opened).IsEqualTo(1);
    }

    [Test]
    [Arguments("catalog-v1")]
    [Arguments("content-v3")]
    public async Task ADescriptionVersionCannotBeReplacedByAnotherIdentity(string version)
    {
        var opened = 0;
        var failure = await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), Catalog,
            DescriptionXid.Replace("catalog-v2", version, StringComparison.Ordinal), new(["http"]), (_, _, _) =>
            {
                opened++;
                throw new InvalidOperationException("A mismatched Version must fail before opening.");
            })).Throws<FederationException>() ?? throw new InvalidOperationException("Expected the description mismatch.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(opened).IsEqualTo(0);
    }

    [Test]
    [Arguments("http", "https://another.example/registry")]
    [Arguments("http", "https://target.example/registry//")]
    [Arguments("git", "https://target.example/registry")]
    public async Task AFactoryCannotReturnAnotherBindingOrRegistry(string binding, string endpoint)
    {
        var source = new Source { Context = new(binding, endpoint) };
        var releases = 0;
        var failure = await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), Catalog,
            DescriptionXid, new(["http"]), (_, _, _) => ValueTask.FromResult(new FederationSourceLease(source,
                () => { releases++; return ValueTask.CompletedTask; })))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected a different acquired source to fail.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(releases).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationAfterAcquisitionReleasesTheNewLeaseInsteadOfReturningIt()
    {
        using var cancellation = new CancellationTokenSource();
        var releases = 0;
        await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), Catalog,
            DescriptionXid, new(["http"]), (_, _, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(new FederationSourceLease(new Source(),
                    () => { releases++; return ValueTask.CompletedTask; }));
            }, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(releases).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AReadCannotChangeTheAcquiredSourceOrResolutionOwner(bool owner, bool duringRead)
    {
        var source = new Source();
        await using var lease = await CatalogSourceSelection.OpenAsync(Description(), Catalog, DescriptionXid,
            new(["http"]), (_, _, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask)));
        void Change()
        {
            if (owner) { source.Capabilities = RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement; }
            else { source.Context = new("http", "https://another.example/registry/"); }
        }
        if (duringRead) { source.OnRead = Change; }
        else { Change(); }
        var error = await Assert.That(async () => await lease.Source.ReadAsync(
            new(FederationOperation.Entity, "/documents/g"))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected the captured source to remain fixed.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(source.Reads).IsEqualTo(duringRead ? 1 : 0);
    }

    [Test]
    [Arguments("metadata")]
    [Arguments("external")]
    [Arguments("empty")]
    public async Task AllResultFormsRetainTheirExactContentAndSeparateCatalogProvenance(string kind)
    {
        var source = new Source
        {
            Result = context => kind switch
            {
                "metadata" => FederationReadResult.FromMetadata("/documents/g", context,
                    RegistryJson.Parse("""{"kind":"group","entity":{"xid":"/documents/g","name":"kept"}}""").RootElement),
                "external" => FederationReadResult.FromExternalDocument("/documents/g/schemas/s/versions/v1", context,
                    RegistryJson.Parse("""{"uri":"https://documents.example/exact?name=%2F","base":"https://other.example/base/"}""").RootElement),
                _ => FederationReadResult.FromDocument("/documents/g/schemas/s/versions/v1", context, new([])),
            }
        };
        await using var lease = await CatalogSourceSelection.OpenAsync(Description(), Catalog, DescriptionXid,
            new(["http"]), (_, _, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask)));
        var result = await lease.Source.ReadAsync(new(FederationOperation.Entity, "/documents/g"));
        await Assert.That(result.Context.CatalogOrigin!.DescriptionVersionXid).IsEqualTo(DescriptionXid);
        if (kind == "metadata")
        {
            await Assert.That(result.Metadata.GetRawText()).IsEqualTo("""{"kind":"group","entity":{"xid":"/documents/g","name":"kept"}}""");
            await Assert.That(result.Document).IsNull();
        }
        else if (kind == "external")
        {
            await Assert.That(result.ExternalDocument.GetRawText())
                .IsEqualTo("""{"uri":"https://documents.example/exact?name=%2F","base":"https://other.example/base/"}""");
            await Assert.That(result.Document).IsNull();
        }
        else
        {
            await Assert.That(result.Document!.Length).IsEqualTo(0L);
            await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task PreCancellationDoesNotEvaluatePolicyOrOpenASource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), Catalog, DescriptionXid,
            new(["http"], accessPolicy: _ => { calls++; return AdvertisementAccess.Allow; }), (_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("Pre-cancellation must precede all callbacks.");
            }, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task SourceAndNestedCatalogHopLimitsAreInclusiveAndCheckedBeforeAcquisition()
    {
        var opened = 0;
        ValueTask<FederationSourceLease> Acquire(CatalogAdvertisement _, FederationReadBudget budget, CancellationToken token)
        {
            opened++;
            return ValueTask.FromResult(new FederationSourceLease(new Source(), static () => ValueTask.CompletedTask));
        }
        var budget = new FederationReadBudget(new(maxSources: 1, maxHops: 1));
        await using var first = await CatalogSourceSelection.OpenAsync(Description(), Catalog, DescriptionXid,
            new(["http"]), Acquire, budget);
        await Assert.That(opened).IsEqualTo(1);
        var sources = await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), Catalog,
            DescriptionXid, new(["http"]), Acquire, budget)).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected the inclusive source limit.");
        await Assert.That(sources.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        var hops = await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), first.Source.Context,
            DescriptionXid, new(["http"]), Acquire, new(new(maxHops: 1)))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected a nested catalog hop to exceed its limit.");
        await Assert.That(hops.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(opened).IsEqualTo(1);
    }

    [Test]
    public async Task MissingModelAndMalformedEnabledCapabilitiesReleaseTheAcquiredLease()
    {
        foreach (var noModel in new[] { false, true })
        {
            var source = new Source
            {
                Model = noModel ? null : RegistryModel.Compile(RegistryJson.Parse("{}")),
                Capabilities = RegistryJson.Parse(noModel ? "{}" : """{"federation":{}}""").RootElement
            };
            var releases = 0;
            var error = await Assert.That(async () => await CatalogSourceSelection.OpenAsync(Description(), Catalog,
                DescriptionXid, new(["http"]), (_, _, _) => ValueTask.FromResult(new FederationSourceLease(source,
                    () => { releases++; return ValueTask.CompletedTask; })))).Throws<FederationException>()
                ?? throw new InvalidOperationException("Expected established model/capability evidence.");
            await Assert.That(error.Code).IsEqualTo(noModel ? FederationErrorCode.UnsupportedOperation : FederationErrorCode.InvalidPackage);
            await Assert.That(releases).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ActualHttpAcquisitionRetainsCatalogOriginAndUsesTheSharedWireBudget()
    {
        var paths = new List<string>();
        await using var app = await HttpFederationReadSourceTests.StartAsync(app =>
        {
            HttpFederationReadSourceTests.Bootstrap(app, paths);
            app.MapGet("/mount/documents/g", () => HttpFederationReadSourceTests.Json(
                """{"xid":"/documents/g","documentid":"g","self":"#/entity"}"""));
        });
        var root = new Uri(new Uri(app.Urls.Single()), "/mount/");
        var description = CatalogDescription.Parse(Encoding.UTF8.GetBytes(
            $$$"""{"versionid":"catalog-v2","federationprofiles":[{"name":"http","endpoint":"{{{root}}}"}]}"""));
        var budget = new FederationReadBudget(new(maxSources: 2, maxRequests: 5));
        await using var lease = await CatalogSourceSelection.OpenAsync(description, Catalog, DescriptionXid,
            new(["http"]), async (advertisement, shared, token) =>
            {
                var opened = await HttpFederationReadSource.OpenAsync(new Uri(advertisement.Endpoint),
                    new() { AllowLoopbackHttp = true }, null, shared, token);
                return new(opened, () => { opened.Dispose(); return ValueTask.CompletedTask; });
            }, budget);
        await Assert.That(budget.Requests).IsEqualTo(4L);
        await Assert.That(string.Join(',', paths)).IsEqualTo("/mount/,/mount/model,/mount/capabilities,/mount/modelsource");
        var result = await lease.Source.ReadAsync(new(FederationOperation.Entity, "/documents/g",
            representation: FederationRepresentation.ApiView));
        await Assert.That(budget.Requests).IsEqualTo(5L);
        await Assert.That(result.Context.CatalogOrigin!.DescriptionVersionXid).IsEqualTo(DescriptionXid);
        await Assert.That(result.Metadata.GetProperty("entity").GetProperty("xid").GetString()).IsEqualTo("/documents/g");
        var error = await Assert.That(async () => await lease.Source.ReadAsync(new(FederationOperation.Entity,
            "/documents/g", representation: FederationRepresentation.ApiView))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected every HTTP read to use the shared limit.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(budget.Requests).IsEqualTo(5L);
    }

    [Test]
    public async Task AnEstablishedModelCannotBeReplacedWithoutChangingTheCapture()
    {
        var source = new Source();
        await using var lease = await CatalogSourceSelection.OpenAsync(Description(), Catalog, DescriptionXid,
            new(["http"]), (_, _, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask)));
        source.Model = RegistryModel.Compile(RegistryJson.Parse("""{"attributes":{"changed":{"type":"string"}}}"""));
        var error = await Assert.That(async () => await lease.Source.ReadAsync(new(FederationOperation.Entity, "/documents/g")))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected the established model to stay fixed.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(source.Reads).IsEqualTo(0);
    }

    private static CatalogDescription Description() => CatalogDescription.Parse(Encoding.UTF8.GetBytes("""
        {"versionid":"catalog-v2","registryid":"description-id","federationprofiles":[
          {"name":"http","endpoint":"https://target.example/registry"},
          {"name":"http","endpoint":"https://fallback.example/registry","priority":2}]}
        """));

    private sealed class Source : IFederationReadSource
    {
        public NativeRegistryContext Context { get; set; } = new("http", "https://target.example/registry/");
        public JsonElement Capabilities { get; set; } = RegistryJson.Parse("{}").RootElement;
        public RegistryModel? Model { get; set; } = RegistryModel.Compile(RegistryJson.Parse("{}"));
        internal int Reads { get; private set; }
        internal Action? OnRead { get; set; }
        internal Func<NativeRegistryContext, FederationReadResult>? Result { get; init; }
        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            var context = Context;
            OnRead?.Invoke();
            if (Result is not null) { return ValueTask.FromResult(Result(context)); }
            return ValueTask.FromResult(FederationReadResult.FromDocument("/documents/g/schemas/s/versions/content-v3",
                context, new([0, 255])));
        }
    }
}
