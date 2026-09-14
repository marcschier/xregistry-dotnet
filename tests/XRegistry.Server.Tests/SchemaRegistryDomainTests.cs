using System.Globalization;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class SchemaRegistryDomainTests
{
    [Test]
    public async Task PackagedSchemaModelAllowsDifferentFormatsWithinAnUnconstrainedGroup()
    {
        var engine = SchemaEngine();
        var json = await Send(engine, RegistryAction.Replace, "/schemagroups/g/schemas/json$details",
            """{"format":"JsonSchema/draft-07","schema":{"type":"string"}}""");
        const string proto = "syntax = \"proto3\"; message Event { string name = 1; }";
        var protobuf = await Send(engine, RegistryAction.Replace, "/schemagroups/g/schemas/proto$details",
            """{"format":"Protobuf/3","contenttype":"text/plain","schema":"syntax = \"proto3\"; message Event { string name = 1; }"}""");
        await Assert.That(json.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(protobuf.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        var group = (await Send(engine, RegistryAction.Read, "/schemagroups/g")).Metadata!.RootElement;
        await Assert.That(group.GetProperty("schemascount").GetInt32()).IsEqualTo(2);
        await Assert.That(group.TryGetProperty("format", out _)).IsFalse();
        var document = await Send(engine, RegistryAction.Read, "/schemagroups/g/schemas/proto");
        using var content = document.Document!.OpenRead();
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        await Assert.That(Encoding.UTF8.GetString(copy.ToArray())).IsEqualTo(proto);
        await Assert.That(document.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
    }

    [Test]
    public async Task SchemaGroupFormatAndInstanceDefaultConstrainEverySchemaWithoutPartialPublication()
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = SchemaEngine(persistence);
        await Send(engine, RegistryAction.Replace, "/schemagroups/g", """
            {"format":"JsonSchema/draft-07","constraints":{"schemas.format":{"default":"JsonSchema/draft-07"}}}
            """);
        var created = await Send(engine, RegistryAction.Replace, "/schemagroups/g/schemas/s$details",
            """{"schema":{"type":"string"}}""");
        await Assert.That(created.Metadata!.RootElement.GetProperty("format").GetString()).IsEqualTo("JsonSchema/draft-07");
        await Assert.That(created.Metadata.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        using var before = await persistence.ReadSnapshotAsync();
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/schemagroups/g/schemas/s$details",
            """{"format":"Avro/1.11.0","schema":"string"}"""), "constraint_failure");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/schemagroups/g",
            """{"format":"Avro/1.11.0"}"""), "constraint_failure");
        using var after = await persistence.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        var schema = (await Send(engine, RegistryAction.Read, "/schemagroups/g/schemas/s$details")).Metadata!.RootElement;
        await Assert.That(schema.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        await Assert.That(schema.GetProperty("format").GetString()).IsEqualTo("JsonSchema/draft-07");
        await Assert.That((await Send(engine, RegistryAction.Read, "/schemagroups/g")).Metadata!.RootElement
            .GetProperty("format").GetString()).IsEqualTo("JsonSchema/draft-07");
    }

    [Test]
    public async Task SchemaVersionsUseTheDefaultMonotonicAlgorithmAcrossTheDecimalWidthBoundary()
    {
        var engine = SchemaEngine();
        for (var number = 1; number <= 11; number++)
        {
            var result = await Send(engine, number == 1 ? RegistryAction.Replace : RegistryAction.Post,
                "/schemagroups/g/schemas/com.example.event.v1$details",
                """{"format":"Avro/1.11.0","schema":"string"}""");
            await Assert.That(result.Metadata!.RootElement.GetProperty("versionid").GetString())
                .IsEqualTo(number.ToString(CultureInfo.InvariantCulture));
            await Assert.That(result.Metadata.RootElement.GetProperty("ancestorid").GetString())
                .IsEqualTo(Math.Max(1, number - 1).ToString(CultureInfo.InvariantCulture));
        }
        var current = (await Send(engine, RegistryAction.Read, "/schemagroups/g/schemas/com.example.event.v1$details")).Metadata!.RootElement;
        await Assert.That(current.GetProperty("versionid").GetString()).IsEqualTo("11");
        await Assert.That(current.GetProperty("versionscount").GetInt32()).IsEqualTo(11);
        await Send(engine, RegistryAction.Delete, "/schemagroups/g/schemas/com.example.event.v1/versions/11");
        var retained = (await Send(engine, RegistryAction.Read, "/schemagroups/g/schemas/com.example.event.v1$details")).Metadata!.RootElement;
        await Assert.That(retained.GetProperty("versionid").GetString()).IsEqualTo("10");
        var next = await Send(engine, RegistryAction.Post, "/schemagroups/g/schemas/com.example.event.v1$details",
            """{"format":"Avro/1.11.0","schema":"string"}""");
        await Assert.That(next.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("12");
    }

    [Test]
    public async Task SchemaWildcardExtensionsRoundTripWithoutRewritingExactValuesOrDocumentBytes()
    {
        var engine = SchemaEngine();
        await Send(engine, RegistryAction.Replace, "/schemagroups/g/schemas/s$details", """
            {"format":"JsonSchema/draft-07","schema":{"type":"string"},
             "vendor":{"sequence":184467440737095516160001,"values":[false,"",{"name":"preserved"}]}}
            """);
        await Send(engine, RegistryAction.Patch, "/schemagroups/g/schemas/s$details", """{"description":"metadata only"}""");
        var metadata = (await Send(engine, RegistryAction.Read, "/schemagroups/g/schemas/s$details")).Metadata!.RootElement;
        await Assert.That(metadata.GetProperty("vendor").GetProperty("sequence").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.GetProperty("vendor").GetProperty("values")[0].GetBoolean()).IsFalse();
        await Assert.That(metadata.GetProperty("vendor").GetProperty("values")[1].GetString()).IsEqualTo("");
        await Assert.That(metadata.GetProperty("vendor").GetProperty("values")[2].GetProperty("name").GetString()).IsEqualTo("preserved");
        var document = await Send(engine, RegistryAction.Read, "/schemagroups/g/schemas/s");
        using var content = document.Document!.OpenRead();
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        await Assert.That(Encoding.UTF8.GetString(copy.ToArray())).IsEqualTo("""{"type":"string"}""");
    }

    [Test]
    public async Task BreakingSchemaEvolutionRequiresADifferentResourceUnderTheDeclaredPolicy()
    {
        var engine = SchemaEngine();
        await Send(engine, RegistryAction.Replace, "/schemagroups/g/schemas/event.v1$details", """
            {"format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"forward"}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/schemagroups/g/schemas/event.v1$details",
            """{"format":"Avro/1.11.0","schema":"long"}"""), "compatibility_violation");
        var replacement = await Send(engine, RegistryAction.Replace, "/schemagroups/g/schemas/event.v2$details", """
            {"format":"Avro/1.11.0","schema":"long","meta":{"compatibility":"forward"}}
            """);
        await Assert.That(replacement.Metadata!.RootElement.GetProperty("schemaid").GetString()).IsEqualTo("event.v2");
        await Assert.That(replacement.Metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(replacement.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        var original = (await Send(engine, RegistryAction.Read, "/schemagroups/g/schemas/event.v1$details")).Metadata!.RootElement;
        await Assert.That(original.GetProperty("versionid").GetString()).IsEqualTo("1");
        await Assert.That(original.GetProperty("versionscount").GetInt32()).IsEqualTo(1);
        await Assert.That((await Send(engine, RegistryAction.Read, "/schemagroups/g")).Metadata!.RootElement
            .GetProperty("schemascount").GetInt32()).IsEqualTo(2);
    }

    private static RegistryEngine SchemaEngine(IRegistryPersistence? persistence = null) => new(new()
    {
        RegistryId = "schema-domain",
        PublicRoot = new Uri("https://registry.example/schema"),
        Model = BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
        ResourceValidator = new BuiltInRegistryResourceValidator()
    }, persistence ?? new InMemoryRegistryPersistence(), new PermitPolicy());
}
