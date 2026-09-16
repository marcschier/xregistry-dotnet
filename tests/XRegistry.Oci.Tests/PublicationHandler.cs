// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text.Json.Nodes;

namespace XRegistry.Oci.Tests;

internal sealed class PublicationHandler : HttpMessageHandler
{
    internal List<string> Requests { get; } = [];
    internal Dictionary<(bool Manifest, string Digest), byte[]> Objects { get; } = [];
    internal string? CommittedDigest { get; private set; }
    internal Uri? UploadLocation { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Post)
        {
            var accepted = new HttpResponseMessage(HttpStatusCode.Accepted) { RequestMessage = request };
            accepted.Headers.Location = UploadLocation ?? new Uri("https://registry.example.org/v2/team/catalog/blobs/uploads/session?_state=opaque");
            return accepted;
        }
        if (request.Method != HttpMethod.Put) { throw new InvalidOperationException("Unexpected publication method."); }
        var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        var digest = GraphFixture.Hash(bytes);
        if (uri.AbsolutePath.Contains("/blobs/uploads/", StringComparison.Ordinal))
        {
            if (!uri.Query.Contains("digest=" + digest, StringComparison.Ordinal)) { throw new InvalidOperationException("Missing exact upload digest."); }
            Objects[(false, digest)] = bytes;
        }
        else
        {
            var reference = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
            if (reference.StartsWith("sha256:", StringComparison.Ordinal) && reference != digest)
            {
                throw new InvalidOperationException("Manifest digest does not match exact request bytes.");
            }
            var node = JsonNode.Parse(bytes)!;
            if (node["manifests"] is JsonArray entries)
            {
                foreach (var entry in entries) { Require(entry!, true); }
            }
            else
            {
                Require(node["config"]!, false);
                foreach (var layer in node["layers"]!.AsArray()) { Require(layer!, false); }
            }
            Objects[(true, digest)] = bytes;
            if (reference == "release") { CommittedDigest = digest; }
        }
        var response = new HttpResponseMessage(HttpStatusCode.Created) { RequestMessage = request };
        response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
        return response;
    }

    private void Require(JsonNode edge, bool manifest)
    {
        var digest = edge["digest"]!.GetValue<string>();
        if (!Objects.TryGetValue((manifest, digest), out var bytes) || bytes.LongLength != edge["size"]!.GetValue<long>())
        {
            throw new InvalidOperationException("Published parent precedes a required native-route object.");
        }
    }
}
