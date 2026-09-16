// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net.Http.Headers;
using System.Text.Json;
using XRegistry.Bindings.File;
using XRegistry.Bindings.Oci;
using XRegistry.Client;
using XRegistry.Federation;

namespace XRegistry.Sample.Client;

internal static class OciCommands
{
    internal static async ValueTask<FederationReadResult> ReadRemoteAsync(
        string endpoint, string reference, FederationReadRequest request, bool allowPrivate,
        Func<Uri, CancellationToken, ValueTask<AuthenticationHeaderValue?>>? credentials, CancellationToken token)
    {
        var repository = OciRepository.Parse(endpoint);
        using var transport = Transport(repository, allowPrivate, credentials);
        var snapshot = await OciSnapshot.OpenAsync(transport, reference, cancellationToken: token).ConfigureAwait(false);
        await using (snapshot.ConfigureAwait(false))
        {
            return await ((IFederationReadSource)snapshot).ReadAsync(request, token).ConfigureAwait(false);
        }
    }

    internal static async ValueTask<FederationReadResult> ReadLayoutAsync(
        string directory, string reference, FederationReadRequest request, CancellationToken token)
    {
        using var tree = FileDocumentTreeReader.Open(DirectoryUri(directory));
        var snapshot = await OciSnapshot.OpenLayoutAsync(tree, reference, cancellationToken: token).ConfigureAwait(false);
        await using (snapshot.ConfigureAwait(false))
        {
            return await ((IFederationReadSource)snapshot).ReadAsync(request, token).ConfigureAwait(false);
        }
    }

    internal static async ValueTask<RegistryJson> VerifyLayoutAsync(string directory, string reference, CancellationToken token)
    {
        using var tree = FileDocumentTreeReader.Open(DirectoryUri(directory));
        var snapshot = await OciSnapshot.OpenLayoutAsync(tree, reference, cancellationToken: token).ConfigureAwait(false);
        await using (snapshot.ConfigureAwait(false))
        {
            var result = await snapshot.ValidateAsync(token).ConfigureAwait(false);
            return ValidationJson(result);
        }
    }

    internal static async ValueTask<RegistryJson> VerifyCaptureAsync(string capturePath, CancellationToken token) =>
        ValidationJson((await LoadCaptureAsync(capturePath, token).ConfigureAwait(false)).Validation);

    internal static async ValueTask<string> PublishAsync(
        string endpoint, string reference, string capturePath, bool allowPrivate,
        Func<Uri, CancellationToken, ValueTask<AuthenticationHeaderValue?>>? credentials, CancellationToken token)
    {
        var package = await LoadCaptureAsync(capturePath, token).ConfigureAwait(false);
        var repository = OciRepository.Parse(endpoint);
        using var transport = Transport(repository, allowPrivate, credentials);
        await package.PublishAsync(transport, reference, cancellationToken: token).ConfigureAwait(false);
        return package.RootDigest;
    }

    private static async ValueTask<OciSnapshotPackage> LoadCaptureAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        var limits = new OciWriteOptions().Limits;
        var budget = new FederationReadBudget(limits);
        using var tree = FileDocumentTreeReader.Open(
            DirectoryUri(Path.GetDirectoryName(path)!), limits.MaxObjectBytes);
        var captureBytes = await ReadFileAsync(tree, Path.GetFileName(path), budget, token).ConfigureAwait(false);
        var capture = RegistryJson.Parse(captureBytes, new() { MaxBytes = limits.MaxObjectBytes }).RootElement;
        RequireFields(capture, ["records", "documents"]);
        if (!capture.TryGetProperty("records", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("An OCI capture input requires an array of portable records.");
        }
        var records = new List<RegistryJson>();
        foreach (var record in entries.EnumerateArray())
        {
            budget.ChargeObject();
            records.Add(RegistryJson.FromElement(record));
        }
        var documents = new Dictionary<string, FederationDocument>(StringComparer.Ordinal);
        if (capture.TryGetProperty("documents", out var files))
        {
            if (files.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The capture documents field must be an object keyed by Version XID.");
            }
            foreach (var file in files.EnumerateObject())
            {
                RequireFields(file.Value, ["file", "contenttype", "base", "origin"]);
                var relative = Text(file.Value, "file") ??
                    throw new InvalidDataException("Each embedded Document needs a root-relative file.");
                var bytes = await ReadFileAsync(tree, relative, budget, token).ConfigureAwait(false);
                documents.Add(file.Name, new FederationDocument(bytes, Text(file.Value, "contenttype"),
                    Text(file.Value, "base"), Text(file.Value, "origin")));
            }
        }
        return await OciSnapshotWriter.CreateAsync(new OciSnapshotInput(records, documents, limits),
            cancellationToken: token).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadFileAsync(
        FileDocumentTreeReader tree, string relative, FederationReadBudget budget, CancellationToken token)
    {
        budget.ChargeRequest();
        budget.ChargeObject();
        using var input = await tree.OpenReadAsync(relative, token).ConfigureAwait(false) ??
            throw new FileNotFoundException("An explicitly referenced capture file is absent.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            budget.CheckObjectBytes(output.Length + read);
            budget.ChargeBytes(read);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static OciDistributionClient Transport(OciRepository repository, bool allowPrivate,
        Func<Uri, CancellationToken, ValueTask<AuthenticationHeaderValue?>>? credentials) =>
        new(repository, new RegistryHttpConnectionPolicy(repository.Origin, allowPrivateOrigin: allowPrivate),
            new OciDistributionOptions { AuthorizationProvider = credentials });

    private static Uri DirectoryUri(string directory) =>
        new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar);

    private static RegistryJson ValidationJson(OciValidationResult result)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("rootDigest", result.RootDigest);
            writer.WriteString("snapshotClass", result.SnapshotClass);
            writer.WriteNumber("objects", result.Objects);
            writer.WriteNumber("indexes", result.Indexes);
            writer.WriteNumber("manifests", result.Manifests);
            writer.WriteNumber("configs", result.Configs);
            writer.WriteNumber("documents", result.Documents);
            writer.WriteEndObject();
        }
        return RegistryJson.Parse(output.ToArray());
    }

    private static void RequireFields(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The capture input contains a non-object or an unsupported field.");
        }
    }

    private static string? Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field))
        {
            return null;
        }
        return field.ValueKind == JsonValueKind.String ? field.GetString() :
            throw new InvalidDataException("Capture text fields must be strings, not null or another JSON type.");
    }
}
