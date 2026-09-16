// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Federation.Tests;

internal sealed class MemoryTreeReader : IDocumentTreeReader
{
    internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    internal List<string> Reads { get; } = [];
    internal int DisposedStreams { get; private set; }
    public NativeRegistryContext Context { get; set; } = new("memory", "urn:test:tree");
    internal Func<string, Stream?>? Open { get; set; }

    internal static MemoryTreeReader Load(string fixture = "Mapping")
    {
        var tree = new MemoryTreeReader();
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            tree.Files.Add(Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllBytes(path));
        }
        return tree;
    }

    public ValueTask<Stream?> OpenReadAsync(string rootRelativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add(rootRelativePath);
        if (Open is not null)
        {
            return ValueTask.FromResult(Open(rootRelativePath));
        }
        return ValueTask.FromResult<Stream?>(Files.TryGetValue(rootRelativePath, out var bytes)
            ? new OwnedStream(bytes, () => DisposedStreams++) : null);
    }

    private sealed class OwnedStream(byte[] bytes, Action disposed) : MemoryStream(bytes, false)
    {
        private bool released;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !released)
            {
                released = true;
                disposed();
            }
            base.Dispose(disposing);
        }
    }
}
