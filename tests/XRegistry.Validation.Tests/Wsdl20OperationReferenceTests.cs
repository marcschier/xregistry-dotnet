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

    [Test]
    public async Task Wsdl20ImportsAndCircularIncludesRetainOneDeclaringComponentAndItsOrigin()
    {
        var origin = new Uri("https://example.test/root.wsdl");
        var calls = new List<string>();
        var root = Document("""
            <include location="left.wsdl"/><include location="right.wsdl"/>
            <interface name="Port" xmlns:f="urn:foreign" extends="f:Base"/>
            <import namespace="urn:foreign" location=" foreign.wsdl "/>
            """);
        var foreign = Document("""
            <interface name="Base" styleDefault="urn:foreign-style">
              <fault name="F" element="#none"/>
              <operation name="Read"><input element="#none"/><output element="#any"/><outfault ref="t:F"/></operation>
            </interface>
            """, "urn:foreign");
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root,
            ApiOperationReference.Parse("/apigroups/g/apis/a#interfaces/%7Burn:api%7DPort/operations/%7Burn:foreign%7DRead"),
            origin, (uri, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri switch
                {
                    "https://example.test/left.wsdl" => Document("<include location=\"root.wsdl\"/><include location=\"right.wsdl\"/>"),
                    "https://example.test/right.wsdl" => Document("<include location=\"root.wsdl\"/>"),
                    "https://example.test/foreign.wsdl" => foreign,
                    _ => throw new InvalidOperationException("Unexpected dependency " + uri)
                });
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.SequenceEqual(["https://example.test/left.wsdl", "https://example.test/right.wsdl",
            "https://example.test/foreign.wsdl"])).IsTrue();
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://example.test/foreign.wsdl"));
        await Assert.That(result.Wsdl20Operation!.DeclaringInterfaceName).IsEqualTo("{urn:foreign}Base");
        await Assert.That(result.Wsdl20Operation.OperationName).IsEqualTo("{urn:foreign}Read");
        await Assert.That(result.Wsdl20Operation.Styles.SequenceEqual(["urn:foreign-style"])).IsTrue();
        await Assert.That(result.Wsdl20Operation.Faults[0].FaultName).IsEqualTo("{urn:foreign}F");
        Array.Fill(root, (byte)'!');
        Array.Fill(foreign, (byte)'!');
        await Assert.That(XElement.Parse(result.Wsdl20Operation.OperationXml).GetNamespaceOfPrefix("t")!.NamespaceName)
            .IsEqualTo("urn:foreign");
        await Assert.That(XElement.Parse(result.Wsdl20Operation.Faults[0].FaultXml).Attribute("name")!.Value).IsEqualTo("F");
    }

    [Test]
    public async Task Wsdl20DependenciesUseContainingXmlBaseAndNamespaceCatalogWithoutInventingOrigin()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document("<include xml:base=\"parts/\" location=\"first.wsdl\"/>"), Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri switch
                {
                    "https://example.test/parts/first.wsdl" => Document(
                        "<include xml:base=\"../shared/\" location=\"second.wsdl\"/>"),
                    "https://example.test/shared/second.wsdl" => Document(
                        "<interface name=\"Port\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>"),
                    _ => throw new InvalidOperationException("Unexpected dependency " + uri)
                });
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.SequenceEqual(["https://example.test/parts/first.wsdl",
            "https://example.test/shared/second.wsdl"])).IsTrue();
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://example.test/shared/second.wsdl"));
        var catalog = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document("<import namespace=\"urn:foreign\"/>"),
            ApiOperationReference.Parse("/apigroups/g/apis/a#interfaces/%7Burn:foreign%7DPort/operations/%7Burn:foreign%7DRead"),
            resolveReference: (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document(
                    "<interface name=\"Port\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>",
                    "urn:foreign"));
            });
        await Assert.That(catalog.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.Last()).IsEqualTo("urn:foreign");
        await Assert.That(catalog.DocumentUri).IsNull();
        await Assert.That(catalog.Wsdl20Operation!.OperationName).IsEqualTo("{urn:foreign}Read");
    }

    [Test]
    [Arguments("<include/>", "wsdl.include", 0)]
    [Arguments("<import location=\"other.wsdl\"/>", "wsdl.import_namespace", 0)]
    [Arguments("<import namespace=\"urn:api\" location=\"other.wsdl\"/>", "wsdl.import_namespace", 0)]
    [Arguments("<include location=\"other.wsdl#fragment\"/>", "dependency.uri", 0)]
    [Arguments("<include location=\"other.wsdl\"/>", "wsdl.import_namespace", 1)]
    [Arguments("<import namespace=\"urn:foreign\" location=\"other.wsdl\"/><import namespace=\"urn:foreign\" location=\"other.wsdl\"/>",
        "wsdl.duplicate_import", 1)]
    public async Task Wsdl20ImportAndIncludeContradictionsPreventPartialSelection(string references, string code, int expectedCalls)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(references),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document("", "urn:foreign"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
        await Assert.That(calls).IsEqualTo(expectedCalls);
    }

    [Test]
    public async Task Wsdl20ForeignVisibilityRequiresImportsInTheAuthoredDocument()
    {
        var root = Document("""
            <include location="part.wsdl"/>
            <interface name="Port" xmlns:f="urn:foreign" extends="f:Base"/>
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root,
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
                ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri.EndsWith("part.wsdl", StringComparison.Ordinal) ?
                    Document("<import namespace=\"urn:foreign\" location=\"foreign.wsdl\"/>") :
                    Document("<interface name=\"Base\"/>", "urn:foreign")));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl.import_required");
        await Assert.That(result.Wsdl20Operation).IsNull();
        var unrelated = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document("<import namespace=\"urn:foreign\" location=\"foreign.wsdl\"/>"),
            ApiOperationReference.Parse("/apigroups/g/apis/a#interfaces/%7Burn:third%7DPort/operations/%7Burn:third%7DRead"),
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
                ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri.EndsWith("foreign.wsdl", StringComparison.Ordinal) ?
                    Document("<import namespace=\"urn:third\" location=\"third.wsdl\"/>", "urn:foreign") :
                    Document("<interface name=\"Port\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>",
                        "urn:third")));
        await Assert.That(unrelated.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        await Assert.That(unrelated.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Wsdl20NamespaceVisibilityUsesAllAcquiredHintsWithoutOrderDependentCatalogLookups(bool catalogFirst)
    {
        const string catalog = "<import namespace=\"urn:foreign\"/>";
        const string include = "<include location=\"part.wsdl\"/>";
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document((catalogFirst ? catalog + include : include + catalog) +
                "<interface name=\"Port\" xmlns:f=\"urn:foreign\" extends=\"f:Base\"/>"),
            ApiOperationReference.Parse("/apigroups/g/apis/a#interfaces/%7Burn:api%7DPort/operations/%7Burn:foreign%7DRead"),
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri switch
                {
                    "https://example.test/part.wsdl" => Document("<import namespace=\"urn:foreign\" location=\"foreign.wsdl\"/>"),
                    "https://example.test/foreign.wsdl" => Document(
                        "<interface name=\"Base\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>",
                        "urn:foreign"),
                    _ => throw new InvalidOperationException("Unnecessary catalog lookup " + uri)
                });
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.SequenceEqual(["https://example.test/part.wsdl", "https://example.test/foreign.wsdl"])).IsTrue();
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://example.test/foreign.wsdl"));
        await Assert.That(result.Wsdl20Operation!.DeclaringInterfaceName).IsEqualTo("{urn:foreign}Base");
    }

    [Test]
    public async Task Wsdl20UnavailableImportHintsDoNotPreventResolvedComponentsOrInventMissingOnes()
    {
        var calls = new List<string>();
        var reference = ApiOperationReference.Parse(
            "/apigroups/g/apis/a#interfaces/%7Burn:api%7DPort/operations/%7Burn:foreign%7DRead");
        var root = Document("""
            <import namespace="urn:foreign" location="missing.wsdl"/>
            <import namespace="urn:foreign" location="available.wsdl"/>
            <interface name="Port" xmlns:f="urn:foreign" extends="f:Base"/>
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, reference,
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri.EndsWith("available.wsdl", StringComparison.Ordinal) ?
                    Document("<interface name=\"Base\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>",
                        "urn:foreign") : (ReadOnlyMemory<byte>?)null);
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.SequenceEqual(["https://example.test/missing.wsdl",
            "https://example.test/available.wsdl"])).IsTrue();
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://example.test/available.wsdl"));
        var unresolved = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, reference,
            new Uri("https://example.test/root.wsdl"));
        await Assert.That(unresolved.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(unresolved.Diagnostic!.Code).IsEqualTo("reference.unresolved");
        await Assert.That(unresolved.Wsdl20Operation).IsNull();
        var local = await Resolve("""
            <import namespace="urn:unused"/>
            <interface name="Port"><operation name="Read"><input element="#none"/><output element="#any"/></operation></interface>
            """);
        await Assert.That(local.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(local.Wsdl20Operation!.OperationName).IsEqualTo("{urn:api}Read");
        var required = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document("<import namespace=\"urn:foreign\" location=\"missing.wsdl\"/><include location=\"missing.wsdl\"/>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });
        await Assert.That(required.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(required.Diagnostic!.Code).IsEqualTo("reference.unresolved");
        await Assert.That(calls.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Wsdl20DependencyPolicyMissingBaseMalformedXmlAndCancellationRemainExplicit()
    {
        var bytes = Document("<include location=\"part.wsdl\"/>");
        var missingBase = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes, Reference("Port"));
        await Assert.That(missingBase.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missingBase.Diagnostic!.Code).IsEqualTo("dependency.base");
        var missing = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes, Reference("Port"),
            new Uri("https://example.test/root.wsdl"));
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missing.Diagnostic!.Code).IsEqualTo("reference.unresolved");
        var denied = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (_, _) => throw new UnauthorizedAccessException());
        await Assert.That(denied.Status).IsEqualTo(ApiOperationResolutionStatus.Denied);
        await Assert.That(denied.Diagnostic!.Code).IsEqualTo("dependency.denied");
        foreach (var xml in new[] { "<!DOCTYPE x [<!ENTITY e 'unsafe'>]><x>&e;</x>",
            "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>" })
        {
            var invalid = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes, Reference("Port"),
                new Uri("https://example.test/root.wsdl"), (_, _) =>
                    ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(xml)));
            await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
            await Assert.That(invalid.Wsdl20Operation).IsNull();
        }
        using var cancellation = new CancellationTokenSource();
        await Assert.That(async () => await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            bytes, Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document(""));
            }, cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments(32)]
    [Arguments(33)]
    public async Task Wsdl20DistinctDocumentBudgetCountsAcquisitionsRatherThanRepeatedIncludes(int count)
    {
        var calls = 0;
        var references = string.Concat(Enumerable.Range(0, count)
            .Select(index => "<include location=\"part" + index + ".wsdl\"/><include location=\"part" + index + ".wsdl\"/>"));
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document(references + "<interface name=\"Port\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document(""));
            });
        await Assert.That(calls).IsEqualTo(32);
        await Assert.That(result.Status).IsEqualTo(count == 32 ? ApiOperationResolutionStatus.Selected :
            ApiOperationResolutionStatus.Indeterminate);
        if (count == 33)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.references");
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
    }

    [Test]
    [Arguments(3, false)]
    [Arguments(4, false)]
    [Arguments(1, true)]
    public async Task Wsdl20DependencyBytesShareTheCumulativeBudget(int count, bool oversized)
    {
        var calls = 0;
        var body = Document("<!---->");
        var size = ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes + (oversized ? 1 : 0);
        var dependency = Document("<!--" + new string('x', size - body.Length) + "-->");
        await Assert.That(dependency.Length).IsEqualTo(size);
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document(string.Concat(Enumerable.Range(0, count).Select(index => "<include location=\"part" + index + ".wsdl\"/>")) +
                "<interface name=\"Port\"><operation name=\"Read\"><input element=\"#none\"/><output element=\"#any\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(dependency);
            });
        await Assert.That(calls).IsEqualTo(count);
        await Assert.That(result.Status).IsEqualTo(!oversized && count == 3 ? ApiOperationResolutionStatus.Selected :
            ApiOperationResolutionStatus.Indeterminate);
        if (oversized || count == 4)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo(oversized ? "limit.bytes" : "limit.total_bytes");
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
    }

    private static ValueTask<ApiOperationResolutionResult> Resolve(string interfaces) =>
        ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(interfaces), Reference("Port"));

    private static ApiOperationReference Reference(string name) => ApiOperationReference.Parse(
        "/apigroups/g/apis/a#interfaces/%7Burn:api%7D" + name + "/operations/%7Burn:api%7DRead");

    private static byte[] Document(string content, string targetNamespace = "urn:api") => Encoding.UTF8.GetBytes(
        "<description xmlns=\"http://www.w3.org/ns/wsdl\" xmlns:t=\"" + targetNamespace + "\" targetNamespace=\"" + targetNamespace + "\">" +
        content + "</description>");
}