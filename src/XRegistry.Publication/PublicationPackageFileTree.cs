// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XRegistry.Publication;

internal sealed partial class PublicationPackageFileTree : IDisposable
{
    private readonly SafeFileHandle _root;
    private readonly string _rootPath;
    private readonly string? _canonicalRoot;
    private readonly List<SafeFileHandle> _windowsAncestors;

    private PublicationPackageFileTree(string rootPath, string? canonicalRoot, SafeFileHandle root,
        List<SafeFileHandle> windowsAncestors)
    {
        _rootPath = rootPath;
        _canonicalRoot = canonicalRoot;
        _root = root;
        _windowsAncestors = windowsAncestors;
    }

    internal static PublicationPackageFileTree Open(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Handle-anchored Publication package reads currently support Windows and Linux.");
        }

        var fullPath = Path.GetFullPath(directory);
        var info = new DirectoryInfo(fullPath);
        if (!info.Exists)
        {
            throw PublicationJson.Error("unavailable", directory,
                "The Publication package directory does not exist.");
        }

        var resolved = info.ResolveLinkTarget(true);
        var rootPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(resolved?.FullName ?? info.FullName));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return OpenWindowsRoot(rootPath);
            }

            return OpenLinuxRoot(rootPath);
        }
        catch (FileNotFoundException)
        {
            throw PublicationJson.Error("unavailable", directory,
                "The Publication package directory disappeared before it could be opened.");
        }
        catch (DirectoryNotFoundException)
        {
            throw PublicationJson.Error("unavailable", directory,
                "The Publication package directory disappeared before it could be opened.");
        }
    }

    internal FileStream OpenRead(string relativePath)
    {
        var parts = relativePath.Split('/');
        if (parts.Length == 0 || parts.Any(static part => part.Length == 0 || part is "." or ".." ||
            part.Contains('\\') || part.Contains('%')))
        {
            throw PublicationJson.Error("invalid_package_path", relativePath,
                "The package path is not a validated portable relative path.");
        }

        var parents = new List<SafeFileHandle>();
        SafeFileHandle? file = null;
        try
        {
            var path = _rootPath;
            var canonicalPath = _canonicalRoot;
            var parent = _root;
            var relative = "";
            for (var index = 0; index < parts.Length; index++)
            {
                var directory = index + 1 < parts.Length;
                relative = relative.Length == 0 ? parts[index] : relative + "/" + parts[index];
                if (OperatingSystem.IsWindows())
                {
                    path = Path.Combine(path, parts[index]);
                    file = OpenWindows(path, directory, relative);
                    canonicalPath = Path.Combine(canonicalPath!, parts[index]);
                    VerifyWindowsName(file, canonicalPath, relative);
                }
                else
                {
                    file = OpenLinuxAt(parent, parts[index], directory, relative);
                }

                if (directory)
                {
                    parents.Add(file);
                    parent = file;
                    file = null;
                }
            }

            var stream = new FileStream(file!, FileAccess.Read, 81920, isAsync: false);
            file = null;
            return stream;
        }
        finally
        {
            file?.Dispose();
            foreach (var parent in parents)
            {
                parent.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _root.Dispose();
        foreach (var ancestor in _windowsAncestors)
        {
            ancestor.Dispose();
        }
    }

    private static PublicationPackageFileTree OpenWindowsRoot(string path)
    {
        var parts = new Stack<string>();
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            parts.Push(directory.FullName);
        }

        var ancestors = new List<SafeFileHandle>();
        SafeFileHandle? root = null;
        try
        {
            string? canonicalParent = null;
            while (parts.TryPop(out var part))
            {
                var handle = OpenWindows(part, directory: true, part);
                ancestors.Add(handle);
                if (canonicalParent is not null)
                {
                    VerifyWindowsName(handle, Path.Combine(canonicalParent, new DirectoryInfo(part).Name), part);
                }

                canonicalParent = CanonicalWindowsName(handle);
            }

            root = ancestors[^1];
            ancestors.RemoveAt(ancestors.Count - 1);
            return new(path, CanonicalWindowsName(root), root, ancestors);
        }
        catch
        {
            root?.Dispose();
            foreach (var ancestor in ancestors)
            {
                ancestor.Dispose();
            }

            throw;
        }
    }

    private static PublicationPackageFileTree OpenLinuxRoot(string path)
    {
        var flags = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 0x10000 | 0x80000,
            Architecture.Arm64 => 0x4000 | 0x80000,
            _ => throw new PlatformNotSupportedException(
                "Handle-anchored Publication package reads support Linux x64 and arm64.")
        };
        var descriptor = OpenNative("/", flags);
        if (descriptor < 0)
        {
            throw Denied(path, "The filesystem root cannot be opened safely.",
                Marshal.GetLastPInvokeError());
        }

        var handle = new SafeFileHandle(descriptor, true);
        try
        {
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenLinuxAt(handle, part, directory: true, path);
                handle.Dispose();
                handle = next;
            }

            return new(path, null, handle, []);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenWindows(string path, bool directory, string relativePath)
    {
        var handle = CreateFile(path, directory ? 0x80u : 0x80000000u,
            directory ? 3u : 1u, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3)
            {
                throw new FileNotFoundException("The package object is absent.");
            }

            throw Denied(relativePath, "The package object cannot be opened safely.", error);
        }

        try
        {
            var attributes = File.GetAttributes(handle);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directory ||
                (attributes & FileAttributes.Device) != 0)
            {
                throw PathEscape(relativePath,
                    "A package path contains a symbolic link or unexpected file type.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static int LinuxOpenFlags(bool directory)
    {
        var flags = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 0x20000 | (directory ? 0x10000 : 0),
            Architecture.Arm64 => 0x8000 | (directory ? 0x4000 : 0),
            _ => throw new PlatformNotSupportedException(
                "Handle-anchored Publication package reads support Linux x64 and arm64.")
        };
        return flags | 0x80000 | 0x800;
    }

    private static SafeFileHandle OpenLinuxAt(SafeFileHandle parent, string name, bool directory,
        string relativePath)
    {
        var descriptor = OpenAt(parent, name, LinuxOpenFlags(directory));
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2)
            {
                throw new FileNotFoundException("The package object is absent.");
            }

            if (error is 20 or 40)
            {
                throw PathEscape(relativePath, "A package path contains a symbolic link.");
            }

            throw Denied(relativePath, "The package object cannot be opened without following links.", error);
        }

        var handle = new SafeFileHandle(descriptor, true);
        try
        {
            if (Statx(handle, "", 0x1000, 0x7ff, out var info) != 0)
            {
                throw Denied(relativePath, "The opened package object's file type cannot be verified.",
                    Marshal.GetLastPInvokeError());
            }

            if ((info.Mode & 0xf000) != (directory ? 0x4000 : 0x8000))
            {
                throw PathEscape(relativePath, "The package path has an unexpected file type.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void VerifyWindowsName(SafeFileHandle handle, string path, string relativePath)
    {
        if (!string.Equals(CanonicalWindowsName(handle), Path.TrimEndingDirectorySeparator(path),
            StringComparison.OrdinalIgnoreCase))
        {
            throw PathEscape(relativePath, "A package path resolves through an alias.");
        }
    }

    private static unsafe string CanonicalWindowsName(SafeFileHandle handle)
    {
        Span<char> buffer = stackalloc char[16_384];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = GetFinalPathName(handle, pointer, (uint)buffer.Length, 0);
        }

        if (length == 0)
        {
            throw Denied("", "The opened package object's canonical path cannot be verified.",
                Marshal.GetLastPInvokeError());
        }

        if (length >= buffer.Length)
        {
            throw PublicationJson.Error("path_limit", "",
                "The canonical package path exceeds its name budget.");
        }

        var actual = new string(buffer[..(int)length]);
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            actual = @"\\" + actual[8..];
        }
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            actual = actual[4..];
        }

        return Path.TrimEndingDirectorySeparator(actual);
    }

    private static PublicationException PathEscape(string path, string message) =>
        PublicationJson.Error("path_escape", path, message);

    private static PublicationException Denied(string path, string message, int error) =>
        PublicationJson.Error("path_access_denied", path,
            $"{message} (OS error {error}: {new Win32Exception(error).Message})");

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxInfo
    {
        [FieldOffset(28)] internal ushort Mode;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share,
        nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathName(SafeFileHandle handle, char* path, uint count, uint flags);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(SafeFileHandle directory, string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(SafeFileHandle descriptor, string path, int flags, uint mask,
        out StatxInfo information);
}
