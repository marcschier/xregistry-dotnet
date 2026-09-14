using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.File;
using XRegistry.Federation;
using XRegistry.Federation.Tests;

namespace XRegistry.File.Tests;

public class FileRegistryXidTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FileMappingComposesEscapedEntityCollectionAndDocumentSelection(bool rewritten)
    {
        var root = FileIntegrationFixture.CreateDirectory();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping");
            var files = rewritten ? MappingUriFixture.Renamed(fixture, true) : MappingUriFixture.Load(fixture);
            foreach (var pair in files)
            {
                var path = Path.Combine(root.FullName, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllBytes(path, pair.Value);
            }
            await using var source = await FileRegistrySource.OpenAsync(FileIntegrationFixture.Uri(root.FullName),
                FileRegistryLayout.DocumentTree, authorizedRoot: FileIntegrationFixture.Uri(root.FullName));
            var target = rewritten ? "/documents/group%3aone/assets/item%40stable/%76ersions/v%3a1" :
                "/%64ocuments/%6dain/assets/CON/%76ersions/a%3ab%40c.";
            var stored = rewritten ? "/%64ocuments/group%3Aone/%61ssets/item%40stable/%76ersions/v%3A1" :
                "/documents/main/assets/CON/versions/a:b@c.";
            var metadata = await source.ReadAsync(new(FederationOperation.Entity, target));
            await Assert.That(metadata.SelectedXid).IsEqualTo(stored);
            await Assert.That(metadata.Metadata.GetProperty("entity").GetProperty("xid").GetString()).IsEqualTo(stored);
            var collection = await source.ReadAsync(new(FederationOperation.Collection, target[..target.LastIndexOf('/')]));
            var id = rewritten ? "v:1" : "a:b@c.";
            await Assert.That(collection.Metadata.GetProperty("entities").GetProperty(id).GetProperty("versionid").GetString()).IsEqualTo(id);
            var document = await source.ReadAsync(new(FederationOperation.Document, target));
            await Assert.That(document.SelectedXid).IsEqualTo(stored);
            await Assert.That(await FileIntegrationFixture.Hex(document.Document!))
                .IsEqualTo(rewritten ? "7B2268656C6C6F223A22776F726C64227D0A" : "0001FF7F0A");
            await Assert.That(document.Context.Binding).IsEqualTo("file");
            await Assert.That(document.Context.Source).IsEqualTo(FileIntegrationFixture.Uri(root.FullName).AbsoluteUri);
            await source.DisposeAsync();
            await Assert.That(await FileIntegrationFixture.Hex(document.Document!))
                .IsEqualTo(rewritten ? "7B2268656C6C6F223A22776F726C64227D0A" : "0001FF7F0A");
        }
        finally { root.Delete(recursive: true); }
    }
}
