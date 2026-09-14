using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryTimestampEnumTests
{
    private const string DefaultDefinition = """
        {"type":"timestamp","required":true,"default":"2026-09-11T14:00:00+02:00",
         "enum":["2026-09-11T14:00:00+02:00"]}
        """;

    [Test]
    public async Task RequiredOffsetDefaultIsStableAcrossRepeatedValidation()
    {
        var definitions = Shape("""
            {"t":{"type":"timestamp","required":true,"default":"2026-09-11T14:00:00+02:00",
              "enum":["2026-09-11T14:00:00+02:00"]}}
            """);

        var first = RegistryMetadataValidator.Validate(RegistryJson.Parse("{}"), definitions);
        await Assert.That(first.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00Z");
        var second = RegistryMetadataValidator.Validate(first.Metadata, definitions);

        await Assert.That(second.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00Z");
        await Assert.That(second.Metadata.RootElement.GetRawText()).IsEqualTo(first.Metadata.RootElement.GetRawText());
    }

    [Test]
    [Arguments("2026-09-11T14:00:00+02:00", "2026-09-11T14:00:00+02:00", "2026-09-11T12:00:00Z")]
    [Arguments("2026-09-11T14:00:00+02:00", "2026-09-11T12:00:00Z", "2026-09-11T12:00:00Z")]
    [Arguments("2026-09-11T14:00:00+02:00", "2026-09-11t07:00:00-05:00", "2026-09-11T12:00:00Z")]
    [Arguments("2026-09-11T14:00:00.123456789+02:00", "2026-09-11T12:00:00.123456789Z", "2026-09-11T12:00:00.123456789Z")]
    [Arguments("2026-09-11T17:45:00.12345678901234567890123456789+05:45",
        "2026-09-11T08:30:00.12345678901234567890123456789-03:30", "2026-09-11T12:00:00.12345678901234567890123456789Z")]
    [Arguments("2026-09-11T14:00:00.100+02:00", "2026-09-11T12:00:00.1Z", "2026-09-11T12:00:00.1Z")]
    [Arguments("2026-09-11T14:00:00.1+02:00", "2026-09-11T12:00:00.100000000Z", "2026-09-11T12:00:00.100000000Z")]
    [Arguments("2026-09-11T14:00:00+02:00", "2026-09-11T12:00:00.000000000Z", "2026-09-11T12:00:00.000000000Z")]
    [Arguments("2026-09-11T14:00:00.000000000+02:00", "2026-09-11T12:00:00Z", "2026-09-11T12:00:00Z")]
    [Arguments("2027-01-01T00:15:00+00:30", "2027-01-01T00:15:00+00:30", "2026-12-31T23:45:00Z")]
    [Arguments("2026-12-31T23:45:00-00:30", "2026-12-31T23:45:00-00:30", "2027-01-01T00:15:00Z")]
    [Arguments("2026-09-12T11:59:00+23:59", "2026-09-11T12:00:00Z", "2026-09-11T12:00:00Z")]
    [Arguments("0001-01-01T00:30:00+00:30", "0001-01-01T00:00:00Z", "0001-01-01T00:00:00Z")]
    [Arguments("9999-12-31T23:29:59.999999999-00:30", "9999-12-31T23:59:59.999999999Z", "9999-12-31T23:59:59.999999999Z")]
    [Arguments("2016-12-31T18:59:60.123456789-05:00", "2016-12-31T23:59:60.123456789Z", "2016-12-31T23:59:60.123456789Z")]
    public async Task ExplicitTimestampVariantsAreStableWithoutLosingFractionOrTimezonePrecision(
        string enumValue, string input, string expected)
    {
        var definitions = EnumShape("timestamp", enumValue);
        var first = RegistryMetadataValidator.Validate(RegistryJson.Parse("{\"t\":\"" + input + "\"}"), definitions);
        var second = RegistryMetadataValidator.Validate(first.Metadata, definitions);

        await Assert.That(first.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo(expected);
        await Assert.That(second.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo(expected);
        await Assert.That(second.Metadata.RootElement.GetRawText()).IsEqualTo(first.Metadata.RootElement.GetRawText());
    }

    [Test]
    [Arguments("2026-09-11T14:00:00.123456789+02:00", "2026-09-11T12:00:00.123456788Z")]
    [Arguments("2026-09-11T14:00:00.123456789+02:00", "2026-09-11T12:00:00.123456790Z")]
    [Arguments("2026-09-11T14:00:00.123456789012345678901+02:00", "2026-09-11T12:00:00.123456789012345678902Z")]
    [Arguments("2026-09-11T14:00:00.0000000001+02:00", "2026-09-11T12:00:00Z")]
    [Arguments("2026-09-11T14:00:00+02:00", "2026-09-11T12:00:01Z")]
    [Arguments("2026-09-11T14:00:00+02:00", "2026-09-11T14:00:00+01:00")]
    [Arguments("2016-12-31T23:59:60Z", "2017-01-01T00:00:00Z")]
    [Arguments("0001-01-01T00:00:00Z", "0001-01-01T00:00:00+00:01")]
    [Arguments("9999-12-31T23:59:59Z", "9999-12-31T23:59:59-00:01")]
    [Arguments("2026-09-11T14:00:00+02:00", "not-a-timestamp")]
    public async Task TimestampEnumsRejectDifferentInstantsAndInvalidBoundariesWithoutRounding(string enumValue, string input)
    {
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"t\":\"" + input + "\"}"), EnumShape("timestamp", enumValue)));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/t");
    }

    [Test]
    [Arguments("ready", "READY")]
    [Arguments("2026-09-11T14:00:00+02:00", "2026-09-11T12:00:00Z")]
    [Arguments("2026-09-11T12:00:00Z", "2026-09-11t12:00:00z")]
    public async Task OrdinaryStringEnumsDoNotAdoptTimestampOrCaseNormalization(string enumValue, string input)
    {
        var definitions = EnumShape("string", enumValue);
        var accepted = RegistryMetadataValidator.Validate(RegistryJson.Parse("{\"t\":\"" + enumValue + "\"}"), definitions);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"t\":\"" + input + "\"}"), definitions));

        await Assert.That(accepted.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo(enumValue);
        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/t");
    }

    [Test]
    [Arguments("""{"type":"timestamp","enum":["2026-09-11T14:00:00+02:00"],"strict":false}""")]
    [Arguments("""{"type":"timestamp","enum":[]}""")]
    public async Task AdvisoryAndEmptyTimestampEnumsStillEnforceTheTimestampType(string definition)
    {
        var definitions = Shape("{\"t\":" + definition + "}");
        var accepted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"t":"2026-09-12T17:45:00+05:45"}"""), definitions);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"t":"not-a-timestamp"}"""), definitions));

        await Assert.That(accepted.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-12T12:00:00Z");
        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/t");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"t":null}""")]
    public async Task TimestampDefaultsApplyOnlyDuringCompletionAndPreserveClientDeletionInput(string input)
    {
        var definitions = Shape("{\"t\":" + DefaultDefinition + "}");
        var submitted = RegistryMetadataValidator.Validate(RegistryJson.Parse(input), definitions,
            new() { Mode = RegistryMetadataMode.ClientInput });
        var completed = RegistryMetadataValidator.Validate(submitted.Metadata, definitions);
        var repeated = RegistryMetadataValidator.Validate(completed.Metadata, definitions);

        await Assert.That(submitted.Metadata.RootElement.GetRawText()).IsEqualTo(input);
        await Assert.That(completed.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00Z");
        await Assert.That(repeated.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00Z");
    }

    [Test]
    [Arguments("{}", false)]
    [Arguments("""{"nested":null}""", false)]
    [Arguments("""{"nested":{}}""", true)]
    [Arguments("""{"nested":{"t":null}}""", true)]
    public async Task TimestampDefaultsNeverCreateAnAbsentOptionalParent(string input, bool expectedParent)
    {
        var definitions = Shape("{\"nested\":{\"type\":\"object\",\"attributes\":{\"t\":" + DefaultDefinition + "}}}");
        var completed = RegistryMetadataValidator.Validate(RegistryJson.Parse(input), definitions);
        var repeated = RegistryMetadataValidator.Validate(completed.Metadata, definitions);

        await Assert.That(completed.Metadata.RootElement.TryGetProperty("nested", out var nested)).IsEqualTo(expectedParent);
        if (expectedParent)
        {
            await Assert.That(nested.GetProperty("t").GetString()).IsEqualTo("2026-09-11T12:00:00Z");
        }
        else
        {
            await Assert.That(completed.Metadata.RootElement.GetRawText()).IsEqualTo("{}");
        }

        await Assert.That(repeated.Metadata.RootElement.GetRawText()).IsEqualTo(completed.Metadata.RootElement.GetRawText());
    }

    [Test]
    public async Task ExplicitOutOfEnumTimestampDoesNotFallBackToItsDefault()
    {
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"t":"2026-09-11T12:00:01Z"}"""), Shape("{\"t\":" + DefaultDefinition + "}")));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/t");
    }

    [Test]
    public async Task TimestampEnumChecksAllDeclaredInstantsUsingNormalizedValues()
    {
        var definitions = Shape("""
            {"t":{"type":"timestamp","enum":["2026-09-11T14:00:00+02:00","2026-09-11T15:00:00+02:00"]}}
            """);
        var accepted = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"t":"2026-09-11T13:00:00Z"}"""), definitions);
        var repeated = RegistryMetadataValidator.Validate(accepted.Metadata, definitions);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"t":"2026-09-11T14:00:00Z"}"""), definitions));

        await Assert.That(accepted.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T13:00:00Z");
        await Assert.That(repeated.Metadata.RootElement.GetProperty("t").GetString()).IsEqualTo("2026-09-11T13:00:00Z");
        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/t");
    }

    [Test]
    [Arguments("array", """[{"when":"2026-09-11T12:00:00.123456789Z"}]""",
        """[{"when":"2026-09-11T12:00:00.123456788Z"}]""", "/t/0/when")]
    [Arguments("map", """{"item":{"when":"2026-09-11T12:00:00.123456789Z"}}""",
        """{"item":{"when":"2026-09-11T12:00:00.123456788Z"}}""", "/t/item/when")]
    public async Task TimestampCollectionObjectEnumsRetainPrecisionAndExactFailurePointers(
        string collectionType, string input, string invalid, string expectedPath)
    {
        var definitions = Shape("{\"t\":{\"type\":\"" + collectionType +
            "\",\"item\":{\"type\":\"object\",\"attributes\":{\"when\":{\"type\":\"timestamp\"," +
            "\"enum\":[\"2026-09-11T14:00:00.123456789+02:00\"]}}}}}");
        var accepted = RegistryMetadataValidator.Validate(RegistryJson.Parse("{\"t\":" + input + "}"), definitions);
        var repeated = RegistryMetadataValidator.Validate(accepted.Metadata, definitions);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"t\":" + invalid + "}"), definitions));

        await Assert.That(accepted.Metadata.RootElement.GetProperty("t").GetRawText()).IsEqualTo(input);
        await Assert.That(repeated.Metadata.RootElement.GetRawText()).IsEqualTo(accepted.Metadata.RootElement.GetRawText());
        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo(expectedPath);
    }

    private static IReadOnlyDictionary<string, RegistryAttributeDefinition> EnumShape(string type, string enumValue) =>
        Shape("{\"t\":{\"type\":\"" + type + "\",\"enum\":[\"" + enumValue + "\"]}}");

    private static IReadOnlyDictionary<string, RegistryAttributeDefinition> Shape(string attributes) =>
        RegistryModel.Compile(RegistryJson.Parse(
            "{\"attributes\":{\"body\":{\"type\":\"object\",\"attributes\":" + attributes + "}}}"))
            .Attributes["body"].Attributes;
}
