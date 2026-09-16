// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class SchemaObjectXmlSelectionTests
{
    private const string Schema = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:events" targetNamespace="urn:events">
          <xs:annotation><xs:documentation>Example</xs:documentation></xs:annotation>
          <xs:simpleType name="Count"><xs:restriction base="xs:int"/></xs:simpleType>
          <xs:complexType name="Record"><xs:sequence><xs:element name="Child" type="xs:string"/></xs:sequence></xs:complexType>
          <xs:element name="Event" type="t:Record"/>
        </xs:schema>
        """;

    [Test]
    [Arguments("", "{urn:events}Event", SchemaTypeKind.XmlSchemaElement)]
    [Arguments("/xs:schema/xs:element[@name='Event']", "{urn:events}Event", SchemaTypeKind.XmlSchemaElement)]
    [Arguments("//xsd:simpleType[@name=\"Count\"]", "{urn:events}Count", SchemaTypeKind.XmlSchemaType)]
    [Arguments("/xs:schema/xs:*[ @name = 'Record' ]", "{urn:events}Record", SchemaTypeKind.XmlSchemaType)]
    public async Task XmlDefaultAndStructuralSelectionsUseExactDeclarations(
        string expression, string name, SchemaTypeKind kind)
    {
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(Schema),
            expression.Length == 0 ? "events.xsd" : "events.xsd#" + expression);

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo(name);
        await Assert.That(result.Selection.Kind).IsEqualTo(kind);
        await Assert.That(result.Selection.Xml!.Attribute("name")!.Value).IsEqualTo(name[(name.IndexOf('}') + 1)..]);
    }

    [Test]
    [Arguments("/xs:schema/xs:element[@name='Missing']", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("/schema/element", SchemaObjectSelectionStatus.NotFound, "selection.not_found")]
    [Arguments("//xs:*", SchemaObjectSelectionStatus.Ambiguous, "selection.ambiguous")]
    [Arguments("/xs:schema", SchemaObjectSelectionStatus.Invalid, "selection.not_type")]
    [Arguments("/xs:schema/xs:annotation", SchemaObjectSelectionStatus.Invalid, "selection.not_type")]
    [Arguments("//xs:element[@name='Child']", SchemaObjectSelectionStatus.Unsupported, "selection.xsd_scope")]
    [Arguments("/s:schema/s:element", SchemaObjectSelectionStatus.Invalid, "selection.namespace")]
    [Arguments("/", SchemaObjectSelectionStatus.Invalid, "selection.xpath")]
    [Arguments("/xs:schema/", SchemaObjectSelectionStatus.Invalid, "selection.xpath")]
    public async Task XmlSelectorsDistinguishCardinalityKindScopeAndNamespaceFailures(
        string expression, SchemaObjectSelectionStatus expected, string code)
    {
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(Schema), "events.xsd#" + expression);

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("//xs:element[1]")]
    [Arguments("//xs:element[position()=1]")]
    [Arguments("/xs:schema//xs:element")]
    [Arguments("//*[@name='Event' or @name='Count']")]
    [Arguments("/xs:schema/xs:element[@name='Event'][1]")]
    [Arguments("/xs:schema/xs:element | //xs:complexType")]
    [Arguments("//*[local-name()='element']")]
    [Arguments("/xs:schema/descendant::xs:element")]
    [Arguments("id('Event')")]
    [Arguments("//xs:element[@type='xs:string']")]
    public async Task XmlUnsafeOrUnimplementedExpressionsAreNeverEvaluatedOrPartiallyIgnored(string expression)
    {
        var calls = 0;
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(Schema), "events.xsd#" + expression,
            new()
            {
                Validation = new()
                {
                    ResolveReference = (_, _) =>
                    {
                        calls++;
                        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
                    },
                },
            });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Unsupported);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.xpath_unsupported");
        await Assert.That(result.Selection).IsNull();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task XmlWithoutElementsUsesOnlyAUniqueGlobalType()
    {
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", """
            <schema xmlns="http://www.w3.org/2001/XMLSchema">
              <simpleType name="Value"><restriction base="string"/></simpleType>
            </schema>
            """u8.ToArray(), "types.xsd");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.Kind).IsEqualTo(SchemaTypeKind.XmlSchemaType);
        await Assert.That(result.Selection.TypeName).IsEqualTo("Value");
    }

    [Test]
    [Arguments("", SchemaObjectSelectionStatus.NotFound)]
    [Arguments("""<xs:element name="A" type="xs:string"/><xs:element name="B" type="xs:string"/>""", SchemaObjectSelectionStatus.Ambiguous)]
    [Arguments("""<xs:complexType name="A"/><xs:complexType name="B"/>""", SchemaObjectSelectionStatus.Ambiguous)]
    public async Task XmlDefaultNeverChoosesAnArbitraryFirstDeclaration(string declarations, SchemaObjectSelectionStatus expected)
    {
        var schema = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">""" + declarations + "</xs:schema>";
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(schema), "types.xsd");

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Selection).IsNull();
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo(
            expected == SchemaObjectSelectionStatus.NotFound ? "selection.not_found" : "selection.ambiguous");
    }

    [Test]
    public async Task XmlWildcardNamePredicateReportsBothTypeAndElementMatches()
    {
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="Same"/><xs:element name="Same" type="Same"/>
            </xs:schema>
            """u8.ToArray(), "schema.xsd#/xs:schema/*[@name='Same']");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Ambiguous);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.ambiguous");
    }

    [Test]
    public async Task XmlNamespaceBindingsMatchExpandedNamesNotDocumentPrefixes()
    {
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(Schema),
            "schema.xsd#/xs:schema/xs:element", new()
            {
                XmlNamespaces = new Dictionary<string, string>(StringComparer.Ordinal) { ["xs"] = "urn:wrong" },
            });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.NotFound);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("selection.not_found");
    }

    [Test]
    [Arguments(1, SchemaObjectSelectionStatus.Indeterminate)]
    [Arguments(2, SchemaObjectSelectionStatus.Selected)]
    public async Task XmlStructuralStepLimitIsInclusive(int limit, SchemaObjectSelectionStatus expected)
    {
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(Schema),
            "schema.xsd#/xs:schema/xs:element", new() { MaxSelectorSegments = limit });

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == SchemaObjectSelectionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.selector_steps");
        }
        else
        {
            await Assert.That(result.Selection!.TypeName).IsEqualTo("{urn:events}Event");
        }
    }

    [Test]
    [Arguments(31, SchemaObjectSelectionStatus.Selected)]
    [Arguments(32, SchemaObjectSelectionStatus.Selected)]
    [Arguments(33, SchemaObjectSelectionStatus.Indeterminate)]
    public async Task XmlNamespaceBindingCountHasAnInclusiveBound(int count, SchemaObjectSelectionStatus expected)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++) { bindings["n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "urn:test"; }
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(Schema),
            "schema.xsd#/xs:schema/xs:element", new() { XmlNamespaces = bindings });

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == SchemaObjectSelectionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.namespaces");
        }
        else
        {
            await Assert.That(result.Selection!.TypeName).IsEqualTo("{urn:events}Event");
        }
    }

    [Test]
    public async Task XmlNamespaceLengthIsBoundedBeforeLexicalValidation()
    {
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(Schema), "s", new()
        {
            MaxSelectorLength = 8,
            XmlNamespaces = new Dictionary<string, string>(StringComparer.Ordinal) { ["invalid:long-prefix"] = "urn:test" },
        });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.namespace");
    }

    [Test]
    public async Task XmlOccurrenceExpansionCannotEscapeTheSharedWorkBudget()
    {
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Root"><xs:complexType><xs:sequence>
                <xs:element name="Value" type="xs:string" maxOccurs="1000000000"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """u8.ToArray(), "schema.xsd");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.work");
        await Assert.That(result.Selection).IsNull();
    }

    [Test]
    public async Task XmlExplicitIncludeIsOwnedContextNotAnImplicitSecondSelection()
    {
        const string dependency = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:simpleType name="Value"><xs:restriction base="xs:string"/></xs:simpleType></xs:schema>
            """;
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:include schemaLocation="types.xsd"/><xs:element name="Root" type="Value"/>
            </xs:schema>
            """u8.ToArray(), "schema.xsd", new()
        {
            Validation = new()
            {
                ResolveReference = (name, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    name == "types.xsd" ? Encoding.UTF8.GetBytes(dependency) : null),
            },
        });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.TypeName).IsEqualTo("Root");
        await Assert.That(result.Selection.Kind).IsEqualTo(SchemaTypeKind.XmlSchemaElement);
        await Assert.That(Encoding.UTF8.GetString(result.Selection.References["types.xsd"].Span)).IsEqualTo(dependency);
    }

    [Test]
    public async Task XmlDtdsAreProhibitedWithoutCallingAnExplicitResolver()
    {
        var calls = 0;
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", """
            <!DOCTYPE schema [<!ENTITY external SYSTEM "https://must-not-fetch.invalid/entity">]>
            <schema xmlns="http://www.w3.org/2001/XMLSchema">&external;</schema>
            """u8.ToArray(), "https://must-not-fetch.invalid/schema", new()
        {
            Validation = new()
            {
                ResolveReference = (_, _) =>
                {
                    calls++;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
                },
            },
        });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("xml.syntax");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task XmlLocalAppendedPathAndReturnedNodesPreserveTheirOwnContext()
    {
        const string reference = "#/schemagroups/g:one/schemas/xsd:two/versions/v:3";
        var bytes = Encoding.UTF8.GetBytes(Schema);
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", bytes,
            reference + "/xs:schema/xs:element[@name='Event']", new() { DocumentReference = reference });
        Array.Fill(bytes, (byte)'!');
        var firstCopy = result.Selection!.Xml!;
        firstCopy.SetAttributeValue("name", "Changed");
        firstCopy.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "t", "urn:changed");

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection.DocumentReference).IsEqualTo(reference);
        await Assert.That(result.Selection.Xml!.Attribute("name")!.Value).IsEqualTo("Event");
        await Assert.That(result.Selection.Xml!.GetNamespaceOfPrefix("t")!.NamespaceName).IsEqualTo("urn:events");
        await Assert.That(Encoding.UTF8.GetString(result.Selection.Document.Span)).IsEqualTo(Schema);
    }

    [Test]
    public async Task XmlSchemaXPathSelectsAGlobalTypeByNamespaceAndRetainsBindings()
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:tns="urn:telemetry" targetNamespace="urn:telemetry">
              <xs:simpleType name="Count"><xs:restriction base="xs:int"/></xs:simpleType>
              <xs:complexType name="Telemetry"><xs:sequence><xs:element name="reading" type="tns:Count"/></xs:sequence></xs:complexType>
              <xs:element name="Event" type="tns:Telemetry"/>
            </xs:schema>
            """;
        var result = await SchemaObjectSelector.SelectAsync("XSD/1.0", Encoding.UTF8.GetBytes(schema),
            "https://schemas.test/event.xsd#/%73:schema/s:complexType%5B%40name%3D%27Telemetry%27%5D",
            new()
            {
                XmlNamespaces = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["s"] = "http://www.w3.org/2001/XMLSchema",
                },
            });

        await Assert.That(result.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(result.Selection!.Kind).IsEqualTo(SchemaTypeKind.XmlSchemaType);
        await Assert.That(result.Selection.TypeName).IsEqualTo("{urn:telemetry}Telemetry");
        await Assert.That(result.Selection.XPath).IsEqualTo("/s:schema/s:complexType[@name='Telemetry']");
        await Assert.That(result.Selection.JsonPointer).IsNull();
        var xml = result.Selection.Xml!;
        await Assert.That(xml.Name.LocalName).IsEqualTo("complexType");
        await Assert.That(xml.Attribute("name")!.Value).IsEqualTo("Telemetry");
        await Assert.That(xml.GetNamespaceOfPrefix("tns")!.NamespaceName).IsEqualTo("urn:telemetry");
        await Assert.That(Encoding.UTF8.GetString(result.Selection.Document.Span)).IsEqualTo(schema);
    }
}
