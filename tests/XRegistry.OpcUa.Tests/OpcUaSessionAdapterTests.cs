// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaSessionAdapterTests
{
    private static readonly string[] s_namespaceUris = ["http://opcfoundation.org/UA/"];
    private static readonly string[] s_serverUris =
        ["urn:example:local-server", "urn:remote:server-one", "urn:Remote:Server%2fTwo"];
    private static readonly string[] s_externalNamespaceUris =
        ["http://opcfoundation.org/UA/", "urn:example:namespace-one", "urn:Exact:Namespace%2fTwo"];

    [Test]
    public async Task ReadJsonValueDecodesExtensionObjectAndResolvesItsDataType()
    {
        var value = new BuildInfo
        {
            ProductUri = "urn:example:product",
            ManufacturerName = "Example",
            ProductName = "Registry",
            SoftwareVersion = "1.2.3",
            BuildNumber = "42"
        };
        var session = DispatchProxy.Create<ISessionClient, SessionProxy>();
        var proxy = (SessionProxy)(object)session;
        proxy.Extension = new ExtensionObject(value, true);
        proxy.DataTypeBrowseName = "ProductInformation";
        var adapter = new OpcUaSessionAdapter(session, new NodeId("RegistryType", 1));
        typeof(OpcUaSessionAdapter).GetField("namespaceUris", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(adapter, new NamespaceTable(s_namespaceUris));
        var result = await adapter.ReadJsonValueAsync(new NodeId("metadata", 1), CancellationToken.None);

        await Assert.That(result.BuiltInType).IsEqualTo(BuiltInType.ExtensionObject);
        await Assert.That(result.DataTypeName).IsEqualTo("ProductInformation");
        await Assert.That(result.Value.GetProperty("ProductName").GetString()).IsEqualTo("Registry");
        await Assert.That(result.Value.GetProperty("BuildNumber").GetString()).IsEqualTo("42");
    }

    [Test]
    public async Task ReadServerArrayReturnsTheExactSessionServerUris()
    {
        var (adapter, _) = CreateAdapter();

        var serverArray = await adapter.ReadServerArrayAsync(CancellationToken.None);

        await Assert.That(serverArray).IsEquivalentTo(s_serverUris);
        await Assert.That(serverArray[0]).IsEqualTo("urn:example:local-server");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadStringRejectsNonGoodStatusAndRetainsTheOriginalStatus(bool uncertain)
    {
        var (adapter, proxy) = CreateAdapter();
        var statusCode = uncertain ? StatusCodes.Uncertain : StatusCodes.Bad;
        proxy.ReadValue = new Variant("plausible value");
        proxy.ReadStatus = statusCode;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadStringAsync(new NodeId("Metadata", 1), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(error.InnerException).IsTypeOf<ServiceResultException>();
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode).IsEqualTo(statusCode);
    }

    [Test]
    [Arguments("read")]
    [Arguments("browse")]
    [Arguments("browse-next")]
    [Arguments("write")]
    [Arguments("call")]
    public async Task GoodServiceDoesNotOverrideFailedOperationAndRetainsDiagnostics(string operation)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.ReadStatus = StatusCodes.BadUserAccessDenied;
        proxy.BrowseStatus = StatusCodes.BadUserAccessDenied;
        proxy.WriteStatus = StatusCodes.BadUserAccessDenied;
        proxy.CallStatus = StatusCodes.BadUserAccessDenied;

        var error = await Assert.ThrowsAsync<FederationException>(() => InvokeOperation(adapter, operation));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.BadUserAccessDenied);
        await Assert.That(error.Data["OpcUa.ResponseHeader"]).IsSameReferenceAs(proxy.Header);
        var diagnostics = (ArrayOf<DiagnosticInfo>)error.Data["OpcUa.DiagnosticInfos"]!;
        await Assert.That(diagnostics.Count).IsEqualTo(1);
        await Assert.That(diagnostics[0]).IsSameReferenceAs(proxy.Diagnostics[0]);
    }

    [Test]
    [Arguments("browse")]
    [Arguments("browse-next")]
    [Arguments("write")]
    [Arguments("call")]
    public async Task NonReadOperationsRejectUncertainStatus(string operation)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.BrowseStatus = StatusCodes.Uncertain;
        proxy.WriteStatus = StatusCodes.Uncertain;
        proxy.CallStatus = StatusCodes.Uncertain;

        var error = await Assert.ThrowsAsync<FederationException>(() => InvokeOperation(adapter, operation));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.Uncertain);
    }

    [Test]
    public async Task ReadFailureMapsNativeStatusAndRetainsDiagnosticContext()
    {
        (StatusCode Status, FederationErrorCode Outcome)[] cases =
        [
            (StatusCodes.BadNotSupported, FederationErrorCode.UnsupportedOperation),
            (StatusCodes.BadMethodInvalid, FederationErrorCode.UnsupportedOperation),
            (StatusCodes.BadNodeIdUnknown, FederationErrorCode.NotFound),
            (StatusCodes.BadNotFound, FederationErrorCode.NotFound),
            (StatusCodes.BadUserAccessDenied, FederationErrorCode.PolicyDenied),
            (StatusCodes.BadSecurityChecksFailed, FederationErrorCode.PolicyDenied),
            (StatusCodes.BadInvalidArgument, FederationErrorCode.InvalidPackage),
            (StatusCodes.BadContinuationPointInvalid, FederationErrorCode.InconsistentSnapshot),
            (StatusCodes.BadTooManyOperations, FederationErrorCode.LimitExceeded),
            (StatusCodes.BadEncodingLimitsExceeded, FederationErrorCode.LimitExceeded),
            (StatusCodes.BadCommunicationError, FederationErrorCode.Unavailable),
            (StatusCodes.BadTimeout, FederationErrorCode.Unavailable),
            (StatusCodes.BadSessionClosed, FederationErrorCode.Unavailable),
            (StatusCodes.BadSessionIdInvalid, FederationErrorCode.Unavailable),
            (StatusCodes.BadResourceUnavailable, FederationErrorCode.Unavailable),
            (StatusCodes.BadNotReadable, FederationErrorCode.Unavailable),
            (StatusCodes.BadInvalidState, FederationErrorCode.Unavailable)
        ];
        foreach (var (status, outcome) in cases)
        {
            var (adapter, proxy) = CreateAdapter();
            proxy.ReadStatus = status;

            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await adapter.ReadStringAsync(new NodeId("Metadata", 1), CancellationToken.None));

            await Assert.That(error!.Code).IsEqualTo(outcome);
            await Assert.That(((ServiceResultException)error.InnerException!).StatusCode).IsEqualTo(status);
            await Assert.That(error.Data["OpcUa.ResponseHeader"]).IsSameReferenceAs(proxy.Header);
            var diagnostics = (ArrayOf<DiagnosticInfo>)error.Data["OpcUa.DiagnosticInfos"]!;
            await Assert.That(diagnostics[0]).IsSameReferenceAs(proxy.Diagnostics[0]);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NonGoodServiceCannotBeOverriddenByGoodOperation(bool uncertain)
    {
        var (adapter, proxy) = CreateAdapter();
        var status = uncertain ? StatusCodes.Uncertain : StatusCodes.BadTimeout;
        proxy.Header.ServiceResult = status;
        proxy.ReadValue = new Variant("plausible value");

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadStringAsync(new NodeId("Metadata", 1), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode).IsEqualTo(status);
        await Assert.That(error.Data["OpcUa.ResponseHeader"]).IsSameReferenceAs(proxy.Header);
        var results = (ArrayOf<DataValue>)error.Data["OpcUa.Results"]!;
        await Assert.That(results[0].StatusCode).IsEqualTo(StatusCodes.Good);
    }

    [Test]
    public async Task GoodStatusWithInformationBitsRetainsTheValue()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.ReadStatus = StatusCodes.GoodClamped;
        proxy.ReadValue = new Variant("accepted value");

        var result = await adapter.ReadStringAsync(new NodeId("Metadata", 1), CancellationToken.None);

        await Assert.That(result).IsEqualTo("accepted value");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MethodFailureRetainsArgumentStatusAndDiagnostics(bool goodMethod)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.CallStatus = goodMethod ? StatusCodes.Good : StatusCodes.BadInvalidArgument;
        proxy.ArgumentResults = [StatusCodes.Good, StatusCodes.BadInvalidArgument];

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.OpenAsync(new NodeId("Document", 1), 1, CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Data["OpcUa.MethodStatusCode"]).IsEqualTo(proxy.CallStatus);
        var arguments = (ArrayOf<StatusCode>)error.Data["OpcUa.InputArgumentResults"]!;
        await Assert.That(arguments.Count).IsEqualTo(2);
        await Assert.That(arguments[0]).IsEqualTo(StatusCodes.Good);
        await Assert.That(arguments[1]).IsEqualTo(StatusCodes.BadInvalidArgument);
        var diagnostics = (ArrayOf<DiagnosticInfo>)error.Data["OpcUa.InputArgumentDiagnosticInfos"]!;
        await Assert.That(diagnostics[0]).IsSameReferenceAs(proxy.Diagnostics[0]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedBrowseRetainsReturnedContinuationReleaseFailure(bool nextPage)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.BrowseStatus = StatusCodes.BadUserAccessDenied;
        proxy.BrowseContinuation = [7, 8, 9];
        proxy.ReleaseStatus = StatusCodes.Uncertain;

        var error = await Assert.ThrowsAsync<FederationException>(() =>
            InvokeOperation(adapter, nextPage ? "browse-next" : "browse"));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.ReleasedContinuations.Count).IsEqualTo(1);
        await Assert.That(proxy.ReleasedContinuations[0]).IsEquivalentTo(new byte[] { 7, 8, 9 });
        var releaseFailure = (FederationException)error.Data["OpcUa.ContinuationReleaseFailure"]!;
        await Assert.That(releaseFailure.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(((ServiceResultException)releaseFailure.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.Uncertain);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.BadUserAccessDenied);
    }

    [Test]
    public async Task ExplicitContinuationReleaseRejectsNonGoodStatus()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.ReleaseStatus = StatusCodes.BadContinuationPointInvalid;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.BrowseNextAsync([1, 2, 3], true, CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.BadContinuationPointInvalid);
        await Assert.That(proxy.ReleasedContinuations.Single()).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task InternalBrowseRetainsBothContinuationReleaseFailuresWithoutReplacingOperationFailure()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.BrowseContinuation = [1, 2, 3];
        proxy.NextBrowseStatus = StatusCodes.BadUserAccessDenied;
        proxy.NextBrowseContinuation = [7, 8, 9];
        proxy.ReleaseStatus = StatusCodes.Uncertain;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CallMethodAsync(new NodeId("Target", 1), "Action", [], CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.BadUserAccessDenied);
        await Assert.That(proxy.ReleasedContinuations.Count).IsEqualTo(2);
        await Assert.That(proxy.ReleasedContinuations[0]).IsEquivalentTo(new byte[] { 7, 8, 9 });
        await Assert.That(proxy.ReleasedContinuations[1]).IsEquivalentTo(new byte[] { 1, 2, 3 });
        var cleanup = (AggregateException)error.Data["OpcUa.ContinuationReleaseFailure"]!;
        await Assert.That(cleanup.InnerExceptions.Count).IsEqualTo(2);
        foreach (var inner in cleanup.InnerExceptions)
        {
            var failure = (FederationException)inner;
            await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.Unavailable);
            await Assert.That(((ServiceResultException)failure.InnerException!).StatusCode)
                .IsEqualTo(StatusCodes.Uncertain);
        }
    }

    [Test]
    [Arguments("read", false)]
    [Arguments("browse", false)]
    [Arguments("browse-next", false)]
    [Arguments("write", false)]
    [Arguments("call", false)]
    [Arguments("read", true)]
    [Arguments("browse", true)]
    [Arguments("browse-next", true)]
    [Arguments("write", true)]
    [Arguments("call", true)]
    public async Task SdkServiceFailureMapsOutcomeAndRetainsOriginalException(string operation, bool failAsynchronously)
    {
        var (adapter, proxy) = CreateAdapter();
        var original = new ServiceResultException(StatusCodes.BadSecurityChecksFailed, "native service diagnostic");
        proxy.ServiceFailure = original;
        proxy.FailAsynchronously = failAsynchronously;

        var error = await Assert.ThrowsAsync<FederationException>(() => InvokeOperation(adapter, operation));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(error.InnerException).IsSameReferenceAs(original);
        await Assert.That(error.Message).IsEqualTo("The OPC UA operation did not return a Good status.");
        await Assert.That(error.InnerException!.Message).IsEqualTo("native service diagnostic");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SdkCancellationRemainsCancellationInsteadOfNativeFailure(bool failAsynchronously)
    {
        var (adapter, proxy) = CreateAdapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException(cancellation.Token);
        proxy.ServiceFailure = original;
        proxy.FailAsynchronously = failAsynchronously;

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await adapter.ReadStringAsync(new NodeId("Metadata", 1), cancellation.Token));

        await Assert.That(error).IsSameReferenceAs(original);
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
    }

    private static async Task InvokeOperation(OpcUaSessionAdapter adapter, string operation)
    {
        var node = new NodeId("Metadata", 1);
        switch (operation)
        {
            case "read":
                _ = await adapter.ReadStringAsync(node, CancellationToken.None);
                break;
            case "browse":
                _ = await adapter.BrowseAsync(node, CancellationToken.None);
                break;
            case "browse-next":
                _ = await adapter.BrowseNextAsync([1, 2, 3], false, CancellationToken.None);
                break;
            case "write":
                await adapter.WriteValueAsync(node, new Variant("value"), CancellationToken.None);
                break;
            case "call":
                _ = await adapter.OpenAsync(node, 1, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    [Test]
    public async Task MapExternalNodeIdUsesSourceServerAndNamespaceUrisAndPreservesIdentifier()
    {
        var (adapter, _) = CreateAdapter();
        var expandedNodeId = new ExpandedNodeId(new NodeId(42u, 2), null, 2u);

        var result = await adapter.MapExternalNodeIdAsync(expandedNodeId, CancellationToken.None);

        await Assert.That(result.ServerApplicationUri).IsEqualTo("urn:Remote:Server%2fTwo");
        await Assert.That(result.NamespaceUri).IsEqualTo("urn:Exact:Namespace%2fTwo");
        await Assert.That(result.IdentifierType).IsEqualTo(IdType.Numeric);
        await Assert.That(result.Identifier).IsEqualTo(42u);
        var targetNamespaceUris = new NamespaceTable(
            ["http://opcfoundation.org/UA/", "urn:target:other", "urn:Exact:Namespace%2fTwo"]);
        var targetNodeId = result.MapToLocalNodeId("urn:Remote:Server%2fTwo", targetNamespaceUris);
        await Assert.That(targetNodeId.NamespaceIndex).IsEqualTo((ushort)2);
        await Assert.That(targetNodeId.TryGetValue(out uint targetIdentifier) && targetIdentifier == 42u).IsTrue();
    }

    [Test]
    public async Task MapExternalNodeIdPreservesExpandedNodeIdNamespaceUriExactly()
    {
        var (adapter, _) = CreateAdapter();
        const string namespaceUri = "urn:Exact:Namespace%2fAlreadyPresent";
        var expandedNodeId = new ExpandedNodeId("resource", namespaceUri, 1);

        var result = await adapter.MapExternalNodeIdAsync(expandedNodeId, CancellationToken.None);

        await Assert.That(result.ServerApplicationUri).IsEqualTo("urn:remote:server-one");
        await Assert.That(result.NamespaceUri).IsEqualTo(namespaceUri);
        await Assert.That(result.IdentifierType).IsEqualTo(IdType.String);
        await Assert.That(result.Identifier).IsEqualTo("resource");
    }

    [Test]
    public async Task MapExternalNodeIdPreservesGuidAndOpaqueIdentifiers()
    {
        var (adapter, _) = CreateAdapter();
        var guid = Guid.Parse("47c31b9c-52e6-41f2-b064-ea8c44e086f0");
        byte[] opaque = [0x00, 0x7f, 0x80, 0xff];
        var guidResult = await adapter.MapExternalNodeIdAsync(
            new ExpandedNodeId(guid, 2, null, 2u), CancellationToken.None);
        var opaqueResult = await adapter.MapExternalNodeIdAsync(
            new ExpandedNodeId(ByteString.From(opaque), 2, null, 2u), CancellationToken.None);
        var targetNamespaceUris = new NamespaceTable(
            ["http://opcfoundation.org/UA/", "urn:target:other", "urn:Exact:Namespace%2fTwo"]);

        await Assert.That(guidResult.IdentifierType).IsEqualTo(IdType.Guid);
        await Assert.That(guidResult.Identifier).IsEqualTo(guid);
        await Assert.That(opaqueResult.IdentifierType).IsEqualTo(IdType.Opaque);
        await Assert.That((byte[])opaqueResult.Identifier).IsEquivalentTo(opaque);
        var targetGuid = guidResult.MapToLocalNodeId("urn:Remote:Server%2fTwo", targetNamespaceUris);
        var targetOpaque = opaqueResult.MapToLocalNodeId("urn:Remote:Server%2fTwo", targetNamespaceUris);
        await Assert.That(targetGuid.NamespaceIndex).IsEqualTo((ushort)2);
        await Assert.That(targetGuid.TryGetValue(out Guid mappedGuid) && mappedGuid == guid).IsTrue();
        await Assert.That(targetOpaque.NamespaceIndex).IsEqualTo((ushort)2);
        await Assert.That(targetOpaque.TryGetValue(out ByteString mappedOpaque)).IsTrue();
        await Assert.That(mappedOpaque.ToArray()).IsEquivalentTo(opaque);
    }

    [Test]
    public async Task ExternalOpaqueIdentifierCopiesInputAndReturnedBytes()
    {
        byte[] source = [0x00, 0x7f, 0x80, 0xff];
        var identity = new OpcUaExternalNodeId("urn:remote:server", "urn:namespace", IdType.Opaque, source);
        source[0] = 0xff;
        var firstRead = (byte[])identity.Identifier;
        firstRead[1] = 0xff;
        var namespaceUris = new NamespaceTable(["http://opcfoundation.org/UA/", "urn:namespace"]);
        var target = identity.MapToLocalNodeId("urn:remote:server", namespaceUris);

        await Assert.That((byte[])identity.Identifier).IsEquivalentTo(new byte[] { 0x00, 0x7f, 0x80, 0xff });
        await Assert.That(target.TryGetValue(out ByteString targetIdentifier)).IsTrue();
        await Assert.That(targetIdentifier.ToArray()).IsEquivalentTo(new byte[] { 0x00, 0x7f, 0x80, 0xff });
    }

    [Test]
    public async Task MapExternalNodeIdRejectsMissingTargetNamespaceInsteadOfUsingNamespaceZero()
    {
        var identity = new OpcUaExternalNodeId("urn:remote:server", "urn:missing:namespace",
            IdType.Numeric, 42u);
        var namespaceUris = new NamespaceTable(["http://opcfoundation.org/UA/"]);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
        {
            _ = identity.MapToLocalNodeId("urn:remote:server", namespaceUris);
            await Task.CompletedTask;
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task MapExternalNodeIdRejectsDifferentTargetApplicationUri()
    {
        var identity = new OpcUaExternalNodeId("urn:Remote:Server%2fTwo", "urn:namespace",
            IdType.Numeric, 42u);
        var namespaceUris = new NamespaceTable(["http://opcfoundation.org/UA/", "urn:namespace"]);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
        {
            _ = identity.MapToLocalNodeId("urn:remote:server%2fTwo", namespaceUris);
            await Task.CompletedTask;
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
    }

    [Test]
    public async Task MapExternalNodeIdRejectsOutOfRangeServerIndex()
    {
        var (adapter, _) = CreateAdapter();
        var expandedNodeId = new ExpandedNodeId(new NodeId(42u, 1), null, 3u);

        await Assert.ThrowsAsync<FederationException>(
            async () => await adapter.MapExternalNodeIdAsync(expandedNodeId, CancellationToken.None));
    }

    [Test]
    public async Task MapExternalNodeIdRejectsOutOfRangeNamespaceIndex()
    {
        var (adapter, _) = CreateAdapter();
        var expandedNodeId = new ExpandedNodeId(new NodeId(42u, 3), null, 1u);

        await Assert.ThrowsAsync<FederationException>(
            async () => await adapter.MapExternalNodeIdAsync(expandedNodeId, CancellationToken.None));
    }

    private static (OpcUaSessionAdapter Adapter, SessionProxy Proxy) CreateAdapter()
    {
        var session = DispatchProxy.Create<ISessionClient, SessionProxy>();
        var proxy = (SessionProxy)(object)session;
        proxy.ServerArray = s_serverUris;
        proxy.NamespaceArray = s_externalNamespaceUris;
        return (new OpcUaSessionAdapter(session, new NodeId("RegistryType", 1)), proxy);
    }

    public class SessionProxy : DispatchProxy
    {
        public ExtensionObject Extension { get; set; }
        public string DataTypeBrowseName { get; set; } = "BuildInfo";
        public string[] ServerArray { get; set; } = [];
        public string[] NamespaceArray { get; set; } = [];
        public Variant? ReadValue { get; set; }
        public StatusCode ReadStatus { get; set; }
        public StatusCode BrowseStatus { get; set; }
        public StatusCode WriteStatus { get; set; }
        public StatusCode CallStatus { get; set; }
        public StatusCode ReleaseStatus { get; set; }
        public StatusCode? NextBrowseStatus { get; set; }
        public byte[]? NextBrowseContinuation { get; set; }
        public ArrayOf<StatusCode> ArgumentResults { get; set; }
        public ResponseHeader Header { get; } = new();
        public ArrayOf<DiagnosticInfo> Diagnostics { get; } = [new DiagnosticInfo { AdditionalInfo = "native diagnostic" }];
        public byte[] BrowseContinuation { get; set; } = [];
        public List<byte[]> ReleasedContinuations { get; } = [];
        public Exception? ServiceFailure { get; set; }
        public bool FailAsynchronously { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (ServiceFailure is not null)
            {
                if (FailAsynchronously)
                {
#pragma warning disable CA2012
                    return targetMethod?.Name switch
                    {
                        "ReadAsync" => ValueTask.FromException<ReadResponse>(ServiceFailure),
                        "BrowseAsync" => ValueTask.FromException<BrowseResponse>(ServiceFailure),
                        "BrowseNextAsync" => ValueTask.FromException<BrowseNextResponse>(ServiceFailure),
                        "WriteAsync" => ValueTask.FromException<WriteResponse>(ServiceFailure),
                        "CallAsync" => ValueTask.FromException<CallResponse>(ServiceFailure),
                        _ => throw new NotSupportedException($"Unexpected OPC UA service call: {targetMethod?.Name}.")
                    };
#pragma warning restore CA2012
                }
                throw ServiceFailure;
            }
            switch (targetMethod?.Name)
            {
                case "ReadAsync":
#pragma warning disable CA2012
                    return Read((ArrayOf<ReadValueId>)args![3]!);
#pragma warning restore CA2012
                case "BrowseAsync":
#pragma warning disable CA2012
                    return Browse((ArrayOf<BrowseDescription>)args![3]!);
#pragma warning restore CA2012
                case "BrowseNextAsync":
#pragma warning disable CA2012
                    return BrowseNext((bool)args![1]!, (ArrayOf<ByteString>)args[2]!);
#pragma warning restore CA2012
                case "WriteAsync":
                    return new ValueTask<WriteResponse>(new WriteResponse
                    {
                        ResponseHeader = Header,
                        DiagnosticInfos = Diagnostics,
                        Results = [WriteStatus]
                    });
                case "CallAsync":
                    return new ValueTask<CallResponse>(new CallResponse
                    {
                        ResponseHeader = Header,
                        DiagnosticInfos = Diagnostics,
                        Results = [new CallMethodResult
                        {
                            StatusCode = CallStatus,
                            OutputArguments = [new Variant(7u)],
                            InputArgumentResults = ArgumentResults,
                            InputArgumentDiagnosticInfos = Diagnostics
                        }]
                    });
                default:
                    throw new NotSupportedException($"Unexpected OPC UA service call: {targetMethod?.Name}.");
            }
        }

        private ValueTask<ReadResponse> Read(ArrayOf<ReadValueId> nodes)
        {
            var node = nodes[0].NodeId;
            var value = node.TryGetValue(out uint identifier) && node.NamespaceIndex == 0
                ? identifier switch
                {
                    2254u => Variant.From(ServerArray),
                    2255u => Variant.From(NamespaceArray),
                    _ => Variant.From(Extension)
                }
                : Variant.From(Extension);
            return new(new ReadResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new DataValue(ReadValue ?? value, ReadStatus)]
            });
        }

        private ValueTask<BrowseResponse> Browse(ArrayOf<BrowseDescription> nodes)
        {
            var dataType = new ReferenceDescription
            {
                ReferenceTypeId = ReferenceTypeIds.HasEncoding,
                IsForward = false,
                NodeId = new ExpandedNodeId(new NodeId(338u, 0)),
                BrowseName = new QualifiedName(DataTypeBrowseName, 0),
                NodeClass = NodeClass.DataType
            };
            return new(new BrowseResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new BrowseResult
                {
                    References = [dataType],
                    StatusCode = BrowseStatus,
                    ContinuationPoint = new ByteString(BrowseContinuation)
                }]
            });
        }

        private ValueTask<BrowseNextResponse> BrowseNext(bool release, ArrayOf<ByteString> continuations)
        {
            if (release)
            {
                ReleasedContinuations.Add(continuations[0].ToArray());
            }
            return new(new BrowseNextResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new BrowseResult
                {
                    StatusCode = release ? ReleaseStatus : NextBrowseStatus ?? BrowseStatus,
                    ContinuationPoint = release ? default : new ByteString(NextBrowseContinuation ?? BrowseContinuation)
                }]
            });
        }
    }
}
