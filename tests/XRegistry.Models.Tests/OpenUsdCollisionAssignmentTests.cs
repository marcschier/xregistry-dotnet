// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class OpenUsdCollisionAssignmentTests
{
    [Test]
    [Arguments("a/b", "a.b", "a.b", "a.b.2e7336dc")]
    [Arguments("a.b", "a.b", "a/b", "a.b.c14cddc0")]
    [Arguments("Pump", "Pump", "pump", "pump.0b203c46")]
    public async Task AssignmentPreservesPublishedCandidatesAndSuffixesOnlyTheNewCollision(
        string original, string publishedId, string source, string fallback)
    {
        var siblings = new Dictionary<string, string>(StringComparer.Ordinal);
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId(original, siblings, 1024, 4)).IsEqualTo(publishedId);
        siblings.Add(publishedId, original);

        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId(source, siblings, 1024, 4)).IsEqualTo(fallback);
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId(original, siblings, 1024, 4)).IsEqualTo(publishedId);
        await Assert.That(siblings.Count).IsEqualTo(1);
        await Assert.That(siblings[publishedId]).IsEqualTo(original);
    }

    [Test]
    [Arguments(119, "31eba51c")]
    [Arguments(120, "2f3d3354")]
    [Arguments(127, "c57e9278")]
    [Arguments(128, "6836cf13")]
    public async Task CollisionOnlySuffixReservationPreservesTheUnoccupiedCandidate(int length, string hash)
    {
        var source = new string('a', length);
        var siblings = new Dictionary<string, string> { [source.ToUpperInvariant()] = source.ToUpperInvariant() };

        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId(source, new Dictionary<string, string>(), 1024, 1)).IsEqualTo(source);
        var result = OpenUsdIdentifiers.AssignSymbolicId(source, siblings, 1024, 1);
        await Assert.That(result).IsEqualTo(new string('a', 119) + "." + hash);
        await Assert.That(result.Length).IsEqualTo(128);
        await Assert.That(RegistryId.IsValid(result)).IsTrue();
    }

    [Test]
    [Arguments(127)]
    [Arguments(128)]
    [Arguments(129)]
    public async Task AssignmentKeepsTheOriginal127128129TruncationBoundary(int length)
    {
        var source = new string('a', length);
        var expected = length == 129 ? new string('a', 119) + ".c12cb024" : source;
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId(source, new Dictionary<string, string>(), 129, 0)).IsEqualTo(expected);
    }

    [Test]
    public async Task CollisionReservationUsesSourceLabelsAndTrimsOnlyExposedPunctuation()
    {
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate("root/" + new string('b', 115), 128))
            .IsEqualTo("root.ce99e712");
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate("a." + new string('b', 126), 128))
            .IsEqualTo("a." + new string('b', 117) + ".58166593");
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate(new string('a', 118) + "-bb", 128))
            .IsEqualTo(new string('a', 118) + ".1b16de6e");
    }

    [Test]
    [Arguments("caf\u00e9/x", "caf.x", "caf.x.2aac9a7e")]
    [Arguments("caf%C3%A9/x", "caf.x", "caf.x.a136b4af")]
    [Arguments("pkg.usdz[tex/a.png]", "pkg.usdz-tex.a.png", "pkg.usdz-tex.a.png.9cf45f74")]
    [Arguments("pkg.usdz[tex%2Fa.png]", "pkg.usdz-tex-a.png", "pkg.usdz-tex-a.png.6261f061")]
    [Arguments("https://example.test/a?rev=1", "test.example.a", "test.example.a.a8427da8")]
    [Arguments("https://example.test/a?rev=2", "test.example.a", "test.example.a.c3bb29a9")]
    [Arguments("a%252Fb", "a-2Fb", "a-2Fb.4bcfe892")]
    public async Task CollisionHashUsesExactUnicodePackageAndUriSourceWithoutASecondDecode(
        string source, string candidate, string fallback)
    {
        var siblings = new Dictionary<string, string> { [candidate] = candidate };
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 256)).IsEqualTo(candidate);
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId(source, siblings, 512, 1)).IsEqualTo(fallback);
    }

    [Test]
    public async Task AssignmentRetainsAFallbackAfterItsOriginalCompetitorDisappears()
    {
        var siblings = new Dictionary<string, string> { ["a.b.2e7336dc"] = "a.b" };
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId("a.b", siblings, 128, 1)).IsEqualTo("a.b.2e7336dc");
        await Assert.That(siblings.Count).IsEqualTo(1);
        await Assert.That(siblings["a.b.2e7336dc"]).IsEqualTo("a.b");
    }

    [Test]
    public async Task AssignmentIsDeterministicForAFixedSnapshotRegardlessOfEnumerationOrder()
    {
        var first = new Dictionary<string, string> { ["a.b"] = "a/b", ["other"] = "other" };
        var reversed = new Dictionary<string, string> { ["other"] = "other", ["a.b"] = "a/b" };
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId("a.b", first, 128, 2)).IsEqualTo("a.b.2e7336dc");
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId("a.b", reversed, 128, 2)).IsEqualTo("a.b.2e7336dc");
    }

    [Test]
    public async Task AssignmentRejectsANaturalSourceOccupyingTheSoleFallback()
    {
        var siblings = new Dictionary<string, string>
        {
            ["a.b"] = "a/b",
            ["a.b.2e7336dc"] = "a.b.2e7336dc",
        };
        var error = await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("a.b", siblings, 128, 2))
            .Throws<InvalidOperationException>();
        await Assert.That(error!.Message).IsEqualTo("Unresolved OpenUSD symbolic-ID collision: the sole fallback is occupied.");
        await Assert.That(siblings["a.b"]).IsEqualTo("a/b");
        await Assert.That(siblings["a.b.2e7336dc"]).IsEqualTo("a.b.2e7336dc");
    }

    [Test]
    public async Task RealEightHexShaCollisionIsRejectedRatherThanRehashedOrOverwritten()
    {
        const string first = "https://example.test/asset?i=184995";
        const string second = "https://example.test/asset?i=191756";
        const string fallback = "test.example.asset.df41192b";
        var siblings = new Dictionary<string, string>
        {
            ["test.example.asset"] = "test.example.asset",
            [fallback] = first,
        };
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate(first, 128)).IsEqualTo(fallback);
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate(second, 128)).IsEqualTo(fallback);
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId(second, siblings, 512, 2))
            .Throws<InvalidOperationException>();
        await Assert.That(siblings[fallback]).IsEqualTo(first);
        await Assert.That(siblings.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AnAlreadyTruncatedCandidateHasNoSecondDiscriminator()
    {
        var source = new string('a', 129);
        var candidate = new string('a', 119) + ".c12cb024";
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate(source, 129)).IsEqualTo(candidate);
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId(source,
            new Dictionary<string, string> { [candidate] = candidate }, 512, 1))
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("other")]
    [Arguments("A.B")]
    public async Task AssignmentNeverRenamesAnExistingAuthoritativeBinding(string invalidId)
    {
        var siblings = new Dictionary<string, string> { [invalidId] = "a.b" };
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("a.b", siblings, 128, 1))
            .Throws<InvalidDataException>();
        await Assert.That(siblings[invalidId]).IsEqualTo("a.b");
    }

    [Test]
    public async Task AssignmentValidatesCaseInsensitiveSiblingAndExactSourceUniqueness()
    {
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x", new Dictionary<string, string>
        {
            ["Pump"] = "Pump",
            ["pump"] = "pump",
        }, 128, 2)).Throws<InvalidDataException>();
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x", new Dictionary<string, string>
        {
            ["a.b"] = "a.b",
            ["a.b.2e7336dc"] = "a.b",
        }, 128, 2)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("-invalid")]
    [Arguments("a/b")]
    [Arguments("\u00e9")]
    [Arguments("")]
    public async Task AssignmentDoesNotWidenTheCoreSiblingIdGrammar(string id)
    {
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x",
            new Dictionary<string, string> { [id] = "old" }, 128, 1)).Throws<InvalidDataException>();
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId("x",
            new Dictionary<string, string> { ["A~:@"] = "old" }, 128, 1)).IsEqualTo("x");
    }

    [Test]
    public async Task AssignmentEnforcesInclusiveCumulativeUtf8AndSiblingBudgetsEvenForAnExistingBinding()
    {
        var siblings = new Dictionary<string, string> { ["caf"] = "caf\u00e9" };
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId("caf\u00e9", siblings, 13, 1)).IsEqualTo("caf");
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("caf\u00e9", siblings, 12, 1)).Throws<InvalidDataException>();
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("caf\u00e9", siblings, 13, 0)).Throws<InvalidDataException>();
        await Assert.That(OpenUsdIdentifiers.AssignSymbolicId("", new Dictionary<string, string>(), 0, 0)).IsEqualTo("_");
    }

    [Test]
    public async Task AssignmentRejectsInvalidArgumentsAndMalformedEncoding()
    {
        var empty = new Dictionary<string, string>();
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId(null!, empty, 128, 0)).Throws<ArgumentNullException>();
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x", null!, 128, 0)).Throws<ArgumentNullException>();
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x", empty, -1, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x", empty, 128, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("\ud800", empty, 128, 0)).Throws<ArgumentException>();
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("%C0%AF", empty, 128, 0)).Throws<ArgumentException>();
    }

    [Test]
    public async Task AssignmentHonorsCancellationBeforeAndAfterSnapshotEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        var snapshot = new ObservableSnapshot(() => cancellation.Cancel());
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x", snapshot, 128, 1, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(snapshot.Reads).IsEqualTo(1);
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x", snapshot, 128, 1, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(snapshot.Reads).IsEqualTo(1);
    }

    [Test]
    public async Task AssignmentBoundsEnumerationEvenWhenASnapshotMisreportsItsCount()
    {
        var snapshot = new ObservableSnapshot(static () => { }, repeat: true);
        await Assert.That(() => OpenUsdIdentifiers.AssignSymbolicId("x", snapshot, 128, 1))
            .Throws<InvalidDataException>();
        await Assert.That(snapshot.Reads).IsEqualTo(2);
    }

    private sealed class ObservableSnapshot(Action afterItem, bool repeat = false) : IReadOnlyDictionary<string, string>
    {
        public int Reads { get; private set; }
        public int Count => 0;
        public IEnumerable<string> Keys => ["other"];
        public IEnumerable<string> Values => ["other"];
        public string this[string key] => key == "other" ? "other" : throw new KeyNotFoundException();
        public bool ContainsKey(string key) => key == "other";
        public bool TryGetValue(string key, out string value)
        {
            value = "other";
            return ContainsKey(key);
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            do
            {
                Reads++;
                var key = repeat ? "other" + Reads.ToString(System.Globalization.CultureInfo.InvariantCulture) : "other";
                yield return new(key, key);
                afterItem();
            } while (repeat);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
