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
    private static readonly string[] s_fileMethods = ["Open", "Read", "Write", "SetPosition", "Close"];

    [Test]
    [Arguments("Open")]
    [Arguments("Read")]
    [Arguments("Write")]
    [Arguments("SetPosition")]
    [Arguments("Close")]
    public async Task FileTransferCallsActualMethodOnSelectedObject(string method)
    {
        var (adapter, proxy) = CreateAdapter();
        var file = new NodeId("SelectedFile", 2);
        var actual = new NodeId("Actual." + method, 2);
        proxy.BrowseResultFactory = description =>
        {
            return new BrowseResult
            {
                References =
                [
                    new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(actual),
                        BrowseName = new QualifiedName(method, 0),
                        NodeClass = NodeClass.Method
                    },
                    new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(new NodeId("NotAMethod", 2)),
                        BrowseName = new QualifiedName(method, 0),
                        NodeClass = NodeClass.Variable
                    },
                    new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(new NodeId("OtherNamespaceMethod", 2)),
                        BrowseName = new QualifiedName(method, 2),
                        NodeClass = NodeClass.Method
                    }
                ]
            };
        };
        await InvokeFileMethod(adapter, file, method);

        var browse = proxy.BrowseRequests.Single();
        await Assert.That(browse.NodeId).IsEqualTo(file);
        await Assert.That(browse.BrowseDirection).IsEqualTo(BrowseDirection.Forward);
        await Assert.That(browse.ReferenceTypeId).IsEqualTo(ReferenceTypeIds.HierarchicalReferences);
        var call = proxy.MethodCalls.Single();
        await Assert.That(call.ObjectId).IsEqualTo(file);
        await Assert.That(call.MethodId).IsEqualTo(actual);
        await Assert.That(call.InputArguments.Count).IsEqualTo(method is "Read" or "Write" or "SetPosition" ? 2 : 1);
        if (method == "Open")
        {
            await Assert.That(call.InputArguments[0].TryGetValue(out byte mode) && mode == 1).IsTrue();
        }
        else
        {
            await Assert.That(call.InputArguments[0].TryGetValue(out uint handle) && handle == 7u).IsTrue();
        }
        if (method == "Read")
        {
            await Assert.That(call.InputArguments[1].TryGetValue(out int length) && length == 64).IsTrue();
        }
        if (method == "Write")
        {
            await Assert.That(call.InputArguments[1].TryGetValue(out ByteString bytes)).IsTrue();
            await Assert.That(bytes.ToArray()).IsEquivalentTo(new byte[] { 1, 2 });
        }
        if (method == "SetPosition")
        {
            await Assert.That(call.InputArguments[1].TryGetValue(out ulong position) && position == 0UL).IsTrue();
        }
    }

    [Test]
    [Arguments("Open", false)]
    [Arguments("Read", false)]
    [Arguments("Write", false)]
    [Arguments("SetPosition", false)]
    [Arguments("Close", false)]
    [Arguments("Open", true)]
    [Arguments("Read", true)]
    [Arguments("Write", true)]
    [Arguments("SetPosition", true)]
    [Arguments("Close", true)]
    public async Task FileTransferRejectsMissingOrAmbiguousMethodsWithoutDeclarationFallback(
        string method, bool ambiguous)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.BrowseResultFactory = _ => new BrowseResult
        {
            References = ambiguous
                ? new ArrayOf<ReferenceDescription>(
                    Enumerable.Range(1, 2).Select(index => new ReferenceDescription
                    {
                        NodeId = new ExpandedNodeId(new NodeId("Actual." + index, 1)),
                        BrowseName = new QualifiedName(method),
                        NodeClass = NodeClass.Method
                    }).ToArray())
                : [new ReferenceDescription
                {
                    NodeId = new ExpandedNodeId(new NodeId("WrongCaseMethod", 1)),
                    BrowseName = new QualifiedName(method.ToLowerInvariant()),
                    NodeClass = NodeClass.Method
                }]
        };

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await InvokeFileMethod(adapter, new NodeId("File", 1), method));

        await Assert.That(failure!.Code).IsEqualTo(
            ambiguous ? FederationErrorCode.Ambiguous : FederationErrorCode.NotFound);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(0);
        await Assert.That(proxy.BrowseRequests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("Open")]
    [Arguments("Read")]
    [Arguments("Write")]
    [Arguments("SetPosition")]
    [Arguments("Close")]
    public async Task FileTransferObservesCancellationAfterMethodDiscovery(string method)
    {
        var (adapter, proxy) = CreateAdapter();
        using var cancellation = new CancellationTokenSource();
        proxy.BrowseResultFactory = _ =>
        {
            cancellation.Cancel();
            return new BrowseResult
            {
                References = [new ReferenceDescription
                {
                    NodeId = new ExpandedNodeId(new NodeId("Actual", 1)),
                    BrowseName = new QualifiedName(method),
                    NodeClass = NodeClass.Method
                }]
            };
        };

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await InvokeFileMethod(adapter, new NodeId("File", 1), method, cancellation.Token));

        await Assert.That(failure!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(0);
        await Assert.That(proxy.BrowseRequests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("Open")]
    [Arguments("Read")]
    [Arguments("Write")]
    [Arguments("SetPosition")]
    [Arguments("Close")]
    public async Task FileTransferRejectsRemoteMethodTarget(string method)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.BrowseResultFactory = _ => new BrowseResult
        {
            References = [new ReferenceDescription
            {
                NodeId = new ExpandedNodeId(new NodeId("RemoteMethod", 1), null, 1u),
                BrowseName = new QualifiedName(method),
                NodeClass = NodeClass.Method
            }]
        };

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await InvokeFileMethod(adapter, new NodeId("File", 1), method));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("OpenWrite")]
    [Arguments("Write")]
    [Arguments("SetPosition")]
    [Arguments("Close")]
    public async Task FileTransferRechecksEncryptionAfterMethodDiscovery(string method)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.BrowseResultFactory = _ =>
        {
            proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.Sign };
            return new BrowseResult
            {
                References = [new ReferenceDescription
                {
                    NodeId = new ExpandedNodeId(new NodeId("Actual", 1)),
                    BrowseName = new QualifiedName(method == "OpenWrite" ? "Open" : method),
                    NodeClass = NodeClass.Method
                }]
            };
        };

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await InvokeFileMethod(adapter, new NodeId("File", 1), method));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(0);
        await Assert.That(proxy.BrowseRequests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FileTransferFindsActualMethodOnContinuationPage()
    {
        var (adapter, proxy) = CreateAdapter();
        var file = new NodeId("SelectedFile", 2);
        var actual = new NodeId("Actual.Open", 2);
        proxy.BrowseResultFactory = _ => new BrowseResult { ContinuationPoint = new ByteString(new byte[] { 1 }) };
        proxy.NextBrowseReferences = [new ReferenceDescription
        {
            NodeId = new ExpandedNodeId(actual),
            BrowseName = new QualifiedName("Open"),
            NodeClass = NodeClass.Method
        }];

        var handle = await adapter.OpenAsync(file, 6, CancellationToken.None);

        await Assert.That(handle).IsEqualTo(7u);
        await Assert.That(proxy.NextBrowseCount).IsEqualTo(1);
        var call = proxy.MethodCalls.Single();
        await Assert.That(call.ObjectId).IsEqualTo(file);
        await Assert.That(call.MethodId).IsEqualTo(actual);
        await Assert.That(call.InputArguments[0].TryGetValue(out byte mode) && mode == 6).IsTrue();
        await Assert.That(proxy.ReleasedContinuations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FileTransferDoesNotCallAPartiallyDiscoveredMethodAfterBrowseBudgetExhaustion()
    {
        var (adapter, proxy) = CreateAdapter(maxBrowsePages: 1);
        proxy.BrowseResultFactory = _ => new BrowseResult
        {
            ContinuationPoint = new ByteString(new byte[] { 1 }),
            References = [new ReferenceDescription
            {
                NodeId = new ExpandedNodeId(new NodeId("Actual.Open", 1)),
                BrowseName = new QualifiedName("Open"),
                NodeClass = NodeClass.Method
            }]
        };

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.OpenAsync(new NodeId("File", 1), 1, CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(0);
        await Assert.That(proxy.NextBrowseCount).IsEqualTo(0);
        await Assert.That(proxy.ReleasedContinuations.Single()).IsEquivalentTo(new byte[] { 1 });
    }

    [Test]
    [Arguments("Open", "namespace")]
    [Arguments("Open", "browse")]
    [Arguments("Open", "call")]
    [Arguments("OpenWrite", "namespace")]
    [Arguments("OpenWrite", "browse")]
    [Arguments("OpenWrite", "call")]
    [Arguments("Read", "namespace")]
    [Arguments("Read", "browse")]
    [Arguments("Read", "call")]
    [Arguments("Write", "namespace")]
    [Arguments("Write", "browse")]
    [Arguments("Write", "call")]
    [Arguments("SetPosition", "namespace")]
    [Arguments("SetPosition", "browse")]
    [Arguments("SetPosition", "call")]
    [Arguments("Close", "namespace")]
    [Arguments("Close", "browse")]
    [Arguments("Close", "call")]
    public async Task FileTransferRejectsSessionChangeAcrossAwaitedOperations(string method, string stage)
    {
        var (adapter, proxy) = CreateAdapter();
        var file = new NodeId("File", 1);
        Action replaceSession = () => proxy.SessionId = new NodeId("ReplacementSession", 0);
        if (stage == "namespace")
        {
            proxy.AfterRead = replaceSession;
        }
        if (stage == "browse")
        {
            proxy.AfterBrowse = replaceSession;
        }
        if (stage == "call")
        {
            proxy.AfterCall = replaceSession;
        }

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await InvokeFileMethod(adapter, file, method));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(failure.Message).Contains("Session changed");
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(stage == "call" ? 1 : 0);
        await Assert.That(proxy.BrowseRequests.Count).IsEqualTo(stage == "namespace" ? 0 : 1);
        await Assert.That(proxy.NextBrowseCount).IsEqualTo(0);
        await Assert.That(proxy.ReleasedContinuations.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FileTransferDoesNotReuseOrReleaseContinuationInReplacementSession(bool duringNext)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.BrowseResultFactory = _ => new BrowseResult
        {
            ContinuationPoint = new ByteString(new byte[] { 1 })
        };
        Action replaceSession = () => proxy.SessionId = new NodeId("ReplacementSession", 0);
        if (duringNext)
        {
            proxy.AfterBrowseNext = replaceSession;
        }
        else
        {
            proxy.AfterBrowse = replaceSession;
        }

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.OpenAsync(new NodeId("File", 1), 1, CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(proxy.NextBrowseCount).IsEqualTo(duringNext ? 1 : 0);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(0);
        await Assert.That(proxy.ReleasedContinuations.Count).IsEqualTo(0);
        var cleanup = failure.Data["OpcUa.ContinuationReleaseFailure"] as FederationException;
        await Assert.That(cleanup).IsNotNull();
        await Assert.That(cleanup!.Code).IsEqualTo(FederationErrorCode.Unavailable);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FileTransferReadHandleExemptionDoesNotCrossSessionChangeDuringDiscovery(bool close)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.None };
        var file = new NodeId("File", 1);
        var handle = await adapter.OpenAsync(file, 1, CancellationToken.None);
        proxy.AfterBrowse = () => proxy.SessionId = new NodeId("ReplacementSession", 0);
        var before = proxy.MethodCalls.Count;

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
        {
            if (close)
            {
                await adapter.CloseAsync(file, handle, CancellationToken.None);
            }
            else
            {
                await adapter.SetPositionAsync(file, handle, 0, CancellationToken.None);
            }
        });

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(before);
        proxy.AfterBrowse = null;
        var retry = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CloseAsync(file, handle, CancellationToken.None));
        await Assert.That(retry!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(before);
    }

    [Test]
    public async Task FileTransferCanOpenAgainWithFreshNamespaceTableAfterSessionTransition()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.AfterCall = () => proxy.SessionId = new NodeId("ReplacementSession", 0);
        var file = new NodeId("File", 1);

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.OpenAsync(file, 1, CancellationToken.None));
        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(proxy.ReadCount).IsEqualTo(1);
        proxy.AfterCall = null;
        proxy.NamespaceArray = ["http://opcfoundation.org/UA/", "urn:new-session"];

        var handle = await adapter.OpenAsync(file, 1, CancellationToken.None);

        await Assert.That(handle).IsEqualTo(7u);
        await Assert.That(proxy.ReadCount).IsEqualTo(2);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(2);
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.None };
        await adapter.CloseAsync(file, handle, CancellationToken.None);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(3);
    }

    private static async ValueTask InvokeFileMethod(OpcUaSessionAdapter adapter, NodeId file, string method,
        CancellationToken cancellationToken = default)
    {
        switch (method)
        {
            case "Open":
                var handle = await adapter.OpenAsync(file, 1, cancellationToken);
                await Assert.That(handle).IsEqualTo(7u);
                break;
            case "OpenWrite":
                _ = await adapter.OpenAsync(file, 6, cancellationToken);
                break;
            case "Read":
                var bytes = await adapter.ReadAsync(file, 7, 64, cancellationToken);
                await Assert.That(bytes).IsEquivalentTo(new byte[] { 1, 2 });
                break;
            case "Write":
                await adapter.WriteAsync(file, 7, [1, 2], cancellationToken);
                break;
            case "SetPosition":
                await adapter.SetPositionAsync(file, 7, 0, cancellationToken);
                break;
            case "Close":
                await adapter.CloseAsync(file, 7, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method));
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task FileTransferReadRejectsNonpositiveLengthBeforeNativeOperations(int length)
    {
        var (adapter, proxy) = CreateAdapter();

        var failure = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await adapter.ReadAsync(new NodeId("File", 1), 7, length, CancellationToken.None));

        await Assert.That(failure!.ParamName).IsEqualTo("length");
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(1, 1)]
    [Arguments(2, 1)]
    [Arguments(2, 2)]
    [Arguments(2, int.MaxValue)]
    public async Task FileTransferReadEnforcesRequestedLengthAndPreservesShortOrEmptyChunks(int returned, int length)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.ReadMethodBytes = Enumerable.Range(0, returned).Select(value => (byte)value).ToArray();

        if (returned > length)
        {
            var failure = await Assert.ThrowsAsync<FederationException>(async () =>
                await adapter.ReadAsync(new NodeId("File", 1), 7, length, CancellationToken.None));
            await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(failure.Message).Contains("more bytes than requested");
        }
        else
        {
            var bytes = await adapter.ReadAsync(new NodeId("File", 1), 7, length, CancellationToken.None);
            await Assert.That(bytes).IsEquivalentTo(proxy.ReadMethodBytes);
        }
        var call = proxy.MethodCalls.Single();
        await Assert.That(call.MethodId).IsEqualTo(new NodeId("Actual.Read", 1));
        await Assert.That(call.InputArguments[1].TryGetValue(out int requested) && requested == length).IsTrue();
    }

    [Test]
    [Arguments("Write", 1)]
    [Arguments("Write", 2)]
    [Arguments("SetPosition", 1)]
    [Arguments("SetPosition", 2)]
    [Arguments("Close", 1)]
    [Arguments("Close", 2)]
    public async Task FileTransferVoidMethodsRejectUnexpectedOutputsWithoutRetry(string method, int count)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.MethodOutputs = _ => new ArrayOf<Variant>(
            Enumerable.Repeat(new Variant(7u), count).ToArray());

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await InvokeFileMethod(adapter, new NodeId("File", 1), method));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(failure.Message).Contains(method);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(1);
        await Assert.That(proxy.BrowseRequests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments((byte)1, false, false)]
    [Arguments((byte)1, true, false)]
    [Arguments((byte)6, false, false)]
    [Arguments((byte)6, true, false)]
    [Arguments((byte)1, false, true)]
    [Arguments((byte)1, true, true)]
    [Arguments((byte)6, false, true)]
    [Arguments((byte)6, true, true)]
    public async Task FileTransferMalformedOpenClosesIdentifiableHandleAndRetainsCleanupError(
        byte mode, bool closeFails, bool cancelDuringOpen)
    {
        var (adapter, proxy) = CreateAdapter();
        var file = new NodeId("File", 1);
        if (mode == 1)
        {
            proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.None };
        }
        using var cancellation = new CancellationTokenSource();
        proxy.MethodOutputs = call =>
        {
            if (call.MethodId.IdentifierAsString == "Actual.Open")
            {
                if (cancelDuringOpen)
                {
                    cancellation.Cancel();
                }
                if (closeFails)
                {
                    proxy.CallStatus = StatusCodes.BadUserAccessDenied;
                }
                return [new Variant(11u), new Variant("extra")];
            }
            return [];
        };

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.OpenAsync(file, mode, cancellation.Token));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(failure.Message).Contains("output count");
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(2);
        var close = proxy.MethodCalls[1];
        await Assert.That(close.ObjectId).IsEqualTo(file);
        await Assert.That(close.MethodId).IsEqualTo(new NodeId("Actual.Close", 1));
        await Assert.That(close.InputArguments[0].TryGetValue(out uint handle) && handle == 11u).IsTrue();
        await Assert.That(proxy.MethodTokens[0]).IsEqualTo(cancellation.Token);
        await Assert.That(proxy.MethodTokens[1].CanBeCanceled).IsFalse();
        await Assert.That(failure.Data.Contains("OpcUa.FileCloseFailure")).IsEqualTo(closeFails);
        if (closeFails)
        {
            var cleanup = failure.Data["OpcUa.FileCloseFailure"] as FederationException;
            await Assert.That(cleanup).IsNotNull();
            await Assert.That(cleanup!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
            await Assert.That(((ServiceResultException)cleanup.InnerException!).StatusCode)
                .IsEqualTo(StatusCodes.BadUserAccessDenied);
        }
        else if (mode == 1)
        {
            var retry = await Assert.ThrowsAsync<FederationException>(async () =>
                await adapter.CloseAsync(file, 11u, CancellationToken.None));
            await Assert.That(retry!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
            await Assert.That(proxy.MethodCalls.Count).IsEqualTo(2);
        }
    }

    [Test]
    [Arguments("Open", "none")]
    [Arguments("Open", "signed")]
    [Arguments("Open", "string")]
    [Arguments("Open", "null")]
    [Arguments("Open", "extra")]
    [Arguments("Read", "none")]
    [Arguments("Read", "signed")]
    [Arguments("Read", "unsigned")]
    [Arguments("Read", "string")]
    [Arguments("Read", "null")]
    [Arguments("Read", "extra")]
    public async Task FileTransferRejectsMissingOrMistypedSingleOutputs(string method, string shape)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.MethodOutputs = _ => shape switch
        {
            "none" => [],
            "signed" => [new Variant(7)],
            "unsigned" => [new Variant(7u)],
            "string" => [new Variant("7")],
            "null" => [Variant.Null],
            _ => [new Variant(new ByteString(new byte[] { 1 })), new Variant(7u)]
        };

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await InvokeFileMethod(adapter, new NodeId("File", 1), method));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(failure.Message).Contains(method);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("Open")]
    [Arguments("Read")]
    [Arguments("Write")]
    [Arguments("SetPosition")]
    [Arguments("Close")]
    public async Task FileTransferNativeFailurePrecedesMalformedOutputs(string method)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.CallStatus = StatusCodes.BadUserAccessDenied;
        proxy.MethodOutputs = _ => [new Variant("malformed")];

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await InvokeFileMethod(adapter, new NodeId("File", 1), method));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(((ServiceResultException)failure.InnerException!).StatusCode)
            .IsEqualTo(StatusCodes.BadUserAccessDenied);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FileTransferMalformedSuccessfulCloseConsumesReadHandleExemption()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.None };
        var file = new NodeId("File", 1);
        var handle = await adapter.OpenAsync(file, 1, CancellationToken.None);
        proxy.MethodOutputs = _ => [new Variant(7u)];

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CloseAsync(file, handle, CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        var calls = proxy.MethodCalls.Count;
        var retry = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CloseAsync(file, handle, CancellationToken.None));
        await Assert.That(retry!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(calls);
    }

    [Test]
    [Arguments(0u)]
    [Arguments(uint.MaxValue)]
    public async Task FileTransferPreservesFullUInt32HandleRange(uint expected)
    {
        var (adapter, proxy) = CreateAdapter();
        var file = new NodeId("File", 1);
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.None };
        proxy.MethodOutputs = call => call.MethodId.IdentifierAsString == "Actual.Open"
            ? [new Variant(expected)] : [];

        var handle = await adapter.OpenAsync(file, 1, CancellationToken.None);
        await adapter.CloseAsync(file, handle, CancellationToken.None);

        await Assert.That(handle).IsEqualTo(expected);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(2);
        await Assert.That(proxy.MethodCalls[1].InputArguments[0].TryGetValue(out uint closed) && closed == expected)
            .IsTrue();
    }

    [Test]
    [Arguments(MessageSecurityMode.None)]
    [Arguments(MessageSecurityMode.Sign)]
    [Arguments(MessageSecurityMode.SignAndEncrypt)]
    public async Task FileTransferOpenValidatesEveryModeBitCombinationBeforeNativeOperations(
        MessageSecurityMode security)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = new EndpointDescription { SecurityMode = security };
        var file = new NodeId("File", 1);
        for (var candidate = 0; candidate <= byte.MaxValue; candidate++)
        {
            var mode = (byte)candidate;
            var valid = mode is 1 or 2 or 3 or 6 or 7 or 9 or 10 or 11 or 14 or 15;
            var writable = mode is 2 or 3 or 6 or 7 or 10 or 11 or 14 or 15;
            var before = proxy.NativeOperationCount;
            if (!valid || writable && security != MessageSecurityMode.SignAndEncrypt)
            {
                var failure = await Assert.ThrowsAsync<FederationException>(async () =>
                    await adapter.OpenAsync(file, mode, CancellationToken.None));
                await Assert.That(failure!.Code).IsEqualTo(
                    !valid ? FederationErrorCode.InvalidPackage : FederationErrorCode.PolicyDenied);
                await Assert.That(proxy.NativeOperationCount).IsEqualTo(before);
            }
            else
            {
                var handle = await adapter.OpenAsync(file, mode, CancellationToken.None);
                await Assert.That(handle).IsEqualTo(7u);
                var call = proxy.MethodCalls[^1];
                await Assert.That(call.ObjectId).IsEqualTo(file);
                await Assert.That(call.MethodId).IsEqualTo(new NodeId("Actual.Open", 1));
                await Assert.That(call.InputArguments.Count).IsEqualTo(1);
                await Assert.That(call.InputArguments[0].TryGetValue(out byte actual) && actual == mode).IsTrue();
                await adapter.CloseAsync(file, handle, CancellationToken.None);
            }
        }
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(
            security == MessageSecurityMode.SignAndEncrypt ? 20 : 4);
    }

    [Test]
    [Arguments(MessageSecurityMode.None)]
    [Arguments(MessageSecurityMode.Sign)]
    public async Task FileTransferReadAppendModeAllowsReadOnlyPositionAndCloseWithoutMutationPermission(
        MessageSecurityMode security)
    {
        var (adapter, proxy) = CreateAdapter();
        var file = new NodeId("File", 1);
        proxy.Endpoint = new EndpointDescription { SecurityMode = security };

        var handle = await adapter.OpenAsync(file, 9, CancellationToken.None);
        await adapter.SetPositionAsync(file, handle, 0, CancellationToken.None);
        var bytes = await adapter.ReadAsync(file, handle, 2, CancellationToken.None);
        var calls = proxy.MethodCalls.Count;
        var writeFailure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.WriteAsync(file, handle, [3], CancellationToken.None));
        await Assert.That(writeFailure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.MethodCalls.Count).IsEqualTo(calls);
        await adapter.CloseAsync(file, handle, CancellationToken.None);

        await Assert.That(bytes).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(string.Join("|", proxy.MethodCalls.Select(call => call.MethodId.IdentifierAsString)))
            .IsEqualTo("Actual.Open|Actual.SetPosition|Actual.Read|Actual.Close");
        var beforeRetry = proxy.NativeOperationCount;
        var retry = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CloseAsync(file, handle, CancellationToken.None));
        await Assert.That(retry!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(beforeRetry);
    }

    [Test]
    [Arguments((byte)0)]
    [Arguments((byte)9)]
    [Arguments((byte)255)]
    public async Task FileTransferOpenPreCancellationPrecedesModeAndChannelInspection(byte mode)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await adapter.OpenAsync(new NodeId("File", 1), mode, cancellation.Token));

        await Assert.That(failure!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(proxy.EndpointReads).IsEqualTo(0);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(MessageSecurityMode.None)]
    [Arguments(MessageSecurityMode.Sign)]
    [Arguments(MessageSecurityMode.Invalid)]
    public async Task MutationsRejectUnencryptedChannelsBeforeNativeOperations(MessageSecurityMode mode)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = new EndpointDescription { SecurityMode = mode };
        var node = new NodeId("Target", 1);
        Func<ValueTask>[] mutations =
        [
            async () => { _ = await adapter.OpenAsync(node, 2, CancellationToken.None); },
            async () => { _ = await adapter.OpenAsync(node, 6, CancellationToken.None); },
            async () => { _ = await adapter.OpenAsync(node, 10, CancellationToken.None); },
            () => adapter.WriteAsync(node, 7, [1], CancellationToken.None),
            () => adapter.WriteValueAsync(node, new Variant("value"), CancellationToken.None),
            async () => { _ = await adapter.CallMethodAsync(node, "GetOrCreateResource", [], CancellationToken.None); },
            () => adapter.SetPositionAsync(node, 7, 0, CancellationToken.None),
            () => adapter.CloseAsync(node, 7, CancellationToken.None)
        ];
        foreach (var mutation in mutations)
        {
            var failure = await Assert.ThrowsAsync<FederationException>(async () => await mutation());
            await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        }
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(MessageSecurityMode.None)]
    [Arguments(MessageSecurityMode.Sign)]
    public async Task NamedMutationRechecksChannelAfterMethodDiscovery(MessageSecurityMode changedMode)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.BrowseResultFactory = _ =>
        {
            proxy.Endpoint = new EndpointDescription { SecurityMode = changedMode };
            return new BrowseResult
            {
                References = [new ReferenceDescription
                {
                    NodeClass = NodeClass.Method,
                    BrowseName = new QualifiedName("Delete"),
                    NodeId = new ExpandedNodeId(55u)
                }]
            };
        };

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CallMethodAsync(new NodeId("Target", 1), "Delete", [], CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(1);
    }

    [Test]
    public async Task MutationsRejectMissingChannelSecurityBeforeNativeOperations()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = null;

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.WriteValueAsync(new NodeId("Target", 1), new Variant("value"), CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(0);
    }

    [Test]
    public async Task MutationPreCancellationAvoidsChannelAccessAndNativeOperations()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await adapter.WriteValueAsync(new NodeId("Target", 1), new Variant("value"), cancellation.Token));

        await Assert.That(failure!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(proxy.EndpointReads).IsEqualTo(0);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("file")]
    [Arguments("session")]
    [Arguments("handle")]
    [Arguments("closed")]
    public async Task ReadHandleExemptionDoesNotAuthorizeAnotherFileSessionOrHandle(string changed)
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.None };
        var file = new NodeId("File", 1);
        var handle = await adapter.OpenAsync(file, 1, CancellationToken.None);
        if (changed == "session")
        {
            proxy.SessionId = new NodeId("AnotherSession", 0);
        }
        if (changed == "closed")
        {
            await adapter.CloseAsync(file, handle, CancellationToken.None);
        }
        var before = proxy.NativeOperationCount;

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CloseAsync(changed == "file" ? new NodeId("AnotherFile", 1) : file,
                changed == "handle" ? handle + 1 : handle, CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(before);
    }

    [Test]
    public async Task ReadHandleCanCloseOnAnUnsecuredChannelAndRetryNativeCloseFailure()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.None };
        var file = new NodeId("File", 1);
        var handle = await adapter.OpenAsync(file, 1, CancellationToken.None);
        proxy.CallStatus = StatusCodes.BadResourceUnavailable;
        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CloseAsync(file, handle, CancellationToken.None));
        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        proxy.CallStatus = StatusCodes.Good;

        await adapter.SetPositionAsync(file, handle, 0, CancellationToken.None);
        await adapter.CloseAsync(file, handle, CancellationToken.None);

        await Assert.That(proxy.NativeOperationCount).IsEqualTo(9);
        await Assert.That(proxy.MethodCalls.Select(call => call.MethodId)).IsEquivalentTo(new NodeId[]
        {
            new("Actual.Open", 1), new("Actual.Close", 1), new("Actual.SetPosition", 1),
            new("Actual.Close", 1)
        });
    }

    [Test]
    public async Task ReusedReadHandleDoesNotAuthorizeCommittingAWritableOpen()
    {
        var (adapter, proxy) = CreateAdapter();
        var file = new NodeId("File", 1);
        var readHandle = await adapter.OpenAsync(file, 1, CancellationToken.None);
        var writeHandle = await adapter.OpenAsync(file, 6, CancellationToken.None);
        await Assert.That(writeHandle).IsEqualTo(readHandle);
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.None };
        var before = proxy.NativeOperationCount;

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CloseAsync(file, writeHandle, CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(before);
    }

    [Test]
    public async Task WritableHandleCloseRejectsAChannelDowngradeAndCanRetrySecurely()
    {
        var (adapter, proxy) = CreateAdapter();
        var file = new NodeId("File", 1);
        var handle = await adapter.OpenAsync(file, 6, CancellationToken.None);
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.Sign };
        var before = proxy.NativeOperationCount;

        var failure = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.CloseAsync(file, handle, CancellationToken.None));

        await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(before);
        proxy.Endpoint = new EndpointDescription { SecurityMode = MessageSecurityMode.SignAndEncrypt };
        await adapter.CloseAsync(file, handle, CancellationToken.None);
        await Assert.That(proxy.NativeOperationCount).IsEqualTo(before + 2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegistryOriginCaptureResolvesSourceContextAndSurvivesTableReordering(bool opaque)
    {
        var (adapter, proxy) = CreateAdapter();
        object identifier = opaque ? new byte[] { 0, 128, 255 } : "Registry";
        var expected = OpcUaRegistryOrigin.Application(new(
            s_serverUris[2], s_externalNamespaceUris[2], opaque ? IdType.Opaque : IdType.String, identifier));
        var node = opaque ? new NodeId(ByteString.From((byte[])identifier), 2) : new NodeId((string)identifier, 2);
        var captured = await OpcUaRegistryOrigin.CaptureAsync(
            new("", s_serverUris[2], new ExpandedNodeId(node, null, 2u)), adapter);
        proxy.ServerArray = [s_serverUris[0], s_serverUris[2], s_serverUris[1]];
        proxy.NamespaceArray = [s_externalNamespaceUris[0], s_externalNamespaceUris[2], s_externalNamespaceUris[1]];
        var reorderedNode = opaque ? new NodeId(ByteString.From((byte[])identifier), 1) :
            new NodeId((string)identifier, 1);
        var recaptured = await OpcUaRegistryOrigin.CaptureAsync(
            new("", s_serverUris[2], new ExpandedNodeId(reorderedNode, null, 1u)), adapter);

        await Assert.That(captured.Equals(expected)).IsTrue();
        await Assert.That(captured.Equals(recaptured)).IsTrue();
        await Assert.That(captured.GetHashCode()).IsEqualTo(recaptured.GetHashCode());
        await Assert.That(captured.RegistryRoot!.NamespaceUri).IsEqualTo(s_externalNamespaceUris[2]);
        await Assert.That(captured.RegistryRoot.MapToLocalNodeId(s_serverUris[2],
            new NamespaceTable(proxy.NamespaceArray))).IsEqualTo(reorderedNode);
    }

    [Test]
    public async Task RegistryOriginCaptureRejectsServerUriThatContradictsSourceTableIdentity()
    {
        var (adapter, _) = CreateAdapter();

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryOrigin.CaptureAsync(
                new("", s_serverUris[1], new ExpandedNodeId(new NodeId("Registry", 2), null, 2u)), adapter));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("server")]
    [Arguments("namespace")]
    public async Task RegistryOriginCaptureRejectsMissingSourceTableContext(string state)
    {
        var (adapter, proxy) = CreateAdapter();
        if (state == "server")
        {
            proxy.ServerArray = [];
        }
        else
        {
            proxy.NamespaceArray = [];
        }

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryOrigin.CaptureAsync(
                new("", s_serverUris[2], new ExpandedNodeId(new NodeId("Registry", 2), null, 2u)), adapter));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RegistryOriginCaptureStableFormNeedsNoNativeTableOrManufacturedRoot()
    {
        var (adapter, proxy) = CreateAdapter();
        proxy.ServiceFailure = new InvalidOperationException("Stable origin must not read native tables.");
        var captured = await OpcUaRegistryOrigin.CaptureAsync(new("urn:Origin:Exact%2f", null, ExpandedNodeId.Null),
            adapter);

        await Assert.That(captured.Equals(OpcUaRegistryOrigin.Stable("urn:Origin:Exact%2f"))).IsTrue();
        await Assert.That(captured.RegistryRoot).IsNull();
        await Assert.That(proxy.ReadCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RegistryOriginCapturePreCancellationAvoidsSourceTableReads(bool stable)
    {
        var (adapter, proxy) = CreateAdapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var value = stable ? new OpcUaRegistryOriginValue("urn:origin", "", ExpandedNodeId.Null) :
            new("", s_serverUris[2], new ExpandedNodeId(new NodeId("Registry", 2), null, 2u));

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await OpcUaRegistryOrigin.CaptureAsync(value, adapter, cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(proxy.ReadCount).IsEqualTo(0);
    }

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
    [Arguments("numeric")]
    [Arguments("string")]
    [Arguments("guid")]
    [Arguments("opaque")]
    [Arguments("uri")]
    [Arguments("null")]
    [Arguments("stable")]
    public async Task RegistryOriginDecodedBodyPreservesScalarFieldsAndSourceNodeContext(string kind)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var root = kind switch
        {
            "numeric" => new ExpandedNodeId(new NodeId(42u, 2), null, 2u),
            "string" => new ExpandedNodeId(new NodeId("CaseSensitive%2f", 2), null, 2u),
            "guid" => new ExpandedNodeId(new NodeId(Guid.Parse("9b81dd7d-333a-4746-ad52-403a07e353df"), 2),
                null, 2u),
            "opaque" => new ExpandedNodeId(new NodeId(ByteString.From(new byte[] { 0, 128, 255 }), 2),
                null, 2u),
            "uri" => new ExpandedNodeId(new NodeId("Registry", 2), s_externalNamespaceUris[2], 2u),
            _ => ExpandedNodeId.Null
        };
        var originUri = kind == "null" ? null : kind == "stable" ? "urn:Origin:Exact%2f" : "";
        var serverUri = kind == "null" ? null : kind == "stable" ? "" : s_serverUris[2];
        proxy.Extension = new ExtensionObject(new OriginStructure(originUri, serverUri, root), true);

        var origin = await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
            new NodeId(338u), CancellationToken.None);

        await Assert.That(origin.OriginUri).IsEqualTo(originUri);
        await Assert.That(origin.ServerUri).IsEqualTo(serverUri);
        await Assert.That(origin.RegistryNodeId).IsEqualTo(root);
    }

    [Test]
    public async Task RegistryOriginDecodedBodyRejectsContradictoryDataTypeBeforeEncoding()
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var encoded = false;
        proxy.Extension = new ExtensionObject(new OriginStructure("urn:origin", "", ExpandedNodeId.Null,
            () => encoded = true, typeId: new ExpandedNodeId(339u)), true);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(encoded).IsFalse();
    }

    [Test]
    [Arguments("truncated")]
    [Arguments("extra")]
    public async Task RegistryOriginDecodedBodyRejectsMalformedScalarStructure(string state)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        proxy.Extension = new ExtensionObject(new OriginStructure("urn:origin", "", ExpandedNodeId.Null,
            malformed: state), true);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RegistryOriginDecodedBodyRetainsNativeEncodingFailure()
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var failure = new ServiceResultException(StatusCodes.BadEncodingError, "Origin encoding failed.");
        proxy.Extension = new ExtensionObject(new OriginStructure("urn:origin", "", ExpandedNodeId.Null,
            () => throw failure), true);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), CancellationToken.None));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(ReferenceEquals(error.InnerException, failure)).IsTrue();
        await Assert.That(error.Diagnostic).IsEqualTo(failure.StatusCode.ToString());
    }

    [Test]
    public async Task RegistryOriginDecodedBodyPreservesCancellationDuringEncoding()
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        proxy.Extension = new ExtensionObject(new OriginStructure("urn:origin", "", ExpandedNodeId.Null,
            cancellation.Cancel), true);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await adapter.ReadRegistryOriginAsync(new NodeId("OriginRegistry", 2),
                new NodeId(338u), cancellation.Token));

        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(proxy.ReadCount).IsEqualTo(3);
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
    [Arguments(65535, true, false)]
    [Arguments(65536, true, false)]
    [Arguments(65537, false, false)]
    [Arguments(65535, true, true)]
    [Arguments(65536, true, true)]
    [Arguments(65537, false, true)]
    public async Task RegistryOriginDecodingEnforcesExactStructureByteBudget(
        int length, bool succeeds, bool decodedBody)
    {
        var (adapter, proxy) = CreateAdapter();
        await adapter.ReadNamespaceArrayAsync(CancellationToken.None);
        var overhead = EncodeOriginBytes("", "", ExpandedNodeId.Null).Length;
        var originUri = new string('a', length - overhead);
        var bytes = EncodeOriginBytes(originUri, "", ExpandedNodeId.Null);
        await Assert.That(bytes.Length).IsEqualTo(length);
        proxy.Extension = decodedBody
            ? new ExtensionObject(new OriginStructure(originUri, "", ExpandedNodeId.Null), true)
            : new ExtensionObject(new ExpandedNodeId(43u), ByteString.From(bytes));

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

    private sealed class OriginStructure(
        string? originUri, string? serverUri, ExpandedNodeId root, Action? beforeEncode = null,
        ExpandedNodeId? typeId = null, string? malformed = null) : IEncodeable
    {
        public ExpandedNodeId TypeId => typeId ?? new(338u);
        public ExpandedNodeId BinaryEncodingId => new(43u);
        public ExpandedNodeId XmlEncodingId => new(44u);

        public void Encode(IEncoder encoder)
        {
            beforeEncode?.Invoke();
            encoder.WriteString("OriginUri", originUri);
            encoder.WriteString("ServerUri", serverUri);
            if (malformed != "truncated")
            {
                encoder.WriteExpandedNodeId("RegistryNodeId", root);
            }
            if (malformed == "extra")
            {
                encoder.WriteByte("Extra", 1);
            }
        }

        public void Decode(IDecoder decoder) => throw new NotSupportedException();
        public bool IsEqual(IEncodeable? encodeable) => ReferenceEquals(this, encodeable);
        public object Clone() => MemberwiseClone();
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
        public EndpointDescription? Endpoint { get; set; } =
            new() { SecurityMode = MessageSecurityMode.SignAndEncrypt };
        public NodeId SessionId { get; set; } = new("Session", 0);
        public int EndpointReads { get; private set; }
        public int NativeOperationCount { get; private set; }
        public List<CallMethodRequest> MethodCalls { get; } = [];
        public List<BrowseDescription> BrowseRequests { get; } = [];
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
        public ArrayOf<ReferenceDescription> NextBrowseReferences { get; set; }
        public Action? AfterBrowse { get; set; }
        public Action? AfterBrowseNext { get; set; }
        public Action? AfterCall { get; set; }
        public byte[] ReadMethodBytes { get; set; } = [1, 2];
        public Func<CallMethodRequest, ArrayOf<Variant>>? MethodOutputs { get; set; }
        public List<CancellationToken> MethodTokens { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Endpoint")
            {
                EndpointReads++;
                return Endpoint;
            }
            if (targetMethod?.Name == "get_SessionId")
            {
                return SessionId;
            }
            NativeOperationCount++;
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
                    var call = ((ArrayOf<CallMethodRequest>)args![1]!)[0];
                    MethodCalls.Add(call);
                    MethodTokens.Add((CancellationToken)args[2]!);
                    AfterCall?.Invoke();
                    return new ValueTask<CallResponse>(new CallResponse
                    {
                        ResponseHeader = Header,
                        DiagnosticInfos = Diagnostics,
                        Results = [new CallMethodResult
                        {
                            StatusCode = CallStatus,
                            OutputArguments = MethodOutputs?.Invoke(call) ??
                                (call.MethodId.IdentifierAsString switch
                                {
                                    "Actual.Read" => [new Variant(new ByteString(ReadMethodBytes))],
                                    "Actual.Write" or "Actual.SetPosition" or "Actual.Close" => [],
                                    _ => [new Variant(7u)]
                                }),
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
            BrowseRequests.Add(nodes[0]);
            var dataType = new ReferenceDescription
            {
                ReferenceTypeId = ReferenceTypeIds.HasEncoding,
                IsForward = false,
                NodeId = new ExpandedNodeId(new NodeId(338u, 0)),
                BrowseName = new QualifiedName(DataTypeBrowseName, 0),
                NodeClass = NodeClass.DataType
            };
            var response = new BrowseResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [BrowseResultFactory?.Invoke(nodes[0]) ?? new BrowseResult
                {
                    References = nodes[0].BrowseDirection == BrowseDirection.Forward &&
                        nodes[0].ReferenceTypeId == ReferenceTypeIds.HierarchicalReferences
                        ? new ArrayOf<ReferenceDescription>(s_fileMethods
                            .Select(name => new ReferenceDescription
                            {
                                NodeId = new ExpandedNodeId(new NodeId("Actual." + name, 1)),
                                BrowseName = new QualifiedName(name),
                                NodeClass = NodeClass.Method
                            }).ToArray())
                        : [dataType],
                    StatusCode = BrowseStatus,
                    ContinuationPoint = new ByteString(BrowseContinuation)
                }]
            };
            AfterBrowse?.Invoke();
            return new(response);
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
            var response = new BrowseNextResponse
            {
                ResponseHeader = Header,
                DiagnosticInfos = Diagnostics,
                Results = [new BrowseResult
                {
                    StatusCode = release ? ReleaseStatus : NextBrowseStatus ?? BrowseStatus,
                    ContinuationPoint = release ? default : new ByteString(continuation),
                    References = release ? default : NextBrowseReferences
                }]
            };
            AfterBrowseNext?.Invoke();
            return new(response);
        }
    }
}
