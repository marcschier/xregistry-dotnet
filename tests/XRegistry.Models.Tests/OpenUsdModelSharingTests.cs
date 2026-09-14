using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class OpenUsdModelSharingTests
{
    [Test]
    public async Task AssetAndPluginGroupsShareTheSameDeclaredResourceType()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.OpenUsd);
        var asset = model.Groups["usdassetgroups"].Resources["usdassets"];
        var plugin = model.Groups["usdschemaplugingroups"].Resources["usdassets"];

        await Assert.That(ReferenceEquals(asset, plugin)).IsTrue();
        await Assert.That(plugin.HasDocument).IsTrue();
        await Assert.That(plugin.Attributes["assetidentifier"].Required).IsTrue();
        await Assert.That(model.Source.RootElement.GetProperty("groups").GetProperty("usdschemaplugingroups")
            .GetProperty("ximportresources")[0].GetString()).IsEqualTo("/usdassetgroups/usdassets");
    }
}
