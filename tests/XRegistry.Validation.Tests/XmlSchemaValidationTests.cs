// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class XmlSchemaValidationTests
{
    [Test]
    [Arguments("""<xs:union memberTypes="xs:int xs:string"/>""")]
    [Arguments("""<xs:union memberTypes=" xs:int  xs:string "/>""")]
    [Arguments("""<xs:union><xs:simpleType><xs:restriction base="xs:int"/></xs:simpleType><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="unbounded"/></xs:restriction></xs:simpleType></xs:union>""")]
    [Arguments("""<xs:union memberTypes="xs:int"><xs:simpleType><xs:list itemType="xs:string"/></xs:simpleType></xs:union>""")]
    [Arguments("""<xs:union><xs:simpleType><xs:union memberTypes="xs:int xs:boolean"/></xs:simpleType><xs:simpleType><xs:restriction base="xs:string"/></xs:simpleType></xs:union>""")]
    public async Task UnionSimpleTypesCompileNativeNamedInlineListAndNestedMembers(string union)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:simpleType name=\"Value\">" +
            union + "</xs:simpleType><xs:element name=\"value\" type=\"Value\"/></xs:schema>"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""<xs:simpleType name="Value"><xs:union/></xs:simpleType>""")]
    [Arguments("""<xs:simpleType name="Value"><xs:union memberTypes="xs:notAType"/></xs:simpleType>""")]
    [Arguments("""<xs:simpleType name="Value"><xs:union memberTypes="missing:int"/></xs:simpleType>""")]
    [Arguments("""<xs:complexType name="Complex"/><xs:simpleType name="Value"><xs:union memberTypes="Complex xs:int"/></xs:simpleType>""")]
    [Arguments("""<xs:simpleType name="Value"><xs:union memberTypes="Value xs:int"/></xs:simpleType>""")]
    [Arguments("""<xs:simpleType name="Value"><xs:union><xs:element name="bad"/></xs:union></xs:simpleType>""")]
    public async Task UnionMemberContradictionsRemainNativeSchemaErrors(string declarations)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" + declarations + "</xs:schema>"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("xsd.schema");
    }

    [Test]
    [Arguments("12", DocumentValidationStatus.Valid)]
    [Arguments("true", DocumentValidationStatus.Valid)]
    [Arguments("neither", DocumentValidationStatus.Invalid)]
    public async Task UnionSchemaDefaultsMustMatchAnActualNativeMember(string value, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes($"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="value" default="{value}"><xs:simpleType><xs:union memberTypes="xs:int xs:boolean"/></xs:simpleType></xs:element>
            </xs:schema>
            """));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Invalid)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("xsd.schema");
        }
    }

    [Test]
    [Arguments(2, DocumentValidationStatus.Valid)]
    [Arguments(15, DocumentValidationStatus.Indeterminate)]
    public async Task InlineUnionMembersAlsoContributeToCumulativeExpansionReservations(
        int count, DocumentValidationStatus expected)
    {
        var declarations = string.Concat(Enumerable.Range(0, count).Select(index =>
            $"<xs:simpleType name=\"Union{index}\"><xs:union><xs:simpleType><xs:restriction base=\"xs:int\"/></xs:simpleType>" +
            "<xs:simpleType><xs:restriction base=\"xs:boolean\"/></xs:simpleType></xs:union></xs:simpleType>"));
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" + declarations + "</xs:schema>"));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.work");
        }
    }

    [Test]
    [Arguments(2, DocumentValidationStatus.Valid)]
    [Arguments(20, DocumentValidationStatus.Indeterminate)]
    public async Task RepeatedUnionReferencesReserveTheirTransitiveExpansionBeforeCompilation(
        int depth, DocumentValidationStatus expected)
    {
        var declarations = new StringBuilder();
        for (var index = 0; index < depth; index++)
        {
            var member = index == 0 ? "xs:int" : "Union" + (index - 1);
            declarations.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"<xs:simpleType name=\"Union{index}\"><xs:union memberTypes=\"{member} {member}\"/></xs:simpleType>");
        }
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" + declarations + "</xs:schema>"));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.work");
        }
    }

    [Test]
    public async Task UnionMembersCannotHideUnsupportedPatternsOrTriggerDependencyAcquisition()
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:include schemaLocation="never.xsd"/>
              <xs:simpleType name="Value"><xs:union memberTypes="xs:int">
                <xs:simpleType><xs:restriction base="xs:string"><xs:pattern value=".*"/></xs:restriction></xs:simpleType>
              </xs:union></xs:simpleType>
            </xs:schema>
            """), new() { ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); } });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("xsd.construct_unsupported");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task UnionNamedMembersResolveOnlyThroughExplicitSchemaImports()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:v="urn:value">
              <xs:import namespace="urn:value" schemaLocation="value.xsd"/>
              <xs:simpleType name="Value"><xs:union memberTypes="v:Integer xs:boolean"/></xs:simpleType>
            </xs:schema>
            """), new()
        {
            DocumentUri = new Uri("https://schemas.test/root.xsd"),
            ResolveReference = (reference, _) =>
            {
                calls.Add(reference);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:value">
                          <xs:simpleType name="Integer"><xs:restriction base="xs:int"/></xs:simpleType>
                        </xs:schema>
                        """));
            }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/value.xsd"])).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("minOccurs=\"0\"")]
    [Arguments("minOccurs=\"0\" maxOccurs=\"1\"")]
    public async Task AllContentModelsCompileWithNativeOptionalAndRequiredElements(string occurrence)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes($"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="person"><xs:complexType><xs:all {occurrence}>
                <xs:element name="name" type="xs:string"/>
                <xs:element name="age" type="xs:int" minOccurs="0"/>
              </xs:all></xs:complexType></xs:element>
            </xs:schema>
            """));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("<xs:all maxOccurs=\"2\"><xs:element name=\"x\"/></xs:all>")]
    [Arguments("<xs:all minOccurs=\"2\"><xs:element name=\"x\"/></xs:all>")]
    [Arguments("<xs:all><xs:element name=\"x\" maxOccurs=\"2\"/></xs:all>")]
    [Arguments("<xs:all><xs:sequence><xs:element name=\"x\"/></xs:sequence></xs:all>")]
    [Arguments("<xs:sequence><xs:all><xs:element name=\"x\"/></xs:all></xs:sequence>")]
    [Arguments("<xs:all><xs:element name=\"x\"/><xs:element name=\"x\"/></xs:all>")]
    public async Task AllContentModelsRejectInvalidNativeParticleConstraints(string content)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"person\"><xs:complexType>" +
            content + "</xs:complexType></xs:element></xs:schema>"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("xsd.schema");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(32)]
    [Arguments(128)]
    public async Task AllContentModelsUseBoundedNativeCompilationAcrossEmptyAndLargerGroups(int count)
    {
        var elements = string.Concat(Enumerable.Range(0, count).Select(index =>
            $"<xs:element name=\"child{index}\" type=\"xs:string\" minOccurs=\"0\"/>"));
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"root\"><xs:complexType><xs:all>" +
            elements + "</xs:all></xs:complexType></xs:element></xs:schema>"));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("minOccurs", "18446744073709551616")]
    [Arguments("maxOccurs", "18446744073709551616")]
    [Arguments("maxOccurs", "+18446744073709551616")]
    [Arguments("maxOccurs", "  18446744073709551616  ")]
    [Arguments("minOccurs", "00018446744073709551616")]
    public async Task ArbitraryWidthOccurrenceIntegersCannotBypassPreflightWorkReservation(string attribute, string value)
    {
        var additional = attribute == "minOccurs" ? "maxOccurs=\"unbounded\"" : "";
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes($"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="person"><xs:complexType><xs:sequence>
                <xs:element name="x" {attribute}="{value}" {additional}/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.work");
    }

    [Test]
    [Arguments("0", DocumentValidationStatus.Valid)]
    [Arguments("1", DocumentValidationStatus.Valid)]
    [Arguments("+2", DocumentValidationStatus.Valid)]
    [Arguments("  002  ", DocumentValidationStatus.Valid)]
    [Arguments("-0", DocumentValidationStatus.Valid)]
    [Arguments("-1", DocumentValidationStatus.Invalid)]
    [Arguments("+", DocumentValidationStatus.Invalid)]
    [Arguments("1e2", DocumentValidationStatus.Invalid)]
    [Arguments("1.0", DocumentValidationStatus.Invalid)]
    [Arguments("1 0", DocumentValidationStatus.Invalid)]
    public async Task OccurrenceParsingRetainsXmlIntegerLexicalSemantics(string value, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes($"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="person"><xs:complexType><xs:sequence>
                <xs:element name="x" minOccurs="0" maxOccurs="{value}"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Invalid)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("xsd.schema");
        }
    }

    [Test]
    [Arguments(1, DocumentValidationStatus.Valid)]
    [Arguments(999, DocumentValidationStatus.Indeterminate)]
    [Arguments(1000, DocumentValidationStatus.Indeterminate)]
    public async Task OccurrenceReservationChecksBothNumericAndStructuralWorkBeforeNativeCompilation(
        int repetitions, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes($"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="person"><xs:complexType><xs:sequence>
                <xs:element name="x" maxOccurs="{repetitions}"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """), new() { MaxWork = 999 });
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == DocumentValidationStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.work");
        }
    }

    [Test]
    [Arguments("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="age" type="xs:int"/></xs:schema>""", DocumentValidationStatus.Valid)]
    [Arguments("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="p"><xs:complexType><xs:sequence><xs:element name="name" type="xs:string"/><xs:element name="age" type="xs:int" minOccurs="0"/></xs:sequence></xs:complexType></xs:element></xs:schema>""", DocumentValidationStatus.Valid)]
    [Arguments("""<person/>""", DocumentValidationStatus.Invalid)]
    [Arguments("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="age" type="xs:notAType"/></xs:schema>""", DocumentValidationStatus.Invalid)]
    [Arguments("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="age" type="xs:int"/><xs:element name="age" type="xs:string"/></xs:schema>""", DocumentValidationStatus.Invalid)]
    [Arguments("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="p"><xs:complexType><xs:sequence><xs:element name="x" minOccurs="2" maxOccurs="1"/></xs:sequence></xs:complexType></xs:element></xs:schema>""", DocumentValidationStatus.Invalid)]
    [Arguments("""<!DOCTYPE schema [<!ENTITY x SYSTEM "file:///C:/secret">]><schema xmlns="http://www.w3.org/2001/XMLSchema">&x;</schema>""", DocumentValidationStatus.Invalid)]
    [Arguments("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:simpleType name="S"><xs:restriction base="xs:string"><xs:pattern value="(a+)+"/></xs:restriction></xs:simpleType></xs:schema>""", DocumentValidationStatus.Unsupported)]
    [Arguments("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:include schemaLocation="https://schemas.test/external.xsd"/></xs:schema>""", DocumentValidationStatus.Indeterminate)]
    public async Task SchemaCompilationIsHardenedAndNotJustXmlParsing(string schema, DocumentValidationStatus expected)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(schema));

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected != DocumentValidationStatus.Valid)
        {
            await Assert.That(result.Diagnostics[0].Detail.Length).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task ExplicitIncludeProvidesActualTypeDeclarations()
    {
        var schema = Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:include xml:base="" schemaLocation="types.xsd"/>
              <xs:element name="person" type="Name"/>
            </xs:schema>
            """);
        var result = await new BuiltInDocumentValidator().ValidateAsync("xsd/1.0", schema,
            new DocumentValidationOptions
            {
                ResolveReference = (reference, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    reference == "types.xsd"
                        ? Encoding.UTF8.GetBytes("""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:simpleType name="Name"><xs:restriction base="xs:string"/></xs:simpleType></xs:schema>""")
                        : null),
            });

        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
    }

    [Test]
    [Arguments("https://schemas.test/root.xsd", "../shared/", "v1/", "https://schemas.test/shared/v1/types.xsd")]
    [Arguments(null, "https://schemas.test/shared/", "v1/", "https://schemas.test/shared/v1/types.xsd")]
    [Arguments("https://schemas.test/root.xsd", "", "//other.test/shared/", "https://other.test/shared/types.xsd")]
    [Arguments("https://schemas.test/root.xsd", "/shared/", "", "https://schemas.test/shared/types.xsd")]
    public async Task SchemaLocationsUseInheritedAndDeclarationXmlBases(
        string? origin, string rootBase, string declarationBase, string expected)
    {
        var calls = new List<string>();
        var schema = Encoding.UTF8.GetBytes($"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xml:base="{rootBase}">
              <xs:include xml:base="{declarationBase}" schemaLocation="types.xsd"/>
              <xs:element name="person" type="Name"/>
            </xs:schema>
            """);
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", schema, new()
        {
            DocumentUri = origin is null ? null : new Uri(origin),
            ResolveReference = (reference, _) =>
            {
                calls.Add(reference);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(reference == expected ?
                    Encoding.UTF8.GetBytes("""
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:simpleType name="Name"><xs:restriction base="xs:string"/></xs:simpleType></xs:schema>
                        """) : (ReadOnlyMemory<byte>?)null);
            }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(calls.SequenceEqual([expected])).IsTrue();
    }

    [Test]
    public async Task ImportedSchemaDependenciesUseTheirOwnRetrievedOrigin()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:p="urn:person">
              <xs:import namespace="urn:person" schemaLocation="parts/person.xsd"/>
              <xs:element name="person" type="p:Name"/>
            </xs:schema>
            """), new()
        {
            DocumentUri = new Uri("https://schemas.test/root.xsd"),
            ResolveReference = (reference, _) =>
            {
                calls.Add(reference);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(reference switch
                {
                    "https://schemas.test/parts/person.xsd" => Encoding.UTF8.GetBytes("""
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:person" xml:base="nested/">
                          <xs:include schemaLocation="types.xsd"/>
                        </xs:schema>
                        """),
                    "https://schemas.test/parts/nested/types.xsd" => Encoding.UTF8.GetBytes("""
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:person">
                          <xs:simpleType name="Name"><xs:restriction base="xs:string"/></xs:simpleType>
                        </xs:schema>
                        """),
                    _ => null
                });
            }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/parts/person.xsd",
            "https://schemas.test/parts/nested/types.xsd"])).IsTrue();
    }

    [Test]
    [Arguments("part%GG.xsd")]
    [Arguments("part\\types.xsd")]
    [Arguments("https://schemas.test/%ZZ")]
    public async Task MalformedSchemaLocationsRejectBeforeResolverNormalization(string location)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:include schemaLocation=\"" +
            location + "\"/></xs:schema>"), new()
            {
                DocumentUri = new Uri("https://schemas.test/root.xsd"),
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("dependency.uri");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task StandaloneSchemaFragmentLocationsRemainExplicitlyUnsupported()
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:include schemaLocation="part.xml#schema"/></xs:schema>
            """), new()
        {
            DocumentUri = new Uri("https://schemas.test/root.xsd"),
            ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("xsd.schema_fragment");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task CircularSchemaIncludesReuseBytesAndRetainNativeChameleonCompilation()
    {
        var calls = new List<string>();
        var root = Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:p="urn:person" targetNamespace="urn:person">
              <xs:include schemaLocation="types.xsd"/><xs:element name="person" type="p:Name"/>
            </xs:schema>
            """);
        var child = Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:include schemaLocation="types.xsd"/>
              <xs:simpleType name="Name"><xs:restriction base="xs:string"/></xs:simpleType>
            </xs:schema>
            """);
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", root, new()
        {
            DocumentUri = new Uri("https://schemas.test/root.xsd"),
            MaxReferences = 1,
            MaxTotalBytes = root.Length + child.Length,
            ResolveReference = (reference, _) =>
            {
                calls.Add(reference);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(child);
            }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/types.xsd"])).IsTrue();
    }

    [Test]
    public async Task SchemaDependencyCancellationPropagatesWithoutDispatchingLaterLocations()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        await Assert.That(async () => await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:include schemaLocation="first.xsd"/><xs:include schemaLocation="second.xsd"/>
            </xs:schema>
            """), new()
        {
            DocumentUri = new Uri("https://schemas.test/root.xsd"),
            ResolveReference = (reference, token) =>
            {
                calls.Add(reference);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException();
            }
        }, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(calls.SequenceEqual(["https://schemas.test/first.xsd"])).IsTrue();
    }

    [Test]
    [Arguments("../parts/")]
    [Arguments("//schemas.test/parts/")]
    public async Task RelativeXmlBasesWithoutContainingOriginsNeverBecomeRetrievalOrigins(string xmlBase)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xml:base=\"" +
            xmlBase + "\"><xs:include schemaLocation=\"types.xsd\"/></xs:schema>"), new()
            {
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("dependency.base");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task NamespaceCatalogsDoNotSupplyAnInventedSchemaRetrievalBase()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="https://namespace.test/person"/></xs:schema>
            """), new()
        {
            DocumentUri = new Uri("https://schemas.test/root.xsd"),
            ResolveReference = (reference, _) =>
            {
                calls.Add(reference);
                if (reference != "https://namespace.test/person") { throw new UnauthorizedAccessException(); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("""
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="https://namespace.test/person" xml:base="nested/">
                          <xs:include schemaLocation="types.xsd"/>
                        </xs:schema>
                        """));
            }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(result.Diagnostics[0].Code).IsEqualTo("dependency.base");
        await Assert.That(calls.SequenceEqual(["https://namespace.test/person"])).IsTrue();
    }

    [Test]
    public async Task NamespaceCatalogAndRetrievalLocationsRemainDistinctAcquisitionContexts()
    {
        const string identity = "https://namespace.test/person";
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:import namespace="https://namespace.test/person"/>
              <xs:import namespace="https://namespace.test/person" schemaLocation="https://namespace.test/person"/>
            </xs:schema>
            """), new()
        {
            DocumentUri = new Uri("https://schemas.test/root.xsd"),
            MaxReferences = 4,
            ResolveReference = (reference, _) =>
            {
                calls.Add(reference);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(reference == identity ?
                    """
                        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="https://namespace.test/person">
                          <xs:include schemaLocation="types.xsd"/>
                        </xs:schema>
                        """ : "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/>"));
            }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(calls.SequenceEqual([identity, "types.xsd", identity, "https://namespace.test/types.xsd"])).IsTrue();
    }

    [Test]
    [Arguments(6, DocumentValidationStatus.Valid)]
    [Arguments(5, DocumentValidationStatus.Indeterminate)]
    public async Task CachedSchemaDependenciesCannotBypassTheCompletedGraphDepthLimit(
        int maxDepth, DocumentValidationStatus expected)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("XSD/1.0", Encoding.UTF8.GetBytes("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:include schemaLocation="leaf.xsd"/><xs:include schemaLocation="middle.xsd"/><xs:include schemaLocation="outer.xsd"/>
              <xs:include schemaLocation="outer2.xsd"/><xs:include schemaLocation="outer3.xsd"/>
            </xs:schema>
            """), new()
        {
            MaxDepth = maxDepth,
            ResolveReference = (reference, _) =>
            {
                calls.Add(reference);
                var body = reference switch
                {
                    "leaf.xsd" => "",
                    "middle.xsd" => "<xs:include schemaLocation=\"leaf.xsd\"/>",
                    "outer.xsd" => "<xs:include schemaLocation=\"middle.xsd\"/>",
                    "outer2.xsd" => "<xs:include schemaLocation=\"outer.xsd\"/>",
                    "outer3.xsd" => "<xs:include schemaLocation=\"outer2.xsd\"/>",
                    _ => throw new UnauthorizedAccessException()
                };
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                    "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" + body + "</xs:schema>"));
            }
        });
        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(calls.SequenceEqual(["leaf.xsd", "middle.xsd", "outer.xsd", "outer2.xsd", "outer3.xsd"])).IsTrue();
        if (expected == DocumentValidationStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo("limit.depth");
        }
    }
}
