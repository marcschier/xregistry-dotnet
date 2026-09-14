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
    [Arguments(7)]
    [Arguments(int.MaxValue)]
    public async Task UnknownModelKindIsRejected(int value)
    {
        await Assert.That(() =>
        {
            using var source = BuiltInRegistryModels.OpenSource((RegistryModelKind)value);
        }).Throws<ArgumentOutOfRangeException>();
    }
}
