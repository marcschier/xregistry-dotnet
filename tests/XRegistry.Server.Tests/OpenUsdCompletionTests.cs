// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;
using static XRegistry.Server.Tests.OpenUsdHostIntegrationTests;

namespace XRegistry.Server.Tests;

public class OpenUsdCompletionTests
{
    [Test]
    public async Task VersionXidsCannotBecomeAuthoritativeAssetIdentifiers()
    {
        var engine = CreateOpenUsd();
        await Seed(engine);
        const string identifier = "/usdassetgroups/g/usdassets/scene.usda/versions/1";
        var id = Models.OpenUsdIdentifiers.CreateSymbolicIdCandidate(identifier, 4096);
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/usdassetgroups/g/usdassets/" + id + "$details",
            Artifact(identifier, "Texture", "Opaque/1.0").ToJsonString()), "invalid_attribute");

        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Assert.That((await Send(engine, RegistryAction.Read, "/usdassetgroups/g")).Metadata!.RootElement
            .GetProperty("usdassetscount").GetInt32()).IsEqualTo(1);
    }
}
