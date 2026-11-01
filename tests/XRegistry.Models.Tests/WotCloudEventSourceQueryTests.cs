// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class WotCloudEventSourceQueryTests
{
    [Test]
    [Arguments("wotid=urn:fabrikam:lamp:42", null, "")]
    [Arguments("wotid=urn:fabrikam:lamp:42;wotversion=1.0.0;site=north%2Fsouth", "1.0.0", "site=north%2Fsouth")]
    [Arguments("wotid=urn:fabrikam:lamp:42;site=+%ZZ&x=y", null, "site=+%ZZ&x=y")]
    [Arguments("wotid=urn:fabrikam:lamp:42;WotVersion=5", null, "WotVersion=5")]
    public async Task SourceQueryRetainsExactSelectionAndUninterpretedAdditionalParameters(
        string query, string? versionId, string additional)
    {
        var result = WotCloudEventSourceQuery.Parse(query);
        await Assert.That(result.WotId).IsEqualTo("urn:fabrikam:lamp:42");
        await Assert.That(result.VersionId).IsEqualTo(versionId);
        await Assert.That(result.AdditionalParameters).IsEqualTo(additional);
    }

    [Test]
    public async Task SourceValuesDecodeExactlyOnceWithoutFormQuerySemantics()
    {
        var result = WotCloudEventSourceQuery.Parse("wotid=a%252F%3b%3D%26%2B%23%20%C3%A9;wotversion=v%2B1");
        await Assert.That(result.WotId).IsEqualTo("a%2F;=&+# \u00e9");
        await Assert.That(result.VersionId).IsEqualTo("v+1");
    }

    [Test]
    public async Task ProducerEncodingPreservesRequiredCharactersAndUsesUppercaseUtf8Escapes()
    {
        const string safe = "aAZ09-._~!$'()*,:@/?";
        var encoded = WotCloudEventSourceQuery.Encode(safe + "%;=&+# \u00e9", "v+1");
        await Assert.That(encoded).IsEqualTo("wotid=" + safe + "%25%3B%3D%26%2B%23%20%C3%A9;wotversion=v%2B1");
        var decoded = WotCloudEventSourceQuery.Parse(encoded);
        await Assert.That(decoded.WotId).IsEqualTo(safe + "%;=&+# \u00e9");
        await Assert.That(decoded.VersionId).IsEqualTo("v+1");
    }

    [Test]
    [Arguments("")]
    [Arguments("wotid")]
    [Arguments("wotid=")]
    [Arguments("WotId=x")]
    [Arguments("%77otid=x")]
    [Arguments("site=x;wotid=y")]
    [Arguments("wotversion=1;wotid=x")]
    [Arguments("wotid=x;wotid=x")]
    [Arguments("wotid=x;wotversion=1;wotversion=1")]
    [Arguments("wotid=x;site=y;wotversion=1")]
    [Arguments("wotid=x;wotversion=")]
    [Arguments("wotid=x;wotversion")]
    [Arguments("wotid=x+y")]
    [Arguments("wotid=x&y")]
    [Arguments("wotid=x=y")]
    [Arguments("wotid=x#y")]
    [Arguments("wotid=x y")]
    [Arguments("wotid=%")]
    [Arguments("wotid=%0")]
    [Arguments("wotid=%GG")]
    [Arguments("wotid=%FF")]
    [Arguments("wotid=%C0%AF")]
    [Arguments("wotid=%ED%A0%80")]
    [Arguments("wotid=%F4%90%80%80")]
    public async Task InvalidStandardizedSourceParametersFailExplicitly(string query)
    {
        RegistryException? error = null;
        try
        {
            WotCloudEventSourceQuery.Parse(query);
        }
        catch (RegistryException exception)
        {
            error = exception;
        }
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid-source");
        await Assert.That(error.Diagnostic.Path).IsEqualTo("/source");
    }

    [Test]
    [Arguments("", null)]
    [Arguments("id", "")]
    [Arguments("\uD800", null)]
    public async Task ProducerRejectsEmptyValuesAndInvalidUnicode(string wotId, string? versionId)
    {
        RegistryException? error = null;
        try
        {
            WotCloudEventSourceQuery.Encode(wotId, versionId);
        }
        catch (RegistryException exception)
        {
            error = exception;
        }
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("invalid-source");
    }

    [Test]
    public async Task SourceQueryLengthBoundariesApplyToEncodedAndRawQueries()
    {
        var value = new string('a', 16_378);
        var encoded = WotCloudEventSourceQuery.Encode(value);
        await Assert.That(encoded.Length).IsEqualTo(16_384);
        await Assert.That(WotCloudEventSourceQuery.Parse(encoded).WotId).IsEqualTo(value);
        foreach (var encode in new[] { false, true })
        {
            RegistryException? error = null;
            try
            {
                if (encode)
                {
                    WotCloudEventSourceQuery.Encode(value + "a");
                }
                else
                {
                    WotCloudEventSourceQuery.Parse(encoded + "a");
                }
            }
            catch (RegistryException exception)
            {
                error = exception;
            }
            await Assert.That(error).IsNotNull();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("limit.source-query");
        }
    }
}
