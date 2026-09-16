// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;

namespace XRegistry.Server.Tests;

public class CustomModelDocumentationTests
{
    [Test]
    public async Task XrProxyExampleCompilesAndServesDocumentAndDocumentlessResources()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Documentation", "xrproxy-model.json");
        using var input = File.OpenRead(path);
        var source = await RegistryJson.ParseAsync(input);
        var model = RegistryModel.Compile(source, new() { SourceUri = new Uri(path) });

        await Assert.That(model.Attributes.ContainsKey("environment")).IsTrue();
        var group = model.Groups["proxygroups"];
        await Assert.That(group.Resources["proxies"].HasDocument).IsTrue();
        await Assert.That(group.Resources["proxies"].Attributes["upstream"].Required).IsTrue();
        await Assert.That(group.Resources["policies"].HasDocument).IsFalse();

        var engine = new RegistryEngine(new()
        {
            RegistryId = "xrproxy",
            PublicRoot = new Uri("https://registry.example/xrproxy"),
            Model = model,
            InitialMetadata = RegistryJson.Parse("""{"environment":"development"}""")
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        var created = await Send(engine, RegistryAction.Replace,
            "/proxygroups/github/proxies/api$details", """
                {"upstream":"https://api.github.com/","protocol":"https","enabled":true}
                """);
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);

        var bytes = Encoding.UTF8.GetBytes("""{"routes":["/v1/proxy"]}""");
        using var document = new MemoryStream(bytes);
        await engine.ExecuteAsync(new(
            RegistryAction.Replace, RegistryPath.Parse("/proxygroups/github/proxies/api"))
        {
            Document = document,
            ContentType = "application/json"
        }, Writer());

        var root = await Send(engine, RegistryAction.Read, "/");
        await Assert.That(root.Metadata!.RootElement.GetProperty("environment").GetString())
            .IsEqualTo("development");
        var details = await Send(engine, RegistryAction.Read, "/proxygroups/github/proxies/api$details");
        await Assert.That(details.Metadata!.RootElement.GetProperty("upstream").GetString())
            .IsEqualTo("https://api.github.com/");
        await Assert.That(details.Metadata.RootElement.GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(details.Metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("1");
        var stored = await Send(engine, RegistryAction.Read, "/proxygroups/github/proxies/api");
        using var content = stored.Document!.OpenRead();
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        await Assert.That(copy.ToArray()).IsEquivalentTo(bytes);
        var version = await Send(engine, RegistryAction.Read, "/proxygroups/github/proxies/api/versions/1");
        using var versionContent = version.Document!.OpenRead();
        using var versionCopy = new MemoryStream();
        await versionContent.CopyToAsync(versionCopy);
        await Assert.That(versionCopy.ToArray()).IsEquivalentTo(bytes);

        var policy = await Send(engine, RegistryAction.Replace, "/proxygroups/github/policies/default",
            """{"mode":"allow"}""");
        await Assert.That(policy.IsDocument).IsFalse();
        await Assert.That(policy.Metadata!.RootElement.GetProperty("mode").GetString()).IsEqualTo("allow");
    }
}
