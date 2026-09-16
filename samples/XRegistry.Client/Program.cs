// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using XRegistry;
using XRegistry.Bindings.File;
using XRegistry.Bindings.Git;
using XRegistry.Client;
using XRegistry.Federation;
using XRegistry.Models;
using XRegistry.Sample.Client;
using XRegistry.Validation;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    if (arguments.Length == 0 || arguments.Contains("--help", StringComparer.Ordinal))
    {
        Console.WriteLine("""
            xRegistry client sample

              get ROOT PATH [--query NAME=VALUE] [network options]
              pages ROOT COLLECTION-PATH [--query limit=NUMBER] [network options]
              discover ROOT [--host] [network options]
              inspect ROOT [network options]
              put ROOT PATH FILE CONTENT-TYPE [network options]
              delete ROOT PATH [--query epoch=NUMBER] [network options]
              read-file DIRECTORY TARGET [--operation entity|collection|document|model|capabilities]
              read-git HTTPS-REPOSITORY FULL-REF-OR-OID TARGET [--root-path PATH] [--root-sha256 HASH]
              read-oci OCI-REPOSITORY REFERENCE TARGET [--operation ...] [network options]
              read-oci-layout DIRECTORY REFERENCE TARGET [--operation ...]
              verify-oci-layout DIRECTORY REFERENCE
              verify-oci-capture CAPTURE.json
              publish-oci OCI-REPOSITORY REFERENCE CAPTURE.json [network options]
              validate FORMAT FILE
              model core|endpoint|message|schema|cloudevents|registry|openusd
              runtime-info

            Network options: --allow-loopback-http, --allow-private-origin, --token-env NAME, --decode-content.
            Credentials are read from an explicitly named environment variable, never URI userinfo.
            Git defaults to the xregistry directory; SHA-1 acquisition requires a trusted root SHA-256.
            External document descriptors are not automatically fetched. Native OCI always uses HTTPS.
            --decode-content opts HTTP Registry responses into bounded gzip/deflate/Brotli decoding.
            """);
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var (positionals, options, query) = Parse(arguments);
        var command = positionals[0];
        var token = cancellation.Token;
        var credential = Credentials(options);
        switch (command)
        {
            case "runtime-info":
                Arity(positionals, 1);
                using (var output = Console.OpenStandardOutput())
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    var compiledMethods = JitInfo.GetCompiledMethodCount();
                    writer.WriteBoolean("nativeAot", !RuntimeFeature.IsDynamicCodeSupported &&
                        !RuntimeFeature.IsDynamicCodeCompiled && compiledMethods == 0);
                    writer.WriteNumber("jitCompiledMethods", compiledMethods);
                    writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
                    writer.WriteEndObject();
                }
                return 0;
            case "model":
                Arity(positionals, 2);
                var kind = positionals[1].ToLowerInvariant() switch
                {
                    "core" => RegistryModelKind.Core,
                    "endpoint" => RegistryModelKind.Endpoint,
                    "message" => RegistryModelKind.Message,
                    "schema" => RegistryModelKind.Schema,
                    "cloudevents" => RegistryModelKind.CloudEvents,
                    "registry" => RegistryModelKind.Registry,
                    "openusd" => RegistryModelKind.OpenUsd,
                    _ => throw new ArgumentException("Unknown built-in model name.")
                };
                using (var output = Console.OpenStandardOutput())
                using (var writer = new Utf8JsonWriter(output, new() { Indented = true }))
                {
                    BuiltInRegistryModels.Compile(kind).EffectiveModel.RootElement.WriteTo(writer);
                }
                return 0;
            case "validate":
                Arity(positionals, 3);
                var path = Path.GetFullPath(positionals[2]);
                var limits = new DocumentValidationOptions();
                var bytes = await ReadBoundedFileAsync(path, limits.MaxDocumentBytes, token).ConfigureAwait(false);
                var validated = await new BuiltInDocumentValidator().ValidateAsync(positionals[1], bytes, limits, token).ConfigureAwait(false);
                Console.WriteLine(validated.Status);
                foreach (var diagnostic in validated.Diagnostics)
                {
                    Console.Error.WriteLine($"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Detail}");
                }
                return validated.Status == DocumentValidationStatus.Valid ? 0 : 1;
            case "read-file":
                Arity(positionals, 3);
                var directory = Path.GetFullPath(positionals[1]);
                using (var reader = FileDocumentTreeReader.Open(new Uri(directory + Path.DirectorySeparatorChar)))
                {
                    var mapping = await DirectoryMapping.OpenAsync(reader, cancellationToken: token).ConfigureAwait(false);
                    await using (mapping.ConfigureAwait(false))
                    {
                        return await PrintReadAsync(await mapping.ReadAsync(
                            new(Operation(options), positionals[2]), token).ConfigureAwait(false), token).ConfigureAwait(false);
                    }
                }
            case "read-git":
                Arity(positionals, 4);
                var repository = new Uri(positionals[1], UriKind.Absolute);
                var rootPath = options.GetValueOrDefault("--root-path", "xregistry");
                var snapshot = await GitSmartHttpClient.FetchAsync(repository, positionals[2], new()
                {
                    RootPath = rootPath,
                    TrustedRegistryRootSha256 = options.GetValueOrDefault("--root-sha256"),
                    AllowLoopbackHttp = options.ContainsKey("--allow-loopback-http"),
                    AllowPrivateOrigin = options.ContainsKey("--allow-private-origin"),
                    AuthorizationProvider = credential
                }, token).ConfigureAwait(false);
                var tree = new GitDocumentTreeReader(snapshot, repository, rootPath, positionals[2]);
                var gitMapping = await DirectoryMapping.OpenAsync(tree, cancellationToken: token).ConfigureAwait(false);
                await using (gitMapping.ConfigureAwait(false))
                {
                    return await PrintReadAsync(await gitMapping.ReadAsync(
                        new(Operation(options), positionals[3]), token).ConfigureAwait(false), token).ConfigureAwait(false);
                }
            case "read-oci-layout":
                Arity(positionals, 4);
                return await PrintReadAsync(await OciCommands.ReadLayoutAsync(
                    positionals[1], positionals[2], new(Operation(options), positionals[3]), token).ConfigureAwait(false),
                    token).ConfigureAwait(false);
            case "verify-oci-layout":
            case "verify-oci-capture":
                Arity(positionals, command == "verify-oci-layout" ? 3 : 2);
                var validation = command == "verify-oci-layout"
                    ? await OciCommands.VerifyLayoutAsync(positionals[1], positionals[2], token).ConfigureAwait(false)
                    : await OciCommands.VerifyCaptureAsync(positionals[1], token).ConfigureAwait(false);
                using (var output = Console.OpenStandardOutput())
                using (var writer = new Utf8JsonWriter(output, new() { Indented = true }))
                {
                    validation.RootElement.WriteTo(writer);
                }
                return 0;
            case "read-oci":
            case "publish-oci":
                Arity(positionals, 4);
                if (options.ContainsKey("--allow-loopback-http") || options.ContainsKey("--decode-content"))
                {
                    throw new ArgumentException("Native OCI uses exact-byte HTTPS; Registry HTTP transport options do not apply.");
                }
                if (command == "publish-oci")
                {
                    var digest = await OciCommands.PublishAsync(positionals[1], positionals[2], positionals[3],
                        options.ContainsKey("--allow-private-origin"), credential, token).ConfigureAwait(false);
                    Console.WriteLine(digest);
                    return 0;
                }
                return await PrintReadAsync(await OciCommands.ReadRemoteAsync(
                    positionals[1], positionals[2], new(Operation(options), positionals[3]),
                    options.ContainsKey("--allow-private-origin"), credential, token).ConfigureAwait(false), token).ConfigureAwait(false);
            case "get":
            case "pages":
            case "discover":
            case "inspect":
            case "delete":
            case "put":
                Arity(positionals, command is "inspect" or "discover" ? 2 : command == "put" ? 5 : 3);
                using (var client = new XRegistryHttpClient(new Uri(positionals[1], UriKind.Absolute), new()
                {
                    AllowLoopbackHttp = options.ContainsKey("--allow-loopback-http"),
                    AllowPrivateOrigin = options.ContainsKey("--allow-private-origin"),
                    AuthorizationProvider = credential,
                    EnableContentDecoding = options.ContainsKey("--decode-content")
                }))
                {
                    if (command == "pages")
                    {
                        using var output = Console.OpenStandardOutput();
                        await foreach (var page in client.ReadCollectionPagesAsync(
                            positionals[2], query, cancellationToken: token).ConfigureAwait(false))
                        {
                            using var writer = new Utf8JsonWriter(output);
                            page.Records.WriteTo(writer);
                            await writer.FlushAsync(token).ConfigureAwait(false);
                            output.WriteByte((byte)'\n');
                        }
                        return 0;
                    }
                    if (command == "discover")
                    {
                        var discovery = await client.DiscoverAsync(options.ContainsKey("--host")
                            ? RegistryDiscoveryLocation.Host : RegistryDiscoveryLocation.Registry,
                            cancellationToken: token).ConfigureAwait(false);
                        using var output = Console.OpenStandardOutput();
                        using var writer = new Utf8JsonWriter(output, new() { Indented = true });
                        writer.WriteStartObject();
                        writer.WriteStartArray("registries");
                        foreach (var registry in discovery.Registries)
                        {
                            writer.WriteStringValue(registry.OriginalString);
                        }
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                        return 0;
                    }
                    if (command == "put")
                    {
                        using var input = System.IO.File.OpenRead(Path.GetFullPath(positionals[3]));
                        using var response = await client.SendDocumentAsync(HttpMethod.Put, positionals[2],
                            input, positionals[4], query, token).ConfigureAwait(false);
                        using var output = Console.OpenStandardOutput();
                        await response.CopyDocumentToAsync(output, token).ConfigureAwait(false);
                        return (int)response.StatusCode is >= 200 and < 300 ? 0 : 1;
                    }

                    using var result = await client.SendAsync(command == "delete" ? HttpMethod.Delete : HttpMethod.Get,
                        command == "inspect" ? "model" : positionals[2], query: query, cancellationToken: token).ConfigureAwait(false);
                    using var destination = Console.OpenStandardOutput();
                    await result.CopyDocumentToAsync(destination, token).ConfigureAwait(false);
                    return (int)result.StatusCode is >= 200 and < 300 ? 0 : 1;
                }
            default:
                throw new ArgumentException("Unknown command. Use --help for supported commands.");
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine("The operation was cancelled.");
        return 130;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("The operation was cancelled by its configured deadline or source.");
        return 1;
    }
    catch (Exception error) when (error is ArgumentException or FormatException or IOException or HttpRequestException or FederationException or RegistryException or JsonException or NotSupportedException)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
}

static async ValueTask<int> PrintReadAsync(FederationReadResult result, CancellationToken cancellationToken)
{
    using var output = Console.OpenStandardOutput();
    if (result.Document is not null)
    {
        using var input = result.Document.OpenRead();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return 0;
    }
    using var writer = new Utf8JsonWriter(output, new() { Indented = true });
    if (result.ExternalDocument.ValueKind != JsonValueKind.Undefined)
    {
        result.ExternalDocument.WriteTo(writer);
        Console.Error.WriteLine("External document returned as a descriptor; bytes were not fetched.");
        return 2;
    }
    result.Metadata.WriteTo(writer);
    return 0;
}

static async ValueTask<byte[]> ReadBoundedFileAsync(string path, int limit, CancellationToken cancellationToken)
{
    using var input = System.IO.File.OpenRead(path);
    if (input.Length > limit)
    {
        throw new InvalidDataException("The validation input exceeds the sample's byte limit.");
    }
    using var output = new MemoryStream();
    var buffer = new byte[8192];
    while (true)
    {
        var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (count == 0) { break; }
        if (count > limit - output.Length)
        {
            throw new InvalidDataException("The validation input exceeded its byte limit while reading.");
        }
        output.Write(buffer, 0, count);
    }
    return output.ToArray();
}

static FederationOperation Operation(Dictionary<string, string> options) =>
    options.GetValueOrDefault("--operation", "entity") switch
    {
        "entity" => FederationOperation.Entity,
        "collection" => FederationOperation.Collection,
        "document" => FederationOperation.Document,
        "model" => FederationOperation.Model,
        "capabilities" => FederationOperation.Capabilities,
        _ => throw new ArgumentException("Unknown read operation.")
    };

static Func<Uri, CancellationToken, ValueTask<AuthenticationHeaderValue?>>? Credentials(Dictionary<string, string> options)
{
    if (!options.TryGetValue("--token-env", out var name)) { return null; }
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException("The explicitly selected credential environment variable is empty.");
    }
    return (_, token) =>
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AuthenticationHeaderValue?>(new("Bearer", value));
    };
}

static (List<string> Positionals, Dictionary<string, string> Options, List<KeyValuePair<string, string?>> Query) Parse(string[] arguments)
{
    var positionals = new List<string>();
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    var query = new List<KeyValuePair<string, string?>>();
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (argument is "--allow-loopback-http" or "--allow-private-origin" or "--host" or "--decode-content")
        {
            if (!options.TryAdd(argument, "true")) { throw new ArgumentException("An option was repeated."); }
        }
        else if (argument is "--token-env" or "--root-path" or "--root-sha256" or "--operation" or "--query")
        {
            if (++index == arguments.Length) { throw new ArgumentException("An option value is missing."); }
            if (argument == "--query")
            {
                var value = arguments[index];
                var separator = value.IndexOf('=');
                query.Add(new(separator < 0 ? value : value[..separator], separator < 0 ? null : value[(separator + 1)..]));
            }
            else if (!options.TryAdd(argument, arguments[index])) { throw new ArgumentException("An option was repeated."); }
        }
        else if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("An unknown option was supplied.");
        }
        else { positionals.Add(argument); }
    }
    if (positionals.Count == 0) { throw new ArgumentException("A command is required."); }
    return (positionals, options, query);
}

static void Arity(List<string> arguments, int expected)
{
    if (arguments.Count != expected)
    {
        throw new ArgumentException("The command has missing or extra positional arguments. Use --help.");
    }
}
