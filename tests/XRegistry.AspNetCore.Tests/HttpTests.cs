// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Federation;
using XRegistry.Models;
using XRegistry.Publication;
using XRegistry.Server;
using XRegistry.Validation;

namespace XRegistry.AspNetCore.Tests;

public class HttpTests
{
    [Test]
    public async Task ExactHttpWsdl20AcquisitionSelectsInheritedNativeOperationAfterDisposal()
    {
        const string first = """
            <description xmlns="http://www.w3.org/ns/wsdl" xmlns:t="urn:api" targetNamespace="urn:api">
              <include location="base.wsdl"/>
              <interface name="Port" extends="t:Base"/>
            </description>
            """;
        const string dependency = """
            <description xmlns="http://www.w3.org/ns/wsdl" xmlns:t="urn:api" xmlns:p="urn:payload" targetNamespace="urn:api">
              <types xmlns:xs="http://www.w3.org/2001/XMLSchema">
                <xs:import namespace="urn:payload" schemaLocation="payload.xsd#element(missing)element(schema)"/>
              </types>
              <interface name="Base" styleDefault="urn:style">
                <fault name="Failure" element="p:Failure"/>
                <operation name="Read" pattern="http://www.w3.org/ns/wsdl/out-in">
                  <output element="#any"/><input element="p:Payload"/><infault ref="t:Failure"/>
                </operation>
              </interface>
            </description>
            """;
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:p="urn:payload" id="schema" targetNamespace="urn:payload">
              <xs:redefine schemaLocation="age-base.xsd"><xs:complexType name="AgeBase">
                <xs:complexContent><xs:extension base="p:AgeBase"><xs:attribute name="source" type="xs:string"/></xs:extension></xs:complexContent>
              </xs:complexType></xs:redefine>
              <xs:attributeGroup name="AgeAttributes"><xs:attribute name="unit" type="xs:string"/></xs:attributeGroup>
              <xs:group name="AgeFields"><xs:choice>
                <xs:element name="Number"><xs:simpleType><xs:union memberTypes="xs:int xs:boolean"/></xs:simpleType></xs:element>
                <xs:element name="Unknown" type="xs:boolean"/>
              </xs:choice></xs:group>
              <xs:element name="Payload"><xs:complexType><xs:all>
                <xs:element name="Name" type="xs:string"/>
                <xs:element name="Age" minOccurs="0"><xs:complexType><xs:complexContent><xs:extension base="p:AgeBase">
                  <xs:group ref="p:AgeFields"/><xs:attributeGroup ref="p:AgeAttributes"/>
                </xs:extension></xs:complexContent></xs:complexType></xs:element>
              </xs:all></xs:complexType>
                <xs:unique name="Names"><xs:selector xpath="p:Name"/><xs:field xpath="."/></xs:unique>
              </xs:element>
              <xs:element name="Failure" type="xs:string"/>
            </xs:schema>
            """;
        var second = first.Replace("name=\"Port\"", "name=\"NewPort\"", StringComparison.Ordinal);
        const string version = "/apigroups/g/apis/a/versions/1";
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Api),
            useBoundPublicRoot: true);
        foreach (var entry in new[] { (Path: version, Bytes: first), (Path: "/apigroups/g/apis/a/versions/2", Bytes: second) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, entry.Path)
            {
                Content = new StringContent(entry.Bytes, Encoding.UTF8, "application/xml")
            };
            request.Headers.TryAddWithoutValidation("xRegistry-format", "WSDL/2.0");
            using var response = await host.Client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created)
                .Because(await response.Content.ReadAsStringAsync());
        }
        using var source = await HttpFederationReadSource.OpenAsync(host.Client.BaseAddress!,
            new() { AllowLoopbackHttp = true });
        var acquired = await ApiRegistryDocumentAcquirer.AcquireAsync(source, version);
        source.Dispose();
        await host.DisposeAsync();
        using var stream = acquired.Document.OpenRead();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        await Assert.That(Encoding.UTF8.GetString(memory.ToArray())).IsEqualTo(first);
        var dependencyUri = new Uri(new Uri(acquired.Document.Origin!), "base.wsdl");
        var schemaUri = new Uri(dependencyUri, "payload.xsd");
        var ageBaseUri = new Uri(schemaUri, "age-base.xsd");
        var calls = new List<string>();
        ValueTask<ReadOnlyMemory<byte>?> ResolveDependency(string uri, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            calls.Add(uri);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri == dependencyUri.AbsoluteUri ?
                Encoding.UTF8.GetBytes(dependency) : uri == schemaUri.AbsoluteUri ?
                Encoding.UTF8.GetBytes(schema) : uri == ageBaseUri.AbsoluteUri ?
                Encoding.UTF8.GetBytes("""
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:payload">
                      <xs:complexType name="AgeBase"/>
                    </xs:schema>
                    """) : (ReadOnlyMemory<byte>?)null);
        }
        var unresolved = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            memory.ToArray(), ApiOperationReference.Parse(version +
                "#interfaces/%7Burn:api%7DPort/operations/%7Burn:api%7DRead"), new Uri(acquired.Document.Origin!));
        await Assert.That(unresolved.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(calls.Count).IsEqualTo(0);
        var selected = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            memory.ToArray(), ApiOperationReference.Parse(version +
                "#interfaces/%7Burn:api%7DPort/operations/%7Burn:api%7DRead"), new Uri(acquired.Document.Origin!), ResolveDependency);
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.SequenceEqual([dependencyUri.AbsoluteUri, schemaUri.AbsoluteUri, ageBaseUri.AbsoluteUri])).IsTrue();
        await Assert.That(selected.DocumentUri).IsEqualTo(dependencyUri);
        await Assert.That(selected.Wsdl20Operation!.OperationName).IsEqualTo("{urn:api}Read");
        await Assert.That(selected.Wsdl20Operation.DeclaringInterfaceName).IsEqualTo("{urn:api}Base");
        await Assert.That(selected.Wsdl20Operation.MessageExchangePattern).IsEqualTo("http://www.w3.org/ns/wsdl/out-in");
        await Assert.That(selected.Wsdl20Operation.Styles.SequenceEqual(["urn:style"])).IsTrue();
        await Assert.That(selected.Wsdl20Operation.Faults.Count).IsEqualTo(1);
        var fault = selected.Wsdl20Operation.Faults[0];
        await Assert.That(fault.DeclaringInterfaceName).IsEqualTo("{urn:api}Base");
        await Assert.That(fault.FaultName).IsEqualTo("{urn:api}Failure");
        await Assert.That(fault.ContentModel).IsEqualTo("{urn:payload}Failure");
        await Assert.That(fault.Direction).IsEqualTo("in");
        await Assert.That(fault.MessageLabel).IsEqualTo("In");
        await Assert.That(fault.FaultXml.Contains("name=\"Failure\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fault.SchemaElement!.ElementName).IsEqualTo("{urn:payload}Failure");
        await Assert.That(fault.SchemaElement.DocumentUri).IsEqualTo(schemaUri);
        await Assert.That(fault.SchemaElement.SchemaDocumentUris[0]).IsEqualTo(schemaUri);
        await Assert.That(fault.SchemaElement.SchemaDocuments[0].Contains("name=\"Payload\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(selected.Wsdl20Operation.SchemaElements.Count).IsEqualTo(2);
        var payload = selected.Wsdl20Operation.SchemaElements.Single(element => element.ElementName == "{urn:payload}Payload");
        await Assert.That(payload.ElementXml.Contains("<xs:all>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.SchemaDocuments[0].Contains("memberTypes=\"xs:int xs:boolean\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.SchemaDocuments[0].Contains("<xs:choice>", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.ElementXml.Contains("base=\"p:AgeBase\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.SchemaDocuments.Count).IsEqualTo(2);
        await Assert.That(payload.SchemaDocuments[0].Contains("<xs:redefine", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.SchemaDocuments[1].Contains("name=\"AgeBase\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.SchemaDocumentUris[1]).IsEqualTo(ageBaseUri);
        await Assert.That(payload.ElementXml.Contains("ref=\"p:AgeFields\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.ElementXml.Contains("ref=\"p:AgeAttributes\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.ElementXml.Contains("name=\"Names\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(payload.ElementXml.Contains("xpath=\"p:Name\"", StringComparison.Ordinal)).IsTrue();
        var missing = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            memory.ToArray(), ApiOperationReference.Parse(version +
                "#interfaces/%7Burn:api%7DNewPort/operations/%7Burn:api%7DRead"),
            new Uri(acquired.Document.Origin!), ResolveDependency);
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
    }

    [Test]
    public async Task ExactHttpWsdlAcquisitionPreservesNativeOperationSelectionAfterDefaultChangeAndDisposal()
    {
        const string first = """
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/" xmlns:m="urn:messages" targetNamespace="urn:users">
              <import namespace="urn:messages" location="messages.wsdl"/><portType name="Port">
                <operation name="Read"><input message="m:Message"/><output message="m:Message"/></operation>
              </portType>
            </definitions>
            """;
        var second = first.Replace("name=\"Read\"", "name=\"ReadNew\"", StringComparison.Ordinal);
        const string version = "/apigroups/g/apis/a/versions/1";
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Api),
            useBoundPublicRoot: true);
        foreach (var item in new[] { (Path: version, Bytes: first), (Path: "/apigroups/g/apis/a/versions/2", Bytes: second) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, item.Path)
            {
                Content = new StringContent(item.Bytes, Encoding.UTF8, "application/xml")
            };
            request.Headers.TryAddWithoutValidation("xRegistry-format", "WSDL/1.1");
            using var response = await host.Client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created)
                .Because(await response.Content.ReadAsStringAsync());
        }
        using var source = await HttpFederationReadSource.OpenAsync(host.Client.BaseAddress!,
            new() { AllowLoopbackHttp = true });
        var acquired = await ApiRegistryDocumentAcquirer.AcquireAsync(source, version);
        source.Dispose();
        await host.DisposeAsync();
        using var stream = acquired.Document.OpenRead();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        await Assert.That(Encoding.UTF8.GetString(memory.ToArray())).IsEqualTo(first);
        var calls = new List<string>();
        var dependencyUri = new Uri(new Uri(acquired.Document.Origin!), "messages.wsdl").AbsoluteUri;
        ValueTask<ReadOnlyMemory<byte>?> ResolveImport(string uri, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            calls.Add(uri);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri == dependencyUri ? Encoding.UTF8.GetBytes("""
                <definitions xmlns="http://schemas.xmlsoap.org/wsdl/" targetNamespace="urn:messages">
                  <message name="Message"/>
                </definitions>
                """) : (ReadOnlyMemory<byte>?)null);
        }
        var reference = ApiOperationReference.Parse(version + "#interfaces/%7Burn:users%7DPort/operations/Read");
        var unresolved = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            memory.ToArray(), reference, new Uri(acquired.Document.Origin!));
        await Assert.That(unresolved.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(calls.Count).IsEqualTo(0);
        var result = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            memory.ToArray(), reference, new Uri(acquired.Document.Origin!), ResolveImport);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.WsdlOperation!.InterfaceName).IsEqualTo("{urn:users}Port");
        await Assert.That(result.WsdlOperation.OperationName).IsEqualTo("Read");
        await Assert.That(result.WsdlOperation.MessageExchangePattern).IsEqualTo(WsdlMessageExchangePattern.RequestResponse);
        await Assert.That(result.WsdlOperation.InputName).IsEqualTo("ReadRequest");
        await Assert.That(result.WsdlOperation.OutputName).IsEqualTo("ReadResponse");
        await Assert.That(result.WsdlOperation.InputMessage).IsEqualTo("{urn:messages}Message");
        await Assert.That(result.WsdlOperation.OutputMessage).IsEqualTo("{urn:messages}Message");
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo(dependencyUri);
        await Assert.That(result.DocumentUri!.AbsoluteUri).IsEqualTo(acquired.Document.Origin);
        var missing = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            memory.ToArray(), ApiOperationReference.Parse(version +
                "#interfaces/%7Burn:users%7DPort/operations/ReadNew"), new Uri(acquired.Document.Origin!), ResolveImport);
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
    }

    [Test]
    public async Task HttpExternalApiAcquisitionRetainsTheAuthorizedEffectiveUrlForRelativeDependencies()
    {
        const string entry = """
            {"openapi":"3.1.1","info":{"title":"External","version":"1"},
             "paths":{"/value":{"$ref":"operations.json#/Read"}}}
            """;
        var externalRequests = 0;
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Api),
            useBoundPublicRoot: true, documentReferencePolicy: new ExternalApiReferencePolicy(), configureEndpoints: app =>
            {
                app.MapGet("/api-entry", () =>
                {
                    Interlocked.Increment(ref externalRequests);
                    return Results.Redirect("/relocated/api.json");
                });
                app.MapGet("/relocated/api.json", () =>
                {
                    Interlocked.Increment(ref externalRequests);
                    return Results.Text(entry, "application/json");
                });
            });
        var initialUri = new Uri(host.Client.BaseAddress!, "/api-entry");
        var effectiveUri = new Uri(host.Client.BaseAddress!, "/relocated/api.json");
        const string version = "/apigroups/g/apis/a/versions/1";
        using (var request = new HttpRequestMessage(HttpMethod.Put, version + "$details")
        {
            Content = new StringContent(new JsonObject
            {
                ["format"] = "OpenAPI/3.1",
                ["apiurl"] = initialUri.AbsoluteUri,
                ["contenttype"] = "application/json"
            }.ToJsonString(), Encoding.UTF8, "application/json")
        })
        using (var response = await host.Client.SendAsync(request))
        {
            await Assert.That(response.IsSuccessStatusCode).IsTrue()
                .Because(await response.Content.ReadAsStringAsync());
        }
        var budget = new FederationReadBudget();
        using var source = await HttpFederationReadSource.OpenAsync(host.Client.BaseAddress!,
            new() { AllowLoopbackHttp = true }, new() { Limits = budget.Limits }, budget);
        var unfetched = await Assert.That(async () =>
            await ApiRegistryDocumentAcquirer.AcquireAsync(source, version, budget))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected unfetched descriptor.");
        await Assert.That(unfetched.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(externalRequests).IsEqualTo(0);
        var acquired = await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/a", budget,
            resolveExternal: async (uri, token) =>
            {
                if (uri != initialUri) { throw new UnauthorizedAccessException("Only this entry URL is authorized."); }
                using var response = await host.Client.GetAsync(uri, token);
                response.EnsureSuccessStatusCode();
                return new FederationDocument(await response.Content.ReadAsByteArrayAsync(token),
                    response.Content.Headers.ContentType!.MediaType,
                    documentOrigin: response.RequestMessage!.RequestUri!.AbsoluteUri);
            });
        await Assert.That(externalRequests).IsEqualTo(2);
        await Assert.That(acquired.VersionXid).IsEqualTo(version);
        await Assert.That(acquired.Document.Origin).IsEqualTo(effectiveUri.AbsoluteUri);
        await Assert.That(acquired.Document.Base).IsNull();
        source.Dispose();
        await host.DisposeAsync();
        using var stream = acquired.Document.OpenRead();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var calls = new List<string>();
        var selected = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            memory.ToArray(), ApiOperationReference.Parse(version + "#operations/Read"), effectiveUri,
            (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    Encoding.UTF8.GetBytes("""{"Read":{"get":{"operationId":"Read"}}}"""));
            });
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo(new Uri(effectiveUri, "operations.json").AbsoluteUri);
        await Assert.That(selected.DocumentUri!.AbsoluteUri).IsEqualTo(calls[0]);
    }

    private sealed class ExternalApiReferencePolicy : IRegistryDocumentReferencePolicy
    {
        public ValueTask AuthorizeAsync(ClaimsPrincipal caller, Uri reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reference.IsLoopback || reference.Scheme != "http" || reference.AbsolutePath != "/api-entry")
            {
                throw new UnauthorizedAccessException("Only the loopback API entry is authorized.");
            }
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task ExactHttpApiAcquisitionFeedsOperationSelectionWithoutFollowingTheChangedDefault()
    {
        const string first = """
            {"openapi":"3.1.1","info":{"title":"Telemetry","version":"1"},
             "paths":{"/telemetry":{"$ref":"operations.json#/Read"}}}
            """;
        const string dependency = """
            {"Read":{"get":{"operationId":"Read","responses":{"200":{"description":"OK"}}}}}
            """;
        const string second = """
            {"openapi":"3.1.1","info":{"title":"Telemetry","version":"2"},
             "paths":{"/telemetry":{"get":{"operationId":"ReadNew","responses":{"200":{"description":"OK"}}}}}}
            """;
        const string version = "/apigroups/g/apis/a/versions/1";
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Api),
            useBoundPublicRoot: true);
        foreach (var item in new[] { (Path: version, Bytes: first), (Path: "/apigroups/g/apis/a/versions/2", Bytes: second) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, item.Path)
            {
                Content = new StringContent(item.Bytes, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("xRegistry-format", "OpenAPI/3.1");
            using var response = await host.Client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created)
                .Because(await response.Content.ReadAsStringAsync());
        }
        var budget = new FederationReadBudget();
        using var source = await HttpFederationReadSource.OpenAsync(host.Client.BaseAddress!,
            new() { AllowLoopbackHttp = true }, new() { Limits = budget.Limits }, budget);
        var acquired = await ApiRegistryDocumentAcquirer.AcquireAsync(source, version, budget);
        await Assert.That(acquired.VersionXid).IsEqualTo(version);
        await Assert.That(acquired.Format).IsEqualTo("OpenAPI/3.1");
        await Assert.That(acquired.Context.Source).IsEqualTo(source.Context.Source);
        await Assert.That(acquired.Document.Origin).IsEqualTo(new Uri(host.Client.BaseAddress!, version).AbsoluteUri);
        await Assert.That(source.Observations.Any(o => o.RequestUri.AbsolutePath.EndsWith("/versions/2", StringComparison.Ordinal)))
            .IsFalse();
        var currentDefault = await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/a", budget);
        await Assert.That(currentDefault.VersionXid).IsEqualTo("/apigroups/g/apis/a/versions/2");
        using (var defaultStream = currentDefault.Document.OpenRead())
        using (var reader = new StreamReader(defaultStream, Encoding.UTF8))
        {
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo(second);
        }
        using (var change = new HttpRequestMessage(HttpMethod.Put, "/apigroups/g/apis/a/meta")
        {
            Content = new StringContent("""{"defaultversionid":"1","defaultversionsticky":true}""",
                Encoding.UTF8, "application/json")
        })
        using (var response = await host.Client.SendAsync(change))
        {
            await Assert.That(response.IsSuccessStatusCode).IsTrue()
                .Because(await response.Content.ReadAsStringAsync());
        }
        var stickyDefault = await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/a", budget);
        await Assert.That(stickyDefault.VersionXid).IsEqualTo(version);
        await Assert.That(stickyDefault.ContentDigest).IsEqualTo(acquired.ContentDigest);
        using (var aliasRequest = new HttpRequestMessage(HttpMethod.Put, "/apigroups/g/apis/alias$details")
        {
            Content = new StringContent("""{"meta":{"xref":"/apigroups/g/apis/a"}}""",
                Encoding.UTF8, "application/json")
        })
        using (var response = await host.Client.SendAsync(aliasRequest))
        {
            await Assert.That(response.IsSuccessStatusCode).IsTrue()
                .Because(await response.Content.ReadAsStringAsync());
        }
        var aliasDefault = await ApiRegistryDocumentAcquirer.AcquireDefaultAsync(source, "/apigroups/g/apis/alias", budget);
        await Assert.That(aliasDefault.VersionXid).IsEqualTo(version);
        await Assert.That(aliasDefault.Document.Origin).IsEqualTo(acquired.Document.Origin);
        await Assert.That(aliasDefault.ContentDigest).IsEqualTo(acquired.ContentDigest);
        var reference = ApiOperationReference.Parse(version + "#operations/Read");
        source.Dispose();
        await host.DisposeAsync();
        using var stream = acquired.Document.OpenRead();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var bytes = memory.ToArray();
        await Assert.That(Encoding.UTF8.GetString(bytes)).IsEqualTo(first);
        await Assert.That(acquired.ContentDigest).IsEqualTo(
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var dependencyUri = new Uri(new Uri(acquired.Document.Origin!), "operations.json").AbsoluteUri;
        var calls = new List<string>();
        ValueTask<ReadOnlyMemory<byte>?> ResolveDependency(string uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls.Add(uri);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(dependency));
        }
        var unavailable = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            bytes, reference, new Uri(acquired.Document.Origin!));
        await Assert.That(unavailable.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        var selected = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            bytes, reference, new Uri(acquired.Document.Origin!), ResolveDependency);
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Operation.GetProperty("operationId").GetString()).IsEqualTo("Read");
        await Assert.That(selected.DocumentPath).IsEqualTo("$/Read/get");
        await Assert.That(selected.DocumentUri!.AbsoluteUri).IsEqualTo(dependencyUri);
        await Assert.That(calls.Count).IsEqualTo(1);
        await Assert.That(calls[0]).IsEqualTo(dependencyUri);
        var missing = await ApiOperationDocumentResolver.ResolveAsync(acquired.Format, acquired.Document.ContentType!,
            bytes, ApiOperationReference.Parse(version + "#operations/ReadNew"), new Uri(acquired.Document.Origin!),
            ResolveDependency);
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        await Assert.That(missing.Diagnostic!.Code).IsEqualTo("selection.not_found");
    }

    [Test]
    [Arguments("JsonSchema/draft/2020-12")]
    [Arguments("JsonStructure/draft-04")]
    [Arguments("XSD/1.0")]
    public async Task HttpSchemaPointerAndXPathSelectionsRetainTheExactVersionAcrossDefaultChanges(string format)
    {
        var xml = format == "XSD/1.0";
        var structure = format == "JsonStructure/draft-04";
        var schema = xml
            ? """
              <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:telemetry">
                <xs:annotation><xs:documentation>Telemetry</xs:documentation></xs:annotation>
                <xs:simpleType name="Count"><xs:restriction base="xs:string"/></xs:simpleType>
                <xs:element name="Event" type="xs:string"/>
              </xs:schema>
              """
            : structure
                ? """
                  {"$schema":"https://json-structure.org/meta/core/v0/#","$id":"https://schemas.test/telemetry",
                   "name":"Telemetry","$root":"/definitions/Root",
                   "definitions":{"Root":{"type":"object","properties":{"value":{"type":"int64"}}},"Count":{"type":"string"}}}
                  """
                : """
                  {"$schema":"https://json-schema.org/draft/2020-12/schema","title":"Telemetry",
                   "type":"object","$defs":{"Count":{"type":"string"}}}
                  """;
        var selector = xml ? "/xs:schema/xs:simpleType[@name='Count']"
            : structure ? "/definitions/Count" : "/$defs/Count";
        var missingSelector = xml ? "/xs:schema/xs:element[@name='Missing']"
            : structure ? "/definitions/Missing" : "/$defs/Missing";
        var wrongKindSelector = xml ? "/xs:schema/xs:annotation"
            : structure ? "/definitions" : "/title";
        const string versionPath = "/schemagroups/g/schemas/s/versions/1";
        var mediaType = xml ? "application/xml" : "application/json";
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
            resourceValidator: new BuiltInRegistryResourceValidator());
        using var publish = new HttpRequestMessage(HttpMethod.Put, versionPath)
        {
            Content = new StringContent(schema, Encoding.UTF8, mediaType)
        };
        publish.Headers.TryAddWithoutValidation("xRegistry-format", format);
        using var created = await host.Client.SendAsync(publish);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created)
            .Because(await created.Content.ReadAsStringAsync());
        using var read = await host.Client.GetAsync(versionPath);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var bytes = await read.Content.ReadAsByteArrayAsync();
        await Assert.That(Encoding.UTF8.GetString(bytes)).IsEqualTo(schema);
        var root = await SchemaObjectSelector.SelectAsync(format, bytes, versionPath);
        await Assert.That(root.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(root.Selection!.DocumentReference).IsEqualTo(versionPath);
        if (xml)
        {
            await Assert.That(root.Selection.Kind).IsEqualTo(SchemaTypeKind.XmlSchemaElement);
            await Assert.That(root.Selection.TypeName).IsEqualTo("{urn:telemetry}Event");
        }
        else
        {
            await Assert.That(root.Selection.JsonPointer).IsEqualTo(structure ? "/definitions/Root" : "");
            await Assert.That(root.Selection.Json!.Value.GetProperty("type").GetString()).IsEqualTo("object");
        }
        var selectedUri = versionPath + "#" + selector;
        var selected = await SchemaObjectSelector.SelectAsync(format, bytes, selectedUri);
        await Assert.That(selected.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(selected.Selection!.SchemaUri).IsEqualTo(selectedUri);
        await Assert.That(selected.Selection.DocumentReference).IsEqualTo(versionPath);
        await Assert.That(Encoding.UTF8.GetString(selected.Selection.Document.Span)).IsEqualTo(schema);
        if (xml)
        {
            await Assert.That(selected.Selection.Kind).IsEqualTo(SchemaTypeKind.XmlSchemaType);
            await Assert.That(selected.Selection.TypeName).IsEqualTo("{urn:telemetry}Count");
            await Assert.That(selected.Selection.Xml!.Attribute("name")!.Value).IsEqualTo("Count");
        }
        else
        {
            await Assert.That(selected.Selection.Kind).IsEqualTo(structure
                ? SchemaTypeKind.JsonStructure : SchemaTypeKind.JsonSchema);
            await Assert.That(selected.Selection.JsonPointer).IsEqualTo(selector);
            await Assert.That(selected.Selection.Json!.Value.GetProperty("type").GetString()).IsEqualTo("string");
        }
        var missing = await SchemaObjectSelector.SelectAsync(format, bytes, versionPath + "#" + missingSelector);
        await Assert.That(missing.Status).IsEqualTo(SchemaObjectSelectionStatus.NotFound);
        await Assert.That(missing.Selection).IsNull();
        await Assert.That(missing.Diagnostics[0].Code).IsEqualTo("selection.not_found");
        var wrongKind = await SchemaObjectSelector.SelectAsync(format, bytes, versionPath + "#" + wrongKindSelector);
        await Assert.That(wrongKind.Status).IsEqualTo(SchemaObjectSelectionStatus.Invalid);
        await Assert.That(wrongKind.Selection).IsNull();
        await Assert.That(wrongKind.Diagnostics[0].Code).IsEqualTo("selection.not_type");

        var newerSchema = xml ? schema.Replace("xs:string", "xs:boolean", StringComparison.Ordinal)
            : schema.Replace("\"type\":\"string\"", "\"type\":\"boolean\"", StringComparison.Ordinal);
        using var publishNewer = new HttpRequestMessage(HttpMethod.Put, "/schemagroups/g/schemas/s/versions/2")
        {
            Content = new StringContent(newerSchema, Encoding.UTF8, mediaType)
        };
        publishNewer.Headers.TryAddWithoutValidation("xRegistry-format", format);
        using var newer = await host.Client.SendAsync(publishNewer);
        await Assert.That(newer.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var defaultRead = await host.Client.GetAsync("/schemagroups/g/schemas/s");
        await Assert.That(defaultRead.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await defaultRead.Content.ReadAsStringAsync()).IsEqualTo(newerSchema);
        using var exactRead = await host.Client.GetAsync(versionPath);
        await Assert.That(exactRead.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await exactRead.Content.ReadAsStringAsync()).IsEqualTo(schema);
        await host.DisposeAsync();
        Array.Fill(bytes, (byte)'!');
        var retained = await SchemaObjectSelector.SelectAsync(format, selected.Selection.Document, selectedUri);
        await Assert.That(retained.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(Encoding.UTF8.GetString(retained.Selection!.Document.Span)).IsEqualTo(schema);
        if (xml)
        {
            await Assert.That(retained.Selection.TypeName).IsEqualTo("{urn:telemetry}Count");
            await Assert.That(retained.Selection.Xml!.Elements().Single().Attribute("base")!.Value).IsEqualTo("xs:string");
        }
        else
        {
            await Assert.That(retained.Selection.JsonPointer).IsEqualTo(selector);
            await Assert.That(retained.Selection.Json!.Value.GetProperty("type").GetString()).IsEqualTo("string");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HttpSchemaVersionsSupportDirectAndEmbeddedNamedSelectorsWithoutRefetching(bool protobuf)
    {
        var format = protobuf ? "Protobuf/3" : "Avro/1.11.0";
        var schema = protobuf
            ? """syntax="proto3"; message TelemetryEvent { string value=1; }"""
            : """{"type":"record","name":"TelemetryEvent","fields":[]}""";
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
            resourceValidator: new BuiltInRegistryResourceValidator());
        using var publish = new HttpRequestMessage(HttpMethod.Put, "/schemagroups/g/schemas/s/versions/1")
        {
            Content = new StringContent(schema, Encoding.UTF8, protobuf ? "text/plain" : "application/json")
        };
        publish.Headers.TryAddWithoutValidation("xRegistry-format", format);
        using var created = await host.Client.SendAsync(publish);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        const string versionPath = "/schemagroups/g/schemas/s/versions/1";
        using var directRead = await host.Client.GetAsync(versionPath);
        await Assert.That(directRead.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var bytes = await directRead.Content.ReadAsByteArrayAsync();
        await Assert.That(Encoding.UTF8.GetString(bytes)).IsEqualTo(schema);
        var directUri = versionPath + "#TelemetryEvent";
        var direct = await SchemaObjectSelector.SelectAsync(format, bytes, directUri);
        await Assert.That(direct.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(direct.Selection!.DocumentReference).IsEqualTo(versionPath);
        await Assert.That(direct.Selection.TypeName).IsEqualTo("TelemetryEvent");

        const string detailsPath = versionPath + "$details?inline=schema";
        using var embeddedRead = await host.Client.GetAsync(detailsPath);
        await Assert.That(embeddedRead.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var container = await embeddedRead.Content.ReadAsByteArrayAsync();
        var metadata = RegistryJson.Parse(container).RootElement;
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(metadata.GetProperty("format").GetString()).IsEqualTo(format);
        await Assert.That(metadata.GetProperty("schema").ValueKind).IsEqualTo(protobuf
            ? System.Text.Json.JsonValueKind.String : System.Text.Json.JsonValueKind.Object);
        var reference = detailsPath + "#/schema";
        var embedded = await SchemaObjectSelector.SelectEmbeddedAsync(format, container, reference + ":TelemetryEvent",
            new() { DocumentReference = reference });
        await Assert.That(embedded.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(embedded.Selection!.TypeName).IsEqualTo(direct.Selection.TypeName);
        await Assert.That(embedded.Selection.Kind).IsEqualTo(direct.Selection.Kind);
        await Assert.That(Encoding.UTF8.GetString(embedded.Selection.Document.Span)).IsEqualTo(schema);
        await host.DisposeAsync();
        var retained = await SchemaObjectSelector.SelectEmbeddedAsync(format, container, reference + ":TelemetryEvent",
            new() { DocumentReference = reference });
        await Assert.That(retained.Status).IsEqualTo(SchemaObjectSelectionStatus.Selected);
        await Assert.That(retained.Selection!.TypeName).IsEqualTo("TelemetryEvent");
        await Assert.That(Encoding.UTF8.GetString(retained.Selection.Document.Span)).IsEqualTo(schema);
    }

    [Test]
    public async Task PublicationProjectionRequiresReadOnlyBackendAndEnforcesItsResponseBudget()
    {
        var snapshot = await CreateOpenedSnapshot();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        await using var application = builder.Build();
        var writableBackend = new TestRegistryHostBackend { ReadOnly = false };
        var writable = await Assert.That(() =>
                application.MapPublicationSnapshot(writableBackend, snapshot))
            .Throws<ArgumentException>() ?? throw new InvalidOperationException("Expected writable backend rejection.");
        await Assert.That(writable.ParamName).IsEqualTo("backend");

        var limitedBackend = new TestRegistryHostBackend
        {
            ReadOnly = true,
            Limits = new() { MaxResponseBytes = snapshot.OpenRecord.GetExactBytes().Length - 1 }
        };
        var overBudget = await Assert.That(() =>
                application.MapPublicationSnapshot(limitedBackend, snapshot))
            .Throws<ArgumentOutOfRangeException>() ?? throw new InvalidOperationException("Expected context budget rejection.");
        await Assert.That(overBudget.ParamName).IsEqualTo("snapshot");
    }

    [Test]
    public async Task PublicationContextGetReturnsExactOpenedRecordBytes()
    {
        var snapshot = await CreateOpenedSnapshot();
        var backend = new TestRegistryHostBackend
        {
            ReadOnly = true,
            Limits = new() { MaxResponseBytes = snapshot.OpenRecord.GetExactBytes().Length }
        };
        await using var host = await TestHost.StartAsync(hostBackend: backend, publicationSnapshot: snapshot);

        using var response = await host.Client.GetAsync(PublicationEndpointRouteBuilderExtensions.ContextPath);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That(bytes.SequenceEqual(snapshot.OpenRecord.GetExactBytes())).IsTrue();
        await Assert.That(backend.AuthorizationCount).IsEqualTo(1);
        await Assert.That(backend.ExecutionCount).IsZero();
    }

    [Test]
    public async Task PublicationContextHeadRetainsContentLengthAndHasNoBody()
    {
        var snapshot = await CreateOpenedSnapshot();
        await using var host = await TestHost.StartAsync(hostBackend: new TestRegistryHostBackend { ReadOnly = true },
            publicationSnapshot: snapshot);
        using var request = new HttpRequestMessage(HttpMethod.Head, PublicationEndpointRouteBuilderExtensions.ContextPath);

        using var response = await host.Client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentLength).IsEqualTo(snapshot.OpenRecord.GetExactBytes().Length);
        await Assert.That(await response.Content.ReadAsByteArrayAsync()).IsEmpty();
    }

    [Test]
    public async Task PublicationContextRejectsUnsupportedMethodsWith405()
    {
        var snapshot = await CreateOpenedSnapshot();
        await using var host = await TestHost.StartAsync(hostBackend: new TestRegistryHostBackend { ReadOnly = true },
            publicationSnapshot: snapshot);
        using var response = await host.Client.PostAsync(PublicationEndpointRouteBuilderExtensions.ContextPath, Json("{}"));
        var problem = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("action_not_supported");
    }

    [Test]
    public async Task PublicationContextRouteRequiresReadAuthorization()
    {
        var snapshot = await CreateOpenedSnapshot();
        var backend = new TestRegistryHostBackend { ReadOnly = true, DenyAuthorization = true };
        await using var host = await TestHost.StartAsync(hostBackend: backend, publicationSnapshot: snapshot);
        using var response = await host.Client.GetAsync(PublicationEndpointRouteBuilderExtensions.ContextPath);
        var problem = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("forbidden");
        await Assert.That(backend.AuthorizationCount).IsEqualTo(1);
        await Assert.That(backend.ExecutionCount).IsZero();

        using var headRequest = new HttpRequestMessage(HttpMethod.Head,
            PublicationEndpointRouteBuilderExtensions.ContextPath);
        using var headResponse = await host.Client.SendAsync(headRequest);
        await Assert.That(headResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(backend.AuthorizationCount).IsEqualTo(2);
        await Assert.That(backend.ExecutionCount).IsZero();
    }

    [Test]
    public async Task PublicationSnapshotRootLinkRelationTargetsTheContextUrl()
    {
        var snapshot = await CreateOpenedSnapshot();
        await using var host = await TestHost.StartAsync(hostBackend: new TestRegistryHostBackend { ReadOnly = true },
            publicationSnapshot: snapshot);

        using var response = await host.Client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.GetValues("Link").Single()).IsEqualTo(
            "<https://public.example/registry>;rel=xregistry-root, " +
            "<https://public.example/registry" + PublicationEndpointRouteBuilderExtensions.ContextPath +
            ">;rel=\"https://xregistry.io/workingdrafts/publication/context\"");
    }

    [Test]
    public async Task PublicationProjectionLeavesOrdinaryNonRootLinkHeaderUnchanged()
    {
        var snapshot = await CreateOpenedSnapshot();
        await using var host = await TestHost.StartAsync(hostBackend: new TestRegistryHostBackend { ReadOnly = true },
            publicationSnapshot: snapshot);

        using var response = await host.Client.GetAsync("/teams");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.GetValues("Link").Single())
            .IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
    }

    [Test]
    public async Task KestrelRejectsConcreteAvroIncompatibilityWithoutPublishingTheDocument()
    {
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
            resourceValidator: new BuiltInRegistryResourceValidator());
        using var first = new HttpRequestMessage(HttpMethod.Put, "/schemagroups/g/schemas/s")
        {
            Content = Json("\"int\"")
        };
        first.Headers.TryAddWithoutValidation("xRegistry-format", "Avro/1.11.0");
        using var created = await host.Client.SendAsync(first);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.GetValues("xRegistry-formatvalidated").Single()).IsEqualTo("true");
        using var policy = await host.Client.PatchAsync("/schemagroups/g/schemas/s/meta", Json("""{"compatibility":"forward"}"""));
        await Assert.That(policy.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var next = new HttpRequestMessage(HttpMethod.Post, "/schemagroups/g/schemas/s")
        {
            Content = Json("\"long\"")
        };
        next.Headers.TryAddWithoutValidation("xRegistry-format", "Avro/1.11.0");
        using var rejected = await host.Client.SendAsync(next);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("compatibility_violation");
        await RegistryErrorContractHttpTests.AssertProblem(rejected, 400, "compatibility_violation", "spec.md",
            "The request would cause one or more Versions of \"/schemagroups/g/schemas/s\" to violate its compatibility rule (forward).",
            "/schemagroups/g/schemas/s", """{"compat":"forward"}""");
        using var unchanged = await host.Client.GetAsync("/schemagroups/g/schemas/s");
        await Assert.That(await unchanged.Content.ReadAsStringAsync()).IsEqualTo("\"int\"");
        await Assert.That(unchanged.Headers.GetValues("xRegistry-versionid").Single()).IsEqualTo("1");
        await Assert.That(unchanged.Headers.GetValues("xRegistry-compatibilityvalidated").Single()).IsEqualTo("true");
    }

    [Test]
    public async Task KestrelDistinguishesInvalidSchemasFromUncheckedUnsupportedFormats()
    {
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
            resourceValidator: new BuiltInRegistryResourceValidator());
        using var uncheckedFormat = await host.Client.PutAsync("/schemagroups/g/schemas/unchecked$details",
            Json("""{"format":"XSD/1.1","schema":"not fetched or interpreted as XSD 1.0"}"""));
        await Assert.That(uncheckedFormat.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var metadata = RegistryJson.Parse(await uncheckedFormat.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(metadata.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(metadata.GetProperty("formatvalidatedreason").GetString()!).Contains("format.unsupported");
        using var invalid = await host.Client.PutAsync("/schemagroups/g/schemas/invalid$details",
            Json("""{"format":"Avro/1.11.0","schema":{}}"""));
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await invalid.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("format_violation");
        await RegistryErrorContractHttpTests.AssertProblem(invalid, 400, "format_violation", "spec.md",
            "The request would cause Version \"/schemagroups/g/schemas/invalid/versions/1\" to be non-compliant with its \"format\" (Avro/1.11.0).",
            "/schemagroups/g/schemas/invalid/versions/1", """{"format":"Avro/1.11.0"}""");
        await Assert.That(problem.TryGetProperty("formatvalidated", out _)).IsFalse();
        using var absent = await host.Client.GetAsync("/schemagroups/g/schemas/invalid$details");
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments(RegistryModelKind.Endpoint)]
    [Arguments(RegistryModelKind.CloudEvents)]
    public async Task KestrelEnforcesOptedInEndpointUsageWithoutPublishingInvalidPatches(RegistryModelKind kind)
    {
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(kind));
        using var created = await host.Client.PutAsync("/endpoints/e",
            Json("""{"usage":["subscriber","consumer"],"protocol":"MQTT/5.0"}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var rejected = await host.Client.PatchAsync("/endpoints/e", Json("""{"protocol":"HTTP"}"""));
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("type").GetString()).IsEqualTo("https://github.com/xregistry/spec/blob/main/core/spec.md#invalid_attribute");
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        using var get = await host.Client.GetAsync("/endpoints/e");
        var unchanged = RegistryJson.Parse(await get.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(unchanged.GetProperty("protocol").GetString()).IsEqualTo("MQTT/5.0");
        await Assert.That(unchanged.GetProperty("usage").GetArrayLength()).IsEqualTo(2);
        await Assert.That(unchanged.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task KestrelDoesNotInferEndpointDomainFromACollectionName()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"endpoints":{"singular":"endpoint","attributes":{"kind":{"type":"string"}}}}}
            """));
        await using var host = await TestHost.StartAsync(model: model);
        using var response = await host.Client.PutAsync("/endpoints/e", Json("""{"kind":"ordinary-group"}"""));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var metadata = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("ordinary-group");
        await Assert.That(metadata.TryGetProperty("usage", out _)).IsFalse();
    }

    [Test]
    public async Task KestrelUsesRawStatusHeadersAndConfiguredRootRatherThanHost()
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f")
        {
            Content = new ByteArrayContent([0, 255, 123, 0])
        };
        request.Headers.Host = "attacker.invalid";
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var created = await host.Client.SendAsync(request);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.AbsoluteUri).IsEqualTo("https://public.example/registry/teams/g/files/f");
        await Assert.That(created.Headers.GetValues("Link").Single()).IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
        await Assert.That(created.Headers.GetValues("xRegistry-versionid").Single()).IsEqualTo("1");
        await Assert.That(Convert.ToBase64String(await created.Content.ReadAsByteArrayAsync())).IsEqualTo("AP97AA==");
        using var metadata = await host.Client.GetAsync("/teams/g/files/f$details");
        await Assert.That(metadata.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var root = RegistryJson.Parse(await metadata.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(root.GetProperty("self").GetString()).IsEqualTo("https://public.example/registry/teams/g/files/f$details");
        await Assert.That(root.GetProperty("fileid").GetString()).IsEqualTo("f");
    }

    [Test]
    public async Task KestrelHostsAnApplicationOwnedRegistryBackend()
    {
        var backend = new TestRegistryHostBackend();
        await using var host = await TestHost.StartAsync(hostBackend: backend);
        using var response = await host.Client.GetAsync("/");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement
            .GetProperty("backend").GetBoolean()).IsTrue();
        await Assert.That(backend.AuthorizationCount).IsEqualTo(2);
        await Assert.That(backend.DescriptionCount).IsEqualTo(1);
        await Assert.That(backend.ExecutionCount).IsEqualTo(1);
        await Assert.That(backend.ResponsePreparationCount).IsEqualTo(1);
    }

    [Test]
    public async Task CustomBackendDoesNotPublishWhenResponsePreparationFails()
    {
        var backend = new TestRegistryHostBackend
        {
            AllowedActions = [RegistryAction.Replace],
            Limits = new() { MaxResponseBytes = 1 },
            ResponseMetadata = RegistryJson.Parse("""{"response":"exceeds budget"}""")
        };
        await using var host = await TestHost.StartAsync(hostBackend: backend);
        using var response = await host.Client.PutAsync("/custom", Json("{}"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotAcceptable);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement
            .GetProperty("code").GetString()).IsEqualTo("too_large");
        await Assert.That(backend.ExecutionCount).IsEqualTo(1);
        await Assert.That(backend.ResponsePreparationCount).IsZero();
        await Assert.That(backend.Published).IsFalse();
    }

    [Test]
    public async Task RequestScopeIsSharedAcrossBackendPhasesAndDisposedAfterResponsePreparation()
    {
        var backend = new TestRegistryHostBackend();
        var scope = new TestRequestScope(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "scoped-caller")], "test")));
        var factoryCalls = 0;
        await using var host = await TestHost.StartAsync(hostBackend: backend, httpOptions: new RegistryHttpOptions
        {
            RequestScopeFactory = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                factoryCalls++;
                return ValueTask.FromResult<IRegistryRequestScope>(scope);
            }
        });

        using var response = await host.Client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(factoryCalls).IsEqualTo(1);
        await Assert.That(scope.DisposeCount).IsEqualTo(1);
        await Assert.That(backend.RequestScopes).IsNotEmpty();
        await Assert.That(backend.RequestScopes.All(observed => ReferenceEquals(observed, scope))).IsTrue();
        await Assert.That(scope.Caller.FindFirst(ClaimTypes.NameIdentifier)!.Value).IsEqualTo("scoped-caller");
    }

    [Test]
    public async Task RequestScopeAuthorizationFailureIsAnUnauthorizedProblem()
    {
        var backend = new TestRegistryHostBackend();
        await using var host = await TestHost.StartAsync(hostBackend: backend, httpOptions: new RegistryHttpOptions
        {
            RequestScopeFactory = (_, _) =>
                ValueTask.FromException<IRegistryRequestScope>(new UnauthorizedAccessException())
        });

        using var response = await host.Client.GetAsync("/");
        var problem = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("unauthorized");
        await Assert.That(backend.DescriptionCount).IsZero();
        await Assert.That(backend.ExecutionCount).IsZero();
    }

    [Test]
    public async Task RequestScopeIsDisposedWhenBackendAuthorizationRejectsTheRequest()
    {
        var backend = new TestRegistryHostBackend
        {
            AuthorizationFailure = new UnauthorizedAccessException()
        };
        var scope = new TestRequestScope(new ClaimsPrincipal(new ClaimsIdentity("test")));
        await using var host = await TestHost.StartAsync(hostBackend: backend, httpOptions: new RegistryHttpOptions
        {
            RequestScopeFactory = (_, _) => ValueTask.FromResult<IRegistryRequestScope>(scope)
        });

        using var response = await host.Client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var problem = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("server_error");
        await Assert.That(problem.GetProperty("detail").GetString())
            .DoesNotContain("acquire this Registry request scope");
        await Assert.That(backend.ExecutionCount).IsZero();
        await Assert.That(scope.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task RequestScopeDisposalFailureDoesNotSuppressPreparedResponseOrDisposeTwice()
    {
        var backend = new TestRegistryHostBackend();
        var scope = new TestRequestScope(new ClaimsPrincipal(new ClaimsIdentity("test")),
            new TimeoutException("Injected cleanup failure."));
        await using var host = await TestHost.StartAsync(hostBackend: backend, httpOptions: new RegistryHttpOptions
        {
            RequestScopeFactory = (_, _) => ValueTask.FromResult<IRegistryRequestScope>(scope)
        });

        using var response = await host.Client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(backend.ResponsePreparationCount).IsEqualTo(1);
        await Assert.That(scope.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("GET", "/", 200)]
    [Arguments("HEAD", "/", 200)]
    [Arguments("GET", "/.xregistry", 200)]
    [Arguments("GET", "/model", 200)]
    [Arguments("GET", "/modelsource", 200)]
    [Arguments("GET", "/capabilities", 200)]
    [Arguments("GET", "/capabilitiesoffered", 200)]
    [Arguments("GET", "/export", 200)]
    [Arguments("GET", "/teams", 200)]
    [Arguments("PUT", "/teams", 405)]
    [Arguments("DELETE", "/", 405)]
    [Arguments("PATCH", "/model", 405)]
    [Arguments("POST", "/modelsource", 405)]
    [Arguments("GET", "/unknown", 400)]
    public async Task RootAndAdministrativeRouteMatrixHasIndependentStatuses(string method, string path, int expectedStatus)
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await host.Client.SendAsync(request);
        await Assert.That((int)response.StatusCode).IsEqualTo(expectedStatus);
        await Assert.That(response.Headers.Contains("Link")).IsTrue();
        if (expectedStatus == 405)
        {
            await Assert.That(response.Content.Headers.Allow.Contains("GET")).IsTrue();
        }

        if (method == "HEAD")
        {
            await Assert.That((await response.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
            await Assert.That(response.Content.Headers.ContentLength.GetValueOrDefault()).IsGreaterThan(0L);
        }
    }

    [Test]
    public async Task EntityRouteMatrixAndDetailsAreModelDriven()
    {
        await using var host = await TestHost.StartAsync();
        using var created = await host.Client.PutAsync("/teams/g/notes/n", Json("""{"name":"metadata-only"}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        foreach (var path in new[] { "/teams/g", "/teams/g/notes", "/teams/g/notes/n", "/teams/g/notes/n/meta",
            "/teams/g/notes/n/versions", "/teams/g/notes/n/versions/1" })
        {
            using var get = await host.Client.GetAsync(path);
            await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var options = await host.Client.SendAsync(new(HttpMethod.Options, path));
            await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(options.Content.Headers.Allow.Contains("OPTIONS")).IsTrue();
        }

        using var equivalent = await host.Client.GetAsync("/teams/g/notes/n$details");
        await Assert.That(RegistryJson.Parse(await equivalent.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("self").GetString())
            .IsEqualTo("https://public.example/registry/teams/g/notes/n");
        using var patch = await host.Client.PatchAsync("/teams/g/files/f", Json("{}"));
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(RegistryJson.Parse(await patch.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("details_required");
        using var deleteMeta = await host.Client.DeleteAsync("/teams/g/notes/n/meta");
        await Assert.That(deleteMeta.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task RawEpochZeroGuardFailsWith400AndNestedFailureHasNoEffects()
    {
        await using var host = await TestHost.StartAsync();
        using var first = await host.Client.PatchAsync("/", Json("""{"epoch":0,"name":"updated"}"""));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var stale = await host.Client.PatchAsync("/", Json("""{"epoch":0,"name":"not written"}"""));
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await stale.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("type").GetString()).IsEqualTo("https://github.com/xregistry/spec/blob/main/core/spec.md#mismatched_epoch");
        await Assert.That(problem.GetProperty("status").GetInt32()).IsEqualTo(400);
        await Assert.That(problem.GetProperty("args").GetProperty("bad_epoch").GetString()).IsEqualTo("0");
        await Assert.That(problem.GetProperty("args").GetProperty("epoch").GetString()).IsEqualTo("1");
        using var nested = await host.Client.PatchAsync("/", Json("""{"teams":{"one":{},"two":{"notallowed":1}}}"""));
        await Assert.That(nested.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var root = await host.Client.GetAsync("/");
        var metadata = RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(metadata.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        await Assert.That(metadata.GetProperty("name").GetString()).IsEqualTo("updated");
        await Assert.That(metadata.GetProperty("epoch").GetInt32()).IsEqualTo(1);
    }

    [Test]
    [Arguments("application/octet-stream", "eyJhIjoxfQ==")]
    [Arguments("application/json", "/wCAew==")]
    [Arguments("application/json", "")]
    public async Task KestrelPreservesBinaryJsonLookingNonUtf8AndPresentEmptyBodies(string contentType, string base64)
    {
        await using var host = await TestHost.StartAsync();
        using var input = new ByteArrayContent(Convert.FromBase64String(base64));
        input.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var created = await host.Client.PutAsync("/teams/g/files/f", input);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(Convert.ToBase64String(await created.Content.ReadAsByteArrayAsync())).IsEqualTo(base64);
        using var get = await host.Client.GetAsync("/teams/g/files/f");
        await Assert.That(Convert.ToBase64String(await get.Content.ReadAsByteArrayAsync())).IsEqualTo(base64);
        using var inline = await host.Client.GetAsync("/teams/g/files/f$details?inline=file&binary");
        await Assert.That(RegistryJson.Parse(await inline.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("filebase64").GetString()).IsEqualTo(base64);
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, "/teams/g/files/f"));
        await Assert.That(head.Content.Headers.ContentLength).IsEqualTo((long)Convert.FromBase64String(base64).Length);
        await Assert.That((await head.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task ScalarAndMapHeadersUseIndependentEscapedWireValues()
    {
        await using var host = await TestHost.StartAsync();
        using var first = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([1]) };
        first.Headers.TryAddWithoutValidation("xRegistry-name", "hello%20%E2%82%AC%0A");
        first.Headers.TryAddWithoutValidation("xRegistry-labels.a.b", "one%25two");
        first.Headers.TryAddWithoutValidation("xRegistry-labels.old", "remove");
        first.Headers.TryAddWithoutValidation("xRegistry-isdefault", "invalid but ignored");
        using var created = await host.Client.SendAsync(first);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.GetValues("xRegistry-name").Single()).IsEqualTo("hello%20%E2%82%AC%0A");
        await Assert.That(created.Headers.GetValues("xRegistry-labels.a.b").Single()).IsEqualTo("one%25two");
        using var next = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([]) };
        next.Headers.TryAddWithoutValidation("xRegistry-name", "null");
        next.Headers.TryAddWithoutValidation("xRegistry-labels.a.b", "\"quoted%20value\"");
        using var replaced = await host.Client.SendAsync(next);
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replaced.Headers.Contains("xRegistry-name")).IsFalse();
        await Assert.That(replaced.Headers.Contains("xRegistry-labels.old")).IsFalse();
        await Assert.That(replaced.Headers.GetValues("xRegistry-labels.a.b").Single()).IsEqualTo("quoted%20value");
        await Assert.That(replaced.Content.Headers.ContentType).IsNull();
    }

    [Test]
    public async Task AnonymousWritesAndUntrustedIdentityHeadersCannotBypassAuthorization()
    {
        await using var host = await TestHost.StartAsync(authenticated: false);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g") { Content = Json("{}") };
        request.Headers.TryAddWithoutValidation("x-user", "admin");
        request.Headers.TryAddWithoutValidation("x-roles", "administrator");
        using var denied = await host.Client.SendAsync(request);
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var modelDenied = await host.Client.PutAsync("/modelsource", Json("{}"));
        await Assert.That(modelDenied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var root = await host.Client.GetAsync("/");
        await Assert.That(root.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidJsonInvalidHeadersAndOversizeBodiesReturnErrorsNotEmptySuccess()
    {
        await using var host = await TestHost.StartAsync(limits: new() { MaxDocumentBytes = 3 });
        using var malformed = await host.Client.PutAsync("/teams/g", Json("{"));
        await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await malformed.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("parsing_data");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([1]) };
        request.Headers.TryAddWithoutValidation("xRegistry-name", "%C0%A0");
        using var header = await host.Client.SendAsync(request);
        await Assert.That(header.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await header.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("header_error");
        using var oversized = await host.Client.PutAsync("/teams/g/files/f", new ByteArrayContent([1, 2, 3, 4]));
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task EveryNestedAndModelMutationRequiresTheHostPolicy()
    {
        await using var host = await TestHost.StartAsync(policy: new DenyPolicy());
        using var nested = await host.Client.PatchAsync("/", Json("""{"teams":{"allowed":{},"blocked":{}}}"""));
        await Assert.That(nested.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var model = await host.Client.PutAsync("/modelsource", Json("{}"));
        await Assert.That(model.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var inlinedModel = await host.Client.PatchAsync("/", Json("""{"modelsource":{}}"""));
        await Assert.That(inlinedModel.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task HeaderPreparationFailureRejectsBinaryMutationBeforePublication()
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Client.PutAsync("/teams/g/files/f?inline=*",
            new StringContent("body", Encoding.UTF8, "text/plain"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var setup = await host.Client.PatchAsync("/teams/g/files/f$details", Json("""{"contenttype":"not a media type"}"""));
        await Assert.That(setup.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([4, 5]) };
        invalid.Content.Headers.TryAddWithoutValidation("Content-Type", "not a media type");
        using var rejected = await host.Client.SendAsync(invalid);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("header_error");
        using var details = await host.Client.GetAsync("/teams/g/files/f$details?inline=file&binary");
        await Assert.That(RegistryJson.Parse(await details.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("filebase64").GetString()).IsEqualTo("Ym9keQ==");
    }

    [Test]
    public async Task UnknownCommitOutcomeIsAnExplicit503WithoutSuccessfulEntityResponse()
    {
        await using var host = await TestHost.StartAsync(persistence: new UnknownCommitPersistence());
        using var response = await host.Client.PutAsync("/teams/g", Json("{}"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.GetValues("xRegistry-commit-outcome").Single()).IsEqualTo("unknown");
        await Assert.That(response.Headers.Location).IsNull();
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("commit_outcome_unknown");
    }

    [Test]
    public async Task DisconnectedPartialBodyCancelsWithoutCreatingParents()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TestHost.StartAsync(started: context =>
        {
            if (context.Request.Method == "PUT")
            {
                context.RequestAborted.Register(() => aborted.TrySetResult());
                started.TrySetResult();
            }
        }, completedAsync: async context =>
        {
            if (context.Request.Method == "PUT")
            {
                // Kestrel can retire the cancellation token when the application pipeline returns.
                await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                completed.TrySetResult();
            }
        });
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, host.Client.BaseAddress!.Port);
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                "PUT /teams/g/files/f HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\nConnection: close\r\n\r\nx"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A send half-close permits a response; reset the connection to simulate disconnection.
            socket.Client.LingerState = new LingerOption(true, 0);
            socket.Client.Close();
            await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task SlowRequestBodyExpiresWithinConfiguredLifetimeWithoutPublishing()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TestHost.StartAsync(httpOptions: new() { RequestTimeout = TimeSpan.FromMilliseconds(100) },
            completed: context =>
            {
                if (context.Request.Method == "PUT")
                {
                    completed.TrySetResult();
                }
            });
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, host.Client.BaseAddress!.Port);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "PUT /teams/g/files/f HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\nConnection: close\r\n\r\nx"));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = await stream.ReadAsync(buffer, readTimeout.Token);
        await Assert.That(Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HTTP/1.1 408", StringComparison.Ordinal)).IsTrue();
        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    private static async Task<PublicationOpenedSnapshot> CreateOpenedSnapshot()
    {
        var access = new PublicationRegistryAccess("http", "https://publisher.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var registryBytes = Encoding.UTF8.GetBytes("""{"registryid":"captured"}""");
        var registryDescriptor = CreateDescriptor(access,
            "/publications/team/objects/registry/versions/content", registryBytes);
        var manifest = new JsonObject
        {
            ["format"] = "xregistry-publication/1",
            ["scope"] = "urn:example:http-projection",
            ["capture"] = "assembled",
            ["completeness"] = "offline-complete",
            ["registry"] = DescriptorJson(registryDescriptor),
            ["documents"] = new JsonArray()
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var snapshotId = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var snapshotDescriptor = CreateDescriptor(access,
            $"/publications/team/snapshots/{snapshotId}/versions/manifest", manifestBytes);
        var channelMetadata = new PublicationChannelMetadata("urn:example:http-projection", snapshotDescriptor,
            new BigInteger(3));
        var binding = new ProjectionPublicationBinding(access, channelMetadata, new Dictionary<string, byte[]>
        {
            [snapshotDescriptor.Reference.Version] = manifestBytes,
            [registryDescriptor.Reference.Version] = registryBytes
        });
        var client = new PublicationReadClient((_, _) =>
            ValueTask.FromResult<IPublicationReadBinding>(binding));
        return await client.OpenChannelAsync(access, "/publications/team/channels/stable");
    }

    private static PublicationByteDescriptor CreateDescriptor(PublicationRegistryAccess access, string version, byte[] bytes) =>
        new(new PublicationVersionReference(access, version),
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length);

    private static JsonObject DescriptorJson(PublicationByteDescriptor descriptor) => new()
    {
        ["reference"] = new JsonObject
        {
            ["registry"] = new JsonObject
            {
                ["binding"] = descriptor.Reference.Registry.Binding,
                ["endpoint"] = descriptor.Reference.Registry.Endpoint
            },
            ["version"] = descriptor.Reference.Version
        },
        ["digest"] = descriptor.Digest,
        ["size"] = (long)descriptor.Size
    };

    private sealed class ProjectionPublicationBinding(PublicationRegistryAccess access,
        PublicationChannelMetadata metadata, IReadOnlyDictionary<string, byte[]> objects) : IPublicationReadBinding
    {
        public PublicationRegistryAccess Access { get; } = access;

        public ValueTask<PublicationChannelMetadata> ReadChannelMetadataAsync(string channelResource,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(metadata);
        }

        public ValueTask<Stream> OpenVersionDocumentAsync(string version, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(objects[version], writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    internal sealed class TestHost(WebApplication application, HttpClient client) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;

        internal static async Task<TestHost> StartAsync(bool authenticated = true, IRegistryAuthorizationPolicy? policy = null,
            RegistryLimits? limits = null, IRegistryPersistence? persistence = null, RegistryHttpOptions? httpOptions = null,
            Action<HttpContext>? completed = null, RegistryModel? model = null, IRegistryResourceValidator? resourceValidator = null,
            bool allowCapabilityUpdates = false, IRegistryDocumentReferencePolicy? documentReferencePolicy = null,
            string pathBase = "", IRegistryHostBackend? hostBackend = null, Action<HttpContext>? started = null,
            PublicationOpenedSnapshot? publicationSnapshot = null, Func<HttpContext, Task>? completedAsync = null,
            bool useBoundPublicRoot = false, Action<WebApplication>? configureEndpoints = null)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            if (pathBase.Length != 0)
            {
                application.UsePathBase(pathBase);
            }
            if (started is not null)
            {
                application.Use(async (context, next) =>
                {
                    started(context);
                    await next(context);
                });
            }
            if (completed is not null || completedAsync is not null)
            {
                application.Use(async (context, next) =>
                {
                    try
                    {
                        await next(context);
                    }
                    finally
                    {
                        completed?.Invoke(context);
                        if (completedAsync is not null)
                        {
                            await completedAsync(context);
                        }
                    }
                });
            }

            if (authenticated)
            {
                application.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "isolated-loopback-test")], "test"));
                    await next(context);
                });
            }

            var publicRoot = new Uri("https://public.example/registry");
            configureEndpoints?.Invoke(application);
            if (useBoundPublicRoot)
            {
                application.Map("/starting", () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
                application.UseRouting();
                application.UseEndpoints(static _ => { });
                await application.StartAsync();
                publicRoot = new Uri(application.Urls.Single());
            }
            var engine = new RegistryEngine(new()
            {
                RegistryId = "http-test",
                PublicRoot = publicRoot,
                Model = model ?? RegistryModel.Compile(RegistryJson.Parse("""
                    {"groups":{"teams":{"singular":"team","resources":{
                     "notes":{"singular":"note","hasdocument":false},"files":{"singular":"file","maxversions":2}}}}}
                    """)),
                AllowAnonymousReads = true,
                AllowCapabilityUpdates = allowCapabilityUpdates,
                DocumentReferencePolicy = documentReferencePolicy,
                ResourceValidator = resourceValidator,
                Limits = limits ?? new()
            }, persistence ?? new InMemoryRegistryPersistence(), policy ?? new PermitPolicy());
            if (publicationSnapshot is not null)
            {
                if (hostBackend is not IReadOnlyRegistryHostBackend readOnlyBackend)
                {
                    throw new ArgumentException("The selected projection test backend must expose read-only state.",
                        nameof(hostBackend));
                }
                application.MapPublicationSnapshot(readOnlyBackend, publicationSnapshot, httpOptions);
            }
            else
            {
                application.MapXRegistry(hostBackend ?? engine, httpOptions);
            }
            if (!useBoundPublicRoot) { await application.StartAsync(); }
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            return new(application, new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

    private sealed class PermitPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class DenyPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(access != RegistryAccess.UpdateModel &&
                !(path.Kind == RegistryPathKind.Group && path.GroupId?.Value == "blocked"));
    }

    private sealed class TestRegistryHostBackend : IReadOnlyRegistryHostBackend
    {
        internal int AuthorizationCount { get; private set; }
        internal int DescriptionCount { get; private set; }
        internal int ExecutionCount { get; private set; }
        internal int ResponsePreparationCount { get; private set; }
        internal bool Published { get; private set; }
        internal UnauthorizedAccessException? AuthorizationFailure { get; init; }
        internal bool DenyAuthorization { get; init; }
        internal bool ReadOnly { get; init; }
        public bool IsReadOnly => ReadOnly;
        internal List<IRegistryRequestScope?> RequestScopes { get; } = [];
        public Uri PublicRoot { get; } = new("https://public.example/registry");
        public RegistryLimits Limits { get; init; } = new();
        internal IReadOnlyList<RegistryAction> AllowedActions { get; init; } = [RegistryAction.Read];
        internal RegistryJson ResponseMetadata { get; init; } = RegistryJson.Parse("""{"backend":true}""");

        public ValueTask AuthorizeRequestAsync(RegistryAction action, RegistryPath path, RegistryOperationContext context,
            CancellationToken cancellationToken = default)
        {
            AuthorizationCount++;
            if (AuthorizationFailure is { } failure)
            {
                return ValueTask.FromException(failure);
            }
            if (DenyAuthorization)
            {
                return ValueTask.FromException(new RegistryException(new("forbidden", path.EscapedPath,
                    "The caller is not authorized to read this registry.")));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<RegistryPath> ResolvePathAsync(RegistryAction action, string escapedPath,
            RegistryOperationContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RegistryPath.Parse(escapedPath));

        public async ValueTask<RegistryRouteDescription> DescribeAsync(RegistryAction action, RegistryPath path,
            RegistryOperationContext context, IReadOnlyList<KeyValuePair<string, string?>> parameters,
            CancellationToken cancellationToken = default)
        {
            DescriptionCount++;
            RequestScopes.Add(context.RequestScope);
            await AuthorizeRequestAsync(action, path, context, cancellationToken);
            return new RegistryRouteDescription(path, "custom", null, AllowedActions);
        }

        public async ValueTask<RegistryResult> ExecuteAsync(RegistryOperation operation, RegistryOperationContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            RequestScopes.Add(context.RequestScope);
            await AuthorizeRequestAsync(operation.Action, operation.Path, context, cancellationToken);
            var result = new RegistryResult(RegistryResultKind.Success, operation.Path, ResponseMetadata);
            if (context.PrepareResponseAsync is { } prepare)
            {
                await prepare(result, cancellationToken);
                ResponsePreparationCount++;
            }

            Published = true;
            return result;
        }
    }

    private sealed class TestRequestScope(ClaimsPrincipal caller, Exception? disposalFailure = null) : IRegistryRequestScope
    {
        public ClaimsPrincipal Caller { get; } = caller;
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return disposalFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposalFailure);
        }
    }

    private sealed class UnknownCommitPersistence : IRegistryPersistence
    {
        private readonly InMemoryRegistryPersistence _inner = new();
        public bool IsReadOnly => false;
        public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) => _inner.ReadSnapshotAsync(cancellationToken);
        public async ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) =>
            new UnknownCommit(await _inner.PrepareAsync(expectedGeneration, mutations, cancellationToken));

        private sealed class UnknownCommit(IRegistryCommit inner) : IRegistryCommit
        {
            public ValueTask<long> CommitAsync(CancellationToken cancellationToken = default) =>
                throw new RegistryCommitOutcomeUnknownException("Injected uncertain publication.");
            public void Dispose() => inner.Dispose();
        }
    }
}
