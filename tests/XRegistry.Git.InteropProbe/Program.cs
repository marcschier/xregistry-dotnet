using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;

if (args.Length is not (4 or 6) || args[0] != "--repository" || args[2] != "--revision" ||
    args.Length == 6 && args[4] != "--trusted-root" ||
    !Uri.TryCreate(args[1], UriKind.Absolute, out var repository) ||
    repository.Scheme != Uri.UriSchemeHttp || repository.Host != "127.0.0.1" ||
    repository.IsDefaultPort || repository.UserInfo.Length != 0 ||
    repository.Query.Length != 0 || repository.Fragment.Length != 0)
{
    Console.Error.WriteLine(
        "Usage: XRegistry.Git.InteropProbe --repository <literal-loopback-http-repository> " +
        "--revision <exact-ref-or-oid> [--trusted-root <independent-sha256>]");
    return 2;
}

if (!IsNativeRuntime())
{
    Console.Error.WriteLine("Managed Git interoperability requires the published NativeAOT executable, not a JIT apphost.");
    return 2;
}

if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
{
    Console.Error.WriteLine("Managed Git interoperability requires native host architecture, not emulation.");
    return 2;
}

var options = new GitFetchOptions
{
    AllowLoopbackHttp = true,
    Timeout = TimeSpan.FromSeconds(20),
    MaxControlBytes = 64 * 1024,
    MaxReferences = 64,
    TrustedRegistryRootSha256 = args.Length == 6 ? args[5] : null,
    ObjectLimits = new GitReadLimits
    {
        MaxEncodedBytes = 4 * 1024 * 1024,
        MaxCompressedObjectBytes = 1024 * 1024,
        MaxObjects = 256,
        MaxObjectBytes = 1024 * 1024,
        MaxTotalDecompressedBytes = 8 * 1024 * 1024,
        MaxTreeEntries = 128,
        MaxTreeDepth = 8,
        MaxTagDepth = 8
    }
};

try
{
    var snapshot = await GitSmartHttpClient.FetchAsync(repository, args[3], options).ConfigureAwait(false);
    WriteEvidence(repository, args[3], snapshot, null);
    return 0;
}
catch (GitDataException exception)
{
    WriteEvidence(repository, args[3], null, exception.Failure.ToString());
    return 3;
}
catch (Exception exception) when (exception is HttpRequestException or IOException or ArgumentException or OperationCanceledException)
{
    Console.Error.WriteLine($"Managed Git probe failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

// Native-publish runtimeconfig can disable dynamic code even under dotnet/CoreCLR.
static bool IsNativeRuntime() =>
    !RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled &&
    JitInfo.GetCompiledMethodCount() == 0;

static void WriteEvidence(Uri repository, string revision, GitSnapshot? snapshot, string? failure)
{
    using var output = new MemoryStream();
    using (var json = new Utf8JsonWriter(output))
    {
        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteBoolean("nativeAot", IsNativeRuntime());
        json.WriteNumber("jitCompiledMethods", JitInfo.GetCompiledMethodCount());
#if NET8_0
        json.WriteString("framework", "net8.0");
#else
        json.WriteString("framework", "net10.0");
#endif
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "unsupported";
        json.WriteString("rid", platform + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        json.WriteString("repository", repository.AbsoluteUri);
        json.WriteString("revision", revision);
        json.WriteString("status", snapshot is null ? "git-error" : "snapshot");
        if (snapshot is null)
        {
            json.WriteString("failure", failure);
        }
        else
        {
            json.WriteStartObject("snapshot");
            json.WriteString("objectFormat", snapshot.CommitId.Algorithm == GitHashAlgorithm.Sha256 ? "sha256" : "sha1");
            json.WriteString("selectedId", snapshot.SelectedId.ToString());
            json.WriteString("commitId", snapshot.CommitId.ToString());
            json.WriteString("treeId", snapshot.RootTreeId.ToString());
            json.WriteStartArray("documents");
            foreach (var path in new[] { "xregistry/registry.json", "xregistry/nested/raw.bin" })
            {
                var blob = snapshot.ReadBlob(path);
                json.WriteStartObject();
                json.WriteString("path", path);
                json.WriteString("objectId", blob.Id.ToString());
                json.WriteString("hex", Convert.ToHexString(blob.Content));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }
    Console.WriteLine(Encoding.UTF8.GetString(output.ToArray()));
}
