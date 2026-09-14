using System.Runtime.CompilerServices;
using System.Text;
using TUnit.Assertions;
using XRegistry.Queries;

namespace XRegistry.Core.Tests;

internal static class QueryFixture
{
    internal static readonly Uri Root = new("https://public.example/catalog");
    internal static readonly RegistryModel Model = RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"fleets":{"singular":"fleet","attributes":{
          "rank":{"type":"decimal"},"active":{"type":"boolean"},"at":{"type":"timestamp"},
          "info":{"type":"object","attributes":{"owner":{"type":"string"},
            "reviewers":{"type":"array","item":{"type":"string"}},
            "addresses":{"type":"map","item":{"type":"object","attributes":{"state":{"type":"string"}}}}}},
          "kind":{"type":"string","ifvalues":{
            "clock":{"siblingattributes":{"when":{"type":"timestamp"}}},
            "text":{"siblingattributes":{"when":{"type":"string"}}}}}
          },"resources":{"items":{"singular":"item","hasdocument":false,
            "attributes":{"rank":{"type":"decimal"},"active":{"type":"boolean"}}},
            "docs":{"singular":"doc"}}}}}
        """));
    internal static readonly RegistryModel SmallModel = RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"entries":{"singular":"entry"}}}
        """));

    internal static MemoryQuerySource Groups()
    {
        var source = new MemoryQuerySource();
        source.Add("/", """{"registryid":"query","name":"Root"}""");
        source.Add("/fleets/a", """
            {"fleetid":"a","name":"Alpha","rank":2,"active":false,"at":"2025-01-01T00:00:00Z",
             "info":{"owner":"Joe","reviewers":["Mary","STEVE"],"addresses":{"home.office":{"state":"CA"},"*":{"state":"NY"}}}}
            """);
        source.Add("/fleets/b", """{"fleetid":"b","name":"Beta","rank":10,"active":true,"at":"2024-12-31T23:59:59Z"}""");
        source.Add("/fleets/c", """{"fleetid":"c","name":"ALPHA","rank":9007199254740993,"active":true}""");
        source.Add("/fleets/d", """{"fleetid":"d","description":"","rank":null}""");
        source.Add("/fleets/e", """{"fleetid":"e","name":"Delta"}""");
        return source;
    }

    internal static MemoryQuerySource Tree()
    {
        var source = Groups();
        source.Resource("/fleets/a/items/x", """{"defaultversionid":"1","readonly":false}""",
            """{"versionid":"1","name":"match","rank":2,"epoch":7}""");
        source.Resource("/fleets/a/items/y", """{"defaultversionid":"1","readonly":true}""",
            """{"versionid":"1","name":"other","rank":10}""");
        source.Resource("/fleets/b/items/z", """{"defaultversionid":"1","readonly":false}""",
            """{"versionid":"1","name":"match","rank":10}""");
        return source;
    }

    internal static MemoryQuerySource Documents()
    {
        var source = new MemoryQuerySource();
        source.Resource("/fleets/g/docs/json", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"application/json"}""", Encoding.UTF8.GetBytes("""{"a":9007199254740993}"""));
        source.Resource("/fleets/g/docs/text", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"text/plain"}""", Encoding.UTF8.GetBytes("hello"));
        source.Resource("/fleets/g/docs/binary", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"application/octet-stream"}""", Encoding.UTF8.GetBytes("""{"a":1}"""));
        source.Resource("/fleets/g/docs/invalid", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"text/plain"}""", new byte[] { 0xfe, 0xff });
        source.Resource("/fleets/g/docs/empty", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"text/plain"}""", ReadOnlyMemory<byte>.Empty);
        source.Resource("/fleets/g/docs/url", """{"defaultversionid":"1"}""",
            """{"versionid":"1","contenttype":"application/json","docurl":"https://unfetched.invalid/private"}""");
        return source;
    }

    internal static ValueTask<RegistryQuerySelection> Evaluate(MemoryQuerySource source, RegistryQueryBudget budget,
        string path = "/fleets", string[]? filters = null, string? sort = null, RegistryModel? model = null,
        bool defaultSort = false) => RegistryQuery.EvaluateAsync(model ?? Model, source,
            new(RegistryPath.Parse(path), Root) { Filters = filters ?? [], Sort = sort, DefaultSortById = defaultSort }, budget);

    internal static string Ids(RegistryQuerySelection result) => string.Join(',', result.RootPaths.Select(path => path.Kind switch
    {
        RegistryPathKind.Group => path.GroupId!.Value,
        RegistryPathKind.Resource => path.ResourceId!.Value,
        RegistryPathKind.Version => path.VersionId!.Value,
        _ => path.EscapedPath
    }));

    internal static async Task ExpectCode(Func<ValueTask<RegistryQuerySelection>> operation, string code)
    {
        RegistryDiagnostic? failure = null;
        try
        {
            await operation();
        }
        catch (RegistryException exception)
        {
            failure = exception.Diagnostic;
        }

        await Assert.That(failure?.Code).IsEqualTo(code);
    }
}

internal sealed class MemoryQuerySource : IRegistryQuerySource
{
    internal Dictionary<string, RegistryQueryEntity> Entities { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string[]> Collections { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Hidden { get; } = new(StringComparer.Ordinal);
    internal List<string> DocumentReads { get; } = [];

    internal void Add(string path, string metadata) => Entities[path] = new(RegistryJson.Parse(metadata));

    internal void Resource(string path, string meta, string version, ReadOnlyMemory<byte>? document = null)
    {
        Add(path, meta);
        Add(path + "/meta", meta);
        var value = RegistryJson.Parse(version);
        var id = value.RootElement.GetProperty("versionid").GetString()!;
        Entities[path + "/versions/" + Uri.EscapeDataString(id)] = new(value) { Document = document };
    }

    public ValueTask<RegistryQueryEntity?> ReadEntityAsync(RegistryPath path, bool includeDocument,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Hidden.Contains(path.EscapedPath))
        {
            return ValueTask.FromResult<RegistryQueryEntity?>(null);
        }

        if (!Entities.TryGetValue(path.EscapedPath, out var entity))
        {
            throw new RegistryException(new("query_source_incomplete", path.EscapedPath, "A selected entity is missing."));
        }

        if (includeDocument)
        {
            DocumentReads.Add(path.EscapedPath);
        }

        return ValueTask.FromResult<RegistryQueryEntity?>(new(entity.Metadata)
        {
            Document = includeDocument ? entity.Document : null,
            DefaultVersionId = entity.DefaultVersionId,
            ShortSelf = entity.ShortSelf,
            IsDanglingCrossReference = entity.IsDanglingCrossReference
        });
    }

    public async IAsyncEnumerable<RegistryPath> GetChildrenAsync(RegistryPath collection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        if (Hidden.Contains(collection.EscapedPath))
        {
            yield break;
        }

        var prefix = collection.EscapedPath + "/";
        var paths = Collections.TryGetValue(collection.EscapedPath, out var supplied) ? supplied :
            Entities.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && !key[prefix.Length..].Contains('/')).ToArray();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Hidden.Contains(path))
            {
                yield return RegistryPath.Parse(path);
            }
        }
    }
}
