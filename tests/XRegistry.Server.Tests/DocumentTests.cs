// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class DocumentTests
{
    [Test]
    [Arguments("application/octet-stream", "eyJsb29rcyI6Impzb24ifQ==", "filebase64")]
    [Arguments("application/json", "eyJsb29rcyI6Impzb24ifQ==", "file")]
    [Arguments("text/plain", "aGVsbG8K", "file")]
    [Arguments("application/json", "/wCAew==", "filebase64")]
    [Arguments("text/plain", "/wCAew==", "filebase64")]
    [Arguments("application/json", "", "filebase64")]
    public async Task ExactDocumentsSurviveRawAndInlineViews(string contentType, string base64, string expectedAttribute)
    {
        var engine = Create();
        var bytes = Convert.FromBase64String(base64);
        using var document = new MemoryStream(bytes);
        var created = await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/teams/g/files/f"))
        {
            Document = document,
            ContentType = contentType
        }, Writer());
        await Assert.That(created.IsDocument).IsTrue();
        using var echo = created.Document!.OpenRead();
        using var copy = new MemoryStream();
        await echo.CopyToAsync(copy);
        await Assert.That(Convert.ToBase64String(copy.ToArray())).IsEqualTo(base64);
        var stored = await Send(engine, RegistryAction.Read, "/teams/g/files/f");
        using var content = stored.Document!.OpenRead();
        using var body = new MemoryStream();
        await content.CopyToAsync(body);
        await Assert.That(Convert.ToBase64String(body.ToArray())).IsEqualTo(base64);
        var inline = await Send(engine, RegistryAction.Read, "/teams/g/files/f$details", null, new KeyValuePair<string, string?>("inline", "file"));
        await Assert.That(inline.Metadata!.RootElement.TryGetProperty(expectedAttribute, out _)).IsTrue();
        if (expectedAttribute == "filebase64")
        {
            await Assert.That(inline.Metadata.RootElement.GetProperty("filebase64").GetString()).IsEqualTo(base64);
        }
        else if (contentType == "application/json")
        {
            await Assert.That(inline.Metadata.RootElement.GetProperty("file").GetProperty("looks").GetString()).IsEqualTo("json");
        }
        else
        {
            await Assert.That(inline.Metadata.RootElement.GetProperty("file").GetString()).IsEqualTo("hello\n");
        }

        var binary = await Send(engine, RegistryAction.Read, "/teams/g/files/f$details", null, new("inline", "file"), new("binary", null));
        await Assert.That(binary.Metadata!.RootElement.GetProperty("filebase64").GetString()).IsEqualTo(base64);
    }

    [Test]
    public async Task ExportUsesRelativePointersAndNoDefaultProjection()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """{"name":"default name","filebase64":"AAE="}""");
        var export = (await Send(engine, RegistryAction.Read, "/export")).Metadata!.RootElement;
        var resource = export.GetProperty("teams").GetProperty("g").GetProperty("files").GetProperty("f");
        await Assert.That(resource.TryGetProperty("name", out _)).IsFalse();
        await Assert.That(resource.GetProperty("self").GetString()).IsEqualTo("#/teams/g/files/f");
        await Assert.That(resource.GetProperty("meta").GetProperty("defaultversionurl").GetString()).IsEqualTo("#/teams/g/files/f/versions/1");
        await Assert.That(resource.GetProperty("versions").GetProperty("1").GetProperty("filebase64").GetString()).IsEqualTo("AAE=");
        await Assert.That(export.TryGetProperty("modelsource", out _)).IsTrue();
        await Assert.That(export.TryGetProperty("capabilities", out _)).IsTrue();
    }

    [Test]
    public async Task MetadataReplacementPreservesDocumentAndNullMakesItPresentEmpty()
    {
        var engine = Create();
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """{"filebase64":"/wA="}""");
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """{"description":"not the document"}""");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/files/f")).Document!.Length).IsEqualTo(2);
        await Send(engine, RegistryAction.Patch, "/teams/g/files/f$details", """{"file":null}""");
        var empty = await Send(engine, RegistryAction.Read, "/teams/g/files/f");
        await Assert.That(empty.Document).IsNotNull();
        await Assert.That(empty.Document!.Length).IsEqualTo(0);
    }

    [Test]
    public async Task OversizeBodyAndCancellationDoNotPublishImplicitParents()
    {
        var engine = Create(limits: new() { MaxDocumentBytes = 3 });
        using var tooLarge = new MemoryStream(Encoding.UTF8.GetBytes("1234"));
        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/teams/g/files/f"))
        {
            Document = tooLarge
        }, Writer()), "request_too_large");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.That(async () => await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/teams/g"))
        {
            Metadata = RegistryJson.Parse("{}")
        }, Writer(), canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }
}
