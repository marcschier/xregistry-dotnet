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
        await Assert.That(selected.Faults.Count).IsEqualTo(0);
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
    [Arguments("<interface name=\"Port\"><fault name=\"F\" element=\"t:Payload\"/></interface>", "wsdl20.element")]
    [Arguments("<interface name=\"Port\"><fault name=\"F\" element=\"#other\"/></interface>", "wsdl20.element")]
    [Arguments("<interface name=\"Port\"><fault name=\"F\"/></interface>", "wsdl20.element")]
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
    [Arguments("in-out", "outfault", "Out", "<output element=\"#none\"/><input element=\"#any\"/>")]
    [Arguments("out-in", "infault", "In", "<input element=\"#any\"/><output element=\"#none\"/>")]
    [Arguments("robust-in-only", "outfault", "In", "<input element=\"#any\"/>")]
    [Arguments("robust-out-only", "infault", "Out", "<output element=\"#none\"/>")]
    [Arguments("in-opt-out", "outfault", "In", "<input element=\"#any\"/><output element=\"#none\"/>")]
    [Arguments("in-opt-out", "infault", "Out", "<input element=\"#any\"/><output element=\"#none\"/>")]
    [Arguments("out-opt-in", "outfault", "In", "<input element=\"#any\"/><output element=\"#none\"/>")]
    [Arguments("out-opt-in", "infault", "Out", "<input element=\"#any\"/><output element=\"#none\"/>")]
    public async Task Wsdl20FaultReferencesApplyNativePropagationAndDefaultLabels(
        string pattern, string direction, string label, string messages)
    {
        var bytes = Document("<interface name=\"Base\"><fault name=\"F\" element=\"#none\"/></interface>" +
            "<interface name=\"Port\" extends=\"t:Base\"><operation name=\"Read\" pattern=\"http://www.w3.org/ns/wsdl/" +
            pattern + "\">" + messages + "<" + direction + " ref=\"t:F\"/></operation></interface>");
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes, Reference("Port"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var selected = result.Wsdl20Operation!;
        await Assert.That(selected.Faults.Count).IsEqualTo(1);
        var fault = selected.Faults[0];
        await Assert.That(fault.DeclaringInterfaceName).IsEqualTo("{urn:api}Base");
        await Assert.That(fault.FaultName).IsEqualTo("{urn:api}F");
        await Assert.That(fault.ContentModel).IsEqualTo("#none");
        await Assert.That(fault.Direction).IsEqualTo(direction == "infault" ? "in" : "out");
        await Assert.That(fault.MessageLabel).IsEqualTo(label);
        Array.Fill(bytes, (byte)'!');
        var xml = XElement.Parse(fault.FaultXml);
        await Assert.That(xml.Name).IsEqualTo(XName.Get("fault", "http://www.w3.org/ns/wsdl"));
        await Assert.That(xml.Attribute("name")!.Value).IsEqualTo("F");
        await Assert.That(xml.Attribute("element")!.Value).IsEqualTo("#none");
        await Assert.That(xml.GetNamespaceOfPrefix("t")!.NamespaceName).IsEqualTo("urn:api");
        await Assert.That(XElement.Parse(selected.OperationXml).Elements().Last().Name.LocalName).IsEqualTo(direction);
    }

    [Test]
    public async Task Wsdl20DiamondFaultComponentsRetainTheirDeclaringIdentityAndDistinctLabels()
    {
        var result = await Resolve("""
            <interface name="Base"><fault name="F" element="#any"/></interface>
            <interface name="Left" extends="t:Base"/>
            <interface name="Right" extends="t:Base"/>
            <interface name="Port" extends="t:Left t:Right">
              <operation name="Read" pattern="http://www.w3.org/ns/wsdl/in-opt-out">
                <outfault ref="t:F" messageLabel="In"/><output element="#none"/>
                <input element="#none"/><infault ref="t:F" messageLabel="Out"/>
              </operation>
            </interface>
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var faults = result.Wsdl20Operation!.Faults;
        await Assert.That(faults.Count).IsEqualTo(2);
        await Assert.That(faults.All(fault => fault.DeclaringInterfaceName == "{urn:api}Base" &&
            fault.FaultName == "{urn:api}F" && fault.ContentModel == "#any")).IsTrue();
        await Assert.That(faults[0].Direction).IsEqualTo("out");
        await Assert.That(faults[0].MessageLabel).IsEqualTo("In");
        await Assert.That(faults[1].Direction).IsEqualTo("in");
        await Assert.That(faults[1].MessageLabel).IsEqualTo("Out");
    }

    [Test]
    [Arguments("in-only", "<input element=\"#none\"/>", "<outfault ref=\"t:F\"/>", "wsdl.fault_direction")]
    [Arguments("out-only", "<output element=\"#none\"/>", "<infault ref=\"t:F\"/>", "wsdl.fault_direction")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<infault ref=\"t:F\"/>", "wsdl.fault_direction")]
    [Arguments("out-in", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"t:F\"/>", "wsdl.fault_direction")]
    [Arguments("robust-in-only", "<input element=\"#none\"/>", "<infault ref=\"t:F\"/>", "wsdl.fault_direction")]
    [Arguments("robust-out-only", "<output element=\"#none\"/>", "<outfault ref=\"t:F\"/>", "wsdl.fault_direction")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"t:F\" messageLabel=\"In\"/>", "wsdl.fault_label")]
    [Arguments("robust-in-only", "<input element=\"#none\"/>", "<outfault ref=\"t:F\" messageLabel=\"Out\"/>", "wsdl.fault_label")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"t:f\"/>", "wsdl.fault_ref")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault/>", "wsdl.fault_ref")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"x:F\"/>", "wsdl.qname")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"t:1F\"/>", "wsdl.xml")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"\"/>", "wsdl.xml")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"t:\"/>", "wsdl.xml")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\":F\"/>", "wsdl.xml")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"t:F\" messageLabel=\"\"/>", "wsdl.xml")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"t:F\" messageLabel=\" \"/>", "wsdl.xml")]
    [Arguments("in-out", "<input element=\"#none\"/><output element=\"#any\"/>", "<outfault ref=\"t:F\"/><outfault ref=\"p:F\" messageLabel=\"Out\"/>", "wsdl.duplicate_fault_ref")]
    public async Task Wsdl20FaultReferenceFailuresPreventPartialSelection(
        string pattern, string messages, string references, string code)
    {
        var result = await Resolve("<interface name=\"Port\" xmlns:p=\"urn:api\"><operation name=\"Read\" " +
            "pattern=\"http://www.w3.org/ns/wsdl/" + pattern + "\">" + messages + references +
            "</operation><fault name=\"F\" element=\"#none\"/></interface>");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments("<interface name=\"Port\"><fault name=\"F\" element=\"#none\"/><fault name=\"F\" element=\"#none\"/></interface>", "wsdl.duplicate_fault")]
    [Arguments("<interface name=\"Left\"><fault name=\"F\" element=\"#any\"/></interface><interface name=\"Right\"><fault name=\"F\" element=\"#any\"/></interface><interface name=\"Port\" extends=\"t:Left t:Right\"/>", "wsdl.fault_conflict")]
    [Arguments("<interface name=\"Base\"><fault name=\"F\" element=\"#none\"/></interface><interface name=\"Port\" extends=\"t:Base\"><fault name=\"F\" element=\"#none\"/></interface>", "wsdl.fault_conflict")]
    [Arguments("<interface name=\"Left\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/><outfault ref=\"t:F\"/></operation></interface><interface name=\"Right\"><fault name=\"F\" element=\"#none\"/></interface><interface name=\"Port\" extends=\"t:Left t:Right\"/>", "wsdl.fault_ref")]
    [Arguments("<interface name=\"Port\"><fault name=\"1F\" element=\"#none\"/></interface>", "wsdl.xml")]
    [Arguments("<interface name=\"Port\"><fault name=\" \" element=\"#none\"/></interface>", "wsdl.xml")]
    public async Task Wsdl20FaultVisibilityAndComponentConflictsAreNotInferredFromTheSelectedInterface(
        string interfaces, string code)
    {
        var result = await Resolve(interfaces);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    public async Task Wsdl20XmlAttributeWhitespaceUsesNativeTokenValuesWithoutNormalizingIriIdentity()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            <description xmlns="http://www.w3.org/ns/wsdl" xmlns:t="urn:api" targetNamespace=" urn:api ">
              <interface name=" Base "><fault name=" F " element=" #any "/></interface>
              <interface name=" Port " extends=" t:Base ">
                <operation name=" Read " pattern=" http://www.w3.org/ns/wsdl/robust-in-only ">
                  <input element=" #none " messageLabel=" In "/><outfault ref=" t:F " messageLabel=" In "/>
                </operation>
              </interface>
            </description>
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes, Reference("Port"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Wsdl20Operation!.DeclaringInterfaceName).IsEqualTo("{urn:api}Port");
        await Assert.That(result.Wsdl20Operation.OperationName).IsEqualTo("{urn:api}Read");
        await Assert.That(result.Wsdl20Operation.MessageExchangePattern).IsEqualTo("http://www.w3.org/ns/wsdl/robust-in-only");
        var fault = result.Wsdl20Operation.Faults[0];
        await Assert.That(fault.DeclaringInterfaceName).IsEqualTo("{urn:api}Base");
        await Assert.That(fault.FaultName).IsEqualTo("{urn:api}F");
        await Assert.That(fault.ContentModel).IsEqualTo("#any");
        await Assert.That(fault.MessageLabel).IsEqualTo("In");
        await Assert.That(XElement.Parse(fault.FaultXml).Attribute("name")!.Value).IsEqualTo(" F ");
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
            var declarations = Enumerable.Range(0, count - 1)
                .Select(index => "<interface name=\"P" + index + "\" extends=\"t:P" + (index + 1) + "\"/>")
                .Prepend("<interface name=\"Port\" extends=\"t:P0\"/>")
                .Append("<interface name=\"P" + (count - 1) +
                    "\"><fault name=\"F\" element=\"#none\"/><operation name=\"Read\"><input element=\"#none\"/>" +
                    "<output element=\"#any\"/><outfault ref=\"t:F\"/></operation></interface>").ToArray();
            foreach (var chain in new[] { string.Concat(declarations), string.Concat(Enumerable.Reverse(declarations)) })
            {
                var limited = await Resolve(chain);
                await Assert.That(limited.Status).IsEqualTo(count == 64 ? ApiOperationResolutionStatus.Selected :
                    ApiOperationResolutionStatus.Indeterminate);
                if (count == 65)
                {
                    await Assert.That(limited.Diagnostic!.Code).IsEqualTo("limit.depth");
                    await Assert.That(limited.Wsdl20Operation).IsNull();
                }
                else
                {
                    await Assert.That(limited.Wsdl20Operation!.Faults[0].DeclaringInterfaceName).IsEqualTo("{urn:api}P63");
                }
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