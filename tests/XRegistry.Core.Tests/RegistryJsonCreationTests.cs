// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryJsonCreationTests
{
    [Test]
    public async Task CreatedValueOwnsItsBytesAndPreservesExactNumbers()
    {
        RegistryJson value;
        var calls = 0;
        using (var source = JsonDocument.Parse("""{"n":9007199254740993123456789,"nil":null,"empty":""}"""))
        {
            value = RegistryJson.Create(writer =>
            {
                calls++;
                source.RootElement.WriteTo(writer);
            });
        }

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(value.RootElement.GetProperty("n").GetRawText()).IsEqualTo("9007199254740993123456789");
        await Assert.That(value.GetPresence("nil")).IsEqualTo(JsonPresence.Null);
        await Assert.That(value.GetPresence("empty")).IsEqualTo(JsonPresence.Value);
        await Assert.That(value.RootElement.GetProperty("empty").GetString()).IsEqualTo("");
    }

    [Test]
    [Arguments("\"a\"", 3, true)]
    [Arguments("\"ab\"", 3, false)]
    [Arguments("\"\u00e9\"", 4, true)]
    [Arguments("\"\u00e9\"", 3, false)]
    [Arguments("0", 1, true)]
    [Arguments("0", 0, false)]
    public async Task EncodedByteLimitIncludesTheBoundaryWithoutChargingWriterReservation(string json, int limit, bool allowed)
    {
        if (allowed)
        {
            var result = RegistryJson.Create(writer => writer.WriteRawValue(json, skipInputValidation: true),
                new() { MaxBytes = limit });
            await Assert.That(result.RootElement.GetRawText()).IsEqualTo(json);
        }
        else
        {
            var error = TestErrors.Capture(() => RegistryJson.Create(
                writer => writer.WriteRawValue(json, skipInputValidation: true), new() { MaxBytes = limit }));
            await Assert.That(error.Code).IsEqualTo("byte_limit");
        }
    }

    [Test]
    [Arguments("""{"x":1,"x":2}""", "duplicate_member")]
    [Arguments("\"\\uD800\"", "invalid_unicode")]
    [Arguments("[[[]]]", "depth_limit")]
    [Arguments("[1,2,3]", "node_limit")]
    [Arguments("{}{}", "invalid_json")]
    public async Task RawWriterOutputStillRequiresCompleteStrictJson(string json, string code)
    {
        var limits = code switch
        {
            "depth_limit" => new RegistryJsonLimits { MaxDepth = 2 },
            "node_limit" => new RegistryJsonLimits { MaxNodes = 3 },
            _ => new RegistryJsonLimits(),
        };
        var error = TestErrors.Capture(() =>
            RegistryJson.Create(writer => writer.WriteRawValue(json, skipInputValidation: true), limits));
        await Assert.That(error.Code).IsEqualTo(code);
    }

    [Test]
    public async Task EmptyCallbacksCannotReturnAnAbsentValueAsSuccessfulJson()
    {
        var error = TestErrors.Capture(() => RegistryJson.Create(_ => { }));
        await Assert.That(error.Code).IsEqualTo("invalid_json");
    }

    [Test]
    public async Task CallbackFailureIsNotReplacedByDisposalOfUncommittedOversizedOutput()
    {
        var failure = new OperationCanceledException("The producer stopped before completing JSON.");
        OperationCanceledException? observed = null;
        RegistryJson? result = null;
        try
        {
            result = RegistryJson.Create(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("value", new string('x', 32));
                throw failure;
            }, new() { MaxBytes = 1 });
        }
        catch (OperationCanceledException exception)
        {
            observed = exception;
        }

        await Assert.That(ReferenceEquals(observed, failure)).IsTrue();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ArgumentsAreValidatedBeforeInvokingTheCallback()
    {
        await Assert.That(() => RegistryJson.Create(null!)).Throws<ArgumentNullException>();
        var called = false;
        await Assert.That(() => RegistryJson.Create(_ => called = true, new() { MaxBytes = -1 }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(called).IsFalse();
    }
}
