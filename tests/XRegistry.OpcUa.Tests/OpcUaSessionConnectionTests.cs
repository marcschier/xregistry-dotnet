// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;

namespace XRegistry.OpcUa.Tests;

public class OpcUaSessionConnectionTests
{
    [Test]
    [Arguments("target")]
    [Arguments("configuration")]
    [Arguments("telemetry")]
    [Arguments("userIdentity")]
    [Arguments("isPermitted")]
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
                argument == "isSupportedSpecVersion" ? null! : versionPolicy,
                new CancellationToken(canceled: true)));

        await Assert.That(error!.ParamName).IsEqualTo(argument);
        await Assert.That(endpointPolicyCalls).IsEqualTo(0);
        await Assert.That(versionPolicyCalls).IsEqualTo(0);
    }
}
