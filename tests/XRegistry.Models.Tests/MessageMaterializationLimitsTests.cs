// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Models.Tests.MessageMaterializationTraversalTests;

namespace XRegistry.Models.Tests;

public class MessageMaterializationLimitsTests
{
    [Test]
    [Arguments(1, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task AggregateMetadataByteLimitIsInclusive(long bytes, bool valid)
    {
        var definition = new MessageDefinition(RegistryJson.Parse("{}"), new Uri("https://registry.example/leaf"));
        if (valid)
        {
            var result = await MessageDefinitionMaterializer.MaterializeAsync(definition, options: new() { MaxTotalBytes = bytes });
            await Assert.That(result.Metadata.RootElement.GetRawText()).IsEqualTo("{}");
        }
        else
        {
            var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(definition,
                options: new() { MaxTotalBytes = bytes })).Throws<RegistryException>();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_byte_limit");
        }
    }

    [Test]
    [Arguments(5, false)]
    [Arguments(6, true)]
    [Arguments(7, true)]
    public async Task LeafWorkLimitCoversReadMergeValidationAndProvenance(int work, bool valid)
    {
        var definition = new MessageDefinition(RegistryJson.Parse("{}"), new Uri("https://registry.example/leaf"));
        if (valid)
        {
            var result = await MessageDefinitionMaterializer.MaterializeAsync(definition, options: new() { MaxWork = work });
            await Assert.That(result.IsComplete).IsTrue();
        }
        else
        {
            var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(definition,
                options: new() { MaxWork = work })).Throws<RegistryException>();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_work_limit");
        }
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, true)]
    public async Task BaseDepthLimitRejectsBeforeTheDisallowedAcquisition(int depth, bool valid)
    {
        var calls = 0;
        var definition = new MessageDefinition(Json("https://registry.example/base"), new Uri("https://registry.example/child"));
        ValueTask<MessageMaterializationResult> Resolve() => MessageDefinitionMaterializer.MaterializeAsync(definition, (request, _) =>
        {
            calls++;
            return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("{}"), request.TargetUri)));
        }, new() { MaxDepth = depth });
        if (valid)
        {
            var result = await Resolve();
            await Assert.That(result.Definitions.Count).IsEqualTo(2);
            await Assert.That(calls).IsEqualTo(1);
        }
        else
        {
            var error = await Assert.That(async () => await Resolve()).Throws<RegistryException>();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_depth_limit");
            await Assert.That(calls).IsEqualTo(0);
        }
    }

    [Test]
    public async Task CancellationBeforeMaterializationDoesNotInvokeTheSource()
    {
        var calls = 0;
        await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(Json("https://registry.example/base"), new Uri("https://registry.example/child")), (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Canceled work must not acquire a source.");
            }, cancellationToken: new CancellationToken(canceled: true))).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationStopsWaitingForAnUncooperativeSource()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<MessageDefinitionSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = MessageDefinitionMaterializer.MaterializeAsync(
            new(Json("https://registry.example/base"), new Uri("https://registry.example/child")), (_, _) =>
            {
                started.SetResult();
                return new ValueTask<MessageDefinitionSourceResult>(pending.Task);
            }, cancellationToken: cancellation.Token).AsTask();
        await started.Task;
        await cancellation.CancelAsync();
        await Assert.That(async () => await operation).Throws<OperationCanceledException>();
        pending.SetResult(MessageDefinitionSourceResult.Found(
            new(RegistryJson.Parse("{}"), new Uri("https://registry.example/base"))));
        await Assert.That(operation.IsCanceled).IsTrue();
    }

    [Test]
    public async Task CallerJsonDocumentAndBufferLifetimesDoNotInvalidateResults()
    {
        var bytes = "{\"description\":\"owned\"}"u8.ToArray();
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        var definition = new MessageDefinition(RegistryJson.FromElement(document.RootElement), new Uri("https://registry.example/leaf"));
        var result = await MessageDefinitionMaterializer.MaterializeAsync(definition);
        document.Dispose();
        Array.Fill(bytes, (byte)0);
        await Assert.That(result.Original.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("owned");
        await Assert.That(result.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("owned");
        await Assert.That(result.PropertySources["/description"].Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("owned");
    }

    [Test]
    [Arguments(41, false)]
    [Arguments(42, true)]
    [Arguments(43, true)]
    public async Task AggregateByteBudgetIncludesAcquiredMetadata(long bytes, bool valid)
    {
        var calls = 0;
        ValueTask<MessageMaterializationResult> Resolve() => MessageDefinitionMaterializer.MaterializeAsync(
            new(RegistryJson.Parse("""{"basemessage":"https://b.example/base"}"""), new Uri("https://registry.example/child")),
            (request, _) =>
            {
                calls++;
                return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("{}"), request.TargetUri)));
            }, new() { MaxTotalBytes = bytes });
        if (valid)
        {
            var result = await Resolve();
            await Assert.That(result.IsComplete).IsTrue();
        }
        else
        {
            var error = await Assert.That(async () => await Resolve()).Throws<RegistryException>();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_byte_limit");
        }
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(62, false)]
    [Arguments(76, true)]
    [Arguments(77, true)]
    public async Task OutputByteBudgetRejectsExpansionBeyondIndividuallyValidSources(int bytes, bool valid)
    {
        ValueTask<MessageMaterializationResult> Resolve() => MessageDefinitionMaterializer.MaterializeAsync(
            new(RegistryJson.Parse("""{"basemessage":"https://b.example/base","description":"child"}"""),
                new Uri("https://registry.example/child")), (request, _) =>
                    ValueTask.FromResult(MessageDefinitionSourceResult.Found(
                        new(RegistryJson.Parse("""{"name":"base"}"""), request.TargetUri))),
            new() { JsonLimits = new() { MaxBytes = bytes } });
        if (valid)
        {
            var result = await Resolve();
            await Assert.That(result.Metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("base");
            await Assert.That(result.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("child");
        }
        else
        {
            var error = await Assert.That(async () => await Resolve()).Throws<RegistryException>();
            await Assert.That(error!.Diagnostic.Code).IsEqualTo("byte_limit");
        }
    }

    [Test]
    public async Task SourceCallbacksReceiveTheDeclaredJsonAcquisitionLimits()
    {
        var observedBytes = 0;
        var result = await MessageDefinitionMaterializer.MaterializeAsync(
            new(Json("https://registry.example/base"), new Uri("https://registry.example/child")), (request, _) =>
            {
                observedBytes = request.JsonLimits.MaxBytes;
                return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(RegistryJson.Parse("{}"), request.TargetUri)));
            }, new() { JsonLimits = new() { MaxBytes = 128, MaxNodes = 16, MaxDepth = 8 } });
        await Assert.That(observedBytes).IsEqualTo(128);
        await Assert.That(result.References[0].JsonLimits.MaxNodes).IsEqualTo(16);
        await Assert.That(result.References[0].JsonLimits.MaxDepth).IsEqualTo(8);
    }

    [Test]
    public async Task CancellationAfterAcquisitionPreventsCompositionAndFurtherReads()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(Json("https://registry.example/base"), new Uri("https://registry.example/child")), (request, _) =>
            {
                calls++;
                cancellation.Cancel();
                return ValueTask.FromResult(MessageDefinitionSourceResult.Found(new(Json("https://registry.example/other"), request.TargetUri)));
            }, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task WorkExhaustionBeforeReferenceProcessingDoesNotInvokeTheSource()
    {
        var calls = 0;
        var error = await Assert.That(async () => await MessageDefinitionMaterializer.MaterializeAsync(
            new(Json("https://registry.example/base"), new Uri("https://registry.example/child")), (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Exhausted work cannot authorize a source read.");
            }, new() { MaxWork = 1 })).Throws<RegistryException>();
        await Assert.That(error!.Diagnostic.Code).IsEqualTo("message_work_limit");
        await Assert.That(calls).IsEqualTo(0);
    }
}
