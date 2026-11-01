// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class Wsdl20OperationReferenceTests
{
    [Test]
    [Arguments("in-only", "<input element=\"#none\"/>")]
    [Arguments("robust-in-only", "<input element=\"#any\"/>")]
    [Arguments("out-only", "<output element=\"#none\"/>")]
    [Arguments("robust-out-only", "<output element=\"#any\"/>")]
    [Arguments("in-out", "<output element=\"#any\"/><input element=\"#none\"/>")]
    [Arguments("in-opt-out", "<input element=\"#none\"/><output element=\"#any\"/>")]
    [Arguments("out-in", "<output element=\"#none\"/><input element=\"#any\"/>")]
    [Arguments("out-opt-in", "<input element=\"#any\"/><output element=\"#none\"/>")]
    public async Task Wsdl20PreservesNativePatternIdentityAndOwnedXml(string pattern, string messages)
    {
        var bytes = Document("<interface name=\"Port\"><operation name=\"Read\" pattern=\"http://www.w3.org/ns/wsdl/" +
            pattern + "\">" + messages + "</operation></interface>");
        var origin = new Uri("https://example.test/retained.wsdl");
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "APPLICATION/XML; charset=utf-8",
            bytes, Reference("Port"), origin);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var selected = result.Wsdl20Operation ?? throw new InvalidOperationException("Expected native WSDL 2.0 selection.");
        await Assert.That(selected.OperationName).IsEqualTo("{urn:api}Read");
        await Assert.That(selected.DeclaringInterfaceName).IsEqualTo("{urn:api}Port");
        await Assert.That(selected.MessageExchangePattern).IsEqualTo("http://www.w3.org/ns/wsdl/" + pattern);
        await Assert.That(result.DocumentUri).IsEqualTo(origin);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.GrpcOperation).IsNull();
        await Assert.That(result.WsdlOperation).IsNull();
        await Assert.That(result.DocumentPath).IsNull();
        Array.Fill(bytes, (byte)'!');
        var retained = XElement.Parse(selected.OperationXml);
        await Assert.That(retained.Name).IsEqualTo(XName.Get("operation", "http://www.w3.org/ns/wsdl"));
        await Assert.That(retained.Attribute("pattern")!.Value).IsEqualTo(selected.MessageExchangePattern);
        await Assert.That(retained.Elements().Count()).IsEqualTo(pattern.EndsWith("only", StringComparison.Ordinal) ? 1 : 2);
    }

    [Test]
    public async Task Wsdl20DiamondInheritanceCollapsesTheSameComponentAndRetainsDeclaringDefaults()
    {
        var result = await Resolve("""
            <interface name="Base" styleDefault="urn:style urn:style">
              <operation name="Read"><input element="#none"/><output element="#any"/></operation>
            </interface>
            <interface name="Left" extends="t:Base"/>
            <interface name="Right" extends="t:Base"/>
            <interface name="Port" extends="t:Left t:Right" styleDefault="urn:other"/>
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Wsdl20Operation!.DeclaringInterfaceName).IsEqualTo("{urn:api}Base");
        await Assert.That(result.Wsdl20Operation.OperationName).IsEqualTo("{urn:api}Read");
        await Assert.That(result.Wsdl20Operation.MessageExchangePattern).IsEqualTo("http://www.w3.org/ns/wsdl/in-out");
        await Assert.That(result.Wsdl20Operation.Styles.SequenceEqual(["urn:style"])).IsTrue();
        var xml = XElement.Parse(result.Wsdl20Operation.OperationXml);
        await Assert.That(xml.Attribute("pattern")).IsNull();
        await Assert.That(xml.Attribute("style")).IsNull();
        await Assert.That(xml.GetNamespaceOfPrefix("t")!.NamespaceName).IsEqualTo("urn:api");
        var overrideStyles = await Resolve("""
            <interface name="Port" styleDefault="urn:style">
              <operation name="Read" style=""><input element="#none"/><output element="#any"/></operation>
            </interface>
            """);
        await Assert.That(overrideStyles.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(overrideStyles.Wsdl20Operation!.Styles.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("<interface name=\"Port\" extends=\"t:Missing\"/>", "wsdl.interface")]
    [Arguments("<interface name=\"Port\" extends=\"t:Port\"/>", "wsdl.inheritance_cycle")]
    [Arguments("<interface name=\"Port\" extends=\"t:Other\"/><interface name=\"Other\" extends=\"t:Port\"/>", "wsdl.inheritance_cycle")]
    [Arguments("<interface name=\"Base\"/><interface name=\"Port\" xmlns:p=\"urn:api\" extends=\"t:Base p:Base\"/>", "wsdl.duplicate_parent")]
    [Arguments("<interface name=\"Port\"/><interface name=\"Port\"/>", "wsdl.duplicate_interface")]
    [Arguments("<interface name=\"1Port\"/>", "wsdl.xml")]
    public async Task Wsdl20RejectsInvalidInterfaceIdentityAndInheritance(string interfaces, string code)
    {
        var result = await Resolve(interfaces);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    public async Task Wsdl20ConflictingInheritedComponentsCannotBeCollapsedByXmlSimilarity()
    {
        var operation = "<operation name=\"Read\" pattern=\"http://www.w3.org/ns/wsdl/in-only\"><input element=\"#none\"/></operation>";
        var result = await Resolve("<interface name=\"Left\">" + operation + "</interface>" +
            "<interface name=\"Right\">" + operation + "</interface><interface name=\"Port\" extends=\"t:Left t:Right\"/>");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl.operation_conflict");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments("<operation name=\"Read\"><input element=\"#none\"/></operation>", "wsdl.flow")]
    [Arguments("<operation name=\"Read\"><input element=\"#none\"/><input element=\"#none\"/></operation>", "wsdl.message_label")]
    [Arguments("<operation name=\"Read\"><input element=\"#none\" messageLabel=\"Out\"/><output element=\"#none\"/></operation>", "wsdl.message_label")]
    [Arguments("<operation name=\"Read\" pattern=\"relative\"><input element=\"#none\"/></operation>", "wsdl.pattern")]
    [Arguments("<operation name=\"Read\" style=\"relative\"><input element=\"#none\"/><output element=\"#none\"/></operation>", "wsdl.style")]
    [Arguments("<operation name=\"Read\"><input element=\"#none\"/><output element=\"#none\"/></operation><operation name=\"Read\"><input element=\"#none\"/><output element=\"#none\"/></operation>", "wsdl.duplicate_operation")]
    public async Task Wsdl20RejectsInvalidNativeOperationComponentsBeforeSelection(string operations, string code)
    {
        var result = await Resolve("<interface name=\"Port\">" + operations + "</interface>");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments("<import namespace=\"urn:other\" location=\"other.wsdl\"/>", "wsdl20.import")]
    [Arguments("<include location=\"other.wsdl\"/>", "wsdl20.import")]
    [Arguments("<interface name=\"Port\"><operation name=\"Read\" pattern=\"urn:custom\"><input element=\"#none\"/></operation></interface>", "wsdl.pattern")]
    [Arguments("<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>", "wsdl20.element")]
    [Arguments("<interface name=\"Port\"><operation name=\"Read\"><input/><output element=\"#none\"/></operation></interface>", "wsdl20.element")]
    [Arguments("<interface name=\"Port\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#none\"/><outfault ref=\"t:F\"/></operation></interface>", "wsdl20.fault")]
    [Arguments("<x:policy xmlns:x=\"urn:policy\" xmlns:w=\"http://www.w3.org/ns/wsdl\" w:required=\"true\"/>", "wsdl.extension")]
    public async Task Wsdl20UnimplementedPoliciesCannotProduceSuccessfulSelections(string document, string code)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "text/xml", Document(document), Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task Wsdl20ExpandedNamesAreExactAndCannotUseWsdl11OverloadSelectors()
    {
        var bytes = Document("<interface name=\"Port\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>");
        foreach (var suffix in new[] { "%7Burn:api%7Dread", "%7Burn:other%7DRead" })
        {
            var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes,
                ApiOperationReference.Parse("/apigroups/g/apis/a#interfaces/%7Burn:api%7DPort/operations/" + suffix));
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        }
        var invalid = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes,
            ApiOperationReference.Parse("/apigroups/g/apis/a#interfaces/%7Burn:api%7DPort/operations/Read"));
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("wsdl.expanded_name");
        var overload = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes,
            ApiOperationReference.Parse("/apigroups/g/apis/a#interfaces/%7Burn:api%7DPort/operations/%7Burn:api%7DRead/input/In/output/Out"));
        await Assert.That(overload.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        var media = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/json", bytes, Reference("Port"));
        await Assert.That(media.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(media.Diagnostic!.Code).IsEqualTo("contenttype.unsupported");
    }

    [Test]
    public async Task Wsdl20SecureXmlAndInheritanceBudgetsFailWithoutPartialResults()
    {
        foreach (var xml in new[] { "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///never-read'>]><x>&e;</x>",
            "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>",
            "<description xmlns=\"http://www.w3.org/ns/wsdl\" targetNamespace=\"relative\"/>" })
        {
            var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
                Encoding.UTF8.GetBytes(xml), Reference("Port"));
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        }
        foreach (var count in new[] { 64, 65 })
        {
            var chain = "<interface name=\"Port\" extends=\"t:P0\"/>" + string.Concat(Enumerable.Range(0, count - 1)
                .Select(index => "<interface name=\"P" + index + "\" extends=\"t:P" + (index + 1) + "\"/>")) +
                "<interface name=\"P" + (count - 1) +
                "\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>";
            var limited = await Resolve(chain);
            await Assert.That(limited.Status).IsEqualTo(count == 64 ? ApiOperationResolutionStatus.Selected :
                ApiOperationResolutionStatus.Indeterminate);
            if (count == 65)
            {
                await Assert.That(limited.Diagnostic!.Code).IsEqualTo("limit.depth");
                await Assert.That(limited.Wsdl20Operation).IsNull();
            }
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document(""), Reference("Port"), cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
    }

    private static ValueTask<ApiOperationResolutionResult> Resolve(string interfaces) =>
        ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(interfaces), Reference("Port"));

    private static ApiOperationReference Reference(string name) => ApiOperationReference.Parse(
        "/apigroups/g/apis/a#interfaces/%7Burn:api%7D" + name + "/operations/%7Burn:api%7DRead");

    private static byte[] Document(string content) => Encoding.UTF8.GetBytes(
        "<description xmlns=\"http://www.w3.org/ns/wsdl\" xmlns:t=\"urn:api\" targetNamespace=\"urn:api\">" +
        content + "</description>");
}
