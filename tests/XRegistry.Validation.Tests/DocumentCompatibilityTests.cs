// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class DocumentCompatibilityTests
{
    [Test]
    [Arguments("full", """{"type":"int","logicalType":"date"}""", "\"int\"", DocumentCompatibilityStatus.Compatible)]
    [Arguments("full", "\"long\"", """{"type":"long","logicalType":"timestamp-micros"}""", DocumentCompatibilityStatus.Compatible)]
    [Arguments("full", """{"type":"long","logicalType":"timestamp-millis"}""",
        """{"type":"long","logicalType":"future"}""", DocumentCompatibilityStatus.Compatible)]
    [Arguments("full", """{"type":"int","logicalType":"uuid"}""", "\"int\"", DocumentCompatibilityStatus.Compatible)]
    [Arguments("full", """{"type":"string","logicalType":"uuid"}""", "\"string\"", DocumentCompatibilityStatus.Compatible)]
    [Arguments("backward", """{"type":"long","logicalType":"future"}""", "\"int\"", DocumentCompatibilityStatus.Compatible)]
    [Arguments("forward", """{"type":"long","logicalType":"future"}""", "\"int\"", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("full", """{"type":"string","logicalType":"future"}""", "\"int\"", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("full", """{"type":"fixed","name":"D","size":12,"logicalType":"duration"}""",
        """{"type":"fixed","name":"D","size":11}""", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("full", """{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""", "\"bytes\"",
        DocumentCompatibilityStatus.Compatible)]
    [Arguments("full", "\"bytes\"", """{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""",
        DocumentCompatibilityStatus.Compatible)]
    public async Task AvroLogicalAnnotationsPreserveUnderlyingReaderWriterDirectionAndDecimalBoundary(
        string mode, string candidate, string ancestor, DocumentCompatibilityStatus expected)
    {
        foreach (var format in new[] { "Avro/1.8.2", "Avro/1.11.0" })
        {
            var result = await new BuiltInDocumentCompatibilityValidator().CheckAsync(
                format, mode, Bytes(candidate), [Bytes(ancestor)]);
            await Assert.That(result.Status).IsEqualTo(expected);
            if (expected == DocumentCompatibilityStatus.Compatible)
            {
                await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            }
            else
            {
                await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(
                    expected == DocumentCompatibilityStatus.Unsupported ? "avro.logical_type" : "compatibility.avro");
            }
        }
    }

    [Test]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""",
        """{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""", DocumentCompatibilityStatus.Compatible)]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":4}""",
        """{"type":"bytes","logicalType":"decimal","precision":4,"scale":0}""", DocumentCompatibilityStatus.Compatible)]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":5,"scale":2}""",
        """{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":4,"scale":1}""",
        """{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":1000000000000000000000000000000,"scale":999999999999999999999999999999}""",
        """{"type":"bytes","logicalType":"decimal","precision":1000000000000000000000000000000,"scale":999999999999999999999999999999}""",
        DocumentCompatibilityStatus.Compatible)]
    [Arguments("""{"type":"bytes","logicalType":"decimal","precision":1000000000000000000000000000001}""",
        """{"type":"bytes","logicalType":"decimal","precision":1000000000000000000000000000000}""",
        DocumentCompatibilityStatus.Incompatible)]
    [Arguments("""{"type":"fixed","name":"D","size":2,"logicalType":"decimal","precision":4}""",
        """{"type":"fixed","name":"D","size":2,"logicalType":"decimal","precision":3}""", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("""{"type":"fixed","name":"D","size":2,"logicalType":"decimal","precision":5}""",
        """{"type":"fixed","name":"D","size":2,"logicalType":"decimal","precision":3}""", DocumentCompatibilityStatus.Compatible)]
    [Arguments("""{"type":"fixed","name":"D","size":1,"logicalType":"decimal","precision":2}""",
        """{"type":"fixed","name":"D","size":1,"logicalType":"decimal","precision":1}""", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("""{"type":"fixed","name":"D","size":1,"logicalType":"decimal","precision":3}""",
        """{"type":"fixed","name":"D","size":1,"logicalType":"decimal","precision":1}""", DocumentCompatibilityStatus.Compatible)]
    public async Task ValidDecimalPrecisionAndScaleMustMatchInBothReaderWriterDirections(
        string candidate, string ancestor, DocumentCompatibilityStatus expected)
    {
        foreach (var format in new[] { "Avro/1.8.2", "Avro/1.11.0" })
        {
            foreach (var mode in new[] { "backward", "forward", "full" })
            {
                var result = await new BuiltInDocumentCompatibilityValidator().CheckAsync(
                    format, mode, Bytes(candidate), [Bytes(ancestor)]);
                await Assert.That(result.Status).IsEqualTo(expected);
                await Assert.That(result.Diagnostics.Count).IsEqualTo(
                    expected == DocumentCompatibilityStatus.Compatible ? 0 : 1);
            }
        }
    }

    [Test]
    [Arguments("""{}""")]
    [Arguments("""{"precision":0}""")]
    [Arguments("""{"precision":-1}""")]
    [Arguments("""{"precision":4,"scale":-1}""")]
    [Arguments("""{"precision":4,"scale":5}""")]
    [Arguments("""{"precision":4,"scale":null}""")]
    [Arguments("""{"precision":4,"scale":1.5}""")]
    [Arguments("""{"precision":"4"}""")]
    [Arguments("""{"precision":null}""")]
    [Arguments("""{"precision":true}""")]
    [Arguments("""{"precision":4.5}""")]
    public async Task InvalidDecimalParametersFallBackToUnderlyingCompatibility(string parameters)
    {
        var members = parameters[1..^1];
        var candidate = """{"type":"bytes","logicalType":"decimal" """ +
            (members.Length == 0 ? "" : "," + members) + "}";
        foreach (var format in new[] { "Avro/1.8.2", "Avro/1.11.0" })
        {
            var result = await new BuiltInDocumentCompatibilityValidator().CheckAsync(format, "full",
                Bytes(candidate), [Bytes("""{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""")]);
            await Assert.That(result.Status).IsEqualTo(DocumentCompatibilityStatus.Compatible);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(1, 2)]
    [Arguments(2, 4)]
    [Arguments(4, 9)]
    [Arguments(8, 18)]
    [Arguments(16, 38)]
    public async Task FixedDecimalCapacityUsesTheExactSignedByteBoundary(int size, int maximumPrecision)
    {
        foreach (var precision in new[] { maximumPrecision - 1, maximumPrecision, maximumPrecision + 1 })
        {
            if (precision < 1) { continue; }
            var candidate = $$"""{"type":"fixed","name":"D","size":{{size}},"logicalType":"decimal","precision":{{precision}}}""";
            var ancestor = $$"""{"type":"fixed","name":"D","size":{{size}},"logicalType":"decimal","precision":1}""";
            var result = await new BuiltInDocumentCompatibilityValidator().CheckAsync(
                "Avro/1.11.0", "full", Bytes(candidate), [Bytes(ancestor)]);
            var bothValid = size > 0 && precision <= maximumPrecision;
            await Assert.That(result.Status).IsEqualTo(bothValid && precision != 1
                ? DocumentCompatibilityStatus.Incompatible : DocumentCompatibilityStatus.Compatible);
        }
    }

    [Test]
    public async Task DecimalCompatibilityChecksReachUnionAndRecursiveRecordMembers()
    {
        var ancestor = Bytes("""
            {"type":"record","name":"R","fields":[
              {"name":"amount","type":["null",{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}],"default":null},
              {"name":"next","type":["null","R"],"default":null}]}
            """);
        var candidate = Bytes("""
            {"type":"record","name":"R","fields":[
              {"name":"amount","type":["null",{"type":"bytes","logicalType":"decimal","precision":4,"scale":1}],"default":null},
              {"name":"next","type":["null","R"],"default":null}]}
            """);
        var result = await new BuiltInDocumentCompatibilityValidator().CheckAsync(
            "Avro/1.11.0", "backward_transitive", candidate, [ancestor]);
        await Assert.That(result.Status).IsEqualTo(DocumentCompatibilityStatus.Incompatible);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("compatibility.avro");
    }

    [Test]
    [Arguments("backward", "\"long\"", "\"int\"", DocumentCompatibilityStatus.Compatible)]
    [Arguments("forward", "\"long\"", "\"int\"", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("full", "\"long\"", "\"int\"", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("full", "\"string\"", "\"bytes\"", DocumentCompatibilityStatus.Compatible)]
    [Arguments("backward", """["null","long"]""", "\"int\"", DocumentCompatibilityStatus.Compatible)]
    [Arguments("backward", "\"int\"", """["null","int"]""", DocumentCompatibilityStatus.Incompatible)]
    [Arguments("backward", """{"type":"fixed","name":"F","size":3}""", """{"type":"fixed","name":"F","size":2}""", DocumentCompatibilityStatus.Incompatible)]
    public async Task AvroPromotionAndUnionDirectionAreNotSymmetric(
        string mode, string current, string previous, DocumentCompatibilityStatus expected)
    {
        var result = await new BuiltInDocumentCompatibilityValidator().CheckAsync(
            "Avro/1.11.0", mode, Bytes(current), [Bytes(previous)]);
        await Assert.That(result.Status).IsEqualTo(expected);
    }

    [Test]
    public async Task AddedReaderFieldNeedsADefaultAndForwardPolicyUsesTheOppositeReader()
    {
        var previous = Bytes("""{"type":"record","name":"R","fields":[{"name":"id","type":"int"}]}""");
        var withoutDefault = Bytes("""{"type":"record","name":"R","fields":[{"name":"id","type":"int"},{"name":"name","type":"string"}]}""");
        var withDefault = Bytes("""{"type":"record","name":"R","fields":[{"name":"id","type":"int"},{"name":"name","type":"string","default":""}]}""");
        var checker = new BuiltInDocumentCompatibilityValidator();
        await Assert.That((await checker.CheckAsync("Avro/1.11.0", "backward", withoutDefault, [previous])).Status)
            .IsEqualTo(DocumentCompatibilityStatus.Incompatible);
        await Assert.That((await checker.CheckAsync("Avro/1.11.0", "backward", withDefault, [previous])).Status)
            .IsEqualTo(DocumentCompatibilityStatus.Compatible);
        await Assert.That((await checker.CheckAsync("Avro/1.11.0", "forward", withoutDefault, [previous])).Status)
            .IsEqualTo(DocumentCompatibilityStatus.Compatible);
    }

    [Test]
    public async Task ReaderAliasesAndRecursiveRecordsAreResolvedWithoutInfiniteTraversal()
    {
        var previous = Bytes("""{"type":"record","name":"Old","fields":[{"name":"old","type":"int"},{"name":"next","type":["null","Old"]}]}""");
        var current = Bytes("""{"type":"record","name":"New","aliases":["Old"],"fields":[{"name":"new","aliases":["old"],"type":"long"},{"name":"next","type":["null","New"]}]}""");
        var result = await new BuiltInDocumentCompatibilityValidator().CheckAsync("Avro/1.11.0", "backward", current, [previous]);
        await Assert.That(result.Status).IsEqualTo(DocumentCompatibilityStatus.Compatible);
    }

    [Test]
    public async Task TransitiveModesInspectAllSuppliedAncestorsInNearestFirstOrder()
    {
        var checker = new BuiltInDocumentCompatibilityValidator();
        ReadOnlyMemory<byte>[] history = [Bytes("\"int\""), Bytes("\"string\"")];
        var direct = await checker.CheckAsync("Avro/1.11.0", "backward", Bytes("\"long\""), history);
        var transitive = await checker.CheckAsync("Avro/1.11.0", "backward_transitive", Bytes("\"long\""), history);
        await Assert.That(direct.Status).IsEqualTo(DocumentCompatibilityStatus.Compatible);
        await Assert.That(transitive.Status).IsEqualTo(DocumentCompatibilityStatus.Incompatible);
        await Assert.That(transitive.Diagnostics[0].Detail).Contains("ancestor 1");
    }

    [Test]
    public async Task UnknownPoliciesAndInvalidSchemasNeverBecomeCompatible()
    {
        var checker = new BuiltInDocumentCompatibilityValidator();
        await Assert.That((await checker.CheckAsync("Avro/1.11.0", "invented", Bytes("\"int\""), [])).Status)
            .IsEqualTo(DocumentCompatibilityStatus.Unsupported);
        await Assert.That((await checker.CheckAsync("Avro/1.11.0", "full", Bytes("{bad"), [])).Status)
            .IsEqualTo(DocumentCompatibilityStatus.Indeterminate);
        await Assert.That((await checker.CheckAsync("JsonSchema/draft-07", "backward",
            Bytes("""{"type":"string"}"""), [Bytes("""{"type":"integer"}""")])).Status)
            .IsEqualTo(DocumentCompatibilityStatus.Unsupported);
    }

    [Test]
    public async Task ExactSchemaIdentityIsValidatedBeforeProvidingCompatibility()
    {
        var checker = new BuiltInDocumentCompatibilityValidator();
        var valid = Bytes("""{"type":"string"}""");
        var invalid = Bytes("""{"type":"not-a-type"}""");
        await Assert.That((await checker.CheckAsync("JsonSchema/draft-07", "full", valid, [valid])).Status)
            .IsEqualTo(DocumentCompatibilityStatus.Compatible);
        await Assert.That((await checker.CheckAsync("JsonSchema/draft-07", "full", invalid, [invalid])).Status)
            .IsEqualTo(DocumentCompatibilityStatus.Indeterminate);
    }

    [Test]
    public async Task HistoryBytesAndWorkLimitsCannotBeResetForEachAncestor()
    {
        var checker = new BuiltInDocumentCompatibilityValidator();
        ReadOnlyMemory<byte>[] history = [Bytes("\"int\""), Bytes("\"int\"")];
        var result = await checker.CheckAsync("Avro/1.11.0", "backward_transitive", Bytes("\"long\""), history,
            new() { MaxTotalBytes = 15 });
        await Assert.That(result.Status).IsEqualTo(DocumentCompatibilityStatus.Indeterminate);
        await Assert.That((await checker.CheckAsync("Avro/1.11.0", "backward", Bytes("\"long\""), history,
            new() { MaxHistory = 1 })).Status).IsEqualTo(DocumentCompatibilityStatus.Indeterminate);
    }

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
