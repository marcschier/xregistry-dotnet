// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace XRegistry.Oci.Tests;

internal sealed class DistributionHandler(MemoryLayout layout) : HttpMessageHandler
{
    internal string TagDigest { get; set; } = OciBootstrapTests.Offline;
    internal List<string> Requests { get; } = [];
    internal List<string?> Authorizations { get; } = [];
    internal List<string> Accepts { get; } = [];
    internal Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
    internal bool MoveTagAfterRead { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
        Authorizations.Add(request.Headers.Authorization?.ToString());
        Accepts.Add(string.Join(",", request.Headers.Accept.Select(a => a.MediaType)));
        var response = Override?.Invoke(request);
        if (response is not null) { response.RequestMessage = request; return Task.FromResult(response); }
        var path = request.RequestUri!.AbsolutePath;
        var reference = path[(path.LastIndexOf('/') + 1)..];
        var digest = reference == "release" ? TagDigest : reference;
        if (reference == "release" && MoveTagAfterRead) { TagDigest = OciBootstrapTests.Linked; }
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !layout.Files.TryGetValue("blobs/sha256/" + digest[7..], out var bytes))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
        var mediaType = "application/octet-stream";
        if (bytes.Length != 0 && bytes[0] == '{')
        {
            using var parsed = JsonDocument.Parse(bytes);
            if (parsed.RootElement.TryGetProperty("mediaType", out var media)) { mediaType = media.GetString()!; }
        }
        var manifest = mediaType is GraphFixture.Index or GraphFixture.Manifest;
        if (manifest != path.Contains("/manifests/", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { RequestMessage = request });
        }
        response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
        return Task.FromResult(response);
    }
}
