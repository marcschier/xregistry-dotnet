// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Storage.File.Tests;

internal sealed class TestDirectory : IDisposable
{
    internal TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xregistry-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
