using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class FederationContractTests
{
    [Test]
    [Arguments("{}", FederationResolutionOwner.Consumer)]
    [Arguments("""{"federation":{"resolution":"consumer"}}""", FederationResolutionOwner.Consumer)]
    [Arguments("""{"federation":{"resolution":"producer"}}""", FederationResolutionOwner.Producer)]
    public async Task ResolutionOwnerUsesEnabledCapability(string json, FederationResolutionOwner expected)
    {
        using var document = JsonDocument.Parse(json);
        await Assert.That(FederationCapabilities.GetResolutionOwner(document.RootElement)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("null", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"federation":null}""", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"federation":{}}""", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"federation":{"resolution":1}}""", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"federation":{"resolution":"Producer"}}""", FederationErrorCode.UnsupportedOperation)]
    [Arguments("""{"federation":{"resolution":"server"}}""", FederationErrorCode.UnsupportedOperation)]
    [Arguments("""{"federation":{"resolution":"producer","fallback":true}}""", FederationErrorCode.UnsupportedOperation)]
    public async Task ResolutionOwnerUsesNormativeErrorCategories(string json, FederationErrorCode expected)
    {
        using var document = JsonDocument.Parse(json);
        await Check.Error(() => FederationCapabilities.GetResolutionOwner(document.RootElement), expected);
    }

    [Test]
    public async Task FrozenRequestPreservesLiteralSelector()
    {
        var request = FederationReadRequest.Parse(Encoding.UTF8.GetBytes("""
            {"operation":"collection","target":"/documents/main/assets",
             "selector":{"label":"stage","value":"*\\.=production"}}
            """));
        await Assert.That(request.Operation).IsEqualTo(FederationOperation.Collection);
        await Assert.That(request.Target).IsEqualTo("/documents/main/assets");
        await Assert.That(request.Selector!.Value).IsEqualTo("*\\.=production");
        using var labels = JsonDocument.Parse("""{"stage":"*\\.=PRODUCTION","note":""}""");
        await Assert.That(request.Selector.Matches(labels.RootElement)).IsTrue();
        await Assert.That(new FederationLabelSelector("note", "").Matches(labels.RootElement)).IsTrue();
        await Assert.That(new FederationLabelSelector("missing", "").Matches(labels.RootElement)).IsFalse();
        await Assert.That(new FederationLabelSelector("stage", "*").Matches(labels.RootElement)).IsFalse();
        await Assert.That(new FederationLabelSelector("note", "\u00e9").Matches(
            JsonDocument.Parse("""{"note":"e\u0301"}""").RootElement)).IsFalse();
    }

    [Test]
    [Arguments("""{"operation":"put","target":"/"}""", FederationErrorCode.UnsupportedOperation)]
    [Arguments("""{"operation":"model","target":"/documents/main"}""", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"operation":"entity","target":"/documents/main","selector":{"label":"stage","value":""}}""", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"operation":"document","target":"/documents/main"}""", FederationErrorCode.UnsupportedOperation)]
    [Arguments("""{"operation":"entity","target":"/documents/main/assets/item/versions/request"}""", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"operation":"entity","target":"/documents/main/assets/%2569tem"}""", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"operation":"entity","target":"/documents/main/assets/item/versions/V1/"}""", FederationErrorCode.InvalidPackage)]
    [Arguments("""{"operation":"entity","target":"/documents/main","extra":true}""", FederationErrorCode.InvalidPackage)]
    public async Task ReadRequestsRejectMutationAndNonliteralTargets(string json, FederationErrorCode expected)
    {
        await Check.Error(() => FederationReadRequest.Parse(Encoding.UTF8.GetBytes(json)), expected);
    }

    [Test]
    public async Task ReadRequestsAcceptCoreRelativeUriEscapesWithoutDiscardingTheirSpelling()
    {
        var request = FederationReadRequest.Parse(Encoding.UTF8.GetBytes(
            """{"operation":"entity","target":"/documents/group%3Aone/assets/%69tem"}"""));
        await Assert.That(request.Target).IsEqualTo("/documents/group%3Aone/assets/%69tem");
        await Assert.That(request.Operation).IsEqualTo(FederationOperation.Entity);
    }

    [Test]
    public async Task BudgetBoundsAreInclusiveAndFailuresDoNotOverflow()
    {
        var budget = new FederationReadBudget(new(maxRequests: 1, maxTotalBytes: 3, maxWork: 2,
            maxSources: 1, maxHops: 1, maxDepth: 2));
        budget.ChargeRequest();
        budget.ChargeBytes(2);
        budget.ChargeBytes(1);
        budget.ChargeWork(2);
        budget.ChargeSource();
        budget.CheckHops(1);
        budget.CheckDepth(2);
        await Check.Error(budget.ChargeRequest, FederationErrorCode.LimitExceeded);
        await Check.Error(() => budget.ChargeBytes(long.MaxValue), FederationErrorCode.LimitExceeded);
        await Check.Error(() => budget.ChargeWork(1), FederationErrorCode.LimitExceeded);
        await Check.Error(budget.ChargeSource, FederationErrorCode.LimitExceeded);
        await Check.Error(() => budget.CheckHops(2), FederationErrorCode.LimitExceeded);
        await Check.Error(() => budget.CheckDepth(3), FederationErrorCode.LimitExceeded);
        await Assert.That(budget.BytesRead).IsEqualTo(3L);
        await Assert.That(budget.Requests).IsEqualTo(1L);
    }

    [Test]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1806",
        Justification = "The constructor is deliberately invoked only to assert rejection of invalid context.")]
    public async Task NativeContextKeepsRevisionAndRootEvidenceSeparate()
    {
        var context = new NativeRegistryContext("git", "https://git.test/project", "COMMIT", true,
            "7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf");
        await Assert.That(context.Revision).IsEqualTo("COMMIT");
        await Assert.That(context.IsImmutable).IsTrue();
        await Assert.That(context.RootSha256).IsEqualTo("7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf");
        await Check.Error(() => new NativeRegistryContext("http", "https://name:password@host.test"),
            FederationErrorCode.PolicyDenied);
        await Assert.That(() => new NativeRegistryContext("git", "https://host.test", isImmutable: true))
            .Throws<ArgumentException>();
    }
}
