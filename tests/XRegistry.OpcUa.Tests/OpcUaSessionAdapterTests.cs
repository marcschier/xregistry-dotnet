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
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegistryOriginDecodingRetainsScalarFieldsAndExpandedNodeContext(bool stableOrigin)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var root = stableOrigin ? ExpandedNodeId.Null :
            new ExpandedNodeId(new NodeId("Registry", 2), "urn:Exact:Namespace%2fTwo", 2u);
        proxy.Extension = EncodeOrigin(stableOrigin ? "urn:Origin:Exact%2f" : "",
            stableOrigin ? "" : "urn:Remote:Server%2fTwo", root);

        var origin = await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
            new NodeId(338u), CancellationToken.None);

        await Assert.That(origin.OriginUri).IsEqualTo(stableOrigin ? "urn:Origin:Exact%2f" : "");
        await Assert.That(origin.ServerUri).IsEqualTo(stableOrigin ? "" : "urn:Remote:Server%2fTwo");
        await Assert.That(origin.RegistryNodeId).IsEqualTo(root);
    }

    [Test]
    [Arguments("numeric")]
    [Arguments("string")]
    [Arguments("guid")]
    [Arguments("opaque")]
    public async Task RegistryOriginDecodingPreservesRootIdentifierKindAndSourceIndexes(string kind)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var node = kind switch
        {
            "numeric" => new NodeId(42u, 2),
            "string" => new NodeId("CaseSensitive%2f", 2),
            "guid" => new NodeId(Guid.Parse("9b81dd7d-333a-4746-ad52-403a07e353df"), 2),
            _ => new NodeId(ByteString.From(new byte[] { 0, 128, 255 }), 2)
        };
        var root = new ExpandedNodeId(node, null, 2u);
        proxy.Extension = EncodeOrigin("", "urn:Remote:Server%2fTwo", root);

        var origin = await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
            new NodeId(338u), CancellationToken.None);

        await Assert.That(origin.RegistryNodeId).IsEqualTo(root);
        await Assert.That(origin.RegistryNodeId.IdType).IsEqualTo(node.IdType);
        await Assert.That(origin.RegistryNodeId.NamespaceIndex).IsEqualTo((ushort)2);
        await Assert.That(origin.RegistryNodeId.ServerIndex).IsEqualTo(2u);
    }

    [Test]
    [Arguments("truncated")]
    [Arguments("extra")]
    [Arguments("scalar")]
    public async Task RegistryOriginDecodingRejectsMalformedScalarStructure(string state)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var bytes = EncodeOriginBytes("urn:origin", "", ExpandedNodeId.Null);
        proxy.Extension = new ExtensionObject(new ExpandedNodeId(43u),
            ByteString.From(state == "truncated" ? bytes[..^1] : [.. bytes, 0]));
        if (state == "scalar")
        {
            proxy.ReadValue = new Variant("not an ExtensionObject");
        }

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments(65536, true)]
    [Arguments(65537, false)]
    public async Task RegistryOriginDecodingEnforcesExactStructureByteBudget(int length, bool succeeds)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var overhead = EncodeOriginBytes("", "", ExpandedNodeId.Null).Length;
        var bytes = EncodeOriginBytes(new string('a', length - overhead), "", ExpandedNodeId.Null);
        await Assert.That(bytes.Length).IsEqualTo(length);
        proxy.Extension = new ExtensionObject(new ExpandedNodeId(43u), ByteString.From(bytes));

        if (succeeds)
        {
            var origin = await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), CancellationToken.None);
            await Assert.That(origin.OriginUri!.Length).IsEqualTo(length - overhead);
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                    new NodeId(338u), CancellationToken.None));
            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
    }

    [Test]
    public async Task RegistryOriginDecodingRetainsNativeReadStatusAndDiagnostics()
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        proxy.ReadStatus = StatusCodes.BadUserAccessDenied;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(((ServiceResultException)error.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.BadUserAccessDenied);
        await Assert.That(ReferenceEquals(error.Data["OpcUa.ResponseHeader"], proxy.Header)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegistryOriginDecodingRejectsAnUnrelatedDeclaredDataType(bool wrongShape)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        proxy.Extension = EncodeOrigin("urn:origin", "", ExpandedNodeId.Null);
        proxy.DeclaredDataType = wrongShape ? new Variant("RegistryOriginDataType") :
            new Variant(new NodeId("Unrelated", 2));
        var before = proxy.ReadCount;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(proxy.ReadCount).IsEqualTo(before + 1);
    }

    [Test]
    [Arguments("before")]
    [Arguments("datatype")]
    [Arguments("value")]
    public async Task RegistryOriginDecodingPreservesCancellationWithoutFurtherReads(string stage)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        proxy.Extension = EncodeOrigin("urn:origin", "", ExpandedNodeId.Null);
        using var cancellation = new CancellationTokenSource();
        var before = proxy.ReadCount;
        var expected = stage == "before" ? 0 : stage == "datatype" ? 1 : 2;
        proxy.AfterRead = () =>
        {
            if (proxy.ReadCount == before + expected)
            {
                cancellation.Cancel();
            }
        };
        if (stage == "before")
        {
            cancellation.Cancel();
        }

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(proxy.ReadCount).IsEqualTo(before + expected);
    }

    [Test]
    public async Task RegistryOriginDecodingPreservesNullScalarFields()
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        proxy.Extension = EncodeOrigin(null, null, ExpandedNodeId.Null);

        var origin = await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
            new NodeId(338u), CancellationToken.None);

        await Assert.That(origin.OriginUri).IsNull();
        await Assert.That(origin.ServerUri).IsNull();
        await Assert.That(origin.RegistryNodeId.IsNull).IsTrue();
    }

    [Test]
    [Arguments("absent")]
    [Arguments("competing")]
    [Arguments("object")]
    public async Task RegistryOriginDecodingRequiresOneEncodingDataType(string state)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        proxy.Extension = EncodeOrigin("urn:origin", "", ExpandedNodeId.Null);
        var reference = new ReferenceDescription
        {
            NodeId = new ExpandedNodeId(338u),
            NodeClass = state == "object" ? NodeClass.Object : NodeClass.DataType
        };
        proxy.BrowseResultFactory = _ => new BrowseResult
        {
            References = state == "absent" ? [] : state == "competing" ? [reference, reference] : [reference]
        };

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RegistryOriginDecodingRejectsAnUnrelatedDataTypeWithTheSameBrowseName()
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        proxy.DataTypeBrowseName = "RegistryOriginDataType";
        proxy.DeclaredDataType = new Variant(new NodeId("DifferentDataType", 2));
        proxy.Extension = EncodeOrigin("urn:origin", "", ExpandedNodeId.Null);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId("DifferentDataType", 2), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    private static ExtensionObject EncodeOrigin(string? originUri, string? serverUri, ExpandedNodeId root) =>
        new(new ExpandedNodeId(43u), ByteString.From(EncodeOriginBytes(originUri, serverUri, root)));

    private static byte[] EncodeOriginBytes(string? originUri, string? serverUri, ExpandedNodeId root)
    {
        var context = ServiceMessageContext.CreateEmpty(DefaultTelemetry.Create(static _ => { }));
        using var encoder = new BinaryEncoder(context);
        encoder.WriteString("OriginUri", originUri);
        encoder.WriteString("ServerUri", serverUri);
        encoder.WriteExpandedNodeId("RegistryNodeId", root);
        return encoder.CloseAndReturnBuffer() ?? throw new InvalidOperationException("No encoded origin bytes.");
    }

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

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(31)]
    public async Task RegistryAdmissionUsesOnlyTheSelectedRootAndPermittedObjectTypeChain(int depth)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var root = new NodeId("SelectedRoot", 1);
        var registryType = new NodeId("RegistryType", 1);
        var requests = new List<BrowseDescription>();
        proxy.BrowseResultFactory = request =>
        {
            requests.Add(request);
            if (request.NodeId == root)
            {
                return new BrowseResult
                {
                    References = [TypeReference(depth == 0 ? registryType : new NodeId("Domain0", 1))]
                };
            }
            for (var index = 0; index < depth; index++)
            {
                if (request.NodeId == new NodeId($"Domain{index}", 1))
                {
                    return new BrowseResult
                    {
                        References = [TypeReference(index + 1 == depth
                            ? registryType
                            : new NodeId($"Domain{index + 1}", 1))]
                    };
                }
            }
            throw new InvalidOperationException("Admission browsed outside the selected root's type hierarchy.");
        };

        var admitted = await adapter.IsRegistryNodeAsync(root, CancellationToken.None);

        await Assert.That(admitted).IsTrue();
        await Assert.That(requests.Count).IsEqualTo(depth + 1);
        await Assert.That(requests[0].NodeId).IsEqualTo(root);
        await Assert.That(requests[0].ReferenceTypeId).IsEqualTo(ReferenceTypeIds.HasTypeDefinition);
        await Assert.That(requests[0].BrowseDirection).IsEqualTo(BrowseDirection.Forward);
        foreach (var request in requests.Skip(1))
        {
            await Assert.That(request.ReferenceTypeId).IsEqualTo(ReferenceTypeIds.HasSubtype);
            await Assert.That(request.BrowseDirection).IsEqualTo(BrowseDirection.Inverse);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegistryAdmissionRejectsCompetingOrNonObjectTypeDefinitions(bool competing)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var valid = TypeReference(new NodeId("RegistryType", 1));
        proxy.BrowseResultFactory = _ => new BrowseResult
        {
            References = competing
                ? [valid, TypeReference(new NodeId("OtherType", 1))]
                : [new ReferenceDescription { NodeId = valid.NodeId, NodeClass = NodeClass.DataType }]
        };

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.IsRegistryNodeAsync(new NodeId("SelectedRoot", 1), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("absent")]
    [Arguments("unrelated")]
    [Arguments("competing-parents")]
    [Arguments("cycle")]
    [Arguments("non-object-parent")]
    [Arguments("unmapped")]
    [Arguments("depth")]
    public async Task RegistryAdmissionRejectsMissingUnrelatedOrMalformedTypeHierarchy(string shape)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var root = new NodeId("SelectedRoot", 1);
        var parentDepth = 0;
        proxy.BrowseResultFactory = request =>
        {
            if (request.NodeId == root)
            {
                return new BrowseResult
                {
                    References = shape switch
                    {
                        "absent" => [],
                        "unmapped" => [TypeReference(new NodeId("Domain", 9))],
                        _ => [TypeReference(new NodeId("Domain0", 1))]
                    }
                };
            }
            return new BrowseResult
            {
                References = shape switch
                {
                    "unrelated" => [],
                    "competing-parents" =>
                        [TypeReference(new NodeId("RegistryType", 1)), TypeReference(new NodeId("Other", 1))],
                    "cycle" => [TypeReference(new NodeId("Domain0", 1))],
                    "non-object-parent" => [new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(new NodeId("RegistryType", 1)),
                        NodeClass = NodeClass.DataType
                    }],
                    "depth" => [TypeReference(new NodeId($"Domain{++parentDepth}", 1))],
                    _ => throw new InvalidOperationException("Unexpected hierarchy Browse.")
                }
            };
        };

        if (shape is "absent" or "unrelated")
        {
            await Assert.That(await adapter.IsRegistryNodeAsync(root, CancellationToken.None)).IsFalse();
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await adapter.IsRegistryNodeAsync(root, CancellationToken.None));
            await Assert.That(error!.Code).IsEqualTo(shape switch
            {
                "unmapped" => FederationErrorCode.NotFound,
                "depth" => FederationErrorCode.LimitExceeded,
                _ => FederationErrorCode.InvalidPackage
            });
            if (shape == "depth")
            {
                await Assert.That(parentDepth).IsEqualTo(32);
            }
        }
    }

    private static ReferenceDescription TypeReference(NodeId node) => new()
    {
        NodeId = new ExpandedNodeId(node),
        NodeClass = NodeClass.ObjectType
    };

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
    public async Task CapturedExternalIdentitySurvivesSourceAndTargetTableReordering()
    {
        var (adapter, proxy) = CreateAdapter();
        var originalReference = new ExpandedNodeId(new NodeId(42u, 2), null, 2u);
        var first = await adapter.MapExternalNodeIdAsync(originalReference, CancellationToken.None);
        proxy.ServerArray =
            ["urn:example:local-server", "urn:Remote:Server%2fTwo", "urn:remote:server-one"];
        proxy.NamespaceArray =
            ["http://opcfoundation.org/UA/", "urn:Exact:Namespace%2fTwo", "urn:example:namespace-one"];
        var movedReference = new ExpandedNodeId(new NodeId(42u, 1), null, 1u);
        var second = await adapter.MapExternalNodeIdAsync(movedReference, CancellationToken.None);
        var reinterpreted = await adapter.MapExternalNodeIdAsync(originalReference, CancellationToken.None);
        var firstTarget = new NamespaceTable(
            ["http://opcfoundation.org/UA/", "urn:Exact:Namespace%2fTwo", "urn:target:other"]);
        var secondTarget = new NamespaceTable(
            ["http://opcfoundation.org/UA/", "urn:target:other", "urn:Exact:Namespace%2fTwo"]);

        await Assert.That(first.ServerApplicationUri).IsEqualTo("urn:Remote:Server%2fTwo");
        await Assert.That(first.NamespaceUri).IsEqualTo("urn:Exact:Namespace%2fTwo");
        await Assert.That(second.ServerApplicationUri).IsEqualTo(first.ServerApplicationUri);
        await Assert.That(second.NamespaceUri).IsEqualTo(first.NamespaceUri);
        await Assert.That(second.IdentifierType).IsEqualTo(first.IdentifierType);
        await Assert.That(second.Identifier).IsEqualTo(first.Identifier);
        await Assert.That(first.MapToLocalNodeId(first.ServerApplicationUri, firstTarget))
            .IsEqualTo(new NodeId(42u, 1));
        await Assert.That(second.MapToLocalNodeId(second.ServerApplicationUri, secondTarget))
            .IsEqualTo(new NodeId(42u, 2));
        await Assert.That(first.MapToLocalNodeId(first.ServerApplicationUri, secondTarget))
            .IsEqualTo(new NodeId(42u, 2));
        await Assert.That(reinterpreted.ServerApplicationUri).IsEqualTo("urn:remote:server-one");
        await Assert.That(reinterpreted.NamespaceUri).IsEqualTo("urn:example:namespace-one");
        await Assert.That(firstTarget.Count).IsEqualTo(3);
        await Assert.That(secondTarget.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ExternalReferenceServerIndexZeroUsesSourceApplicationWithoutEndpointSubstitution()
    {
        var (adapter, _) = CreateAdapter();
        var identity = await adapter.MapExternalNodeIdAsync(
            new ExpandedNodeId("resource", "urn:Exact:Namespace%2fTwo", 0), CancellationToken.None);

        await Assert.That(identity.ServerApplicationUri).IsEqualTo("urn:example:local-server");
        await Assert.That(identity.NamespaceUri).IsEqualTo("urn:Exact:Namespace%2fTwo");
        await Assert.That(identity.Identifier).IsEqualTo("resource");
        var target = new NamespaceTable(["http://opcfoundation.org/UA/", "urn:Exact:Namespace%2fTwo"]);
        await Assert.That(identity.MapToLocalNodeId("urn:example:local-server", target))
            .IsEqualTo(new NodeId("resource", 1));
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

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InternalBrowsePageBudgetRejectsBeforeRequestingExcessPage(int limit)
    {
        var (adapter, proxy) = CreateAdapter(maxBrowsePages: limit);
        proxy.BrowseResultFactory = _ => new BrowseResult
        {
            ContinuationPoint = new ByteString(new byte[] { 1 })
        };
        proxy.NextBrowseContinuation = [1];
        proxy.ContinuedPagesRemaining = limit - 1;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.IsRegistryNodeAsync(new NodeId("root", 1), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(error.Message).IsEqualTo("The OPC UA Browse result exceeds the page budget.");
        await Assert.That(proxy.NextBrowseCount).IsEqualTo(limit - 1);
        await Assert.That(proxy.ReleasedContinuations.Single()).IsEquivalentTo(new byte[] { 1 });
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InternalBrowsePageBudgetPermitsExactLimitCompletion(int limit)
    {
        var (adapter, proxy) = CreateAdapter(maxBrowsePages: limit);
        proxy.BrowseResultFactory = _ => new BrowseResult
        {
            ContinuationPoint = limit == 1 ? default : new ByteString(new byte[] { 1 })
        };
        proxy.NextBrowseContinuation = [1];
        proxy.ContinuedPagesRemaining = limit - 2;

        var found = await adapter.IsRegistryNodeAsync(new NodeId("root", 1), CancellationToken.None);

        await Assert.That(found).IsFalse();
        await Assert.That(proxy.NextBrowseCount).IsEqualTo(limit - 1);
        await Assert.That(proxy.ReleasedContinuations.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task InternalBrowsePageBudgetMustBePositive(int limit)
    {
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            _ = CreateAdapter(maxBrowsePages: limit);
            await Task.CompletedTask;
        });

        await Assert.That(error!.ParamName).IsEqualTo("maxBrowsePages");
    }

    [Test]
    public async Task InternalBrowsePageBudgetRetainsReleaseFailureOnLimitError()
    {
        var (adapter, proxy) = CreateAdapter(maxBrowsePages: 1);
        proxy.BrowseContinuation = [1];
        proxy.ReleaseStatus = StatusCodes.BadUserAccessDenied;

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.IsRegistryNodeAsync(new NodeId("root", 1), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        var cleanup = error.Data["OpcUa.ContinuationReleaseFailure"] as FederationException;
        await Assert.That(cleanup).IsNotNull();
        await Assert.That(cleanup!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That((cleanup.InnerException as ServiceResultException)!.StatusCode)
            .IsEqualTo(StatusCodes.BadUserAccessDenied);
        await Assert.That(proxy.NextBrowseCount).IsEqualTo(0);
        await Assert.That(proxy.ReleasedContinuations.Single()).IsEquivalentTo(new byte[] { 1 });
    }

    private static (OpcUaSessionAdapter Adapter, SessionProxy Proxy) CreateAdapter(int maxBrowsePages = 10_000)
    {
        var session = DispatchProxy.Create<ISessionClient, SessionProxy>();
        var proxy = (SessionProxy)(object)session;
        proxy.ServerArray = s_serverUris;
        proxy.NamespaceArray = s_externalNamespaceUris;
        return (new OpcUaSessionAdapter(session, new NodeId("RegistryType", 1),
            maxBrowsePages: maxBrowsePages), proxy);
    }

    public class SessionProxy : DispatchProxy
    {
        public ExtensionObject Extension { get; set; }
        public string DataTypeBrowseName { get; set; } = "BuildInfo";
        public string[] ServerArray { get; set; } = [];
        public string[] NamespaceArray { get; set; } = [];
        public Variant? ReadValue { get; set; }
        public Variant? DeclaredDataType { get; set; }
        public int ReadCount { get; private set; }
        public Action? AfterRead { get; set; }
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
        public Func<BrowseDescription, BrowseResult>? BrowseResultFactory { get; set; }
        public int? ContinuedPagesRemaining { get; set; }
        public int NextBrowseCount { get; private set; }

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
            ReadCount++;
            var node = nodes[0].NodeId;
            var value = node.TryGetValue(out uint identifier) && node.NamespaceIndex == 0
                ? identifier switch
                {
                    2254u => Variant.From(ServerArray),
                    2255u => Variant.From(NamespaceArray),
                    _ => Variant.From(Extension)
                }
                : Variant.From(Extension);
            var response = new ReadResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new DataValue(nodes[0].AttributeId == Attributes.DataType
                    ? DeclaredDataType ?? new Variant(new NodeId(338u)) : ReadValue ?? value, ReadStatus)]
            };
            AfterRead?.Invoke();
            return new(response);
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
                Results = [BrowseResultFactory?.Invoke(nodes[0]) ?? new BrowseResult
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
            else
            {
                NextBrowseCount++;
            }
            var continuation = NextBrowseContinuation ?? BrowseContinuation;
            if (!release && ContinuedPagesRemaining is { } remaining)
            {
                ContinuedPagesRemaining = remaining - 1;
                if (remaining <= 0)
                {
                    continuation = [];
                }
            }
            return new(new BrowseNextResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new BrowseResult
                {
                    StatusCode = release ? ReleaseStatus : NextBrowseStatus ?? BrowseStatus,
                    ContinuationPoint = release ? default : new ByteString(continuation)
                }]
            });
        }
    }
}
