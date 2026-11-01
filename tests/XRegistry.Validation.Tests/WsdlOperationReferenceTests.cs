// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class WsdlOperationReferenceTests
{
    [Test]
    [Arguments("<input message=\"t:Message\"/>", "Read", null, WsdlMessageExchangePattern.OneWay)]
    [Arguments("<output message=\"t:Message\"/>", null, "Read", WsdlMessageExchangePattern.Notification)]
    [Arguments("<input message=\"t:Message\"/><output message=\"t:Message\"/>",
        "ReadRequest", "ReadResponse", WsdlMessageExchangePattern.RequestResponse)]
    [Arguments("<output message=\"t:Message\"/><input message=\"t:Message\"/>",
        "ReadResponse", "ReadSolicit", WsdlMessageExchangePattern.SolicitResponse)]
    public async Task Wsdl11PreservesEveryNativeExchangePatternAndEffectiveDefaultName(
        string flow, string? input, string? output, WsdlMessageExchangePattern pattern)
    {
        var bytes = Document("<operation name=\"Read\">" + flow + "</operation>");
        var origin = new Uri("https://registry.example/apis/service.wsdl");
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", bytes,
            Reference("Read"), origin);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var selected = result.WsdlOperation ?? throw new InvalidOperationException("Expected native WSDL selection.");
        await Assert.That(selected.InterfaceName).IsEqualTo("{urn:users}Port");
        await Assert.That(selected.OperationName).IsEqualTo("Read");
        await Assert.That(selected.InputName).IsEqualTo(input);
        await Assert.That(selected.OutputName).IsEqualTo(output);
        await Assert.That(selected.InputMessage).IsEqualTo(input is null ? null : "{urn:users}Message");
        await Assert.That(selected.OutputMessage).IsEqualTo(output is null ? null : "{urn:users}Message");
        await Assert.That(selected.MessageExchangePattern).IsEqualTo(pattern);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Undefined);
        await Assert.That(result.GrpcOperation).IsNull();
        await Assert.That(result.DocumentPath).IsNull();
        await Assert.That(result.DocumentUri).IsEqualTo(origin);
        Array.Fill(bytes, (byte)'!');
        var xml = XElement.Parse(selected.OperationXml);
        await Assert.That(xml.Name.LocalName).IsEqualTo("operation");
        await Assert.That(xml.GetNamespaceOfPrefix("t")!.NamespaceName).IsEqualTo("urn:users");
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsCallerProvidedWsdlImports).IsTrue();
    }

    [Test]
    public async Task Wsdl11RejectsNonExpandedInterfaceTokensAndUnsupportedSelectorOrMediaForms()
    {
        var bytes = Document("<operation name=\"Read\"><input message=\"t:Message\"/></operation>");
        var invalid = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", bytes,
            ApiOperationReference.Parse("/apigroups/g/apis/a#interfaces/t:Port/operations/Read"));
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("wsdl.interface_name");
        var selector = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", bytes,
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Read"));
        await Assert.That(selector.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        var media = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/json", bytes, Reference("Read"));
        await Assert.That(media.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
    }

    [Test]
    public async Task Wsdl11OverloadsRequireEffectiveInputAndOutputNamesIncludingAbsentMarkers()
    {
        var bytes = Document("""
            <operation name="Read"><input name="1:request" message="t:Message"/><output name="2:reply" message="t:Message"/></operation>
            <operation name="Read"><input name="one" message="t:Message"/></operation>
            """);
        var ambiguous = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "text/xml", bytes, Reference("Read"));
        await Assert.That(ambiguous.Status).IsEqualTo(ApiOperationResolutionStatus.Ambiguous);
        foreach (var entry in new[] { (Input: "1:request", Output: "2:reply"), (Input: "one", Output: "!") })
        {
            var selected = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "text/xml", bytes,
                Reference("Read/input/" + entry.Input + "/output/" + entry.Output));
            await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
            await Assert.That(selected.WsdlOperation!.InputName).IsEqualTo(entry.Input);
            await Assert.That(selected.WsdlOperation.OutputName).IsEqualTo(entry.Output == "!" ? null : entry.Output);
        }
        var missing = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", bytes,
            Reference("Read/input/missing/output/!"));
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
    }

    [Test]
    [Arguments("w")]
    [Arguments("wsdl")]
    public async Task Wsdl11ExpandedNamesDoNotDependOnXmlPrefixesAndFaultsRemainNative(string prefix)
    {
        var xml = """
            <w:definitions xmlns:w="http://schemas.xmlsoap.org/wsdl/" xmlns:t="urn:users" targetNamespace="urn:users">
              <w:message name="Message"/><w:portType name="Port">
                <w:operation name="Read" parameterOrder="value">
                  <w:input message="t:Message"/><w:output message="t:Message"/>
                  <w:fault name="Failed" message="t:Message"/>
                </w:operation>
              </w:portType>
            </w:definitions>
            """.Replace("w:", prefix + ":", StringComparison.Ordinal)
            .Replace("xmlns:w=", "xmlns:" + prefix + "=", StringComparison.Ordinal);
        var selected = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "APPLICATION/XML; charset=utf-8",
            Encoding.UTF8.GetBytes(xml), Reference("Read"));
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.WsdlOperation!.FaultMessages.SequenceEqual(["{urn:users}Message"])).IsTrue();
        await Assert.That(XElement.Parse(selected.WsdlOperation.OperationXml).Attribute("parameterOrder")!.Value)
            .IsEqualTo("value");
        var wrongCase = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Encoding.UTF8.GetBytes(xml), Reference("read"));
        await Assert.That(wrongCase.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        var wrongNamespace = ApiOperationReference.Parse(
            "/apigroups/g/apis/a#interfaces/%7Burn:other%7DPort/operations/Read");
        var missing = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Encoding.UTF8.GetBytes(xml), wrongNamespace);
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
    }

    [Test]
    [Arguments("<operation name=\"Read\"/>", "wsdl.flow")]
    [Arguments("<operation name=\"Read\"><input message=\"t:Missing\"/></operation>", "wsdl.message")]
    [Arguments("<operation name=\"Read\"><input message=\"unknown:Message\"/></operation>", "wsdl.qname")]
    [Arguments("<operation name=\"Read\"><input message=\"t:Message\"/><input message=\"t:Message\"/></operation>", "wsdl.flow")]
    [Arguments("<operation name=\"Read\"><output message=\"t:Message\"/><fault name=\"F\" message=\"t:Message\"/></operation>", "wsdl.flow")]
    [Arguments("<operation name=\"Read\"><input message=\"t:Message\"/></operation><operation name=\"Read\"><input message=\"t:Message\"/></operation>", "wsdl.duplicate_operation")]
    [Arguments("<operation name=\"Read\"><input name=\"same\" message=\"t:Message\"/><output name=\"same\" message=\"t:Message\"/></operation>", "wsdl.message_name")]
    [Arguments("<operation name=\"Read\"><input name=\"same\" message=\"t:Message\"/></operation><operation name=\"Write\"><output name=\"same\" message=\"t:Message\"/></operation>", "wsdl.message_name")]
    [Arguments("<operation name=\"Read\"><input message=\"t:Message\"/><output message=\"t:Message\"/></operation><operation name=\"Write\"><input name=\"ReadRequest\" message=\"t:Message\"/></operation>", "wsdl.message_name")]
    public async Task Wsdl11RejectsInvalidOperationComponentsBeforeSelection(string operations, string code)
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Document(operations), Reference("Read"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.WsdlOperation).IsNull();
    }

    [Test]
    public async Task Wsdl11RejectsDtdsAndWrongRootsAndReportsUnresolvedImports()
    {
        foreach (var xml in new[]
        {
            "<!DOCTYPE definitions [<!ENTITY x SYSTEM 'file:///must-not-read'>]><definitions>&x;</definitions>",
            "<description xmlns='http://www.w3.org/ns/wsdl'/>",
            "<definitions"
        })
        {
            var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
                Encoding.UTF8.GetBytes(xml), Reference("Read"));
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        }
        var calls = 0;
        var imported = Encoding.UTF8.GetBytes("""
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"><import namespace="urn:other" location="other.wsdl"/></definitions>
            """);
        var unsupported = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", imported,
            Reference("Read"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });
        await Assert.That(unsupported.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(unsupported.Diagnostic!.Code).IsEqualTo("reference.unresolved");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("true", ApiOperationResolutionStatus.Unsupported)]
    [Arguments("1", ApiOperationResolutionStatus.Unsupported)]
    [Arguments(" true ", ApiOperationResolutionStatus.Unsupported)]
    [Arguments("false", ApiOperationResolutionStatus.Selected)]
    [Arguments("0", ApiOperationResolutionStatus.Selected)]
    [Arguments("yes", ApiOperationResolutionStatus.Invalid)]
    public async Task Wsdl11RequiredExtensionsCannotBecomeSuccessfulUncheckedSelections(
        string required, ApiOperationResolutionStatus status)
    {
        var bytes = Document("<operation name=\"Read\"><input message=\"t:Message\"/>" +
            "<x:policy xmlns:x=\"urn:policy\" xmlns:w=\"http://schemas.xmlsoap.org/wsdl/\" w:required=\"" +
            required + "\"/></operation>");
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", bytes, Reference("Read"));
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == ApiOperationResolutionStatus.Unsupported)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl.extension");
            await Assert.That(result.WsdlOperation).IsNull();
        }
        if (status == ApiOperationResolutionStatus.Invalid)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl.extension_required");
            await Assert.That(result.WsdlOperation).IsNull();
        }
    }

    [Test]
    public async Task Wsdl11RepeatedNamespaceExpansionAndRetentionConsumeCumulativeWork()
    {
        var xml = Encoding.UTF8.GetString(Document(string.Concat(Enumerable.Range(0, 200)
            .Select(index => "<operation name=\"Read" + index +
                "\"><input message=\"t:Message\"/></operation>"))));
        xml = xml.Replace("urn:users", "urn:" + new string('x', 50_000), StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Encoding.UTF8.GetBytes(xml), Reference("Read0"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.work");
        await Assert.That(result.WsdlOperation).IsNull();
    }

    [Test]
    public async Task Wsdl11SelectionHonorsByteAndDepthBudgetsAndCancellation()
    {
        var baseline = Encoding.UTF8.GetString(Document("<operation name=\"Read\"><input message=\"t:Message\"/></operation>"));
        var position = baseline.IndexOf("</definitions>", StringComparison.Ordinal);
        var exact = baseline.Insert(position, "<!--" + new string('x',
            ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes - Encoding.UTF8.GetByteCount(baseline) - 7) + "-->");
        var selected = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Encoding.UTF8.GetBytes(exact), Reference("Read"));
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var exceeded = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Encoding.UTF8.GetBytes(exact + " "), Reference("Read"));
        await Assert.That(exceeded.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(exceeded.Diagnostic!.Code).IsEqualTo("limit.bytes");
        var deep = baseline.Insert(position, string.Concat(Enumerable.Repeat("<extra>", 70)) +
            string.Concat(Enumerable.Repeat("</extra>", 70)));
        var depth = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Encoding.UTF8.GetBytes(deep), Reference("Read"));
        await Assert.That(depth.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(depth.Diagnostic!.Code).IsEqualTo("limit.depth");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Encoding.UTF8.GetBytes(baseline), Reference("Read"), cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    private static ApiOperationReference Reference(string selector) => ApiOperationReference.Parse(
        "/apigroups/g/apis/a#interfaces/%7Burn:users%7DPort/operations/" + selector);

    private static byte[] Document(string operations) => Encoding.UTF8.GetBytes(
        "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\" xmlns:t=\"urn:users\" targetNamespace=\"urn:users\">" +
        "<message name=\"Message\"/><portType name=\"Port\">" + operations + "</portType></definitions>");
}
