using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryEscapedIdTests
{
    [Test]
    [Arguments("name", "name")]
    [Arguments("name:one@host", "name:one@host")]
    [Arguments("name%3Aone%40host", "name:one@host")]
    [Arguments("name%3aone%40host", "name:one@host")]
    [Arguments("%6eame", "name")]
    public async Task EscapedSegmentsRetainDecodedOrdinalIdentifierIdentity(string encoded, string value)
    {
        await Assert.That(RegistryId.ParseEscaped(encoded).Value).IsEqualTo(value);
    }

    [Test]
    [Arguments("name%2Fother")]
    [Arguments("name%5Cother")]
    [Arguments("name%252Fother")]
    [Arguments("%2e%2e")]
    [Arguments("name%00")]
    [Arguments("name%20")]
    [Arguments("name%ff")]
    [Arguments("name%C0%AE")]
    [Arguments("name%")]
    [Arguments("name%GG")]
    [Arguments("\"name\"")]
    public async Task InvalidEscapesAndSecondDecodeCandidatesAreNeverIdentifiers(string value)
    {
        await Assert.That(() => RegistryId.ParseEscaped(value)).Throws<RegistryException>();
    }

    [Test]
    public async Task EscapedIdentifierByteBoundaryIsInclusive()
    {
        var encoded = string.Concat(Enumerable.Repeat("%61", 128));
        await Assert.That(RegistryId.ParseEscaped(encoded).Value).IsEqualTo(new string('a', 128));
        await Assert.That(() => RegistryId.ParseEscaped(encoded + "%61")).Throws<RegistryException>();
    }
}
