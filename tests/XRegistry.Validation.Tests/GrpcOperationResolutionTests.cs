// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class GrpcOperationResolutionTests
{
    [Test]
    [Arguments("has space.proto")]
    [Arguments("has\\tcontrol.proto")]
    [Arguments("has\\ncontrol.proto")]
    [Arguments("bad%.proto")]
    [Arguments("bad%GG.proto")]
    public async Task GrpcRejectsMalformedImportUrisBeforeRetrieval(string import)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("syntax='proto3'; import '" + import + "'; service S {}"),
            Reference("S"), new Uri("https://example.test/root.proto"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("syntax='proto3';"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.uri");
        await Assert.That(result.GrpcOperation).IsNull();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task GrpcPreservesEncodedImportPathsForCallerAuthorization()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; import 'has%20space.proto'; service S { rpc Get(Row) returns(Row); }
                """), Reference("S"), new Uri("https://example.test/root.proto"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("syntax='proto3'; message Row {}"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.GrpcOperation!.RequestType).IsEqualTo("Row");
        await Assert.That(calls.SequenceEqual(["https://example.test/has%20space.proto"])).IsTrue();
    }

    [Test]
    public async Task GrpcValidatesImportedDocumentUriSpellingsBeforeResolvingTheirDependencies()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; import 'child.proto'; message Row {} service S { rpc Get(Row) returns(Row); }
                """), Reference("S"), new Uri("https://example.test/root.proto"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                    "syntax='proto3'; import 'bad%.proto';"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.uri");
        await Assert.That(result.GrpcOperation).IsNull();
        await Assert.That(calls.SequenceEqual(["https://example.test/child.proto"])).IsTrue();
    }

    private static ApiOperationReference Reference(string service = "p.S", string method = "Get") =>
        ApiOperationReference.Parse("/apigroups/g/apis/a#services/" + service + "/operations/" + method);

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task GrpcSelectsTheExactRootServiceMethodAndItsStreamingModes(bool clientStreaming, bool serverStreaming)
    {
        var source = "syntax='proto3'; package p; message Request {} message Response {} " +
            "service Other { rpc Get(Response) returns(Request); } " +
            "service S { rpc Other(Response) returns(Request); rpc Get(" +
            (clientStreaming ? "stream " : "") + "Request) returns(" +
            (serverStreaming ? "stream " : "") + "Response); }";
        var result = ApiOperationDocumentResolver.Resolve("gRPC/proto3", "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(source), Reference());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
        var selected = result.GrpcOperation ?? throw new InvalidOperationException("Expected a native RPC selection.");
        await Assert.That(selected.ServiceName).IsEqualTo("p.S");
        await Assert.That(selected.MethodName).IsEqualTo("Get");
        await Assert.That(selected.RequestType).IsEqualTo("p.Request");
        await Assert.That(selected.ResponseType).IsEqualTo("p.Response");
        await Assert.That(selected.ClientStreaming).IsEqualTo(clientStreaming);
        await Assert.That(selected.ServerStreaming).IsEqualTo(serverStreaming);
    }

    [Test]
    public async Task GrpcSelectsAnUnpackagedServiceAndNestedQualifiedMessageTypes()
    {
        var result = ApiOperationDocumentResolver.Resolve("gRPC/proto3", "TEXT/PLAIN",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; message Envelope { message Input {} message Output {} }
                service S { rpc Get(Envelope.Input) returns(.Envelope.Output) {} }
                """), Reference("S"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var selected = result.GrpcOperation ?? throw new InvalidOperationException("Expected a native RPC selection.");
        await Assert.That(selected.ServiceName).IsEqualTo("S");
        await Assert.That(selected.RequestType).IsEqualTo("Envelope.Input");
        await Assert.That(selected.ResponseType).IsEqualTo("Envelope.Output");
    }

    [Test]
    [Arguments("S", "Get")]
    [Arguments(".p.S", "Get")]
    [Arguments("p.s", "Get")]
    [Arguments("p.S", "get")]
    [Arguments("p.S", "Missing")]
    public async Task GrpcDoesNotFallBackFromAnInexactServiceOrMethod(string service, string method)
    {
        var result = ApiOperationDocumentResolver.Resolve("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("syntax='proto3'; package p; message Row {} service S { rpc Get(Row) returns(Row); }"),
            Reference(service, method));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.not_found");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.GrpcOperation).IsNull();
    }

    [Test]
    [Arguments("syntax='proto2'; service S {}", "protobuf.syntax")]
    [Arguments("service S {}", "protobuf.syntax")]
    [Arguments("syntax='proto3'; message Row {}", "grpc.service")]
    [Arguments("syntax='proto3'; service S { rpc Get(Missing) returns(Missing); }", "protobuf.type")]
    [Arguments("syntax='proto3'; service S { rpc Get(string) returns(string); }", "protobuf.type")]
    [Arguments("syntax='proto3'; enum E { ZERO=0; } service S { rpc Get(E) returns(E); }", "protobuf.type")]
    [Arguments("syntax='proto3'; message Row {} service S { rpc Get(Row) returns(Row); rpc Get(Row) returns(Row); }", "protobuf.duplicate")]
    [Arguments("syntax='proto3'; service S {} service S {}", "protobuf.duplicate")]
    public async Task GrpcValidatesTheWholeSourceBeforeReturningASelection(string source, string code)
    {
        var result = ApiOperationDocumentResolver.Resolve("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes(source), Reference("S"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.GrpcOperation).IsNull();
    }

    [Test]
    [Arguments("application/json")]
    [Arguments("application/octet-stream")]
    [Arguments("application/yaml")]
    public async Task GrpcRejectsUnsupportedRepresentationsWithoutCallingTheResolver(string contentType)
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", contentType,
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'types.proto'; service S {}"), Reference("S"),
            resolveReference: (_, _) => throw new InvalidOperationException("Representation rejection must precede retrieval."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("contenttype.unsupported");
    }

    [Test]
    [Arguments("gRPC/proto2")]
    [Arguments("gRPC/descriptor-set")]
    [Arguments("gRPC/editions")]
    public async Task GrpcDoesNotAdvertiseOrSelectUnsupportedProfiles(string format)
    {
        var result = ApiOperationDocumentResolver.Resolve(format, "text/plain",
            Encoding.UTF8.GetBytes("syntax='proto3'; service S {}"), Reference("S"));
        await Assert.That(ApiOperationDocumentResolver.Capabilities.Formats.Contains(format)).IsFalse();
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("format.unsupported");
        await Assert.That(result.GrpcOperation).IsNull();
    }

    [Test]
    [Arguments("operations/Get")]
    [Arguments("/services/S/operations/Get")]
    [Arguments("interfaces/S/operations/Get")]
    public async Task GrpcDoesNotInterpretJsonPointersOrOtherLocatorForms(string selector)
    {
        var result = ApiOperationDocumentResolver.Resolve("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("syntax='proto3'; service S {}"),
            ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selector.unsupported");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GrpcResolvesImportedMessageTypesOnlyThroughAuthorizedDocumentUris(int dependencySyntax)
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; package p; import 'types.proto';
                service S { rpc Get(.shared.Row) returns(shared.Row); }
                """), Reference(), new Uri("https://example.test/apis/root.proto"), (uri, token) =>
            {
                token.ThrowIfCancellationRequested();
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                    "syntax='proto" + dependencySyntax + "'; package shared; message Row {}"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(calls.SequenceEqual(["https://example.test/apis/types.proto"])).IsTrue();
        await Assert.That(result.DocumentUri!.AbsoluteUri).IsEqualTo("https://example.test/apis/root.proto");
        var selected = result.GrpcOperation ?? throw new InvalidOperationException("Expected a native RPC selection.");
        await Assert.That(selected.RequestType).IsEqualTo("shared.Row");
        await Assert.That(selected.ResponseType).IsEqualTo("shared.Row");
    }

    [Test]
    public async Task GrpcCannotSelectAServiceDeclaredOnlyInAnImportedDocument()
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'import.proto'; service Local {}"),
            Reference("Remote"), new Uri("https://example.test/root.proto"),
            (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                Encoding.UTF8.GetBytes("syntax='proto3'; message Row {} service Remote { rpc Get(Row) returns(Row); }")));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.not_found");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GrpcReportsUnavailableAndDeniedDependenciesWithoutSelection(bool denied)
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'types.proto'; service S {}"),
            Reference("S"), new Uri("https://example.test/root.proto"), (_, _) =>
            denied ? throw new UnauthorizedAccessException("No egress.") : ValueTask.FromResult<ReadOnlyMemory<byte>?>(null));
        await Assert.That(result.Status).IsEqualTo(denied ? ApiOperationResolutionStatus.Denied : ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(denied ? "dependency.denied" : "reference.unresolved");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task GrpcRelativeImportsWithoutABaseNeverInvokeAnAbsoluteUriResolver()
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'types.proto'; service S {}"), Reference("S"),
            resolveReference: (_, _) => throw new InvalidOperationException("No absolute base is available."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.base_uri");
    }

    [Test]
    public async Task GrpcRejectsAnInvalidUnselectedImportedService()
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; import 'types.proto'; message Row {}
                service S { rpc Get(Row) returns(Row); }
                """), Reference("S"), new Uri("https://example.test/root.proto"),
            (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                Encoding.UTF8.GetBytes("syntax='proto3'; service Other { rpc Call(Missing) returns(Missing); }")));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("protobuf.type");
    }

    [Test]
    public async Task GrpcRejectsSourceAboveTheAdvertisedByteLimit()
    {
        var result = ApiOperationDocumentResolver.Resolve("gRPC/proto3", "text/plain",
            new byte[ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes + 1], Reference("S"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.bytes");
    }

    [Test]
    public async Task GrpcAcceptsSourceAtTheAdvertisedByteLimit()
    {
        var source = "syntax='proto3'; message Row {} service S { rpc Get(Row) returns(Row); }"
            .PadRight(ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes);
        var result = ApiOperationDocumentResolver.Resolve("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes(source), Reference("S"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.GrpcOperation!.RequestType).IsEqualTo("Row");
    }

    [Test]
    public async Task GrpcBoundsTheCombinedImportedDocumentBytes()
    {
        var calls = 0;
        var dependency = Encoding.UTF8.GetBytes("syntax='proto3';"
            .PadRight(ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes));
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; import 'a.proto'; import 'b.proto'; import 'c.proto'; import 'd.proto'; service S {}
                """), Reference("S"), new Uri("https://example.test/root.proto"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(dependency);
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.total_bytes");
        await Assert.That(result.GrpcOperation).IsNull();
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    public async Task GrpcBoundsDistinctImportsBeforeInvokingTheNextResolver()
    {
        var calls = 0;
        var source = "syntax='proto3'; service S {} " +
            string.Concat(Enumerable.Range(0, ApiOperationDocumentResolver.Capabilities.MaxReferencedDocuments + 1)
                .Select(index => "import 'file" + index + ".proto'; "));
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes(source), Reference("S"), new Uri("https://example.test/root.proto"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes("syntax='proto3';"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.references");
        await Assert.That(calls).IsEqualTo(ApiOperationDocumentResolver.Capabilities.MaxReferencedDocuments);
    }

    [Test]
    public async Task GrpcBoundsParsedTokens()
    {
        var source = "syntax='proto3'; service S {} " + new string(';',
            ApiOperationDocumentResolver.Capabilities.MaxDocumentNodes + 1);
        var result = ApiOperationDocumentResolver.Resolve("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes(source), Reference("S"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.nodes");
    }

    [Test]
    public async Task GrpcRequiresPublicImportVisibilityForRequestAndResponseTypes()
    {
        foreach (var isPublic in new[] { false, true })
        {
            var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
                Encoding.UTF8.GetBytes("""
                    syntax='proto3'; import 'bridge.proto'; service S { rpc Get(p.Row) returns(p.Row); }
                    """), Reference("S"), new Uri("https://example.test/root.proto"), (uri, _) =>
                    ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(uri.EndsWith("bridge.proto", StringComparison.Ordinal)
                        ? "syntax='proto3'; import " + (isPublic ? "public " : "") + "'types.proto';"
                        : "syntax='proto3'; package p; message Row {}")));
            await Assert.That(result.Status).IsEqualTo(isPublic ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (isPublic)
            {
                await Assert.That(result.GrpcOperation!.RequestType).IsEqualTo("p.Row");
                await Assert.That(result.GrpcOperation.ResponseType).IsEqualTo("p.Row");
            }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("protobuf.type");
                await Assert.That(result.GrpcOperation).IsNull();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GrpcDetectsDirectAndTransitiveImportCyclesWithoutRefetchingTheRoot(bool transitive)
    {
        var calls = 0;
        var source = "syntax='proto3'; import '" + (transitive ? "child.proto" : "root.proto") +
            "'; message Row {} service S { rpc Get(Row) returns(Row); }";
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes(source), Reference("S"), new Uri("https://example.test/root.proto"), (_, _) =>
            {
                calls++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    Encoding.UTF8.GetBytes("syntax='proto3'; import 'root.proto';"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.cycle");
        await Assert.That(result.GrpcOperation).IsNull();
        await Assert.That(calls).IsEqualTo(transitive ? 1 : 0);
    }

    [Test]
    public async Task GrpcSharesRepeatedImportsWithoutReportingAnAcyclicGraphAsACycle()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("""
                syntax='proto3'; import 'bridge.proto'; import 'types.proto'; import 'types.proto';
                service S { rpc Get(p.Row) returns(p.Row); }
                """), Reference("S"), new Uri("https://example.test/root.proto"), (uri, _) =>
            {
                calls.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(
                    uri.EndsWith("bridge.proto", StringComparison.Ordinal)
                        ? "syntax='proto3'; import 'types.proto';"
                        : "syntax='proto3'; package p; message Row {}"));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.GrpcOperation!.RequestType).IsEqualTo("p.Row");
        await Assert.That(calls.SequenceEqual(["https://example.test/bridge.proto", "https://example.test/types.proto"])).IsTrue();
    }

    [Test]
    public async Task GrpcCallerCancellationPropagatesDuringDependencyRetrieval()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.That(async () => await ApiOperationDocumentResolver.ResolveAsync("gRPC/proto3", "text/plain",
            Encoding.UTF8.GetBytes("syntax='proto3'; import 'types.proto'; service S {}"),
            Reference("S"), new Uri("https://example.test/root.proto"), async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }, cancellation.Token)).Throws<OperationCanceledException>();
    }
}
