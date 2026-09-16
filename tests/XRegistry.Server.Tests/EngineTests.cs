// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Server.Tests;

public class EngineTests
{
    internal const string Model = """
        {"groups":{"teams":{"singular":"team","resources":{
          "notes":{"singular":"note","hasdocument":false},
          "files":{"singular":"file","maxversions":2}}}}}
        """;

    [Test]
    public async Task RootCapabilitiesAndDiscoveryUseConfiguredPublicRoot()
    {
        var engine = Create();
        var root = await engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/")), Reader());
        await Assert.That(root.Metadata!.RootElement.GetProperty("registryid").GetString()).IsEqualTo("test");
        await Assert.That(root.Metadata.RootElement.GetProperty("self").GetString()).IsEqualTo("https://registry.example/catalog");
        await Assert.That(root.Metadata.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        var capabilities = await engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/capabilities")), Reader());
        await Assert.That(capabilities.Metadata!.RootElement.GetProperty("available").GetProperty("model")
            .GetProperty("mutable").GetBoolean()).IsFalse();
        var discovery = await engine.ExecuteAsync(new(RegistryAction.Read, RegistryPath.Parse("/.xregistry")), Reader());
        await Assert.That(discovery.Metadata!.RootElement.GetProperty("registries")[0].GetString())
            .IsEqualTo("https://registry.example/catalog");
    }

    [Test]
    public async Task NestedFailureRollsBackEveryEntity()
    {
        var engine = Create();
        await Assert.That(async () => await Send(engine, RegistryAction.Replace, "/", """
            {"teams":{"good":{"name":"valid","notes":{"n":{"name":"valid"}}},"bad":{"undeclared":1}}}
            """)).Throws<RegistryException>();
        var unchanged = await Send(engine, RegistryAction.Read, "/");
        await Assert.That(unchanged.Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        await Assert.That(unchanged.Metadata.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        var created = await Send(engine, RegistryAction.Replace, "/teams/g/notes/n", """{"name":"first"}""");
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(created.Metadata!.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
        var parent = await Send(engine, RegistryAction.Read, "/teams/g");
        await Assert.That(parent.Metadata!.RootElement.GetProperty("notescount").GetInt32()).IsEqualTo(1);
        var root = await Send(engine, RegistryAction.Read, "/");
        await Assert.That(root.Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(1);
    }

    internal static ValueTask<RegistryResult> Send(RegistryEngine engine, RegistryAction action, string path,
        string? metadata = null, params KeyValuePair<string, string?>[] parameters) =>
        engine.ExecuteAsync(new(action, RegistryPath.Parse(path))
        {
            Metadata = metadata is null ? null : RegistryJson.Parse(metadata),
            Parameters = parameters
        }, Writer());

    internal static RegistryEngine Create(string model = Model, IRegistryPersistence? persistence = null,
        IRegistryAuthorizationPolicy? policy = null, RegistryLimits? limits = null) =>
        new(new RegistryEngineOptions
        {
            RegistryId = "test",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = RegistryModel.Compile(RegistryJson.Parse(model)),
            AllowAnonymousReads = true,
            Limits = limits ?? new()
        }, persistence ?? new InMemoryRegistryPersistence(), policy ?? new PermitPolicy());

    internal static RegistryOperationContext Reader() => new(new ClaimsPrincipal(new ClaimsIdentity()));
    internal static RegistryOperationContext Writer() => new(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "test-writer")], "test")));

    internal sealed class PermitPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
