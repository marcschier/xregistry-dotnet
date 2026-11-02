// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;
using Opc.Ua.Server;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaRegistryEventSubscriptionIntegrationTests
{
    private static readonly byte[] ExpectedVersionDocument = [0x00, 0x7f, 0x80, 0xff];
    private static readonly byte[] InitialWritableDocument = "initial document"u8.ToArray();

    [Test]
    [Arguments(false, false, "none")]
    [Arguments(true, false, "none")]
    [Arguments(false, true, "none")]
    [Arguments(true, true, "none")]
    [Arguments(false, false, "service")]
    [Arguments(false, true, "service")]
    [Arguments(false, false, "repeated")]
    [Arguments(false, false, "already-absent")]
    [Arguments(false, false, "cancel")]
    [Arguments(false, false, "interrupted-without-cancellation")]
    public async Task SubscriptionCreatesAndRemovesAnEventMonitoredItemOnARealServer(
        bool cancelStop, bool concurrentStop, string deletionFailure)
    {
        var failDeletion = deletionFailure != "none";
        var expectedDeletionRequests = deletionFailure is "repeated" or "already-absent" ? 3 :
            failDeletion ? 2 : 1;
        await using var server = await InProcessRegistryServer.StartAsync();
        var telemetry = DefaultTelemetry.Create(static _ => { });
        await using var clientApplication = new ApplicationInstance(telemetry)
        {
            ApplicationName = "XRegistry OPC UA Integration Client",
            ApplicationType = ApplicationType.Client,
        };
        var clientCertificates = ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates(
            "CN=XRegistry OPC UA Integration Client, O=xregistry-dotnet, DC=localhost",
            CertificateStoreType.Directory,
            server.PkiRoot);
        var clientConfiguration = await clientApplication
            .Build("urn:xregistry:opcua:integration:client", "uri:xregistry.org:opcua:integration:client")
            .AsClient()
            .AddSecurityConfiguration(clientCertificates, server.PkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync();
        if (!await clientApplication.CheckApplicationInstanceCertificatesAsync(true))
        {
            throw new InvalidOperationException("The integration client certificate is invalid.");
        }
        var target = OpcUaFederationTarget.FromAdvertisement(CatalogDescription.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                federationprofiles = new[]
                {
                    new
                    {
                        binding = "opcua",
                        endpoint = server.Endpoint,
                        parameters = new
                        {
                            registryroot =
                                $"nsu={Uri.EscapeDataString(InProcessRegistryServer.NamespaceUri)};s=Registry",
                        },
                    },
                },
            })).Select(new(["opcua"])));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var connection = await OpcUaSessionConnection.OpenAsync(
            target,
            clientConfiguration,
            telemetry,
            new UserIdentity(),
            server.RegistryType,
            static endpoint => endpoint.SecurityMode == MessageSecurityMode.None &&
                endpoint.SecurityPolicyUri == SecurityPolicies.None,
            static profile => profile == Profiles.UaTcpTransport,
            static version => version == "1.0",
            cancellation.Token);
        await using var subscription = await OpcUaRegistryEventSubscription.CreateAsync(
            connection,
            connection.RegistryRoot,
            cancellation.Token);

        await Assert.That(connection.SpecVersion).IsEqualTo("1.0");
        await Assert.That(connection.Session.SubscriptionCount).IsEqualTo(1);
        if (cancelStop)
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await subscription.StopAsync(cancelled.Token));
                await Assert.That(connection.Session.SubscriptionCount).IsEqualTo(1);
            }
        }
        var nativeSubscription = connection.Session.Subscriptions.Single();
        var originalSubscriptionId = nativeSubscription.Id;
        await Assert.That(nativeSubscription.Created).IsTrue();
        await Assert.That(nativeSubscription.CurrentPublishingEnabled).IsTrue();
        var nativeMonitoredItem = nativeSubscription.MonitoredItems.Single();
        await Assert.That(nativeMonitoredItem.Status.Created).IsTrue();
        var received = new TaskCompletionSource<OpcUaRegistryEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        subscription.EventReceived += (_, args) =>
        {
            Interlocked.Increment(ref delivered);
            received.TrySetResult(args.Value);
        };
        subscription.NotificationFailed += (_, error) => received.TrySetException(error);
        var time = new DateTimeOffset(2026, 9, 15, 12, 30, 45, TimeSpan.Zero);
        server.ReportRegistryCreatedEvent(time);

        var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);

        await Assert.That(notification.Type).IsEqualTo("io.xregistry.registry.created");
        await Assert.That(notification.EventType)
            .IsEqualTo(new NodeId("RegistryCreatedEventType", connection.RegistryRoot.NamespaceIndex));
        await Assert.That(notification.Id).IsEqualTo("AQID");
        await Assert.That(notification.SourceNode).IsEqualTo(connection.RegistryRoot);
        await Assert.That(notification.SourceName).IsEqualTo("Registry");
        await Assert.That(notification.SourceUrl).IsEqualTo(InProcessRegistryServer.EventSourceUrl);
        await Assert.That(notification.Subject).IsEqualTo("/");
        await Assert.That(notification.Time).IsEqualTo(time);
        await Assert.That(notification.Epoch).IsEqualTo(1u);
        await Assert.That(notification.MetaEpoch).IsNull();
        await Assert.That(notification.Changed).IsNull();
        await Assert.That(notification.CorrelationId).IsNull();
        if (deletionFailure == "cancel")
        {
            server.BlockSubscriptionDeletion();
            using var interrupted = new CancellationTokenSource();
            var stopping = subscription.StopAsync(interrupted.Token).AsTask();
            Task? joinedStop = null;
            try
            {
                await server.SubscriptionDeletionEntered.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
                joinedStop = subscription.StopAsync().AsTask();
                await interrupted.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await stopping);
            }
            finally
            {
                server.ReleaseSubscriptionDeletion();
            }
            var interruption = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await joinedStop!);
            await Assert.That(interruption!.CancellationToken).IsEqualTo(interrupted.Token);
            await Assert.That(interruption.InnerException is FederationException).IsTrue();
            var nativeFailure = (FederationException)interruption.InnerException!;
            await Assert.That(nativeFailure.InnerException is ServiceResultException).IsTrue();
            await Assert.That(((ServiceResultException)nativeFailure.InnerException!).StatusCode)
                .IsEqualTo(StatusCodes.BadRequestInterrupted);
            await Assert.That(nativeSubscription.Created).IsFalse();
            await Assert.That(connection.Session.SubscriptionCount).IsEqualTo(1);
            await Assert.That(server.SubscriptionDeletionRequests).IsEqualTo(1);
        }
        else if (failDeletion)
        {
            var remoteSubscriptionId = nativeSubscription.Id;
            var expectedStatus = deletionFailure == "interrupted-without-cancellation"
                ? StatusCodes.BadRequestInterrupted : StatusCodes.BadResourceUnavailable;
            server.FailNextSubscriptionDeletion(expectedStatus);
            var failure = await Assert.ThrowsAsync<FederationException>(
                async () => await subscription.StopAsync(cancellation.Token));
            await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.Unavailable);
            await Assert.That(failure.InnerException is ServiceResultException).IsTrue();
            await Assert.That(((ServiceResultException)failure.InnerException!).StatusCode)
                .IsEqualTo(expectedStatus);
            await Assert.That(cancellation.IsCancellationRequested).IsFalse();
            await Assert.That(nativeSubscription.Created).IsFalse();
            await Assert.That(connection.Session.SubscriptionCount).IsEqualTo(1);
            await Assert.That(server.SubscriptionDeletionRequests).IsEqualTo(1);
            if (deletionFailure == "repeated")
            {
                server.FailNextSubscriptionDeletion();
                var retryFailure = await Assert.ThrowsAsync<FederationException>(
                    async () => await subscription.StopAsync(cancellation.Token));
                await Assert.That(retryFailure!.Code).IsEqualTo(FederationErrorCode.Unavailable);
                await Assert.That(retryFailure.InnerException is ServiceResultException).IsTrue();
                await Assert.That(((ServiceResultException)retryFailure.InnerException!).StatusCode)
                    .IsEqualTo(StatusCodes.BadResourceUnavailable);
                await Assert.That(connection.Session.SubscriptionCount).IsEqualTo(1);
                await Assert.That(server.SubscriptionDeletionRequests).IsEqualTo(2);
            }
            else if (deletionFailure == "already-absent")
            {
                var removed = await connection.Session.DeleteSubscriptionsAsync(
                    new RequestHeader(), [remoteSubscriptionId], cancellation.Token);
                await Assert.That(removed.Results.Count).IsEqualTo(1);
                await Assert.That(StatusCode.IsGood(removed.Results[0])).IsTrue();
                await Assert.That(server.SubscriptionDeletionRequests).IsEqualTo(2);
            }
        }
        if (concurrentStop)
        {
            server.BlockSubscriptionDeletion();
            var firstStop = subscription.StopAsync(cancellation.Token).AsTask();
            try
            {
                await server.SubscriptionDeletionEntered.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
                var secondStop = subscription.StopAsync(cancellation.Token).AsTask();
                var disposal = subscription.DisposeAsync().AsTask();
                try
                {
                    await Assert.That(firstStop.IsCompleted).IsFalse();
                    await Assert.That(secondStop.IsCompleted).IsFalse();
                    await Assert.That(disposal.IsCompleted).IsFalse();
                    using var waiting = new CancellationTokenSource();
                    var cancelledStop = subscription.StopAsync(waiting.Token).AsTask();
                    await waiting.CancelAsync();
                    await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelledStop);
                    await Assert.That(firstStop.IsCompleted).IsFalse();
                    await Assert.That(connection.Session.SubscriptionCount).IsEqualTo(1);
                }
                finally
                {
                    server.ReleaseSubscriptionDeletion();
                    await Task.WhenAll(firstStop, secondStop, disposal).WaitAsync(cancellation.Token);
                }
            }
            finally
            {
                server.ReleaseSubscriptionDeletion();
                await firstStop.WaitAsync(cancellation.Token);
            }
            await Assert.That(server.SubscriptionDeletionRequests).IsEqualTo(expectedDeletionRequests);
        }
        else
        {
            await subscription.StopAsync(cancellation.Token);
        }
        await Assert.That(connection.Session.SubscriptionCount).IsEqualTo(0);
        await Assert.That(nativeSubscription.Created).IsFalse();
        await Assert.That(server.SubscriptionDeletionRequests).IsEqualTo(expectedDeletionRequests);
        var absence = await connection.Session.DeleteSubscriptionsAsync(
            new RequestHeader(), [originalSubscriptionId], cancellation.Token);
        await Assert.That(absence.Results.Count).IsEqualTo(1);
        await Assert.That(absence.Results[0]).IsEqualTo(StatusCodes.BadSubscriptionIdInvalid);
        expectedDeletionRequests++;
        await subscription.StopAsync(cancellation.Token);
        await subscription.DisposeAsync();
        await Assert.That(connection.Session.SubscriptionCount).IsEqualTo(0);
        await Assert.That(server.SubscriptionDeletionRequests).IsEqualTo(expectedDeletionRequests);
        await Assert.That(Volatile.Read(ref delivered)).IsEqualTo(1);
    }

    [Test]
    [Arguments("target-only")]
    [Arguments("stable")]
    [Arguments("application")]
    public async Task TrustedProviderResolverReadsAnExternalVersionFromAnAlternateTargetModel(string originForm)
    {
        await using var targetServer = await InProcessRegistryServer.StartAsync(alternateModelLayout: true);
        await using var sourceServer = await InProcessRegistryServer.StartAsync(targetServer.ApplicationUri);
        var telemetry = DefaultTelemetry.Create(static _ => { });
        await using var clientApplication = new ApplicationInstance(telemetry)
        {
            ApplicationName = "XRegistry OPC UA Federation Integration Client",
            ApplicationType = ApplicationType.Client,
        };
        var clientCertificates = ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates(
            "CN=XRegistry OPC UA Federation Integration Client, O=xregistry-dotnet, DC=localhost",
            CertificateStoreType.Directory,
            sourceServer.PkiRoot);
        var clientConfiguration = await clientApplication
            .Build("urn:xregistry:opcua:federation:integration:client",
                "uri:xregistry.org:opcua:federation:integration:client")
            .AsClient()
            .AddSecurityConfiguration(clientCertificates, sourceServer.PkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync();
        if (!await clientApplication.CheckApplicationInstanceCertificatesAsync(true))
        {
            throw new InvalidOperationException("The federation integration client certificate is invalid.");
        }

        var sourceTarget = CreateTarget(sourceServer.Endpoint);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var sourceConnection = await OpcUaSessionConnection.OpenAsync(
            sourceTarget,
            clientConfiguration,
            telemetry,
            new UserIdentity(),
            sourceServer.RegistryType,
            static endpoint => endpoint.SecurityMode == MessageSecurityMode.None &&
                endpoint.SecurityPolicyUri == SecurityPolicies.None,
            static profile => profile == Profiles.UaTcpTransport,
            static version => version == "1.0",
            cancellation.Token);
        _ = await sourceConnection.Reader.ReadModelDefinitionAsync(
            sourceConnection.RegistryRoot, cancellationToken: cancellation.Token);
        var identity = await sourceConnection.Reader.ReadExternalNodeIdAsync(
            sourceServer.GroupNode,
            "/teams/team-a",
            "metadata",
            "metadata-a",
            sourceServer.MetadataResourceType,
            cancellationToken: cancellation.Token);

        await Assert.That(identity).IsNotNull();
        await Assert.That(identity!.ServerApplicationUri).IsEqualTo(targetServer.ApplicationUri);
        await Assert.That(identity.NamespaceUri).IsEqualTo(InProcessRegistryServer.NamespaceUri);
        await Assert.That(identity.Identifier).IsEqualTo("Registry.MetadataResource");

        var versionIdentity = await sourceConnection.Reader.ReadExternalNodeIdAsync(
            sourceServer.GroupNode,
            "/teams/team-a",
            "versioned",
            "versioned-a",
            sourceServer.VersionedResourceType,
            "v1",
            sourceServer.ResourceVersionsType,
            cancellation.Token);
        await Assert.That(versionIdentity).IsNotNull();
        await Assert.That(versionIdentity!.ServerApplicationUri).IsEqualTo(targetServer.ApplicationUri);
        await Assert.That(versionIdentity.NamespaceUri).IsEqualTo(InProcessRegistryServer.NamespaceUri);
        await Assert.That(versionIdentity.Identifier).IsEqualTo("Registry.VersionedResource.Versions.v1");

        var receivedIdentities = new List<OpcUaExternalNodeId>();
        var activeTargetServer = targetServer;
        OpcUaTrustedProviderResolver resolver = async (requested, token) =>
        {
            if (!string.Equals(requested.ServerApplicationUri, targetServer.ApplicationUri,
                    StringComparison.Ordinal) ||
                !string.Equals(requested.NamespaceUri, InProcessRegistryServer.NamespaceUri,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The resolver received an unexpected provider identity.");
            }

            receivedIdentities.Add(requested);
            var targetConnection = await OpcUaSessionConnection.OpenAsync(
                CreateTarget(activeTargetServer.Endpoint),
                clientConfiguration,
                telemetry,
                new UserIdentity(),
                activeTargetServer.RegistryType,
                static endpoint => endpoint.SecurityMode == MessageSecurityMode.None &&
                    endpoint.SecurityPolicyUri == SecurityPolicies.None,
                static profile => profile == Profiles.UaTcpTransport,
                static version => version == "1.0",
                token);
            return new OpcUaTrustedProviderLease(
                targetConnection,
                new NativeRegistryContext("opcua", activeTargetServer.Endpoint),
                CreateMetadataTypeMap(activeTargetServer));
        };
        var trustedOrigin = originForm == "application"
            ? OpcUaRegistryOrigin.Application(new(targetServer.ApplicationUri,
                InProcessRegistryServer.NamespaceUri, IdType.String, "Registry"))
            : OpcUaRegistryOrigin.Stable("urn:xregistry:integration:stable-origin");
        var sourceServers = await sourceConnection.Adapter.ReadServerArrayAsync(cancellation.Token);
        var targetServerIndex = sourceServers.ToList().IndexOf(targetServer.ApplicationUri);
        var capturedOrigin = await OpcUaRegistryOrigin.CaptureAsync(originForm == "application"
            ? new("", targetServer.ApplicationUri, new ExpandedNodeId(new NodeId("Registry", 0),
                InProcessRegistryServer.NamespaceUri, checked((uint)targetServerIndex)))
            : new("urn:xregistry:integration:stable-origin", "", ExpandedNodeId.Null),
            sourceConnection.Adapter, cancellation.Token);
        ValueTask<OpcUaResolvedExternalResource> Resolve(OpcUaExternalNodeId requested, OpcUaExternalTargetRole role) =>
            originForm == "target-only"
                ? OpcUaRegistryReadSource.ResolveExternalTargetAsync(requested, resolver,
                    static version => version == "1.0", role, cancellation.Token)
                : OpcUaRegistryReadSource.ResolveOriginTargetAsync(capturedOrigin, trustedOrigin, requested,
                    (_, node, token) => resolver(node, token), static version => version == "1.0", role,
                    cancellation.Token);
        await using var resolved = await Resolve(identity, OpcUaExternalTargetRole.LogicalResource);

        await Assert.That(resolved.Node).IsEqualTo(targetServer.MetadataResourceNode);
        await Assert.That(resolved.Xid).IsEqualTo("/departments/team-a/metadata/metadata-a");
        await Assert.That(resolved.SpecVersion).IsEqualTo("1.0");
        await Assert.That(resolved.Origin?.Equals(trustedOrigin) ?? false).IsEqualTo(originForm != "target-only");

        await using var resolvedVersion = await Resolve(versionIdentity, OpcUaExternalTargetRole.ExactVersion);

        await Assert.That(resolvedVersion.Node).IsEqualTo(targetServer.VersionNode);
        await Assert.That(resolvedVersion.Xid)
            .IsEqualTo("/departments/team-a/artifacts/versioned-a/versions/v1");
        var targetVersionDocument = await resolvedVersion.ReadSource.ReadAsync(
            new(FederationOperation.Document, resolvedVersion.Xid), cancellation.Token);
        await Assert.That(targetVersionDocument.SelectedXid).IsEqualTo(resolvedVersion.Xid);
        await Assert.That(targetVersionDocument.Document).IsNotNull();
        await Assert.That(targetVersionDocument.Document!.Length).IsEqualTo(ExpectedVersionDocument.Length);
        await Assert.That(targetVersionDocument.Document.ContentType).IsEqualTo("application/octet-stream");
        using (var stream = targetVersionDocument.Document.OpenRead())
        {
            var actual = new byte[ExpectedVersionDocument.Length];
            await stream.ReadExactlyAsync(actual, cancellation.Token);
            await Assert.That(actual).IsEquivalentTo(ExpectedVersionDocument);
        }
        if (originForm != "target-only")
        {
            await Assert.That(resolvedVersion.SourcePin).IsNotNull();
            var storedPin = resolvedVersion.SourcePin!.Serialize();
            var pinPath = Path.Combine(sourceServer.PkiRoot, "native-source-pin.json");
            using (var file = new FileStream(pinPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(storedPin);
                file.Flush(flushToDisk: true);
            }
            await using var relocatedServer = await InProcessRegistryServer.StartAsync(
                alternateModelLayout: true, serverApplicationUri: targetServer.ApplicationUri, extraNamespace: true);
            activeTargetServer = relocatedServer;
            var pin = OpcUaFederationSourcePin.Parse(await File.ReadAllBytesAsync(pinPath, cancellation.Token));
            await using var reconnected = await OpcUaRegistryReadSource.ResolvePinnedTargetAsync(
                pin, trustedOrigin, (_, node, token) => resolver(node, token),
                static version => version == "1.0", cancellation.Token);
            pin.ValidateReconnect(reconnected.SourcePin!);
            var document = await reconnected.ReadSource.ReadAsync(
                new(FederationOperation.Document, reconnected.Xid), cancellation.Token);
            using var stream = document.Document!.OpenRead();
            var bytes = new byte[ExpectedVersionDocument.Length];
            await stream.ReadExactlyAsync(bytes, cancellation.Token);
            await Assert.That(bytes).IsEquivalentTo(ExpectedVersionDocument);
            await Assert.That(reconnected.Xid).IsEqualTo(resolvedVersion.Xid);
            await Assert.That(relocatedServer.Endpoint == targetServer.Endpoint).IsFalse();
            await Assert.That(reconnected.ReadSource.Context.Source).IsEqualTo(relocatedServer.Endpoint);
            await Assert.That(reconnected.Node.NamespaceIndex == resolvedVersion.Node.NamespaceIndex).IsFalse();
            await Assert.That(reconnected.Node).IsEqualTo(relocatedServer.VersionNode);
            await Assert.That(reconnected.SourcePin!.Serialize()).IsEquivalentTo(storedPin);
        }
        else
        {
            await Assert.That(resolved.SourcePin).IsNull();
            await Assert.That(resolvedVersion.SourcePin).IsNull();
        }
        await Assert.That(receivedIdentities).Count().IsEqualTo(originForm == "target-only" ? 2 : 3);
        await Assert.That(receivedIdentities[0]).IsEqualTo(identity);
        await Assert.That(receivedIdentities[1]).IsEqualTo(versionIdentity);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriterWritesPropertiesDeleteAndFileDocumentsOnARealServer(bool secured)
    {
        await using var server = await InProcessRegistryServer.StartAsync();
        var telemetry = DefaultTelemetry.Create(static _ => { });
        await using var clientApplication = new ApplicationInstance(telemetry)
        {
            ApplicationName = "XRegistry OPC UA Integration Client",
            ApplicationType = ApplicationType.Client,
        };
        var clientCertificates = ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates(
            "CN=XRegistry OPC UA Integration Client, O=xregistry-dotnet, DC=localhost",
            CertificateStoreType.Directory,
            server.PkiRoot);
        var clientConfiguration = await clientApplication
            .Build("urn:xregistry:opcua:integration:client", "uri:xregistry.org:opcua:integration:client")
            .AsClient()
            .AddSecurityConfiguration(clientCertificates, server.PkiRoot)
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync();
        if (!await clientApplication.CheckApplicationInstanceCertificatesAsync(true))
        {
            throw new InvalidOperationException("The integration client certificate is invalid.");
        }
        var target = OpcUaFederationTarget.FromAdvertisement(CatalogDescription.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                federationprofiles = new[]
                {
                    new
                    {
                        binding = "opcua",
                        endpoint = server.Endpoint,
                        parameters = new
                        {
                            registryroot =
                                $"nsu={Uri.EscapeDataString(InProcessRegistryServer.NamespaceUri)};s=Registry",
                        },
                    },
                },
            })).Select(new(["opcua"])));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = await OpcUaSessionConnection.OpenAsync(
            target,
            clientConfiguration,
            telemetry,
            new UserIdentity(),
            server.RegistryType,
            endpoint => secured
                ? endpoint.SecurityMode == MessageSecurityMode.SignAndEncrypt &&
                    endpoint.SecurityPolicyUri == SecurityPolicies.Basic256Sha256
                : endpoint.SecurityMode == MessageSecurityMode.None &&
                    endpoint.SecurityPolicyUri == SecurityPolicies.None,
            static profile => profile == Profiles.UaTcpTransport,
            static version => version == "1.0",
            cancellation.Token);
        _ = await connection.Reader.ReadModelDefinitionAsync(
            connection.RegistryRoot, cancellationToken: cancellation.Token);
        var groupEntity = await connection.Reader.ReadGroupPropertiesAsync(
            connection.RegistryRoot, "teams", "team-a", server.GroupType, cancellation.Token);
        await Assert.That(groupEntity.GetProperty("region").GetString()).IsEqualTo("west");

        var appendedReadHandle = await connection.Adapter.OpenAsync(
            server.DocumentFile, 9, cancellation.Token);
        try
        {
            var eof = await connection.Adapter.ReadAsync(
                server.DocumentFile, appendedReadHandle, 3, cancellation.Token);
            await Assert.That(eof.Length).IsEqualTo(0);
            await connection.Adapter.SetPositionAsync(server.DocumentFile, appendedReadHandle, 0,
                cancellation.Token);
            var contents = await connection.Adapter.ReadAsync(
                server.DocumentFile, appendedReadHandle, 3, cancellation.Token);
            await Assert.That(contents).IsEquivalentTo(InitialWritableDocument.Take(3));
        }
        finally
        {
            await connection.Adapter.CloseAsync(server.DocumentFile, appendedReadHandle, CancellationToken.None);
        }

        var initial = await connection.Adapter.ReadJsonValueAsync(server.WriteProbe, cancellation.Token);
        await Assert.That(initial.Value.GetString()).IsEqualTo("before");
        using var value = JsonDocument.Parse("\"after\"");
        var writer = new OpcUaRegistryWriter(connection.Reader);
        if (!secured)
        {
            Func<ValueTask>[] mutations =
            [
                () => writer.WritePropertyAsync(server.WriteProbe,
                    new OpcUaJsonValue(value.RootElement.Clone(), BuiltInType.String), cancellation.Token),
                async () => { _ = await writer.CreateGroupAsync(connection.RegistryRoot, "new", cancellation.Token); },
                async () => { _ = await writer.GetOrCreateGroupAsync(connection.RegistryRoot, "team-a", cancellation.Token); },
                async () => { _ = await writer.CreateResourceAsync(server.GroupNode, "new",
                    cancellationToken: cancellation.Token); },
                async () => { _ = await writer.GetOrCreateResourceAsync(server.GroupNode, "existing",
                    cancellationToken: cancellation.Token); },
                () => writer.DeleteAsync(connection.RegistryRoot, 5, cancellation.Token),
                () => writer.AddAttributeAsync(server.LabelsNode, "label", "value", 1, cancellation.Token),
                () => writer.RemoveAttributeAsync(server.LabelsNode, "label", 1, cancellation.Token),
                () => writer.AddMetaAttributeAsync(server.MetaLabelsNode, "label", "value", 1, cancellation.Token),
                () => writer.RemoveMetaAttributeAsync(server.MetaLabelsNode, "label", 1, cancellation.Token),
                () => writer.ReplaceFileDocumentAsync(server.DocumentFile, new byte[] { 1 }, cancellation.Token),
                () => writer.ReplaceResourceDocumentAsync(server.GroupNode, "/teams/team-a", "schemas", "schema-a",
                    ObjectTypeIds.FileType, new byte[] { 1 }, cancellationToken: cancellation.Token),
                async () => { _ = await writer.CreateOrReplaceResourceDocumentAsync(
                    server.GroupNode, "/teams/team-a", "schemas", "schema-a", ObjectTypeIds.FileType,
                    server.ResourceVersionsType, new byte[] { 1 }, cancellationToken: cancellation.Token); },
                () => writer.CompleteCreatedResourceDocumentAsync(server.DocumentFile, 7,
                    new byte[] { 1 }, cancellation.Token)
            ];
            foreach (var mutation in mutations)
            {
                var failure = await Assert.ThrowsAsync<FederationException>(async () => await mutation());
                await Assert.That(failure!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
            }
            await Assert.That(server.DeleteInvocations).IsEqualTo(0);
            await Assert.That(server.AttributeMethodInvocations).IsEqualTo(0);
            var unchanged = await connection.Adapter.ReadJsonValueAsync(server.WriteProbe, cancellation.Token);
            await Assert.That(unchanged.Value.GetString()).IsEqualTo("before");
            _ = await connection.Reader.ReadFileAsync(server.DocumentFile, cancellation.Token);
            return;
        }
        await writer.WritePropertyAsync(
            server.WriteProbe,
            new OpcUaJsonValue(value.RootElement.Clone(), BuiltInType.String),
            cancellation.Token);
        var written = await connection.Adapter.ReadJsonValueAsync(server.WriteProbe, cancellation.Token);

        await Assert.That(written.BuiltInType).IsEqualTo(BuiltInType.String);
        await Assert.That(written.Value.GetString()).IsEqualTo("after");
        await writer.DeleteAsync(connection.RegistryRoot, 5, cancellation.Token);
        await Assert.That(server.DeleteInvocations).IsEqualTo(1);
        await Assert.That(server.DeleteExpectedEpoch).IsEqualTo(5u);
        await writer.DeleteAsync(connection.RegistryRoot, cancellationToken: cancellation.Token);
        await Assert.That(server.DeleteInvocations).IsEqualTo(2);
        await Assert.That(server.DeleteExpectedEpoch).IsEqualTo(0u);
        await writer.DeleteAsync(connection.RegistryRoot, 0u, cancellation.Token);
        await Assert.That(server.DeleteInvocations).IsEqualTo(3);
        await Assert.That(server.DeleteExpectedEpoch).IsEqualTo(0u);
        await writer.DeleteAsync(connection.RegistryRoot, uint.MaxValue, cancellation.Token);
        await Assert.That(server.DeleteInvocations).IsEqualTo(4);
        await Assert.That(server.DeleteExpectedEpoch).IsEqualTo(uint.MaxValue);

        var group = await writer.CreateGroupAsync(connection.RegistryRoot, "team-a", cancellation.Token);
        var resolvedGroup = await writer.GetOrCreateGroupAsync(connection.RegistryRoot, "team-a",
            cancellation.Token);
        await Assert.That(group).IsEqualTo(server.GroupNode);
        await Assert.That(resolvedGroup.GroupNode).IsEqualTo(server.GroupNode);
        await Assert.That(resolvedGroup.Created).IsTrue();

        var createdResource = await writer.CreateResourceAsync(group, "schema-a", "v1", true,
            cancellation.Token);
        await Assert.That(createdResource.ResourceNode).IsEqualTo(server.DocumentVersionFile);
        await Assert.That(createdResource.VersionId).IsEqualTo("v1");
        await Assert.That(createdResource.FileHandle).IsGreaterThan(0u);
        byte[] createdDocument = "created resource document"u8.ToArray();
        await writer.CompleteCreatedResourceDocumentAsync(
            createdResource.ResourceNode, createdResource.FileHandle, createdDocument, cancellation.Token);
        var createdReadback = await connection.Reader.ReadFileAsync(server.DocumentFile, cancellation.Token);
        await Assert.That(Convert.ToHexString(createdReadback)).IsEqualTo(Convert.ToHexString(createdDocument));

        var resolvedResource = await writer.GetOrCreateResourceAsync(group, "schema-a", "v1", true,
            cancellation.Token);
        await Assert.That(resolvedResource.ResourceNode).IsEqualTo(server.DocumentVersionFile);
        await Assert.That(resolvedResource.VersionId).IsEqualTo("v1");
        await Assert.That(resolvedResource.Created).IsFalse();
        byte[] resolvedDocument = "short"u8.ToArray();
        await writer.CompleteCreatedResourceDocumentAsync(
            resolvedResource.ResourceNode, resolvedResource.FileHandle, resolvedDocument, cancellation.Token);
        var resolvedReadback = await connection.Reader.ReadFileAsync(server.DocumentFile, cancellation.Token);
        await Assert.That(Convert.ToHexString(resolvedReadback)).IsEqualTo(Convert.ToHexString(resolvedDocument));

        foreach (var requestedVersion in new string?[] { null, "v1" })
        {
            byte[] replacement = requestedVersion is null ? "composed replacement"u8.ToArray() : "exact"u8.ToArray();
            var replaced = await writer.CreateOrReplaceResourceDocumentAsync(
                group, "/teams/team-a", "schemas", "schema-a", ObjectTypeIds.FileType,
                server.ResourceVersionsType, replacement, requestedVersion, cancellation.Token);
            await Assert.That(replaced.ResourceNode).IsEqualTo(server.DocumentVersionFile);
            await Assert.That(replaced.VersionId).IsEqualTo("v1");
            await Assert.That(replaced.Created).IsFalse();
            await Assert.That(replaced.FileHandle).IsEqualTo(0u);
            var replacementReadback = await connection.Reader.ReadFileAsync(
                server.DocumentVersionFile, cancellation.Token);
            await Assert.That(Convert.ToHexString(replacementReadback))
                .IsEqualTo(Convert.ToHexString(replacement));
        }

        var absent = await Assert.ThrowsAsync<FederationException>(async () =>
            await connection.Reader.ResolveResourceNodeAsync(group, "/teams/team-a", "schemas", "new-schema",
                ObjectTypeIds.FileType, cancellationToken: cancellation.Token));
        await Assert.That(absent!.Code).IsEqualTo(FederationErrorCode.NotFound);
        foreach (var firstCreation in new[] { true, false })
        {
            byte[] replacement = firstCreation ? "new resource document"u8.ToArray() : "new"u8.ToArray();
            var replaced = await writer.CreateOrReplaceResourceDocumentAsync(
                group, "/teams/team-a", "schemas", "new-schema", ObjectTypeIds.FileType,
                server.ResourceVersionsType, replacement, cancellationToken: cancellation.Token);
            await Assert.That(replaced.Created).IsEqualTo(firstCreation);
            await Assert.That(replaced.VersionId).IsEqualTo("v1");
            await Assert.That(replaced.FileHandle).IsEqualTo(0u);
            var selected = await connection.Reader.ResolveResourceNodeAsync(
                group, "/teams/team-a", "schemas", "new-schema", ObjectTypeIds.FileType, "v1",
                server.ResourceVersionsType, cancellation.Token);
            await Assert.That(replaced.ResourceNode).IsEqualTo(selected);
            var replacementReadback = await connection.Reader.ReadFileAsync(selected, cancellation.Token);
            await Assert.That(Convert.ToHexString(replacementReadback))
                .IsEqualTo(Convert.ToHexString(replacement));
            var unchangedOriginal = await connection.Reader.ReadFileAsync(server.DocumentFile, cancellation.Token);
            await Assert.That(Convert.ToHexString(unchangedOriginal))
                .IsEqualTo(Convert.ToHexString("exact"u8.ToArray()));
        }

        await writer.AddAttributeAsync(server.LabelsNode, "team", "alpha", 1, cancellation.Token);
        await writer.RemoveAttributeAsync(server.LabelsNode, "team", 2, cancellation.Token);
        await writer.AddMetaAttributeAsync(server.MetaLabelsNode, "region", "west", 3, cancellation.Token);
        await writer.RemoveMetaAttributeAsync(server.MetaLabelsNode, "region", 4, cancellation.Token);
        await Assert.That(server.AttributeMethodInvocations).IsEqualTo(4);

        byte[] resourceDocument = "resource document replacement"u8.ToArray();
        await writer.ReplaceResourceDocumentAsync(group, "/teams/team-a", "schemas", "schema-a",
            ObjectTypeIds.FileType, resourceDocument, cancellationToken: cancellation.Token);
        var resourceReadback = await connection.Reader.ReadFileAsync(server.DocumentFile, cancellation.Token);
        await Assert.That(Convert.ToHexString(resourceReadback)).IsEqualTo(Convert.ToHexString(resourceDocument));

        byte[] document = "replacement document"u8.ToArray();
        await writer.ReplaceFileDocumentAsync(server.DocumentFile, document, cancellation.Token);
        var readback = await connection.Reader.ReadFileAsync(server.DocumentFile, cancellation.Token);
        await Assert.That(Convert.ToHexString(readback)).IsEqualTo(Convert.ToHexString(document));
    }

    private static OpcUaFederationTarget CreateTarget(string endpoint) =>
        OpcUaFederationTarget.FromAdvertisement(CatalogDescription.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                federationprofiles = new[]
                {
                    new
                    {
                        binding = "opcua",
                        endpoint,
                        parameters = new
                        {
                            registryroot =
                                $"nsu={Uri.EscapeDataString(InProcessRegistryServer.NamespaceUri)};s=Registry",
                        },
                    },
                },
            })).Select(new(["opcua"])));

    private static OpcUaRegistryTypeMap CreateMetadataTypeMap(InProcessRegistryServer server)
    {
        string groupCollection = server.AlternateModelLayout ? "departments" : "teams";
        string versionedCollection = server.AlternateModelLayout ? "artifacts" : "versioned";
        return new(
            new Dictionary<string, NodeId>(StringComparer.Ordinal)
            {
                [groupCollection] = server.GroupType,
            },
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
            {
                [(groupCollection, "metadata")] =
                    OpcUaResourceTypeBinding.ForMetadataOnly(server.MetadataResourceType),
                [(groupCollection, versionedCollection)] =
                    new(server.VersionedResourceType, server.ResourceVersionsType),
            },
            new NodeId("AttributesType", server.GroupType.NamespaceIndex));
    }

    private sealed class InProcessRegistryServer : IAsyncDisposable
    {
        private readonly string pkiRoot;
        private readonly ApplicationInstance application;
        private readonly IntegrationServer server;
        public bool AlternateModelLayout { get; }

        private InProcessRegistryServer(
            string endpoint,
            string applicationUri,
            string pkiRoot,
            ApplicationInstance application,
            IntegrationServer server,
            bool alternateModelLayout)
        {
            Endpoint = endpoint;
            ApplicationUri = applicationUri;
            this.pkiRoot = pkiRoot;
            this.application = application;
            this.server = server;
            AlternateModelLayout = alternateModelLayout;
        }

        public const string NamespaceUri = "urn:xregistry:opcua:integration";
        public const string EventSourceUrl = "https://registry.example.test";

        public string Endpoint { get; }
        public string ApplicationUri { get; }
        public string PkiRoot => pkiRoot;
        public NodeId RegistryType => server.RegistryType;
        public NodeId GroupType => server.GroupType;
        public NodeId MetadataResourceType => server.MetadataResourceType;
        public NodeId MetadataResourceNode => server.MetadataResourceNode;
        public NodeId VersionedResourceType => server.VersionedResourceType;
        public NodeId ResourceVersionsType => server.ResourceVersionsType;
        public NodeId VersionNode => server.VersionNode;
        public NodeId WriteProbe => server.WriteProbe;
        public NodeId DocumentFile => server.DocumentFile;
        public NodeId DocumentVersionFile => server.DocumentVersionFile;
        public NodeId GroupNode => server.GroupNode;
        public NodeId LabelsNode => server.LabelsNode;
        public NodeId MetaLabelsNode => server.MetaLabelsNode;
        public int DeleteInvocations => server.DeleteInvocations;
        public uint? DeleteExpectedEpoch => server.DeleteExpectedEpoch;
        public int AttributeMethodInvocations => server.AttributeMethodInvocations;

        public void ReportRegistryCreatedEvent(DateTimeOffset time) => server.ReportRegistryCreatedEvent(time);
        public Task SubscriptionDeletionEntered => server.SubscriptionDeletionEntered.Task;
        public int SubscriptionDeletionRequests => server.SubscriptionDeletionRequests;
        public void BlockSubscriptionDeletion() => server.BlockSubscriptionDeletion = true;
        public void ReleaseSubscriptionDeletion() => server.ContinueSubscriptionDeletion.TrySetResult();
        public void FailNextSubscriptionDeletion(StatusCode? status = null) =>
            server.NextSubscriptionDeletionFailure = status ?? StatusCodes.BadResourceUnavailable;

        public static async ValueTask<InProcessRegistryServer> StartAsync(
            string? externalServerApplicationUri = null,
            bool alternateModelLayout = false, string? serverApplicationUri = null, bool extraNamespace = false)
        {
            var telemetry = DefaultTelemetry.Create(static _ => { });
            var application = new ApplicationInstance(telemetry)
            {
                ApplicationName = "XRegistry OPC UA Integration Server",
                ApplicationType = ApplicationType.Server,
            };
            var port = GetFreePort();
            var endpoint = $"opc.tcp://localhost:{port}/xregistry-integration";
            var applicationUri = serverApplicationUri ??
                "urn:xregistry:opcua:integration:server:" + Guid.NewGuid().ToString("N");
            var pkiRoot = Path.Combine(Path.GetTempPath(), "xregistry-opcua-integration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pkiRoot);

            var certificates = ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates(
                "CN=XRegistry OPC UA Integration Server, O=xregistry-dotnet, DC=localhost",
                CertificateStoreType.Directory,
                pkiRoot);
            var configuration = await application
                .Build(applicationUri, "uri:xregistry.org:opcua:integration:server")
                .AsServer([endpoint])
                .AddUnsecurePolicyNone()
                .AddSignAndEncryptPolicies()
                .SetShutdownDelay(0)
                .AddSecurityConfiguration(certificates, pkiRoot)
                .SetAutoAcceptUntrustedCertificates(true)
                .CreateAsync();
            if (!await application.CheckApplicationInstanceCertificatesAsync(true))
            {
                throw new InvalidOperationException("The integration server certificate is invalid.");
            }
            var server = new IntegrationServer(telemetry, externalServerApplicationUri, alternateModelLayout,
                extraNamespace);
            await application.StartAsync(server, CancellationToken.None);
            return new(endpoint, applicationUri, pkiRoot, application, server, alternateModelLayout);
        }

        public async ValueTask DisposeAsync()
        {
            await server.StopAsync(CancellationToken.None);
            await application.DisposeAsync();
            if (Directory.Exists(pkiRoot))
            {
                Directory.Delete(pkiRoot, recursive: true);
            }
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class IntegrationServer(
        ITelemetryContext telemetry,
        string? externalServerApplicationUri,
        bool alternateModelLayout, bool extraNamespace) : StandardServer(telemetry)
    {
        private NodeId registryType = NodeId.Null;
        private NodeId writeProbe = NodeId.Null;
        private IntegrationNodeManager? nodeManager;
        public NodeId RegistryType => registryType.IsNull
            ? throw new InvalidOperationException("The RegistryType was not initialized.")
            : registryType;
        public NodeId GroupType => nodeManager?.GroupType ??
            throw new InvalidOperationException("The GroupType was not initialized.");
        public NodeId MetadataResourceType => nodeManager?.MetadataResourceType ??
            throw new InvalidOperationException("The MetadataResourceType was not initialized.");
        public NodeId MetadataResourceNode => nodeManager?.MetadataResourceNode ??
            throw new InvalidOperationException("The MetadataResource was not initialized.");
        public NodeId VersionedResourceType => nodeManager?.VersionedResourceType ??
            throw new InvalidOperationException("The VersionedResourceType was not initialized.");
        public NodeId ResourceVersionsType => nodeManager?.ResourceVersionsType ??
            throw new InvalidOperationException("The ResourceVersionsType was not initialized.");
        public NodeId VersionNode => nodeManager?.VersionNode ??
            throw new InvalidOperationException("The Version was not initialized.");
        public NodeId WriteProbe => writeProbe.IsNull
            ? throw new InvalidOperationException("The WriteProbe was not initialized.")
            : writeProbe;
        public NodeId DocumentFile => nodeManager?.DocumentFile ??
            throw new InvalidOperationException("The DocumentFile was not initialized.");
        public NodeId DocumentVersionFile => nodeManager?.DocumentVersionFile ??
            throw new InvalidOperationException("The DocumentVersionFile was not initialized.");
        public NodeId GroupNode => nodeManager?.GroupNode ??
            throw new InvalidOperationException("The GroupNode was not initialized.");
        public NodeId LabelsNode => nodeManager?.LabelsNode ??
            throw new InvalidOperationException("The LabelsNode was not initialized.");
        public NodeId MetaLabelsNode => nodeManager?.MetaLabelsNode ??
            throw new InvalidOperationException("The MetaLabelsNode was not initialized.");
        public int DeleteInvocations => nodeManager?.DeleteInvocations ?? 0;
        public uint? DeleteExpectedEpoch => nodeManager?.DeleteExpectedEpoch;
        public int AttributeMethodInvocations => nodeManager?.AttributeMethodInvocations ?? 0;
        public bool BlockSubscriptionDeletion { get; set; }
        public StatusCode? NextSubscriptionDeletionFailure { get; set; }
        public TaskCompletionSource SubscriptionDeletionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueSubscriptionDeletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SubscriptionDeletionRequests { get; private set; }

        protected override async ValueTask OnRequestValidatedAsync(OperationContext context)
        {
            await base.OnRequestValidatedAsync(context);
            if (context.RequestType == RequestType.DeleteSubscriptions)
            {
                SubscriptionDeletionRequests++;
                if (NextSubscriptionDeletionFailure is { } failure)
                {
                    NextSubscriptionDeletionFailure = null;
                    throw new ServiceResultException(failure);
                }
                if (BlockSubscriptionDeletion)
                {
                    SubscriptionDeletionEntered.TrySetResult();
                    await ContinueSubscriptionDeletion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }

        public void ReportRegistryCreatedEvent(DateTimeOffset time) =>
            (nodeManager ?? throw new InvalidOperationException("The node manager was not initialized."))
                .ReportRegistryCreatedEvent(time);

        protected override IMasterNodeManager CreateMasterNodeManager(
            IServerInternal server,
            ApplicationConfiguration configuration)
        {
            if (extraNamespace)
            {
                server.NamespaceUris.GetIndexOrAppend("urn:xregistry:integration:namespace-padding");
            }
            nodeManager = new IntegrationNodeManager(
                server, configuration, externalServerApplicationUri, alternateModelLayout);
            registryType = nodeManager.RegistryType;
            writeProbe = nodeManager.WriteProbe;
            return new MasterNodeManager(server, configuration, null, nodeManager);
        }
    }

    private sealed class IntegrationNodeManager : CustomNodeManager2
    {
        private const string SpecVersion = "1.0";
        private const string ModelJson =
            """{"groups":{"teams":{"singular":"team","attributes":{"region":{"type":"string","required":true}},"resources":{"schemas":{"singular":"schema","hasdocument":true},"metadata":{"singular":"metadata","hasdocument":false,"maxversions":1},"versioned":{"singular":"versioned","hasdocument":true,"maxversions":1}}}}}""";
        private const string AlternateModelJson =
            """{"groups":{"departments":{"singular":"department","attributes":{"region":{"type":"string","required":true}},"resources":{"metadata":{"singular":"metadata","hasdocument":false,"maxversions":1},"artifacts":{"singular":"artifact","hasdocument":true,"maxversions":1}}}}}""";
        private readonly IServerInternal server;
        private readonly Dictionary<uint, MemoryStream> openFiles = [];
        private readonly string? externalServerApplicationUri;
        private readonly bool alternateModelLayout;
        private BaseObjectState? registryRoot;
        private readonly Dictionary<string, byte[]> documentContents = new(StringComparer.Ordinal);
        private FileState? createdDocumentResource;
        private uint nextFileHandle;
        public int DeleteInvocations { get; private set; }
        public uint? DeleteExpectedEpoch { get; private set; }
        public int AttributeMethodInvocations { get; private set; }

        public IntegrationNodeManager(
            IServerInternal server,
            ApplicationConfiguration configuration,
            string? externalServerApplicationUri,
            bool alternateModelLayout)
            : base(server, configuration, InProcessRegistryServer.NamespaceUri)
        {
            this.server = server;
            this.externalServerApplicationUri = externalServerApplicationUri;
            this.alternateModelLayout = alternateModelLayout;
        }

        public NodeId RegistryType => new("RegistryType", NamespaceIndex);
        public NodeId GroupType =>
            new(alternateModelLayout ? "MappedTeamGroupType" : "TeamGroupType", NamespaceIndex);
        public NodeId MetadataResourceType =>
            new(alternateModelLayout ? "MappedMetadataResourceType" : "MetadataResourceType", NamespaceIndex);
        public NodeId MetadataResourceNode => new("Registry.MetadataResource", NamespaceIndex);
        public NodeId VersionedResourceType =>
            new(alternateModelLayout ? "MappedVersionedResourceType" : "VersionedResourceType", NamespaceIndex);
        public NodeId ResourceVersionsType =>
            new(alternateModelLayout ? "MappedResourceVersionsType" : "ResourceVersionsType", NamespaceIndex);
        public NodeId VersionedResourceNode => new("Registry.VersionedResource", NamespaceIndex);
        public NodeId VersionNode => new("Registry.VersionedResource.Versions.v1", NamespaceIndex);
        public NodeId WriteProbe => new("Registry.WriteProbe", NamespaceIndex);
        public NodeId DocumentFile => new("Registry.Document", NamespaceIndex);
        public NodeId DocumentVersionFile => new("Registry.Document.Versions.v1", NamespaceIndex);
        public NodeId GroupNode => new("Registry.Group", NamespaceIndex);
        public NodeId LabelsNode => new("Registry.Group.Labels", NamespaceIndex);
        public NodeId MetaLabelsNode => new("Registry.Group.MetaLabels", NamespaceIndex);

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            base.CreateAddressSpace(externalReferences);

            var registryType = new BaseObjectTypeState()
            {
                NodeId = RegistryType,
                BrowseName = new QualifiedName("RegistryType", NamespaceIndex),
                DisplayName = new LocalizedText("RegistryType"),
                IsAbstract = false,
            };
            registryType.AddReference(ReferenceTypeIds.HasSubtype, true, ObjectTypeIds.BaseObjectType);
            var eventType = new BaseObjectTypeState()
            {
                NodeId = new NodeId("RegistryCreatedEventType", NamespaceIndex),
                BrowseName = new QualifiedName("RegistryCreatedEventType", NamespaceIndex),
                DisplayName = new LocalizedText("RegistryCreatedEventType"),
                IsAbstract = false,
            };
            eventType.AddReference(ReferenceTypeIds.HasSubtype, true, ObjectTypeIds.BaseEventType);
            AddEventField(eventType, "SourceUrl", DataTypeIds.String);
            AddEventField(eventType, "Subject", DataTypeIds.String);
            AddEventField(eventType, "Epoch", DataTypeIds.UInt32);
            AddEventField(eventType, "MetaEpoch", DataTypeIds.UInt32);
            AddEventField(eventType, "Changed", DataTypeIds.String, ValueRanks.OneDimension);
            AddEventField(eventType, "CorrelationId", DataTypeIds.String);
            registryType.AddReference(ReferenceTypeIds.GeneratesEvent, false, eventType.NodeId);

            var groupType = new BaseObjectTypeState()
            {
                NodeId = GroupType,
                BrowseName = new QualifiedName(GroupType.IdentifierAsString, NamespaceIndex),
                DisplayName = new LocalizedText(GroupType.IdentifierAsString),
                IsAbstract = false,
            };
            groupType.AddReference(ReferenceTypeIds.HasSubtype, true, ObjectTypeIds.BaseObjectType);

            var metadataResourceType = new BaseObjectTypeState()
            {
                NodeId = MetadataResourceType,
                BrowseName = new QualifiedName(MetadataResourceType.IdentifierAsString, NamespaceIndex),
                DisplayName = new LocalizedText(MetadataResourceType.IdentifierAsString),
                IsAbstract = false,
            };
            metadataResourceType.AddReference(ReferenceTypeIds.HasSubtype, true, ObjectTypeIds.BaseObjectType);
            var versionedResourceType = new BaseObjectTypeState()
            {
                NodeId = VersionedResourceType,
                BrowseName = new QualifiedName(VersionedResourceType.IdentifierAsString, NamespaceIndex),
                DisplayName = new LocalizedText(VersionedResourceType.IdentifierAsString),
                IsAbstract = false,
            };
            versionedResourceType.AddReference(ReferenceTypeIds.HasSubtype, true, ObjectTypeIds.FileType);
            var resourceVersionsType = new BaseObjectTypeState()
            {
                NodeId = ResourceVersionsType,
                BrowseName = new QualifiedName(ResourceVersionsType.IdentifierAsString, NamespaceIndex),
                DisplayName = new LocalizedText(ResourceVersionsType.IdentifierAsString),
                IsAbstract = false,
            };
            resourceVersionsType.AddReference(ReferenceTypeIds.HasSubtype, true, ObjectTypeIds.BaseObjectType);

            var root = new BaseObjectState(null)
            {
                NodeId = new NodeId("Registry", NamespaceIndex),
                BrowseName = new QualifiedName("Registry", NamespaceIndex),
                DisplayName = new LocalizedText("Registry"),
                TypeDefinitionId = registryType.NodeId,
                EventNotifier = EventNotifiers.SubscribeToEvents,
            };
            root.AddReference(ReferenceTypeIds.HasTypeDefinition, false, registryType.NodeId);
            root.AddReference(ReferenceTypeIds.HasNotifier, true, ObjectIds.Server);
            root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            var specVersion = PropertyState<string>.With<VariantBuilder>(root, SpecVersion);
            specVersion.NodeId = new NodeId("Registry.SpecVersion", NamespaceIndex);
            specVersion.BrowseName = new QualifiedName("SpecVersion", NamespaceIndex);
            specVersion.DisplayName = new LocalizedText("SpecVersion");
            specVersion.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            specVersion.TypeDefinitionId = VariableTypeIds.PropertyType;
            specVersion.DataType = DataTypeIds.String;
            specVersion.ValueRank = ValueRanks.Scalar;
            root.AddChild(specVersion);
            AddGroupProperty(root, "RegistryId", "registry-a", DataTypeIds.String);
            AddGroupProperty(root, "Xid", "/", DataTypeIds.String);
            AddGroupProperty(root, "Epoch", 1u, DataTypeIds.UInt32);
            AddGroupProperty(root, "CreatedAt",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DataTypeIds.DateTime);
            AddGroupProperty(root, "ModifiedAt",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DataTypeIds.DateTime);
            AddGroupProperty(root, "Name", "Integration Registry", DataTypeIds.String);
            var writeProbe = PropertyState<string>.With<VariantBuilder>(root, "before");
            writeProbe.NodeId = WriteProbe;
            writeProbe.BrowseName = new QualifiedName("WriteProbe", NamespaceIndex);
            writeProbe.DisplayName = new LocalizedText("WriteProbe");
            writeProbe.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            writeProbe.TypeDefinitionId = VariableTypeIds.PropertyType;
            writeProbe.DataType = DataTypeIds.String;
            writeProbe.ValueRank = ValueRanks.Scalar;
            writeProbe.AccessLevel = AccessLevels.CurrentReadOrWrite;
            writeProbe.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
            root.AddChild(writeProbe);
            AddMethod(root, "Delete",
                [CreateArgument("expectedEpoch", DataTypeIds.UInt32)],
                [],
                (_, _, inputs, _) =>
                {
                    if (inputs.Count != 1 || !inputs[0].TryGetValue(out uint expectedEpoch))
                    {
                        return new ServiceResult(StatusCodes.BadInvalidArgument);
                    }

                    DeleteExpectedEpoch = expectedEpoch;
                    DeleteInvocations++;
                    return ServiceResult.Good;
                });
            AddMethod(root, "CreateGroup",
                [CreateArgument("groupId", DataTypeIds.String)],
                [CreateArgument("groupNode", DataTypeIds.NodeId)],
                (_, _, inputs, outputs) =>
                {
                    if (inputs.Count != 1 || !inputs[0].TryGetValue(out string? groupId) || groupId != "team-a")
                    {
                        return new ServiceResult(StatusCodes.BadInvalidArgument);
                    }

                    outputs[0] = new Variant(GroupNode);
                    return ServiceResult.Good;
                });
            AddMethod(root, "GetOrCreateGroup",
                [CreateArgument("groupId", DataTypeIds.String)],
                [CreateArgument("groupNode", DataTypeIds.NodeId), CreateArgument("created", DataTypeIds.Boolean)],
                (_, _, inputs, outputs) =>
                {
                    if (inputs.Count != 1 || !inputs[0].TryGetValue(out string? groupId) || groupId != "team-a")
                    {
                        return new ServiceResult(StatusCodes.BadInvalidArgument);
                    }

                    outputs[0] = new Variant(GroupNode);
                    outputs[1] = new Variant(true);
                    return ServiceResult.Good;
                });
            var group = new BaseObjectState(root)
            {
                NodeId = GroupNode,
                BrowseName = new QualifiedName("team-a", NamespaceIndex),
                DisplayName = new LocalizedText("team-a"),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                TypeDefinitionId = GroupType,
            };
            group.AddReference(ReferenceTypeIds.HasTypeDefinition, false, GroupType);
            root.AddChild(group);
            AddGroupProperty(group, "GroupId", "team-a", DataTypeIds.String);
            var groupXid = alternateModelLayout ? "/departments/team-a" : "/teams/team-a";
            AddGroupProperty(group, "Xid", groupXid, DataTypeIds.String);
            AddGroupProperty(group, "Epoch", 1u, DataTypeIds.UInt32);
            AddGroupProperty(group, "CreatedAt",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DataTypeIds.DateTime);
            AddGroupProperty(group, "ModifiedAt",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DataTypeIds.DateTime);
            AddGroupProperty(group, "Name", "Team A", DataTypeIds.String);
            AddGroupProperty(group, "Region", "west", DataTypeIds.String);
            var metadataResource = new BaseObjectState(group)
            {
                NodeId = MetadataResourceNode,
                BrowseName = new QualifiedName("metadata-a", NamespaceIndex),
                DisplayName = new LocalizedText("metadata-a"),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                TypeDefinitionId = MetadataResourceType,
            };
            metadataResource.AddReference(ReferenceTypeIds.HasTypeDefinition, false, MetadataResourceType);
            group.AddChild(metadataResource);
            AddGroupProperty(metadataResource, "ResourceId", "metadata-a", DataTypeIds.String);
            AddGroupProperty(metadataResource, "Xid", $"{groupXid}/metadata/metadata-a", DataTypeIds.String);
            AddGroupProperty(metadataResource, "Epoch", 1u, DataTypeIds.UInt32);
            AddGroupProperty(metadataResource, "CreatedAt",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DataTypeIds.DateTime);
            AddGroupProperty(metadataResource, "ModifiedAt",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DataTypeIds.DateTime);
            AddGroupProperty(metadataResource, "Name", "Metadata A", DataTypeIds.String);
            if (!string.IsNullOrEmpty(externalServerApplicationUri))
            {
                var serverIndex = server.ServerUris.GetIndexOrAppend(externalServerApplicationUri);
                var externalNode = new ExpandedNodeId(
                    MetadataResourceNode, InProcessRegistryServer.NamespaceUri, serverIndex);
                AddGroupProperty(metadataResource, "ExternalReference",
                    new Variant(externalNode), DataTypeIds.ExpandedNodeId);
            }
            AddReadOnlyFile(metadataResource,
                new NodeId("Registry.MetadataResource.Metadata", NamespaceIndex), "Metadata", "{}"u8.ToArray());
            var versionedResource = new BaseObjectState(group)
            {
                NodeId = VersionedResourceNode,
                BrowseName = new QualifiedName("versioned-a", NamespaceIndex),
                DisplayName = new LocalizedText("versioned-a"),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                TypeDefinitionId = VersionedResourceType,
            };
            versionedResource.AddReference(ReferenceTypeIds.HasTypeDefinition, false, VersionedResourceType);
            group.AddChild(versionedResource);
            AddResourceCoreProperties(versionedResource, "versioned-a",
                $"{groupXid}/{(alternateModelLayout ? "artifacts" : "versioned")}/versioned-a", "Versioned A");
            var versions = new BaseObjectState(versionedResource)
            {
                NodeId = new NodeId("Registry.VersionedResource.Versions", NamespaceIndex),
                BrowseName = new QualifiedName("Versions", NamespaceIndex),
                DisplayName = new LocalizedText("Versions"),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                TypeDefinitionId = ResourceVersionsType,
            };
            versions.AddReference(ReferenceTypeIds.HasTypeDefinition, false, ResourceVersionsType);
            versionedResource.AddChild(versions);
            var version = AddReadOnlyFile(
                versions, VersionNode, "v1", ExpectedVersionDocument, VersionedResourceType);
            AddResourceCoreProperties(version, "versioned-a",
                $"{groupXid}/{(alternateModelLayout ? "artifacts" : "versioned")}/versioned-a/versions/v1",
                "Versioned A v1", "v1");
            AddGroupProperty(version, "ContentType", "application/octet-stream", DataTypeIds.String);
            if (!string.IsNullOrEmpty(externalServerApplicationUri))
            {
                var serverIndex = server.ServerUris.GetIndexOrAppend(externalServerApplicationUri);
                var externalNode = new ExpandedNodeId(
                    VersionNode, InProcessRegistryServer.NamespaceUri, serverIndex);
                AddGroupProperty(version, "ExternalReference",
                    new Variant(externalNode), DataTypeIds.ExpandedNodeId);
            }
            AddMethod(group, "CreateResource",
                [
                    CreateArgument("resourceId", DataTypeIds.String),
                    CreateArgument("versionId", DataTypeIds.String),
                    CreateArgument("requestFileOpen", DataTypeIds.Boolean),
                ],
                [
                    CreateArgument("resourceNode", DataTypeIds.NodeId),
                    CreateArgument("versionId", DataTypeIds.String),
                    CreateArgument("fileHandle", DataTypeIds.UInt32),
                ],
                (_, _, inputs, outputs) =>
                {
                    if (!IsExpectedResourceCall(inputs))
                    {
                        return new ServiceResult(StatusCodes.BadInvalidArgument);
                    }

                    outputs[0] = new Variant(DocumentVersionFile);
                    outputs[1] = new Variant("v1");
                    outputs[2] = new Variant(inputs[2].TryGetValue(out bool requestOpen) && requestOpen
                        ? OpenDocumentForWrite() : 0u);
                    return ServiceResult.Good;
                });
            AddMethod(group, "GetOrCreateResource",
                [
                    CreateArgument("resourceId", DataTypeIds.String),
                    CreateArgument("versionId", DataTypeIds.String),
                    CreateArgument("requestFileOpen", DataTypeIds.Boolean),
                ],
                [
                    CreateArgument("resourceNode", DataTypeIds.NodeId),
                    CreateArgument("versionId", DataTypeIds.String),
                    CreateArgument("fileHandle", DataTypeIds.UInt32),
                    CreateArgument("created", DataTypeIds.Boolean),
                ],
                (_, _, inputs, outputs) =>
                {
                    if (inputs.Count == 3 &&
                        inputs[0].TryGetValue(out string? resourceId) && resourceId == "new-schema" &&
                        inputs[1].TryGetValue(out string? versionId) && versionId is "" or "v1" &&
                        inputs[2].TryGetValue(out bool requestNewOpen))
                    {
                        var created = createdDocumentResource is null;
                        if (createdDocumentResource is null)
                        {
                            createdDocumentResource = AddDocumentResource(
                                group, new NodeId("Registry.CreatedDocument", NamespaceIndex), resourceId, groupXid);
                            AddPredefinedNode(SystemContext, createdDocumentResource);
                        }
                        outputs[0] = new Variant(
                            new NodeId(createdDocumentResource.NodeId.IdentifierAsString + ".Versions.v1", NamespaceIndex));
                        outputs[1] = new Variant("v1");
                        outputs[2] = new Variant(requestNewOpen ? OpenDocumentForWrite(resourceId) : 0u);
                        outputs[3] = new Variant(created);
                        return ServiceResult.Good;
                    }
                    if (!IsExpectedResourceCall(inputs))
                    {
                        return new ServiceResult(StatusCodes.BadInvalidArgument);
                    }

                    outputs[0] = new Variant(DocumentVersionFile);
                    outputs[1] = new Variant("v1");
                    outputs[2] = new Variant(inputs[2].TryGetValue(out bool requestOpen) && requestOpen
                        ? OpenDocumentForWrite() : 0u);
                    outputs[3] = new Variant(false);
                    return ServiceResult.Good;
                });
            var labels = new BaseObjectState(group)
            {
                NodeId = LabelsNode,
                BrowseName = new QualifiedName("Labels", NamespaceIndex),
                DisplayName = new LocalizedText("Labels"),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
            };
            group.AddChild(labels);
            AddAttributeMethods(labels);
            var metaLabels = new BaseObjectState(group)
            {
                NodeId = MetaLabelsNode,
                BrowseName = new QualifiedName("MetaLabels", NamespaceIndex),
                DisplayName = new LocalizedText("MetaLabels"),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
            };
            group.AddChild(metaLabels);
            AddAttributeMethods(metaLabels);
            AddReadOnlyFile(root, new NodeId("Registry.Model", NamespaceIndex), "Model",
                System.Text.Encoding.UTF8.GetBytes(alternateModelLayout ? AlternateModelJson : ModelJson));
            AddReadOnlyFile(root, new NodeId("Registry.Capabilities", NamespaceIndex), "Capabilities",
                """{"flags":["doc"]}"""u8.ToArray());
            _ = AddDocumentResource(group, DocumentFile, "schema-a", groupXid);

            AddPredefinedNode(SystemContext, registryType);
            AddPredefinedNode(SystemContext, groupType);
            AddPredefinedNode(SystemContext, metadataResourceType);
            AddPredefinedNode(SystemContext, versionedResourceType);
            AddPredefinedNode(SystemContext, resourceVersionsType);
            AddPredefinedNode(SystemContext, eventType);
            AddPredefinedNode(SystemContext, root);
            registryRoot = root;
            AddRootNotifier(root);
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
            {
                externalReferences[ObjectIds.ObjectsFolder] = references = [];
            }
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));
            AddReverseReferences(externalReferences);
        }

        public void ReportRegistryCreatedEvent(DateTimeOffset time)
        {
            var root = registryRoot ?? throw new InvalidOperationException("The Registry root was not initialized.");
            var notification = new BaseEventState(null);
            notification.Initialize(SystemContext, root, EventSeverity.Low, new LocalizedText("Registry created"));
            notification.NodeId = new NodeId("IntegrationEvent", NamespaceIndex);
            if (notification.EventId is not { } eventId || notification.EventType is not { } eventType ||
                notification.SourceNode is not { } sourceNode || notification.SourceName is not { } sourceName ||
                notification.Time is not { } eventTime)
            {
                throw new InvalidOperationException("The SDK did not initialize the native event fields.");
            }
            eventId.Value = new ByteString(new byte[] { 1, 2, 3 });
            eventType.Value = new NodeId("RegistryCreatedEventType", NamespaceIndex);
            sourceNode.Value = root.NodeId;
            sourceName.Value = "Registry";
            eventTime.Value = new DateTimeUtc(time);
            AddGroupProperty(notification, "SourceUrl", InProcessRegistryServer.EventSourceUrl, DataTypeIds.String);
            AddGroupProperty(notification, "Subject", "/", DataTypeIds.String);
            AddGroupProperty(notification, "Epoch", 1u, DataTypeIds.UInt32);
            root.ReportEvent(SystemContext, notification);
        }

        private FileState AddDocumentResource(BaseObjectState group, NodeId node, string resourceId, string groupXid)
        {
            documentContents.Add(resourceId, resourceId == "schema-a" ? InitialWritableDocument.ToArray() : []);
            var document = AddDocumentFile(group, node, resourceId, resourceId);
            var versions = new BaseObjectState(document)
            {
                NodeId = new NodeId(node.IdentifierAsString + ".Versions", NamespaceIndex),
                BrowseName = new QualifiedName("Versions", NamespaceIndex),
                DisplayName = new LocalizedText("Versions"),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                TypeDefinitionId = ResourceVersionsType,
            };
            versions.AddReference(ReferenceTypeIds.HasTypeDefinition, false, ResourceVersionsType);
            document.AddChild(versions);
            var exact = AddDocumentFile(versions,
                new NodeId(node.IdentifierAsString + ".Versions.v1", NamespaceIndex), "v1", resourceId);
            AddResourceProperty(exact, "Xid", $"{groupXid}/schemas/{resourceId}/versions/v1");
            return document;
        }

        private FileState AddDocumentFile(BaseObjectState parent, NodeId node, string browseName, string resourceId)
        {
            var file = new FileState(parent)
            {
                NodeId = node,
                BrowseName = new QualifiedName(browseName, NamespaceIndex),
                DisplayName = new LocalizedText(browseName),
                TypeDefinitionId = ObjectTypeIds.FileType,
            };
            AddResourceProperty(file, "ResourceId", resourceId);
            AddResourceProperty(file, "VersionId", "v1");
            AddResourceProperty(file, "Name", "Schema A");
            file.Size = PropertyState<ulong>.With<VariantBuilder>(file, 0UL);
            file.Size.NodeId = new NodeId(node.IdentifierAsString + ".Size", NamespaceIndex);
            file.Size.BrowseName = new QualifiedName(BrowseNames.Size);
            file.Size.DisplayName = new LocalizedText(BrowseNames.Size);
            file.Size.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            file.Size.DataType = DataTypeIds.UInt64;
            file.Size.ValueRank = ValueRanks.Scalar;
            file.Size.AccessLevel = AccessLevels.CurrentRead;
            file.Size.UserAccessLevel = AccessLevels.CurrentRead;
            file.Size.OnSimpleReadValue = (ISystemContext _, NodeState _, ref Variant value) =>
            {
                value = new Variant((ulong)documentContents[resourceId].Length);
                return ServiceResult.Good;
            };
            file.MaxByteStringLength = PropertyState<uint>.With<VariantBuilder>(file, 3u);
            file.MaxByteStringLength.NodeId = new NodeId(node.IdentifierAsString + ".MaxByteStringLength", NamespaceIndex);
            file.MaxByteStringLength.BrowseName = new QualifiedName(BrowseNames.MaxByteStringLength);
            file.MaxByteStringLength.DisplayName = new LocalizedText(BrowseNames.MaxByteStringLength);
            file.MaxByteStringLength.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            file.MaxByteStringLength.DataType = DataTypeIds.UInt32;
            file.MaxByteStringLength.ValueRank = ValueRanks.Scalar;
            file.MaxByteStringLength.AccessLevel = AccessLevels.CurrentRead;
            file.MaxByteStringLength.UserAccessLevel = AccessLevels.CurrentRead;
            file.Open = new OpenMethodState(file)
            {
                Executable = true,
                UserExecutable = true,
                OnCall = (ISystemContext _, MethodState _, NodeId _, byte mode, ref uint handle) =>
                {
                    handle = ++nextFileHandle;
                    var stream = (mode & 2) == 0
                        ? new MemoryStream(documentContents[resourceId].ToArray(), writable: false)
                        : new MemoryStream();
                    if ((mode & 8) != 0)
                    {
                        stream.Position = stream.Length;
                    }
                    openFiles.Add(handle, stream);
                    return ServiceResult.Good;
                },
            };
            file.Open.Create(SystemContext, new NodeId(node.IdentifierAsString + ".Open", NamespaceIndex),
                new QualifiedName(BrowseNames.Open), new LocalizedText(BrowseNames.Open), false);
            file.Open.MethodDeclarationId = MethodIds.FileType_Open;

            file.Write = new WriteMethodState(file)
            {
                Executable = true,
                UserExecutable = true,
                OnCall = (_, _, _, handle, data) =>
                {
                    if (!openFiles.TryGetValue(handle, out var stream) || !stream.CanWrite)
                    {
                        return new ServiceResult(StatusCodes.BadInvalidState);
                    }

                    var bytes = data.ToArray();
                    if (bytes.Length > 3)
                    {
                        return new ServiceResult(StatusCodes.BadEncodingLimitsExceeded);
                    }
                    stream.Write(bytes, 0, bytes.Length);
                    return ServiceResult.Good;
                },
            };
            file.Write.Create(SystemContext, new NodeId(node.IdentifierAsString + ".Write", NamespaceIndex),
                new QualifiedName(BrowseNames.Write), new LocalizedText(BrowseNames.Write), false);
            file.Write.MethodDeclarationId = MethodIds.FileType_Write;

            file.Read = new ReadMethodState(file)
            {
                Executable = true,
                UserExecutable = true,
                OnCall = (ISystemContext _, MethodState _, NodeId _, uint handle, int length,
                    ref ByteString data) =>
                {
                    if (!openFiles.TryGetValue(handle, out var stream) || !stream.CanRead || length <= 0 || length > 3)
                    {
                        return new ServiceResult(StatusCodes.BadInvalidState);
                    }

                    var buffer = new byte[length];
                    var read = stream.Read(buffer, 0, length);
                    data = ByteString.From(buffer.AsSpan(0, read).ToArray());
                    return ServiceResult.Good;
                },
            };
            file.Read.Create(SystemContext, new NodeId(node.IdentifierAsString + ".Read", NamespaceIndex),
                new QualifiedName(BrowseNames.Read), new LocalizedText(BrowseNames.Read), false);
            file.Read.MethodDeclarationId = MethodIds.FileType_Read;

            file.Close = new CloseMethodState(file)
            {
                Executable = true,
                UserExecutable = true,
                OnCall = (_, _, _, handle) =>
                {
                    if (!openFiles.Remove(handle, out var stream))
                    {
                        return new ServiceResult(StatusCodes.BadInvalidState);
                    }

                    if (stream.CanWrite)
                    {
                        documentContents[resourceId] = stream.ToArray();
                    }

                    stream.Dispose();
                    return ServiceResult.Good;
                },
            };
            file.Close.Create(SystemContext, new NodeId(node.IdentifierAsString + ".Close", NamespaceIndex),
                new QualifiedName(BrowseNames.Close), new LocalizedText(BrowseNames.Close), false);
            file.Close.MethodDeclarationId = MethodIds.FileType_Close;

            file.SetPosition = new SetPositionMethodState(file)
            {
                Executable = true,
                UserExecutable = true,
                OnCall = (_, _, _, handle, position) =>
                {
                    if (!openFiles.TryGetValue(handle, out var stream) || position > (ulong)stream.Length)
                    {
                        return new ServiceResult(StatusCodes.BadInvalidState);
                    }

                    stream.Position = (long)position;
                    return ServiceResult.Good;
                },
            };
            file.SetPosition.Create(SystemContext, new NodeId(node.IdentifierAsString + ".SetPosition", NamespaceIndex),
                new QualifiedName(BrowseNames.SetPosition), new LocalizedText(BrowseNames.SetPosition), false);
            file.SetPosition.MethodDeclarationId = MethodIds.FileType_SetPosition;

            parent.AddChild(file);
            return file;
        }

        private void AddResourceProperty(FileState resource, string name, string value)
        {
            var property = PropertyState<string>.With<VariantBuilder>(resource, value);
            property.NodeId = new NodeId($"{resource.NodeId.IdentifierAsString}.{name}", NamespaceIndex);
            property.BrowseName = new QualifiedName(name, NamespaceIndex);
            property.DisplayName = new LocalizedText(name);
            property.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            property.TypeDefinitionId = VariableTypeIds.PropertyType;
            property.DataType = DataTypeIds.String;
            property.ValueRank = ValueRanks.Scalar;
            resource.AddChild(property);
        }

        private void AddResourceCoreProperties(
            BaseObjectState resource,
            string resourceId,
            string xid,
            string name,
            string? versionId = null)
        {
            AddGroupProperty(resource, "ResourceId", resourceId, DataTypeIds.String);
            AddGroupProperty(resource, "Xid", xid, DataTypeIds.String);
            AddGroupProperty(resource, "Epoch", 1u, DataTypeIds.UInt32);
            AddGroupProperty(resource, "CreatedAt",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DataTypeIds.DateTime);
            AddGroupProperty(resource, "ModifiedAt",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DataTypeIds.DateTime);
            AddGroupProperty(resource, "Name", name, DataTypeIds.String);
            if (versionId is not null)
            {
                AddGroupProperty(resource, "VersionId", versionId, DataTypeIds.String);
            }
        }

        private void AddGroupProperty(BaseObjectState group, string name, string value, NodeId dataType) =>
            AddGroupProperty(group, name, new Variant(value), dataType);

        private void AddGroupProperty(BaseObjectState group, string name, uint value, NodeId dataType) =>
            AddGroupProperty(group, name, Variant.From(value), dataType);

        private void AddGroupProperty(BaseObjectState group, string name, DateTime value, NodeId dataType) =>
            AddGroupProperty(group, name, Variant.From(value), dataType);

        private void AddGroupProperty(BaseObjectState group, string name, Variant value, NodeId dataType)
        {
            var property = new BaseDataVariableState(group)
            {
                NodeId = new NodeId($"{group.NodeId.IdentifierAsString}.{name}", NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText(name),
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                TypeDefinitionId = VariableTypeIds.PropertyType,
                DataType = dataType,
                ValueRank = ValueRanks.Scalar,
                Value = value,
            };
            group.AddChild(property);
        }

        private FileState AddReadOnlyFile(
            BaseObjectState parent,
            NodeId nodeId,
            string browseName,
            byte[] contents,
            NodeId? typeDefinitionId = null)
        {
            var file = new FileState(parent)
            {
                NodeId = nodeId,
                BrowseName = new QualifiedName(browseName, NamespaceIndex),
                DisplayName = new LocalizedText(browseName),
                TypeDefinitionId = typeDefinitionId ?? ObjectTypeIds.FileType,
            };
            file.Size = PropertyState<ulong>.With<VariantBuilder>(file, (ulong)contents.Length);
            file.Size.NodeId = new NodeId(nodeId.IdentifierAsString + ".Size", NamespaceIndex);
            file.Size.BrowseName = new QualifiedName(BrowseNames.Size);
            file.Size.DisplayName = new LocalizedText(BrowseNames.Size);
            file.Size.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            file.Size.DataType = DataTypeIds.UInt64;
            file.Size.ValueRank = ValueRanks.Scalar;
            file.Size.AccessLevel = AccessLevels.CurrentRead;
            file.Size.UserAccessLevel = AccessLevels.CurrentRead;
            file.Open = new OpenMethodState(file)
            {
                Executable = true,
                UserExecutable = true,
                OnCall = (ISystemContext _, MethodState _, NodeId _, byte mode, ref uint handle) =>
                {
                    if (mode != 1)
                    {
                        return new ServiceResult(StatusCodes.BadNotSupported);
                    }

                    handle = ++nextFileHandle;
                    openFiles.Add(handle, new MemoryStream(contents.ToArray(), writable: false));
                    return ServiceResult.Good;
                },
            };
            file.Open.Create(SystemContext, new NodeId($"{nodeId.IdentifierAsString}.Open", NamespaceIndex),
                new QualifiedName(BrowseNames.Open), new LocalizedText(BrowseNames.Open), false);
            file.Open.MethodDeclarationId = MethodIds.FileType_Open;
            file.Read = new ReadMethodState(file)
            {
                Executable = true,
                UserExecutable = true,
                OnCall = (ISystemContext _, MethodState _, NodeId _, uint handle, int length,
                    ref ByteString data) =>
                {
                    if (!openFiles.TryGetValue(handle, out var stream) || !stream.CanRead || length <= 0)
                    {
                        return new ServiceResult(StatusCodes.BadInvalidState);
                    }

                    var buffer = new byte[length];
                    var read = stream.Read(buffer, 0, length);
                    data = ByteString.From(buffer.AsSpan(0, read).ToArray());
                    return ServiceResult.Good;
                },
            };
            file.Read.Create(SystemContext, new NodeId($"{nodeId.IdentifierAsString}.Read", NamespaceIndex),
                new QualifiedName(BrowseNames.Read), new LocalizedText(BrowseNames.Read), false);
            file.Read.MethodDeclarationId = MethodIds.FileType_Read;
            file.Close = new CloseMethodState(file)
            {
                Executable = true,
                UserExecutable = true,
                OnCall = (_, _, _, handle) =>
                {
                    if (!openFiles.Remove(handle, out var stream))
                    {
                        return new ServiceResult(StatusCodes.BadInvalidState);
                    }

                    stream.Dispose();
                    return ServiceResult.Good;
                },
            };
            file.Close.Create(SystemContext, new NodeId($"{nodeId.IdentifierAsString}.Close", NamespaceIndex),
                new QualifiedName(BrowseNames.Close), new LocalizedText(BrowseNames.Close), false);
            file.Close.MethodDeclarationId = MethodIds.FileType_Close;
            parent.AddChild(file);
            return file;
        }

        private uint OpenDocumentForWrite(string resourceId = "schema-a")
        {
            uint handle = ++nextFileHandle;
            var stream = new MemoryStream();
            stream.Write(documentContents[resourceId]);
            stream.Position = 0;
            openFiles.Add(handle, stream);
            return handle;
        }

        private void AddAttributeMethods(BaseObjectState target)
        {
            AddMethod(target, "AddAttribute",
                [
                    CreateArgument("key", DataTypeIds.String),
                    CreateArgument("value", DataTypeIds.String),
                    CreateArgument("expectedEpoch", DataTypeIds.UInt32),
                ],
                [],
                (_, _, inputs, _) =>
                {
                    if (!IsExpectedAttributeCall(target, "AddAttribute", inputs))
                    {
                        return new ServiceResult(StatusCodes.BadInvalidArgument);
                    }

                    AttributeMethodInvocations++;
                    return ServiceResult.Good;
                });
            AddMethod(target, "RemoveAttribute",
                [
                    CreateArgument("key", DataTypeIds.String),
                    CreateArgument("expectedEpoch", DataTypeIds.UInt32),
                ],
                [],
                (_, _, inputs, _) =>
                {
                    if (!IsExpectedAttributeCall(target, "RemoveAttribute", inputs))
                    {
                        return new ServiceResult(StatusCodes.BadInvalidArgument);
                    }

                    AttributeMethodInvocations++;
                    return ServiceResult.Good;
                });
        }

        private static bool IsExpectedResourceCall(ArrayOf<Variant> inputs) =>
            inputs.Count == 3 &&
            inputs[0].TryGetValue(out string? resourceId) && resourceId == "schema-a" &&
            inputs[1].TryGetValue(out string? versionId) && versionId is "" or "v1" &&
            inputs[2].TryGetValue(out bool _);

        private bool IsExpectedAttributeCall(BaseObjectState target, string method, ArrayOf<Variant> inputs)
        {
            bool isMeta = target.NodeId == MetaLabelsNode;
            string expectedKey = isMeta ? "region" : "team";
            uint expectedEpoch = method == "AddAttribute" ? (isMeta ? 3u : 1u) : (isMeta ? 4u : 2u);
            if (method == "AddAttribute")
            {
                string expectedValue = isMeta ? "west" : "alpha";
                return inputs.Count == 3 &&
                    inputs[0].TryGetValue(out string? key) && key == expectedKey &&
                    inputs[1].TryGetValue(out string? value) && value == expectedValue &&
                    inputs[2].TryGetValue(out uint epoch) && epoch == expectedEpoch;
            }

            return inputs.Count == 2 &&
                inputs[0].TryGetValue(out string? removeKey) && removeKey == expectedKey &&
                inputs[1].TryGetValue(out uint removeEpoch) && removeEpoch == expectedEpoch;
        }

        private void AddMethod(BaseObjectState target, string name, Argument[] inputs, Argument[] outputs,
            GenericMethodCalledEventHandler onCall)
        {
            var methodId = new NodeId($"{target.NodeId.IdentifierAsString}.{name}", NamespaceIndex);
            var method = new MethodState(target)
            {
                NodeId = methodId,
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText(name),
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                Executable = true,
                UserExecutable = true,
                OnCallMethod = onCall,
            };
            if (inputs.Length > 0)
            {
                method.CreateOrReplaceInputArguments(SystemContext, null).Value = inputs;
            }
            if (outputs.Length > 0)
            {
                method.CreateOrReplaceOutputArguments(SystemContext, null).Value = outputs;
            }
            target.AddChild(method);
        }

        private static Argument CreateArgument(string name, NodeId dataType) =>
            new()
            {
                Name = name,
                DataType = dataType,
                ValueRank = ValueRanks.Scalar,
            };

        private void AddEventField(BaseObjectTypeState eventType, string name, NodeId dataType,
            int valueRank = ValueRanks.Scalar)
        {
            var field = new BaseDataVariableState(eventType);
            if (dataType == DataTypeIds.UInt32)
            {
                field.Value = 0u;
            }
            else if (valueRank == ValueRanks.OneDimension)
            {
                field.Value = new Variant(Array.Empty<string>());
            }
            else
            {
                field.Value = string.Empty;
            }

            field.NodeId = new NodeId($"RegistryCreatedEventType.{name}", NamespaceIndex);
            field.BrowseName = new QualifiedName(name, NamespaceIndex);
            field.DisplayName = new LocalizedText(name);
            field.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            field.TypeDefinitionId = VariableTypeIds.PropertyType;
            field.DataType = dataType;
            field.ValueRank = valueRank;
            eventType.AddChild(field);
        }
    }
}
