using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class DocumentValidationPolicyTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidFormatsAreRejectedEvenWhenStrictValidationIsDisabled(bool strict)
    {
        var result = await new DocumentValidationPolicy().EvaluateAsync(new()
        {
            ValidateFormat = true,
            Format = "JsonSchema/draft-07",
            StrictValidation = strict
        }, """{"type":"invented"}"""u8.ToArray(), []);
        await Assert.That(result.Accepted).IsFalse();
        await Assert.That(result.FormatValidated).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsNotEqualTo("");
    }

    [Test]
    public async Task UnknownFormatIsExplicitlyUncheckedOrRejectedNotReportedAsValidated()
    {
        var policy = new DocumentValidationPolicyOptions { ValidateFormat = true, Format = "Unknown/1" };
        var validator = new DocumentValidationPolicy();
        var nonStrict = await validator.EvaluateAsync(policy, "{}"u8.ToArray(), []);
        var strict = await validator.EvaluateAsync(policy with { StrictValidation = true }, "{}"u8.ToArray(), []);
        await Assert.That(nonStrict.Accepted).IsTrue();
        await Assert.That(nonStrict.FormatValidated).IsFalse();
        await Assert.That(nonStrict.Diagnostics[0].Code).IsEqualTo("format.unsupported");
        await Assert.That(strict.Accepted).IsFalse();
        await Assert.That(strict.FormatValidated).IsNull();
    }

    [Test]
    public async Task IncompatibleAvroAlwaysRejectsAndDoesNotSetFalseCompatibleMetadata()
    {
        var result = await new DocumentValidationPolicy().EvaluateAsync(new()
        {
            ValidateFormat = true,
            ValidateCompatibility = true,
            Format = "Avro/1.11.0",
            Compatibility = "backward"
        }, "\"int\""u8.ToArray(), ["\"long\""u8.ToArray()]);
        await Assert.That(result.Accepted).IsFalse();
        await Assert.That(result.FormatValidated).IsTrue();
        await Assert.That(result.CompatibilityValidated).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("compatibility.avro");
    }

    [Test]
    public async Task MissingFormatAndMissingModeDoNotInventValidationResults()
    {
        var validator = new DocumentValidationPolicy();
        var missingFormat = await validator.EvaluateAsync(new()
        {
            ValidateFormat = true,
            ValidateCompatibility = true,
            StrictValidation = true
        }, "not json"u8.ToArray(), []);
        await Assert.That(missingFormat.Accepted).IsTrue();
        await Assert.That(missingFormat.FormatValidated).IsNull();
        await Assert.That(missingFormat.CompatibilityValidated).IsNull();
        var missingMode = await validator.EvaluateAsync(new()
        {
            ValidateFormat = true,
            ValidateCompatibility = true,
            Format = "Avro/1.11.0"
        }, "\"int\""u8.ToArray(), []);
        await Assert.That(missingMode.FormatValidated).IsTrue();
        await Assert.That(missingMode.CompatibilityValidated).IsNull();
    }

    [Test]
    public async Task UnsupportedEvolutionIsNotSilentlyDeclaredCompatible()
    {
        var policy = new DocumentValidationPolicyOptions
        {
            ValidateFormat = true,
            ValidateCompatibility = true,
            Format = "JsonSchema/draft-07",
            Compatibility = "full"
        };
        var validator = new DocumentValidationPolicy();
        var result = await validator.EvaluateAsync(policy,
            """{"type":"string"}"""u8.ToArray(), ["""{"type":"number"}"""u8.ToArray()]);
        await Assert.That(result.Accepted).IsTrue();
        await Assert.That(result.FormatValidated).IsTrue();
        await Assert.That(result.CompatibilityValidated).IsFalse();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("compatibility.policy");
        var strict = await validator.EvaluateAsync(policy with { StrictValidation = true },
            """{"type":"string"}"""u8.ToArray(), ["""{"type":"number"}"""u8.ToArray()]);
        await Assert.That(strict.Accepted).IsFalse();
    }
}
