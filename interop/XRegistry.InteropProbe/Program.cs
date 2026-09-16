// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text;
using XRegistry.Client;

if (args.Length != 2 || args[0] != "--server" ||
    !Uri.TryCreate(args[1], UriKind.Absolute, out var server))
{
    Console.Error.WriteLine("Usage: XRegistry.InteropProbe --server <isolated-loopback-registry>");
    return 2;
}

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled || JitInfo.GetCompiledMethodCount() != 0)
{
    Console.Error.WriteLine("Interop qualification requires the published native client executable.");
    return 2;
}

using var client = new XRegistryHttpClient(server, new XRegistryHttpClientOptions
{
    AllowLoopbackHttp = true,
    RequestTimeout = TimeSpan.FromSeconds(20)
});
using (var root = await client.SendAsync(HttpMethod.Get).ConfigureAwait(false))
{
    Require(root.StatusCode == HttpStatusCode.OK, "root-status");
    using var metadata = await root.ReadMetadataAsync().ConfigureAwait(false);
    Require(metadata.RootElement.GetProperty("specversion").GetString() == "1.0-rc4", "root-specversion");
    Require(metadata.RootElement.GetProperty("xid").GetString() == "/", "root-xid");
}

using (var capabilities = await client.SendAsync(HttpMethod.Get, "capabilities").ConfigureAwait(false))
{
    Require(capabilities.StatusCode == HttpStatusCode.OK, "capabilities-status");
    using var metadata = await capabilities.ReadMetadataAsync().ConfigureAwait(false);
    Require(metadata.RootElement.GetProperty("available").GetProperty("entities").GetProperty("mutable").GetBoolean(),
        "mutable-entity-capability");
}

var model = Encoding.UTF8.GetBytes(
    """{"groups":{"dirs":{"singular":"dir","resources":{"files":{"singular":"file"}}}}}""");
using (var response = await client.SendAsync(
    HttpMethod.Put, "modelsource", model, "application/json").ConfigureAwait(false))
{
    Require(response.StatusCode == HttpStatusCode.OK, "install-model");
}

byte[] bytes = [0, 255, 13, 10, 65, 0, 127];
const string versionPath = "dirs/native-client/files/binary/versions/v1";
using (var response = await client.SendAsync(
    HttpMethod.Put, versionPath, bytes, "application/octet-stream").ConfigureAwait(false))
{
    Require(response.StatusCode == HttpStatusCode.Created, "create-version-status");
}

using (var response = await client.SendAsync(HttpMethod.Get, versionPath).ConfigureAwait(false))
{
    Require(response.StatusCode == HttpStatusCode.OK, "read-version-status");
    using var destination = new MemoryStream();
    await response.CopyDocumentToAsync(destination).ConfigureAwait(false);
    Require(Convert.ToHexString(destination.ToArray()) == "00FF0D0A41007F", "exact-binary-document");
}

using (var response = await client.SendAsync(HttpMethod.Get, versionPath + "$details").ConfigureAwait(false))
{
    Require(response.StatusCode == HttpStatusCode.OK, "metadata-status");
    using var metadata = await response.ReadMetadataAsync().ConfigureAwait(false);
    Require(metadata.RootElement.GetProperty("versionid").GetString() == "v1", "metadata-versionid");
    Require(metadata.RootElement.GetProperty("xid").GetString() == "/" + versionPath, "metadata-xid");
    Require(metadata.RootElement.GetProperty("contenttype").GetString() == "application/octet-stream",
        "metadata-contenttype");
}

using (var response = await client.SendAsync(
    HttpMethod.Get, "dirs/native-client/files/binary").ConfigureAwait(false))
{
    Require(response.StatusCode == HttpStatusCode.OK, "default-version-status");
    using var destination = new MemoryStream();
    await response.CopyDocumentToAsync(destination).ConfigureAwait(false);
    Require(Convert.ToHexString(destination.ToArray()) == "00FF0D0A41007F", "default-version-bytes");
}

using (var response = await client.SendAsync(HttpMethod.Head, versionPath).ConfigureAwait(false))
{
    Require(response.StatusCode == HttpStatusCode.MethodNotAllowed, "pinned-peer-head-unavailable");
    Console.WriteLine("Peer applicability: HEAD returns 405 at the pinned source; no HEAD conformance credit.");
}

using (var response = await client.SendAsync(HttpMethod.Get, "unavailable-collection").ConfigureAwait(false))
{
    Require(response.StatusCode == HttpStatusCode.NotFound, "unknown-path-rejection");
}

Console.WriteLine("Native .NET client -> pinned peer: 16 contract assertions and one peer-applicability assertion passed.");
Console.WriteLine("This is the initial interop slice, not full xRegistry conformance or reverse interop.");
return 0;

static void Require(bool condition, string assertion)
{
    if (!condition)
    {
        throw new InvalidDataException($"Native client interoperability failed: {assertion}.");
    }
}
