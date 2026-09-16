// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class CatalogPriorityValueTests
{
    [Test]
    [Arguments("0.0")]
    [Arguments("1e0")]
    [Arguments("-0")]
    [Arguments("-0.00e5")]
    [Arguments("100e-2")]
    [Arguments("9007199254740993.00")]
    public async Task CatalogPriorityUsesTheSameUnsignedValueSemanticsAsItsCoreModel(string priority)
    {
        var metadata = RegistryJson.Parse($$"""
            {"federationprofiles":[{"name":"future","endpoint":"urn:example:registry","priority":{{priority}}}]}
            """);
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Registry);
        var validated = RegistryMetadataValidator.Validate(metadata, model.Groups["categories"].Resources["registries"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });
        await Assert.That(validated.Metadata.RootElement.GetProperty("federationprofiles")[0].GetProperty("priority").GetRawText())
            .IsEqualTo(priority);
        await Assert.That(() => RegistryDomainRules.ValidateCatalogMetadata(validated.Metadata.RootElement)).ThrowsNothing();
    }
}
