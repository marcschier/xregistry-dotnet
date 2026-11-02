// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaSessionConnectionTests
{
    [Test]
    [Arguments("target")]
    [Arguments("configuration")]
    [Arguments("telemetry")]
    [Arguments("userIdentity")]
    [Arguments("isPermitted")]
    [Arguments("isSupportedTransportProfile")]
    [Arguments("isSupportedSpecVersion")]
    public async Task OpenAsyncRejectsNullRequiredArgumentsBeforeEndpointDiscovery(string argument)
    {
        var target = OpcUaFederationTargetTests.Target("""{"registryroot":"i=85"}""");
        var telemetry = DefaultTelemetry.Create(static _ => { });
        var configuration = new ApplicationConfiguration(telemetry);
        var identity = new UserIdentity();
        var endpointPolicyCalls = 0;
        var versionPolicyCalls = 0;
        Func<EndpointDescription, bool> endpointPolicy = _ =>
        {
            endpointPolicyCalls++;
            return true;
        };
        Func<string, bool> versionPolicy = _ =>
        {
            versionPolicyCalls++;
            return true;
        };

        var error = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await OpcUaSessionConnection.OpenAsync(
                argument == "target" ? null! : target,
                argument == "configuration" ? null! : configuration,
                argument == "telemetry" ? null! : telemetry,
                argument == "userIdentity" ? null! : identity,
                new NodeId("RegistryType", 1),
                argument == "isPermitted" ? null! : endpointPolicy,
                argument == "isSupportedTransportProfile" ? null! : _ => true,
                argument == "isSupportedSpecVersion" ? null! : versionPolicy,
                new CancellationToken(canceled: true)));

        await Assert.That(error!.ParamName).IsEqualTo(argument);
        await Assert.That(endpointPolicyCalls).IsEqualTo(0);
        await Assert.That(versionPolicyCalls).IsEqualTo(0);
    }

    [Test]
    public async Task OpenAsyncRejectsUnsupportedRequestedTransportBeforeEndpointDiscovery()
    {
        var target = OpcUaFederationTargetTests.Target(
            """{"registryroot":"i=85","transportprofileuri":"urn:example:unsupported"}""");
        var telemetry = DefaultTelemetry.Create(static _ => { });
        var checkedProfiles = new List<string>();

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaSessionConnection.OpenAsync(target, new ApplicationConfiguration(telemetry),
                telemetry, new UserIdentity(), new NodeId("RegistryType", 1),
                _ => throw new InvalidOperationException("Endpoint policy must not run."),
                profile =>
                {
                    checkedProfiles.Add(profile);
                    return false;
                },
                _ => throw new InvalidOperationException("Core version policy must not run.")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(checkedProfiles).IsEquivalentTo(["urn:example:unsupported"]);
    }

    [Test]
    public async Task OpenAsyncRejectsUnregisteredTransportSchemesBeforeEndpointDiscovery()
    {
        var target = OpcUaFederationTarget.FromAdvertisement(CatalogDescription.Parse(Encoding.UTF8.GetBytes("""
            {"federationprofiles":[{
                "binding":"opcua",
                "endpoint":"xregistry-unregistered://registry.example.test",
                "parameters":{"registryroot":"i=85"}
            }]}
            """)).Select(new(["opcua"])));
        var telemetry = DefaultTelemetry.Create(static _ => { });

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaSessionConnection.OpenAsync(target, new ApplicationConfiguration(telemetry),
                telemetry, new UserIdentity(), new NodeId("RegistryType", 1),
                _ => throw new InvalidOperationException("Endpoint policy must not run."),
                _ => true,
                _ => throw new InvalidOperationException("Core version policy must not run.")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task OpenAsyncHonorsCancellationBeforeTransportChecksAndEndpointDiscovery()
    {
        var target = OpcUaFederationTargetTests.Target(
            """{"registryroot":"i=85","transportprofileuri":"urn:example:transport"}""");
        var telemetry = DefaultTelemetry.Create(static _ => { });
        var token = new CancellationToken(canceled: true);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await OpcUaSessionConnection.OpenAsync(target, new ApplicationConfiguration(telemetry),
                telemetry, new UserIdentity(), new NodeId("RegistryType", 1),
                _ => throw new InvalidOperationException("Endpoint policy must not run."),
                _ => throw new InvalidOperationException("Transport support policy must not run."),
                _ => throw new InvalidOperationException("Core version policy must not run."),
                token));

        await Assert.That(error!.CancellationToken).IsEqualTo(token);
    }
}
