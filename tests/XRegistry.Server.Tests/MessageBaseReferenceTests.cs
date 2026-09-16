// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class MessageBaseReferenceTests
{
    [Test]
    [Arguments("/messagegroups/missing/messages/base")]
    [Arguments("/messagegroups/missing/messages/base/versions/v%3A1")]
    [Arguments("https://unreachable.invalid/message")]
    public async Task MissingBaseDefinitionsPublishWithoutAcquisitionOrExistenceValidation(string reference)
    {
        var validator = new NeverAcquire();
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
        var store = new InMemoryRegistryPersistence();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "messages",
            PublicRoot = new("https://registry.example/"),
            Model = model,
            ObligationValidator = validator
        }, store, new PermitPolicy());
        var input = "{\"basemessage\":\"" + reference + "\"}";
        var result = await Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m", input);
        await Assert.That(result.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(result.Metadata!.RootElement.GetProperty("basemessage").GetString()).IsEqualTo(reference);
        await Assert.That(validator.Calls).IsEqualTo(0);

        var restarted = new RegistryEngine(new() { RegistryId = "messages", PublicRoot = new("https://registry.example/"), Model = model },
            store, new PermitPolicy());
        await Send(restarted, RegistryAction.Patch, "/messagegroups/g/messages/m", """{"description":"retained"}""");
        var stored = await Send(restarted, RegistryAction.Read, "/messagegroups/g/messages/m");
        await Assert.That(stored.Metadata!.RootElement.GetProperty("basemessage").GetString()).IsEqualTo(reference);
        await Assert.That(stored.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("retained");
    }

    [Test]
    [Arguments("/messagegroups/g")]
    [Arguments("/messagegroups/g/messages/base/meta")]
    [Arguments("/messagegroups/g/messages/base$details")]
    [Arguments("/messagegroups/g/messages/base/versions/request")]
    [Arguments("/schemagroups/g/schemas/s")]
    [Arguments("//remote.example/message")]
    public async Task InvalidLocalBaseKindsAndNonMessageTypesRejectBeforePublication(string reference)
    {
        var engine = MessageEndpointRuntimeTests.CreateDomain(RegistryModelKind.CloudEvents);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            "{\"basemessage\":\"" + reference + "\"}"), "invalid_attribute");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnmarkedCustomBaseAttributesRetainTheirOrdinaryTargetPolicy()
    {
        var engine = Create("""
            {"groups":{"messagegroups":{"singular":"messagegroup","resources":{"messages":{
              "singular":"message","hasdocument":false,"attributes":{
                "basemessage":{"type":"uri","target":"/messagegroups/messages[/versions]"}}}}}}}
            """);
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/messagegroups/g/messages/m",
            """{"basemessage":"/messagegroups/g/messages/base"}"""), "obligation_policy_required");
    }

    private sealed class NeverAcquire : IRegistryMetadataObligationValidator
    {
        internal int Calls { get; private set; }
        public ValueTask ValidateAsync(RegistryMetadataObligationContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Message base admission must not acquire or require the referenced entity.");
        }
    }
}
