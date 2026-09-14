using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XRegistry.Federation;
using IOFile = System.IO.File;

namespace XRegistry.Bindings.File;

internal sealed partial class FileOciLayoutNative : IDisposable
{
    private readonly FileDocumentTreeReader root;
    private readonly List<FileDocumentTreeReader> children = [];
    private readonly Dictionary<FileDocumentTreeReader, SafeFileHandle> barriers = [];
    private readonly FileState rootState;
    private SafeFileHandle? parentBarrier;
    private SafeFileHandle? writerLock;

    internal FileOciLayoutNative(FileDocumentTreeReader root, FederationReadBudget budget)
    {
        this.root = root;
        var parent = Directory.GetParent(root.NativeRoot);
        if (parent is null) { throw FileRegistryLocation.Denied("An OCI layout writer cannot target a filesystem root."); }
        if (OperatingSystem.IsWindows())
        {
            var drive = new DriveInfo(Path.GetPathRoot(root.NativeRoot)!);
            if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.Ordinal))
            {
                throw FileRegistryLocation.Unsupported("Durable File OCI publication requires a fixed local NTFS volume; mounted shares are read-only sources.");
            }
        }
        rootState = State(root.RootHandle, directory: true);
        if (OperatingSystem.IsLinux()) { RequireExt4(root.RootHandle, rootState.MountId, budget); }
        var initialized = false;
        try
        {
            barriers.Add(root, OpenBarrier(root));
            parentBarrier = OperatingSystem.IsWindows()
                ? WindowsOpen(parent.FullName, 0x40000080, 3, 3)
                : LinuxOpen(root.RootHandle, "..", 0x10000 | 0x20000 | 0x80000, 0);
            CheckSameFileSystem(parentBarrier, directory: true);
            Flush(root);
            FlushHandle(parentBarrier);
            writerLock = OpenLock();
            Flush(root);
            initialized = true;
        }
        finally { if (!initialized) { Dispose(); } }
    }

    internal FileDocumentTreeReader Root => root;

    internal FileDocumentTreeReader EnsureDirectory(FileDocumentTreeReader parent, string name)
    {
        var path = Path.Combine(parent.NativeRoot, name);
        if (OperatingSystem.IsWindows())
        {
            if (CreateDirectory(path, 0) == 0 && Marshal.GetLastPInvokeError() != 183) { throw Io("Creating an OCI storage directory failed."); }
        }
        else if (MkdirAt(parent.RootHandle, name, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
        {
            throw Io("Creating an anchored OCI storage directory failed.");
        }
        var reader = parent.OpenInternalDirectory(name);
        var retained = false;
        try
        {
            CheckSameFileSystem(reader.RootHandle, directory: true);
            barriers.Add(reader, OpenBarrier(reader));
            children.Add(reader);
            retained = true;
            Flush(parent);
            Flush(reader);
            return reader;
        }
        finally { if (!retained) { reader.Dispose(); } }
    }

    internal SafeFileHandle? OpenExisting(FileDocumentTreeReader directory, string name)
    {
        SafeFileHandle handle;
        try
        {
            handle = OperatingSystem.IsWindows()
                ? WindowsOpen(Path.Combine(directory.NativeRoot, name), 0xc0000080, 1, 3)
                : LinuxOpen(directory.RootHandle, name, 0x20000 | 0x80000 | 0x800, 0);
        }
        catch (FileNotFoundException) { return null; }
        var transferred = false;
        try
        {
            CheckSameFileSystem(handle, directory: false);
            if (OperatingSystem.IsWindows()) { FileDocumentTreeReader.VerifyWindowsName(handle, Path.Combine(directory.NativeRoot, name)); }
            transferred = true;
            return handle;
        }
        finally { if (!transferred) { handle.Dispose(); } }
    }

    internal SafeFileHandle CreateTemporary(FileDocumentTreeReader directory, string name)
    {
        var handle = OperatingSystem.IsWindows()
            ? WindowsOpen(Path.Combine(directory.NativeRoot, name), 0xc0010080, 1, 1)
            : LinuxOpen(directory.RootHandle, name, 2 | 0x40 | 0x80 | 0x20000 | 0x80000 | 0x800, 0x180);
        var transferred = false;
        try
        {
            CheckSameFileSystem(handle, directory: false);
            transferred = true;
            return handle;
        }
        finally { if (!transferred) { handle.Dispose(); } }
    }

    internal unsafe bool Place(SafeFileHandle file, FileDocumentTreeReader directory, string temporaryName,
        string destinationName, bool replace)
    {
        if (OperatingSystem.IsWindows())
        {
            var rootOffset = IntPtr.Size;
            var lengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = lengthOffset + 4;
            var length = checked(nameOffset + (destinationName.Length + 1) * 2);
            Span<byte> information = stackalloc byte[length];
            information.Clear();
            information[0] = replace ? (byte)1 : (byte)0;
            var retained = false;
            try
            {
                var parent = barriers[directory];
                parent.DangerousAddRef(ref retained);
                fixed (byte* pointer = information)
                {
                    *(nint*)(pointer + rootOffset) = parent.DangerousGetHandle();
                    *(uint*)(pointer + lengthOffset) = (uint)destinationName.Length * 2;
                    destinationName.AsSpan().CopyTo(new Span<char>(pointer + nameOffset, destinationName.Length));
                    // Unlike the Win32 wrapper, NT rename accepts an actual destination directory handle.
                    var status = NtSetInformationFile(file, out _, pointer, (uint)length, 10);
                    if (status < 0)
                    {
                        var error = (int)RtlNtStatusToDosError(status);
                        if (!replace && error is 80 or 183) { return false; }
                        throw Io("Atomic anchored OCI file placement failed.", error);
                    }
                }
            }
            finally { if (retained) { barriers[directory].DangerousRelease(); } }
            FileDocumentTreeReader.VerifyWindowsName(file, Path.Combine(directory.NativeRoot, destinationName));
        }
        else
        {
            var before = State(file, directory: false);
            if (Statx(directory.RootHandle, temporaryName, 0x100, 0x17ff, out var named) != 0 ||
                named.Inode != before.Inode || named.MountId != before.MountId)
            {
                throw Changed("The temporary OCI object's directory entry changed before placement.");
            }
            if (RenameAt2(directory.RootHandle, temporaryName, directory.RootHandle, destinationName, replace ? 0u : 1u) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (!replace && error == 17) { return false; }
                throw Io("Atomic anchored OCI file placement failed.", error);
            }
            if (Statx(directory.RootHandle, destinationName, 0x100, 0x17ff, out named) != 0 ||
                named.Inode != before.Inode || named.MountId != before.MountId)
            {
                throw Changed("The placed OCI object's directory entry changed.");
            }
        }
        return true;
    }

    internal static unsafe void RemoveTemporary(SafeFileHandle file, FileDocumentTreeReader directory, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FileDocumentTreeReader.WindowsNameMatches(file, Path.Combine(directory.NativeRoot, name))) { return; }
            byte delete = 1;
            if (SetFileInformation(file, 4, &delete, 1) == 0) { throw Io("Removing the owned temporary OCI file failed."); }
        }
        else
        {
            if (Statx(directory.RootHandle, name, 0x100, 0x17ff, out var named) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2) { return; }
                throw Io("The temporary OCI name cannot be checked for cleanup.", error);
            }
            var owned = State(file, directory: false);
            if (named.Inode != owned.Inode || named.MountId != owned.MountId)
            {
                throw Changed("The temporary OCI name no longer belongs to this writer; it was not deleted.");
            }
            if (UnlinkAt(directory.RootHandle, name, 0) != 0 && Marshal.GetLastPInvokeError() != 2)
            {
                throw Io("Removing the owned temporary OCI file failed.");
            }
        }
    }

    internal void Flush(FileDocumentTreeReader directory) => FlushHandle(barriers[directory]);
    internal void FlushParent() => FlushHandle(parentBarrier!);
    internal static void FlushFile(SafeFileHandle file) => FlushHandle(file);

    internal FileState VerifyFile(SafeFileHandle file) => CheckSameFileSystem(file, directory: false);

    private FileState CheckSameFileSystem(SafeFileHandle handle, bool directory)
    {
        var state = State(handle, directory);
        if (state.Device != rootState.Device || state.MountId != rootState.MountId)
        {
            throw FileRegistryLocation.Unsupported("Every OCI publication object and directory must remain on the selected local filesystem.");
        }
        return state;
    }

    private SafeFileHandle OpenBarrier(FileDocumentTreeReader reader)
    {
        var handle = OperatingSystem.IsWindows()
            ? WindowsOpen(reader.NativeRoot, 0x40000080, 3, 3)
            : LinuxOpen(reader.RootHandle, ".", 0x10000 | 0x20000 | 0x80000, 0);
        var retained = false;
        try
        {
            CheckSameFileSystem(handle, directory: true);
            if (OperatingSystem.IsWindows()) { FileDocumentTreeReader.VerifyWindowsName(handle, reader.NativeRoot); }
            retained = true;
            return handle;
        }
        finally { if (!retained) { handle.Dispose(); } }
    }

    private SafeFileHandle OpenLock()
    {
        const string name = ".xregistry-oci.lock";
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(Path.Combine(root.NativeRoot, name), 0xc0000080, 0, 0, 4, 0x00200000, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is 32 or 33) { throw Busy(); }
                throw Io("The OCI directory writer lease could not be opened.", error);
            }
        }
        else
        {
            handle = LinuxOpen(root.RootHandle, name, 2 | 0x40 | 0x20000 | 0x80000 | 0x800, 0x180);
            if (Flock(handle, 2 | 4) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is 11 or 35) { throw Busy(); }
                throw Io("The OCI directory writer lease could not be acquired.", error);
            }
        }
        var retained = false;
        try
        {
            CheckSameFileSystem(handle, directory: false);
            if (OperatingSystem.IsWindows()) { FileDocumentTreeReader.VerifyWindowsName(handle, Path.Combine(root.NativeRoot, name)); }
            retained = true;
            return handle;
        }
        finally { if (!retained) { handle.Dispose(); } }
    }

    private static FileState State(SafeFileHandle handle, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetFileInformation(handle, out var info) == 0) { throw Io("The opened OCI storage object's identity cannot be read."); }
            var attributes = (FileAttributes)info.Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directory || !directory && info.Links != 1)
            {
                throw FileRegistryLocation.Denied("OCI publication requires ordinary directories and single-link regular files.");
            }
            return new(((ulong)info.SizeHigh << 32) | info.SizeLow, ((ulong)info.IndexHigh << 32) | info.IndexLow,
                info.Volume, 0, ((long)info.ModifiedHigh << 32) | info.ModifiedLow, 0);
        }
        if (Statx(handle, "", 0x1000, 0x17ff, out var stat) != 0) { throw Io("The anchored OCI storage object's identity cannot be read."); }
        if ((stat.Mask & 0x1346) != 0x1346)
        {
            throw FileRegistryLocation.Unsupported("The kernel does not expose the required file/mount identity evidence.");
        }
        if ((stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || !directory && stat.Links != 1)
        {
            throw FileRegistryLocation.Denied("OCI publication rejects links, devices, sockets and non-regular objects.");
        }
        return new(stat.Size, stat.Inode, ((ulong)stat.DeviceMajor << 32) | stat.DeviceMinor,
            stat.MountId, stat.ModifiedSeconds, stat.ModifiedNanoseconds);
    }

    private static void RequireExt4(SafeFileHandle root, ulong mountId, FederationReadBudget budget)
    {
        if (Fstatfs(root, out var fileSystem) != 0) { throw Io("The publication filesystem cannot be identified."); }
        if (fileSystem.Type != 0xef53)
        {
            throw FileRegistryLocation.Unsupported("Durable Linux OCI publication supports local ext4, not network, overlay or memory filesystems.");
        }
        using var stream = IOFile.OpenRead("/proc/self/mountinfo");
        var buffer = new byte[1_048_577];
        var count = 0;
        while (true)
        {
            var read = stream.Read(buffer.AsSpan(count));
            if (read == 0) { break; }
            count += read;
            budget.ChargeBytes(read);
            if (count > 1_048_576) { throw FileRegistryLocation.Limit("Mount identity metadata exceeds its finite budget."); }
        }
        foreach (var line in Encoding.UTF8.GetString(buffer, 0, count).Split('\n'))
        {
            budget.ChargeWork();
            var space = line.IndexOf(' ');
            if (space < 0 || !ulong.TryParse(line.AsSpan(0, space), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id != mountId) { continue; }
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator >= 0 && line.AsSpan(separator + 3).StartsWith("ext4 ", StringComparison.Ordinal)) { return; }
            throw FileRegistryLocation.Unsupported("The selected mount is not the ext4 driver.");
        }
        throw FileRegistryLocation.Unsupported("The selected ext4 mount identity cannot be confirmed.");
    }

    private static SafeFileHandle WindowsOpen(string path, uint access, uint share, uint disposition)
    {
        var handle = CreateFile(path, access, share, 0, disposition, 0x02200000, 0);
        if (!handle.IsInvalid) { return handle; }
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error is 2 or 3) { throw new FileNotFoundException("The anchored OCI storage object is absent."); }
        throw Io("An OCI storage object could not be opened safely.", error);
    }

    private static SafeFileHandle LinuxOpen(SafeFileHandle directory, string name, int flags, uint mode)
    {
        var descriptor = OpenAt(directory, name, flags, mode);
        if (descriptor >= 0) { return new SafeFileHandle(descriptor, true); }
        var error = Marshal.GetLastPInvokeError();
        if (error == 2) { throw new FileNotFoundException("The anchored OCI storage object is absent."); }
        throw Io("An OCI storage object could not be opened without following links.", error);
    }

    private static void FlushHandle(SafeFileHandle handle)
    {
        var success = OperatingSystem.IsWindows() ? FlushFileBuffers(handle) != 0 : Fsync(handle) == 0;
        if (!success)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The filesystem did not complete a required file or directory durability barrier.",
                "durability_barrier_failed", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static FederationException Busy() =>
        new(FederationErrorCode.Unavailable, "Another writer holds this OCI directory's publication lease.", "writer_busy");

    internal static FederationException Changed(string message) => new(FederationErrorCode.InconsistentSnapshot, message);

    private static FederationException Io(string message, int? errorCode = null)
    {
        var error = errorCode ?? Marshal.GetLastPInvokeError();
        var denied = OperatingSystem.IsWindows() ? error is 5 or 4390 or 4392 : error is 1 or 13 or 20 or 40;
        return new(denied ? FederationErrorCode.PolicyDenied : FederationErrorCode.Unavailable,
            message, innerException: new Win32Exception(error));
    }

    public void Dispose()
    {
        writerLock?.Dispose();
        parentBarrier?.Dispose();
        foreach (var handle in barriers.Values) { handle.Dispose(); }
        for (var index = children.Count - 1; index >= 0; index--) { children[index].Dispose(); }
    }

    internal readonly record struct FileState(ulong Size, ulong Inode, ulong Device, ulong MountId, long Modified, uint Nanoseconds);

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct WindowsInformation
    {
        [FieldOffset(0)] internal uint Attributes;
        [FieldOffset(20)] internal uint ModifiedLow;
        [FieldOffset(24)] internal uint ModifiedHigh;
        [FieldOffset(28)] internal uint Volume;
        [FieldOffset(32)] internal uint SizeHigh;
        [FieldOffset(36)] internal uint SizeLow;
        [FieldOffset(40)] internal uint Links;
        [FieldOffset(44)] internal uint IndexHigh;
        [FieldOffset(48)] internal uint IndexLow;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxInformation
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(112)] internal long ModifiedSeconds;
        [FieldOffset(120)] internal uint ModifiedNanoseconds;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(144)] internal ulong MountId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxFileSystem
    {
        internal nint Type, BlockSize;
        internal ulong Blocks, BlocksFree, BlocksAvailable, Files, FilesFree;
        internal int FileSystemId1, FileSystemId2;
        internal nint NameLength, FragmentSize, Flags, Spare0, Spare1, Spare2, Spare3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint Status;
        internal nuint Information;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int CreateDirectory(string path, nint security);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    private static partial int GetFileInformation(SafeFileHandle handle, out WindowsInformation information);
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    private static unsafe partial int SetFileInformation(SafeFileHandle handle, int informationClass, byte* information, uint size);
    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status,
        byte* information, uint size, int informationClass);
    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int FlushFileBuffers(SafeFileHandle handle);
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(SafeFileHandle parent, string name, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MkdirAt(SafeFileHandle parent, string name, uint mode);
    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt2(SafeFileHandle oldParent, string oldName, SafeFileHandle newParent, string newName, uint flags);
    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(SafeFileHandle parent, string name, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle handle);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(SafeFileHandle handle, int operation);
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(SafeFileHandle descriptor, string path, int flags, uint mask, out LinuxInformation information);
    [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static partial int Fstatfs(SafeFileHandle descriptor, out LinuxFileSystem information);
}
