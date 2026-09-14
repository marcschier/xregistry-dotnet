using System.Text;
using BenchmarkDotNet.Attributes;
using XRegistry.Http;

namespace XRegistry.Benchmarks;

[MemoryDiagnoser]
public class RegistryParsingBenchmarks
{
    private byte[] _metadata = [];
    private string _header = "";

    [Params(128, 8192, 262144)]
    public int PayloadCharacters { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var text = new string('x', PayloadCharacters);
        _metadata = Encoding.UTF8.GetBytes("{\"epoch\":184467440737095516160,\"payload\":\"" + text + "\"}");
        _header = "Euro \u20ac " + text;
        var parsed = RegistryJson.Parse(_metadata);
        if (parsed.RootElement.GetProperty("payload").GetString()!.Length != PayloadCharacters ||
            parsed.RootElement.GetProperty("epoch").GetRawText() != "184467440737095516160")
        {
            throw new InvalidOperationException("The benchmark fixture does not preserve its expected value.");
        }
    }

    [Benchmark]
    public RegistryJson ParseMetadata() => RegistryJson.Parse(_metadata);

    [Benchmark]
    public string EncodeHeader() => RegistryHeaderEncoding.Encode(_header, 1024 * 1024);
}
