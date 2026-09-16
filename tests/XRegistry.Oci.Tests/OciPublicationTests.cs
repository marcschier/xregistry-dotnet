// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Client;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciPublicationTests
{
    [Test]
    public async Task DistributionPublishesOrdinaryBlobsAndNestedManifestsBeforeTheSelectedTag()
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load());
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents));
        using var handler = new PublicationHandler();
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        using var publisher = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin));
        await package.PublishAsync(publisher, "release");
        await Assert.That(handler.CommittedDigest).IsEqualTo(package.RootDigest);
        await Assert.That(handler.Requests[^1]).IsEqualTo("PUT /v2/team/catalog/manifests/release");
        await Assert.That(handler.Requests[^2]).IsEqualTo("PUT /v2/team/catalog/manifests/" + package.RootDigest);
        await Assert.That(handler.Objects[(false, "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")].Length).IsEqualTo(0);
        await Assert.That(handler.Requests.Any(r => r.Contains("?_state=opaque&digest=", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("https://unauthorized.example.org/v2/team/catalog/blobs/uploads/session")]
    [Arguments("https://registry.example.org/v2/different/repository/blobs/uploads/session")]
    [Arguments("http://registry.example.org/v2/team/catalog/blobs/uploads/session")]
    [Arguments("https://user:password@registry.example.org/v2/team/catalog/blobs/uploads/session")]
    public async Task UploadSessionDestinationsRequireIndependentRepositoryScopedAuthorization(string location)
    {
        var capture = GraphFixture.Capture(MemoryLayout.Load());
        var package = await OciSnapshotWriter.CreateAsync(new(capture.Records, capture.Documents));
        using var handler = new PublicationHandler { UploadLocation = new Uri(location) };
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        var credentialCalls = 0;
        using var publisher = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin), new()
        {
            AuthorizationProvider = (_, _) =>
            {
                credentialCalls++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(new("Bearer", "repository-test-only"));
            },
        });
        await Check.Error(() => package.PublishAsync(publisher, "release").AsTask(), FederationErrorCode.PolicyDenied);
        await Assert.That(handler.CommittedDigest).IsNull();
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(credentialCalls).IsEqualTo(1);
    }
}
