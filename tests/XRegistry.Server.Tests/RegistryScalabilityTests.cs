// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryScalabilityTests
{
    internal const string Model = """
        {"groups":{"dirs":{"singular":"dir","resources":{"records":{"singular":"record","hasdocument":false,
          "attributes":{"ordinal":{"type":"integer"},"payload":{"type":"string"}}}}}}}
        """;

    [Test]
    public async Task DefaultLimitsAcceptTheMeasuredThreeHundredToThreeHundredTwentyFiveResourcePost()
    {
        var engine = Create(Model);
        await Seed(engine, 300);
        var result = await Send(engine, RegistryAction.Post, "/dirs/perf/records", Batch(300, 25).ToJsonString());
        await Assert.That(result.Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(25);
        await Assert.That(result.Metadata.RootElement.GetProperty("r000324").GetProperty("ordinal").GetInt32()).IsEqualTo(324);
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/perf")).Metadata!.RootElement
            .GetProperty("recordscount").GetInt32()).IsEqualTo(325);
    }

    [Test]
    public async Task DefaultLimitsSupportThousandResourceBatchesAtomicNestedWritesAndIdentityRollback()
    {
        var persistence = new InMemoryRegistryPersistence();
        var engine = Create(Model, persistence);
        await Seed(engine, 1000);

        await Assert.That(await Generation(persistence)).IsEqualTo(40L);
        var group = await Send(engine, RegistryAction.Read, "/dirs/perf");
        await Assert.That(group.Metadata!.RootElement.GetProperty("recordscount").GetInt32()).IsEqualTo(1000);
        var rootBefore = await Send(engine, RegistryAction.Read, "/");
        await Assert.That(Epoch(rootBefore)).IsEqualTo(BigInteger.One);
        var firstBefore = await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000000");
        var lastBefore = await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000999");

        await Send(engine, RegistryAction.Patch, "/", """
            {"name":"atomic nested update","dirs":{"perf":{"records":{
              "r000000":{"versions":{"v1":{"payload":"nested first"}}},
              "r000999":{"versions":{"v1":{"payload":"nested last"}}}
            }}}}
            """);
        await Assert.That(await Generation(persistence)).IsEqualTo(41L);
        var first = await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000000");
        var last = await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000999");
        await Assert.That(first.Metadata!.RootElement.GetProperty("payload").GetString()).IsEqualTo("nested first");
        await Assert.That(last.Metadata!.RootElement.GetProperty("payload").GetString()).IsEqualTo("nested last");
        await Assert.That(Epoch(first)).IsEqualTo(Epoch(firstBefore) + 1);
        await Assert.That(Epoch(last)).IsEqualTo(Epoch(lastBefore) + 1);
        var rootAfter = await Send(engine, RegistryAction.Read, "/");
        await Assert.That(Epoch(rootAfter)).IsEqualTo(Epoch(rootBefore) + 1);

        var metaBefore = await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000500/meta");
        await Send(engine, RegistryAction.Patch, "/dirs/perf/records/r000500",
            """{"payload":"point metadata update","meta":{"defaultversionsticky":true}}""");
        var metaAfter = await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000500/meta");
        await Assert.That(metaAfter.Metadata!.RootElement.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000500")).Metadata!.RootElement
            .GetProperty("payload").GetString()).IsEqualTo("point metadata update");
        await Assert.That(Epoch(metaAfter)).IsEqualTo(Epoch(metaBefore) + 1);
        await Assert.That(await Generation(persistence)).IsEqualTo(42L);
        await Assert.That(Epoch(await Send(engine, RegistryAction.Read, "/"))).IsEqualTo(Epoch(rootAfter));
        await Assert.That(Epoch(await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000000"))).IsEqualTo(Epoch(first));

        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """
            {"name":"must roll back","dirs":{"perf":{"records":{
              "r000000":{"payload":"must roll back"},
              "r000001":{"recordid":"different"}
            }}}}
            """), "mismatched_id");
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000000")).Metadata!.RootElement
            .GetProperty("payload").GetString()).IsEqualTo("nested first");
        await Assert.That(Epoch(await Send(engine, RegistryAction.Read, "/dirs/perf/records/r000000"))).IsEqualTo(Epoch(first));
        await Assert.That(Epoch(await Send(engine, RegistryAction.Read, "/"))).IsEqualTo(Epoch(rootAfter));
        await Assert.That(await Generation(persistence)).IsEqualTo(42L);

        await ExpectCode(() => Send(engine, RegistryAction.Post, "/dirs/perf/records", """{"R000000":{}}"""), "mismatched_id");
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/dirs/perf/records", """{"new-id":{},"NEW-ID":{}}"""), "mismatched_id");
        await Assert.That((await Send(engine, RegistryAction.Read, "/dirs/perf")).Metadata!.RootElement
            .GetProperty("recordscount").GetInt32()).IsEqualTo(1000);
        await Assert.That(await Generation(persistence)).IsEqualTo(42L);
    }

    internal static BigInteger Epoch(RegistryResult result) =>
        RegistryNumber.FromElement(result.Metadata!.RootElement.GetProperty("epoch")).ToBigInteger();

    internal static async Task Seed(RegistryEngine engine, int count)
    {
        for (var offset = 0; offset < count; offset += 25)
        {
            var created = await Send(engine, RegistryAction.Post, "/dirs/perf/records", Batch(offset, 25).ToJsonString());
            await Assert.That(created.Metadata!.RootElement.EnumerateObject().Count()).IsEqualTo(25);
        }
    }

    internal static JsonObject Batch(int offset, int count)
    {
        var batch = new JsonObject();
        for (var index = offset; index < offset + count; index++)
        {
            batch["r" + index.ToString("D6", CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["versions"] = new JsonObject
                {
                    ["v1"] = new JsonObject
                    {
                        ["ordinal"] = index,
                        ["payload"] = "deterministic-native-server-performance-fixture"
                    }
                }
            };
        }

        return batch;
    }

    internal static async Task<long> Generation(IRegistryPersistence persistence)
    {
        using var snapshot = await persistence.ReadSnapshotAsync();
        return snapshot.Generation;
    }
}
