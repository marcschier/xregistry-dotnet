using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XRegistry.Bindings.File;
using XRegistry.Bindings.Git;
using XRegistry.Bindings.Git.Objects;
using XRegistry.Bindings.Oci;
using XRegistry.Client;
using XRegistry.Federation;
using XRegistry.Server;
using IOFile = System.IO.File;

namespace XRegistry.Samples.Bridge;

public sealed class BridgeConfiguration
{
    private BridgeConfiguration(RegistrySampleHostOptions hosting, BridgeHostOptions options,
        IReadOnlyList<FederationSourceRegistration> sources, IReadOnlyList<BridgeWriteMount> mounts)
    {
        Hosting = hosting;
        Options = options;
        Sources = sources;
        Mounts = mounts;
    }

    public RegistrySampleHostOptions Hosting { get; }
    public BridgeHostOptions Options { get; }
    public IReadOnlyList<FederationSourceRegistration> Sources { get; }
    public IReadOnlyList<BridgeWriteMount> Mounts { get; }

    public static async ValueTask<BridgeConfiguration> LoadAsync(string configurationPath,
        Func<string, string?>? secrets = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        secrets ??= Environment.GetEnvironmentVariable;
        var file = Path.GetFullPath(configurationPath);
        var directory = Path.GetDirectoryName(file)!;
        var config = await JsonFileAsync(file, 256 * 1024, cancellationToken).ConfigureAwait(false);
        Fields(config, "hosting", "registryId", "modelFile", "sources", "mounts", "limits", "externalDocumentOrigins", "paging");
        var host = Required(config, "hosting");
        Fields(host, "publicRoot", "listenPort", "demoLoopback", "allowAnonymousReads", "certificatePath",
            "certificatePasswordEnvironment", "writeTokenEnvironment", "readTokenEnvironment");
        var publicRoot = new Uri(Text(host, "publicRoot"), UriKind.Absolute);
        var hosting = new RegistrySampleHostOptions
        {
            PublicRoot = publicRoot,
            ListenPort = Integer(host, "listenPort", 8443),
            DemoLoopback = Boolean(host, "demoLoopback"),
            AllowAnonymousReads = Boolean(host, "allowAnonymousReads"),
            CertificatePath = Optional(host, "certificatePath") is { } certificate ? Resolve(directory, certificate) : null,
            CertificatePasswordEnvironment = EnvironmentName(host, "certificatePasswordEnvironment"),
            WriteTokenEnvironment = EnvironmentName(host, "writeTokenEnvironment"),
            ReadTokenEnvironment = EnvironmentName(host, "readTokenEnvironment")
        };
        var model = await ModelFileAsync(Resolve(directory, Text(config, "modelFile")), cancellationToken).ConfigureAwait(false);
        var limits = config.TryGetProperty("limits", out var configuredLimits) ? configuredLimits : RegistryJson.Parse("{}").RootElement;
        Fields(limits, "maxConcurrentRequests", "maxBodyBytes", "requestTimeoutSeconds", "maxSourceBytes", "maxSourceRequests");
        var maxBody = Integer(limits, "maxBodyBytes", 2 * 1024 * 1024);
        var seconds = Integer(limits, "requestTimeoutSeconds", 30);
        var readLimits = new FederationReadLimits(
            maxObjectBytes: maxBody, maxTotalBytes: Integer(limits, "maxSourceBytes", 16 * 1024 * 1024),
            maxRequests: Integer(limits, "maxSourceRequests", 256), maxObjects: 1024,
            maxWork: 200_000, maxSources: 32, maxHops: 4, maxDepth: 32, maxResultBytes: maxBody);
        if (readLimits.MaxTotalBytes > 128 * 1024 * 1024 || readLimits.MaxRequests > 2048)
        {
            throw new ArgumentException("Source byte/request limits exceed the bounded sample profile.");
        }
        var sourceSettings = Array(config, "sources", required: true);
        if (sourceSettings.Length is < 1 or > 8) { throw new ArgumentException("One to eight sources are required."); }
        var adminOnly = new Dictionary<string, bool>(StringComparer.Ordinal);
        var sources = new List<FederationSourceRegistration>();
        foreach (var source in sourceSettings)
        {
            Fields(source, "name", "binding", "endpoint", "directory", "authorizedDirectory", "layout", "reference",
                "rootPath", "trustedRegistryRootSha256", "credentialEnvironment", "allowLoopbackHttp",
                "allowPrivateOrigin", "view", "adminOnly");
            var name = Text(source, "name");
            RegistryId.Parse(name);
            if (!adminOnly.TryAdd(name, Boolean(source, "adminOnly"))) { throw new ArgumentException("Duplicate source name."); }
            sources.Add(Source(source, directory, readLimits, TimeSpan.FromSeconds(seconds), secrets));
        }
        var policy = RegistrySampleHosting.CreateAuthorizationPolicy(hosting);
        var paging = config.TryGetProperty("paging", out var pageSettings) ? pageSettings : RegistryJson.Parse("{}").RootElement;
        Fields(paging, "maxPageRecords", "maxCaptureRecords", "maxCaptureBytes", "maxCaptures", "maxTotalBytes", "maxTokens", "lifetimeSeconds");
        var options = new BridgeHostOptions
        {
            Model = model,
            PublicRoot = publicRoot,
            RegistryId = Text(config, "registryId"),
            AllowAnonymousReads = hosting.AllowAnonymousReads,
            MaxConcurrentRequests = Integer(limits, "maxConcurrentRequests", 4),
            MaxRequestBytes = maxBody,
            MaxResponseBytes = maxBody,
            RequestTimeout = TimeSpan.FromSeconds(seconds),
            ReadLimits = readLimits,
            PagingLimits = new BridgePagingLimits
            {
                MaxPageRecords = Integer(paging, "maxPageRecords", 256),
                MaxCaptureRecords = Integer(paging, "maxCaptureRecords", 1024),
                MaxCaptureBytes = Integer(paging, "maxCaptureBytes", 4 * 1024 * 1024),
                MaxCaptures = Integer(paging, "maxCaptures", 16),
                MaxTotalBytes = Integer(paging, "maxTotalBytes", 16 * 1024 * 1024),
                MaxTokens = Integer(paging, "maxTokens", 4096),
                Lifetime = TimeSpan.FromSeconds(Integer(paging, "lifetimeSeconds", 120))
            },
            ExternalDocumentOrigins = Array(config, "externalDocumentOrigins")
                .Select(value => new Uri(value.GetString() ?? throw new ArgumentException("An external origin must be a string."), UriKind.Absolute)).ToArray(),
            AuthorizeSource = (caller, name, token) => policy.AuthorizeAsync(caller,
                adminOnly[name] ? RegistryAccess.Update : RegistryAccess.Read, RegistryPath.Parse("/"), token),
            AuthorizeMount = (caller, _, method, token) => policy.AuthorizeAsync(caller,
                method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options
                    ? RegistryAccess.Read : RegistryAccess.Update, RegistryPath.Parse("/"), token),
            AuthorizeRetainedRead = (caller, source, path, token) => policy.AuthorizeAsync(caller,
                adminOnly[source] ? RegistryAccess.Update : RegistryAccess.Read, path, token)
        };
        options.Validate();
        var mounts = new List<BridgeWriteMount>();
        foreach (var mount in Array(config, "mounts"))
        {
            Fields(mount, "name", "upstreamRoot", "modelFile", "credentialEnvironment", "allowLoopbackHttp", "allowPrivateOrigin");
            var root = new Uri(Text(mount, "upstreamRoot"), UriKind.Absolute);
            var credential = EnvironmentName(mount, "credentialEnvironment");
            var selected = new BridgeWriteMount
            {
                Name = Text(mount, "name"),
                UpstreamRoot = root,
                Model = Optional(mount, "modelFile") is { } modelFile
                    ? await ModelFileAsync(Resolve(directory, modelFile), cancellationToken).ConfigureAwait(false) : model,
                AllowLoopbackHttp = Boolean(mount, "allowLoopbackHttp"),
                AllowPrivateOrigin = Boolean(mount, "allowPrivateOrigin"),
                AuthorizationProvider = credential is null ? null : (uri, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!BridgeApplication.SameOrigin(root, uri)) { throw new InvalidOperationException("Credential scope mismatch."); }
                    return ValueTask.FromResult<AuthenticationHeaderValue?>(Credential(credential, secrets));
                }
            };
            BridgeWriteThrough.Validate(selected);
            mounts.Add(selected);
        }
        if (mounts.Count > 8 || sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count ||
            mounts.Select(mount => mount.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mounts.Count)
        {
            throw new ArgumentException("Configured source/mount names must be unique and bounded.");
        }
        return new(hosting, options, sources.AsReadOnly(), mounts.AsReadOnly());
    }

    private static FederationSourceRegistration Source(JsonElement source, string directory, FederationReadLimits limits,
        TimeSpan timeout, Func<string, string?> secrets)
    {
        var name = Text(source, "name");
        var binding = Text(source, "binding");
        var credentialName = EnvironmentName(source, "credentialEnvironment");
        var allowLoopback = Boolean(source, "allowLoopbackHttp");
        var allowPrivate = Boolean(source, "allowPrivateOrigin");
        Func<CancellationToken, ValueTask<string>> stamp = token =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CredentialStamp(Credential(credentialName, secrets)));
        };
        var view = Optional(source, "view") ?? (binding == "http" ? "api" : "document");
        var representation = view switch
        {
            "api" => FederationRepresentation.ApiView,
            "document" => FederationRepresentation.DocumentView,
            _ => throw new ArgumentException("Source view must be explicitly api or document.")
        };
        if (binding != "http" && representation != FederationRepresentation.DocumentView)
        {
            throw new ArgumentException("Native sources do not provide an HTTP API view.");
        }
        if (binding == "file")
        {
            Reject(source, "endpoint", "credentialEnvironment", "allowLoopbackHttp", "allowPrivateOrigin", "rootPath", "trustedRegistryRootSha256");
            var endpoint = DirectoryUri(Resolve(directory, Text(source, "directory")));
            var authorized = DirectoryUri(Resolve(directory, Text(source, "authorizedDirectory")));
            var layout = FileRegistrySource.ParseLayout(Text(source, "layout"));
            var reference = Optional(source, "reference");
            if (layout == FileRegistryLayout.DocumentTree && reference is not null ||
                layout == FileRegistryLayout.OciLayout && string.IsNullOrEmpty(reference))
            {
                throw new ArgumentException("File references must match the explicit selected layout.");
            }
            return new(name, representation, async (budget, token) =>
            {
                var opened = await FileRegistrySource.OpenAsync(endpoint, layout, reference, authorized, budget, token).ConfigureAwait(false);
                return new FederationSourceLease(opened, opened.DisposeAsync);
            });
        }
        Reject(source, "directory", "authorizedDirectory", "layout");
        if (binding == "http")
        {
            Reject(source, "reference", "rootPath", "trustedRegistryRootSha256");
            var root = new Uri(Text(source, "endpoint"), UriKind.Absolute);
            _ = new RegistryHttpConnectionPolicy(root, allowLoopback, allowPrivate);
            return new(name, representation, async (budget, token) =>
            {
                var credential = Credential(credentialName, secrets);
                var opened = await HttpFederationReadSource.OpenAsync(root, new XRegistryHttpClientOptions
                {
                    AllowLoopbackHttp = allowLoopback,
                    AllowPrivateOrigin = allowPrivate,
                    RequestTimeout = timeout,
                    MaxMetadataBytes = limits.MaxObjectBytes,
                    MaxDocumentBytes = limits.MaxObjectBytes,
                    MaxEncodedResponseBytes = limits.MaxTotalBytes,
                    AuthorizationProvider = credential is null ? null : (_, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return ValueTask.FromResult<AuthenticationHeaderValue?>(credential);
                    }
                }, new HttpFederationReadOptions { Limits = budget.Limits, Timeout = timeout }, budget, token).ConfigureAwait(false);
                return new FederationSourceLease(opened, () => { opened.Dispose(); return ValueTask.CompletedTask; }, CredentialStamp(credential));
            }, stamp);
        }
        if (binding == "git")
        {
            var root = new Uri(Text(source, "endpoint"), UriKind.Absolute);
            if (root.Scheme != "https" || allowLoopback) { throw new ArgumentException("Git document-tree locators require HTTPS; loopback Git HTTP is not enabled by this host."); }
            _ = new RegistryHttpConnectionPolicy(root, allowPrivateOrigin: allowPrivate);
            var reference = Text(source, "reference");
            GitBindingSyntax.ValidateRevision(reference);
            var rootPath = Optional(source, "rootPath", allowEmpty: true) ?? "xregistry";
            GitBindingSyntax.ValidateRootPath(rootPath);
            var trust = Optional(source, "trustedRegistryRootSha256");
            if (trust is not null && (trust.Length != 64 || trust.Any(value => !char.IsAsciiDigit(value) && value is not (>= 'a' and <= 'f'))))
            {
                throw new ArgumentException("A Git SHA-1 trust anchor must be a lowercase SHA-256.");
            }
            return new(name, representation, async (budget, token) =>
            {
                var credential = Credential(credentialName, secrets);
                var snapshot = await GitSmartHttpClient.FetchAsync(root, reference, new GitFetchOptions
                {
                    RootPath = rootPath,
                    TrustedRegistryRootSha256 = trust,
                    AllowPrivateOrigin = allowPrivate,
                    Timeout = timeout,
                    ObjectLimits = new GitReadLimits
                    {
                        MaxEncodedBytes = Math.Min(limits.MaxTotalBytes, 128 * 1024 * 1024),
                        MaxObjectBytes = limits.MaxObjectBytes,
                        MaxObjects = (int)limits.MaxObjects,
                        MaxTotalDecompressedBytes = limits.MaxTotalBytes
                    },
                    AuthorizationProvider = credential is null ? null : (_, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return ValueTask.FromResult<AuthenticationHeaderValue?>(credential);
                    }
                }, token).ConfigureAwait(false);
                var reader = new GitDocumentTreeReader(snapshot, root, rootPath, reference);
                var mapping = await DirectoryMapping.OpenAsync(reader, budget, token).ConfigureAwait(false);
                return new FederationSourceLease(mapping, mapping.DisposeAsync, CredentialStamp(credential));
            }, stamp);
        }
        if (binding == "oci")
        {
            Reject(source, "rootPath", "trustedRegistryRootSha256");
            var repository = OciRepository.Parse(Text(source, "endpoint"));
            var reference = Text(source, "reference");
            var policy = new RegistryHttpConnectionPolicy(repository.Origin, allowLoopback, allowPrivate);
            return new(name, representation, async (budget, token) =>
            {
                var credential = Credential(credentialName, secrets);
                var client = new OciDistributionClient(repository, policy, new OciDistributionOptions
                {
                    RequestTimeout = timeout,
                    MaxResponseBytes = limits.MaxObjectBytes,
                    AuthorizationProvider = credential is null ? null : (_, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return ValueTask.FromResult<AuthenticationHeaderValue?>(credential);
                    }
                });
                OciSnapshot snapshot;
                try { snapshot = await OciSnapshot.OpenAsync(client, reference, budget, token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is FederationException or IOException or HttpRequestException or OperationCanceledException or ArgumentException)
                {
                    client.Dispose();
                    throw;
                }
                return new FederationSourceLease(snapshot, async () =>
                {
                    try { await snapshot.DisposeAsync().ConfigureAwait(false); }
                    finally { client.Dispose(); }
                }, CredentialStamp(credential));
            }, stamp);
        }
        throw new ArgumentException("Only explicit HTTP, File, Git and OCI sources are supported; catalog discovery is not enabled.");
    }

    private static AuthenticationHeaderValue? Credential(string? name, Func<string, string?> secrets)
    {
        if (name is null) { return null; }
        var value = secrets(name);
        if (value is null || value.Length is < 32 or > 512 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '~' or '+' or '/' or '=')))
        {
            throw new InvalidOperationException("A configured upstream bearer credential is missing or invalid.");
        }
        return new("Bearer", value);
    }

    private static string CredentialStamp(AuthenticationHeaderValue? credential) => credential is null ? "" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.ToString())));

    private static async ValueTask<JsonElement> JsonFileAsync(string path, int maximum, CancellationToken token)
    {
        using var stream = IOFile.OpenRead(path);
        return (await RegistryJson.ParseAsync(stream, new RegistryJsonLimits { MaxBytes = maximum }, token).ConfigureAwait(false)).RootElement;
    }

    private static async ValueTask<RegistryModel> ModelFileAsync(string path, CancellationToken token) =>
        RegistryModel.Compile(RegistryJson.FromElement(await JsonFileAsync(path, 2 * 1024 * 1024, token).ConfigureAwait(false)));

    private static Uri DirectoryUri(string path) => new(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar);
    private static string Resolve(string root, string path) => Path.GetFullPath(path, root);

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new ArgumentException("The bridge configuration contains an unknown field or non-object section.");
        }
    }

    private static void Reject(JsonElement value, params string[] names)
    {
        if (names.Any(name => value.TryGetProperty(name, out _))) { throw new ArgumentException("A source field is not valid for its selected binding."); }
    }

    private static JsonElement Required(JsonElement value, string name) => value.TryGetProperty(name, out var member) ? member :
        throw new ArgumentException("A required bridge configuration field is missing.");

    private static string Text(JsonElement value, string name) => Optional(value, name) ??
        throw new ArgumentException("A required bridge configuration string is missing.");

    private static string? Optional(JsonElement value, string name, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var member)) { return null; }
        if (member.ValueKind != JsonValueKind.String || !allowEmpty && member.GetString()!.Length == 0)
        {
            throw new ArgumentException("A configuration field must be a nonempty string.");
        }
        return member.GetString();
    }

    private static string? EnvironmentName(JsonElement value, string name)
    {
        var result = Optional(value, name);
        if (result is not null && (result.Length > 128 || !(char.IsAsciiLetter(result[0]) || result[0] == '_') ||
            result.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
        {
            throw new ArgumentException("Secret names must be explicit portable environment-variable names.");
        }
        return result;
    }

    private static bool Boolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var member)) { return false; }
        if (member.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw new ArgumentException("A configuration boolean has the wrong type."); }
        return member.GetBoolean();
    }

    private static int Integer(JsonElement value, string name, int defaultValue)
    {
        if (!value.TryGetProperty(name, out var member)) { return defaultValue; }
        return member.ValueKind == JsonValueKind.Number && member.TryGetInt32(out var result) ? result :
            throw new ArgumentException("A bounded configuration integer has the wrong type.");
    }

    private static JsonElement[] Array(JsonElement value, string name, bool required = false)
    {
        if (!value.TryGetProperty(name, out var member))
        {
            if (required) { throw new ArgumentException("A required configuration array is missing."); }
            return [];
        }
        if (member.ValueKind != JsonValueKind.Array || member.GetArrayLength() > 16)
        {
            throw new ArgumentException("A configuration array must be bounded to sixteen entries.");
        }
        return member.EnumerateArray().ToArray();
    }
}
