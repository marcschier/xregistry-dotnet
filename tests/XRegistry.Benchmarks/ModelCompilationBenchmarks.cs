using BenchmarkDotNet.Attributes;
using XRegistry.Models;

namespace XRegistry.Benchmarks;

[MemoryDiagnoser]
public class ModelCompilationBenchmarks
{
    private RegistryJson _source = null!;

    [GlobalSetup]
    public void Setup()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Registry);
        _source = RegistryJson.FromElement(source.RootElement);
        if (RegistryModel.Compile(_source).Groups["categories"].Resources["registries"].HasDocument)
        {
            throw new InvalidOperationException("The catalog benchmark fixture must remain documentless.");
        }
    }

    [Benchmark]
    public RegistryModel CompileCatalog() => RegistryModel.Compile(_source);
}
