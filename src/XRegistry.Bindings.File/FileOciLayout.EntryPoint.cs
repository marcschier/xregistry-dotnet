// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.File;

internal static class FileOciLayoutEntryPoint
{
    private const string IndexMediaType = "application/vnd.oci.image.index.v1+json";
    private const string ReferenceName = "org.opencontainers.image.ref.name";

    internal static byte[] Merge(byte[]? previous, byte[] selected, string reference, FileOciLayoutPublicationOptions options,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        var newIndex = Parse(selected, options, budget, cancellationToken);
        Validate(newIndex, options, budget);
        var newEntry = newIndex.GetProperty("manifests")[0];
        if (previous is null) { return Encode(JsonNode.Parse(newIndex.GetRawText())!, options.MaxEntryPointBytes); }
        var oldIndex = Parse(previous, options, budget, cancellationToken);
        Validate(oldIndex, options, budget);
        var result = JsonNode.Parse(oldIndex.GetRawText())!.AsObject();
        var entries = result["manifests"]!.AsArray();
        if (reference.StartsWith("sha256:", StringComparison.Ordinal))
        {
            if (!oldIndex.GetProperty("manifests").EnumerateArray().Any(e =>
                Text(e, "digest") == reference && Text(e, "mediaType") == IndexMediaType &&
                e.TryGetProperty("artifactType", out var artifact) &&
                artifact.GetString() == "application/vnd.xregistry.registry.v1+json"))
            {
                entries.Add(JsonNode.Parse(newEntry.GetRawText()));
            }
        }
        else
        {
            var matches = oldIndex.GetProperty("manifests").EnumerateArray().Select((entry, index) => (entry, index))
                .Where(pair => pair.entry.TryGetProperty("annotations", out var annotations) &&
                    annotations.TryGetProperty(ReferenceName, out var name) && name.GetString() == reference)
                .Select(pair => pair.index).ToArray();
            if (matches.Length > 1)
            {
                throw new FederationException(FederationErrorCode.Ambiguous, "The selected local OCI tag has multiple existing entries.");
            }
            if (matches.Length == 1) { entries[matches[0]] = JsonNode.Parse(newEntry.GetRawText()); }
            else { entries.Add(JsonNode.Parse(newEntry.GetRawText())); }
        }
        if (entries.Count > options.MaxEntryPointDescriptors)
        {
            throw FileRegistryLocation.Limit("The merged OCI entrypoint exceeds its descriptor bound.");
        }
        return Encode(result, options.MaxEntryPointBytes);
    }

    internal static JsonElement Parse(byte[] bytes, FileOciLayoutPublicationOptions options,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var value = RegistryJson.Parse(bytes, new()
            {
                MaxBytes = options.MaxEntryPointBytes,
                MaxDepth = budget.Limits.MaxJsonDepth,
                MaxNodes = (int)Math.Max(1, Math.Min(int.MaxValue, budget.Limits.MaxWork - budget.Work)),
            }).RootElement;
            RequireObject(value);
            Charge(value);
            return value;
        }
        catch (RegistryException exception)
        {
            throw new FederationException(exception.Diagnostic.Code.EndsWith("_limit", StringComparison.Ordinal) ?
                FederationErrorCode.LimitExceeded : FederationErrorCode.InvalidPackage,
                "Malformed OCI layout control JSON.", exception.Diagnostic.Code, exception);
        }

        void Charge(JsonElement value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) { Charge(property.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray()) { Charge(entry); }
            }
        }
    }

    private static void Validate(JsonElement index, FileOciLayoutPublicationOptions options, FederationReadBudget budget)
    {
        if (Integer(index, "schemaVersion") != 2 ||
            index.TryGetProperty("mediaType", out _) && Text(index, "mediaType") != IndexMediaType)
        {
            throw Invalid("The existing entrypoint is not a standard OCI image index.");
        }
        if (index.TryGetProperty("annotations", out var annotations)) { Annotations(annotations); }
        var entries = Required(index, "manifests");
        if (entries.ValueKind != JsonValueKind.Array) { throw Invalid("An OCI entrypoint requires a manifests array."); }
        if (entries.GetArrayLength() > options.MaxEntryPointDescriptors)
        {
            throw FileRegistryLocation.Limit("The existing OCI entrypoint exceeds its descriptor bound.");
        }
        foreach (var entry in entries.EnumerateArray())
        {
            budget.ChargeWork();
            RequireObject(entry);
            if (!MediaType(Text(entry, "mediaType"))) { throw Invalid("An OCI entrypoint descriptor has a malformed media type."); }
            Integer(entry, "size");
            var digest = Text(entry, "digest");
            var colon = digest.IndexOf(':');
            if (colon <= 0 || colon == digest.Length - 1 ||
                digest[..colon].Split(['+', '.', '_', '-']).Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c))) ||
                digest[(colon + 1)..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('=' or '_' or '-')))
            {
                throw Invalid("An OCI entrypoint descriptor has malformed digest syntax.");
            }
            if (entry.TryGetProperty("artifactType", out _) && !MediaType(Text(entry, "artifactType")))
            {
                throw Invalid("An OCI entrypoint descriptor has a malformed artifact type.");
            }
            if (entry.TryGetProperty("annotations", out annotations)) { Annotations(annotations); }
        }
    }

    private static long Integer(JsonElement value, string name)
    {
        var element = Required(value, name);
        if (element.ValueKind != JsonValueKind.Number) { throw Invalid("An OCI entrypoint integer is malformed."); }
        var number = RegistryNumber.FromElement(element);
        if (!number.IsInteger || number.Significand.Sign < 0 || number.Exponent > 18)
        {
            throw Invalid("An OCI entrypoint integer is outside its permitted range.");
        }
        var integer = number.ToBigInteger();
        return integer <= long.MaxValue ? (long)integer : throw Invalid("An OCI entrypoint integer is too large.");
    }

    private static void Annotations(JsonElement value)
    {
        RequireObject(value);
        if (value.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
        {
            throw Invalid("OCI entrypoint annotations must be string-valued.");
        }
    }

    private static bool MediaType(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length is >= 1 and <= 127 && char.IsAsciiLetterOrDigit(p[0]) &&
            p.All(c => char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-'));
    }

    private static JsonElement Required(JsonElement value, string name)
    {
        RequireObject(value);
        return value.TryGetProperty(name, out var property) ? property : throw Invalid("A required OCI entrypoint field is absent.");
    }

    private static string Text(JsonElement value, string name)
    {
        var property = Required(value, name);
        return property.ValueKind == JsonValueKind.String && property.GetString()!.Length != 0
            ? property.GetString()! : throw Invalid("An OCI entrypoint string is malformed.");
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw Invalid("OCI layout controls require JSON objects."); }
    }

    private static FederationException Invalid(string message) => new(FederationErrorCode.InvalidPackage, message);

    private static byte[] Encode(JsonNode value, int limit)
    {
        using var stream = new BoundedStream(limit);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 256 })) { value.WriteTo(writer); }
        return stream.ToArray();
    }

    private sealed class BoundedStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }
        private void Check(int count)
        {
            if (count > limit - Length) { throw FileRegistryLocation.Limit("The merged OCI entrypoint exceeds its exact encoded byte bound."); }
        }
    }
}
