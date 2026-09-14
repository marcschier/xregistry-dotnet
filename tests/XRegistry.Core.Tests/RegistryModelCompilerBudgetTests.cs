using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryModelCompilerBudgetTests
{
    private static readonly int[] s_flatCounts = [256, 512, 1024];

    [Test]
    public async Task FlatAttributeCompilationHasLinearAllocationGrowth()
    {
        var profiles = s_flatCounts.Select(MeasureFlat).ToArray();
        Export("flat", profiles);

        await Assert.That(profiles[0].AllocatedBytes).IsGreaterThan(0);
        await Assert.That(profiles[1].AllocatedBytes).IsLessThanOrEqualTo(profiles[0].AllocatedBytes * 3);
        await Assert.That(profiles[2].AllocatedBytes).IsLessThanOrEqualTo(profiles[0].AllocatedBytes * 6);
    }

    [Test]
    public async Task IncludeOutputQuotaStopsAmplifiedBufferAllocation()
    {
        var source = IncludedAttributes(16);
        var document = RegistryJson.Parse(new JsonObject
        {
            ["type"] = "string",
            ["description"] = new string('x', 4096)
        }.ToJsonString());
        var options = new RegistryModelCompilationOptions { Resolver = new DocumentResolver(document) };
        var low = MeasureFailure(source, options, 1024);
        var higher = MeasureFailure(source, options, 32 * 1024);
        Export("includes", [low, higher]);

        await Assert.That(low.Code).IsEqualTo("byte_limit");
        await Assert.That(higher.Code).IsEqualTo("byte_limit");
        await Assert.That(low.AllocatedBytes + 16 * 1024).IsLessThan(higher.AllocatedBytes);
    }

    [Test]
    public async Task EffectiveOutputQuotaStopsImportedBufferAllocation()
    {
        var source = ImportedResources(16);
        var low = MeasureFailure(source, new(), 1024);
        var higher = MeasureFailure(source, new(), 64 * 1024);
        Export("imports", [low, higher]);

        await Assert.That(low.Code).IsEqualTo("byte_limit");
        await Assert.That(higher.Code).IsEqualTo("byte_limit");
        await Assert.That(low.AllocatedBytes + 16 * 1024).IsLessThan(higher.AllocatedBytes);
    }

    [Test]
    public async Task IncludeOutputQuotaDoesNotAllocateTheRejectedExpansion()
    {
        var document = RegistryJson.Parse(new JsonObject
        {
            ["type"] = "string",
            ["description"] = new string('x', 4096)
        }.ToJsonString());
        var options = new RegistryModelCompilationOptions { Resolver = new DocumentResolver(document) };
        var smaller = MeasureFailure(IncludedAttributes(16), options, 1024) with { Parameter = 16 };
        var larger = MeasureFailure(IncludedAttributes(32), options, 1024) with { Parameter = 32 };
        Export("rejected-includes", [smaller, larger]);

        await Assert.That(smaller.Code).IsEqualTo("byte_limit");
        await Assert.That(larger.Code).IsEqualTo("byte_limit");
        await Assert.That(larger.AllocatedBytes - smaller.AllocatedBytes).IsLessThan(48 * 1024);
    }

    [Test]
    public async Task EffectiveOutputQuotaDoesNotAllocateRepeatedDescriptions()
    {
        var smaller = MeasureFailure(ImportedResources(16, 1024), new(), 1024) with { Parameter = 1024 };
        var larger = MeasureFailure(ImportedResources(16, 4096), new(), 1024) with { Parameter = 4096 };
        Export("rejected-imports", [smaller, larger]);

        await Assert.That(smaller.Code).IsEqualTo("byte_limit");
        await Assert.That(larger.Code).IsEqualTo("byte_limit");
        await Assert.That(larger.AllocatedBytes - smaller.AllocatedBytes).IsLessThan(48 * 1024);
    }

    [Test]
    // The allocation comparison includes shared JSON-parser pools.
    [NotInParallel]
    public async Task AcceptedOutputStorageDoesNotReservePastItsByteLimit()
    {
        var document = RegistryJson.Parse(new JsonObject
        {
            ["type"] = "string",
            ["description"] = new string('x', 4096)
        }.ToJsonString());
        var source = IncludedAttributes(16);
        var options = new RegistryModelCompilationOptions { Resolver = new DocumentResolver(document) };
        var expected = RegistryModel.Compile(source, options).EffectiveModel.RootElement.GetRawText();
        var exactBytes = Encoding.UTF8.GetByteCount(expected);
        var tight = MeasureOutput(source, options with { JsonLimits = options.JsonLimits with { MaxBytes = exactBytes } });
        var generous = MeasureOutput(source, options with { JsonLimits = options.JsonLimits with { MaxBytes = exactBytes * 4 } });

        await Assert.That(tight.Output.RootElement.GetRawText()).IsEqualTo(expected);
        await Assert.That(generous.Output.RootElement.GetRawText()).IsEqualTo(expected);
        await Assert.That(tight.AllocatedBytes + 4096).IsLessThan(generous.AllocatedBytes);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpandedOutputByteLimitIsInclusive(bool escapeHeavy)
    {
        var schema = "https://models.example.test/" +
            (escapeHeavy ? "a<\"\\\u2603\U0001f600" : "") + new string('x', 4096);
        var document = RegistryJson.Parse(new JsonObject
        {
            ["$schema"] = schema,
            ["attributes"] = new JsonObject()
        }.ToJsonString());
        var exactBytes = Encoding.UTF8.GetByteCount(document.RootElement.GetRawText());
        var source = RegistryJson.Parse("""{"$include":"https://models.example.test/root"}""");
        var options = new RegistryModelCompilationOptions
        {
            Resolver = new DocumentResolver(document),
            JsonLimits = new() { MaxBytes = exactBytes }
        };
        var model = RegistryModel.Compile(source, options);

        await Assert.That(model.Groups.Count).IsEqualTo(0);
        await Assert.That(model.Source.RootElement.GetProperty("$include").GetString()).IsEqualTo("https://models.example.test/root");
        var error = TestErrors.Capture(() => RegistryModel.Compile(source,
            options with { JsonLimits = options.JsonLimits with { MaxBytes = exactBytes - 1 } }));
        await Assert.That(error.Code).IsEqualTo("byte_limit");
        await Assert.That(error.Path).IsEqualTo("");

        var extraByte = RegistryJson.Parse(new JsonObject
        {
            ["$schema"] = schema + "x",
            ["attributes"] = new JsonObject()
        }.ToJsonString());
        var overflow = TestErrors.Capture(() => RegistryModel.Compile(source,
            options with { Resolver = new DocumentResolver(extraByte) }));
        await Assert.That(Encoding.UTF8.GetByteCount(extraByte.RootElement.GetRawText())).IsEqualTo(exactBytes + 1);
        await Assert.That(overflow.Code).IsEqualTo("byte_limit");
        await Assert.That(overflow.Path).IsEqualTo("");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(4096)]
    public async Task EffectiveOutputByteLimitIsInclusive(int padding)
    {
        var description = "a<\"\\\u2603\U0001f600" + new string('z', padding);
        var source = RegistryJson.Parse(new JsonObject
        {
            ["description"] = description,
            ["attributes"] = new JsonObject
            {
                ["count"] = new JsonObject { ["type"] = "integer", ["required"] = true, ["default"] = 7 }
            }
        }.ToJsonString());
        var expected = RegistryModel.Compile(source).EffectiveModel.RootElement.GetRawText();
        var exactBytes = Encoding.UTF8.GetByteCount(expected);
        var options = new RegistryModelCompilationOptions { JsonLimits = new() { MaxBytes = exactBytes } };

        await Assert.That(RegistryModel.Compile(source, options).EffectiveModel.RootElement.GetRawText()).IsEqualTo(expected);
        var error = TestErrors.Capture(() => RegistryModel.Compile(source,
            options with { JsonLimits = options.JsonLimits with { MaxBytes = exactBytes - 1 } }));
        await Assert.That(error.Code).IsEqualTo("byte_limit");
        await Assert.That(error.Path).IsEqualTo("");

        var extraByte = JsonNode.Parse(source.RootElement.GetRawText())!.AsObject();
        extraByte["description"] = description + "x";
        var overflowSource = RegistryJson.Parse(extraByte.ToJsonString());
        var overflowBytes = Encoding.UTF8.GetByteCount(RegistryModel.Compile(overflowSource).EffectiveModel.RootElement.GetRawText());
        var overflow = TestErrors.Capture(() => RegistryModel.Compile(overflowSource, options));
        await Assert.That(overflowBytes).IsEqualTo(exactBytes + 1);
        await Assert.That(overflow.Code).IsEqualTo("byte_limit");
        await Assert.That(overflow.Path).IsEqualTo("");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ExhaustedOutputBudgetsNeverReturnAPartialModel(int maxBytes)
    {
        RegistryModel? result = null;
        var error = TestErrors.Capture(() => result = RegistryModel.Compile(RegistryJson.Parse("{}"),
            new() { JsonLimits = new() { MaxBytes = maxBytes } }));

        await Assert.That(result).IsNull();
        await Assert.That(error.Code).IsEqualTo("byte_limit");
        await Assert.That(error.Path).IsEqualTo("");
    }

    [Test]
    public async Task BoundedOutputKeepsExactOwningJsonAcrossLaterCalls()
    {
        RegistryJson included;
        using (var document = JsonDocument.Parse("""
            {"description":"escaped <text> \u2603 \ud83d\ude00 \"quoted\" \\",
             "attributes":{"count":{"type":"integer","required":true,
                "default":900719925474099312345678901234567890,
                "enum":[900719925474099312345678901234567890]}}}
            """))
        {
            included = RegistryJson.FromElement(document.RootElement);
        }

        var bytes = Encoding.UTF8.GetBytes("""{"$include":"https://models.example.test/root"}""");
        var model = RegistryModel.Compile(RegistryJson.Parse(bytes), new() { Resolver = new DocumentResolver(included) });
        var effective = model.EffectiveModel;
        var expected = effective.RootElement.GetRawText();
        Array.Fill(bytes, (byte)' ');
        var rejected = TestErrors.Capture(() => RegistryModel.Compile(ImportedResources(4),
            new() { JsonLimits = new() { MaxBytes = 1024 } }));
        var later = RegistryModel.Compile(ImportedResources(4));

        await Assert.That(rejected.Code).IsEqualTo("byte_limit");
        await Assert.That(later.Groups.Count).IsEqualTo(4);
        await Assert.That(model.Source.RootElement.GetProperty("$include").GetString()).IsEqualTo("https://models.example.test/root");
        await Assert.That(ReferenceEquals(effective, model.EffectiveModel)).IsTrue();
        await Assert.That(effective.RootElement.GetRawText()).IsEqualTo(expected);
        await Assert.That(effective.RootElement.GetProperty("description").GetString())
            .IsEqualTo("escaped <text> \u2603 \U0001f600 \"quoted\" \\");
        await Assert.That(effective.RootElement.GetProperty("attributes").GetProperty("count")
            .GetProperty("default").GetRawText()).IsEqualTo("900719925474099312345678901234567890");
        await Assert.That(effective.RootElement.GetProperty("attributes").GetProperty("count")
            .GetProperty("enum")[0].GetRawText()).IsEqualTo("900719925474099312345678901234567890");
    }

    [Test]
    public async Task IndependentCompilationBudgetsDoNotLeakAcrossCalls()
    {
        var description = new string('x', 4096);
        var document = RegistryJson.Parse(new JsonObject { ["description"] = description }.ToJsonString());
        var source = RegistryJson.Parse("""{"$include":"https://models.example.test/root"}""");
        var options = new RegistryModelCompilationOptions { Resolver = new DocumentResolver(document) };
        var exactBytes = Encoding.UTF8.GetByteCount(RegistryModel.Compile(source, options).EffectiveModel.RootElement.GetRawText());
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            var limited = options with { JsonLimits = options.JsonLimits with { MaxBytes = exactBytes - index % 2 } };
            if (index % 2 == 0)
            {
                var model = RegistryModel.Compile(source, limited);
                return (Code: "", Path: "", Description: model.Annotations.Description);
            }

            var error = TestErrors.Capture(() => RegistryModel.Compile(source, limited));
            return (error.Code, error.Path, Description: (string?)null);
        })));

        for (var index = 0; index < results.Length; index++)
        {
            await Assert.That(results[index].Code).IsEqualTo(index % 2 == 0 ? "" : "byte_limit");
            await Assert.That(results[index].Path).IsEqualTo("");
            await Assert.That(results[index].Description).IsEqualTo(index % 2 == 0 ? description : null);
        }
    }

    [Test]
    public async Task ReentrantCompilationKeepsOuterByteBudget()
    {
        var document = RegistryJson.Parse(new JsonObject { ["description"] = new string('x', 4096) }.ToJsonString());
        var source = RegistryJson.Parse("""{"$include":"https://models.example.test/root"}""");
        var resolver = new ReentrantResolver(document);
        var error = TestErrors.Capture(() => RegistryModel.Compile(source,
            new() { Resolver = resolver, JsonLimits = new() { MaxBytes = 1024 } }));
        var model = RegistryModel.Compile(source, new() { Resolver = resolver });

        await Assert.That(error.Code).IsEqualTo("byte_limit");
        await Assert.That(error.Path).IsEqualTo("");
        await Assert.That(model.Annotations.Description).IsEqualTo(new string('x', 4096));
        await Assert.That(resolver.Calls).IsEqualTo(2);
        await Assert.That(resolver.InnerModel!.Annotations.Description).IsEqualTo("inner");
    }

    private static Profile MeasureFlat(int count)
    {
        var attributes = new JsonObject();
        for (var index = 0; index < count; index++)
        {
            attributes["a" + index.ToString("D4", CultureInfo.InvariantCulture)] = new JsonObject { ["type"] = "string" };
        }

        var source = RegistryJson.Parse(new JsonObject { ["attributes"] = attributes }.ToJsonString());
        _ = RegistryModel.Compile(source);
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var model = RegistryModel.Compile(source);
        var ticks = Stopwatch.GetTimestamp() - started;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        if (model.Attributes["a" + (count - 1).ToString("D4", CultureInfo.InvariantCulture)].Type != RegistryValueType.String)
        {
            throw new InvalidOperationException("The measured compilation did not produce the expected last attribute.");
        }

        GC.KeepAlive(model);
        return new(count, allocated, ticks, "compiled");
    }

    private static Profile MeasureFailure(RegistryJson source, RegistryModelCompilationOptions options, int limit)
    {
        var limited = options with { JsonLimits = options.JsonLimits with { MaxBytes = limit } };
        _ = TestErrors.Capture(() => RegistryModel.Compile(source, limited));
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var error = TestErrors.Capture(() => RegistryModel.Compile(source, limited));
        var ticks = Stopwatch.GetTimestamp() - started;
        return new(limit, GC.GetAllocatedBytesForCurrentThread() - bytesBefore, ticks, error.Code);
    }

    private static (long AllocatedBytes, RegistryJson Output) MeasureOutput(
        RegistryJson source, RegistryModelCompilationOptions options)
    {
        _ = RegistryModel.Compile(source, options);
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var model = RegistryModel.Compile(source, options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        return (allocated, model.EffectiveModel);
    }

    private static RegistryJson IncludedAttributes(int count)
    {
        var attributes = new JsonObject();
        for (var index = 0; index < count; index++)
        {
            attributes["a" + index.ToString(CultureInfo.InvariantCulture)] =
                new JsonObject { ["$include"] = "https://models.example.test/definition" };
        }

        return RegistryJson.Parse(new JsonObject { ["attributes"] = attributes }.ToJsonString());
    }

    private static RegistryJson ImportedResources(int count, int descriptionLength = 4096)
    {
        var groups = new JsonObject();
        for (var index = 0; index < count; index++)
        {
            var group = new JsonObject { ["singular"] = "item" + index.ToString(CultureInfo.InvariantCulture) };
            if (index == 0)
            {
                group["resources"] = new JsonObject
                {
                    ["rs"] = new JsonObject
                    {
                        ["singular"] = "r",
                        ["hasdocument"] = false,
                        ["description"] = new string('x', descriptionLength)
                    }
                };
            }
            else
            {
                group["ximportresources"] = new JsonArray("/g0/rs");
            }

            groups["g" + index.ToString(CultureInfo.InvariantCulture)] = group;
        }

        return RegistryJson.Parse(new JsonObject { ["groups"] = groups }.ToJsonString());
    }

    private static void Export(string scenario, IReadOnlyList<Profile> profiles)
    {
        var lines = new List<string> { "parameter,allocated_bytes,elapsed_ticks,stopwatch_frequency,result" };
        foreach (var profile in profiles)
        {
            var row = FormattableString.Invariant(
                $"{profile.Parameter},{profile.AllocatedBytes},{profile.ElapsedTicks},{Stopwatch.Frequency},{profile.Code}");
            lines.Add(row);
            Console.WriteLine("MODEL-COMPILER-PROFILE," + scenario + "," + row);
        }

        var directory = Environment.GetEnvironmentVariable("XREGISTRY_COMPILER_PROFILE_DIRECTORY");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, scenario + ".csv"), lines, Encoding.UTF8);
        }
    }

    private sealed record Profile(int Parameter, long AllocatedBytes, long ElapsedTicks, string Code);

    private sealed class DocumentResolver(RegistryJson document) : IRegistryModelResolver
    {
        public RegistryJson Resolve(Uri documentUri) => document;
    }

    private sealed class ReentrantResolver(RegistryJson document) : IRegistryModelResolver
    {
        internal int Calls { get; private set; }
        internal RegistryModel? InnerModel { get; private set; }

        public RegistryJson Resolve(Uri documentUri)
        {
            Calls++;
            InnerModel = RegistryModel.Compile(RegistryJson.Parse("""{"description":"inner"}"""));
            return document;
        }
    }
}
