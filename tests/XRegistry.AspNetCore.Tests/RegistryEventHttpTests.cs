// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class RegistryEventHttpTests
{
    [Test]
    public async Task SuccessfulHttpMutationReturnsTheExactCommittedEventCorrelation()
    {
        var persistence = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(persistence: persistence);
        using var response = await host.Client.PutAsync("/teams/events", new StringContent("{}", Encoding.UTF8, "application/json"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var correlation = response.Headers.GetValues("xRegistry-xregcorrelationid").Single();
        await Assert.That(Guid.TryParseExact(correlation, "N", out _)).IsTrue();
        using var snapshot = await persistence.ReadSnapshotAsync();
        var batch = snapshot.Find("$events/" + correlation);
        await Assert.That(batch).IsNotNull();
        await Assert.That(batch!.Metadata.RootElement.GetProperty("correlationid").GetString()).IsEqualTo(correlation);
        var events = batch.Metadata.RootElement.GetProperty("events").EnumerateArray().ToArray();
        await Assert.That(events.Length).IsEqualTo(2);
        await Assert.That(events.All(item => item.GetProperty("xregcorrelationid").GetString() == correlation)).IsTrue();
        await Assert.That(events.Select(static item => item.GetProperty("subject").GetString()!).ToArray())
            .IsEquivalentTo(["/", "/teams/events"], StringComparer.Ordinal);
        await Assert.That(events.Select(static item => item.GetProperty("time").GetString()).Distinct().Count()).IsEqualTo(1);
    }
}
