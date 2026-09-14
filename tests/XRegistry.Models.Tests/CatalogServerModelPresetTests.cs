using System.Security.Cryptography;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class CatalogServerModelPresetTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerPresetsAnnotateOnlyTheExplicitCatalogResource(bool all)
    {
        var preset = all ? BuiltInRegistryModels.CompileAllForServer() :
            BuiltInRegistryModels.CompileForServer(RegistryModelKind.Registry);
        var ordinary = all ? BuiltInRegistryModels.CompileAll() :
            BuiltInRegistryModels.Compile(RegistryModelKind.Registry);
        await Assert.That(preset.Groups["categories"].Resources["registries"].Annotations.ModelCompatibleWith)
            .IsEqualTo("https://xregistry.io/xreg/domains/registry/specs/model.json");
        await Assert.That(preset.Groups["categories"].Annotations.ModelCompatibleWith).IsNull();
        await Assert.That(preset.Groups["categories"].Resources["registries"].HasDocument).IsFalse();
        await Assert.That(preset.Groups.Keys).IsEquivalentTo(ordinary.Groups.Keys, StringComparer.Ordinal);
        foreach (var group in ordinary.Groups.Values.Where(static group => group.Plural != "categories"))
        {
            await Assert.That(preset.Groups[group.Plural].Annotations.ModelCompatibleWith).IsEqualTo(group.Annotations.ModelCompatibleWith);
            foreach (var resource in group.Resources.Values)
            {
                await Assert.That(preset.Groups[group.Plural].Resources[resource.Plural].Annotations.ModelCompatibleWith)
                    .IsEqualTo(resource.Annotations.ModelCompatibleWith);
            }
        }
    }

    [Test]
    public async Task CompositeServerPresetPreservesUnexpandedIncludesAndSharedResourceIdentity()
    {
        var preset = BuiltInRegistryModels.CompileAllForServer();
        var source = preset.Source.RootElement;
        await Assert.That(source.GetProperty("$include").GetString()).IsEqualTo("https://xregistry.invalid/spec/core/model.json");
        var groups = source.GetProperty("groups");
        var includes = groups.GetProperty("$includes");
        await Assert.That(includes.GetArrayLength()).IsEqualTo(3);
        await Assert.That(includes[0].GetString()).IsEqualTo("https://xregistry.invalid/spec/cloudevents/model.json#/groups");
        await Assert.That(includes[1].GetString()).IsEqualTo("https://xregistry.invalid/spec/workingdrafts/models/registry/model.json#/groups");
        await Assert.That(includes[2].GetString()).IsEqualTo("https://xregistry.invalid/spec/workingdrafts/models/openusd/model.json#/groups");
        await Assert.That(groups.TryGetProperty("messagegroups", out _)).IsFalse();
        await Assert.That(groups.TryGetProperty("usdassetgroups", out _)).IsFalse();
        await Assert.That(groups.GetProperty("categories").GetProperty("resources").GetProperty("registries")
            .GetProperty("modelcompatiblewith").GetString()).IsEqualTo("https://xregistry.io/xreg/domains/registry/specs/model.json");
        await Assert.That(ReferenceEquals(preset.Groups["messagegroups"].Resources["messages"],
            preset.Groups["endpoints"].Resources["messages"])).IsTrue();
        await Assert.That(ReferenceEquals(preset.Groups["usdassetgroups"].Resources["usdassets"],
            preset.Groups["usdschemaplugingroups"].Resources["usdassets"])).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerPresetsDoNotMutateOriginalCompilationOrPackagedBytes(bool all)
    {
        using var before = BuiltInRegistryModels.OpenSource(RegistryModelKind.Registry);
        await Assert.That(Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant())
            .IsEqualTo("afec5aeb7c2a3d435bdbdf7244f3e97bdb5ddb5c4679b6c418a6f7c643c22210");
        _ = all ? BuiltInRegistryModels.CompileAllForServer() : BuiltInRegistryModels.CompileForServer(RegistryModelKind.Registry);
        using var after = BuiltInRegistryModels.OpenSource(RegistryModelKind.Registry);
        await Assert.That(Convert.ToHexString(SHA256.HashData(after)).ToLowerInvariant())
            .IsEqualTo("afec5aeb7c2a3d435bdbdf7244f3e97bdb5ddb5c4679b6c418a6f7c643c22210");
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Registry);
        await Assert.That(source.RootElement.GetProperty("groups").GetProperty("categories").GetProperty("resources")
            .GetProperty("registries").TryGetProperty("modelcompatiblewith", out _)).IsFalse();
        var ordinary = all ? BuiltInRegistryModels.CompileAll() : BuiltInRegistryModels.Compile(RegistryModelKind.Registry);
        await Assert.That(ordinary.Groups["categories"].Resources["registries"].Annotations.ModelCompatibleWith).IsNull();
        if (all)
        {
            await Assert.That(ordinary.Source.RootElement.GetProperty("groups").TryGetProperty("categories", out _)).IsFalse();
        }
        else
        {
            await Assert.That(ordinary.Source.RootElement.GetRawText()).IsEqualTo(source.RootElement.GetRawText());
        }
    }

    [Test]
    [Arguments(RegistryModelKind.Core)]
    [Arguments(RegistryModelKind.Endpoint)]
    [Arguments(RegistryModelKind.Message)]
    [Arguments(RegistryModelKind.Schema)]
    [Arguments(RegistryModelKind.CloudEvents)]
    [Arguments(RegistryModelKind.OpenUsd)]
    public async Task OtherServerPresetChoicesRetainOrdinaryCompilation(RegistryModelKind kind)
    {
        var ordinary = BuiltInRegistryModels.Compile(kind);
        var preset = BuiltInRegistryModels.CompileForServer(kind);
        await Assert.That(preset.Source.RootElement.GetRawText()).IsEqualTo(ordinary.Source.RootElement.GetRawText());
        await Assert.That(preset.EffectiveModel.RootElement.GetRawText()).IsEqualTo(ordinary.EffectiveModel.RootElement.GetRawText());
        await Assert.That(preset.Groups.ContainsKey("categories")).IsFalse();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(7)]
    [Arguments(int.MaxValue)]
    public async Task UnknownServerPresetKindsDoNotFallBackToAValidModel(int kind)
    {
        await Assert.That(() => BuiltInRegistryModels.CompileForServer((RegistryModelKind)kind)).Throws<ArgumentOutOfRangeException>();
    }
}
