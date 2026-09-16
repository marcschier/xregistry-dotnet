// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Federation;

namespace XRegistry.Oci.Tests;

internal sealed class MemoryLayout : IDocumentTreeReader
{
    internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    internal List<string> Reads { get; } = [];
    internal int DisposedStreams { get; private set; }
    internal Func<string, CancellationToken, ValueTask>? BeforeRead { get; set; }
    public NativeRegistryContext Context { get; set; } = new("file", "file:///fixtures/oci/");

    internal static MemoryLayout Load()
    {
        var result = new MemoryLayout();
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "layout");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result.Files.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes(file));
        }
        return result;
    }

    public async ValueTask<Stream?> OpenReadAsync(string rootRelativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add(rootRelativePath);
        if (BeforeRead is not null) { await BeforeRead(rootRelativePath, cancellationToken); }
        return Files.TryGetValue(rootRelativePath, out var bytes) ? new ReadStream(bytes, this) : null;
    }

    private sealed class ReadStream(byte[] bytes, MemoryLayout owner) : MemoryStream(bytes, writable: false)
    {
        private bool disposed;
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                owner.DisposedStreams++;
            }
            base.Dispose(disposing);
        }
    }
}
