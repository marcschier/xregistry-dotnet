using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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

public class GitBindingCompletionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapabilityCountUsesTheConfiguredLimitBeforeAnyFetch(bool exceeds)
    {
        var posts = 0;
        var lines = new List<string> { "version 2\n", "ls-refs\n", "fetch\n", "object-format=sha256\n" };
        if (exceeds) { lines.Add("agent=another-capability\n"); }
        await using var server = await StartAsync(async context =>
        {
            if (context.Request.Method == "GET")
            {
                await Reply(context, Packets(lines.ToArray()), advertisement: true);
            }
            else
            {
                posts++;
                await Reply(context, Pack(Fixtures.Pack(GitHashAlgorithm.Sha256, "snapshot")), advertisement: false);
            }
        });
        var repository = new Uri(new Uri(server.Urls.Single()), "/repo.git");
        var options = new GitFetchOptions { AllowLoopbackHttp = true, RootPath = "", MaxReferences = 3 };
        if (exceeds)
        {
            var error = await Assert.That(async () => await GitSmartHttpClient.FetchAsync(repository,
                Fixtures.Id(GitHashAlgorithm.Sha256, "commit"), options)).Throws<GitDataException>()
                ?? throw new InvalidOperationException("Expected the advertised capability limit.");
            await Assert.That(error.Failure).IsEqualTo(GitFailure.LimitExceeded);
            await Assert.That(posts).IsEqualTo(0);
        }
        else
        {
            var snapshot = await GitSmartHttpClient.FetchAsync(repository, Fixtures.Id(GitHashAlgorithm.Sha256, "commit"), options);
            await Assert.That(snapshot.CommitId.ToString()).IsEqualTo(Fixtures.Id(GitHashAlgorithm.Sha256, "commit"));
            await Assert.That(posts).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ControlResponseBytesHaveAnInclusiveIndependentLimit(bool exceeds)
    {
        var advertisement = Packets("version 2\n", "ls-refs\n", "fetch\n", "object-format=sha256\n");
        var posts = 0;
        await using var server = await StartAsync(async context =>
        {
            if (context.Request.Method == "GET") { await Reply(context, advertisement, advertisement: true); }
            else
            {
                posts++;
                await Reply(context, Pack(Fixtures.Pack(GitHashAlgorithm.Sha256, "snapshot")), advertisement: false);
            }
        });
        var repository = new Uri(new Uri(server.Urls.Single()), "/repo.git");
        var options = new GitFetchOptions
        {
            AllowLoopbackHttp = true,
            RootPath = "",
            MaxControlBytes = advertisement.Length - (exceeds ? 1 : 0),
        };
        if (exceeds)
        {
            var error = await Assert.That(async () => await GitSmartHttpClient.FetchAsync(repository,
                Fixtures.Id(GitHashAlgorithm.Sha256, "commit"), options)).Throws<GitDataException>()
                ?? throw new InvalidOperationException("Expected the control-response byte limit.");
            await Assert.That(error.Failure).IsEqualTo(GitFailure.LimitExceeded);
            await Assert.That(posts).IsEqualTo(0);
        }
        else
        {
            var snapshot = await GitSmartHttpClient.FetchAsync(repository, Fixtures.Id(GitHashAlgorithm.Sha256, "commit"), options);
            await Assert.That(snapshot.CommitId.ToString()).IsEqualTo(Fixtures.Id(GitHashAlgorithm.Sha256, "commit"));
            await Assert.That(posts).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RefCountLimitsStopBeforeFetchingInsteadOfSelectingAnEarlyMatch(bool exceeds)
    {
        var oid = Fixtures.Id(GitHashAlgorithm.Sha256, "commit");
        var fetches = 0;
        await using var server = await StartAsync(async context =>
        {
            if (context.Request.Method == "GET")
            {
                await Reply(context, Packets("version 2\n", "ls-refs\n", "fetch\n", "object-format=sha256\n"), advertisement: true);
                return;
            }
            using var reader = new StreamReader(context.Request.Body);
            var request = await reader.ReadToEndAsync(context.RequestAborted);
            if (request.Contains("command=ls-refs", StringComparison.Ordinal))
            {
                var refs = new List<string> { oid + " refs/heads/main\n", oid + " refs/heads/other1\n", oid + " refs/heads/other2\n" };
                if (exceeds) { refs.Add(oid + " refs/heads/other3\n"); }
                await Reply(context, Packets(refs.ToArray()), advertisement: false);
            }
            else
            {
                fetches++;
                await Reply(context, Pack(Fixtures.Pack(GitHashAlgorithm.Sha256, "snapshot")), advertisement: false);
            }
        });
        var repository = new Uri(new Uri(server.Urls.Single()), "/repo.git");
        var options = new GitFetchOptions { AllowLoopbackHttp = true, RootPath = "", MaxReferences = 3 };
        if (exceeds)
        {
            var error = await Assert.That(async () => await GitSmartHttpClient.FetchAsync(repository,
                "refs/heads/main", options)).Throws<GitDataException>()
                ?? throw new InvalidOperationException("Expected the reference-count limit.");
            await Assert.That(error.Failure).IsEqualTo(GitFailure.LimitExceeded);
            await Assert.That(fetches).IsEqualTo(0);
        }
        else
        {
            var snapshot = await GitSmartHttpClient.FetchAsync(repository, "refs/heads/main", options);
            await Assert.That(snapshot.CommitId.ToString()).IsEqualTo(oid);
            await Assert.That(fetches).IsEqualTo(1);
        }
    }

    [Test]
    public async Task VersionOneServiceBannerKeepsExactSelectionAndOwnedPackBytes()
    {
        var oid = Fixtures.Id(GitHashAlgorithm.Sha256, "commit");
        var posts = new List<string>();
        await using var server = await StartAsync(async context =>
        {
            if (context.Request.Method == "GET")
            {
                await Reply(context, [.. Packets("# service=git-upload-pack\n"),
                    .. Packets("version 1\n", oid + " refs/heads/main\0object-format=sha256\n")], advertisement: true);
            }
            else
            {
                using var reader = new StreamReader(context.Request.Body);
                posts.Add(await reader.ReadToEndAsync(context.RequestAborted));
                using var response = new MemoryStream();
                Packet(response, "NAK\n"u8);
                response.Write(Fixtures.Pack(GitHashAlgorithm.Sha256, "snapshot"));
                await Reply(context, response.ToArray(), advertisement: false);
            }
        });
        var snapshot = await GitSmartHttpClient.FetchAsync(new Uri(new Uri(server.Urls.Single()), "/repo.git"),
            "refs/heads/main", new() { AllowLoopbackHttp = true, RootPath = "" });
        await Assert.That(snapshot.CommitId.ToString()).IsEqualTo(oid);
        await Assert.That(Convert.ToHexString(snapshot.ReadBlob("nested/raw.bin").Content)).IsEqualTo("00FF0D0A62696E6172790A");
        await Assert.That(posts.Count).IsEqualTo(1);
        await Assert.That(posts[0]).Contains("want " + oid);
        await Assert.That(posts[0].Contains("thin-pack", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments(401, GitFailure.PolicyDenied)]
    [Arguments(403, GitFailure.PolicyDenied)]
    [Arguments(301, GitFailure.RemoteFatal)]
    [Arguments(307, GitFailure.RemoteFatal)]
    public async Task AuthenticationAndRedirectRejectionsNeverForwardCredentialsOrRetry(int status, GitFailure failure)
    {
        var redirected = 0;
        await using var other = await StartAsync(context =>
        {
            Interlocked.Increment(ref redirected);
            context.Response.StatusCode = 500;
            return Task.CompletedTask;
        });
        var requests = 0;
        var credentialCalls = 0;
        await using var server = await StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.StatusCode = status;
            context.Response.Headers.Location = new Uri(new Uri(other.Urls.Single()), "/must-not-contact").AbsoluteUri;
            return Task.CompletedTask;
        });
        var repository = new Uri(new Uri(server.Urls.Single()), "/repo.git");
        var error = await Assert.That(async () => await GitSmartHttpClient.FetchAsync(repository,
            "refs/heads/main", new()
            {
                AllowLoopbackHttp = true,
                AuthorizationProvider = (uri, _) =>
                {
                    if (uri.Authority != repository.Authority) { throw new InvalidOperationException("Credential destination changed."); }
                    credentialCalls++;
                    return ValueTask.FromResult<AuthenticationHeaderValue?>(new("Bearer", "scope-test-only"));
                },
            })).Throws<GitDataException>()
            ?? throw new InvalidOperationException("Expected explicit HTTP rejection.");
        await Assert.That(error.Failure).IsEqualTo(failure);
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(credentialCalls).IsEqualTo(1);
        await Assert.That(redirected).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StalledControlBodiesRespectDeadlineAndCallerCancellation(bool callerCancels)
    {
        var requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartAsync(async context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.ContentType = "application/x-git-upload-pack-advertisement";
            await context.Response.StartAsync(context.RequestAborted);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        using var cancellation = new CancellationTokenSource();
        var fetch = GitSmartHttpClient.FetchAsync(new Uri(new Uri(server.Urls.Single()), "/repo.git"), "refs/heads/main",
            new() { AllowLoopbackHttp = true, Timeout = callerCancels ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(300) },
            cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancels) { await cancellation.CancelAsync(); }
        await Assert.That(async () => await fetch.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(requests).IsEqualTo(1);
    }

    private static async Task<WebApplication> StartAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(handler);
        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    private static async Task Reply(HttpContext context, byte[] bytes, bool advertisement)
    {
        context.Response.ContentType = advertisement
            ? "application/x-git-upload-pack-advertisement" : "application/x-git-upload-pack-result";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static byte[] Packets(params string[] lines)
    {
        using var bytes = new MemoryStream();
        foreach (var line in lines) { Packet(bytes, Encoding.UTF8.GetBytes(line)); }
        bytes.Write("0000"u8);
        return bytes.ToArray();
    }

    private static byte[] Pack(byte[] pack)
    {
        using var bytes = new MemoryStream();
        Packet(bytes, "packfile\n"u8);
        Packet(bytes, [1, .. pack]);
        bytes.Write("0000"u8);
        return bytes.ToArray();
    }

    private static void Packet(Stream stream, ReadOnlySpan<byte> payload)
    {
        stream.Write(Encoding.ASCII.GetBytes((payload.Length + 4).ToString("x4", CultureInfo.InvariantCulture)));
        stream.Write(payload);
    }
}
