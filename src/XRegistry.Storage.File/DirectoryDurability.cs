using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using IOFile = System.IO.File;

namespace XRegistry.Storage.File;

// Only this module knows the native directory, filesystem and rename contracts.
internal sealed partial class DirectoryDurability : IDisposable
{
    private readonly List<SafeFileHandle> _ancestors = [];
    private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.Ordinal);
    private LinuxFileSystemInfo _rootFileSystem;

    private DirectoryDurability(string root) => Root = root;

    internal string Root { get; }

    internal static DirectoryDurability Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) ||
            (OperatingSystem.IsWindows() && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.AsSpan(2).Contains(':'))))
        {
            throw Unsupported("Storage requires an ordinary absolute local directory path.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw Unsupported("Directory durability is implemented only for Windows NTFS and Linux ext4.");
        }

        if (OperatingSystem.IsWindows())
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.Ordinal))
            {
                throw Unsupported("The Windows writer requires a fixed local NTFS volume; shares are not supported.");
            }
        }

        var result = new DirectoryDurability(root);
        try
        {
            var ancestors = new Stack<string>();
            for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            {
                ancestors.Push(current.FullName);
            }

            while (ancestors.TryPop(out var ancestor))
            {
                EnsureDirectory(ancestor);
                result._ancestors.Add(OpenDirectory(ancestor, writable: false));
            }

            var handle = OpenDirectory(root, writable: true);
            result._directories.Add(root, handle);
            if (OperatingSystem.IsLinux())
            {
                result._rootFileSystem = GetFileSystem(handle);
                if (result._rootFileSystem.Type != 0xEF53)
                {
                    throw Unsupported("The Linux writer currently supports only local ext4; network and overlay filesystems are not qualified.");
                }
            }

            result.Flush(root);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal void RetainChild(string name)
    {
        var path = Path.Combine(Root, name);
        EnsureDirectory(path);
        var handle = OpenDirectory(path, writable: true);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var fileSystem = GetFileSystem(handle);
                if (fileSystem.Type != _rootFileSystem.Type ||
                    fileSystem.FileSystemId1 != _rootFileSystem.FileSystemId1 ||
                    fileSystem.FileSystemId2 != _rootFileSystem.FileSystemId2)
                {
                    throw Unsupported("All store directories must be on the same supported local filesystem.");
                }
            }

            _directories.Add(path, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void Flush(string directory)
    {
        if (!_directories.TryGetValue(directory, out var handle))
        {
            throw new InvalidOperationException("The directory durability handle is not retained.");
        }

        var success = OperatingSystem.IsWindows() ? FlushFileBuffers(handle) != 0 : Fsync(handle) == 0;
        if (!success)
        {
            throw Unsupported("The filesystem did not provide the required directory durability barrier.", Marshal.GetLastPInvokeError());
        }
    }

    internal void FlushParent()
    {
        var parent = Directory.GetParent(Root) ?? throw Unsupported("A filesystem root cannot be used as a store directory.");
        using var handle = OpenDirectory(parent.FullName, writable: true);
        var success = OperatingSystem.IsWindows() ? FlushFileBuffers(handle) != 0 : Fsync(handle) == 0;
        if (!success)
        {
            throw Unsupported("The store parent directory could not be made durable.", Marshal.GetLastPInvokeError());
        }
    }

    internal static void PlaceWithoutReplacement(string source, string destination)
    {
        var success = OperatingSystem.IsWindows()
            ? MoveFileEx(source, destination, 0) != 0
            : RenameAt2(-100, source, -100, destination, 1) == 0;
        if (!success)
        {
            throw new IOException("Immutable same-filesystem file placement failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    internal static void EnsureRegularFile(string path)
    {
        var attributes = IOFile.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new StorageException(StorageFailure.InvalidStore, "A store file is a directory or a reparse/symbolic link.");
        }
    }

    private static void EnsureDirectory(string path)
    {
        var attributes = IOFile.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Unsupported("Store paths must contain only existing directories, without reparse or symbolic links.");
        }
    }

    private static SafeFileHandle OpenDirectory(string path, bool writable)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(path, writable ? 0x40000000u : 0x80000000u, 3, 0, 3, 0x02200000, 0);
        }
        else
        {
            var descriptor = OpenNative(path, 0x10000 | 0x20000 | 0x80000);
            handle = new SafeFileHandle(descriptor, ownsHandle: true);
        }

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Unsupported("A safe local directory handle could not be opened.", error);
        }

        return handle;
    }

    private static LinuxFileSystemInfo GetFileSystem(SafeFileHandle handle)
    {
        if (Fstatfs(handle, out var result) != 0)
        {
            throw Unsupported("The local filesystem could not be identified.", Marshal.GetLastPInvokeError());
        }

        return result;
    }

    private static StorageException Unsupported(string message, int? error = null) =>
        new(StorageFailure.UnsupportedStorage, message, error is null ? null : new Win32Exception(error.Value));

    public void Dispose()
    {
        foreach (var handle in _directories.Values)
        {
            handle.Dispose();
        }

        foreach (var handle in _ancestors)
        {
            handle.Dispose();
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int FlushFileBuffers(SafeFileHandle handle);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int MoveFileEx(string source, string destination, uint flags);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle handle);

    [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static partial int Fstatfs(SafeFileHandle handle, out LinuxFileSystemInfo result);

    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt2(int oldDirectory, string source, int newDirectory, string destination, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxFileSystemInfo
    {
        internal nint Type;
        internal nint BlockSize;
        internal ulong Blocks;
        internal ulong BlocksFree;
        internal ulong BlocksAvailable;
        internal ulong Files;
        internal ulong FilesFree;
        internal int FileSystemId1;
        internal int FileSystemId2;
        internal nint NameLength;
        internal nint FragmentSize;
        internal nint Flags;
        internal nint Spare0;
        internal nint Spare1;
        internal nint Spare2;
        internal nint Spare3;
    }
}
