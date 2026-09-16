// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class OpenUsdIdentifierTests
{
    [Test]
    public async Task NormalizeAssetIdentifierRemovesLeadingDotSlash()
    {
        var actual = OpenUsdIdentifiers.NormalizeAssetIdentifier("./pump.usda", maxUtf8Bytes: 128);

        await Assert.That(actual).IsEqualTo("pump.usda");
    }

    [Test]
    [Arguments("pump.usda", 9, "pump.usda")]
    [Arguments("./pump.usda", 11, "pump.usda")]
    [Arguments("\u00e9", 2, "\u00e9")]
    [Arguments("\ud83d\udca1", 4, "\ud83d\udca1")]
    public async Task NormalizationHonorsInclusiveUtf8InputLimit(string input, int limit, string expected)
    {
        await Assert.That(OpenUsdIdentifiers.NormalizeAssetIdentifier(input, limit)).IsEqualTo(expected);
        await Assert.That(() => OpenUsdIdentifiers.NormalizeAssetIdentifier(input, limit - 1))
            .Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("textures/albedo.png", "textures/albedo.png")]
    [Arguments("pkg.usdz[tex/a.png]", "pkg.usdz[tex/a.png]")]
    [Arguments("textures.albedo.png", "textures.albedo.png")]
    [Arguments("./Textures/Albedo%20Map.PNG", "Textures/Albedo%20Map.PNG")]
    [Arguments("././Textures/Albedo.PNG", "Textures/Albedo.PNG")]
    [Arguments("a/../b", "a/../b")]
    [Arguments("a\\b", "a\\b")]
    [Arguments("tex/%GG.png", "tex/%GG.png")]
    public async Task NormalizationPreservesAuthoredSpelling(string input, string expected)
    {
        await Assert.That(OpenUsdIdentifiers.NormalizeAssetIdentifier(input, 128)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("./")]
    [Arguments("././")]
    public async Task NormalizationRequiresAnAuthoredIdentifier(string input)
    {
        await Assert.That(() => OpenUsdIdentifiers.NormalizeAssetIdentifier(input, 128))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments(0xd800)]
    [Arguments(0xdc00)]
    public async Task NormalizationRejectsUnpairedUtf16InsteadOfEncodingReplacementBytes(int value)
    {
        var input = "a" + (char)value + "b";
        await Assert.That(() => OpenUsdIdentifiers.NormalizeAssetIdentifier(input, 128))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task NormalizationRejectsInvalidArgumentsAndHonorsCancellation()
    {
        await Assert.That(() => OpenUsdIdentifiers.NormalizeAssetIdentifier(null!, 128))
            .Throws<ArgumentNullException>();
        await Assert.That(() => OpenUsdIdentifiers.NormalizeAssetIdentifier("a", -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => OpenUsdIdentifiers.NormalizeAssetIdentifier("a", 0))
            .Throws<InvalidDataException>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.That(() => OpenUsdIdentifiers.NormalizeAssetIdentifier("a", 1, cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("pump.usda", "pump.usda")]
    [Arguments("textures/albedo.png", "textures.albedo.png")]
    [Arguments("pkg.usdz[tex/a.png]", "pkg.usdz-tex.a.png")]
    public async Task SymbolicCandidatesMatchPublishedExamples(string source, string expected)
    {
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 128)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://contoso.org/pump.usda", "org.contoso.pump.usda")]
    [Arguments("https://contoso.org", "org.contoso")]
    [Arguments("https://localhost/pump", "localhost.pump")]
    [Arguments("https://contoso.org:443/pump", "org.contoso.443.pump")]
    [Arguments("https://contoso.org:8443/pump", "org.contoso.8443.pump")]
    [Arguments("https://contoso.org:65536/pump", "org.contoso.65536.pump")]
    [Arguments("https://contoso.org:000443/pump", "org.contoso.000443.pump")]
    [Arguments("https://[2001:db8::1]:443/pump", "2001-db8-1.443.pump")]
    [Arguments("example://[v1.Foo:Bar]:123/pump", "Foo-Bar.v1.123.pump")]
    [Arguments("file:///root/pump.usda", "root.pump.usda")]
    [Arguments("example://host/a?query=/x?y#fragment=/z?y", "host.a")]
    [Arguments("https://user@Example.COM:443/Textures/Albedo.PNG?rev=2#part", "COM.Example.443.Textures.Albedo.PNG")]
    [Arguments("https://contoso.org/a/../b", "org.contoso.a.b")]
    [Arguments("urn:vendor:Pump:part", "urn.vendor.Pump.part")]
    [Arguments("URN:vendor:Pump:part", "URN.vendor.Pump.part")]
    [Arguments("tex%2Falbedo.png/part%2Ev1", "tex-albedo.png.part.v1")]
    [Arguments("a%252Fb", "a-2Fb")]
    [Arguments("caf%C3%A9/texture", "caf.texture")]
    public async Task SymbolicCandidatesInterpretSourceComponentsWithoutUriCanonicalization(string source, string expected)
    {
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 512)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("A_Z-09/Pump.usda", "A_Z-09.Pump.usda")]
    [Arguments("A! ?B/~Pump@", "A-B.Pump")]
    [Arguments("--a---b...c--", "a-b.c")]
    [Arguments("...a...", "a")]
    [Arguments("///---/.../Pump/!?//", "Pump")]
    [Arguments("///", "_")]
    [Arguments("---/.../!?", "_")]
    [Arguments("", "_")]
    public async Task SymbolicCandidatesNormalizeLabelsWithoutLosingCase(string source, string expected)
    {
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 128)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("%")]
    [Arguments("%2")]
    [Arguments("%GG")]
    [Arguments("%C3%28")]
    [Arguments("%C0%AF")]
    [Arguments("%ED%A0%80")]
    [Arguments("%F4%90%80%80")]
    public async Task SymbolicCandidatesRejectMalformedEscapesAndDecodedUtf8(string source)
    {
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 128))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("a/b", 3, "a.b")]
    [Arguments("A/\u00e9/B", 6, "A.B")]
    [Arguments("caf%C3%A9", 9, "caf")]
    public async Task SymbolicCandidatesHonorEncodedInputByteLimits(string source, int limit, string expected)
    {
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, limit)).IsEqualTo(expected);
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, limit - 1))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task SymbolicCandidatesLeaveCollisionsToTheOwningHost()
    {
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate("A B", 3)).IsEqualTo("A-B");
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate("A?B", 3)).IsEqualTo("A-B");
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate("A B", 3)).IsEqualTo("A-B");
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate("Pump", 4)).IsEqualTo("Pump");
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate("pump", 4)).IsEqualTo("pump");
    }

    [Test]
    public async Task SymbolicCandidatesRejectInvalidArgumentsAndHonorCancellation()
    {
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate("", 0)).IsEqualTo("_");
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate(null!, 1))
            .Throws<ArgumentNullException>();
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate("a", -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate("a", 0))
            .Throws<InvalidDataException>();
        var invalid = new string((char)0xd800, 1);
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate(invalid, 8))
            .Throws<ArgumentException>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate("a", 1, cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments(119)]
    [Arguments(120)]
    [Arguments(128)]
    public async Task SymbolicCandidatesPreserveUntruncatedBoundary(int length)
    {
        var source = new string('a', length);
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, length)).IsEqualTo(source);
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate("a" + new string('!', 200) + "b", 202))
            .IsEqualTo("a-b");
    }

    [Test]
    public async Task SymbolicCandidatesShortenOnlyBeyond128()
    {
        var result = OpenUsdIdentifiers.CreateSymbolicIdCandidate(new string('a', 129), 129);
        await Assert.That(result).IsEqualTo(new string('a', 119) + ".c12cb024");
        await Assert.That(result.Length).IsEqualTo(128);
    }

    [Test]
    public async Task ShorteningDropsWholeSourceLabelsToThe119CharacterBudget()
    {
        var source = "root/" + new string('b', 114) + "/tail/last";
        var result = OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 129);
        await Assert.That(result).IsEqualTo("root." + new string('b', 114) + ".652dd2ce");
        await Assert.That(result.Length).IsEqualTo(128);
    }

    [Test]
    [Arguments("-", ".0325ab26")]
    [Arguments(".", ".f1a0f1b2")]
    public async Task ShorteningStripsNewlyExposedPunctuation(string punctuation, string suffix)
    {
        var source = new string('a', 118) + punctuation + "b" + new string('c', 9);
        var result = OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 129);
        await Assert.That(result).IsEqualTo(new string('a', 118) + suffix);
        await Assert.That(result.Length).IsEqualTo(127);
    }

    [Test]
    public async Task ShorteningDoesNotInventNewLabelBoundariesAtLiteralDots()
    {
        var source = "a." + new string('b', 127);
        var result = OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 129);
        await Assert.That(result).IsEqualTo("a." + new string('b', 117) + ".422f5fec");
        await Assert.That(result.Length).IsEqualTo(128);
    }

    [Test]
    public async Task TruncationHashesExactSourceRatherThanDecodedSpelling()
    {
        var raw = OpenUsdIdentifiers.CreateSymbolicIdCandidate(new string('A', 129), 129);
        var encoded = OpenUsdIdentifiers.CreateSymbolicIdCandidate("%41" + new string('A', 128), 131);
        await Assert.That(raw).IsEqualTo(new string('A', 119) + ".e7118c3a");
        await Assert.That(encoded).IsEqualTo(new string('A', 119) + ".b56136fc");
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate("%41" + new string('A', 128), 130))
            .Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("one", "org.contoso.b5bbc04b")]
    [Arguments("two", "org.contoso.2c724fe1")]
    public async Task TruncationHashIncludesDiscardedUriComponents(string revision, string expected)
    {
        var source = "https://contoso.org/" + new string('x', 129) + "?revision=" + revision;
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 256)).IsEqualTo(expected);
    }

    [Test]
    public async Task LongUriSyntaxDoesNotBecomeAnOrdinaryPathAtAFrameworkParserLimit()
    {
        var source = "https://contoso.org/" + new string('x', 65536);
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 65556))
            .IsEqualTo("org.contoso.10452a09");
        await Assert.That(() => OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 65555))
            .Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("1http://host/x", "1http.host.x")]
    [Arguments("example://a@@b/x", "example.a-b.x")]
    [Arguments("example://host:port/x", "example.host-port.x")]
    [Arguments("example://ho^st/x", "example.ho-st.x")]
    public async Task NonUriSourceStringsUseTheOrdinaryPathConstruction(string source, string expected)
    {
        await Assert.That(OpenUsdIdentifiers.CreateSymbolicIdCandidate(source, 128)).IsEqualTo(expected);
    }
}
