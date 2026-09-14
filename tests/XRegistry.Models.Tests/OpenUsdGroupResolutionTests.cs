using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class OpenUsdGroupResolutionTests
{
    [Test]
    [Arguments("a..b")]
    [Arguments("schema:main@v1")]
    [Arguments("Pump")]
    public async Task LegalContainerIdsUseOnlyTheirVerbatimLocation(string name)
    {
        var probes = new List<string>();
        var result = await OpenUsdGroupResolution.ResolveAsync(name, OpenUsdGroupKind.AssetContainer, (id, _, _) =>
        {
            probes.Add(id);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Group(id, name, container: true));
        }, new OpenUsdResolutionLimits(128, 512));
        await Assert.That(result.GroupId).IsEqualTo(name);
        await Assert.That(result.Name).IsEqualTo(name);
        await Assert.That(probes.Count).IsEqualTo(1);
        await Assert.That(probes[0]).IsEqualTo(name);
    }

    [Test]
    public async Task MissingLegalContainerNeverTriesASymbolicOrHashedAlternative()
    {
        var probes = new List<string>();
        await Assert.That(async () => await OpenUsdGroupResolution.ResolveAsync("a..b", OpenUsdGroupKind.AssetContainer,
            (id, _, _) =>
            {
                probes.Add(id);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            }, new OpenUsdResolutionLimits(4, 256))).Throws<KeyNotFoundException>();
        await Assert.That(string.Join(",", probes)).IsEqualTo("a..b");
    }

    [Test]
    public async Task SymbolicContainerNamesAreNotNormalizedAsAssetIdentifiers()
    {
        var probes = new List<string>();
        var result = await OpenUsdGroupResolution.ResolveAsync("./scene", OpenUsdGroupKind.AssetContainer,
            (id, _, _) =>
            {
                probes.Add(id);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Group(id, "./scene", container: true));
            }, new OpenUsdResolutionLimits(7, 256));
        await Assert.That(result.GroupId).IsEqualTo("scene");
        await Assert.That(result.Name).IsEqualTo("./scene");
        await Assert.That(string.Join(",", probes)).IsEqualTo("scene");
    }

    [Test]
    [Arguments("""{"usdschemaplugingroupid":"a.b","name":"a/b"}""", true)]
    [Arguments("""{"usdschemaplugingroupid":"A.B","name":"a.b"}""", false)]
    [Arguments("""{"usdschemaplugingroupid":"a.b","name":""}""", false)]
    [Arguments("""{"usdassetgroupid":"a.b","name":"a.b"}""", false)]
    [Arguments("""{"usdschemaplugingroupid":"a.b","assetidentifier":"a.b"}""", false)]
    public async Task GroupLookupDistinguishesAValidNonmatchFromMalformedIdentity(string first, bool fallback)
    {
        var probes = new List<string>();
        ValueTask<ReadOnlyMemory<byte>?> Read(string id, int allowance, CancellationToken token)
        {
            probes.Add(id);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(id == "a.b"
                ? Encoding.UTF8.GetBytes(first) : Group(id, "a.b"));
        }
        if (fallback)
        {
            var result = await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin, Read, new(3, 512));
            await Assert.That(result.GroupId).IsEqualTo("a.b.2e7336dc");
            await Assert.That(string.Join(",", probes)).IsEqualTo("a.b,a.b.2e7336dc");
        }
        else
        {
            await Assert.That(async () => await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin,
                Read, new(3, 512))).Throws<InvalidDataException>();
            await Assert.That(string.Join(",", probes)).IsEqualTo("a.b");
        }
    }

    [Test]
    public async Task GroupLookupBudgetsAreInclusiveAndCumulativeAndResultsOwnTheirMetadata()
    {
        var first = Group("a.b", "a/b");
        var second = Group("a.b.2e7336dc", "a.b");
        var total = first.Length + second.Length;
        var allowances = new List<int>();
        ValueTask<ReadOnlyMemory<byte>?> Read(string id, int allowance, CancellationToken token)
        {
            allowances.Add(allowance);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(id == "a.b" ? first : second);
        }
        var result = await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin, Read, new(3, total));
        await Assert.That(allowances.Count).IsEqualTo(2);
        await Assert.That(allowances[0]).IsEqualTo(total);
        await Assert.That(allowances[1]).IsEqualTo(second.Length);
        await Assert.That(async () => await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin,
            Read, new(3, total - 1))).Throws<InvalidDataException>();
        await Assert.That(async () => await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin,
            Read, new(2, total))).Throws<InvalidDataException>();
        await Assert.That(async () => await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin,
            Read, new(3, total, 1))).Throws<InvalidDataException>();
        Array.Fill(second, (byte)'!');
        await Assert.That(result.Metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("a.b");
    }

    [Test]
    public async Task GroupLookupAccessFailureAndCancellationNeverBecomeFallbackMisses()
    {
        var calls = 0;
        await Assert.That(async () => await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin,
            (_, _, _) =>
            {
                calls++;
                throw new UnauthorizedAccessException("Explicit metadata access denied.");
            }, new(3, 256))).Throws<UnauthorizedAccessException>();
        await Assert.That(calls).IsEqualTo(1);
        using var canceled = new CancellationTokenSource();
        await Assert.That(async () => await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin,
            async (_, _, _) =>
            {
                await canceled.CancelAsync();
                return Group("a.b", "a.b");
            }, new(3, 256), canceled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task SymbolicPluginLookupFindsARetainedFallbackByExactNameWithoutAcquisition()
    {
        var probes = new List<string>();
        var result = await OpenUsdGroupResolution.ResolveAsync("a.b", OpenUsdGroupKind.SchemaPlugin,
            (id, _, _) =>
            {
                probes.Add(id);
                if (id == "a.b") { return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    Encoding.UTF8.GetBytes("""{"usdschemaplugingroupid":"a.b.2e7336dc","name":"a.b","opaque":{"url":"https://unacquired.invalid"}}"""));
            }, new OpenUsdResolutionLimits(3, 256));

        await Assert.That(result.GroupId).IsEqualTo("a.b.2e7336dc");
        await Assert.That(result.Name).IsEqualTo("a.b");
        await Assert.That(result.Kind).IsEqualTo(OpenUsdGroupKind.SchemaPlugin);
        await Assert.That(result.Metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("a.b");
        await Assert.That(string.Join(",", probes)).IsEqualTo("a.b,a.b.2e7336dc");
    }

    private static byte[] Group(string id, string name, bool container = false) =>
        Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        {
            [container ? "usdassetgroupid" : "usdschemaplugingroupid"] = id,
            ["name"] = name,
        }.ToJsonString());
}
