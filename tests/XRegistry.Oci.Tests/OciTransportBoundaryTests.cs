// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Oci;
using XRegistry.Client;
using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

public class OciTransportBoundaryTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResponseBodyDeadlinesAndCallerCancellationDisposeTheOwnedStream(bool callerCancels)
    {
        using var cancellation = new CancellationTokenSource();
        using var content = new BlockingStream(callerCancels ? cancellation.Cancel : null);
        using var handler = new DistributionHandler(MemoryLayout.Load())
        {
            Override = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) };
                response.Content.Headers.ContentType = new(GraphFixture.Index);
                return response;
            },
        };
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        using var transport = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin),
            new() { RequestTimeout = TimeSpan.FromMilliseconds(200) });
        if (callerCancels)
        {
            await Assert.That(async () => { await OciSnapshot.OpenAsync(transport, "release", cancellationToken: cancellation.Token); })
                .Throws<OperationCanceledException>();
        }
        else
        {
            await Check.Error(() => OciSnapshot.OpenAsync(transport, "release").AsTask(), FederationErrorCode.Unavailable);
        }
        await Assert.That(content.Reads).IsEqualTo(1);
        await Assert.That(content.Disposed).IsTrue();
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("annotations")]
    [Arguments("digest")]
    [Arguments("size")]
    [Arguments("mediaType")]
    public async Task LayoutEntrypointDescriptorSyntaxIsCheckedEvenForDirectDigestSelection(string invalid)
    {
        var tree = MemoryLayout.Load();
        var index = JsonNode.Parse(tree.Files["index.json"])!;
        if (invalid == "annotations") { index["annotations"] = new JsonObject { ["example"] = 7 }; }
        else if (invalid == "size") { index["manifests"]![0]!["size"] = -1; }
        else { index["manifests"]![0]!.AsObject().Remove(invalid); }
        tree.Files["index.json"] = GraphFixture.Encode(index);
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, OciBootstrapTests.Offline).AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("oci://user:pass@registry.example.org/team/repo", FederationErrorCode.PolicyDenied)]
    [Arguments("oci://registry.example.org/team/repo:tag", FederationErrorCode.InvalidPackage)]
    [Arguments("oci://registry.example.org/team/repo@sha256:abc", FederationErrorCode.InvalidPackage)]
    [Arguments("oci://registry.example.org/team/Repo", FederationErrorCode.InvalidPackage)]
    [Arguments("oci://registry.example.org/team//repo", FederationErrorCode.InvalidPackage)]
    [Arguments("oci://registry.example.org/team/%72epo", FederationErrorCode.InvalidPackage)]
    [Arguments("oci://registry.example.org/team/repo?query", FederationErrorCode.InvalidPackage)]
    [Arguments("oci://registry.example.org/team/repo#fragment", FederationErrorCode.InvalidPackage)]
    [Arguments("https://registry.example.org/team/repo", FederationErrorCode.InvalidPackage)]
    [Arguments("oci://registry.example.org", FederationErrorCode.InvalidPackage)]
    public async Task UnsafeOrNonDistributionRepositoryLocatorsAreRejectedBeforeRequests(string endpoint, FederationErrorCode code) =>
        await Check.Error(() => Task.FromResult(OciRepository.Parse(endpoint)), code);

    [Test]
    [Arguments(128, false)]
    [Arguments(129, true)]
    public async Task DistributionTagLengthHasAnExactInclusiveLimit(int length, bool invalid)
    {
        var repository = OciRepository.Parse("oci://registry.example.org/team/repo");
        var tag = new string('a', length);
        if (invalid)
        {
            await Check.Error(() => Task.FromResult(repository.ManifestUri(tag)), FederationErrorCode.InvalidPackage);
        }
        else { await Assert.That(repository.ManifestUri(tag).AbsolutePath.EndsWith(tag, StringComparison.Ordinal)).IsTrue(); }
    }

    [Test]
    public async Task WrongRouteAndMovedTagCannotRepairAMissingPinnedManifest()
    {
        using var handler = new DistributionHandler(MemoryLayout.Load()) { MoveTagAfterRead = true };
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        using var transport = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin));
        await using var snapshot = await OciSnapshot.OpenAsync(transport, "release");
        handler.Override = request => request.RequestUri!.AbsolutePath.Contains("/manifests/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound) : null;
        await Check.Error(() => snapshot.ReadAsync(new(FederationOperation.Document, OciDocumentTests.Sample)).AsTask(),
            FederationErrorCode.InvalidPackage);
        await Assert.That(handler.Requests.Count(p => p.EndsWith("/release", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(handler.Requests[^1].Contains("/manifests/sha256:", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments(204)]
    [Arguments(206)]
    public async Task NonOkReadStatusIsNotASuccessfulRootEvenWithValidBytes(int status)
    {
        var tree = MemoryLayout.Load();
        using var handler = new DistributionHandler(tree)
        {
            Override = _ =>
            {
                var response = new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new ByteArrayContent(tree.Files["blobs/sha256/" + OciBootstrapTests.Offline[7..]]),
                };
                response.Content.Headers.ContentType = new(GraphFixture.Index);
                return response;
            },
        };
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        using var transport = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin));
        await Check.Error(() => OciSnapshot.OpenAsync(transport, "release").AsTask(), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task AmbientCredentialsAddedAfterConstructionAreNotSent()
    {
        using var handler = new DistributionHandler(MemoryLayout.Load());
        using var http = new HttpClient(handler);
        var repository = OciRepository.Parse("oci://registry.example.org/team/catalog");
        using var transport = new OciDistributionClient(repository, http, new RegistryHttpConnectionPolicy(repository.Origin));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "test-only");
        await Check.Error(() => OciSnapshot.OpenAsync(transport, "release").AsTask(), FederationErrorCode.PolicyDenied);
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LayoutEntrypointIsBoundedAndNotItselfARegistryRoot()
    {
        var tree = MemoryLayout.Load();
        var bytes = tree.Files["index.json"];
        GraphFixture.RepointRoot(tree, bytes);
        await Check.Error(() => OciSnapshot.OpenLayoutAsync(tree, "offline").AsTask(), FederationErrorCode.InvalidPackage);
        tree = MemoryLayout.Load();
        var index = JsonNode.Parse(tree.Files["index.json"])!;
        index["schemaVersion"] = 2.0m;
        tree.Files["index.json"] = GraphFixture.Encode(index);
        await using var snapshot = await OciSnapshot.OpenLayoutAsync(tree, "offline");
        await Assert.That(snapshot.RootDigest).IsEqualTo(OciBootstrapTests.Offline);
    }

    private sealed class BlockingStream(Action? onRead) : Stream
    {
        internal bool Disposed { get; private set; }
        internal int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            onRead?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed |= disposing;
            base.Dispose(disposing);
        }
    }
}
