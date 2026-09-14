using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Storage.File.Tests;

public class StoreLifecycleTests
{
    [Test]
    public async Task InitializesAndReopensGenerationZero()
    {
        using var directory = new TestDirectory();
        using (var store = LocalFileStore.Initialize(directory.Path))
        using (var snapshot = store.ReadSnapshot())
        {
            await Assert.That(snapshot.Generation).IsEqualTo(0L);
            await Assert.That(snapshot.Records.Count).IsEqualTo(0);
        }

        using var reopened = LocalFileStore.Open(directory.Path);
        using var reopenedSnapshot = reopened.ReadSnapshot();
        await Assert.That(reopenedSnapshot.Generation).IsEqualTo(0L);
        await Assert.That(reopenedSnapshot.Records.Count).IsEqualTo(0);
    }
}
