// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class CompositeModelTests
{
    [Test]
    public async Task AllModelsCompositeIncludesEveryScopedDomainWithoutOpcUa()
    {
        var model = BuiltInRegistryModels.CompileAll();
        await Assert.That(string.Join(',', model.Groups.Keys.Order(StringComparer.Ordinal)))
            .IsEqualTo("categories,endpoints,messagegroups,schemagroups,usdassetgroups,usdschemaplugingroups");
        await Assert.That(model.Groups["categories"].Resources["registries"].HasDocument).IsFalse();
        await Assert.That(model.Groups["schemagroups"].Resources["schemas"].HasDocument).IsTrue();
        await Assert.That(model.Groups["endpoints"].Attributes["usage"].Required).IsTrue();
        await Assert.That(model.Attributes["specversion"].DefaultValue.GetString()).IsEqualTo("1.0-rc4");
    }
}
