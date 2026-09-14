using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;

namespace XRegistry.Git.Tests;

public class GitSmartHttpTests
{
    [Test]
    public async Task V2NegotiationSelectsExactRefAndVerifiesTheIndependentSnapshotPack()
    {
        var posts = new List<string>();
        var oid = Fixtures.Id(GitHashAlgorithm.Sha256, "tag");
        await using var server = await StartAsync(
            Packets("version 2\n", "ls-refs=unborn\n", "fetch=shallow\n", "object-format=sha256\n"),
            body =>
            {
                posts.Add(body);
                return body.Contains("command=ls-refs", StringComparison.Ordinal)
                    ? Packets(oid + " refs/heads/main-other\n", oid + " refs/heads/main\n")
                    : V2Pack(Fixtures.Pack(GitHashAlgorithm.Sha256, "snapshot"));
            });
        var snapshot = await GitSmartHttpClient.FetchAsync(
            new Uri(new Uri(server.Urls.Single()), "/repo.git"), "refs/heads/main",
            new() { AllowLoopbackHttp = true, RootPath = "" });

        await Assert.That(snapshot.CommitId.ToString()).IsEqualTo(Fixtures.Id(GitHashAlgorithm.Sha256, "commit"));
        await Assert.That(Convert.ToHexString(snapshot.ReadBlob("nested/raw.bin").Content)).IsEqualTo("00FF0D0A62696E6172790A");
        await Assert.That(posts.Count).IsEqualTo(2);
        await Assert.That(posts[0]).Contains("ref-prefix refs/heads/main\n");
        await Assert.That(posts[1]).Contains("want " + oid + "\n");
        await Assert.That(posts[1]).Contains("deepen 1\n");
        await Assert.That(posts[1].Contains("thin-pack", StringComparison.Ordinal)).IsFalse();
        await Assert.That(posts[1].Contains("filter ", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task LegacyNegotiationSupportsSidebandAndRawPackWithoutUnadvertisedCapabilities(bool sideband)
    {
        var oid = Fixtures.Id(GitHashAlgorithm.Sha256, "commit");
        string? request = null;
        var capabilities = "object-format=sha256" + (sideband ? " side-band-64k" : "");
        await using var server = await StartAsync(
            Packets(oid + " refs/heads/main\0" + capabilities + "\n"),
            body =>
            {
                request = body;
                using var response = new MemoryStream();
                WritePacket(response, "NAK\n"u8);
                var pack = Fixtures.Pack(GitHashAlgorithm.Sha256, "snapshot");
                if (sideband)
                {
                    WritePacket(response, [1, .. pack]);
                    response.Write("0000"u8);
                }
                else { response.Write(pack); }
                return response.ToArray();
            });
        var snapshot = await GitSmartHttpClient.FetchAsync(
            new Uri(new Uri(server.Urls.Single()), "/repo.git"), "refs/heads/main",
            new() { AllowLoopbackHttp = true, RootPath = "" });

        await Assert.That(snapshot.CommitId.ToString()).IsEqualTo(oid);
        await Assert.That(request!.Contains("deepen ", StringComparison.Ordinal)).IsFalse();
        await Assert.That(request.Contains("side-band-64k", StringComparison.Ordinal)).IsEqualTo(sideband);
    }

    [Test]
    public async Task SimilarRefDoesNotReplaceAnAbsentExactRef()
    {
        var oid = Fixtures.Id(GitHashAlgorithm.Sha256, "commit");
        var requests = 0;
        await using var server = await StartAsync(
            Packets("version 2\n", "ls-refs\n", "fetch\n", "object-format=sha256\n"),
            _ =>
            {
                requests++;
                return Packets(oid + " refs/heads/main-other\n");
            });
        await Assert.That(async () => await GitSmartHttpClient.FetchAsync(
            new Uri(new Uri(server.Urls.Single()), "/repo.git"), "refs/heads/main",
            new() { AllowLoopbackHttp = true, RootPath = "" })).Throws<GitDataException>();
        await Assert.That(requests).IsEqualTo(1);
    }

    [Test]
    public async Task Sha1RequiresIndependentStrongerRootCommitmentBeforeFetch()
    {
        var requests = 0;
        await using var server = await StartAsync(Packets("version 2\n", "ls-refs\n", "fetch\n", "object-format=sha1\n"),
            _ => { requests++; return []; });
        var exception = await Assert.That(async () => await GitSmartHttpClient.FetchAsync(
            new Uri(new Uri(server.Urls.Single()), "/repo.git"), "refs/heads/main",
            new() { AllowLoopbackHttp = true })).Throws<GitDataException>()
            ?? throw new InvalidOperationException("Expected explicit SHA-1 trust rejection.");

        await Assert.That(exception.Failure).IsEqualTo(GitFailure.PolicyDenied);
        await Assert.That(requests).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Sha1SnapshotMustMatchTheCallerSuppliedRootCommitment(bool wrongRoot)
    {
        const GitHashAlgorithm algorithm = GitHashAlgorithm.Sha1;
        var content = "{}\n"u8.ToArray();
        var blob = BinaryTestData.Id(algorithm, "blob", content);
        byte[] treeContent = [.. "100644 registry.json\0"u8, .. blob.Bytes];
        var tree = BinaryTestData.Id(algorithm, "tree", treeContent);
        var commitContent = Encoding.ASCII.GetBytes(
            $"tree {tree}\nauthor Fixture <fixture@example.invalid> 0 +0000\ncommitter Fixture <fixture@example.invalid> 0 +0000\n\nroot\n");
        var commit = BinaryTestData.Id(algorithm, "commit", commitContent);
        var pack = BinaryTestData.Pack(algorithm,
            [.. BinaryTestData.Entry(3, content), .. BinaryTestData.Entry(2, treeContent), .. BinaryTestData.Entry(1, commitContent)], 3);
        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await using var server = await StartAsync(
            Packets("version 2\n", "ls-refs\n", "fetch\n", "object-format=sha1\n"),
            body => body.Contains("ls-refs", StringComparison.Ordinal)
                ? Packets(commit + " refs/heads/main\n") : V2Pack(pack));
        var options = new GitFetchOptions
        {
            AllowLoopbackHttp = true,
            RootPath = "",
            TrustedRegistryRootSha256 = wrongRoot ? new string('0', 64) : expected
        };
        var uri = new Uri(new Uri(server.Urls.Single()), "/repo.git");
        if (wrongRoot)
        {
            var exception = await Assert.That(async () => await GitSmartHttpClient.FetchAsync(uri, "refs/heads/main", options))
                .Throws<GitDataException>() ?? throw new InvalidOperationException("Expected root integrity rejection.");
            await Assert.That(exception.Failure).IsEqualTo(GitFailure.IntegrityMismatch);
        }
        else
        {
            var snapshot = await GitSmartHttpClient.FetchAsync(uri, "refs/heads/main", options);
            await Assert.That(snapshot.CommitId).IsEqualTo(commit);
            await Assert.That(Convert.ToHexString(snapshot.ReadBlob("registry.json").Content)).IsEqualTo("7B7D0A");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FatalSidebandAndCorruptPacksNeverPublishASnapshot(bool corrupt)
    {
        var oid = Fixtures.Id(GitHashAlgorithm.Sha256, "commit");
        await using var server = await StartAsync(
            Packets("version 2\n", "ls-refs\n", "fetch\n", "object-format=sha256\n"),
            body =>
            {
                if (body.Contains("ls-refs", StringComparison.Ordinal)) { return Packets(oid + " refs/heads/main\n"); }
                if (corrupt)
                {
                    var pack = Fixtures.Pack(GitHashAlgorithm.Sha256, "snapshot");
                    pack[^1] ^= 1;
                    return V2Pack(pack);
                }

                using var response = new MemoryStream();
                WritePacket(response, "packfile\n"u8);
                WritePacket(response, [3, .. "remote sensitive text must not become the error message"u8]);
                response.Write("0000"u8);
                return response.ToArray();
            });
        var exception = await Assert.That(async () => await GitSmartHttpClient.FetchAsync(
            new Uri(new Uri(server.Urls.Single()), "/repo.git"), "refs/heads/main",
            new() { AllowLoopbackHttp = true, RootPath = "" })).Throws<GitDataException>()
            ?? throw new InvalidOperationException("Expected pack failure.");
        await Assert.That(exception.Failure).IsEqualTo(corrupt ? GitFailure.IntegrityMismatch : GitFailure.RemoteFatal);
        await Assert.That(exception.Message.Contains("sensitive", StringComparison.Ordinal)).IsFalse();
    }

    private static byte[] Packets(params string[] lines)
    {
        using var stream = new MemoryStream();
        foreach (var line in lines) { WritePacket(stream, Encoding.UTF8.GetBytes(line)); }
        stream.Write("0000"u8);
        return stream.ToArray();
    }

    private static byte[] V2Pack(byte[] pack)
    {
        using var stream = new MemoryStream();
        WritePacket(stream, "packfile\n"u8);
        WritePacket(stream, [1, .. pack]);
        stream.Write("0000"u8);
        return stream.ToArray();
    }

    private static void WritePacket(Stream stream, ReadOnlySpan<byte> payload)
    {
        stream.Write(Encoding.ASCII.GetBytes((payload.Length + 4).ToString("x4", CultureInfo.InvariantCulture)));
        stream.Write(payload);
    }

    private static async Task<WebApplication> StartAsync(byte[] advertisement, Func<string, byte[]> response)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/repo.git/info/refs", () => Results.Bytes(advertisement, "application/x-git-upload-pack-advertisement"));
        app.MapPost("/repo.git/git-upload-pack", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            return Results.Bytes(response(body), "application/x-git-upload-pack-result");
        });
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }
}
