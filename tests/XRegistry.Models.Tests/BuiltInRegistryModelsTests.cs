// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class BuiltInRegistryModelsTests
{
    [Test]
    [Arguments(RegistryModelKind.Core, "")]
    [Arguments(RegistryModelKind.Endpoint, "endpoints")]
    [Arguments(RegistryModelKind.Message, "messagegroups")]
    [Arguments(RegistryModelKind.Schema, "schemagroups")]
    [Arguments(RegistryModelKind.CloudEvents, "schemagroups")]
    [Arguments(RegistryModelKind.Registry, "categories")]
    [Arguments(RegistryModelKind.OpenUsd, "usdassetgroups")]
    [Arguments(RegistryModelKind.Api, "apigroups")]
    [Arguments(RegistryModelKind.Publication, "publications")]
    [Arguments(RegistryModelKind.Wot, "thingmodelgroups")]
    public async Task EveryPackagedModelCompilesWithOnlyExplicitPackagedDependencies(
        RegistryModelKind kind, string expectedGroup)
    {
        var model = BuiltInRegistryModels.Compile(kind);
        await Assert.That(model.Attributes["specversion"].DefaultValue.GetString()).IsEqualTo("1.0-rc4");
        if (expectedGroup.Length == 0)
        {
            await Assert.That(model.Groups.Count).IsEqualTo(0);
        }
        else
        {
            await Assert.That(model.Groups.ContainsKey(expectedGroup)).IsTrue();
            await Assert.That(model.EffectiveModel.RootElement.GetProperty("groups").GetProperty(expectedGroup)
                .GetProperty("plural").GetString()).IsEqualTo(expectedGroup);
        }
    }

    [Test]
    [Arguments(RegistryModelKind.Core, "00f9342b7ef4f65fcf3859632c909dacd3cb7f6d8b8c019e03ea1936cc307e6e")]
    [Arguments(RegistryModelKind.Endpoint, "9533a1720ec2da0b74b8f0bdb9df32057fc59b257aabb46f6aaae6535fc814e7")]
    [Arguments(RegistryModelKind.Message, "0b72ea67304b040f7057ee0a924999a09d0a41605eff47ab196199278bd6e121")]
    [Arguments(RegistryModelKind.Schema, "b2e7efd1a89512c6a0a9731c6e868ce1785ec1923d0348527667d8e191e484f7")]
    [Arguments(RegistryModelKind.CloudEvents, "8000babf16b868ff97144b534b0639a68d408285c9ced823e5d5c3414eff08f7")]
    [Arguments(RegistryModelKind.Registry, "afec5aeb7c2a3d435bdbdf7244f3e97bdb5ddb5c4679b6c418a6f7c643c22210")]
    [Arguments(RegistryModelKind.OpenUsd, "bd2209ff833d3016809eb699330c5ebf798f610925a583796998427eff7a242d")]
    [Arguments(RegistryModelKind.Api, "c3d333b91a8b5b926ba3c41a88c6a55a141697c6f514dc53d820da1608e4250f")]
    [Arguments(RegistryModelKind.Publication, "f287f3c0972e5fda9a8e3c5e8a727ecc43c536f254795cf6a6433a4d69e6d134")]
    [Arguments(RegistryModelKind.Wot, "5a1cd2f60e8b54dd7151d6be25d033701a251780c14468711227b2e69a86d832")]
    public async Task SourceBytesMatchIndependentBaselineOrCorrectionDigest(RegistryModelKind kind, string expected)
    {
        using var source = BuiltInRegistryModels.OpenSource(kind);
        var digest = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();

        await Assert.That(digest).IsEqualTo(expected);
    }

    [Test]
    public async Task CoreModelRetainsRequiredReadonlyVersionContract()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Core);
        var version = source.RootElement.GetProperty("attributes").GetProperty("specversion");

        await Assert.That(version.GetProperty("default").GetString()).IsEqualTo("1.0-rc4");
        await Assert.That(version.GetProperty("readonly").GetBoolean()).IsTrue();
        await Assert.That(version.GetProperty("required").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task CatalogModelRetainsDocumentlessRegistries()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Registry);
        var registries = source.RootElement.GetProperty("groups").GetProperty("categories")
            .GetProperty("resources").GetProperty("registries");

        await Assert.That(registries.GetProperty("hasdocument").GetBoolean()).IsFalse();
        await Assert.That(registries.GetProperty("attributes").GetProperty("federationprofiles")
            .GetProperty("type").GetString()).IsEqualTo("array");
    }

    [Test]
    public async Task ApiRegistryModelDefinesVersionedApiDocuments()
    {
        var apiResource = BuiltInRegistryModels.Compile(RegistryModelKind.Api).Groups["apigroups"].Resources["apis"];

        await Assert.That(apiResource.MaxVersions).IsEqualTo(System.Numerics.BigInteger.Zero);
        await Assert.That(apiResource.SetVersionId).IsTrue();
        await Assert.That(apiResource.ValidateFormat).IsTrue();
        await Assert.That(apiResource.Attributes["format"].Required).IsTrue();
    }

    [Test]
    public async Task PublicationModelDefinesChannelsObjectsAndJsonSnapshots()
    {
        var resources = BuiltInRegistryModels.Compile(RegistryModelKind.Publication).Groups["publications"].Resources;

        await Assert.That(resources["channels"].HasDocument).IsFalse();
        await Assert.That(resources["channels"].MaxVersions).IsEqualTo(System.Numerics.BigInteger.One);
        await Assert.That(resources["objects"].SetVersionId).IsTrue();
        await Assert.That(resources["snapshots"].SetVersionId).IsTrue();
        await Assert.That(resources["snapshots"].Attributes["contenttype"].Required).IsTrue();
    }

    [Test]
    public async Task WotModelDefinesVersionedThingModelsAndThingDescriptions()
    {
        var groups = BuiltInRegistryModels.Compile(RegistryModelKind.Wot).Groups;

        await Assert.That(groups["thingmodelgroups"].Resources["thingmodels"].HasDocument).IsTrue();
        await Assert.That(groups["thingmodelgroups"].Resources["thingmodels"].VersionMode).IsEqualTo("semver");
        await Assert.That(groups["thingmodelgroups"].Resources["thingmodels"].Attributes["versionorigin"].Required).IsTrue();
        await Assert.That(groups["thingdescriptiongroups"].Resources["thingdescriptions"].HasDocument).IsTrue();
        await Assert.That(groups["thingdescriptiongroups"].Resources["thingdescriptions"].SingleVersionRoot).IsTrue();
    }

    [Test]
    public async Task SourceLoadingPreservesUnexpandedCloudEventsIncludes()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.CloudEvents);
        var includes = source.RootElement.GetProperty("groups").GetProperty("$includes");

        await Assert.That(includes.GetArrayLength()).IsEqualTo(3);
        await Assert.That(includes[0].GetString()).IsEqualTo("../endpoint/model.json#/groups");
        await Assert.That(includes[1].GetString()).IsEqualTo("../message/model.json#/groups");
        await Assert.That(includes[2].GetString()).IsEqualTo("../schema/model.json#/groups");
    }

    [Test]
    public async Task LoadedDocumentsHaveIndependentCallerOwnedLifetimes()
    {
        using var first = BuiltInRegistryModels.LoadSource(RegistryModelKind.OpenUsd);
        using var second = BuiltInRegistryModels.LoadSource(RegistryModelKind.OpenUsd);
        first.Dispose();

        await Assert.That(second.RootElement.GetProperty("groups").GetProperty("usdassetgroups")
            .GetProperty("singular").GetString()).IsEqualTo("usdassetgroup");
        await Assert.That(second.RootElement.GetProperty("groups").GetProperty("usdschemaplugingroups")
            .GetProperty("singular").GetString()).IsEqualTo("usdschemaplugingroup");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(10)]
    [Arguments(int.MaxValue)]
    public async Task UnknownModelKindIsRejected(int value)
    {
        await Assert.That(() =>
        {
            using var source = BuiltInRegistryModels.OpenSource((RegistryModelKind)value);
        }).Throws<ArgumentOutOfRangeException>();
    }
}
