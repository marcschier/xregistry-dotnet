// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryConditionalPatchTests
{
    [Test]
    public async Task PatchUsesRetainedDiscriminatorForConditionalSibling()
    {
        var engine = Create("""
            {"groups":{"gs":{"singular":"g","attributes":{
              "kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra":{"type":"integer"}}}}}
            }}}}
            """);
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":1}""");

        var patched = await Send(engine, RegistryAction.Patch, "/gs/g", """{"extra":2}""");
        await Assert.That(patched.Metadata!.RootElement.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(patched.Metadata.RootElement.GetProperty("extra").GetInt32()).IsEqualTo(2);

        var stored = await Send(engine, RegistryAction.Read, "/gs/g");
        await Assert.That(stored.Metadata!.RootElement.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(stored.Metadata.RootElement.GetProperty("extra").GetInt32()).IsEqualTo(2);
    }

    [Test]
    [Arguments("null")]
    [Arguments("\"not an integer\"")]
    [Arguments("""{"malformed":true}""")]
    public async Task PatchIgnoresMalformedRetainedConditionalReadonlySibling(string value)
    {
        var engine = ConditionalGroup("""{"type":"integer","readonly":true,"required":true,"default":1}""");
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on"}""");

        await Send(engine, RegistryAction.Patch, "/gs/g", "{\"extra\":" + value + "}");

        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(stored.GetProperty("extra").GetInt32()).IsEqualTo(1);
    }

    [Test]
    [Arguments("\"off\"")]
    [Arguments("null")]
    [Arguments("""{"malformed":true}""")]
    public async Task ReadonlyDiscriminatorUsesServerValueInsteadOfSubmittedValue(string value)
    {
        var engine = Create("""
            {"groups":{"gs":{"singular":"g","attributes":{
              "kind":{"type":"string","readonly":true,"required":true,"default":"on","ifvalues":{
                "on":{"siblingattributes":{"extra":{"type":"integer","required":true,"default":1}}}
              }}
            }}}}
            """);
        await Send(engine, RegistryAction.Replace, "/gs/g", "{}");

        await Send(engine, RegistryAction.Patch, "/gs/g", "{\"kind\":" + value + ",\"extra\":2}");

        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(stored.GetProperty("extra").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task ConditionalDefaultPreservesOmittedValueAndResetsExplicitNull()
    {
        var engine = ConditionalGroup("""{"type":"integer","required":true,"default":5}""");
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":9}""");

        var omitted = await Send(engine, RegistryAction.Patch, "/gs/g", "{}");
        await Assert.That(omitted.Metadata!.RootElement.GetProperty("extra").GetInt32()).IsEqualTo(9);
        await Send(engine, RegistryAction.Patch, "/gs/g", """{"extra":null}""");
        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(stored.GetProperty("extra").GetInt32()).IsEqualTo(5);
    }

    [Test]
    public async Task OptionalConditionalNullDeletesWhileOmissionPreservesTheStoredValue()
    {
        var engine = ConditionalGroup("""{"type":"integer"}""");
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":1}""");

        var omitted = await Send(engine, RegistryAction.Patch, "/gs/g", "{}");
        await Assert.That(omitted.Metadata!.RootElement.GetProperty("extra").GetInt32()).IsEqualTo(1);
        var deleted = await Send(engine, RegistryAction.Patch, "/gs/g", """{"extra":null}""");
        await Assert.That(deleted.Metadata!.RootElement.TryGetProperty("extra", out _)).IsFalse();

        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(stored.TryGetProperty("extra", out _)).IsFalse();
    }

    [Test]
    public async Task ExplicitNullDeletionOfRequiredConditionalValueRemainsInvalidAttribute()
    {
        var engine = ConditionalGroup("""{"type":"integer","required":true}""");
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":1}""");

        await RejectPatch(engine, """{"extra":null}""", "invalid_attribute");
    }

    [Test]
    public async Task ActivatingConditionalRequiredValueStillRequiresCompleteValidation()
    {
        var engine = ConditionalGroup("""{"type":"integer","required":true}""");
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"off"}""");

        await RejectPatch(engine, """{"kind":"on"}""", "required_attribute_missing");
    }

    [Test]
    public async Task DeactivatingBranchStillValidatesTheRetainedSiblingBeforePublication()
    {
        var engine = ConditionalGroup("""{"type":"integer"}""");
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":1}""");

        await RejectPatch(engine, """{"kind":"off"}""", "unknown_attribute");
    }

    [Test]
    public async Task ChangedDiscriminatorSelectsSubmittedBranchInsteadOfRetainedBranch()
    {
        var engine = TwoBranchGroup();
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":1}""");

        await RejectPatch(engine, """{"kind":"off","extra":2}""", "invalid_attribute");
        await Send(engine, RegistryAction.Patch, "/gs/g", """{"kind":"off","extra":"changed"}""");

        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("kind").GetString()).IsEqualTo("off");
        await Assert.That(stored.GetProperty("extra").GetString()).IsEqualTo("changed");
    }

    [Test]
    public async Task NullDiscriminatorUsesItsDefaultInsteadOfItsRetainedValue()
    {
        var engine = TwoBranchGroup();
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"off","extra":"old"}""");

        await Send(engine, RegistryAction.Patch, "/gs/g", """{"kind":null,"extra":2}""");

        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(stored.GetProperty("extra").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task NullDiscriminatorWithoutDefaultDoesNotActivateRetainedBranch()
    {
        var engine = ConditionalGroup("""{"type":"integer"}""");
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":1}""");

        await RejectPatch(engine, """{"kind":null,"extra":2}""", "unknown_attribute");
    }

    [Test]
    public async Task SubmittedNestedObjectReplacesRatherThanBorrowsRetainedDiscriminator()
    {
        var engine = ConditionalGroup("""
            {"type":"object","attributes":{
              "mode":{"type":"string","ifvalues":{"on":{"siblingattributes":{"value":{"type":"integer"}}}}}
            }}
            """);
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":{"mode":"on","value":1}}""");

        await RejectPatch(engine, """{"extra":{"value":2}}""", "unknown_attribute");
        await Send(engine, RegistryAction.Patch, "/gs/g", """{"extra":{"mode":"off"}}""");

        var stored = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(stored.GetProperty("extra").GetProperty("mode").GetString()).IsEqualTo("off");
        await Assert.That(stored.GetProperty("extra").TryGetProperty("value", out _)).IsFalse();
    }

    [Test]
    [Arguments("""{"other":2}""", "unknown_attribute")]
    [Arguments("""{"extra":"wrong type"}""", "invalid_attribute")]
    [Arguments("""{"epoch":999,"extra":2}""", "mismatched_epoch")]
    [Arguments("""{"gid":"other","extra":2}""", "mismatched_id")]
    public async Task RetainedContextDoesNotBypassModelOrIdentityGuards(string patch, string code)
    {
        var engine = ConditionalGroup("""{"type":"integer"}""");
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"kind":"on","extra":1}""");

        await RejectPatch(engine, patch, code);
    }

    [Test]
    [Arguments("""{"name":"changed","extra":2}""")]
    [Arguments("""{"name":null,"extra":2}""")]
    public async Task RetainedContextPreservesSystemImmutableGuard(string patch)
    {
        var engine = Create("""
            {"groups":{"gs":{"singular":"g","attributes":{
              "name":{"immutable":true},
              "kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra":{"type":"integer"}}}}}
            }}}}
            """);
        await Send(engine, RegistryAction.Replace, "/gs/g", """{"name":"original","kind":"on","extra":1}""");

        await RejectPatch(engine, patch, "invalid_attribute");
        var unchanged = await Send(engine, RegistryAction.Patch, "/gs/g", """{"name":"original","extra":2}""");
        await Assert.That(unchanged.Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("original");
        await Assert.That(unchanged.Metadata.RootElement.GetProperty("extra").GetInt32()).IsEqualTo(2);
    }

    private static RegistryEngine ConditionalGroup(string extra) => Create(
        "{\"groups\":{\"gs\":{\"singular\":\"g\",\"attributes\":{\"kind\":{\"type\":\"string\",\"ifvalues\":" +
        "{\"on\":{\"siblingattributes\":{\"extra\":" + extra + "}}}}}}}}");

    private static RegistryEngine TwoBranchGroup() => Create("""
        {"groups":{"gs":{"singular":"g","attributes":{
          "kind":{"type":"string","required":true,"default":"on","ifvalues":{
            "on":{"siblingattributes":{"extra":{"type":"integer"}}},
            "off":{"siblingattributes":{"extra":{"type":"string"}}}
          }}
        }}}}
        """);

    private static async Task RejectPatch(RegistryEngine engine, string patch, string code)
    {
        var before = (await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;

        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/gs/g", patch), code);

        await Assert.That((await Send(engine, RegistryAction.Read, "/gs/g")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
    }
}
