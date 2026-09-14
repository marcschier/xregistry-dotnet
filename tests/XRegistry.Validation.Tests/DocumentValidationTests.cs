using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class DocumentValidationTests
{
    [Test]
    [Arguments("Unknown/1")]
    [Arguments("JsonSchema/draft-99")]
    [Arguments("JsonSchema/draft-2020-12")]
    [Arguments("Avro/99")]
    [Arguments("Protobuf/2023")]
    [Arguments("JsonStructure/rfc-0000")]
    [Arguments("XSD/1.1")]
    public async Task UnknownOrUnimplementedFormatNeverValidates(string format)
    {
        var validator = new BuiltInDocumentValidator();
        var result = await validator.ValidateAsync(format, Encoding.UTF8.GetBytes("{}"));

        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("format.unsupported");
        await Assert.That(result.Diagnostics[0].Path).IsEqualTo("$");
    }
}
