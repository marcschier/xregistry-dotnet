using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryGroupConstraintValidationTests
{
    [Test]
    [Arguments("""{"color":"red"}""", true)]
    [Arguments("""{"color":"blue"}""", false)]
    [Arguments("{}", true)]
    [Arguments("""{"color":null}""", true)]
    public async Task ConstraintOnlyEnumsCheckPresentValuesWithoutRequiringMissingOnes(string input, bool allowed)
    {
        var group = Group("""{"enum":["red"]}""");
        var metadata = RegistryJson.Parse(input);
        var context = RegistryJson.Parse("{}");
        if (allowed)
        {
            RegistryMetadataValidator.ValidateGroupConstraints(metadata, group, group.Resources["rs"], context.RootElement);
        }
        else
        {
            var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.ValidateGroupConstraints(
                metadata, group, group.Resources["rs"], context.RootElement));
            await Assert.That(diagnostic.Code).IsEqualTo("constraint_failure");
            await Assert.That(diagnostic.Path).IsEqualTo("/color");
        }
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(input);
    }

    [Test]
    [Arguments("""{"color":"red"}""", """{"expected":"red"}""", true)]
    [Arguments("""{"color":"blue"}""", """{"expected":"red"}""", false)]
    [Arguments("{}", """{"expected":"red"}""", false)]
    [Arguments("""{"color":"blue"}""", "{}", true)]
    [Arguments("""{"color":"blue"}""", """{"expected":null}""", true)]
    public async Task ConstraintOnlyEqualsUsesExplicitGroupMetadata(string input, string owner, bool allowed)
    {
        var group = Group("""{"equals":"expected"}""");
        var metadata = RegistryJson.Parse(input);
        var context = RegistryJson.Parse(owner);
        if (allowed)
        {
            RegistryMetadataValidator.ValidateGroupConstraints(metadata, group, group.Resources["rs"], context.RootElement);
        }
        else
        {
            var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.ValidateGroupConstraints(
                metadata, group, group.Resources["rs"], context.RootElement));
            await Assert.That(diagnostic.Code).IsEqualTo("constraint_failure");
            await Assert.That(diagnostic.Path).IsEqualTo("/color");
        }
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(input);
        await Assert.That(context.RootElement.GetRawText()).IsEqualTo(owner);
    }

    [Test]
    public async Task ConstraintOnlyValidationRetainsExactLargeNumericEquality()
    {
        var group = Group("""{"equals":"expected"}""", "integer");
        var context = RegistryJson.Parse("""{"expected":184467440737095516160001}""");
        RegistryMetadataValidator.ValidateGroupConstraints(RegistryJson.Parse("""{"color":184467440737095516160001}"""),
            group, group.Resources["rs"], context.RootElement);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.ValidateGroupConstraints(
            RegistryJson.Parse("""{"color":184467440737095516160002}"""), group, group.Resources["rs"], context.RootElement));
        await Assert.That(diagnostic.Code).IsEqualTo("constraint_failure");
        await Assert.That(diagnostic.Path).IsEqualTo("/color");
    }

    [Test]
    [Arguments("enum")]
    [Arguments("equals")]
    public async Task TimestampConstraintsCompareNormalizedInstantsWithoutLosingPrecision(string facet)
    {
        var group = Group(facet == "enum"
            ? """{"enum":["2026-09-12T14:00:00.123456789+02:00"]}"""
            : """{"equals":"expected"}""", "timestamp");
        var context = RegistryJson.Parse("""{"expected":"2026-09-12T14:00:00.123456789+02:00"}""");
        RegistryMetadataValidator.ValidateGroupConstraints(RegistryJson.Parse("""{"color":"2026-09-12T12:00:00.123456789Z"}"""),
            group, group.Resources["rs"], context.RootElement);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.ValidateGroupConstraints(
            RegistryJson.Parse("""{"color":"2026-09-12T12:00:00.123456788Z"}"""),
            group, group.Resources["rs"], context.RootElement));
        await Assert.That(diagnostic.Code).IsEqualTo("constraint_failure");
        await Assert.That(diagnostic.Path).IsEqualTo("/color");
    }

    [Test]
    public async Task OrdinaryStringConstraintsDoNotInferTimestampOrCaseInsensitiveEquality()
    {
        var group = Group("""{"enum":["2026-09-12T14:00:00+02:00","red"]}""");
        foreach (var text in new[] { "2026-09-12T12:00:00Z", "RED" })
        {
            var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.ValidateGroupConstraints(
                RegistryJson.Parse("{\"color\":\"" + text + "\"}"), group, group.Resources["rs"],
                RegistryJson.Parse("{}").RootElement));
            await Assert.That(diagnostic.Code).IsEqualTo("constraint_failure");
            await Assert.That(diagnostic.Path).IsEqualTo("/color");
        }
    }

    [Test]
    public async Task DefaultOnlyConstraintsAndUnrelatedMetadataAreNotNormalizedOrMutated()
    {
        var group = Group("""{"default":"red"}""");
        var metadata = RegistryJson.Parse("""{"unmodeled":{"value":null,"large":184467440737095516160001}}""");
        var original = metadata.RootElement.GetRawText();
        RegistryMetadataValidator.ValidateGroupConstraints(metadata, group, group.Resources["rs"],
            RegistryJson.Parse("{}").RootElement);
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo(original);
        await Assert.That(metadata.RootElement.TryGetProperty("color", out _)).IsFalse();
    }

    [Test]
    public async Task ConstraintOnlyInputLimitsAndCancellationAreExplicit()
    {
        var group = Group("""{"default":"red"}""");
        var metadata = RegistryJson.Parse("{}");
        RegistryMetadataValidator.ValidateGroupConstraints(metadata, group, group.Resources["rs"], metadata.RootElement,
            new() { MaxBytes = 2 });
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.ValidateGroupConstraints(
            metadata, group, group.Resources["rs"], metadata.RootElement, new() { MaxBytes = 1 }));
        await Assert.That(diagnostic.Code).IsEqualTo("byte_limit");
        await Assert.That(() => RegistryMetadataValidator.ValidateGroupConstraints(
            metadata, group, group.Resources["rs"], RegistryJson.Parse("[]").RootElement)).Throws<ArgumentException>();
        var unrelated = Group("""{"default":"red"}""");
        await Assert.That(() => RegistryMetadataValidator.ValidateGroupConstraints(
            metadata, group, unrelated.Resources["rs"], metadata.RootElement)).Throws<ArgumentException>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(() => RegistryMetadataValidator.ValidateGroupConstraints(
            metadata, group, group.Resources["rs"], metadata.RootElement, cancellationToken: canceled.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ConstraintOnlyValidationDoesNotApplyTheReferringGroupDefault()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","attributes":{"requiredcolor":"string"},
              "constraints":{"rs.color":{"default":"red","equals":"requiredcolor"}},
              "resources":{"rs":{"singular":"r","attributes":{"color":"string"}}}
            }}}
            """));
        var group = model.Groups["gs"];
        var metadata = RegistryJson.Parse("{}");
        var groupMetadata = RegistryJson.Parse("""{"requiredcolor":"red"}""");

        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.ValidateGroupConstraints(
            metadata, group, group.Resources["rs"], groupMetadata.RootElement));

        await Assert.That(diagnostic.Code).IsEqualTo("constraint_failure");
        await Assert.That(diagnostic.Path).IsEqualTo("/color");
        await Assert.That(metadata.RootElement.GetRawText()).IsEqualTo("{}");
        await Assert.That(groupMetadata.RootElement.GetRawText()).IsEqualTo("""{"requiredcolor":"red"}""");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConstraintComparisonsCannotExceedTheSharedWorkBudget(int maximumNodes)
    {
        var group = Group("""{"enum":["red","blue"]}""");
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.ValidateGroupConstraints(
            RegistryJson.Parse("""{"color":"blue"}"""), group, group.Resources["rs"],
            RegistryJson.Parse("{}").RootElement, new() { MaxNodes = maximumNodes }));

        await Assert.That(diagnostic.Code).IsEqualTo("node_limit");
    }

    [Test]
    public async Task ConstraintComparisonWorkBudgetAcceptsItsExactBoundary()
    {
        var group = Group("""{"enum":["red","blue"]}""");
        var metadata = RegistryJson.Parse("""{"color":"blue"}""");
        RegistryMetadataValidator.ValidateGroupConstraints(metadata, group, group.Resources["rs"],
            RegistryJson.Parse("{}").RootElement, new() { MaxNodes = 4 });
        await Assert.That(metadata.RootElement.GetProperty("color").GetString()).IsEqualTo("blue");
    }

    [Test]
    public async Task FullMetadataValidationUsesTheSameConstraintWorkBudget()
    {
        var group = Group("""{"enum":["red","blue"]}""");
        var resource = group.Resources["rs"];
        var definitions = new Dictionary<string, RegistryAttributeDefinition>(StringComparer.Ordinal)
        {
            ["color"] = resource.Attributes["color"]
        };
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"color":"blue"}"""), definitions,
            new()
            {
                Group = group,
                Resource = resource,
                GroupMetadata = RegistryJson.Parse("{}").RootElement,
                Limits = new RegistryJsonLimits { MaxNodes = 3 }
            }));

        await Assert.That(diagnostic.Code).IsEqualTo("node_limit");
    }

    private static RegistryGroupDefinition Group(string constraint, string type = "string") =>
        RegistryModel.Compile(RegistryJson.Parse($$"""
            {
              "groups": {
                "gs": {
                  "singular": "g",
                  "attributes": { "expected": { "type": "{{type}}" } },
                  "constraints": { "rs.color": {{constraint}} },
                  "resources": {
                    "rs": { "singular": "r", "attributes": { "color": { "type": "{{type}}" } } }
                  }
                }
              }
            }
            """)).Groups["gs"];
}
