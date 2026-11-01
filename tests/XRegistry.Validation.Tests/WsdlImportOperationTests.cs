// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class WsdlImportOperationTests
{
    private const string RootUri = "https://example.test/relocated/root.wsdl";
    private const string Operation = "<portType name=\"Port\"><operation name=\"Read\"><input message=\"t:Message\"/></operation></portType>";
    private const string Message = "<message name=\"Message\"/>";

    [Test]
    public async Task WsdlImportsResolveContainingBasesAndCacheSharedDocumentsWithNativeOrigin()
    {
        var root = Document("urn:entry", """
            <import namespace="urn:api" location="api.wsdl" xml:base="nested/"/>
            <import namespace="urn:messages" location="/shared/messages.wsdl"/>
            """);
        var api = Document("urn:api", """
            <import namespace="urn:messages" location="messages.wsdl" xml:base="../../shared/"/>
            <portType name="Port"><operation name="Read"><input message="m:Message" xmlns:m="urn:messages"/></operation></portType>
            """);
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", root, Reference("urn:api"),
            new Uri(RootUri), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(uri switch
                {
                    "https://example.test/relocated/nested/api.wsdl" => api,
                    "https://example.test/shared/messages.wsdl" => Document("urn:messages", Message),
                    _ => null
                });
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.SequenceEqual([
            "https://example.test/relocated/nested/api.wsdl", "https://example.test/shared/messages.wsdl"])).IsTrue();
        await Assert.That(result.DocumentUri!.AbsoluteUri).IsEqualTo(calls[0]);
        await Assert.That(result.WsdlOperation!.InterfaceName).IsEqualTo("{urn:api}Port");
        await Assert.That(result.WsdlOperation.InputMessage).IsEqualTo("{urn:messages}Message");
        await Assert.That(result.WsdlOperation.MessageExchangePattern).IsEqualTo(WsdlMessageExchangePattern.OneWay);
    }

    [Test]
    [Arguments("missing", ApiOperationResolutionStatus.Indeterminate, "reference.unresolved")]
    [Arguments("denied", ApiOperationResolutionStatus.Denied, "dependency.denied")]
    [Arguments("wrong-namespace", ApiOperationResolutionStatus.Invalid, "wsdl.import_namespace")]
    [Arguments("wrong-kind", ApiOperationResolutionStatus.Unsupported, "wsdl.import_kind")]
    [Arguments("malformed", ApiOperationResolutionStatus.Invalid, "wsdl.xml")]
    [Arguments("dtd", ApiOperationResolutionStatus.Invalid, "wsdl.xml")]
    public async Task WsdlImportFailurePreventsAnEarlierSelectableOperation(
        string mode, ApiOperationResolutionStatus status, string code)
    {
        var root = Document("urn:api", Message + Operation + "<import namespace=\"urn:other\" location=\"other.wsdl\"/>");
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "text/xml", root, Reference("urn:api"),
            new Uri(RootUri), (_, _) =>
            {
                calls++;
                if (mode == "denied") { throw new UnauthorizedAccessException("private policy details"); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(mode switch
                {
                    "missing" => (ReadOnlyMemory<byte>?)null,
                    "wrong-namespace" => Document("urn:wrong", ""),
                    "wrong-kind" => Encoding.UTF8.GetBytes("<data/>"),
                    "malformed" => Encoding.UTF8.GetBytes("<definitions"),
                    _ => Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///must-not-read'>]><x>&e;</x>")
                });
            });
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.WsdlOperation).IsNull();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("child.wsdl", "dependency.base")]
    [Arguments("/child.wsdl", "dependency.base")]
    [Arguments("//other.test/child.wsdl", "dependency.base")]
    [Arguments("child%ZZ.wsdl", "dependency.uri")]
    [Arguments("child\\x.wsdl", "dependency.uri")]
    [Arguments(" child.wsdl", "dependency.uri")]
    [Arguments("child.wsdl#part", "dependency.uri")]
    public async Task WsdlImportUriFailuresDoNotDispatch(string location, string code)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Document("urn:entry", "<import namespace=\"urn:api\" location=\"" + location + "\"/>"), Reference("urn:api"),
            resolveReference: (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Status).IsEqualTo(code == "dependency.base"
            ? ApiOperationResolutionStatus.Indeterminate : ApiOperationResolutionStatus.Invalid);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task WsdlAbsoluteAndNetworkPathImportsUseAuthoredAuthorityWithoutAmbientRetrieval()
    {
        foreach (var location in new[] { "https://other.test/api.wsdl", "//other.test/api.wsdl" })
        {
            string? requested = null;
            var root = Document("urn:entry", "<import namespace=\"urn:api\" location=\"" + location + "\"/>");
            var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", root,
                Reference("urn:api"), location.StartsWith('/') ? new Uri(RootUri) : null, (uri, _) =>
                {
                    requested = uri;
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document("urn:api", Message + Operation));
                });
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
            await Assert.That(requested).IsEqualTo("https://other.test/api.wsdl");
            await Assert.That(result.DocumentUri!.AbsoluteUri).IsEqualTo(requested);
        }
        var unresolved = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Document("urn:entry", "<import namespace=\"urn:api\" location=\"https://other.test/api.wsdl\"/>"),
            Reference("urn:api"));
        await Assert.That(unresolved.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(unresolved.Diagnostic!.Code).IsEqualTo("reference.unresolved");
    }

    [Test]
    public async Task WsdlImportCyclesFailAndRepeatedNamespaceClaimsAreCheckedAfterCaching()
    {
        var calls = 0;
        var cycle = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Document("urn:entry", "<import namespace=\"urn:api\" location=\"api.wsdl\"/>"), Reference("urn:api"),
            new Uri(RootUri), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document("urn:api",
                    "<import namespace=\"urn:entry\" location=\"root.wsdl\"/>" + Message + Operation));
            });
        await Assert.That(cycle.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(cycle.Diagnostic!.Code).IsEqualTo("dependency.cycle");
        await Assert.That(calls).IsEqualTo(1);
        calls = 0;
        var inconsistent = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Document("urn:entry", """
                <import namespace="urn:api" location="api.wsdl"/>
                <import namespace="urn:wrong" location="api.wsdl"/>
                """), Reference("urn:api"), new Uri(RootUri), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document("urn:api", Message + Operation));
            });
        await Assert.That(inconsistent.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(inconsistent.Diagnostic!.Code).IsEqualTo("wsdl.import_namespace");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(32, ApiOperationResolutionStatus.Selected)]
    [Arguments(33, ApiOperationResolutionStatus.Indeterminate)]
    public async Task WsdlDistinctImportsHonorTheInclusiveDocumentLimit(int count, ApiOperationResolutionStatus status)
    {
        var calls = 0;
        var root = Document("urn:api", Message + Operation + string.Concat(Enumerable.Range(0, count)
            .Select(index => "<import namespace=\"urn:schema\" location=\"schema" + index + ".xsd\"/>")));
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", root, Reference("urn:api"),
            new Uri(RootUri), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                    "<schema xmlns=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:schema\"/>"));
            });
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(calls).IsEqualTo(32);
        if (count == 33)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.references");
            await Assert.That(result.WsdlOperation).IsNull();
        }
    }

    [Test]
    [Arguments(0, ApiOperationResolutionStatus.Selected)]
    [Arguments(1, ApiOperationResolutionStatus.Indeterminate)]
    public async Task WsdlImportsShareTheInclusiveAggregateByteLimit(int extra, ApiOperationResolutionStatus status)
    {
        var root = Document("urn:api", Message + Operation + string.Concat(Enumerable.Range(0, 4)
            .Select(index => "<import namespace=\"urn:schema\" location=\"schema" + index + ".xsd\"/>")));
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", root, Reference("urn:api"),
            new Uri(RootUri), (_, _) =>
            {
                var size = ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes;
                if (calls++ == 3) { size -= root.Length - extra; }
                const string schema = "<schema xmlns=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:schema\"><!--";
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(schema +
                    new string('x', size - schema.Length - "--></schema>".Length) + "--></schema>"));
            });
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(calls).IsEqualTo(4);
        if (extra == 1)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.total_bytes");
        }
    }

    [Test]
    [Arguments("<import namespace=\"urn:api\"/>", "wsdl.import")]
    [Arguments("<import location=\"api.wsdl\"/>", "wsdl.import")]
    [Arguments("<import namespace=\"urn:api\" location=\"api.wsdl\" xml:base=\"bad%ZZ\"/>", "dependency.uri")]
    public async Task WsdlMalformedImportDeclarationsDoNotDispatch(string import, string code)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", Document("urn:entry", import),
            Reference("urn:api"), new Uri(RootUri), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(Message, "wsdl.duplicate_message")]
    [Arguments("<portType name=\"Port\"/>", "wsdl.duplicate_interface")]
    public async Task WsdlDifferentImportedDocumentsCannotSilentlyReplaceComponents(string component, string code)
    {
        var calls = 0;
        var root = Document("urn:api", Message + Operation + "<import namespace=\"urn:api\" location=\"duplicate.wsdl\"/>");
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", root, Reference("urn:api"),
            new Uri(RootUri), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Document("urn:api", component));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.WsdlOperation).IsNull();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task WsdlRelativeTargetNamespacesFailBeforeImportAcquisition()
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml",
            Document("relative/namespace", "<import namespace=\"urn:api\" location=\"api.wsdl\"/>"),
            Reference("urn:api"), new Uri(RootUri), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("wsdl.namespace");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task WsdlImportCancellationDoesNotDispatchLaterDependencies()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var root = Document("urn:entry", """
            <import namespace="urn:api" location="api.wsdl"/>
            <import namespace="urn:later" location="later.wsdl"/>
            """);
        await Assert.That(async () => await ApiOperationDocumentResolver.ResolveAsync("WSDL/1.1", "application/xml", root,
            Reference("urn:api"), new Uri(RootUri), (_, token) =>
            {
                calls++;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            }, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    private static ApiOperationReference Reference(string ns) => ApiOperationReference.Parse(
        "/apigroups/g/apis/a#interfaces/%7B" + ns + "%7DPort/operations/Read");

    private static byte[] Document(string ns, string content) => Encoding.UTF8.GetBytes(
        "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\" xmlns:t=\"" + ns +
        "\" targetNamespace=\"" + ns + "\">" + content + "</definitions>");
}
