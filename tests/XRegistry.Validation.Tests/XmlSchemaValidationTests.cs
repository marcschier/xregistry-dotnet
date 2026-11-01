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
