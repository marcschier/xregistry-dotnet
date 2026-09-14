using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Client;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciDistributionTests
{
    [Test]
    public async Task DistributionUsesManifestAndBlobRoutesAndPinsTagOnce()
    {
        var tree = MemoryLayout.Load();
        using var handler = new DistributionHandler(tree) { MoveTagAfterRead = true };
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        var credentials = new List<string>();
        using var transport = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin),
            new()
            {
                AuthorizationProvider = (uri, _) =>
                {
                    credentials.Add(uri.AbsoluteUri);
                    return ValueTask.FromResult<AuthenticationHeaderValue?>(new("Bearer", "repository-test-only"));
                },
            });
        await using var snapshot = await OciSnapshot.OpenAsync(transport, "release");
        var result = await snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample + "/versions/v2"));
        await Assert.That(snapshot.RootDigest).IsEqualTo(OciBootstrapTests.Offline);
        await Assert.That(result.Context.Source).IsEqualTo("oci://registry.example.org/team/catalog");
        await Assert.That(Encoding.UTF8.GetString(await Check.Bytes(result.Document!))).IsEqualTo("{\"type\":\"number\"}\n");
        await Assert.That(handler.Requests[0]).IsEqualTo("GET /v2/team/catalog/manifests/release");
        await Assert.That(handler.Requests[^1]).IsEqualTo(
            "GET /v2/team/catalog/blobs/sha256:e562cc019db9f5cb554d77b7cdd6b0e0640c1fd7a045e36b1b164879a24ac87d");
        await Assert.That(handler.Requests.Count(p => p.EndsWith("/release", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(credentials.Count).IsEqualTo(handler.Requests.Count);
        for (var i = 0; i < handler.Requests.Count; i++)
        {
            if (handler.Requests[i].Contains("/manifests/", StringComparison.Ordinal))
            {
                await Assert.That(handler.Accepts[i]).IsEqualTo(GraphFixture.Index + "," + GraphFixture.Manifest);
            }
        }
        await Assert.That(handler.Authorizations.All(a => a == "Bearer repository-test-only")).IsTrue();
    }

    [Test]
    [Arguments(301, FederationErrorCode.PolicyDenied)]
    [Arguments(307, FederationErrorCode.PolicyDenied)]
    [Arguments(401, FederationErrorCode.UnsupportedOperation)]
    [Arguments(403, FederationErrorCode.PolicyDenied)]
    [Arguments(503, FederationErrorCode.Unavailable)]
    public async Task UnauthorizedRedirectAndTokenFlowNeverForwardCredentials(int status, FederationErrorCode code)
    {
        using var handler = new DistributionHandler(MemoryLayout.Load())
        {
            Override = _ =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)status);
                response.Headers.Location = new Uri("https://different.example.org/credentials");
                response.Headers.WwwAuthenticate.Add(new("Bearer", "realm=\"https://different.example.org/token\""));
                return response;
            },
        };
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        using var transport = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin));
        await Check.Error(() => OciSnapshot.OpenAsync(transport, "release").AsTask(), code);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Authorizations[0]).IsNull();
    }

    [Test]
    [Arguments("bad-sha256", FederationErrorCode.IntegrityError)]
    [Arguments("bad-size", FederationErrorCode.IntegrityError)]
    [Arguments("missing-type", FederationErrorCode.InvalidPackage)]
    [Arguments("wrong-type", FederationErrorCode.InvalidPackage)]
    [Arguments("encoded", FederationErrorCode.UnsupportedOperation)]
    public async Task DistributionRejectsContradictoryTransportEvidence(string mutation, FederationErrorCode code)
    {
        var tree = MemoryLayout.Load();
        using var handler = new DistributionHandler(tree)
        {
            Override = _ =>
            {
                var bytes = tree.Files["blobs/sha256/" + OciBootstrapTests.Offline[7..]];
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                if (mutation != "missing-type") { response.Content.Headers.ContentType = new(mutation == "wrong-type" ? GraphFixture.Manifest : GraphFixture.Index); }
                if (mutation == "bad-sha256") { response.Headers.TryAddWithoutValidation("Docker-Content-Digest", OciBootstrapTests.Linked); }
                if (mutation == "bad-size") { response.Content.Headers.ContentLength = 999; }
                if (mutation == "encoded") { response.Content.Headers.ContentEncoding.Add("gzip"); }
                return response;
            },
        };
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        using var transport = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin));
        await Check.Error(() => OciSnapshot.OpenAsync(transport, "release").AsTask(), code);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task MissingDigestHeaderAndIndependentSha512EvidenceDoNotReplaceTheSha256Pin(bool sha512, bool uppercaseMediaType)
    {
        var tree = MemoryLayout.Load();
        using var handler = new DistributionHandler(tree)
        {
            Override = request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal)) { return null; }
                var bytes = tree.Files["blobs/sha256/" + OciBootstrapTests.Offline[7..]];
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new(uppercaseMediaType ? GraphFixture.Index.ToUpperInvariant() : GraphFixture.Index);
                response.Content.Headers.ContentType.Parameters.Add(new("charset", "utf-8"));
                if (sha512) { response.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha512:" + Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant()); }
                return response;
            },
        };
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        using var transport = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin));
        await using var snapshot = await OciSnapshot.OpenAsync(transport, "release");
        await Assert.That(snapshot.RootDigest).IsEqualTo(OciBootstrapTests.Offline);
    }

    [Test]
    public async Task AdvertisementUsesExactRepositoryAndReferenceGrammarAndRejectsUnknownParameters()
    {
        var profile = OciRepository.ParseProfile("""
            {"name":"oci","endpoint":"oci://registry.example.org:5443/team/a__b/c--d","parameters":{"reference":"_release.1"}}
            """u8.ToArray());
        await Assert.That(profile.Repository.ManifestUri(profile.Reference).AbsoluteUri)
            .IsEqualTo("https://registry.example.org:5443/v2/team/a__b/c--d/manifests/_release.1");
        await Check.Error(() => Task.FromResult(OciRepository.ParseProfile("""
            {"name":"oci","endpoint":"oci://registry.example.org/team/repo","parameters":{"reference":"release","unknown":true}}
            """u8.ToArray())), FederationErrorCode.UnsupportedOperation);
    }
}
