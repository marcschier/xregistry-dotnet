using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using XRegistry.Federation;
using IOFile = System.IO.File;

namespace XRegistry.Bindings.File;

/// <summary>A handle-anchored File binding reader. It never follows symlinks/reparse points or decodes document hrefs.</summary>
public sealed partial class FileDocumentTreeReader : IDocumentTreeReader, IDisposable
{
    private readonly object _gate = new();
    private readonly SafeFileHandle _root;
    private readonly string _rootPath;
    private readonly string? _canonicalRootPath;
    private readonly List<SafeFileHandle> _windowsAncestors;
    private readonly long _maxFileBytes;
    private bool _disposed;

    private FileDocumentTreeReader(Uri uri, string path, SafeFileHandle root,
        List<SafeFileHandle> ancestors, long maxFileBytes)
    {
        Context = new NativeRegistryContext("file", uri.AbsoluteUri);
        _rootPath = path;
        _canonicalRootPath = OperatingSystem.IsWindows() ? CanonicalWindowsName(root) : null;
        _root = root;
        _windowsAncestors = ancestors;
        _maxFileBytes = maxFileBytes;
    }

    /// <summary>Gets the selected locator. A mutable directory is not advertised as an immutable revision.</summary>
    public NativeRegistryContext Context { get; }

    /// <summary>Opens a local-authority File root, including an already OS-mounted share.</summary>
    /// <param name="root">An absolute file URI without query, fragment, remote authority, or path aliases.</param>
    /// <param name="maxFileBytes">Inclusive per-file size limit checked on the opened handle.</param>
    public static FileDocumentTreeReader Open(Uri root, long maxFileBytes = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFileBytes);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Handle-anchored File readers currently support Windows and Linux.");
        }

        var path = FileRegistryLocation.Parse(root);
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var ancestors = new List<SafeFileHandle>();
        SafeFileHandle? handle = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var parts = new Stack<string>();
                for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                {
                    parts.Push(directory.FullName);
                }

                string? canonicalParent = null;
                while (parts.TryPop(out var part))
                {
                    var ancestor = OpenWindows(part, directory: true);
                    ancestors.Add(ancestor);
                    if (canonicalParent is not null)
                    {
                        VerifyWindowsName(ancestor, Path.Combine(canonicalParent, new DirectoryInfo(part).Name));
                    }
                    canonicalParent = CanonicalWindowsName(ancestor);
                }

                handle = ancestors[^1];
                ancestors.RemoveAt(ancestors.Count - 1);
            }
            else
            {
                handle = OpenLinuxRoot(path);
            }

            return new FileDocumentTreeReader(root, path, handle, ancestors, maxFileBytes);
        }
        catch
        {
            handle?.Dispose();
            foreach (var ancestor in ancestors)
            {
                ancestor.Dispose();
            }

            throw;
        }
    }

    /// <summary>Returns a caller-owned regular-file stream or null for a missing object.</summary>
    /// <remarks>Handle size and timestamps are rechecked on stream disposal; detected mutation is inconsistent_snapshot.</remarks>
    public ValueTask<Stream?> OpenReadAsync(string rootRelativePath, CancellationToken cancellationToken = default)
    {
        DocumentTreePath.Validate(rootRelativePath);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SafeFileHandle? file = null;
            var parents = new List<SafeFileHandle>();
            try
            {
                var parts = rootRelativePath.Split('/');
                if (parts.Length > 128)
                {
                    throw new FederationException(FederationErrorCode.LimitExceeded, "The relative File path is too deep.");
                }

                var parent = _root;
                var path = _rootPath;
                var canonicalPath = _canonicalRootPath;
                for (var index = 0; index < parts.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var directory = index + 1 < parts.Length;
                    if (OperatingSystem.IsWindows())
                    {
                        path = Path.Combine(path, parts[index]);
                        file = OpenWindows(path, directory);
                        canonicalPath = Path.Combine(canonicalPath!, parts[index]);
                        VerifyWindowsName(file, canonicalPath);
                    }
                    else
                    {
                        file = OpenLinuxAt(parent, parts[index], directory);
                    }

                    if (directory)
                    {
                        parents.Add(file);
                        parent = file;
                        file = null;
                    }
                }

                var length = RandomAccess.GetLength(file!);
                if (length > _maxFileBytes)
                {
                    throw new FederationException(FederationErrorCode.LimitExceeded, "The opened file exceeds its size budget.");
                }

                var modified = IOFile.GetLastWriteTimeUtc(file!);
                var stream = new FileStream(file!, FileAccess.Read, 64 * 1024, isAsync: false);
                file = null;
                return ValueTask.FromResult<Stream?>(new StableReadStream(stream, length, modified));
            }
            catch (FileNotFoundException)
            {
                return ValueTask.FromResult<Stream?>(null);
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
    }

    /// <summary>Closes the root and ancestor pins. Streams already returned retain their own handles.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _root.Dispose();
            foreach (var ancestor in _windowsAncestors)
            {
                ancestor.Dispose();
            }
        }
    }

    private static SafeFileHandle OpenWindows(string path, bool directory)
    {
        var handle = CreateFile(path, directory ? 0x80u : 0x80000000u,
            directory ? 3u : 1u, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3)
            {
                throw new FileNotFoundException("The root-relative object is absent.");
            }

            throw new FederationException(FederationErrorCode.PolicyDenied, "The File object cannot be opened safely.",
                innerException: new Win32Exception(error));
        }

        try
        {
            var attributes = IOFile.GetAttributes(handle);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != directory ||
                (attributes & FileAttributes.Device) != 0)
            {
                throw Denied("The File object is a link or has an unexpected file type.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static int LinuxOpenFlags(bool directory)
    {
        // arm64 uses distinct O_DIRECTORY/O_NOFOLLOW bits; O_CLOEXEC is common.
        var flags = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 0x20000 | (directory ? 0x10000 : 0),
            Architecture.Arm64 => 0x8000 | (directory ? 0x4000 : 0),
            _ => throw new PlatformNotSupportedException("Native File reads support Linux x64 and arm64.")
        };
        return flags | 0x80000;
    }

    private static SafeFileHandle OpenLinuxRoot(string path)
    {
        var descriptor = OpenNative("/", LinuxOpenFlags(directory: true));
        if (descriptor < 0)
        {
            throw Denied("The filesystem root cannot be opened safely.");
        }

        var handle = new SafeFileHandle(descriptor, true);
        try
        {
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenLinuxAt(handle, part, true);
                handle.Dispose();
                handle = next;
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenLinuxAt(SafeFileHandle parent, string name, bool directory)
    {
        var descriptor = OpenAt(parent, name, LinuxOpenFlags(directory) | 0x800);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2)
            {
                throw new FileNotFoundException("The root-relative object is absent.");
            }

            throw new FederationException(FederationErrorCode.PolicyDenied, "The relative File object cannot be opened without following links.",
                innerException: new Win32Exception(error));
        }

        var handle = new SafeFileHandle(descriptor, true);
        try
        {
            if (Statx(handle, "", 0x1000, 0x7ff, out var info) != 0 ||
                (info.Mode & 0xf000) != (directory ? 0x4000 : 0x8000))
            {
                throw Denied("The root-relative object is not the required regular file or directory.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FederationException Denied(string message) => new(FederationErrorCode.PolicyDenied, message);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxInfo
    {
        [FieldOffset(28)] internal ushort Mode;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share,
        nint security, uint disposition, uint flags, nint template);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(SafeFileHandle directory, string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(SafeFileHandle descriptor, string path, int flags, uint mask, out StatxInfo information);

    private sealed class StableReadStream(FileStream stream, long length, DateTime modified) : Stream
    {
        private bool _disposed;
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => stream.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            stream.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            stream.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void Flush() => stream.Flush();
        public override void SetLength(long value) => throw new NotSupportedException("File source reads are read-only.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("File source reads are read-only.");

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try
                {
                    if (stream.Length != length || IOFile.GetLastWriteTimeUtc(stream.SafeFileHandle) != modified)
                    {
                        throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The opened File source changed during its read.");
                    }
                }
                finally
                {
                    stream.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
