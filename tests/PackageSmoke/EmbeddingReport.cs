// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmbeddingConsumer;

internal sealed class EmbeddingReport(string runtimeIdentifier)
{
#if NET8_0
    internal const string Framework = "net8.0";
#elif NET10_0
    internal const string Framework = "net10.0";
#else
#error The embedding probe qualifies only net8.0 and net10.0.
#endif

    internal bool DynamicCodeSupported { get; } = RuntimeFeature.IsDynamicCodeSupported;
    internal bool DynamicCodeCompiled { get; } = RuntimeFeature.IsDynamicCodeCompiled;
    internal long JitAtStart { get; } = JitInfo.GetCompiledMethodCount();
    internal string Outcome { get; set; } = "failed";
    internal string? Failure { get; set; }
    internal long HttpRequests { get; set; }
    internal long CommittedBatches { get; set; }
    internal long DocumentBytesVerified { get; set; }
    internal List<string> Requests { get; } = [];
    internal Dictionary<string, long> RuntimeEvidence { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> RuntimeFacts { get; } = new(StringComparer.Ordinal);
    internal List<string> NativeModules { get; } = [];
    internal List<(string Name, string Outcome)> Cases { get; } = [];
    private readonly List<(string Name, string Hash, string[] References)> _assemblies = [];

    internal bool IsNative => !DynamicCodeSupported && !DynamicCodeCompiled && JitAtStart == 0 &&
        RuntimeInformation.ProcessArchitecture == RuntimeInformation.OSArchitecture;

    internal async Task CaseAsync(string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            Cases.Add((name, "passed"));
            Console.WriteLine("PASS " + name);
        }
        catch
        {
            Cases.Add((name, "failed"));
            throw;
        }
    }

    internal void InspectAssemblies(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        foreach (var asset in document.RootElement.GetProperty("assemblies").EnumerateArray())
        {
            using var stream = File.OpenRead(asset.GetProperty("path").GetString()!);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            Program.Require(hash == asset.GetProperty("sha256").GetString(), "package assembly hash");
            stream.Position = 0;
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            var references = metadata.AssemblyReferences.Select(handle =>
                metadata.GetString(metadata.GetAssemblyReference(handle).Name)).Order(StringComparer.Ordinal).ToArray();
            Program.Require(!Forbidden(name) && !references.Any(Forbidden), "no OPC UA or test-framework assembly dependency");
            _assemblies.Add((name, hash, references));
        }

        Program.Require(_assemblies.Count >= 11, "all eleven package runtime assemblies");
    }

    private static bool Forbidden(string name) => name.StartsWith("Opc.Ua", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("OpcFoundation", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase);

    internal void Write(string path)
    {
        var jitAtEnd = JitInfo.GetCompiledMethodCount();
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("scope", "development-package-embedding");
        writer.WriteBoolean("releaseQualified", false);
        writer.WriteString("outcome", Outcome);
        writer.WriteString("framework", Framework);
        writer.WriteString("runtimeIdentifier", runtimeIdentifier);
        writer.WriteString("processArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteString("osArchitecture", RuntimeInformation.OSArchitecture.ToString());
        writer.WriteBoolean("dynamicCodeSupported", DynamicCodeSupported);
        writer.WriteBoolean("dynamicCodeCompiled", DynamicCodeCompiled);
        writer.WriteNumber("jitCompiledMethodsAtStart", JitAtStart);
        writer.WriteNumber("jitCompiledMethodsAtEnd", jitAtEnd);
        writer.WriteNumber("httpRequests", HttpRequests);
        writer.WriteNumber("committedBatches", CommittedBatches);
        writer.WriteNumber("documentBytesVerified", DocumentBytesVerified);
        writer.WriteStartArray("requests");
        foreach (var request in Requests) { writer.WriteStringValue(request); }
        writer.WriteEndArray();
        writer.WriteStartObject("runtimeEvidence");
        foreach (var entry in RuntimeEvidence) { writer.WriteNumber(entry.Key, entry.Value); }
        writer.WriteEndObject();
        writer.WriteStartObject("runtimeFacts");
        foreach (var entry in RuntimeFacts) { writer.WriteString(entry.Key, entry.Value); }
        writer.WriteEndObject();
        writer.WriteStartArray("nativeModules");
        foreach (var module in NativeModules) { writer.WriteStringValue(module); }
        writer.WriteEndArray();
        writer.WriteStartArray("cases");
        foreach (var result in Cases)
        {
            writer.WriteStartObject();
            writer.WriteString("name", result.Name);
            writer.WriteString("outcome", result.Outcome);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("assemblies");
        foreach (var assembly in _assemblies)
        {
            writer.WriteStartObject();
            writer.WriteString("name", assembly.Name);
            writer.WriteString("sha256", assembly.Hash);
            writer.WriteStartArray("references");
            foreach (var reference in assembly.References) { writer.WriteStringValue(reference); }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (Failure is not null) { writer.WriteString("failure", Failure); }
        writer.WriteEndObject();
    }
}
