// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class CatalogPriorityValueTests
{
    [Test]
    [Arguments("0.0")]
    [Arguments("-0")]
    [Arguments("1e0")]
    [Arguments("9007199254740993.00")]
    public async Task TrustedCatalogPresetPreservesValidUnsignedPriorityTokens(string priority)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(BuiltInRegistryModels.CompileForServer(RegistryModelKind.Registry).Source.RootElement.GetRawText(), store);
        await Send(engine, RegistryAction.Replace, "/categories/c/registries/r", $$"""
            {"federationprofiles":[{"name":"future","endpoint":"urn:example:registry","priority":{{priority}}}]}
            """);
        var result = await Send(engine, RegistryAction.Read, "/categories/c/registries/r");
        await Assert.That(result.Metadata!.RootElement.GetProperty("federationprofiles")[0].GetProperty("priority").GetRawText())
            .IsEqualTo(priority);
    }
}
