using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using XRegistry.Storage.File;

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled ||
    JitInfo.GetCompiledMethodCount() != 0 || RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
{
    Console.Error.WriteLine("The storage qualification probe must execute as Native AOT on the selected native architecture.");
    return 2;
}

if (args.Length != 2 || args[0] is not ("prepare" or "commit" or "race" or "inspect"))
{
    throw new ArgumentException("Expected: prepare|commit|race|inspect EXISTING-DIRECTORY");
}

if (args[0] == "inspect")
{
    using var store = LocalFileStore.Open(args[1]);
    using var snapshot = store.ReadSnapshot();
    using var output = Console.OpenStandardOutput();
    using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    writer.WriteBoolean("native", !RuntimeFeature.IsDynamicCodeSupported &&
        !RuntimeFeature.IsDynamicCodeCompiled && JitInfo.GetCompiledMethodCount() == 0);
    writer.WriteNumber("jitCompiledMethods", JitInfo.GetCompiledMethodCount());
    writer.WriteNumber("generation", snapshot.Generation);
    writer.WriteStartObject("records");
    foreach (var record in snapshot.Records)
    {
        writer.WriteStartObject(record.Key);
        writer.WritePropertyName("metadata");
        record.Metadata.WriteTo(writer);
        if (record.Document is null)
        {
            writer.WriteNull("document");
        }
        else
        {
            using var document = snapshot.OpenDocument(record.Key);
            using var bytes = new MemoryStream();
            await document.CopyToAsync(bytes);
            writer.WriteString("document", Convert.ToHexString(bytes.ToArray()));
        }
        writer.WriteEndObject();
    }
    writer.WriteEndObject();
    writer.WriteNumber("removedOrphans", store.CollectOrphans());
    writer.WriteNumber("remainingStagingFiles", Directory.GetFiles(Path.Combine(args[1], "staging")).Length);
    writer.WriteEndObject();
    return 0;
}

using (var store = LocalFileStore.Initialize(args[1]))
using (var document = new MemoryStream(Enumerable.Range(0, 256).Select(value => (byte)value).ToArray()))
{
    var mutations = Enumerable.Range(0, 1000).Select(index =>
    {
        var key = "record-" + index.ToString("D4", CultureInfo.InvariantCulture);
        var metadata = Encoding.UTF8.GetBytes("{\"ordinal\":" + index.ToString(CultureInfo.InvariantCulture) +
            ",\"epoch\":184467440737095516160}");
        return index == 0 ? StorageMutation.Put(key, metadata, document) : StorageMutation.Put(key, metadata);
    }).ToArray();
    using var candidate = await store.PrepareAsync(0, mutations);
    Console.WriteLine("PREPARED");
    await Console.Out.FlushAsync();
    if (args[0] == "prepare")
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    else
    {
        if (args[0] == "race" && await Console.In.ReadLineAsync() != "COMMIT")
        {
            throw new InvalidOperationException("The crash harness did not release the commit barrier.");
        }
        var generation = store.Commit(candidate);
        Console.WriteLine("COMMITTED " + generation.ToString(CultureInfo.InvariantCulture));
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
return 0;
