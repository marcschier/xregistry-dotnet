using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryPathTests
{
    [Test]
    public async Task PreservesEscapedPathAndCase()
    {
        var path = RegistryPath.Parse("/groups/G%3aid/resources/CON%40host/versions/V1$details");
        await Assert.That(path.Kind).IsEqualTo(RegistryPathKind.Version);
        await Assert.That(path.GroupId!.Value).IsEqualTo("G:id");
        await Assert.That(path.ResourceId!.Value).IsEqualTo("CON@host");
        await Assert.That(path.VersionId!.Value).IsEqualTo("V1");
        await Assert.That(path.IsDetails).IsTrue();
        await Assert.That(path.EscapedPath)
            .IsEqualTo("/groups/G%3aid/resources/CON%40host/versions/V1$details");
        await Assert.That(path.ToXid()).IsEqualTo("/groups/G%3aid/resources/CON%40host/versions/V1");
    }

    [Test]
    [Arguments("A")]
    [Arguments("0")]
    [Arguments("_")]
    [Arguments("_a-Z.9~:@")]
    [Arguments("CON")]
    [Arguments("a:stream")]
    [Arguments("a.")]
    public async Task AcceptsExactRegistryIdentifierGrammar(string text)
    {
        await Assert.That(RegistryId.Parse(text).Value).IsEqualTo(text);
    }

    [Test]
    [Arguments("")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments(".abc")]
    [Arguments("-abc")]
    [Arguments("~abc")]
    [Arguments(":abc")]
    [Arguments("@abc")]
    [Arguments("a/b")]
    [Arguments("a\\b")]
    [Arguments("a b")]
    [Arguments("a$b")]
    [Arguments("a%3Ab")]
    [Arguments("a?b")]
    [Arguments("a#b")]
    [Arguments("\u00e9")]
    public async Task RejectsInvalidIdentifierGrammar(string text)
    {
        await Assert.That(TestErrors.Capture(() => RegistryId.Parse(text)).Code).IsEqualTo("malformed_id");
    }

    [Test]
    public async Task IdentifierLengthAndUniquenessAreIndependentOfLookupCase()
    {
        await Assert.That(RegistryId.Parse(new string('a', 128)).Value.Length).IsEqualTo(128);
        await Assert.That(TestErrors.Capture(() => RegistryId.Parse(new string('a', 129))).Code).IsEqualTo("malformed_id");
        var first = RegistryId.Parse("Case");
        var second = RegistryId.Parse("case");
        await Assert.That(first.Equals(second)).IsFalse();
        await Assert.That(first.ConflictsWith(second)).IsTrue();
    }

    [Test]
    [Arguments("/groups/G/resources/R%24details", "malformed_id")]
    [Arguments("/groups/G/resources/R%24details$details", "malformed_id")]
    [Arguments("/groups/G$details", "bad_details")]
    [Arguments("/groups$details", "bad_details")]
    [Arguments("/groups/G/resources/R/meta$details", "bad_details")]
    public async Task SeparatesDetailsSyntaxFromEncodedDollar(string path, string code)
    {
        await Assert.That(TestErrors.Capture(() => RegistryPath.Parse(path)).Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("/groups/%2e%2e")]
    [Arguments("/groups/a%2fb")]
    [Arguments("/groups/a%5cb")]
    [Arguments("/groups/a%252fb")]
    [Arguments("/groups/%252e%252e")]
    [Arguments("/groups/a%00")]
    [Arguments("/groups/%c0%af")]
    [Arguments("/groups/a%")]
    [Arguments("/groups/a%gg")]
    [Arguments("/groups//resources/r")]
    [Arguments("/groups/g/")]
    [Arguments("//groups/g")]
    [Arguments("https://host/groups/g")]
    [Arguments("/groups/g?x=y")]
    [Arguments("/groups/g#f")]
    [Arguments("/groups/g/resources/r/other")]
    public async Task RejectsTraversalAndDoubleDecoding(string path)
    {
        await Assert.That(TestErrors.Capture(() => RegistryPath.Parse(path)).Message.Length).IsGreaterThan(0);
    }

    [Test]
    [Arguments("/", RegistryPathKind.Registry)]
    [Arguments("/model", RegistryPathKind.Model)]
    [Arguments("/modelsource", RegistryPathKind.ModelSource)]
    [Arguments("/capabilities", RegistryPathKind.Capabilities)]
    [Arguments("/capabilitiesoffered", RegistryPathKind.CapabilitiesOffered)]
    [Arguments("/export", RegistryPathKind.Export)]
    [Arguments("/.xregistry", RegistryPathKind.Discovery)]
    [Arguments("/groups", RegistryPathKind.GroupCollection)]
    [Arguments("/groups/G", RegistryPathKind.Group)]
    [Arguments("/groups/G/resources", RegistryPathKind.ResourceCollection)]
    [Arguments("/groups/G/resources/R", RegistryPathKind.Resource)]
    [Arguments("/groups/G/resources/R/meta", RegistryPathKind.Meta)]
    [Arguments("/groups/G/resources/R/versions", RegistryPathKind.VersionCollection)]
    [Arguments("/groups/G/resources/R/versions/1", RegistryPathKind.Version)]
    public async Task RecognizesOnlyDefinedProtocolShapes(string path, RegistryPathKind expected)
    {
        await Assert.That(RegistryPath.Parse(path).Kind).IsEqualTo(expected);
    }

    [Test]
    public async Task ConstructsEscapedPathsWithoutWindowsFilenameRestrictions()
    {
        var path = RegistryPath.ForVersion("groups", RegistryId.Parse("CON"), "resources",
            RegistryId.Parse("a:stream@host"), RegistryId.Parse("V1."), details: true);
        await Assert.That(path.EscapedPath).IsEqualTo("/groups/CON/resources/a%3Astream%40host/versions/V1.$details");
        await Assert.That(TestErrors.Capture(() => RegistryPath.Parse("/groups").ToXid()).Code)
            .IsEqualTo("malformed_xid");
    }
}
