// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Http;

namespace XRegistry.Http.Tests;

public class RegistryHeaderEncodingTests
{
    [Test]
    [Arguments("", "")]
    [Arguments("plain-._~:@/\\!#$&'()*+,;=?", "plain-._~:@/\\!#$&'()*+,;=?")]
    [Arguments("Euro \u20AC \U0001F600", "Euro%20%E2%82%AC%20%F0%9F%98%80")]
    [Arguments("a%22 b\"c", "a%2522%20b%22c")]
    [Arguments("\r\n\0\t", "%0D%0A%00%09")]
    public async Task EncodingMatchesIndependentCanonicalWireValues(string value, string expected)
    {
        await Assert.That(RegistryHeaderEncoding.Encode(value)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("", "")]
    [Arguments("Euro%20%e2%82%ac%20%f0%9f%98%80", "Euro \u20AC \U0001F600")]
    [Arguments("%41%42%43", "ABC")]
    [Arguments("%2520", "%20")]
    [Arguments("\"a\\\"b\\\\c%20d\"", "a\"b\\c d")]
    [Arguments("\t \"null\" ", "null")]
    [Arguments("%0D%0A%00%09", "\r\n\0\t")]
    public async Task DecodingHandlesLegacyQuotingUtf8AndOnePercentRound(string value, string expected)
    {
        await Assert.That(RegistryHeaderEncoding.Decode(value)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("%")]
    [Arguments("%0")]
    [Arguments("%GG")]
    [Arguments("%C0%A0")]
    [Arguments("%ED%A0%80")]
    [Arguments("%F4%90%80%80")]
    [Arguments("%FF")]
    [Arguments("%E2%82")]
    [Arguments("\"unterminated")]
    [Arguments("\"escaped\\\"")]
    [Arguments("\"a\"suffix")]
    [Arguments("raw\"quote")]
    [Arguments("raw\r\nvalue")]
    [Arguments("\u20AC")]
    public async Task InvalidWireValuesAreRejected(string value)
    {
        await Assert.That(() => RegistryHeaderEncoding.Decode(value)).Throws<FormatException>();
    }

    [Test]
    public async Task EncodingRejectsUnpairedSurrogates()
    {
        await Assert.That(() => RegistryHeaderEncoding.Encode("\uD800")).Throws<FormatException>();
        await Assert.That(() => RegistryHeaderEncoding.Encode("\uDC00")).Throws<FormatException>();
    }

    [Test]
    public async Task LimitsApplyToActualEncodedAndDecodedUtf8Bytes()
    {
        await Assert.That(RegistryHeaderEncoding.Encode("\u20AC", 9)).IsEqualTo("%E2%82%AC");
        await Assert.That(() => RegistryHeaderEncoding.Encode("\u20AC", 8)).Throws<FormatException>();
        await Assert.That(RegistryHeaderEncoding.Decode("%E2%82%AC", 3)).IsEqualTo("\u20AC");
        await Assert.That(() => RegistryHeaderEncoding.Decode("%E2%82%AC", 2)).Throws<FormatException>();
        await Assert.That(RegistryHeaderEncoding.Encode("", 0)).IsEqualTo("");
        await Assert.That(RegistryHeaderEncoding.Decode("", 0)).IsEqualTo("");
        await Assert.That(() => RegistryHeaderEncoding.Encode("a", 0)).Throws<FormatException>();
        await Assert.That(() => RegistryHeaderEncoding.Decode("a", 0)).Throws<FormatException>();
    }

    [Test]
    public async Task NegativeLimitsAndNullInputsAreExplicitArgumentErrors()
    {
        await Assert.That(() => RegistryHeaderEncoding.Encode("a", -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RegistryHeaderEncoding.Decode("a", -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RegistryHeaderEncoding.Encode(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => RegistryHeaderEncoding.Decode(null!)).Throws<ArgumentNullException>();
    }
}
