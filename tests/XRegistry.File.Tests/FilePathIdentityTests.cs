using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.File;
using XRegistry.Federation;

namespace XRegistry.File.Tests;

public class FilePathIdentityTests
{
    [Test]
    public async Task RootAndIntermediateDirectoryCaseAliasesCannotRedirectReads()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "xregistry-case-" + Guid.NewGuid().ToString("N")));
        try
        {
            var child = Directory.CreateDirectory(Path.Combine(root.FullName, "Canonical"));
            System.IO.File.WriteAllBytes(Path.Combine(child.FullName, "value.bin"), [4]);
            using var reader = FileDocumentTreeReader.Open(new Uri(root.FullName + Path.DirectorySeparatorChar));
            if (OperatingSystem.IsWindows())
            {
                await Assert.That(() => FileDocumentTreeReader.Open(
                    new Uri(Path.Combine(root.FullName, "canonical") + Path.DirectorySeparatorChar)))
                    .Throws<FederationException>();
                await Assert.That(async () =>
                {
                    using var stream = await reader.OpenReadAsync("canonical/value.bin");
                }).Throws<FederationException>();
            }
            else
            {
                using var missing = await reader.OpenReadAsync("canonical/value.bin");
                await Assert.That(missing).IsNull();
            }
            using var exact = await reader.OpenReadAsync("Canonical/value.bin");
            await Assert.That(exact!.ReadByte()).IsEqualTo(4);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task InternalCaseAliasesNeverSelectADifferentCanonicalFileSpelling()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "xregistry-case-" + Guid.NewGuid().ToString("N")));
        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(root.FullName, "MixedCase.bin"), [7, 8, 9]);
            using var reader = FileDocumentTreeReader.Open(new Uri(root.FullName + Path.DirectorySeparatorChar));
            using var correct = await reader.OpenReadAsync("MixedCase.bin");
            await Assert.That(correct!.ReadByte()).IsEqualTo(7);
            if (OperatingSystem.IsWindows())
            {
                var error = await Assert.That(async () =>
                {
                    using var aliased = await reader.OpenReadAsync("mixedcase.bin");
                }).Throws<FederationException>();
                await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
            }
            else
            {
                using var absent = await reader.OpenReadAsync("mixedcase.bin");
                await Assert.That(absent).IsNull();
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
