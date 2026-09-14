using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class CloudEventsRegistryDomainTests
{
    [Test]
    [Arguments("{}", "")]
    [Arguments("""{"endpoints":{"one":{"usage":["consumer"],"protocol":"HTTP"}}}""", "endpoints")]
    [Arguments("""{"messagegroups":{"one":{}}}""", "messagegroups")]
    [Arguments("""{"schemagroups":{"one":{}}}""", "schemagroups")]
    public async Task CloudEventsRegistryExportsOneJsonObjectWithIndependentlyOptionalSubregistries(string metadata, string selected)
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "cloudevents-domain",
            PublicRoot = new Uri("https://registry.example/events"),
            Model = BuiltInRegistryModels.Compile(RegistryModelKind.CloudEvents)
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/", metadata);
        var export = (await Send(engine, RegistryAction.Read, "/export")).Metadata!;
        var parsed = RegistryJson.Parse(export.RootElement.GetRawText()).RootElement;
        await Assert.That(parsed.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(parsed.GetProperty("registryid").GetString()).IsEqualTo("cloudevents-domain");
        await Assert.That(parsed.GetProperty("specversion").GetString()).IsEqualTo("1.0-rc4");
        foreach (var name in new[] { "endpoints", "messagegroups", "schemagroups" })
        {
            var expected = name == selected ? 1 : 0;
            await Assert.That(parsed.GetProperty(name + "count").GetInt32()).IsEqualTo(expected);
            var count = parsed.TryGetProperty(name, out var groups) ? groups.EnumerateObject().Count() : 0;
            await Assert.That(count).IsEqualTo(expected);
            if (expected != 0) { await Assert.That(groups.EnumerateObject().Single().Name).IsEqualTo("one"); }
        }
    }
}
