// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.File;
using XRegistry.Federation;

namespace XRegistry.File.Tests;

public class FileDocumentTreeReaderTests
{
    [Test]
    public async Task ActualFilesystemMappingReturnsPinnedBinaryBytesAndNoImmutableClaim()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mapping");
        using var reader = FileDocumentTreeReader.Open(new Uri(path + Path.DirectorySeparatorChar));
        await using var mapping = await DirectoryMapping.OpenAsync(reader);
        var result = await mapping.ReadAsync(new(FederationOperation.Document,
            "/documents/main/assets/CON/versions/a:b@c."));
        using var stream = result.Document!.OpenRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        await Assert.That(Convert.ToHexString(buffer.ToArray())).IsEqualTo("0001FF7F0A");
        await Assert.That(result.Context.Binding).IsEqualTo("file");
        await Assert.That(result.Context.IsImmutable).IsFalse();
        await Assert.That(result.Context.RootSha256).IsEqualTo("7e4c37ca61b2b875ee055a8fc67e5c90c48b344689823a12dc1fb0a6e33232bf");
    }

    [Test]
    [Arguments("../outside")]
    [Arguments("a/../outside")]
    [Arguments("a/%2e%2e/outside")]
    [Arguments("a/%2foutside")]
    [Arguments("a\\outside")]
    [Arguments("CON")]
    [Arguments("a:b")]
    [Arguments("/absolute")]
    public async Task UnsafeHrefsAreRejectedBeforeFileAccess(string href)
    {
        using var reader = FileDocumentTreeReader.Open(new Uri(AppContext.BaseDirectory));
        await Assert.That(async () => await reader.OpenReadAsync(href)).Throws<FederationException>();
    }

    [Test]
    public async Task MissingFileEmptyFileAndDisposedReaderAreDistinct()
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-reader-");
        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(root.FullName, "empty.bin"), []);
            using var reader = FileDocumentTreeReader.Open(new Uri(root.FullName + Path.DirectorySeparatorChar));
            await Assert.That(await reader.OpenReadAsync("missing.bin")).IsNull();
            using var empty = await reader.OpenReadAsync("empty.bin");
            await Assert.That(empty!.Length).IsEqualTo(0L);
            await Assert.That(empty.ReadByte()).IsEqualTo(-1);
            reader.Dispose();
            await Assert.That(async () => await reader.OpenReadAsync("empty.bin")).Throws<ObjectDisposedException>();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments("file://remote.example/share/registry")]
    [Arguments("https://example.com/registry")]
    [Arguments("file:///tmp/registry?other=value")]
    [Arguments("file:///tmp/registry#fragment")]
    public async Task OnlyLocalAuthorityFileRootsAreAccepted(string uri)
    {
        await Assert.That(() => FileDocumentTreeReader.Open(new Uri(uri))).Throws<FederationException>();
    }

    [Test]
    public async Task OpenedFileSizeBudgetAcceptsBoundaryAndRejectsNextByte()
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-budget-");
        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(root.FullName, "exact.bin"), [1, 2, 3]);
            System.IO.File.WriteAllBytes(Path.Combine(root.FullName, "over.bin"), [1, 2, 3, 4]);
            using var reader = FileDocumentTreeReader.Open(new Uri(root.FullName + Path.DirectorySeparatorChar), 3);
            using var exact = await reader.OpenReadAsync("exact.bin");
            await Assert.That(exact!.Length).IsEqualTo(3L);
            await Assert.That(async () => await reader.OpenReadAsync("over.bin")).Throws<FederationException>();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public async Task DirectoryLinksAreRejectedAtTheRootAndAtEveryTraversalComponent()
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-links-");
        var outside = Directory.CreateTempSubdirectory("xregistry-file-outside-");
        var link = Path.Combine(root.FullName, "alias");
        try
        {
            System.IO.File.WriteAllText(Path.Combine(outside.FullName, "private.txt"), "must-not-be-returned");
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardError = true };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add(
                    "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" +
                    link.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" +
                    outside.FullName.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null");
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create the test junction.");
                await process.WaitForExitAsync();
                await Assert.That(process.ExitCode).IsEqualTo(0);
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside.FullName);
            }

            using var reader = FileDocumentTreeReader.Open(new Uri(root.FullName + Path.DirectorySeparatorChar));
            var exception = await Assert.That(async () => await reader.OpenReadAsync("alias/private.txt"))
                .Throws<FederationException>() ?? throw new InvalidOperationException("Expected link rejection.");
            await Assert.That(exception.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
            await Assert.That(() => FileDocumentTreeReader.Open(new Uri(link + Path.DirectorySeparatorChar)))
                .Throws<FederationException>();
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ActiveReadPreventsOrDetectsFileMutation()
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-mutation-");
        try
        {
            var file = Path.Combine(root.FullName, "value.bin");
            System.IO.File.WriteAllBytes(file, [1, 2, 3]);
            using var reader = FileDocumentTreeReader.Open(new Uri(root.FullName + Path.DirectorySeparatorChar));
            using var stream = await reader.OpenReadAsync("value.bin");
            if (OperatingSystem.IsWindows())
            {
                await Assert.That(() => System.IO.File.WriteAllBytes(file, [9, 9, 9])).Throws<IOException>();
                await Assert.That(stream!.ReadByte()).IsEqualTo(1);
            }
            else
            {
                System.IO.File.WriteAllBytes(file, [9, 9, 9, 9]);
                await Assert.That(() => stream!.Dispose()).Throws<FederationException>();
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments("../")]
    [Arguments("%2e%2e/")]
    [Arguments("./")]
    public async Task OriginalRootDotAliasesAreRejectedBeforeUriNormalizationCanHideThem(string suffix)
    {
        var root = Directory.CreateTempSubdirectory("xregistry-file-root-");
        try
        {
            var uri = new Uri(new Uri(root.FullName + Path.DirectorySeparatorChar).AbsoluteUri + suffix);
            await Assert.That(() => FileDocumentTreeReader.Open(uri)).Throws<FederationException>();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
