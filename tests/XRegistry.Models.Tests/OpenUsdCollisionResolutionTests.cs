using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class OpenUsdCollisionResolutionTests
{
    [Test]
    public async Task ForwardResolutionChecksTheAuthoritativeIdentifierBeforeAcceptingACandidate()
    {
        var probes = new List<string>();
        var result = await OpenUsdResolution.ResolveAsync("./a.b", (id, _, _) =>
        {
            probes.Add(id);
            var json = id == "a.b"
                ? """{"usdassetid":"a.b","assetidentifier":"a/b","name":"a.b"}"""
                : """{"usdassetid":"a.b.2e7336dc","assetidentifier":"a.b"}""";
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(json));
        }, new OpenUsdResolutionLimits(128, 512));

        await Assert.That(result.ResourceId).IsEqualTo("a.b.2e7336dc");
        await Assert.That(result.AssetIdentifier).IsEqualTo("a.b");
        await Assert.That(result.Metadata.RootElement.GetProperty("assetidentifier").GetString()).IsEqualTo("a.b");
        await Assert.That(string.Join(",", probes)).IsEqualTo("a.b,a.b.2e7336dc");
    }

    [Test]
    public async Task APrimaryMatchStopsAfterOneProbeAndOwnsItsJsonWithoutFollowingDocumentUrls()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            {"usdassetid":"a.b","assetidentifier":"a/b","usdasseturl":"https://unacquired.invalid/artifact","xref":"/unused"}
            """);
        var calls = 0;
        var result = await OpenUsdResolution.ResolveAsync("a/b", (id, allowance, _) =>
        {
            calls++;
            if (id != "a.b" || allowance != bytes.Length) { throw new InvalidOperationException("Unexpected metadata request."); }
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes);
        }, new OpenUsdResolutionLimits(3, bytes.Length));
        Array.Fill(bytes, (byte)0);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.ResourceId).IsEqualTo("a.b");
        await Assert.That(result.AssetIdentifier).IsEqualTo("a/b");
        await Assert.That(result.Metadata.RootElement.GetProperty("assetidentifier").GetString()).IsEqualTo("a/b");
        await Assert.That(result.Metadata.RootElement.GetProperty("usdasseturl").GetString())
            .IsEqualTo("https://unacquired.invalid/artifact");
    }

    [Test]
    public async Task ResolutionFindsAPersistedFallbackAfterThePrimaryOwnerWasDeleted()
    {
        var probes = new List<string>();
        var result = await OpenUsdResolution.ResolveAsync("a.b", (id, _, _) =>
        {
            probes.Add(id);
            if (id == "a.b") { return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null); }
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                Encoding.UTF8.GetBytes("""{"usdassetid":"a.b.2e7336dc","assetidentifier":"a.b"}"""));
        }, new OpenUsdResolutionLimits(3, 256));
        await Assert.That(result.ResourceId).IsEqualTo("a.b.2e7336dc");
        await Assert.That(string.Join(",", probes)).IsEqualTo("a.b,a.b.2e7336dc");
    }

    [Test]
    public async Task MissingSourceFailsAfterExactlyTwoForwardProbesWithoutAnInverseOrAcquisition()
    {
        var probes = new List<string>();
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", (id, _, _) =>
        {
            probes.Add(id);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }, new OpenUsdResolutionLimits(3, 256))).Throws<KeyNotFoundException>();
        await Assert.That(string.Join(",", probes)).IsEqualTo("a.b,a.b.2e7336dc");
    }

    [Test]
    public async Task ARealSecondaryHashCollisionDoesNotResolveToTheOtherAuthoritativeSource()
    {
        var probes = new List<string>();
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("https://example.test/asset?i=191756",
            (id, _, _) =>
            {
                probes.Add(id);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Metadata(id,
                    id == "test.example.asset" ? "test.example.asset" : "https://example.test/asset?i=184995"));
            }, new OpenUsdResolutionLimits(128, 512))).Throws<KeyNotFoundException>();
        await Assert.That(string.Join(",", probes)).IsEqualTo("test.example.asset,test.example.asset.df41192b");
    }

    [Test]
    public async Task AnAlreadyTruncatedSourceNeedsOnlyOneProbeAndNeverReceivesASecondHash()
    {
        var probes = new List<string>();
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync(new string('a', 129), (id, _, _) =>
        {
            probes.Add(id);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }, new OpenUsdResolutionLimits(129, 256, 1))).Throws<KeyNotFoundException>();
        await Assert.That(probes.Count).IsEqualTo(1);
        await Assert.That(probes[0]).IsEqualTo(new string('a', 119) + ".c12cb024");
    }

    [Test]
    [Arguments("caf\u00e9/x", "caf.x", "caf.x.2aac9a7e")]
    [Arguments("caf%C3%A9/x", "caf.x", "caf.x.a136b4af")]
    [Arguments("pkg.usdz[tex/a.png]", "pkg.usdz-tex.a.png", "pkg.usdz-tex.a.png.9cf45f74")]
    [Arguments("https://example.test/a?rev=1", "test.example.a", "test.example.a.a8427da8")]
    [Arguments("a%252Fb", "a-2Fb", "a-2Fb.4bcfe892")]
    public async Task ForwardProbesPreserveExactSourceHashAndOnePassDecoding(string source, string candidate, string fallback)
    {
        var probes = new List<string>();
        var result = await OpenUsdResolution.ResolveAsync("./" + source, (id, _, _) =>
        {
            probes.Add(id);
            if (id == candidate) { return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null); }
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Metadata(id, source));
        }, new OpenUsdResolutionLimits(256, 512));
        await Assert.That(string.Join(",", probes)).IsEqualTo(candidate + "," + fallback);
        await Assert.That(result.AssetIdentifier).IsEqualTo(source);
        await Assert.That(result.ResourceId).IsEqualTo(fallback);
    }

    [Test]
    public async Task SourceUtf8BudgetIsInclusiveAndAppliedBeforeNormalization()
    {
        var calls = 0;
        ValueTask<ReadOnlyMemory<byte>?> Read(string id, int allowance, CancellationToken token)
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Metadata(id, "caf\u00e9/x"));
        }
        var result = await OpenUsdResolution.ResolveAsync("./caf\u00e9/x", Read, new OpenUsdResolutionLimits(9, 256));
        await Assert.That(result.AssetIdentifier).IsEqualTo("caf\u00e9/x");
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("./caf\u00e9/x", Read,
            new OpenUsdResolutionLimits(8, 256))).Throws<InvalidDataException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""{"assetidentifier":"a.b"}""")]
    [Arguments("""{"usdassetid":null,"assetidentifier":"a.b"}""")]
    [Arguments("""{"usdassetid":42,"assetidentifier":"a.b"}""")]
    [Arguments("""{"usdassetid":"a.b","name":"a.b"}""")]
    [Arguments("""{"usdassetid":"a.b","assetidentifier":null}""")]
    [Arguments("""{"usdassetid":"a.b","assetidentifier":42}""")]
    [Arguments("""{"usdassetid":"a.b","assetidentifier":""}""")]
    [Arguments("""{"usdassetid":"a.b","assetidentifier":"./a.b"}""")]
    [Arguments("""{"usdassetid":"A.B","assetidentifier":"a.b"}""")]
    [Arguments("""{"usdassetid":"a.b.2e7336dc","assetidentifier":"a.b"}""")]
    public async Task MalformedOrMisaddressedMetadataIsAnErrorNotAFallbackMiss(string json)
    {
        var calls = 0;
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", (_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(json));
        }, new OpenUsdResolutionLimits(3, 256))).Throws<InvalidDataException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("")]
    [Arguments("""{"usdassetid":"a.b","assetidentifier":"other","assetidentifier":"a.b"}""")]
    public async Task StrictJsonFailuresNeverBecomeMissingResources(string json)
    {
        var calls = 0;
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", (_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(json));
        }, new OpenUsdResolutionLimits(3, 256))).Throws<RegistryException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task MetadataRetainsCoreUtf8AndNestingBudgets()
    {
        var calls = 0;
        byte[] bytes = [0xff];
        ValueTask<ReadOnlyMemory<byte>?> Read(string id, int allowance, CancellationToken token)
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes);
        }
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", Read,
            new OpenUsdResolutionLimits(3, 1024))).Throws<RegistryException>();
        await Assert.That(calls).IsEqualTo(1);
        bytes = Encoding.UTF8.GetBytes("""{"usdassetid":"a.b","assetidentifier":"a.b","extra":""" +
            new string('[', 65) + "0" + new string(']', 65) + "}");
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", Read,
            new OpenUsdResolutionLimits(3, 1024))).Throws<RegistryException>();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task MetadataByteBudgetIsCumulativeAndInclusiveAcrossBothProbes()
    {
        var first = Metadata("a.b", "a/b");
        var second = Metadata("a.b.2e7336dc", "a.b");
        var allowances = new List<int>();
        ValueTask<ReadOnlyMemory<byte>?> Read(string id, int allowance, CancellationToken token)
        {
            allowances.Add(allowance);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(id == "a.b" ? first : second);
        }
        var total = first.Length + second.Length;
        var result = await OpenUsdResolution.ResolveAsync("a.b", Read, new OpenUsdResolutionLimits(3, total));
        await Assert.That(result.ResourceId).IsEqualTo("a.b.2e7336dc");
        await Assert.That(allowances.Count).IsEqualTo(2);
        await Assert.That(allowances[0]).IsEqualTo(total);
        await Assert.That(allowances[1]).IsEqualTo(second.Length);
        allowances.Clear();

        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", Read,
            new OpenUsdResolutionLimits(3, total - 1))).Throws<InvalidDataException>();
        await Assert.That(allowances.Count).IsEqualTo(2);
        await Assert.That(allowances[0]).IsEqualTo(total - 1);
        await Assert.That(allowances[1]).IsEqualTo(second.Length - 1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task ProbeQuotaExhaustionIsNotReportedAsNotFound(int maximum)
    {
        var calls = 0;
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", (_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }, new OpenUsdResolutionLimits(3, 256, maximum))).Throws<InvalidDataException>();
        await Assert.That(calls).IsEqualTo(maximum);
    }

    [Test]
    public async Task AnExhaustedByteAllowanceStopsBeforeAnotherCallback()
    {
        var first = Metadata("a.b", "a/b");
        var calls = 0;
        ValueTask<ReadOnlyMemory<byte>?> Read(string id, int allowance, CancellationToken token)
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(first);
        }
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", Read,
            new OpenUsdResolutionLimits(3, 0))).Throws<InvalidDataException>();
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", Read,
            new OpenUsdResolutionLimits(3, first.Length))).Throws<InvalidDataException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task OversizedCallbackMemoryIsRejectedBeforeItCanBeParsedAsJson()
    {
        var calls = 0;
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", (_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(new byte[4]);
        }, new OpenUsdResolutionLimits(3, 3))).Throws<InvalidDataException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task AuthorizationFailurePropagatesWithoutTryingAnotherId()
    {
        var original = new UnauthorizedAccessException("Denied by the explicit callback.");
        var calls = 0;
        var error = await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", (_, _, _) =>
        {
            calls++;
            throw original;
        }, new OpenUsdResolutionLimits(3, 256))).Throws<UnauthorizedAccessException>();
        await Assert.That(ReferenceEquals(error, original)).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task TransportFailurePropagatesWithoutTryingAnotherId()
    {
        var original = new IOException("Incomplete metadata read.");
        var calls = 0;
        var error = await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", (_, _, _) =>
        {
            calls++;
            throw original;
        }, new OpenUsdResolutionLimits(3, 256))).Throws<IOException>();
        await Assert.That(ReferenceEquals(error, original)).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ResolutionObservesCancellationBeforeAndAfterTheCallback()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        ValueTask<ReadOnlyMemory<byte>?> Read(string id, int allowance, CancellationToken token)
        {
            calls++;
            cancellation.Cancel();
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Metadata(id, "a.b"));
        }
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", Read,
            new OpenUsdResolutionLimits(3, 256), cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", Read,
            new OpenUsdResolutionLimits(3, 256), cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task AnActiveCallbackReceivesTheCallerCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = OpenUsdResolution.ResolveAsync("a.b", async (_, _, token) =>
        {
            entered.SetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }, new OpenUsdResolutionLimits(3, 256), cancellation.Token).AsTask();
        var received = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(received).IsEqualTo(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.That(async () => await result.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CancellationDoesNotAbandonAnUncooperativeCallbackOrAcceptItsLateSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<ReadOnlyMemory<byte>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var result = OpenUsdResolution.ResolveAsync("a.b", (_, _, _) =>
        {
            calls++;
            return new(pending.Task);
        }, new OpenUsdResolutionLimits(3, 256), cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.That(result.IsCompleted).IsFalse();
        pending.SetResult(Metadata("a.b", "a.b"));
        await Assert.That(async () => await result.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ResolutionRejectsInvalidLimitsAndSourceBeforeAnyCallback()
    {
        await Assert.That(() => new OpenUsdResolutionLimits(-1, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new OpenUsdResolutionLimits(1, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new OpenUsdResolutionLimits(1, 1, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new OpenUsdResolutionLimits(1, 1, 3)).Throws<ArgumentOutOfRangeException>();
        var calls = 0;
        ValueTask<ReadOnlyMemory<byte>?> Read(string id, int allowance, CancellationToken token)
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync(null!, Read, new OpenUsdResolutionLimits(128, 256)))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", null!, new OpenUsdResolutionLimits(128, 256)))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await OpenUsdResolution.ResolveAsync("a.b", Read, null!)).Throws<ArgumentNullException>();
        foreach (var source in new[] { "", "./", "\ud800", "%C0%AF", "%GG" })
        {
            await Assert.That(async () => await OpenUsdResolution.ResolveAsync(source, Read, new OpenUsdResolutionLimits(128, 256)))
                .Throws<ArgumentException>();
        }
        await Assert.That(calls).IsEqualTo(0);
    }

    private static byte[] Metadata(string id, string source) => Encoding.UTF8.GetBytes(RegistryJson.Create(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("usdassetid", id);
        writer.WriteString("assetidentifier", source);
        writer.WriteEndObject();
    }).RootElement.GetRawText());
}
