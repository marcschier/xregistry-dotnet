using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class CatalogPriorityValueTests
{
    [Test]
    [Arguments("0.0", "0", 0)]
    [Arguments("-0.00e5", "0", 0)]
    [Arguments("100e-2", "1.0", 0)]
    [Arguments("9007199254740993.00", "9007199254740992e0", 1)]
    public async Task ExactPriorityValuesOrderWithoutFloatingPointOrLexicalNarrowing(string first, string second, int index)
    {
        var description = CatalogDescription.Parse(Encoding.UTF8.GetBytes($$"""
            {"federationprofiles":[
              {"name":"http","endpoint":"https://first.example/","priority":{{first}}},
              {"name":"http","endpoint":"https://second.example/","priority":{{second}}}]}
            """));
        var selected = description.Select(new(["http"]));
        await Assert.That(selected.OriginalIndex).IsEqualTo(index);
        await Assert.That(description.Advertisements[0].Data.GetProperty("priority").GetRawText()).IsEqualTo(first);
        await Assert.That(description.Advertisements[1].Data.GetProperty("priority").GetRawText()).IsEqualTo(second);
    }
}
