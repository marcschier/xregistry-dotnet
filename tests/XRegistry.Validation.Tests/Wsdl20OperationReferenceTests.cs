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
    [Arguments("<interface name=\"Port\"><operation name=\"Read\"><input/><output element=\"#none\"/></operation></interface>", "wsdl20.element")]
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

    [Test]
    public async Task Wsdl20InlineElementsRetainDeclarationsAndAllSchemaContextAfterDisposal()
    {
        var bytes = Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:p="urn:payload">
              <xs:schema targetNamespace="urn:payload">
                <xs:simpleType name="Payload"><xs:restriction base="xs:string"/></xs:simpleType>
                <xs:element name="Payload" type="p:Payload"/>
              </xs:schema>
              <xs:schema targetNamespace="urn:payload"><xs:element name="Failure" type="p:Payload"/></xs:schema>
            </types>
            <interface name="Port" xmlns:p="urn:payload">
              <fault name="F" element="p:Failure"/>
              <operation name="Read"><input element="p:Payload"/><output element="p:Payload"/><outfault ref="t:F"/></operation>
            </interface>
            """);
        var origin = new Uri("https://example.test/root.wsdl");
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", bytes,
            Reference("Port"), origin);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var operation = result.Wsdl20Operation!;
        await Assert.That(operation.SchemaElements.Count).IsEqualTo(2);
        await Assert.That(operation.SchemaElements.Select(element => element.ElementName).SequenceEqual(
            ["{urn:payload}Payload", "{urn:payload}Failure"])).IsTrue();
        await Assert.That(operation.Faults[0].ContentModel).IsEqualTo("{urn:payload}Failure");
        await Assert.That(ReferenceEquals(operation.Faults[0].SchemaElement, operation.SchemaElements[1])).IsTrue();
        await Assert.That(operation.SchemaElements[0].DocumentUri).IsEqualTo(origin);
        await Assert.That(operation.SchemaElements[0].SchemaDocuments.Count).IsEqualTo(2);
        Array.Fill(bytes, (byte)'!');
        var retained = XElement.Parse(operation.SchemaElements[0].ElementXml);
        await Assert.That(retained.Attribute("name")!.Value).IsEqualTo("Payload");
        await Assert.That(retained.Attribute("type")!.Value).IsEqualTo("p:Payload");
        await Assert.That(retained.GetNamespaceOfPrefix("p")!.NamespaceName).IsEqualTo("urn:payload");
        await Assert.That(operation.SchemaElements[0].SchemaDocuments.Select(XElement.Parse)
            .Sum(schema => schema.Elements().Count())).IsEqualTo(3);
    }

    [Test]
    [Arguments("include", "urn:api", ApiOperationResolutionStatus.Selected)]
    [Arguments("import", "urn:foreign", ApiOperationResolutionStatus.Invalid)]
    public async Task Wsdl20SchemaVisibilityFollowsIncludesButNotWsdlImports(string kind, string targetNamespace,
        ApiOperationResolutionStatus status)
    {
        var root = Document("<" + kind + " location=\"schemas.wsdl\"" +
            (kind == "import" ? " namespace=\"urn:foreign\"" : "") + "/>" + """
            <interface name="Port" xmlns:p="urn:payload"><operation name="Read">
              <input element="p:Payload"/><output element="#none"/>
            </operation></interface>
            """);
        var schema = Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:schema targetNamespace="urn:payload"><xs:element name="Payload" type="xs:string"/></xs:schema>
            </types>
            """, targetNamespace);
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(schema);
            });
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Wsdl20Operation!.SchemaElements[0].ElementName).IsEqualTo("{urn:payload}Payload");
            await Assert.That(result.Wsdl20Operation.SchemaElements[0].DocumentUri).IsEqualTo(
                new Uri("https://example.test/schemas.wsdl"));
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl20.schema_element");
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
    }

    [Test]
    [Arguments("<xs:simpleType name=\"Payload\"><xs:restriction base=\"xs:string\"/></xs:simpleType>", "wsdl20.schema_element")]
    [Arguments("<xs:element name=\"Container\"><xs:complexType><xs:sequence><xs:element name=\"Payload\" type=\"xs:string\"/></xs:sequence></xs:complexType></xs:element>", "wsdl20.schema_element")]
    [Arguments("<xs:element name=\"Payload\" type=\"xs:Missing\"/>", "xsd.schema")]
    [Arguments("<xs:element name=\"Payload\" type=\"xs:string\"/><xs:element name=\"Payload\" type=\"xs:string\"/>", "wsdl20.schema_duplicate")]
    [Arguments("<xs:simpleType name=\"T\"><xs:restriction base=\"xs:string\"/></xs:simpleType><xs:complexType name=\"T\"/>", "wsdl20.schema_duplicate")]
    public async Task Wsdl20InlineSchemaFailuresNeverSubstituteTypesOrLocalElements(string declarations, string code)
    {
        var result = await Resolve("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            "<xs:schema targetNamespace=\"urn:api\">" + declarations + "</xs:schema></types>" +
            "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Wsdl20DuplicateSymbolsAcrossSeparateInlineSchemasAreRejected(bool types)
    {
        var declaration = types ? "<xs:complexType name=\"Duplicate\"/>" : "<xs:element name=\"Duplicate\" type=\"xs:string\"/>";
        var result = await Resolve("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            "<xs:schema targetNamespace=\"urn:api\">" + declaration +
            "<xs:element name=\"Payload\" type=\"xs:string\"/></xs:schema>" +
            "<xs:schema targetNamespace=\"urn:api\">" + declaration + "</xs:schema></types>" +
            "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl20.schema_duplicate");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    public async Task Wsdl20InlineCompilationCannotBypassTheSharedWorkBudget()
    {
        var result = await Resolve("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:element name="Payload"><xs:complexType><xs:sequence>
                <xs:element name="Child" type="xs:string" maxOccurs="8388609"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema></types>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.work");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    public async Task Wsdl20AllContentModelsRetainTheirNativeSchemaWithoutInventingMessageAlternatives()
    {
        var result = await Resolve("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:element name="Payload"><xs:complexType><xs:all>
                <xs:element name="name" type="xs:string"/><xs:element name="age" type="xs:int" minOccurs="0"/>
              </xs:all></xs:complexType></xs:element>
            </xs:schema></types>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var schema = result.Wsdl20Operation!.SchemaElements.Single();
        await Assert.That(schema.ElementName).IsEqualTo("{urn:api}Payload");
        await Assert.That(XElement.Parse(schema.ElementXml).Descendants(
            XName.Get("all", "http://www.w3.org/2001/XMLSchema")).Count()).IsEqualTo(1);
        await Assert.That(schema.SchemaDocuments.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("minOccurs")]
    [Arguments("maxOccurs")]
    public async Task Wsdl20HugeOccurrenceIntegersNeverReachUnreservedSchemaCompilation(string attribute)
    {
        var additional = attribute == "minOccurs" ? " maxOccurs=\"unbounded\"" : "";
        var result = await Resolve(
            "<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:schema targetNamespace=\"urn:api\">" +
            "<xs:element name=\"Payload\"><xs:complexType><xs:sequence><xs:element name=\"Child\" " +
            attribute + "=\"18446744073709551616\"" + additional +
            "/></xs:sequence></xs:complexType></xs:element></xs:schema></types>" +
            "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.work");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments("<xs:schema targetNamespace=\"urn:api\"><xs:include schemaLocation=\"#xpointer(//xs:schema)\"/></xs:schema>", "wsdl20.schema_fragment")]
    [Arguments("<xs:schema targetNamespace=\"urn:api\"><xs:redefine schemaLocation=\"never.xsd\"/></xs:schema>", "xsd.construct_unsupported")]
    [Arguments("<xs:schema targetNamespace=\"urn:api\"><xs:element name=\"Payload\"><xs:simpleType><xs:restriction base=\"xs:string\"><xs:pattern value=\".*\"/></xs:restriction></xs:simpleType></xs:element></xs:schema>", "xsd.construct_unsupported")]
    public async Task Wsdl20UnimplementedSchemaPoliciesDoNotAcquireDependenciesOrReturnPartialElements(string schema, string code)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml",
            Document("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" + schema + "</types>" +
                "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Unsupported schema policies must not acquire dependencies.");
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task Wsdl20NoNamespaceInlineElementsUseTheirOwnSymbolSpace()
    {
        var result = await Resolve("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema>
              <xs:element name="Payload" type="xs:string"/>
            </xs:schema></types>
            <interface name="Port"><operation name="Read">
              <w:input xmlns="" xmlns:w="http://www.w3.org/ns/wsdl" element="Payload"/>
              <output element="#none"/>
            </operation></interface>
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Wsdl20Operation!.SchemaElements[0].ElementName).IsEqualTo("{}Payload");
        await Assert.That(result.Wsdl20Operation.SchemaElements[0].SchemaDocuments.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Wsdl20InlineValidationDoesNotRechargeAlreadyAcquiredBytesOrCircularIncludes()
    {
        var content = """
            <include location="part0.wsdl"/><include location="part1.wsdl"/><include location="part2.wsdl"/>
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:element name="Payload" type="xs:string"/>
            </xs:schema></types>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """;
        var limit = ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes;
        var root = Document(content + "<!--" + new string('x', limit - Document(content + "<!---->").Length) + "-->");
        var body = "<include location=\"root.wsdl\"/>";
        var dependency = Document(body + "<!--" + new string('x', limit - Document(body + "<!---->").Length) + "-->");
        await Assert.That(root.Length + 3L * dependency.Length).IsEqualTo(
            ApiOperationDocumentResolver.Capabilities.MaxTotalDocumentBytes);
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(dependency);
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(result.Wsdl20Operation!.SchemaElements.Count).IsEqualTo(1);
        await Assert.That(result.Wsdl20Operation.SchemaElements[0].ElementName).IsEqualTo("{urn:api}Payload");
        await Assert.That(result.Wsdl20Operation.SchemaElements[0].SchemaDocuments.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Wsdl20SchemaDependenciesRetainIncludeVisibilityAndForeignCompilerContext(bool exposeForeign)
    {
        var root = Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema" xml:base="schemas/">
              <xs:schema targetNamespace="urn:api"><xs:include schemaLocation="parts/main.xsd"/></xs:schema>
            """ + (exposeForeign ? "<xs:import namespace=\"urn:foreign\" schemaLocation=\"foreign.xsd\"/>" : "") + """
            </types>
            <interface name="Port" xmlns:f="urn:foreign"><operation name="Read">
              <input element="t:Payload"/><output element="
            """ + (exposeForeign ? "f:Foreign" : "#none") + "\"/></operation></interface>");
        var main = Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:foreign" targetNamespace="urn:api">
              <xs:import namespace="urn:foreign" schemaLocation="../foreign.xsd"/>
              <xs:include schemaLocation="other.xsd"/>
              <xs:element name="Payload" type="f:Text"/>
            </xs:schema>
            """);
        var other = Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:api">
              <xs:include schemaLocation="main.xsd"/>
              <xs:element name="Other" type="xs:string"/>
            </xs:schema>
            """);
        var foreign = Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:foreign">
              <xs:simpleType name="Text"><xs:restriction base="xs:string"/></xs:simpleType>
              <xs:element name="Foreign" type="xs:string"/>
            </xs:schema>
            """);
        var supplied = new Dictionary<string, byte[]>
        {
            ["https://example.test/schemas/parts/main.xsd"] = main,
            ["https://example.test/schemas/parts/other.xsd"] = other,
            ["https://example.test/schemas/foreign.xsd"] = foreign
        };
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root,
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(supplied[uri]);
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var expectedCalls = exposeForeign ? new[] {
            "https://example.test/schemas/foreign.xsd", "https://example.test/schemas/parts/main.xsd",
            "https://example.test/schemas/parts/other.xsd" } : [
            "https://example.test/schemas/parts/main.xsd", "https://example.test/schemas/foreign.xsd",
            "https://example.test/schemas/parts/other.xsd"];
        await Assert.That(calls.SequenceEqual(expectedCalls)).IsTrue();
        var element = result.Wsdl20Operation!.SchemaElements[0];
        await Assert.That(element.ElementName).IsEqualTo("{urn:api}Payload");
        await Assert.That(element.DocumentUri).IsEqualTo(new Uri("https://example.test/schemas/parts/main.xsd"));
        await Assert.That(element.SchemaDocuments.Count).IsEqualTo(4);
        await Assert.That(XElement.Parse(element.SchemaDocuments[0]).Attribute(XNamespace.Xml + "base")!.Value)
            .IsEqualTo("https://example.test/schemas/");
        await Assert.That(element.SchemaDocumentUris.Select(uri => uri!.AbsoluteUri).SequenceEqual([
            "https://example.test/root.wsdl", "https://example.test/schemas/parts/main.xsd",
            "https://example.test/schemas/foreign.xsd", "https://example.test/schemas/parts/other.xsd"])).IsTrue();
        foreach (var bytes in supplied.Values) { Array.Fill(bytes, (byte)'!'); }
        Array.Fill(root, (byte)'!');
        await Assert.That(XElement.Parse(element.ElementXml).Attribute("type")!.Value).IsEqualTo("f:Text");
        await Assert.That(element.SchemaDocuments.Any(xml => xml.Contains("name=\"Text\"", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Wsdl20Operation.SchemaElements.Count).IsEqualTo(exposeForeign ? 2 : 1);
        if (exposeForeign)
        {
            await Assert.That(result.Wsdl20Operation.SchemaElements[1].ElementName).IsEqualTo("{urn:foreign}Foreign");
            await Assert.That(result.Wsdl20Operation.SchemaElements[1].DocumentUri)
                .IsEqualTo(new Uri("https://example.test/schemas/foreign.xsd"));
        }
    }

    [Test]
    public async Task Wsdl20InlineImportsDoNotMakeForeignElementsReferenceable()
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:import namespace="urn:foreign" schemaLocation="foreign.xsd"/>
            </xs:schema></types>
            <interface name="Port" xmlns:f="urn:foreign"><operation name="Read">
              <input element="f:Foreign"/><output element="#none"/>
            </operation></interface>
            """), Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:foreign">
                  <xs:element name="Foreign" type="xs:string"/>
                </xs:schema>
                """)));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl20.schema_element");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Wsdl20DirectSchemaCatalogUsesInlineComponentsOrRetainsAnUnknownOrigin(bool inline)
    {
        var calls = new List<string>();
        var schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:payload">
              <xs:element name="Payload" type="xs:string"/>
            </xs:schema>
            """;
        var root = Document("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            "<xs:import namespace=\"urn:payload\"/>" + (inline ? schema : "") + """
            </types><interface name="Port" xmlns:p="urn:payload"><operation name="Read">
              <input element="p:Payload"/><output element="#none"/>
            </operation></interface>
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(schema));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.Count).IsEqualTo(inline ? 0 : 1);
        if (!inline) { await Assert.That(calls[0]).IsEqualTo("urn:payload"); }
        var selected = result.Wsdl20Operation!.SchemaElements[0];
        await Assert.That(selected.ElementName).IsEqualTo("{urn:payload}Payload");
        await Assert.That(selected.DocumentUri).IsEqualTo(inline ? new Uri("https://example.test/root.wsdl") : null);
        await Assert.That(selected.SchemaDocumentUris.Count).IsEqualTo(1);
        await Assert.That(selected.SchemaDocumentUris[0]).IsEqualTo(selected.DocumentUri);
    }

    [Test]
    [Arguments("<xs:schema targetNamespace=\"urn:other\"/>", "wsdl20.schema_namespace", ApiOperationResolutionStatus.Invalid)]
    [Arguments("<wrong/>", "xsd.root", ApiOperationResolutionStatus.Invalid)]
    [Arguments("<!DOCTYPE x [<!ENTITY e 'unsafe'>]><x>&e;</x>", "wsdl.xml", ApiOperationResolutionStatus.Invalid)]
    public async Task Wsdl20SchemaDependencyContradictionsNeverReturnPartialSelection(string schema, string code,
        ApiOperationResolutionStatus status)
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:include schemaLocation="part.xsd"/><xs:element name="Payload" type="xs:string"/>
            </xs:schema></types><interface name="Port"><operation name="Read">
              <input element="t:Payload"/><output element="#none"/>
            </operation></interface>
            """), Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                schema.Replace("<xs:schema", "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"", StringComparison.Ordinal))));
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments(31)]
    [Arguments(32)]
    public async Task Wsdl20SchemaAndWsdlAcquisitionsShareDistinctReferenceLimits(int count)
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(
            "<include location=\"base.wsdl\"/>" +
            "<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:schema targetNamespace=\"urn:api\">" +
            string.Concat(Enumerable.Range(0, count).Select(index => "<xs:include schemaLocation=\"s" + index + ".xsd\"/>")) +
            "<xs:element name=\"Payload\" type=\"xs:string\"/></xs:schema></types>" +
            "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri.EndsWith(".wsdl", StringComparison.Ordinal) ?
                    Document("") : Encoding.UTF8.GetBytes(
                        "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:api\"/>"));
            });
        await Assert.That(calls.Count).IsEqualTo(32);
        await Assert.That(result.Status).IsEqualTo(count == 31 ? ApiOperationResolutionStatus.Selected :
            ApiOperationResolutionStatus.Indeterminate);
        if (count == 32)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.references");
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
    }

    [Test]
    public async Task Wsdl20ChameleonIncludesAdoptEachContainingNamespaceWithoutRewritingSourceSchemas()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:schema targetNamespace="urn:first"><xs:include schemaLocation="shared.xsd"/></xs:schema>
              <xs:schema targetNamespace="urn:second"><xs:include schemaLocation="shared.xsd"/></xs:schema>
            </types>
            <interface name="Port" xmlns:a="urn:first" xmlns:b="urn:second"><operation name="Read">
              <input element="a:Payload"/><output element="b:Payload"/>
            </operation></interface>
            """), Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      <xs:simpleType name="Text"><xs:restriction base="xs:string"/></xs:simpleType>
                      <xs:element name="Payload" type="Text"/>
                    </xs:schema>
                    """));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("https://example.test/shared.xsd");
        await Assert.That(result.Wsdl20Operation!.SchemaElements.Select(element => element.ElementName)
            .SequenceEqual(["{urn:first}Payload", "{urn:second}Payload"])).IsTrue();
        foreach (var element in result.Wsdl20Operation.SchemaElements)
        {
            await Assert.That(element.DocumentUri).IsEqualTo(new Uri(calls[0]));
            await Assert.That(XElement.Parse(element.ElementXml).Attribute("type")!.Value).IsEqualTo("Text");
            var chameleon = element.SchemaDocuments.Select(XElement.Parse)
                .Single(schema => schema.Attribute("targetNamespace") is null);
            await Assert.That(chameleon.Element(XName.Get("element", "http://www.w3.org/2001/XMLSchema"))!
                .Attribute("type")!.Value).IsEqualTo("Text");
            await Assert.That(element.SchemaDocuments.Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task Wsdl20SchemaResolutionPreservesDenialCancellationAndMissingBase()
    {
        var root = Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:include schemaLocation="part.xsd"/><xs:element name="Payload" type="xs:string"/>
            </xs:schema></types><interface name="Port"><operation name="Read">
              <input element="t:Payload"/><output element="#none"/>
            </operation></interface>
            """);
        var missingBase = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"));
        await Assert.That(missingBase.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missingBase.Diagnostic!.Code).IsEqualTo("dependency.base");
        var origin = new Uri("https://example.test/root.wsdl");
        var missing = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"), origin);
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missing.Diagnostic!.Code).IsEqualTo("reference.unresolved");
        var denied = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"), origin,
            (_, _) => throw new UnauthorizedAccessException());
        await Assert.That(denied.Status).IsEqualTo(ApiOperationResolutionStatus.Denied);
        await Assert.That(denied.Diagnostic!.Code).IsEqualTo("dependency.denied");
        using var cancellation = new CancellationTokenSource();
        await Assert.That(async () => await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root,
            Reference("Port"), origin, (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            }, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(missing.Wsdl20Operation).IsNull();
        await Assert.That(denied.Wsdl20Operation).IsNull();
    }

    [Test]
    public async Task Wsdl20LocationlessSchemaImportsCompileAllResolvedNamespaceDocumentsWithoutExposingThem()
    {
        var root = Document("""
            <import namespace="urn:other" location="other.wsdl"/>
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api" xmlns:f="urn:foreign">
              <xs:import namespace="urn:foreign"/>
              <xs:element name="Payload" type="f:Text"/>
            </xs:schema></types>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """);
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document("""
                    <types xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      <xs:schema targetNamespace="urn:foreign"><xs:element name="Foreign" type="xs:string"/></xs:schema>
                      <xs:schema targetNamespace="urn:foreign"><xs:simpleType name="Text"><xs:restriction base="xs:string"/></xs:simpleType></xs:schema>
                    </types>
                    """, "urn:other"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("https://example.test/other.wsdl");
        var selected = result.Wsdl20Operation!.SchemaElements.Single();
        await Assert.That(selected.ElementName).IsEqualTo("{urn:api}Payload");
        await Assert.That(selected.SchemaDocuments.Count).IsEqualTo(3);
        await Assert.That(selected.SchemaDocuments.Any(xml => xml.Contains("name=\"Text\"", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Wsdl20ExternalSchemaBytesUseTheAcquisitionBudgetOnlyOnce(int excess)
    {
        var root = Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="urn:api" schemaLocation="large.xsd"/></types>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """);
        const string start = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:api\"><xs:element name=\"Payload\" type=\"xs:string\"/><!--";
        const string end = "--></xs:schema>";
        var bytes = Encoding.UTF8.GetBytes(start + new string('x',
            ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes + excess - start.Length - end.Length) + end);
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes);
            });
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(bytes.Length).IsEqualTo(ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes + excess);
        await Assert.That(result.Status).IsEqualTo(excess == 0 ? ApiOperationResolutionStatus.Selected :
            ApiOperationResolutionStatus.Indeterminate);
        if (excess == 1)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.bytes");
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
        else
        {
            await Assert.That(result.Wsdl20Operation!.SchemaElements[0].ElementName).IsEqualTo("{urn:api}Payload");
        }
    }

    [Test]
    [Arguments(64, false)]
    [Arguments(65, false)]
    [Arguments(64, true)]
    [Arguments(65, true)]
    public async Task Wsdl20SchemaDepthCannotBeBypassedByPreviouslyCachedNamespaceDocuments(int depth, bool reverse)
    {
        var schemas = Enumerable.Range(0, depth + 1).Select(index =>
            "<xs:schema targetNamespace=\"urn:s" + index + "\">" +
            (index < depth ? "<xs:import namespace=\"urn:s" + (index + 1) + "\"/>" : "") +
            (index == 0 ? "<xs:element name=\"Payload\" type=\"xs:string\"/>" : "") + "</xs:schema>");
        var result = await Resolve("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            string.Concat(reverse ? schemas.Reverse() : schemas) + "</types>" +
            "<interface name=\"Port\" xmlns:p=\"urn:s0\"><operation name=\"Read\"><input element=\"p:Payload\"/><output element=\"#none\"/></operation></interface>");
        await Assert.That(result.Status).IsEqualTo(depth == 64 ? ApiOperationResolutionStatus.Selected :
            ApiOperationResolutionStatus.Indeterminate);
        if (depth == 65)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.depth");
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
        else
        {
            await Assert.That(result.Wsdl20Operation!.SchemaElements[0].ElementName).IsEqualTo("{urn:s0}Payload");
        }
    }

    [Test]
    public async Task Wsdl20RequiredExtensionsAreRejectedBeforeAcquiringSchemaDependencies()
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="urn:api" schemaLocation="part.xsd"/></types>
            <unknown xmlns="urn:extension" xmlns:w="http://www.w3.org/ns/wsdl" w:required="true"/>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """), Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                throw new UnauthorizedAccessException();
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl.extension");
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments("urn:other")]
    [Arguments("")]
    public async Task Wsdl20DirectSchemaImportsRequireTheirDeclaredNamespaceWithoutChameleonAdoption(string namespaceName)
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="urn:api" schemaLocation="part.xsd"/></types>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """), Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"" +
                (namespaceName.Length == 0 ? "" : " targetNamespace=\"" + namespaceName + "\"") +
                "><xs:element name=\"Payload\" type=\"xs:string\"/></xs:schema>")));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl20.schema_namespace");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    public async Task Wsdl20SchemaDependenciesCannotExceedTheSharedTotalByteBudget()
    {
        const string start = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:api\"><!--";
        const string end = "--></xs:schema>";
        var bytes = Encoding.UTF8.GetBytes(start + new string('x',
            ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes - start.Length - end.Length) + end);
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:include schemaLocation="a.xsd"/><xs:include schemaLocation="b.xsd"/>
              <xs:include schemaLocation="c.xsd"/><xs:include schemaLocation="d.xsd"/>
              <xs:element name="Payload" type="xs:string"/>
            </xs:schema></types>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """), Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes);
            });
        await Assert.That(calls).IsEqualTo(4);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.total_bytes");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Wsdl20SchemaNamespaceReferencesUseDeclaredLocationHintsWithoutOrderDependentCatalogCalls(bool reverse)
    {
        const string inline = """
            <xs:schema targetNamespace="urn:api" xmlns:f="urn:foreign">
              <xs:import namespace="urn:foreign"/><xs:element name="Payload" type="f:Text"/>
            </xs:schema>
            """;
        const string import = "<xs:import namespace=\"urn:foreign\" schemaLocation=\"foreign.xsd\"/>";
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(
            "<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" + (reverse ? import + inline : inline + import) +
            "</types><interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                if (uri != "https://example.test/foreign.xsd") { throw new UnauthorizedAccessException(); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:foreign">
                      <xs:simpleType name="Text"><xs:restriction base="xs:string"/></xs:simpleType>
                    </xs:schema>
                    """));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("https://example.test/foreign.xsd");
        await Assert.That(result.Wsdl20Operation!.SchemaElements.Single().ElementName).IsEqualTo("{urn:api}Payload");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Wsdl20UnavailableSchemaImportHintsUseResolvedNamespacesOrExplicitCatalogs(bool inline)
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:api">
              <xs:element name="Payload" type="xs:string"/>
            </xs:schema>
            """;
        var root = Document("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            "<xs:import namespace=\"urn:api\" schemaLocation=\"missing.xsd\"/>" + (inline ? schema : "") +
            "</types><interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>");
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri == "urn:api" ?
                    Encoding.UTF8.GetBytes(schema) : (ReadOnlyMemory<byte>?)null);
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected)
            .Because(result.Diagnostic?.ToString() ?? "All schema imports resolved.");
        await Assert.That(calls.Count).IsEqualTo(inline ? 1 : 2);
        await Assert.That(calls[0]).IsEqualTo("https://example.test/missing.xsd");
        if (!inline) { await Assert.That(calls[1]).IsEqualTo("urn:api"); }
        var selected = result.Wsdl20Operation!.SchemaElements.Single();
        await Assert.That(selected.ElementName).IsEqualTo("{urn:api}Payload");
        await Assert.That(selected.DocumentUri).IsEqualTo(inline ? new Uri("https://example.test/root.wsdl") : null);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Wsdl20SchemaImportsAcquireSiblingHintsBeforeTraversingNamespaceDependencies(bool reverse)
    {
        const string typesImport = "<xs:import namespace=\"urn:types\" schemaLocation=\"types.xsd\"/>";
        const string foreignImport = "<xs:import namespace=\"urn:foreign\" schemaLocation=\"foreign.xsd\"/>";
        var root = Document("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:schema targetNamespace=\"urn:api\" xmlns:q=\"urn:types\">" +
            (reverse ? foreignImport + typesImport : typesImport + foreignImport) +
            "<xs:element name=\"Payload\" type=\"q:Text\"/></xs:schema></types>" +
            "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>");
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                var schema = uri switch
                {
                    "https://example.test/types.xsd" => """
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:types" xmlns:f="urn:foreign">
                          <xs:import namespace="urn:foreign"/><xs:simpleType name="Text"><xs:restriction base="f:Base"/></xs:simpleType>
                        </xs:schema>
                        """,
                    "https://example.test/foreign.xsd" => """
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:foreign">
                          <xs:simpleType name="Base"><xs:restriction base="xs:string"/></xs:simpleType>
                        </xs:schema>
                        """,
                    _ => throw new UnauthorizedAccessException()
                };
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(schema));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.Count).IsEqualTo(2);
        await Assert.That(calls[0]).IsEqualTo(reverse ? "https://example.test/foreign.xsd" : "https://example.test/types.xsd");
        await Assert.That(calls[1]).IsEqualTo(reverse ? "https://example.test/types.xsd" : "https://example.test/foreign.xsd");
        await Assert.That(result.Wsdl20Operation!.SchemaElements.Single().ElementName).IsEqualTo("{urn:api}Payload");
        await Assert.That(result.Wsdl20Operation.SchemaElements[0].SchemaDocuments.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Wsdl20MissingNoNamespaceSchemaInformationIsIndeterminateRatherThanInvalid()
    {
        var result = await Resolve("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:import/><xs:element name="Payload" type="xs:string"/>
            </xs:schema></types><interface name="Port"><operation name="Read">
              <input element="t:Payload"/><output element="#none"/>
            </operation></interface>
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("reference.unresolved");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Wsdl20RelativeSchemaNamespaceIdentityIsNotResolvedAsARetrievalLocation(bool supplied)
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(
            "<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:import namespace=\"relative\"" +
            (supplied ? " schemaLocation=\"part.xsd\"" : "") + "/></types>" +
            "<interface name=\"Port\" xmlns:p=\"relative\"><operation name=\"Read\"><input element=\"p:Payload\"/><output element=\"#none\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="relative">
                      <xs:element name="Payload" type="xs:string"/>
                    </xs:schema>
                    """));
            });
        await Assert.That(result.Status).IsEqualTo(supplied ? ApiOperationResolutionStatus.Selected :
            ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(calls.Count).IsEqualTo(supplied ? 1 : 0);
        if (supplied)
        {
            await Assert.That(calls[0]).IsEqualTo("https://example.test/part.xsd");
            await Assert.That(result.Wsdl20Operation!.SchemaElements.Single().ElementName).IsEqualTo("{relative}Payload");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("reference.unresolved");
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Wsdl20SchemaIdFragmentsSelectCapturedInlineSchemasWithoutRetrieval(bool origin)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:schema targetNamespace="urn:api"><xs:include schemaLocation="#%70ayload"/></xs:schema>
              <xs:schema id="payload" targetNamespace="urn:api"><xs:element name="Payload" type="xs:string"/></xs:schema>
            </types><interface name="Port"><operation name="Read">
              <input element="t:Payload"/><output element="#none"/>
            </operation></interface>
            """), Reference("Port"), origin ? new Uri("https://example.test/root.wsdl") : null, (_, _) =>
            {
                calls++;
                throw new UnauthorizedAccessException();
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls).IsEqualTo(0);
        var selected = result.Wsdl20Operation!.SchemaElements.Single();
        await Assert.That(selected.ElementName).IsEqualTo("{urn:api}Payload");
        await Assert.That(selected.SchemaDocuments.Count).IsEqualTo(2);
        await Assert.That(selected.DocumentUri).IsEqualTo(origin ? new Uri("https://example.test/root.wsdl") : null);
    }

    [Test]
    public async Task Wsdl20ExternalSchemaFragmentsShareRetrievalAndPreserveContainingXmlBase()
    {
        var calls = new List<string>();
        var container = Encoding.UTF8.GetBytes("""
            <description xmlns="http://www.w3.org/ns/wsdl" xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:other">
              <types xml:base="schemas/">
                <xs:schema id="payload" targetNamespace="urn:api"><xs:include schemaLocation="part.xsd"/></xs:schema>
                <xs:schema id="extra" targetNamespace="urn:api"><xs:element name="Extra" type="xs:string"/></xs:schema>
              </types>
            </description>
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:include schemaLocation="other.wsdl#payload"/><xs:include schemaLocation="other.wsdl#extra"/>
            </xs:schema></types>
            <interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="t:Extra"/></operation></interface>
            """), Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri switch
                {
                    "https://example.test/other.wsdl" => container,
                    "https://example.test/schemas/part.xsd" => Encoding.UTF8.GetBytes("""
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:api">
                          <xs:element name="Payload" type="xs:string"/>
                        </xs:schema>
                        """),
                    _ => throw new UnauthorizedAccessException()
                });
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.SequenceEqual(["https://example.test/other.wsdl", "https://example.test/schemas/part.xsd"])).IsTrue();
        Array.Fill(container, (byte)'!');
        await Assert.That(result.Wsdl20Operation!.SchemaElements.Select(element => element.ElementName)
            .SequenceEqual(["{urn:api}Payload", "{urn:api}Extra"])).IsTrue();
        await Assert.That(result.Wsdl20Operation.SchemaElements[0].DocumentUri).IsEqualTo(new Uri(calls[1]));
        await Assert.That(result.Wsdl20Operation.SchemaElements[1].DocumentUri).IsEqualTo(new Uri(calls[0]));
        var schemas = result.Wsdl20Operation.SchemaElements[0].SchemaDocuments.Select(XElement.Parse).ToArray();
        await Assert.That(schemas.Length).IsEqualTo(4);
        await Assert.That(schemas.Single(schema => schema.Attribute("id")?.Value == "payload")
            .Attribute(XNamespace.Xml + "base")!.Value).IsEqualTo("https://example.test/schemas/");
    }

    [Test]
    [Arguments("<xs:schema id=\"part\" targetNamespace=\"urn:api\"/><xs:schema id=\"part\" targetNamespace=\"urn:api\"/>",
        ApiOperationResolutionStatus.Invalid, "wsdl20.schema_fragment_duplicate")]
    [Arguments("<xs:element id=\"part\" name=\"Wrong\"/>", ApiOperationResolutionStatus.Invalid, "xsd.root")]
    [Arguments("<xs:schema id=\"different\" targetNamespace=\"urn:api\"/>", ApiOperationResolutionStatus.Indeterminate, "reference.unresolved")]
    public async Task Wsdl20SchemaFragmentsRejectAmbiguousWrongKindAndMissingTargets(string target,
        ApiOperationResolutionStatus status, string code)
    {
        var result = await Resolve("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            "<xs:schema targetNamespace=\"urn:api\"><xs:include schemaLocation=\"#part\"/></xs:schema>" + target +
            "</types><interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>");
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Wsdl20FragmentAndWholeSchemaReferencesReuseOneCapturedRetrieval(bool fragmentFirst)
    {
        var calls = 0;
        const string fragment = "<xs:include schemaLocation=\"part.xsd#schema\"/>";
        const string whole = "<xs:include schemaLocation=\"part.xsd\"/>";
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(
            "<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:schema targetNamespace=\"urn:api\">" +
            (fragmentFirst ? fragment + whole : whole + fragment) + "</xs:schema></types>" +
            "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xml:id="schema" targetNamespace="urn:api">
                      <xs:element name="Payload" type="xs:string"/>
                    </xs:schema>
                    """));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Wsdl20Operation!.SchemaElements.Single().SchemaDocuments.Count).IsEqualTo(2);
        await Assert.That(result.Wsdl20Operation.SchemaElements[0].DocumentUri)
            .IsEqualTo(new Uri("https://example.test/part.xsd"));
    }

    [Test]
    public async Task Wsdl20FragmentOnlyReferencesRespectInheritedXmlBaseRatherThanTheAuthoredContainer()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema" xml:base="other.xml">
              <xs:schema targetNamespace="urn:api"><xs:include schemaLocation="#part"/></xs:schema>
            </types><interface name="Port"><operation name="Read">
              <input element="t:Payload"/><output element="#none"/>
            </operation></interface>
            """), Reference("Port"), new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                    <container xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema id="part" targetNamespace="urn:api">
                      <xs:element name="Payload" type="xs:string"/>
                    </xs:schema></container>
                    """));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo("https://example.test/other.xml");
        await Assert.That(result.Wsdl20Operation!.SchemaElements.Single().DocumentUri).IsEqualTo(new Uri(calls[0]));
    }

    [Test]
    [Arguments("#bad%GG", ApiOperationResolutionStatus.Invalid, "dependency.uri")]
    [Arguments("#bad%2Fid", ApiOperationResolutionStatus.Invalid, "wsdl.xml")]
    [Arguments("#", ApiOperationResolutionStatus.Unsupported, "wsdl20.schema_fragment")]
    [Arguments("#xpointer(/1)", ApiOperationResolutionStatus.Unsupported, "wsdl20.schema_fragment")]
    public async Task Wsdl20SchemaFragmentSyntaxFailuresDoNotAcquireDocuments(string location,
        ApiOperationResolutionStatus status, string code)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(
            "<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:schema targetNamespace=\"urn:api\">" +
            "<xs:include schemaLocation=\"" + location + "\"/></xs:schema></types>" +
            "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                throw new UnauthorizedAccessException();
            });
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments(32)]
    [Arguments(33)]
    public async Task Wsdl20SchemaFragmentContainersShareTheDistinctAcquisitionLimit(int count)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(
            "<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:schema targetNamespace=\"urn:api\">" +
            string.Concat(Enumerable.Range(0, count).Select(index => "<xs:include schemaLocation=\"p" + index + ".xml#s\"/>")) +
            "<xs:element name=\"Payload\" type=\"xs:string\"/></xs:schema></types>" +
            "<interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                    <container xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema id="s" targetNamespace="urn:api"/></container>
                    """));
            });
        await Assert.That(calls).IsEqualTo(32);
        await Assert.That(result.Status).IsEqualTo(count == 32 ? ApiOperationResolutionStatus.Selected :
            ApiOperationResolutionStatus.Indeterminate);
        if (count == 33)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.references");
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
        else
        {
            await Assert.That(result.Wsdl20Operation!.SchemaElements.Single().SchemaDocuments.Count).IsEqualTo(33);
        }
    }

    [Test]
    public async Task Wsdl20ExternalSchemaFragmentFailuresPreserveDependencyPolicyAndCancellation()
    {
        var root = Document("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:schema targetNamespace="urn:api">
              <xs:include schemaLocation="part.xml#schema"/><xs:element name="Payload" type="xs:string"/>
            </xs:schema></types><interface name="Port"><operation name="Read">
              <input element="t:Payload"/><output element="#none"/>
            </operation></interface>
            """);
        var missingBase = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"));
        await Assert.That(missingBase.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missingBase.Diagnostic!.Code).IsEqualTo("dependency.base");
        var origin = new Uri("https://example.test/root.wsdl");
        var missing = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"), origin);
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missing.Diagnostic!.Code).IsEqualTo("reference.unresolved");
        var denied = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"), origin,
            (_, _) => throw new UnauthorizedAccessException());
        await Assert.That(denied.Status).IsEqualTo(ApiOperationResolutionStatus.Denied);
        await Assert.That(denied.Diagnostic!.Code).IsEqualTo("dependency.denied");
        using var cancellation = new CancellationTokenSource();
        await Assert.That(async () => await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root,
            Reference("Port"), origin, (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            }, cancellation.Token)).Throws<OperationCanceledException>();
        foreach (var result in new[] { missingBase, missing, denied })
        {
            await Assert.That(result.Wsdl20Operation).IsNull();
        }
    }

    [Test]
    [Arguments("element(/1/2/2)")]
    [Arguments("element(types/2)")]
    [Arguments("element(payload)")]
    [Arguments("element(missing)element(types/2)")]
    [Arguments("element(/2)element(/1/2/2)")]
    [Arguments("element(/1/2/9999999999999999999999999999)element(payload)")]
    [Arguments("element(/1/02)element(payload)")]
    [Arguments("element(types/0)element(payload)")]
    [Arguments("element()element(payload)")]
    [Arguments("xpointer(id('missing'))element(payload)")]
    [Arguments("unknown(a%5E(b%5E)%5E%5E)element(payload)")]
    [Arguments("p:element(/1)element(payload)")]
    [Arguments("element(missing)%20element(payload)")]
    public async Task Wsdl20ElementSchemeUsesNativeChildOrdinalsAndFirstSuccessfulPointerPart(string fragment)
    {
        var calls = 0;
        var root = Document("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            "<xs:import namespace=\"urn:payload\" schemaLocation=\"container.xml#" + fragment + "\"/></types>" +
            "<interface name=\"Port\" xmlns:p=\"urn:payload\"><operation name=\"Read\"><input element=\"p:Payload\"/><output element=\"#none\"/></operation></interface>");
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", root, Reference("Port"),
            new Uri("https://example.test/root.wsdl"), (uri, _) =>
            {
                calls++;
                if (uri != "https://example.test/container.xml") { throw new UnauthorizedAccessException(); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                    <container xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      <documentation/>
                      <!-- comments and text are not child elements -->
                      <types xml:id="types">text<?instruction ignored?>
                        <documentation/>
                        <xs:schema id="payload" targetNamespace="urn:payload"><xs:element name="Payload" type="xs:string"/></xs:schema>
                      </types>
                    </container>
                    """));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls).IsEqualTo(1);
        var selected = result.Wsdl20Operation!.SchemaElements.Single();
        await Assert.That(selected.ElementName).IsEqualTo("{urn:payload}Payload");
        await Assert.That(selected.DocumentUri).IsEqualTo(new Uri("https://example.test/container.xml"));
        await Assert.That(selected.SchemaDocuments.Count).IsEqualTo(1);
        await Assert.That(XElement.Parse(selected.SchemaDocuments[0]).Attribute("id")!.Value).IsEqualTo("payload");
    }

    [Test]
    [Arguments("element(/1/0)")]
    [Arguments("element(/1/01)")]
    [Arguments("element(/1/-1)")]
    [Arguments("element(/1/+1)")]
    [Arguments("element(/1/1/)")]
    [Arguments("element(/1/99999999999999999999999999999999)")]
    [Arguments("element(missing)")]
    [Arguments("element(bad:name)")]
    [Arguments("element()")]
    [Arguments("element(%20/1)")]
    public async Task Wsdl20NonMatchingElementSchemeDataNeverSelectsAnApproximateSchema(string fragment)
    {
        var result = await Resolve("<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
            "<xs:schema targetNamespace=\"urn:api\"><xs:include schemaLocation=\"#" + fragment + "\"/></xs:schema>" +
            "<xs:schema id=\"payload\" targetNamespace=\"urn:api\"><xs:element name=\"Payload\" type=\"xs:string\"/></xs:schema>" +
            "</types><interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("reference.unresolved");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    [Arguments("element(/1")]
    [Arguments("unknown(data%5Ex)element(/1)")]
    [Arguments("unknown(data%5E)")]
    [Arguments("element(/1))")]
    [Arguments("element(/1)%20")]
    public async Task Wsdl20MalformedSchemeFrameworkSyntaxPreventsSchemaRetrieval(string fragment)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(
            "<types xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:import namespace=\"urn:api\" schemaLocation=\"part.xml#" +
            fragment + "\"/></types><interface name=\"Port\"><operation name=\"Read\"><input element=\"t:Payload\"/><output element=\"#none\"/></operation></interface>"),
            Reference("Port"), new Uri("https://example.test/root.wsdl"), (_, _) =>
            {
                calls++;
                throw new UnauthorizedAccessException();
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("xpointer.syntax");
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    public async Task Wsdl20ElementSchemeStopsAtFirstLocatedElementEvenWhenItIsNotASchema()
    {
        var result = await Resolve("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:schema targetNamespace="urn:api"><xs:include schemaLocation="#element(/1)element(payload)"/></xs:schema>
              <xs:schema id="payload" targetNamespace="urn:api"><xs:element name="Payload" type="xs:string"/></xs:schema>
            </types><interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("xsd.root");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    [Test]
    public async Task Wsdl20ElementSchemePreservesTheExplicitDuplicateIdRejectionPolicy()
    {
        var result = await Resolve("""
            <types xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:schema targetNamespace="urn:api"><xs:include schemaLocation="#element(payload)"/></xs:schema>
              <xs:schema id="payload" targetNamespace="urn:api"><xs:element name="Payload" type="xs:string"/></xs:schema>
              <xs:schema xml:id="payload" targetNamespace="urn:api"/>
            </types><interface name="Port"><operation name="Read"><input element="t:Payload"/><output element="#none"/></operation></interface>
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl20.schema_fragment_duplicate");
        await Assert.That(result.Wsdl20Operation).IsNull();
    }

    private static ValueTask<ApiOperationResolutionResult> Resolve(string interfaces) =>
        ApiOperationDocumentResolver.ResolveAsync("WSDL/2.0", "application/xml", Document(interfaces), Reference("Port"));

    private static ApiOperationReference Reference(string name) => ApiOperationReference.Parse(
        "/apigroups/g/apis/a#interfaces/%7Burn:api%7D" + name + "/operations/%7Burn:api%7DRead");

    private static byte[] Document(string content, string targetNamespace = "urn:api") => Encoding.UTF8.GetBytes(
        "<description xmlns=\"http://www.w3.org/ns/wsdl\" xmlns:t=\"" + targetNamespace + "\" targetNamespace=\"" + targetNamespace + "\">" +
        content + "</description>");
}
