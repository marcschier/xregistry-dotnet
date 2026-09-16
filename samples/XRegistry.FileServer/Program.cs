// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using XRegistry;
using XRegistry.AspNetCore;
using XRegistry.Models;
using XRegistry.Sample.FileServer;
using XRegistry.Samples;
using XRegistry.Server;
using XRegistry.Storage.File;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    if (args.Contains("--help", StringComparer.Ordinal))
    {
        Console.WriteLine("""
            Durable xRegistry HTTP server (.NET 10 / Native AOT)

            Explicit loopback demonstration:
              --DataRoot DIRECTORY --Initialize true --DemoLoopback true
              --PublicRoot http://127.0.0.1:5280/registry --ListenPort 5280

            HTTPS deployment:
              --DataRoot DIRECTORY [--Initialize true]
              --PublicRoot https://registry.example:8443/registry --ListenPort 8443
              --CertificatePath SERVER.pfx [--CertificatePasswordEnvironment PFX_PASSWORD_ENV]
              --WriteTokenEnvironment ADMIN_TOKEN_ENV [--ReadTokenEnvironment READ_TOKEN_ENV]
              [--AllowAnonymousReads true]

            Models:
              --Model all|core|endpoint|message|schema|cloudevents|registry|openusd
              --ModelFile MODEL.json instead of --Model (external includes are not implicitly fetched)
              --InitialMetadata METADATA.json supplies required custom Registry attributes.
              Persisted models take precedence after initialization.

            Other:
              --MountPath /registry overrides local routing, not advertised PublicRoot.
              --AllowCapabilityUpdates false locks capabilities (authenticated administration is enabled by default).
              --MaxDocumentBytes NUMBER sets the bounded engine/Document budget.
              --BackupTo EMPTY-DIRECTORY performs an offline backup, without starting HTTP.
              --RuntimeInfo true reports execution mode without opening storage.

            Initialization never replaces an existing store. Restart without --Initialize.
            Demo mode grants every local caller administration; never forward its port.
            Token values belong only in the explicitly selected secret environment variables.
            """);
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var configuration = builder.Configuration;
        if (Boolean(configuration, "RuntimeInfo"))
        {
            var methods = JitInfo.GetCompiledMethodCount();
            using var output = Console.OpenStandardOutput();
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteBoolean("nativeAot", !RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled && methods == 0);
            writer.WriteNumber("jitCompiledMethods", methods);
            writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
            writer.WriteEndObject();
            return 0;
        }
        var directory = Required(configuration, "DataRoot");
        var maximumDocumentBytes = Integer(configuration, "MaxDocumentBytes", 8 * 1024 * 1024, 1, 256 * 1024 * 1024);
        var storageLimits = new FileStoreLimits
        {
            MaxRecords = 100_000,
            MaxMutations = 20_000,
            MaxMetadataBytesPerRecord = 16 * 1024 * 1024,
            MaxMetadataBytes = 256 * 1024 * 1024,
            MaxDatabaseBytes = 512 * 1024 * 1024,
            MaxJsonTokens = 4_000_000,
            MaxReadSnapshotBytes = 512 * 1024 * 1024,
            MaxDocumentBytes = maximumDocumentBytes,
            MaxDocumentReferences = 100_000,
            MaxReferencedDocumentBytes = 1024L * 1024 * 1024,
            MaxBlobBytes = 2L * 1024 * 1024 * 1024,
            MaxBlobFiles = 100_000,
            MaxTemporaryBytes = 512 * 1024 * 1024
        };
        if (configuration["BackupTo"] is { Length: > 0 } backup)
        {
            if (Boolean(configuration, "Initialize"))
            {
                throw new ArgumentException("Backup cannot be combined with initialization.");
            }
            using var store = LocalFileStore.Open(Path.GetFullPath(directory), storageLimits, cancellation.Token);
            var generation = await store.CreateBackupAsync(Path.GetFullPath(backup), cancellation.Token).ConfigureAwait(false);
            Console.WriteLine("Backed up committed generation " + generation.ToString(CultureInfo.InvariantCulture) + ".");
            return 0;
        }

        var root = new Uri(Required(configuration, "PublicRoot"), UriKind.Absolute);
        var hosting = new RegistrySampleHostOptions
        {
            PublicRoot = root,
            ListenPort = Integer(configuration, "ListenPort", 8443, 0, 65535),
            DemoLoopback = Boolean(configuration, "DemoLoopback"),
            AllowAnonymousReads = Boolean(configuration, "AllowAnonymousReads"),
            CertificatePath = configuration["CertificatePath"],
            CertificatePasswordEnvironment = configuration["CertificatePasswordEnvironment"],
            WriteTokenEnvironment = configuration["WriteTokenEnvironment"],
            ReadTokenEnvironment = configuration["ReadTokenEnvironment"]
        };
        var model = await ModelAsync(configuration, cancellation.Token).ConfigureAwait(false);
        var metadata = configuration["InitialMetadata"] is { Length: > 0 } metadataFile
            ? await JsonFileAsync(metadataFile, cancellation.Token).ConfigureAwait(false) : RegistryJson.Parse("{}");
        var engineOptions = new RegistryEngineOptions
        {
            RegistryId = configuration["RegistryId"] ?? "registry",
            PublicRoot = root,
            Model = model,
            InitialMetadata = metadata,
            AllowAnonymousReads = hosting.AllowAnonymousReads,
            AllowCapabilityUpdates = Boolean(configuration, "AllowCapabilityUpdates", true),
            ResourceValidator = new BuiltInRegistryResourceValidator(),
            Limits = new RegistryLimits
            {
                MaxDocumentBytes = maximumDocumentBytes,
                MaxResponseBytes = Math.Max(16 * 1024 * 1024, maximumDocumentBytes),
                MaxWorkingSetBytes = Math.Max(64L * 1024 * 1024, (long)maximumDocumentBytes * 4)
            }
        };
        RegistrySampleHosting.Configure(builder, hosting);
        var app = builder.Build();
        await using (app.ConfigureAwait(false))
        {
            RegistrySampleHosting.UseSecurity(app, hosting);
            var policy = app.Services.GetRequiredService<IRegistryAuthorizationPolicy>();
            using var store = await FileServerStore.OpenAsync(directory, Boolean(configuration, "Initialize"),
                engineOptions, policy, storageLimits, cancellation.Token).ConfigureAwait(false);
            var endpoint = app.MapXRegistry(store.Engine, new()
            {
                MountPath = configuration["MountPath"] ?? root.AbsolutePath.TrimEnd('/'),
                AuthenticationChallenge = RegistrySampleHosting.AuthenticationChallenge
            });
            if (hosting.AllowAnonymousReads)
            {
                endpoint.AllowAnonymousRegistryReads();
            }

            app.MapGet("/healthz", (HttpContext context) => HealthAsync(context, store));
            var discovery = app.MapGet("/.well-known/xregistry", (HttpContext context) => DiscoveryAsync(context, root));
            if (hosting.AllowAnonymousReads)
            {
                discovery.AllowAnonymousRegistryReads();
            }
            await ((IHost)app).RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        return 0;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        return 0;
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or RegistryException or JsonException)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}

static async Task HealthAsync(HttpContext context, FileServerStore store)
{
    try
    {
        var generation = store.Generation;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"ready\",\"generation\":" +
            generation.ToString(CultureInfo.InvariantCulture) + "}", context.RequestAborted).ConfigureAwait(false);
    }
    catch (StorageException exception)
    {
        FileServerLog.StorageHealthFailure(
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("StorageHealth"), exception);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    }
}

static async Task DiscoveryAsync(HttpContext context, Uri root)
{
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
        writer.WriteStartObject();
        writer.WriteStartArray("registries");
        writer.WriteStringValue(root.AbsoluteUri.TrimEnd('/'));
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
    context.Response.ContentType = "application/json";
    context.Response.ContentLength = buffer.WrittenCount;
    await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
}

static async ValueTask<RegistryModel> ModelAsync(IConfiguration configuration, CancellationToken token)
{
    if (configuration["ModelFile"] is { Length: > 0 } file)
    {
        if (configuration["Model"] is not null)
        {
            throw new ArgumentException("Select a model file or a built-in model, not both.");
        }
        return RegistryModel.Compile(await JsonFileAsync(file, token).ConfigureAwait(false),
            new() { SourceUri = new Uri(Path.GetFullPath(file)) });
    }
    var name = (configuration["Model"] ?? "all").ToLowerInvariant();
    if (name == "all")
    {
        return BuiltInRegistryModels.CompileAllForServer();
    }
    return BuiltInRegistryModels.CompileForServer(name switch
    {
        "core" => RegistryModelKind.Core,
        "endpoint" => RegistryModelKind.Endpoint,
        "message" => RegistryModelKind.Message,
        "schema" => RegistryModelKind.Schema,
        "cloudevents" => RegistryModelKind.CloudEvents,
        "registry" => RegistryModelKind.Registry,
        "openusd" => RegistryModelKind.OpenUsd,
        _ => throw new ArgumentException("Unknown built-in model selection.")
    });
}

static async ValueTask<RegistryJson> JsonFileAsync(string path, CancellationToken token)
{
    using var input = File.OpenRead(Path.GetFullPath(path));
    return await RegistryJson.ParseAsync(input, cancellationToken: token).ConfigureAwait(false);
}

static string Required(IConfiguration configuration, string key) =>
    configuration[key] is { Length: > 0 } value ? value : throw new ArgumentException($"Configuration '{key}' is required.");

static bool Boolean(IConfiguration configuration, string key, bool defaultValue = false) => configuration[key] switch
{
    null => defaultValue,
    var value when bool.TryParse(value, out var result) => result,
    _ => throw new ArgumentException($"Configuration '{key}' must be true or false.")
};

static int Integer(IConfiguration configuration, string key, int defaultValue, int minimum, int maximum)
{
    var value = configuration[key];
    if (value is null)
    {
        return defaultValue;
    }
    return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= minimum && result <= maximum
        ? result : throw new ArgumentException($"Configuration '{key}' is outside its permitted integer range.");
}
