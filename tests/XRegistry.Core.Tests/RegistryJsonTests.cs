using System.Numerics;
using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryJsonTests
{
    [Test]
    public async Task OwnsInputAndPreservesExactNumbersAndPresence()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"huge":900719925474099312345678901234567890,"nil":null,"empty":"","array":[]}""");
        var value = RegistryJson.Parse(bytes);
        Array.Fill(bytes, (byte)' ');

        await Assert.That(value.RootElement.GetProperty("huge").GetRawText())
            .IsEqualTo("900719925474099312345678901234567890");
        await Assert.That(RegistryNumber.FromElement(value.RootElement.GetProperty("huge")).ToBigInteger())
            .IsEqualTo(BigInteger.Parse("900719925474099312345678901234567890",
                System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(value.GetPresence("missing")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(value.GetPresence("nil")).IsEqualTo(JsonPresence.Null);
        await Assert.That(value.GetPresence("empty")).IsEqualTo(JsonPresence.Value);
        await Assert.That(value.RootElement.GetProperty("empty").GetString()).IsEqualTo("");
        await Assert.That(value.RootElement.GetProperty("array").GetArrayLength()).IsEqualTo(0);
        await Assert.That(value.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    public async Task CloningElementOutlivesDisposedSourceWithoutNumericNarrowing()
    {
        RegistryJson value;
        using (var source = JsonDocument.Parse("""{"n":1.23000000000000000000000000000000000000001e-12}"""))
        {
            value = RegistryJson.FromElement(source.RootElement);
        }

        await Assert.That(value.RootElement.GetProperty("n").GetRawText())
            .IsEqualTo("1.23000000000000000000000000000000000000001e-12");
    }

    [Test]
    [Arguments("""{"a":1,"a":2}""", "/a")]
    [Arguments("""{"outer":[{"a/b~c":1,"a\u002fb~c":2}]}""", "/outer/0/a~1b~0c")]
    [Arguments("""{"a":{"b":{"x":null,"x":true}}}""", "/a/b/x")]
    public async Task RejectsDuplicateMembersAtEveryDepth(string json, string path)
    {
        var error = TestErrors.Capture(() => RegistryJson.Parse(json));
        await Assert.That(error.Code).IsEqualTo("duplicate_member");
        await Assert.That(error.Path).IsEqualTo(path);
    }

    [Test]
    public async Task DuplicateNamesAreOrdinalAndNotCaseFolded()
    {
        var json = RegistryJson.Parse("""{"A":1,"a":2}""");
        await Assert.That(json.RootElement.GetProperty("A").GetInt32()).IsEqualTo(1);
        await Assert.That(json.RootElement.GetProperty("a").GetInt32()).IsEqualTo(2);
    }

    [Test]
    [Arguments("\"\\uD800\"", "invalid_unicode")]
    [Arguments("\"\\uDC00\"", "invalid_unicode")]
    [Arguments("\"\\uD800\\u0041\"", "invalid_unicode")]
    [Arguments("", "invalid_json")]
    [Arguments("{}", "none")]
    [Arguments("{}{}", "invalid_json")]
    [Arguments("[1,]", "invalid_json")]
    [Arguments("{/*comment*/}", "invalid_json")]
    [Arguments("01", "invalid_json")]
    [Arguments("NaN", "invalid_json")]
    public async Task RejectsInvalidJsonAndUnicodeEscapes(string json, string code)
    {
        if (code == "none")
        {
            await Assert.That(RegistryJson.Parse(json).RootElement.EnumerateObject().Count()).IsEqualTo(0);
            return;
        }

        await Assert.That(TestErrors.Capture(() => RegistryJson.Parse(json)).Code).IsEqualTo(code);
    }

    [Test]
    public async Task RejectsInvalidUtf8WithoutReplacement()
    {
        byte[] invalid = [0x22, 0xc0, 0xaf, 0x22];
        await Assert.That(TestErrors.Capture(() => RegistryJson.Parse(invalid)).Code).IsEqualTo("invalid_utf8");
        await Assert.That(TestErrors.Capture(() => RegistryJson.Parse("\"\ud800\"")).Code)
            .IsEqualTo("invalid_unicode");
        await Assert.That(RegistryJson.Parse("\"\\uD83D\\uDE00\"").RootElement.GetString())
            .IsEqualTo(char.ConvertFromUtf32(0x1f600));
        await Assert.That(RegistryJson.Parse("\"\\\\uD800\"").RootElement.GetString()).IsEqualTo("\\uD800");
    }

    [Test]
    public async Task EnforcesByteDepthNodeAndNumberBoundaries()
    {
        var limits = new RegistryJsonLimits { MaxBytes = 3 };
        await Assert.That(RegistryJson.Parse("\"a\"", limits).RootElement.GetString()).IsEqualTo("a");
        await Assert.That(TestErrors.Capture(() => RegistryJson.Parse("\"ab\"", limits)).Code).IsEqualTo("byte_limit");
        await Assert.That(RegistryJson.Parse("[[]]", new() { MaxDepth = 2 }).RootElement.GetArrayLength()).IsEqualTo(1);
        await Assert.That(TestErrors.Capture(() => RegistryJson.Parse("[[[]]]", new() { MaxDepth = 2 })).Code)
            .IsEqualTo("depth_limit");
        await Assert.That(RegistryJson.Parse("[1,2]", new() { MaxNodes = 3 }).RootElement[1].GetInt32()).IsEqualTo(2);
        var nodes = TestErrors.Capture(() => RegistryJson.Parse("[1,2,3]", new() { MaxNodes = 3 }));
        await Assert.That(nodes.Code).IsEqualTo("node_limit");
        await Assert.That(nodes.Path).IsEqualTo("/2");
        await Assert.That(RegistryJson.Parse("123", new() { MaxNumberCharacters = 3 }).RootElement.GetInt32()).IsEqualTo(123);
        await Assert.That(TestErrors.Capture(() => RegistryJson.Parse("1234", new() { MaxNumberCharacters = 3 })).Code)
            .IsEqualTo("number_limit");
        await Assert.That(RegistryNumber.Parse("1e5", new() { MaxNumberExponent = 5 }).ToBigInteger())
            .IsEqualTo(new BigInteger(100000));
        await Assert.That(TestErrors.Capture(() => RegistryJson.Parse("1e6", new() { MaxNumberExponent = 5 })).Code)
            .IsEqualTo("number_limit");
        await Assert.That(TestErrors.Capture(() => RegistryJson.Parse("1e-6", new() { MaxNumberExponent = 5 })).Code)
            .IsEqualTo("number_limit");
        await Assert.That(RegistryNumber.Parse("0e000000", new() { MaxNumberExponent = 0 }).ToBigInteger())
            .IsEqualTo(BigInteger.Zero);
    }

    [Test]
    [Arguments("1", "1.00", true)]
    [Arguments("1e3", "1000", true)]
    [Arguments("-0", "0.000", true)]
    [Arguments("9007199254740992", "9007199254740993", false)]
    [Arguments("0.10000000000000000000001", "0.1", false)]
    [Arguments("-12.30e-1", "-1.2300", true)]
    public async Task ExactNumbersCompareWithoutFloatingPointNarrowing(string left, string right, bool equal)
    {
        await Assert.That(RegistryNumber.Parse(left).Equals(RegistryNumber.Parse(right))).IsEqualTo(equal);
    }

    [Test]
    public async Task FractionalNumbersCannotBeCoercedToIntegers()
    {
        var number = RegistryNumber.Parse("1.01");
        await Assert.That(number.Significand).IsEqualTo(new BigInteger(101));
        await Assert.That(number.Exponent).IsEqualTo(-2);
        await Assert.That(number.IsInteger).IsFalse();
        await Assert.That(() => number.ToBigInteger()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task AsyncIngestionIsBoundedCancellableAndLeavesStreamOpen()
    {
        using var stream = new MemoryStream("[1,2]"u8.ToArray());
        var value = await RegistryJson.ParseAsync(stream, new() { MaxBytes = 5 });
        await Assert.That(value.RootElement[1].GetInt32()).IsEqualTo(2);
        await Assert.That(stream.CanRead).IsTrue();
        stream.Position = 0;
        await Assert.That(async () => await RegistryJson.ParseAsync(stream, new() { MaxBytes = 4 }))
            .Throws<RegistryException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        stream.Position = 0;
        await Assert.That(async () => await RegistryJson.ParseAsync(stream, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }
}
