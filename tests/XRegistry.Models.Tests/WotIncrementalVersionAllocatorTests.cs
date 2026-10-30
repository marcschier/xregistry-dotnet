// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;

namespace XRegistry.Models.Tests;

public class WotIncrementalVersionAllocatorTests
{
    [Test]
    [Arguments(null, null, "1")]
    [Arguments("0", null, "1")]
    [Arguments("8", null, "9")]
    [Arguments("9", null, "10")]
    [Arguments("41", null, "42")]
    [Arguments("99", null, "100")]
    [Arguments("9223372036854775807", null, "9223372036854775808")]
    [Arguments("41", "", "42")]
    [Arguments("41", "0", "42")]
    [Arguments("41", "042", "42")]
    [Arguments("41", "+42", "42")]
    [Arguments("41", "-42", "42")]
    [Arguments("41", "42.0", "42")]
    [Arguments("41", "1.2.3", "42")]
    [Arguments("41", "release-blue", "42")]
    [Arguments("41", " 42", "42")]
    [Arguments("41", "42 ", "42")]
    [Arguments("41", "\u0664\u0662", "42")]
    public async Task GeneratesTheNextRevisionWithoutInterpretingNoncanonicalAuthoredValues(
        string? watermark, string? authored, string expected)
    {
        var result = WotIncrementalVersionAllocator.Assign(watermark, authored, expected);
        await Assert.That(result.VersionId).IsEqualTo(expected);
        await Assert.That(result.HighWaterMark).IsEqualTo(expected);
        await Assert.That(result.Origin).IsEqualTo("generated");
    }

    [Test]
    [Arguments(null, "1")]
    [Arguments("41", "42")]
    [Arguments("41", "100")]
    [Arguments("9", "10")]
    [Arguments("9223372036854775807", "9223372036854775808")]
    public async Task UsesCanonicalAuthoredIntegersAboveTheDurableWatermark(string? watermark, string authored)
    {
        var result = WotIncrementalVersionAllocator.Assign(watermark, authored, authored);
        await Assert.That(result.VersionId).IsEqualTo(authored);
        await Assert.That(result.HighWaterMark).IsEqualTo(authored);
        await Assert.That(result.Origin).IsEqualTo("authored");
    }

    [Test]
    [Arguments("40")]
    [Arguments("41")]
    public async Task DoesNotReuseAuthoredHistoryOrFillDeletedGaps(string authored)
    {
        var failure = await Assert.That(() => WotIncrementalVersionAllocator.Assign("41", authored))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected revision reuse rejection.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    [Arguments("")]
    [Arguments("00")]
    [Arguments("01")]
    [Arguments("-1")]
    [Arguments("+1")]
    [Arguments("1.0")]
    [Arguments(" 1")]
    [Arguments("\u0661")]
    public async Task InvalidDurableStateCannotRestartTheSequence(string watermark)
    {
        var failure = await Assert.That(() => WotIncrementalVersionAllocator.Assign(watermark, null))
            .Throws<ArgumentException>() ?? throw new InvalidOperationException("Expected invalid allocation history.");
        await Assert.That(failure.ParamName).IsEqualTo("highWaterMark");
    }

    [Test]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("042")]
    [Arguments("+42")]
    [Arguments("-42")]
    [Arguments("1.2.3")]
    [Arguments("\u0664\u0662")]
    public async Task RejectsNoncanonicalSuppliedIdsRatherThanTreatingThemAsGeneratedInputs(string supplied)
    {
        var failure = await Assert.That(() => WotIncrementalVersionAllocator.Assign("41", null, supplied))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected invalid supplied revision.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
    }

    [Test]
    [Arguments(null)]
    [Arguments("42")]
    public async Task SuppliedRegistryIdsMustEqualTheAuthoredOrGeneratedAssignment(string? authored)
    {
        var failure = await Assert.That(() => WotIncrementalVersionAllocator.Assign("41", authored, "43"))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected assignment mismatch.");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("version-mismatch");
    }

    [Test]
    public async Task EnforcesTheCoreLengthBoundaryWithoutFixedWidthRollover()
    {
        var maximum = new string('9', 128);
        var predecessor = new string('9', 127) + "8";
        var generated = WotIncrementalVersionAllocator.Assign(predecessor, null);
        var authored = WotIncrementalVersionAllocator.Assign("0", maximum);
        await Assert.That(generated.VersionId).IsEqualTo(maximum);
        await Assert.That(generated.Origin).IsEqualTo("generated");
        await Assert.That(authored.VersionId).IsEqualTo(maximum);
        await Assert.That(authored.Origin).IsEqualTo("authored");
        foreach (var operation in new Func<WotIncrementalVersionAssignment>[]
        {
            () => WotIncrementalVersionAllocator.Assign(maximum, null),
            () => WotIncrementalVersionAllocator.Assign(null, "1" + maximum),
            () => WotIncrementalVersionAllocator.Assign(null, null, "1" + maximum)
        })
        {
            var failure = await Assert.That(operation).Throws<RegistryException>()
                ?? throw new InvalidOperationException("Expected exhausted or oversized revision rejection.");
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("invalid-version");
        }
        var invalidState = await Assert.That(() => WotIncrementalVersionAllocator.Assign("1" + maximum, null))
            .Throws<ArgumentException>() ?? throw new InvalidOperationException("Expected oversized durable state.");
        await Assert.That(invalidState.ParamName).IsEqualTo("highWaterMark");
    }
}
