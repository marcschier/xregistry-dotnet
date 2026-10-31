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
    public async Task ModifiedAtAncestryUsesTimestampTiesAndPersistsRepairedOrdering()
    {
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0,"versionmode":"modifiedat"
            }}}}}
            """;
        var store = new InMemoryRegistryPersistence();
        var options = new RegistryEngineOptions
        {
            RegistryId = "ordering",
            PublicRoot = new Uri("https://registry.example"),
            TimeProvider = new OrderingClock(),
            Model = RegistryModel.Compile(RegistryJson.Parse(model))
        };
        var engine = new RegistryEngine(options, store, new PermitPolicy());
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """
            {"Z":{"createdat":"2000-01-01T00:00:00Z","modifiedat":"2040-01-01T00:00:00Z"},
             "b":{"createdat":"2010-01-01T00:00:00Z","modifiedat":"2040-01-01T00:00:00.0000Z"},
             "A":{"createdat":"2020-01-01T00:00:00Z","modifiedat":"2040-01-01T00:00:00Z"}}
            """);
        await AssertChainAsync(["A", "b", "Z"]);
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """
            {"Z":{"modifiedat":"2030-01-01T00:00:00Z"},"Bad":{"modifiedat":"invalid"}}
            """), "invalid_attribute");
        await Assert.That((await Send(new RegistryEngine(options, store, new PermitPolicy()), RegistryAction.Read, "/export"))
            .Metadata!.RootElement.GetRawText()).IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Z",
            """{"modifiedat":"2030-01-01T00:00:00Z"}""");
        await AssertChainAsync(["Z", "A", "b"]);
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/A");
        await AssertChainAsync(["Z", "b"]);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/New",
            """{"createdat":"1990-01-01T00:00:00Z","modifiedat":"2050-01-01T00:00:00Z"}""");
        await AssertChainAsync(["Z", "b", "New"]);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"Z"}""");
        await Send(engine, RegistryAction.Replace, "/modelsource",
            model.Replace("\"maxversions\":0", "\"maxversions\":2", StringComparison.Ordinal));
        await AssertChainAsync(["Z", "New"], "Z");
        var repaired = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/New")).Metadata!.RootElement;
        await Assert.That(repaired.GetProperty("modifiedat").GetString()).IsEqualTo("2040-01-01T00:00:00.0000000Z");
        await Assert.That(repaired.GetProperty("createdat").GetString()).IsEqualTo("1990-01-01T00:00:00Z");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 5);

        async Task AssertChainAsync(string[] ids, string? selected = null)
        {
            var exported = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
            foreach (var current in new[] { engine, new RegistryEngine(options, store, new PermitPolicy()) })
            {
                var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
                await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(ids.Length);
                for (var index = 0; index < ids.Length; index++)
                {
                    var version = versions.GetProperty(ids[index]);
                    await Assert.That(version.GetProperty("ancestorid").GetString()).IsEqualTo(ids[Math.Max(0, index - 1)]);
                    await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsEqualTo(ids[index] == (selected ?? ids[^1]));
                }
                var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
                await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(selected ?? ids[^1]);
                await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(selected is not null);
                await Assert.That((await Send(current, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText()).IsEqualTo(exported);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CreatedAtAncestryReordersTiesUpdatesDeletionAndRetentionAtomically(bool explicitSingleRoot)
    {
        var model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0,"versionmode":"createdat" ROOT
            }}}}}
            """.Replace("ROOT", explicitSingleRoot ? ",\"singleversionroot\":true" : "", StringComparison.Ordinal);
        await Assert.That(RegistryModel.Compile(RegistryJson.Parse(model)).Groups["teams"].Resources["notes"].SingleVersionRoot)
            .IsTrue();
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """
            {"Z":{"ancestorid":"request","createdat":"2040-01-03T00:00:00Z"},
             "b":{"ancestorid":"request","createdat":"2040-01-02T00:00:00Z"},
             "A":{"ancestorid":"request","createdat":"2040-01-02T00:00:00.0000Z"}}
            """);
        await AssertChainAsync(engine, ["A", "b", "Z"]);
        await AssertChainAsync(Create(model, persistence: store), ["A", "b", "Z"]);
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """
            {"Z":{"createdat":"2040-01-01T00:00:00Z"},"Bad":{"createdat":"not-a-timestamp"}}
            """), "invalid_attribute");
        await Assert.That((await Send(Create(model, persistence: store), RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/Z",
            """{"createdat":"2040-01-01T00:00:00Z"}""");
        await AssertChainAsync(engine, ["Z", "A", "b"]);
        await AssertChainAsync(Create(model, persistence: store), ["Z", "A", "b"]);
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/A");
        await AssertChainAsync(engine, ["Z", "b"]);
        await AssertChainAsync(Create(model, persistence: store), ["Z", "b"]);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/New",
            """{"createdat":"2040-01-04T00:00:00Z"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"Z"}""");
        await Send(engine, RegistryAction.Replace, "/modelsource",
            model.Replace("\"maxversions\":0", "\"maxversions\":2", StringComparison.Ordinal));
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            await AssertChainAsync(current, ["Z", "New"], "Z");
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 5);
        }

        static async Task AssertChainAsync(RegistryEngine current, string[] ids, string? selected = null)
        {
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(ids.Length);
            for (var index = 0; index < ids.Length; index++)
            {
                var version = versions.GetProperty(ids[index]);
                await Assert.That(version.GetProperty("ancestorid").GetString()).IsEqualTo(ids[Math.Max(0, index - 1)]);
                await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsEqualTo(ids[index] == (selected ?? ids[^1]));
            }
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(selected ?? ids[^1]);
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(selected is not null);
        }
    }

    [Test]
    public async Task SemverAncestryUsesPrecedenceThroughBatchDeletionRetentionAndRestart()
    {
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0,"versionmode":"semver"
            }}}}}
            """;
        await Assert.That(RegistryModel.Compile(RegistryJson.Parse(model)).Groups["teams"].Resources["notes"].SingleVersionRoot)
            .IsTrue();
        string[] ordered =
        [
            "1.0.0-1", "1.0.0-2", "1.0.0-10", "1.0.0-A", "1.0.0-alpha", "1.0.0-alpha.1",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
            "1.0.1", "1.1.0", "2.0.0", "10.0.0", "999999999999999999999999999999.0.0"
        ];
        var batch = "{" + string.Join(",", ordered.AsEnumerable().Reverse().Select(id =>
            "\"" + id + "\":{\"ancestorid\":\"request\",\"createdat\":\"2000-01-01T00:00:00Z\"}")) + "}";
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", batch);
        await AssertChainAsync(engine, ordered);
        await AssertChainAsync(Create(model, persistence: store), ordered);
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        foreach (var id in new[] { "01.0.0", "1.00.0", "1.0", "1.0.0-01", "1.0.0-alpha..1" })
        {
            await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/" + id, "{}"),
                "invalid_attribute");
            await Assert.That((await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
        await Assert.That((await Send(Create(model, persistence: store), RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/1.0.0-beta.2");
        var remaining = ordered.Where(id => id != "1.0.0-beta.2").ToArray();
        await AssertChainAsync(engine, remaining);
        await AssertChainAsync(Create(model, persistence: store), remaining);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", "{\"defaultversionid\":\"" + ordered[0] + "\"}");
        await Send(engine, RegistryAction.Replace, "/modelsource",
            model.Replace("\"maxversions\":0", "\"maxversions\":3", StringComparison.Ordinal));
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            var retained = new[] { ordered[0], ordered[^2], ordered[^1] };
            await AssertChainAsync(current, retained, ordered[0]);
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 3);
        }

        static async Task AssertChainAsync(RegistryEngine current, string[] ids, string? selected = null)
        {
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(ids.Length);
            for (var index = 0; index < ids.Length; index++)
            {
                var version = versions.GetProperty(ids[index]);
                await Assert.That(version.GetProperty("versionid").GetString()).IsEqualTo(ids[index]);
                await Assert.That(version.GetProperty("ancestorid").GetString()).IsEqualTo(ids[Math.Max(0, index - 1)]);
                await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsEqualTo(ids[index] == (selected ?? ids[^1]));
            }
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(selected ?? ids[^1]);
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(selected is not null);
        }
    }

    [Test]
    [Arguments("manual", "MANUAL")]
    [Arguments("manual", "MaNuAl")]
    [Arguments("createdat", "CREATEDAT")]
    [Arguments("createdat", "CrEaTeDaT")]
    [Arguments("modifiedat", "MODIFIEDAT")]
    [Arguments("modifiedat", "MoDiFiEdAt")]
    [Arguments("semver", "SEMVER")]
    [Arguments("semver", "SeMvEr")]
    public async Task VersionModeCaseVariantsPreservePublicationRetentionAndRestart(string mode, string authoredMode)
    {
        var model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":2,"versionmode":"MODE"
            }}}}}
            """.Replace("MODE", authoredMode, StringComparison.Ordinal);
        await Assert.That(RegistryModel.Compile(RegistryJson.Parse(model)).Groups["teams"].Resources["notes"].VersionMode)
            .IsEqualTo(mode);
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/1.0.0",
            """{"createdat":"2000-01-01T00:00:00Z","modifiedat":"2000-01-01T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/2.0.0",
            """{"createdat":"2010-01-01T00:00:00Z","modifiedat":"2010-01-01T00:00:00Z"}""");
        var meta = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("2.0.0");
        var second = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/2.0.0")).Metadata!.RootElement;
        await Assert.That(second.GetProperty("ancestorid").GetString()).IsEqualTo("1.0.0");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"1.0.0"}""");
        var selected = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/1.0.0")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/3.0.0",
            """{"createdat":"2020-01-01T00:00:00Z","modifiedat":"2020-01-01T00:00:00Z"}""");
        var exported = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(2);
            await Assert.That(versions.TryGetProperty("2.0.0", out _)).IsFalse();
            await Assert.That(versions.GetProperty("1.0.0").GetRawText()).IsEqualTo(selected);
            await Assert.That(versions.GetProperty("3.0.0").GetProperty("ancestorid").GetString())
                .IsEqualTo(mode == "manual" ? "3.0.0" : "1.0.0");
            var retainedMeta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(retainedMeta.GetProperty("defaultversionid").GetString()).IsEqualTo("1.0.0");
            await Assert.That(retainedMeta.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
            await Assert.That((await Send(current, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText()).IsEqualTo(exported);
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ManualImplicitBatchSortsIdsAndInitializesSelfRoot(bool replacement)
    {
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0,"versionmode":"manual"
            }}}}}
            """;
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        const string batch = """
            {"Z":{"createdat":"2040-01-01T00:00:00Z"},
             "b":{"createdat":"2040-01-01T00:00:00Z"},
             "a":{"createdat":"2040-01-01T00:00:00Z"}}
            """;
        await Send(engine, replacement ? RegistryAction.Replace : RegistryAction.Patch,
            replacement ? "/teams/g/notes/n" : "/teams/g/notes/n/versions",
            replacement ? "{\"versions\":" + batch + "}" : batch);
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(3);
            await Assert.That(versions.GetProperty("a").GetProperty("ancestorid").GetString()).IsEqualTo("a");
            await Assert.That(versions.GetProperty("b").GetProperty("ancestorid").GetString()).IsEqualTo("a");
            await Assert.That(versions.GetProperty("Z").GetProperty("ancestorid").GetString()).IsEqualTo("b");
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("Z");
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ManualImplicitBatchReevaluatesNewestAfterBackdatedLeafAndRerootsDeletedParent()
    {
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0,"versionmode":"manual"
            }}}}}
            """;
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """
            {"Root":{"ancestorid":"Root","createdat":"2000-01-01T00:00:00Z"},
             "P":{"ancestorid":"Root","createdat":"2020-01-01T00:00:00Z"},
             "Z":{"ancestorid":"Root","createdat":"2030-01-01T00:00:00Z"}}
            """);
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """
            {"a":{"createdat":"2040-01-01T00:00:00Z"},
             "B":{"createdat":"2010-01-01T00:00:00Z"},
             "c":{"ancestorid":"Missing","createdat":"2050-01-01T00:00:00Z"}}
            """), "unknown_id");
        await Assert.That((await Send(Create(model, persistence: store), RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """
            {"d":{"ancestorid":"d","createdat":"2060-01-01T00:00:00Z"},
             "c":{"createdat":"2050-01-01T00:00:00Z"},
             "B":{"createdat":"2010-01-01T00:00:00Z"},
             "a":{"createdat":"2040-01-01T00:00:00Z"}}
            """);
        await AssertAncestryAsync(engine, false);
        await AssertAncestryAsync(Create(model, persistence: store), false);
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/a");
        await AssertAncestryAsync(engine, true);
        await AssertAncestryAsync(Create(model, persistence: store), true);

        async Task AssertAncestryAsync(RegistryEngine current, bool deleted)
        {
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(deleted ? 6 : 7);
            await Assert.That(versions.TryGetProperty("a", out _)).IsEqualTo(!deleted);
            if (!deleted)
            {
                await Assert.That(versions.GetProperty("a").GetProperty("ancestorid").GetString()).IsEqualTo("Z");
            }
            await Assert.That(versions.GetProperty("B").GetProperty("ancestorid").GetString()).IsEqualTo(deleted ? "B" : "a");
            await Assert.That(versions.GetProperty("c").GetProperty("ancestorid").GetString()).IsEqualTo("P");
            await Assert.That(versions.GetProperty("d").GetProperty("ancestorid").GetString()).IsEqualTo("d");
            await Assert.That(versions.GetProperty("P").GetProperty("ancestorid").GetString()).IsEqualTo("Root");
            await Assert.That(versions.GetProperty("Z").GetProperty("ancestorid").GetString()).IsEqualTo("Root");
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("d");
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + (deleted ? 2 : 1));
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ManualRetentionUsesRootTimestampsThenCaseInsensitiveIds(bool defaultIsOldest, bool tied)
    {
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":3,"versionmode":"manual"
            }}}}}
            """;
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Default",
            "{\"ancestorid\":\"Default\",\"createdat\":\"2040-01-" + (defaultIsOldest ? "01" : "04") + "T00:00:00Z\"}");
        foreach (var id in new[] { "Z", "b" })
        {
            var ancestor = defaultIsOldest ? "Default" : id;
            var day = id == "Z" || tied ? "02" : "03";
            await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/" + id,
                "{\"ancestorid\":\"" + ancestor + "\",\"createdat\":\"2040-01-" + day + "T00:00:00Z\"}");
        }
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"Default"}""");
        var defaults = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/Default")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/New",
            """{"ancestorid":"Default","createdat":"2040-01-05T00:00:00Z"}""");
        var removed = tied ? "b" : "Z";
        var retained = tied ? "Z" : "b";
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(3);
            await Assert.That(versions.TryGetProperty(removed, out _)).IsFalse();
            await Assert.That(versions.GetProperty(retained).GetProperty("ancestorid").GetString())
                .IsEqualTo(defaultIsOldest ? "Default" : retained);
            await Assert.That(versions.GetProperty("New").GetProperty("ancestorid").GetString()).IsEqualTo("Default");
            await Assert.That(versions.GetProperty("Default").GetRawText()).IsEqualTo(defaults);
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("Default");
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 1);
        }
    }

    [Test]
    [Arguments("", true)]
    [Arguments("manual", false)]
    [Arguments("MaNuAl", true)]
    public async Task ManualNewestUsesOnlyLeavesThenTimestampAndCaseInsensitiveId(string mode, bool tied)
    {
        var model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0 MODE
            }}}}}
            """.Replace("MODE", mode.Length == 0 ? "" : ",\"versionmode\":\"" + mode + "\"", StringComparison.Ordinal);
        await Assert.That(RegistryModel.Compile(RegistryJson.Parse(model)).Groups["teams"].Resources["notes"].VersionMode)
            .IsEqualTo("manual");
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Root",
            """{"ancestorid":"Root","createdat":"2040-01-09T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Z",
            """{"ancestorid":"Root","createdat":"2040-01-02T00:00:00Z"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/b",
            "{\"ancestorid\":\"Root\",\"createdat\":\"2040-01-" + (tied ? "02" : "03") + "T00:00:00.0000Z\"}");
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(tied ? "Z" : "b");
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(3);
            await Assert.That(versions.GetProperty("Root").GetProperty("ancestorid").GetString()).IsEqualTo("Root");
            await Assert.That(versions.GetProperty("Z").GetProperty("ancestorid").GetString()).IsEqualTo("Root");
            await Assert.That(versions.GetProperty("b").GetProperty("ancestorid").GetString()).IsEqualTo("Root");
            await Assert.That(versions.GetProperty(tied ? "Z" : "b").GetProperty("isdefault").GetBoolean()).IsTrue();
            await Assert.That(versions.GetProperty(tied ? "b" : "Z").GetProperty("isdefault").GetBoolean()).IsFalse();
            await Assert.That(versions.GetProperty("Root").GetProperty("isdefault").GetBoolean()).IsFalse();
        }
    }

    [Test]
    [Arguments("a")]
    [Arguments("z")]
    public async Task ReducingMaxVersionsToOneRejectsAnyStickyResourceAtomically(string stickyResource)
    {
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0,"versionmode":"semver"
            }}}}}
            """;
        var replacement = model.Replace("\"maxversions\":0", "\"maxversions\":1", StringComparison.Ordinal);
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        foreach (var resource in new[] { "a", "z" })
        {
            await Send(engine, RegistryAction.Replace, "/teams/g/notes/" + resource + "/versions/1.0.0", """{"name":"old"}""");
            await Send(engine, RegistryAction.Replace, "/teams/g/notes/" + resource + "/versions/2.0.0", """{"name":"new"}""");
        }
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/" + stickyResource + "/meta",
            """{"defaultversionid":"1.0.0"}""");
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var sourceBefore = (await Send(engine, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetRawText();
        var compiledBefore = (await Send(engine, RegistryAction.Read, "/model")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            using var snapshot = await store.ReadSnapshotAsync();
            await ExpectCode(() => Send(current, RegistryAction.Replace, "/modelsource", replacement), "setdefaultversionsticky_false");
            using var after = await store.ReadSnapshotAsync();
            await Assert.That(after.Generation).IsEqualTo(snapshot.Generation);
            await Assert.That((await Send(current, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText()).IsEqualTo(before);
            await Assert.That((await Send(current, RegistryAction.Read, "/modelsource")).Metadata!.RootElement.GetRawText()).IsEqualTo(sourceBefore);
            await Assert.That((await Send(current, RegistryAction.Read, "/model")).Metadata!.RootElement.GetRawText()).IsEqualTo(compiledBefore);
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/" + stickyResource + "/meta",
            """{"defaultversionsticky":false}""");
        await Send(engine, RegistryAction.Replace, "/modelsource", replacement);
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            var compiled = (await Send(current, RegistryAction.Read, "/model")).Metadata!.RootElement;
            await Assert.That(compiled.GetProperty("groups").GetProperty("teams").GetProperty("resources")
                .GetProperty("notes").GetProperty("maxversions").GetInt32()).IsEqualTo(1);
            foreach (var resource in new[] { "a", "z" })
            {
                var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/" + resource + "/versions")).Metadata!.RootElement;
                await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(1);
                await Assert.That(versions.GetProperty("2.0.0").GetProperty("ancestorid").GetString()).IsEqualTo("2.0.0");
                var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/" + resource + "/meta")).Metadata!.RootElement;
                await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("2.0.0");
                await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsFalse();
            }
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 2);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetentionRecalculatesRootsWhenOldestVersionIsStickyDefault(bool independentRoot)
    {
        var store = new InMemoryRegistryPersistence();
        var limit = independentRoot ? "4" : "3";
        var model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":LIMIT,"versionmode":"manual"
            }}}}}
            """.Replace("LIMIT", limit, StringComparison.Ordinal);
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/A",
            """{"ancestorid":"A","createdat":"2040-01-01T00:00:00Z","name":"default"}""");
        if (independentRoot)
        {
            await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/R",
                """{"ancestorid":"R","createdat":"2040-01-02T00:00:00Z","name":"other root"}""");
        }
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Z",
            """{"ancestorid":"A","createdat":"2040-01-03T00:00:00Z","name":"child"}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/B",
            """{"ancestorid":"Z","createdat":"2040-01-04T00:00:00Z","name":"grandchild"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionid":"A"}""");
        var before = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
        await Assert.That(before.EnumerateObject().Count()).IsEqualTo(independentRoot ? 4 : 3);
        await Assert.That(before.GetProperty("Z").GetProperty("ancestorid").GetString()).IsEqualTo("A");
        var defaultMetadata = before.GetProperty("A").GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/C",
            """{"ancestorid":"B","createdat":"2040-01-05T00:00:00Z","name":"new leaf"}""");
        await AssertRetainedAsync(engine);
        await AssertRetainedAsync(Create(model, persistence: store));
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """
            {"D":{"ancestorid":"C","createdat":"2040-01-06T00:00:00Z","name":"fourth"},
             "E":{"ancestorid":"D","createdat":"2040-01-07T00:00:00Z","name":"fifth"}}
            """);
        foreach (var current in new[] { engine, Create(model, persistence: store) })
        {
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(independentRoot ? 4 : 3);
            await Assert.That(versions.GetProperty("A").GetRawText()).IsEqualTo(defaultMetadata);
            await Assert.That(versions.TryGetProperty("B", out _)).IsFalse();
            await Assert.That(versions.TryGetProperty("Z", out _)).IsFalse();
            await Assert.That(versions.TryGetProperty("C", out _)).IsEqualTo(independentRoot);
            await Assert.That(versions.GetProperty("D").GetProperty("ancestorid").GetString()).IsEqualTo(independentRoot ? "C" : "D");
            await Assert.That(versions.GetProperty("E").GetProperty("ancestorid").GetString()).IsEqualTo("D");
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 2);
        }

        async Task AssertRetainedAsync(RegistryEngine current)
        {
            var versions = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
            await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(independentRoot ? 4 : 3);
            await Assert.That(versions.TryGetProperty(independentRoot ? "R" : "Z", out _)).IsFalse();
            await Assert.That(versions.GetProperty("A").GetRawText()).IsEqualTo(defaultMetadata);
            await Assert.That(versions.GetProperty("B").GetProperty("ancestorid").GetString())
                .IsEqualTo(independentRoot ? "Z" : "B");
            await Assert.That(versions.GetProperty("C").GetProperty("ancestorid").GetString()).IsEqualTo("B");
            await Assert.That(versions.GetProperty("B").GetProperty("name").GetString()).IsEqualTo("grandchild");
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("A");
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DefaultAndExplicitSetVersionIdModelsAllowClientChosenVersionCreation(bool explicitPolicy)
    {
        var policy = explicitPolicy ? ",\"setversionid\":true" : "";
        var model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0 POLICY
            }}}}}
            """.Replace("POLICY", policy, StringComparison.Ordinal);
        var source = RegistryJson.Parse(model);
        await Assert.That(RegistryModel.Compile(source).Groups["teams"].Resources["notes"].SetVersionId).IsTrue();
        var store = new InMemoryRegistryPersistence();
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Client", """{"name":"route"}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"Second","name":"post"}""");
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """{"Third":{"name":"collection"}}""");
        var restarted = Create(model, persistence: store);
        var versions = (await Send(restarted, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(3);
        foreach (var (id, name) in new[] { ("Client", "route"), ("Second", "post"), ("Third", "collection") })
        {
            var version = versions.GetProperty(id);
            await Assert.That(version.GetProperty("versionid").GetString()).IsEqualTo(id);
            await Assert.That(version.GetProperty("name").GetString()).IsEqualTo(name);
        }
    }

    [Test]
    [Arguments("manual")]
    [Arguments("semver")]
    public async Task ServerAssignedVersionIdsSurviveRestartDeletionAndRejectedPublication(string mode)
    {
        var store = new InMemoryRegistryPersistence();
        var model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"setversionid":false,"maxversions":0,"versionmode":"MODE"
            }}}}}
            """.Replace("MODE", mode, StringComparison.Ordinal);
        var engine = Create(model, persistence: store);
        var first = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"first"}""");
        var firstId = mode == "semver" ? "1.0.0" : "1";
        var secondId = mode == "semver" ? "2.0.0" : "2";
        var thirdId = mode == "semver" ? "3.0.0" : "3";
        await Assert.That(first.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo(firstId);
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        foreach (var (action, path, body) in new[]
        {
            (RegistryAction.Replace, "/teams/g/notes/n/versions/" + secondId, """{"name":"client supplied"}"""),
            (RegistryAction.Post, "/teams/g/notes/n", "{\"versionid\":\"" + secondId + "\",\"name\":\"client supplied\"}"),
            (RegistryAction.Patch, "/teams/g/notes/n/versions", "{\"" + secondId + "\":{\"name\":\"client supplied\"}}"),
            (RegistryAction.Replace, "/teams/g/notes/new", "{\"versionid\":\"" + secondId + "\"}")
        })
        {
            await ExpectCode(() => Send(engine, action, path, body), "versionid_not_allowed");
            await AssertUnchangedAsync(engine);
        }
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"name":""}"""),
            "invalid_attribute");
        await AssertUnchangedAsync(engine);
        engine = Create(model, persistence: store);
        await AssertUnchangedAsync(engine);
        var second = await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"name":"second"}""");
        await Assert.That(second.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo(secondId);
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/" + secondId);
        engine = Create(model, persistence: store);
        var third = await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"name":"third"}""");
        await Assert.That(third.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo(thirdId);
        foreach (var action in new[] { RegistryAction.Patch, RegistryAction.Replace })
        {
            await Send(engine, action, "/teams/g/notes/n/versions/" + firstId,
                "{\"versionid\":\"" + firstId + "\",\"name\":\"updated\"}");
            var updated = (await Send(Create(model, persistence: store), RegistryAction.Read,
                "/teams/g/notes/n/versions/" + firstId)).Metadata!.RootElement;
            await Assert.That(updated.GetProperty("versionid").GetString()).IsEqualTo(firstId);
            await Assert.That(updated.GetProperty("name").GetString()).IsEqualTo("updated");
        }
        var meta = (await Send(Create(model, persistence: store), RegistryAction.Read, "/teams/g/notes/n/meta"))
            .Metadata!.RootElement;
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(thirdId);
        var versions = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions")).Metadata!.RootElement;
        await Assert.That(versions.EnumerateObject().Count()).IsEqualTo(2);
        await Assert.That(versions.TryGetProperty(secondId, out _)).IsFalse();

        async Task AssertUnchangedAsync(RegistryEngine current)
        {
            await Assert.That((await Send(current, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
    }

    [Test]
    [Arguments("null")]
    [Arguments("request")]
    public async Task ReservedVersionIdsRejectAcrossPublicationRoutesWithoutChangingState(string id)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Root", """{"name":"retained"}""");
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        foreach (var (action, path, body) in new[]
        {
            (RegistryAction.Replace, "/teams/g/notes/n/versions/" + id, """{"name":"invalid"}"""),
            (RegistryAction.Post, "/teams/g/notes/n", "{\"versionid\":\"" + id + "\",\"name\":\"invalid\"}"),
            (RegistryAction.Patch, "/teams/g/notes/n/versions", "{\"" + id + "\":{\"name\":\"invalid\"}}"),
            (RegistryAction.Replace, "/teams/g/notes/new", "{\"versionid\":\"" + id + "\"}")
        })
        {
            await ExpectCode(() => Send(engine, action, path, body), "malformed_id");
            await Assert.That((await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
        await Assert.That((await Send(Create(persistence: store), RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        var allowedId = id == "null" ? "Null" : "Request";
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/" + allowedId, """{"name":"case-sensitive"}""");
        var accepted = (await Send(Create(persistence: store), RegistryAction.Read,
            "/teams/g/notes/n/versions/" + allowedId)).Metadata!.RootElement;
        await Assert.That(accepted.GetProperty("versionid").GetString()).IsEqualTo(allowedId);
        await Assert.That(accepted.GetProperty("name").GetString()).IsEqualTo("case-sensitive");
    }

    [Test]
    [Arguments(RegistryAction.Patch)]
    [Arguments(RegistryAction.Replace)]
    public async Task ResourceDefaultVersionIdentityRejectsMismatchesAndIgnoresForgedMembership(RegistryAction action)
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Root", """{"name":"root"}""");
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"versionid":"Child","name":"child"}""");
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        foreach (var id in new[] { "Root", "child", "missing" })
        {
            await ExpectCode(() => Send(engine, action, "/teams/g/notes/n",
                "{\"versionid\":\"" + id + "\",\"name\":\"must not persist\"}"), "mismatched_id");
            await Assert.That((await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
        await Assert.That((await Send(Create(persistence: store), RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
            .IsEqualTo(before);
        await Send(engine, action, "/teams/g/notes/n", """{"versionid":"Child","name":"matched","isdefault":false}""");
        foreach (var input in new[] { "true", "false", "null", "\"True\"", "{}", "1" })
        {
            await Send(engine, action, "/teams/g/notes/n/versions/Root", "{\"name\":\"root\",\"isdefault\":" + input + "}");
            await Send(engine, action, "/teams/g/notes/n/versions/Child", "{\"name\":\"matched\",\"isdefault\":" + input + "}");
            await AssertMembershipAsync(engine);
        }
        await AssertMembershipAsync(Create(persistence: store));
        var doc = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement
            .GetProperty("teams").GetProperty("g").GetProperty("notes").GetProperty("n").GetProperty("versions");
        await Assert.That(doc.GetProperty("Root").GetProperty("isdefault").GetBoolean()).IsFalse();
        await Assert.That(doc.GetProperty("Child").GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(doc.GetProperty("Root").GetProperty("versionid").GetString()).IsEqualTo("Root");
        await Assert.That(doc.GetProperty("Child").GetProperty("versionid").GetString()).IsEqualTo("Child");

        async Task AssertMembershipAsync(RegistryEngine current)
        {
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("Child");
            var root = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions/Root")).Metadata!.RootElement;
            var child = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions/Child")).Metadata!.RootElement;
            await Assert.That(root.GetProperty("versionid").GetString()).IsEqualTo("Root");
            await Assert.That(child.GetProperty("versionid").GetString()).IsEqualTo("Child");
            await Assert.That(root.GetProperty("noteid").GetString()).IsEqualTo("n");
            await Assert.That(child.GetProperty("noteid").GetString()).IsEqualTo("n");
            await Assert.That(root.GetProperty("isdefault").GetBoolean()).IsFalse();
            await Assert.That(child.GetProperty("isdefault").GetBoolean()).IsTrue();
            await Assert.That(child.GetProperty("name").GetString()).IsEqualTo("matched");
        }
    }

    [Test]
    public async Task DefaultSelectionFlagOverridesMetaAndAtomicallyReplacesDeletedDefault()
    {
        var store = new InMemoryRegistryPersistence();
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"maxversions":0,"versionmode":"semver"
            }}}}}
            """;
        var engine = Create(model, persistence: store);
        foreach (var id in new[] { "1.0.0", "2.0.0", "3.0.0" })
        {
            await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/" + id, "{\"name\":\"" + id + "\"}");
        }
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta",
            """{"defaultversionid":"3.0.0","defaultversionsticky":false}""", new KeyValuePair<string, string?>("setdefaultversionid", "1.0.0"));
        await AssertSelectionAsync(engine, "1.0.0", true);
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        await Send(engine, RegistryAction.Delete, "/teams/g/notes/n/versions/1.0.0", null,
            new KeyValuePair<string, string?>("setdefaultversionid", "2.0.0"));
        await AssertSelectionAsync(engine, "2.0.0", true);
        await ExpectCode(() => Send(engine, RegistryAction.Read, "/teams/g/notes/n/versions/1.0.0"), "not_found");
        await Assert.That((await engine.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events + 1);
        engine = Create(model, persistence: store);
        await AssertSelectionAsync(engine, "2.0.0", true);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta",
            """{"defaultversionid":"2.0.0","defaultversionsticky":true}""", new KeyValuePair<string, string?>("setdefaultversionid", "null"));
        await AssertSelectionAsync(engine, "3.0.0", false);
        await AssertSelectionAsync(Create(model, persistence: store), "3.0.0", false);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n", """{"meta":{"defaultversionsticky":false}}""",
            new KeyValuePair<string, string?>("setdefaultversionid", "2.0.0"));
        await AssertSelectionAsync(engine, "2.0.0", true);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions/3.0.0", """{"name":"changed"}""",
            new KeyValuePair<string, string?>("setdefaultversionid", "3.0.0"));
        await AssertSelectionAsync(engine, "3.0.0", true);
        await Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions", """{"4.0.0":{"name":"new target"}}""",
            new KeyValuePair<string, string?>("setdefaultversionid", "4.0.0"));
        await AssertSelectionAsync(engine, "4.0.0", true);
        await AssertSelectionAsync(Create(model, persistence: store), "4.0.0", true);

        async Task AssertSelectionAsync(RegistryEngine current, string id, bool sticky)
        {
            var meta = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
            await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(id);
            await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsEqualTo(sticky);
            var version = (await Send(current, RegistryAction.Read, "/teams/g/notes/n/versions/" + id)).Metadata!.RootElement;
            await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsTrue();
        }
    }

    [Test]
    public async Task DefaultSelectionFlagRejectsInvalidUsesWithoutPersistingMutations()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = Create(persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n/versions/Root", """{"name":"stored"}""");
        var before = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText();
        var events = (await engine.ReadEventBatchesAsync(Writer())).Count;
        foreach (var target in new[] { "missing", "root" })
        {
            await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", """{"defaultversionsticky":true}""",
                new KeyValuePair<string, string?>("setdefaultversionid", target)), "unknown_id");
            await AssertUnchangedAsync(engine);
        }
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/versions",
            """{"New":{"name":"must not persist"}}""", new KeyValuePair<string, string?>("setdefaultversionid", "missing")),
            "unknown_id");
        await AssertUnchangedAsync(engine);
        foreach (var target in new string?[] { null, "", "bad/id" })
        {
            await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", "{}",
                new KeyValuePair<string, string?>("setdefaultversionid", target)), "bad_defaultversionid");
            await AssertUnchangedAsync(engine);
        }
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/notes/n/meta", "{}",
            new("setdefaultversionid", "Root"), new("setdefaultversionid", "Root")), "bad_flag");
        await AssertUnchangedAsync(engine);
        foreach (var (action, path) in new[]
        {
            (RegistryAction.Read, "/teams/g/notes/n"),
            (RegistryAction.Head, "/teams/g/notes/n"),
            (RegistryAction.Options, "/teams/g/notes/n"),
            (RegistryAction.Patch, "/teams/g/notes/n/meta"),
            (RegistryAction.Replace, "/teams/g/notes/n/versions/new"),
            (RegistryAction.Post, "/teams/g/notes/n/versions")
        })
        {
            await ExpectCode(() => Send(engine, action, path, "{}", new KeyValuePair<string, string?>("setdefaultversionid", "request")), "bad_flag");
            await AssertUnchangedAsync(engine);
        }
        foreach (var path in new[] { "/", "/teams", "/teams/g", "/teams/g/notes" })
        {
            await ExpectCode(() => Send(engine, RegistryAction.Patch, path, "{}", new KeyValuePair<string, string?>("setdefaultversionid", "Root")), "bad_flag");
            await AssertUnchangedAsync(engine);
        }
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/notes/n",
            """{"versionid":"Root","name":"changed"}""", new KeyValuePair<string, string?>("setdefaultversionid", "request")),
            "defaultversionid_request");
        await AssertUnchangedAsync(engine);
        await AssertUnchangedAsync(Create(persistence: store));
        var options = await Send(engine, RegistryAction.Options, "/teams/g/notes/n", null,
            new KeyValuePair<string, string?>("unknownflag", "ignored"));
        await Assert.That(options.AllowedActions.Contains(RegistryAction.Options)).IsTrue();
        await Assert.That(options.AllowedActions.Contains(RegistryAction.Read)).IsTrue();
        await AssertUnchangedAsync(engine);

        async Task AssertUnchangedAsync(RegistryEngine current)
        {
            await Assert.That((await Send(current, RegistryAction.Read, "/export")).Metadata!.RootElement.GetRawText())
                .IsEqualTo(before);
            await Assert.That((await current.ReadEventBatchesAsync(Writer())).Count).IsEqualTo(events);
        }
    }

    [Test]
    public async Task DefaultSelectionRequestFlagSelectsOnlyNewAssignedVersionAndPersists()
    {
        var store = new InMemoryRegistryPersistence();
        const string model = """
            {"groups":{"teams":{"singular":"team","resources":{"notes":{
              "singular":"note","hasdocument":false,"setversionid":false,"maxversions":0
            }}}}}
            """;
        var engine = Create(model, persistence: store);
        await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"first"}""");
        var created = await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"name":"assigned"}""",
            new KeyValuePair<string, string?>("setdefaultversionid", "request"));
        var id = created.Metadata!.RootElement.GetProperty("versionid").GetString()!;
        await Assert.That(id).IsEqualTo("2");
        await Assert.That(created.Metadata.RootElement.GetProperty("isdefault").GetBoolean()).IsTrue();
        var meta = (await Send(engine, RegistryAction.Read, "/teams/g/notes/n/meta")).Metadata!.RootElement;
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo(id);
        await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
        await Send(engine, RegistryAction.Post, "/teams/g/notes/n", """{"name":"later"}""");
        var persisted = (await Send(Create(model, persistence: store), RegistryAction.Read, "/teams/g/notes/n/meta"))
            .Metadata!.RootElement;
        await Assert.That(persisted.GetProperty("defaultversionid").GetString()).IsEqualTo(id);
        await Assert.That(persisted.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
        var selected = (await Send(Create(model, persistence: store), RegistryAction.Read,
            "/teams/g/notes/n/versions/" + id)).Metadata!.RootElement;
        await Assert.That(selected.GetProperty("name").GetString()).IsEqualTo("assigned");
        await Assert.That(selected.GetProperty("isdefault").GetBoolean()).IsTrue();
    }

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
