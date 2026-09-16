// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using XRegistry.Federation;

namespace XRegistry.Bindings.File;

public sealed partial class FileDocumentTreeReader
{
    internal string NativeRoot => _rootPath;
    internal SafeFileHandle RootHandle => _root;

    internal FileDocumentTreeReader OpenAuthorizedChild(Uri endpoint, string path, string relative, long maxFileBytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (OperatingSystem.IsWindows())
            {
                var child = Open(endpoint, maxFileBytes);
                try
                {
                    VerifyWindowsName(child._root, relative == "." ? _canonicalRootPath! : Path.Combine(_canonicalRootPath!, relative));
                    return child;
                }
                catch
                {
                    child.Dispose();
                    throw;
                }
            }
            var components = relative == "." ? ["."] : relative.Split(Path.DirectorySeparatorChar);
            var current = _root;
            SafeFileHandle? owned = null;
            var transferred = false;
            try
            {
                foreach (var component in components)
                {
                    var next = OpenLinuxAt(current, component, directory: true);
                    owned?.Dispose();
                    owned = next;
                    current = next;
                }
                var reader = new FileDocumentTreeReader(endpoint, path, owned!, [], maxFileBytes);
                transferred = true;
                return reader;
            }
            finally { if (!transferred) { owned?.Dispose(); } }
        }
    }

    internal void VerifyCanonicalRoot()
    {
        if (OperatingSystem.IsWindows()) { VerifyWindowsName(_root, _canonicalRootPath!); }
    }

    internal SafeFileHandle OpenDirectChild(string name, bool directory)
    {
        DocumentTreePath.Validate(name);
        if (name.Contains('/', StringComparison.Ordinal)) { throw FileRegistryLocation.Denied("An anchored child operation requires one component."); }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var handle = OperatingSystem.IsWindows() ? OpenWindows(Path.Combine(_rootPath, name), directory) : OpenLinuxAt(_root, name, directory);
            var transferred = false;
            try
            {
                if (OperatingSystem.IsWindows()) { VerifyWindowsName(handle, Path.Combine(_canonicalRootPath!, name)); }
                transferred = true;
                return handle;
            }

            finally { if (!transferred) { handle.Dispose(); } }
        }
    }

    internal FileDocumentTreeReader OpenInternalDirectory(string name)
    {
        var handle = OpenDirectChild(name, directory: true);
        var path = Path.Combine(_rootPath, name);
        return new(new Uri(path + Path.DirectorySeparatorChar), path, handle, [], _maxFileBytes);
    }

    internal static void VerifyWindowsName(SafeFileHandle handle, string path)
    {
        if (!WindowsNameMatches(handle, path))
        {
            throw FileRegistryLocation.Denied("A short-name or case alias cannot select a different canonical File path.");
        }
    }

    internal static bool WindowsNameMatches(SafeFileHandle handle, string path)
    {
        var actual = CanonicalWindowsName(handle);
        var expected = Path.TrimEndingDirectorySeparator(path);
        return actual.Length == expected.Length && actual.AsSpan(2).SequenceEqual(expected.AsSpan(2)) &&
            actual.AsSpan(0, 2).Equals(expected.AsSpan(0, 2), StringComparison.OrdinalIgnoreCase);
    }

    internal static unsafe string CanonicalWindowsName(SafeFileHandle handle)
    {
        Span<char> buffer = stackalloc char[16_384];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = GetFinalPathName(handle, pointer, (uint)buffer.Length, 0);
        }
        if (length == 0)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "The opened File object's canonical name cannot be verified.",
                innerException: new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        if (length >= buffer.Length) { throw FileRegistryLocation.Limit("The canonical File path exceeds its name budget."); }
        var actual = new string(buffer[..(int)length]);
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) { actual = @"\\" + actual[8..]; }
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) { actual = actual[4..]; }
        return Path.TrimEndingDirectorySeparator(actual);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathName(SafeFileHandle handle, char* path, uint count, uint flags);
}
