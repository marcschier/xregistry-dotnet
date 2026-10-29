// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Numerics;
using System.Text;
using System.Text.Json;

namespace XRegistry.Publication;

/// <summary>A portable relative file path for one exact Version reference in a Publication package.</summary>
public sealed record PublicationPackageObject(PublicationVersionReference Reference, string Path);

/// <summary>A parsed byte-preserving Publication package manifest and its local object map.</summary>
public sealed class PublicationPackage
{
    private readonly byte[] _exactBytes;

    private PublicationPackage(byte[] exactBytes, PublicationOpenRecord context,
        IReadOnlyList<PublicationPackageObject> objects)
    {
        _exactBytes = (byte[])exactBytes.Clone();
        Context = context;
        Objects = objects;
    }

    /// <summary>Gets the open context selecting the package's immutable snapshot.</summary>
    public PublicationOpenRecord Context { get; }

    /// <summary>Gets the package's exact Version-to-file mappings.</summary>
    public IReadOnlyList<PublicationPackageObject> Objects { get; }

    /// <summary>Returns a copy of the exact package JSON bytes.</summary>
    public byte[] GetExactBytes() => (byte[])_exactBytes.Clone();

    /// <summary>Creates a schema-checked xregistry-publication-package/1 value.</summary>
    public static PublicationPackage Create(PublicationOpenRecord context,
        IReadOnlyList<PublicationPackageObject> objects)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(objects);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "xregistry-publication-package/1");
            writer.WritePropertyName("context");
            using (var contextDocument = JsonDocument.Parse(context.GetExactBytes()))
            {
                contextDocument.RootElement.WriteTo(writer);
            }

            writer.WriteStartArray("objects");
            foreach (var item in objects)
            {
                ArgumentNullException.ThrowIfNull(item);
                ArgumentNullException.ThrowIfNull(item.Reference);
                ArgumentNullException.ThrowIfNull(item.Path);
                writer.WriteStartObject();
                writer.WritePropertyName("reference");
                WriteReference(writer, item.Reference);
                writer.WriteString("path", item.Path);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Parse(buffer.ToArray());
    }

    /// <summary>Parses and validates the package manifest without reading its local files.</summary>
    /// <exception cref="PublicationException">The package JSON or local object map is invalid.</exception>
    /// <exception cref="RegistryException">The JSON is malformed or exceeds its configured limits.</exception>
    public static PublicationPackage Parse(ReadOnlySpan<byte> exactJson, RegistryJsonLimits? limits = null)
    {
        using var document = RegistryJson.ParseDocument(exactJson, limits);
        var root = document.RootElement;
        PublicationJson.RequireObject(root, "");
        PublicationJson.RequireOnly(root, "", "format", "context", "objects");
        PublicationJson.RequireString(root, "format", "", "xregistry-publication-package/1");
        var contextElement = PublicationJson.RequireProperty(root, "context", "");
        var context = PublicationOpenRecord.Parse(Encoding.UTF8.GetBytes(contextElement.GetRawText()), limits);
        var values = PublicationJson.RequireArray(PublicationJson.RequireProperty(root, "objects", ""), "/objects");
        var objects = new PublicationPackageObject[values.GetArrayLength()];
        var references = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"/objects/{index}";
            PublicationJson.RequireObject(value, path);
            PublicationJson.RequireOnly(value, path, "reference", "path");
            var reference = PublicationByteDescriptor.ParseVersionReference(
                PublicationJson.RequireProperty(value, "reference", path));
            var localPath = PublicationJson.RequireNonEmptyString(value, "path", path);
            ValidatePortablePath(localPath, path + "/path");
            if (!references.Add(PublicationReferenceComparer.GetReferenceIdentityKey(reference)))
            {
                throw PublicationJson.Error("duplicate_reference", path + "/reference",
                    "A package cannot contain equivalent Version references more than once.");
            }

            objects[index++] = new PublicationPackageObject(reference, localPath);
        }

        return new PublicationPackage(exactJson.ToArray(), context, Array.AsReadOnly(objects));
    }

    /// <summary>Reads and opens publication-package.json from a local package directory.</summary>
    public static async ValueTask<PublicationOpenedSnapshot> OpenDirectoryAsync(string directory,
        int maxDocumentBytes = 8 * 1024 * 1024, long maxTotalBytes = 512L * 1024 * 1024,
        int maxObjects = 10_000, int maxPathDepth = 32, int maxPackageBytes = 4 * 1024 * 1024,
        RegistryJsonLimits? jsonLimits = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDocumentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPathDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPackageBytes, 1);
        cancellationToken.ThrowIfCancellationRequested();
        var root = ResolvePackageRoot(directory);
        var packagePath = ResolveObjectPath(root, "publication-package.json");
        var info = new FileInfo(packagePath);
        if (!info.Exists)
        {
            throw PublicationJson.Error("unavailable", "publication-package.json",
                "The package manifest is missing from its local directory.");
        }

        if (info.Length > maxPackageBytes)
        {
            throw PublicationJson.Error("object_limit", "publication-package.json",
                "The package manifest exceeds the configured byte limit.");
        }

        using var packageBytes = new MemoryStream((int)info.Length);
        var buffer = new byte[81920];
        var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (packageBytes.Length > maxPackageBytes - read)
                {
                    throw PublicationJson.Error("object_limit", "publication-package.json",
                        "The package manifest exceeds the configured byte limit.");
                }

                packageBytes.Write(buffer, 0, read);
            }
        }

        var package = Parse(packageBytes.ToArray(), jsonLimits);
        return await package.OpenSnapshotAsync(root, maxDocumentBytes, maxTotalBytes,
            maxObjects, maxPathDepth, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the package's verified snapshot from local files only. No configured network or native binding is used.
    /// </summary>
    public async ValueTask<PublicationOpenedSnapshot> OpenSnapshotAsync(string directory,
        int maxDocumentBytes = 8 * 1024 * 1024, long maxTotalBytes = 512L * 1024 * 1024,
        int maxObjects = 10_000, int maxPathDepth = 32, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDocumentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPathDepth, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (Objects.Count > maxObjects)
        {
            throw PublicationJson.Error("object_limit", "/objects",
                "The package object count exceeds the configured limit.");
        }

        foreach (var item in Objects)
        {
            if (item.Path.Split('/').Length > maxPathDepth)
            {
                throw PublicationJson.Error("path_limit", "/objects",
                    "A package object path exceeds the configured traversal depth.");
            }
        }

        var root = ResolvePackageRoot(directory);
        var entries = Objects.ToDictionary(
            static item => PublicationReferenceComparer.GetReferenceIdentityKey(item.Reference),
            StringComparer.Ordinal);
        var pathsByReference = Objects.ToDictionary(
            static item => PublicationReferenceComparer.GetReferenceIdentityKey(item.Reference),
            item => ResolveObjectPath(root, item.Path),
            StringComparer.Ordinal);
        var bytesByReference = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var bytesByPath = new Dictionary<string, byte[]>(PathComparer);
        long totalBytes = 0;

        var manifestBytes = await ReadDescriptorAsync(Context.Snapshot, entries, pathsByReference, bytesByPath,
            maxDocumentBytes, maxTotalBytes, () => totalBytes, value => totalBytes = value, cancellationToken)
            .ConfigureAwait(false);
        var manifest = PublicationManifest.Parse(manifestBytes);
        Context.ValidateAgainstManifest(manifest);

        var descriptors = new Dictionary<string, PublicationByteDescriptor>(StringComparer.Ordinal);
        AddDescriptor(descriptors, Context.Snapshot);
        AddDescriptor(descriptors, Context.Registry);
        AddDescriptor(descriptors, manifest.Registry);
        foreach (var item in manifest.Documents)
        {
            AddDescriptor(descriptors, item.Content);
        }
        foreach (var descriptor in manifest.Extensions.Values)
        {
            AddDescriptor(descriptors, descriptor);
        }

        foreach (var pair in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytesByReference.ContainsKey(pair.Key))
            {
                continue;
            }

            if (!entries.ContainsKey(pair.Key))
            {
                if (pair.Key == PublicationReferenceComparer.GetReferenceIdentityKey(Context.Registry.Reference) ||
                    string.Equals(Context.Completeness, "offline-complete", StringComparison.Ordinal))
                {
                    throw PublicationJson.Error("bundle_object_missing", "",
                        "The package is missing an object required by its completeness claim.");
                }

                continue;
            }

            var bytes = await ReadDescriptorAsync(pair.Value, entries, pathsByReference, bytesByPath,
                maxDocumentBytes, maxTotalBytes, () => totalBytes, value => totalBytes = value, cancellationToken)
                .ConfigureAwait(false);
            bytesByReference.Add(pair.Key, bytes);
        }

        var reader = new PublicationReadClient((access, _) =>
            ValueTask.FromResult<IPublicationReadBinding>(new PackageBinding(access, bytesByReference)),
            maxDocumentBytes);
        return await reader.OpenRecordAsync(Context, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadDescriptorAsync(PublicationByteDescriptor descriptor,
        Dictionary<string, PublicationPackageObject> entries, Dictionary<string, string> pathsByReference,
        Dictionary<string, byte[]> bytesByPath, int maxDocumentBytes, long maxTotalBytes,
        Func<long> getTotalBytes, Action<long> setTotalBytes, CancellationToken cancellationToken)
    {
        if (descriptor.Size < BigInteger.Zero || descriptor.Size > maxDocumentBytes ||
            descriptor.Size > int.MaxValue)
        {
            throw PublicationJson.Error("object_limit", "",
                "A package object exceeds the configured per-object byte limit.");
        }

        var referenceKey = PublicationReferenceComparer.GetReferenceIdentityKey(descriptor.Reference);
        if (!entries.TryGetValue(referenceKey, out var item))
        {
            throw PublicationJson.Error("bundle_object_missing", "",
                "The package does not map a required Version reference to a local file.");
        }

        var path = pathsByReference[referenceKey];
        if (!bytesByPath.TryGetValue(path, out var bytes))
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw PublicationJson.Error("unavailable", item.Path,
                    "A package object is missing from its local directory.");
            }

            if (info.Length > maxDocumentBytes || info.Length > maxTotalBytes - getTotalBytes())
            {
                throw PublicationJson.Error("object_limit", item.Path,
                    "Package bytes exceed the configured total or per-object limit.");
            }

            var expectedSize = (int)descriptor.Size;
            bytes = new byte[expectedSize];
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (stream.ConfigureAwait(false))
                {
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
                        if (count == 0)
                        {
                            throw PublicationJson.Error("size_mismatch", item.Path,
                                "A package object ended before its declared byte size.");
                        }

                        read += count;
                    }

                    var extra = new byte[1];
                    if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                    {
                        throw PublicationJson.Error("size_mismatch", item.Path,
                            "A package object exceeds its declared byte size.");
                    }
                }
            }
            catch (FileNotFoundException)
            {
                throw PublicationJson.Error("unavailable", item.Path,
                    "A package object disappeared before it could be read.");
            }
            catch (DirectoryNotFoundException)
            {
                throw PublicationJson.Error("unavailable", item.Path,
                    "A package object directory disappeared before it could be read.");
            }

            setTotalBytes(checked(getTotalBytes() + bytes.Length));
            bytesByPath.Add(path, bytes);
        }

        PublicationIntegrity.Verify(bytes, descriptor);
        return (byte[])bytes.Clone();
    }

    private static string ResolvePackageRoot(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var info = new DirectoryInfo(fullPath);
        if (!info.Exists)
        {
            throw PublicationJson.Error("unavailable", directory,
                "The Publication package directory does not exist.");
        }

        var resolved = info.ResolveLinkTarget(true);
        return Path.GetFullPath(resolved?.FullName ?? info.FullName);
    }

    private static string ResolveObjectPath(string root, string relativePath)
    {
        var current = root;
        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.GetFullPath(Path.Combine(current, segments[index]));
            FileSystemInfo info = index == segments.Length - 1
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(true)
                    ?? throw PublicationJson.Error("path_escape", relativePath,
                        "A package path contains an unresolved symbolic link.");
                current = Path.GetFullPath(target.FullName);
            }

            EnsureContained(root, current, relativePath);
        }

        return current;
    }

    private static void EnsureContained(string root, string path, string relativePath)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw PublicationJson.Error("path_escape", relativePath,
                "A package path resolves outside its selected directory.");
        }
    }

    private static void AddDescriptor(Dictionary<string, PublicationByteDescriptor> descriptors,
        PublicationByteDescriptor descriptor)
    {
        var key = PublicationReferenceComparer.GetReferenceIdentityKey(descriptor.Reference);
        if (descriptors.TryGetValue(key, out var existing))
        {
            if (!PublicationReferenceComparer.DescriptorsEqual(existing, descriptor))
            {
                throw PublicationJson.Error("descriptor_conflict", "",
                    "One Version reference identifies conflicting package bytes.");
            }

            return;
        }

        descriptors.Add(key, descriptor);
    }

    private static void ValidatePortablePath(string path, string pointer)
    {
        var segments = path.Split('/');
        if (path.StartsWith('/') || path.Contains('\\') || path.Contains('%') ||
            segments.Any(static segment => segment.Length == 0 || segment is "." or "..") ||
            segments.Take(segments.Length - 1).Any(static segment => !IsPortableComponent(segment)) ||
            !IsPortableFileName(segments[^1]))
        {
            throw PublicationJson.Error("invalid_package_path", pointer,
                "Package paths must be relative ASCII paths with portable components and one file extension.");
        }
    }

    private static bool IsPortableFileName(string name)
    {
        var dot = name.IndexOf('.');
        return dot > 0 && dot == name.LastIndexOf('.') && dot < name.Length - 1 &&
            IsPortableComponent(name[..dot]) && IsPortableComponent(name[(dot + 1)..]);
    }

    private static bool IsPortableComponent(string value) =>
        value.Length != 0 && value.All(static character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static void WriteReference(Utf8JsonWriter writer, PublicationVersionReference reference)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("registry");
        WriteRegistry(writer, reference.Registry);
        writer.WriteString("version", reference.Version);
        writer.WriteEndObject();
    }

    private static void WriteRegistry(Utf8JsonWriter writer, PublicationRegistryAccess registry)
    {
        writer.WriteStartObject();
        writer.WriteString("binding", registry.Binding);
        writer.WriteString("endpoint", registry.Endpoint);
        if (registry.Parameters.Count != 0)
        {
            writer.WriteStartObject("parameters");
            foreach (var parameter in registry.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(parameter.Key, parameter.Value);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class PackageBinding(PublicationRegistryAccess access,
        IReadOnlyDictionary<string, byte[]> bytesByReference) : IPublicationReadBinding
    {
        public PublicationRegistryAccess Access { get; } = access;

        public ValueTask<PublicationChannelMetadata> ReadChannelMetadataAsync(string channelResource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<PublicationChannelMetadata>(
                PublicationJson.Error("unsupported_channel_read", "/channel",
                    "A portable package does not expose a mutable channel."));

        public ValueTask<Stream> OpenVersionDocumentAsync(string version,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = PublicationReferenceComparer.GetReferenceIdentityKey(
                new PublicationVersionReference(Access, version));
            if (!bytesByReference.TryGetValue(key, out var bytes))
            {
                throw PublicationJson.Error("unavailable", version,
                    "The local package does not contain this exact Version reference.");
            }

            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
