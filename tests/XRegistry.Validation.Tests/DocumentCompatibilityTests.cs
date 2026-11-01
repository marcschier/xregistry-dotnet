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
        DocumentCompatibilityStatus.Unsupported)]
    [Arguments("full", "\"bytes\"", """{"type":"bytes","logicalType":"decimal","precision":4,"scale":2}""",
        DocumentCompatibilityStatus.Unsupported)]
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
