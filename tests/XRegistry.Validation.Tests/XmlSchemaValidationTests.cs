// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class XmlSchemaValidationTests
{
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
              <xs:include schemaLocation="types.xsd"/>
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
}
