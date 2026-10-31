// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class ModelAndVersionTests
{
    [Test]
    [Arguments(false, RegistryAction.Patch)]
    [Arguments(false, RegistryAction.Replace)]
    [Arguments(true, RegistryAction.Patch)]
    [Arguments(true, RegistryAction.Replace)]
    public async Task ModeledStickyPolicyControlsSelectionAndRejectsOppositeValues(
        bool requiredSticky, RegistryAction action)
    {
        var store = new InMemoryRegistryPersistence();
        var value = requiredSticky ? "true" : "false";
        var model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0,"versionmode":"semver",
              "metaattributes":{"defaultversionsticky":{
                "enum":[STICKY],"required":true,"default":STICKY
              }}
            }}}}}
            """.Replace("STICKY", value, StringComparison.Ordinal);
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/1.0.0", """{"name":"first"}""");
        await AssertSelectionAsync(engine, "1.0.0");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/2.0.0", """{"name":"second"}""");
        var selected = requiredSticky ? "1.0.0" : "2.0.0";
        await AssertSelectionAsync(engine, selected);
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText();
        var versions = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, action, "/teams/g/notes/n/meta",
            "{\"defaultversionsticky\":" + (requiredSticky ? "false" : "true") + "}"), "invalid_attribute");
        if (!requiredSticky && action == RegistryAction.Patch)
        {
            await ExpectCode(() => Send(engine, action, "/teams/g/notes/n/meta",
                """{"defaultversionid":"1.0.0"}"""), "invalid_attribute");
        }
        await AssertUnchangedAsync(engine);
        engine = Create(model, persistence: store);
        await AssertUnchangedAsync(engine);
        await Send(engine, action, "/teams/g/notes/n/meta", "{\"defaultversionsticky\":" + value + "}");
        if (requiredSticky && action == RegistryAction.Replace)
        {
            selected = "2.0.0";
        }
        await AssertSelectionAsync(engine, selected);
        await Send(engine, action, "/teams/g/notes/n/meta", "{}");
        await AssertSelectionAsync(engine, selected);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/3.0.0", """{"name":"third"}""");
        await AssertSelectionAsync(engine, requiredSticky ? selected : "3.0.0");
        await AssertSelectionAsync(Create(model, persistence: store), requiredSticky ? selected : "3.0.0");

        async Task AssertSelectionAsync(RegistryEngine current, string expectedId)
        {
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(requiredSticky);
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(expectedId);
        }

        async Task AssertUnchangedAsync(RegistryEngine current)
        {
            await Assert.That((await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(versions);
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DefaultVersionUrlsUseAuthoritativeMetadataAndDocumentPointers(bool hasDocument)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        var collection = hasDocument ? "files" : "notes";
        var resource = "/teams/g/" + collection + "/r";
        var detailsSuffix = hasDocument ? "$details" : "";
        var versionId = "v~:@";
        var versionPath = resource + "/versions/" + Uri.EscapeDataString(versionId);
        await Send(engine, RegistryAction.Replace, versionPath + detailsSuffix,
            hasDocument ? """{"filebase64":"AAE="}""" : """{"name":"stored"}""");
        var expectedUrl = "https://registry.example/catalog" + versionPath + detailsSuffix;
        await AssertApiAsync(engine);
        foreach (var action in new[] { RegistryAction.Patch, RegistryAction.Replace })
        {
            foreach (var body in new[] { """{"defaultversionurl":null}""", """{"defaultversionurl":{}}""",
                """{"defaultversionurl":"https://forged.example/document"}""" })
            {
                await Send(engine, action, resource + "/meta", body);
                await AssertApiAsync(engine);
            }
        }
        var restarted = Create(persistence: store);
        await AssertApiAsync(restarted);
        var exported = (await Send(restarted, RegistryAction.Read, "/export")).Metadata!.RootElement;
        var exportedResource = exported.GetProperty("teams").GetProperty("g").GetProperty(collection).GetProperty("r");
        var pointer = "#/teams/g/" + collection + "/r/versions/v~0:@";
        await Assert.That(exportedResource.GetProperty("meta").GetProperty("defaultversionurl").GetString()).IsEqualTo(pointer);
        await Assert.That(exportedResource.GetProperty("versions").GetProperty(versionId).GetProperty("self").GetString())
            .IsEqualTo(pointer);
        var unexpanded = (await Send(restarted, RegistryAction.Read, resource + detailsSuffix, null,
            new("doc", null), new("inline", "meta"))).Metadata!.RootElement;
        await Assert.That(unexpanded.TryGetProperty("versions", out _)).IsFalse();
        await Assert.That(unexpanded.GetProperty("meta").GetProperty("defaultversionurl").GetString()).IsEqualTo(expectedUrl);
        await Send(restarted, RegistryAction.Post, resource + detailsSuffix,
            hasDocument ? """{"versionid":"next","filebase64":"Ag=="}""" : """{"versionid":"next","name":"newer"}""");
        versionId = "next";
        versionPath = resource + "/versions/next";
        expectedUrl = "https://registry.example/catalog" + versionPath + detailsSuffix;
        await AssertApiAsync(restarted);
        await AssertApiAsync(Create(persistence: store));

        async Task AssertApiAsync(RegistryEngine current)
        {
            var meta = (await Send(current, RegistryAction.Read, resource + "/meta")).Metadata!.RootElement;
            var version = (await Send(current, RegistryAction.Read, versionPath + detailsSuffix)).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(versionId);
            await Assert.That(meta.GetProperty("defaultversionurl").GetString()).IsEqualTo(expectedUrl);
            await Assert.That(version.GetProperty("self").GetString()).IsEqualTo(expectedUrl);
        }
    }

    [Test]
    [Arguments(RegistryAction.Patch)]
    [Arguments(RegistryAction.Replace)]
    public async Task DefaultStickyRequiresJsonBooleansAndRejectsOtherTypesAtomically(RegistryAction action)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"only","name":"stored"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText();
        var version = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/only")).Metadata!.RootElement.GetRawText();
        foreach (var value in new[] { "\"true\"", "\"false\"", "\"True\"", "\"FALSE\"", "0", "1", "{}", "[]" })
        {
            await ExpectCode(() => Send(engine, action, "/teams/g/notes/n/meta",
                "{\"defaultversionsticky\":" + value + "}"), "invalid_attribute");
            await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/only")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(version);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        }
        engine = Create(persistence: store);
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        foreach (var value in new[] { "true", "false", "true", "null" })
        {
            await Send(engine, action, "/teams/g/notes/n/meta", "{\"defaultversionsticky\":" + value + "}");
            var meta = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(value == "true");
            engine = Create(persistence: store);
            var persisted = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(persisted.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(value == "true");
        }
    }

    [Test]
    [Arguments(RegistryAction.Patch)]
    [Arguments(RegistryAction.Replace)]
    public async Task OneVersionResourceRejectsExplicitAndImplicitStickyDefaults(RegistryAction action)
    {
        var store = new InMemoryRegistryPersistence();
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":1
            }}}}}
            """;
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"only","name":"stored"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText();
        var version = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/only")).Metadata!.RootElement.GetRawText();
        var bodies = action == RegistryAction.Patch
            ? new[] { """{"defaultversionsticky":true}""", """{"defaultversionid":"only"}""" }
            : new[] { """{"defaultversionsticky":true}""", """{"defaultversionid":"only","defaultversionsticky":true}""" };
        foreach (var body in bodies)
        {
            await ExpectCode(() => Send(engine, action, "/teams/g/notes/n/meta", body), "setdefaultversionsticky_false");
            await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/only")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(version);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        }
        engine = Create(model, persistence: store);
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/only")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(version);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        foreach (var value in new[] { "false", "null" })
        {
            await Send(engine, action, "/teams/g/notes/n/meta", "{\"defaultversionsticky\":" + value + "}");
            var accepted = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(accepted.GetProperty("defaultversionid").GetString()).IsEqualTo("only");
            await Assert.That(accepted.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();
        }
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"next","name":"replacement"}""");
        var meta = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("next");
        await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/only"), "not_found");
        var retained = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/next")).Metadata!.RootElement;
        await Assert.That(retained.GetProperty("ancestorid").GetString()).IsEqualTo("next");
    }

    [Test]
    [Arguments("select", true, "1.0.0")]
    [Arguments("clear", false, "2.0.0")]
    [Arguments("nonsticky", false, "2.0.0")]
    [Arguments("replace", true, "2.0.0")]
    [Arguments("freeze", true, "2.0.0")]
    [Arguments("null-and-freeze", true, "2.0.0")]
    public async Task DefaultPointerTransitionsChangeOnlyMetaAndPersist(
        string transition, bool sticky, string selected)
    {
        var clock = new AncestryClock();
        var store = new InMemoryRegistryPersistence();
        var options = new RegistryEngineOptions
        {
            RegistryId = "default-pointer",
            PublicRoot = new Uri("https://registry.example"),
            TimeProvider = clock,
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"teams":{"singular":"team","resources":{"notes":{
                  "singular":"note","hasdocument":false,"versionmode":"semver"
                }}}}}
                """))
        };
        var engine = new RegistryEngine(options, store, new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/1.0.0", """{"name":"older"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/2.0.0", """{"name":"newer"}""");
        var initial = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
        await Assert.That(initial.GetProperty("defaultversionid").GetString()).IsEqualTo("2.0.0");
        await Assert.That(initial.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();
        if (transition is "clear" or "nonsticky" or "replace")
        {
            await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"1.0.0"}""");
        }
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
        var snapshots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var details = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/" + version);
            snapshots.Add(version, StoredAttributes(details.Metadata!.RootElement.GetRawText()));
        }
        var body = transition switch
        {
            "select" => """{"defaultversionid":"1.0.0"}""",
            "clear" => """{"defaultversionid":null}""",
            "nonsticky" => """{"defaultversionid":"1.0.0","defaultversionsticky":false}""",
            "null-and-freeze" => """{"defaultversionid":null,"defaultversionsticky":true}""",
            _ => """{"defaultversionsticky":true}"""
        };
        clock.Now = clock.Now.AddMinutes(1);
        var updated = await Send(engine, transition == "replace" ? RegistryAction.Replace : RegistryAction.Patch,
            "/teams/g/notes/n/meta", body);
        await AssertPointerAsync(engine);
        await AssertPointerAsync(new RegistryEngine(options, store, new PermitPolicy()));
        var batch = (await engine.ReadEventBatchesAsync(Writer())).Single(item => item.CorrelationId == updated.CorrelationId);
        var events = batch.Events.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(events[0].GetProperty("type").GetString()).IsEqualTo("io.xregistry.resource.updated");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/3.0.0", """{"name":"latest"}""");
        var afterAddition = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
        await Assert.That(afterAddition.GetProperty("defaultversionid").GetString()).IsEqualTo(sticky ? selected : "3.0.0");
        await Assert.That(afterAddition.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(sticky);
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/" +
            (selected == "1.0.0" ? "2.0.0" : "1.0.0"));
        var afterDeletion = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
        await Assert.That(afterDeletion.GetProperty("defaultversionid").GetString()).IsEqualTo(sticky ? selected : "3.0.0");
        await Assert.That(afterDeletion.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(sticky);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionsticky":null}""");
        var cleared = (await Send(new RegistryEngine(options, store, new PermitPolicy()), RegistryAction.Read,
            "/teams/g/notes/n/meta")).Metadata!.RootElement;
        await Assert.That(cleared.GetProperty("defaultversionid").GetString()).IsEqualTo("3.0.0");
        await Assert.That(cleared.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();

        async Task AssertPointerAsync(RegistryEngine current)
        {
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(selected);
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(sticky);
            await Assert.That(RegistryNumber.Parse(meta.GetProperty("epoch").GetRawText()).ToBigInteger())
                .IsEqualTo(RegistryNumber.Parse(before.GetProperty("epoch").GetRawText()).ToBigInteger() + 1);
            await Assert.That(meta.GetProperty("modifiedat").GetDateTimeOffset()).IsEqualTo(clock.Now);
            await Assert.That(meta.GetProperty("createdat").GetString()).IsEqualTo(before.GetProperty("createdat").GetString());
            var resource = (await Send(current, RegistryAction.Read, "/teams/g/notes/n")).Metadata!.RootElement;
            await Assert.That(resource.GetProperty("versionid").GetString()).IsEqualTo(selected);
            await Assert.That(resource.GetProperty("name").GetString()).IsEqualTo(selected == "1.0.0" ? "older" : "newer");
            foreach (var version in new[] { "1.0.0", "2.0.0" })
            {
                var details = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions/" + version)).Metadata!.RootElement;
                await Assert.That(details.GetProperty("isdefault").GetBoolean()).IsEqualTo(version == selected);
                await Assert.That(StoredAttributes(details.GetRawText())).IsEqualTo(snapshots[version]);
            }
        }

        static string StoredAttributes(string json)
        {
            var value = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            value.Remove("isdefault");
            return value.ToJsonString();
        }
    }

    [Test]
    [Arguments("missing")]
    [Arguments("root")]
    public async Task UnknownDefaultPointerRejectsWithoutChangingMetaVersionsOrEvents(string target)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Root", """{"name":"stored"}""");
        var meta = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText();
        var version = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Root")).Metadata!.RootElement.GetRawText();
        foreach (var action in new[] { RegistryAction.Patch, RegistryAction.Replace })
        {
            await ExpectCode(() => Send(engine, action, "/teams/g/notes/n/meta",
                "{\"defaultversionid\":\"" + target + "\",\"defaultversionsticky\":true}"), "unknown_id");
            await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(meta);
            await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Root")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(version);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        }
        var restarted = Create(persistence: store);
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(meta);
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/versions/Root")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(version);
        await Assert.That((await restarted.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
    }

    [Test]
    public async Task ManualAncestryUsesAssignedRootIdentityAndExactCaseReferences()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        var root = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"ancestorid":"request"}""");
        var assigned = root.Metadata!.RootElement.GetProperty("versionid").GetString();
        await Assert.That(assigned).IsNotNullOrEmpty();
        await Assert.That(assigned).IsNotEqualTo("request");
        await Assert.That(root.Metadata.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo(assigned);
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"Root","ancestorid":"Root"}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"Child","ancestorid":"Root"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        foreach (var ancestor in new[] { "root", "missing" })
        {
            await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Child",
                "{\"ancestorid\":\"" + ancestor + "\"}"), "unknown_id");
            await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Root",
            """{"ancestorid":"Child"}"""), "ancestor_circular_reference");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Child", """{"name":"updated"}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement
            .GetProperty("ancestorid").GetString()).IsEqualTo("Root");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Child", """{"name":"replaced"}""");
        var restarted = Create(persistence: store);
        var retained = (await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement;
        await Assert.That(retained.GetProperty("ancestorid").GetString()).IsEqualTo("Root");
        await Assert.That(retained.GetProperty("name").GetString()).IsEqualTo("replaced");
        var restoredRoot = await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/versions/" + assigned);
        await Assert.That(restoredRoot.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo(assigned);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitAndDeletionRepairedAncestryUpdateEpochAndModifiedTime(bool deleteAncestor)
    {
        var clock = new AncestryClock();
        var store = new InMemoryRegistryPersistence();
        var options = new RegistryEngineOptions
        {
            RegistryId = "ancestry-time",
            PublicRoot = new Uri("https://registry.example"),
            TimeProvider = clock,
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"teams":{"singular":"team","resources":{"notes":{
                  "singular":"note","hasdocument":false,"versionmode":"manual"
                }}}}}
                """))
        };
        var engine = new RegistryEngine(options, store, new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Root", """{"ancestorid":"Root"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Child", """{"ancestorid":"Root"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement;
        clock.Now = clock.Now.AddMinutes(1);
        if (deleteAncestor)
        {
            await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/Root");
        }
        else
        {
            await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Child", """{"ancestorid":"Child"}""");
        }
        var after = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement;
        await Assert.That(after.GetProperty("ancestorid").GetString()).IsEqualTo("Child");
        await Assert.That(RegistryNumber.Parse(after.GetProperty("epoch").GetRawText()).ToBigInteger())
            .IsEqualTo(RegistryNumber.Parse(before.GetProperty("epoch").GetRawText()).ToBigInteger() + 1);
        await Assert.That(after.GetProperty("createdat").GetString()).IsEqualTo(before.GetProperty("createdat").GetString());
        await Assert.That(after.GetProperty("modifiedat").GetDateTimeOffset()).IsEqualTo(clock.Now);
        var restarted = new RegistryEngine(options, store, new PermitPolicy());
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(after.GetRawText());
        await Assert.That((await restarted.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(3);
    }

    [Test]
    public async Task ModifiedAtOrderingReachesAStableAncestryAfterIndirectTimestampTouches()
    {
        var engine = new RegistryEngine(new()
        {
            RegistryId = "ordering",
            PublicRoot = new Uri("https://registry.example"),
            TimeProvider = new OrderingClock(),
            Model = RegistryModel.Compile(RegistryJson.Parse("""
                {"groups":{"teams":{"singular":"team","resources":{"notes":{
                  "singular":"note","hasdocument":false,"versionmode":"modifiedat"
                }}}}}
                """))
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/a", """{"modifiedat":"2000-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/b", """{"modifiedat":"2010-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/c", """{"modifiedat":"2020-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/a", """{"modifiedat":"2030-01-01T00:00:00Z"}""");
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("c");
        var versions = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
        await Assert.That(versions.GetProperty("a").GetProperty("ancestorid").GetString()).IsEqualTo("a");
        await Assert.That(versions.GetProperty("b").GetProperty("ancestorid").GetString()).IsEqualTo("a");
        await Assert.That(versions.GetProperty("c").GetProperty("ancestorid").GetString()).IsEqualTo("b");
    }

    [Test]
    public async Task TimestampOrderingTreatsEquivalentZeroFractionsAsTies()
    {
        var engine = Create("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"versionmode":"createdat"
            }}}}}
            """);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/z", """{"createdat":"2020-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/a", """{"createdat":"2020-01-01T00:00:00.0000Z"}""");
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("z");
        var newest = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/z");
        await Assert.That(newest.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("a");
    }

    [Test]
    public async Task ModelNullResetAndReadonlyCapabilitiesInputFollowRootProcessing()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Patch, "/", """{"capabilities":{"not":"mutable"},"modelsource":null}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetRawText()).IsEqualTo("{}");
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/teams"), "unknown_group_type");
    }

    [Test]
    public async Task PersistedModelFreezesResolvedIncludesWithoutRefetchingOnRestart()
    {
        var resolver = new IncludeResolver();
        var options = new RegistryEngineOptions
        {
            RegistryId = "frozen",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse("""{"$include":"https://models.example/model"}"""),
                new() { Resolver = resolver }),
            ModelCompilation = new() { Resolver = resolver }
        };
        var store = new InMemoryRegistryPersistence();
        var engine = new RegistryEngine(options, store, new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g", "{}");
        resolver.Available = false;
        var restarted = new RegistryEngine(options, store, new PermitPolicy());
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g")).Metadata!.RootElement.GetProperty("teamid").GetString()).IsEqualTo("g");
        await Assert.That((await Send(restarted, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetProperty("$include").GetString())
            .IsEqualTo("https://models.example/model");
        await Assert.That(resolver.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("createdat", "late", "early")]
    [Arguments("modifiedat", "late", "early")]
    [Arguments("semver", "2.0.0", "1.0.0-rc.1")]
    public async Task VersionOrderingRebuildsAncestry(string mode, string newest, string oldest)
    {
        var model = $$"""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"versionmode":"{{mode}}","singleversionroot":true
            } } } } }
            """;
        var engine = Create(model);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/" + newest,
            """{"createdat":"2025-06-01T00:00:00Z","modifiedat":"2025-06-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/" + oldest,
            """{"createdat":"2020-01-01T00:00:00Z","modifiedat":"2020-01-01T00:00:00Z"}""");
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo(newest);
        var version = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/" + newest);
        await Assert.That(version.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo(oldest);
    }

    [Test]
    public async Task InvalidAncestryAndMultipleRootsRollBack()
    {
        var engine = Create("""{"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,"singleversionroot":true}}}}}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"versionid":"v1"}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"v2"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/v1", """{"ancestorid":"v2"}"""), "ancestor_circular_reference");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/v2", """{"ancestorid":"v2"}"""), "multiple_roots");
        var version = await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/v1");
        await Assert.That(version.Metadata!.RootElement.GetProperty("ancestorid").GetString()).IsEqualTo("v1");
    }

    [Test]
    public async Task ModelReplacementValidatesExistingEntitiesAtomicallyAndSurvivesEngineRestart()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"existing"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/modelsource", "{}"), "model_compliance_error");
        var replacement = """{"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false},"files":{"singular":"file","maxversions":2}}},"projects":{"singular":"project"}}}""";
        await Send(engine, RegistryAction.Replace, "/modelsource", replacement);
        var restarted = Create(persistence: store);
        var projects = await Send(restarted, RegistryAction.Replace, "/projects/p", """{"name":"new type"}""");
        await Assert.That(projects.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That((await Send(restarted, RegistryAction.Read, "/teams/g/notes/n")).Metadata!.RootElement.GetProperty("name").GetString()).IsEqualTo("existing");
        await Assert.That((await Send(restarted, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetProperty("groups").TryGetProperty("projects", out _)).IsTrue();
    }

    [Test]
    public async Task ServerChosenVersionIdsContinueAfterDeletionAndRespectSetVersionId()
    {
        var engine = Create("""{"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,"setversionid":false}}}}}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", "{}");
        var second = await Send(engine, RegistryAction.Post, "/teams/g/notes/n", "{}", new KeyValuePair<string, string?>("setdefaultversionid", "request"));
        await Assert.That(second.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("2");
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/2");
        var third = await Send(engine, RegistryAction.Post, "/teams/g/notes/n", "{}");
        await Assert.That(third.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("3");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"chosen"}"""), "versionid_not_allowed");
    }

    [Test]
    public async Task ApiGroupAndVersionFormatsMatchCaseInsensitively()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", """{"format":"OpenAPI/3.1"}""");
        await Send(engine, RegistryAction.Replace, "/apigroups/g/apis/a", """{"format":"OpenAPI/3.1"}""");

        var second = await Send(engine, RegistryAction.Post, "/apigroups/g/apis/a",
            """{"versionid":"v2","format":"openapi/3.1"}""");

        await Assert.That(second.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("v2");
    }

    [Test]
    public async Task ApiVersionFormatCaseOnlyAndExactValuesAreAcceptedByOrdinaryMutation()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", """{"format":"OpenAPI/3.1"}""");
        await Send(engine, RegistryAction.Replace, "/apigroups/g/apis/a", """{"versionid":"v1","format":"OpenAPI/3.1"}""");
        await Send(engine, RegistryAction.Post, "/apigroups/g/apis/a",
            """{"versionid":"v2","format":"openapi/3.1"}""");
        await Send(engine, RegistryAction.Post, "/apigroups/g/apis/a",
            """{"versionid":"v3","format":"OpenAPI/3.1"}""");

        var versions = (await Send(engine, RegistryAction.Read, "/apigroups/g/apis/a/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(3);
        await Assert.That(versions.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(versions.GetProperty("v2").GetProperty("format").GetString()).IsEqualTo("openapi/3.1");
        await Assert.That(versions.GetProperty("v3").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
    }

    [Test]
    [Arguments("OpenAPI/3.0")]
    [Arguments("AsyncAPI/3.1")]
    public async Task ApiGroupAndVersionFormatsRejectDistinctCompleteFormatOnMutation(string differentFormat)
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", "{}");
        await Send(engine, RegistryAction.Replace, "/apigroups/g/apis/a", """{"versionid":"v1","format":"OpenAPI/3.1"}""");

        await ExpectCode(() => Send(engine, RegistryAction.Post, "/apigroups/g/apis/a",
            $$"""{"versionid":"v2","format":"{{differentFormat}}"}"""), "mismatched_version_attribute");

        var versions = (await Send(engine, RegistryAction.Read, "/apigroups/g/apis/a/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Select(static item => item.Name).ToArray())
            .IsEquivalentTo(["v1"], StringComparer.Ordinal);
        await Assert.That(versions.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(versions.TryGetProperty("v2", out _)).IsFalse();
    }

    [Test]
    public async Task ApiVersionFormatsCaseOnlyAndExactValuesAreAcceptedDuringCompleteResourceReconciliation()
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", """
            {"apis":{"a":{"versions":{
              "v1":{"format":"OpenAPI/3.1"},
              "v2":{"format":"openapi/3.1"},
              "v3":{"format":"OpenAPI/3.1"}
            }}}}
            """);

        var versions = (await Send(engine, RegistryAction.Read, "/apigroups/g/apis/a/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(3);
        await Assert.That(versions.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(versions.GetProperty("v2").GetProperty("format").GetString()).IsEqualTo("openapi/3.1");
        await Assert.That(versions.GetProperty("v3").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
    }

    [Test]
    [Arguments("OpenAPI/3.0")]
    [Arguments("AsyncAPI/3.1")]
    public async Task DistinctApiVersionFormatsAreRejectedDuringCompleteResourceReconciliationWithoutChangingStoredState(
        string differentFormat)
    {
        using var source = BuiltInRegistryModels.LoadSource(RegistryModelKind.Api);
        var engine = Create(source.RootElement.GetRawText());
        await Send(engine, RegistryAction.Replace, "/apigroups/g", """
            {"apis":{"a":{"versions":{"v1":{"format":"OpenAPI/3.1"}}}}}
            """);

        var importedGroup = new System.Text.Json.Nodes.JsonObject
        {
            ["apis"] = new System.Text.Json.Nodes.JsonObject
            {
                ["a"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["versions"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["v1"] = new System.Text.Json.Nodes.JsonObject { ["format"] = "OpenAPI/3.1" },
                        ["v2"] = new System.Text.Json.Nodes.JsonObject { ["format"] = differentFormat }
                    }
                }
            }
        };
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/apigroups/g", importedGroup.ToJsonString()),
            "mismatched_version_attribute");

        var versions = (await Send(engine, RegistryAction.Read, "/apigroups/g/apis/a/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Select(static item => item.Name).ToArray())
            .IsEquivalentTo(["v1"], StringComparer.Ordinal);
        await Assert.That(versions.GetProperty("v1").GetProperty("format").GetString()).IsEqualTo("OpenAPI/3.1");
        await Assert.That(versions.TryGetProperty("v2", out _)).IsFalse();
    }

    private sealed class IncludeResolver : IRegistryModelResolver
    {
        internal bool Available { get; set; } = true;
        internal int Calls { get; private set; }
        public RegistryJson Resolve(Uri documentUri)
        {
            Calls++;
            return Available ? RegistryJson.Parse(Model) : throw new InvalidOperationException("A stored model must not refetch a mutable include.");
        }
    }

    private sealed class OrderingClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class AncestryClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
