// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.AspNetCore;
using XRegistry.Client;
using XRegistry.Server;

namespace XRegistry.Http.Tests;

public class RegistryDocumentHeaderTests
{
    private const string DocumentPath = "teams/g/files/f";
    private static readonly RegistryModel s_model = RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"teams":{"singular":"team","resources":{
          "notes":{"singular":"note","hasdocument":false},
          "files":{"singular":"file","attributes":{
            "rank":{"type":"integer"},"amount":{"type":"decimal"},"enabled":{"type":"boolean"},
            "anyvalue":{"type":"any"},"items":{"type":"array","item":{"type":"string"}},
            "readonlyvalue":{"type":"string","readonly":true}}}}}}}
        """));
    private static readonly RegistryResourceDefinition s_resource = s_model.Groups["teams"].Resources["files"];

    [Test]
    public async Task ModelAwareStreamedWriteAndReadUseActualMapXRegistryAndLiteralHeaders()
    {
        string? nameHeader = null;
        string? labelHeader = null;
        string? contentType = null;
        var puts = 0;
        await using var app = await StartRegistryAsync(context =>
        {
            if (context.Request.Method == "PUT")
            {
                Interlocked.Increment(ref puts);
                nameHeader = context.Request.Headers["xRegistry-name"].ToString();
                labelHeader = context.Request.Headers["xRegistry-labels.a.b"].ToString();
                contentType = context.Request.ContentType;
            }
        });
        using var client = Client(app);
        using var source = new ObservedStream([0, 255, 13, 10, 0x80, 0x7b, 0]);
        using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, RegistryJson.Parse("""
            {"fileid":"f","versionid":"v1","name":"Euro \u20ac \ud83d\ude00 \r\n\u0000\t\"%",
             "labels":{"a.b":"one% two","empty":""},"rank":184467440737095516160001,
             "amount":1.2345678901234567890123456789,"enabled":false,
             "readonlyvalue":"ignored","contenttype":"application/octet-stream"}
            """), s_resource);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(puts).IsEqualTo(1);
        await Assert.That(nameHeader).IsEqualTo("Euro%20%E2%82%AC%20%F0%9F%98%80%20%0D%0A%00%09%22%25");
        await Assert.That(labelHeader).IsEqualTo("one%25%20two");
        await Assert.That(contentType).IsEqualTo("application/octet-stream");
        await Assert.That(response.Headers.GetValues("xRegistry-name").Single()).IsEqualTo(nameHeader);
        await Assert.That(response.Headers.Contains("xRegistry-contenttype")).IsFalse();

        var metadata = response.ReadHeaderMetadata(s_resource);
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        await Assert.That(metadata.RootElement.GetProperty("xid").GetString()).IsEqualTo("/" + DocumentPath);
        await Assert.That(metadata.RootElement.GetProperty("fileid").GetString()).IsEqualTo("f");
        await Assert.That(metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(metadata.RootElement.GetProperty("epoch").GetRawText()).IsEqualTo("0");
        await Assert.That(metadata.RootElement.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(metadata.RootElement.GetProperty("rank").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("amount").GetRawText()).IsEqualTo("1.2345678901234567890123456789");
        await Assert.That(metadata.RootElement.GetProperty("enabled").GetBoolean()).IsFalse();
        await Assert.That(metadata.GetPresence("readonlyvalue")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(metadata.RootElement.GetProperty("labels").GetProperty("empty").GetString()).IsEqualTo("");
        await Assert.That(metadata.RootElement.GetProperty("contenttype").GetString()).IsEqualTo("application/octet-stream");
        using var destination = new MemoryStream();
        await response.CopyDocumentToAsync(destination);
        await Assert.That(Convert.ToHexString(destination.ToArray())).IsEqualTo("00FF0D0A807B00");
        await Assert.That(response.BodyBytesRead).IsEqualTo(7L);
        await Assert.That(response.ReadHeaderMetadata(s_resource).RootElement.GetProperty("name").GetString())
            .IsEqualTo("Euro \u20ac \U0001f600 \r\n\0\t\"%");
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
        await Assert.That(destination.CanWrite).IsTrue();

        using var read = await client.SendAsync(HttpMethod.Get, DocumentPath);
        var readMetadata = read.ReadHeaderMetadata(s_resource);
        using var downloaded = new MemoryStream();
        await read.CopyDocumentToAsync(downloaded);
        await Assert.That(Convert.ToHexString(downloaded.ToArray())).IsEqualTo("00FF0D0A807B00");
        await Assert.That(readMetadata.RootElement.GetProperty("self").GetString())
            .IsEqualTo("https://public.example/registry/" + DocumentPath);
        read.Dispose();
        response.Dispose();
        client.Dispose();
        await Assert.That(metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(readMetadata.RootElement.GetProperty("rank").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(() => read.ReadHeaderMetadata(s_resource)).Throws<ObjectDisposedException>();
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ActualMapAcceptsPostAndExplicitVersionDocumentTargets()
    {
        await using var app = await StartRegistryAsync();
        using var client = Client(app);
        using var firstBytes = new MemoryStream([1]);
        using var first = await client.SendDocumentAsync(HttpMethod.Post, DocumentPath, firstBytes,
            RegistryJson.Parse("""{"versionid":"v1","name":"first"}"""), s_resource);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(first.ReadHeaderMetadata(s_resource).RootElement.GetProperty("versionid").GetString()).IsEqualTo("v1");

        using var nextBytes = new MemoryStream([2]);
        using var next = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath + "/versions/v2", nextBytes,
            RegistryJson.Parse("""{"versionid":"v2","name":"second"}"""), s_resource);
        await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var metadata = next.ReadHeaderMetadata(s_resource);
        await Assert.That(metadata.RootElement.GetProperty("xid").GetString()).IsEqualTo("/" + DocumentPath + "/versions/v2");
        await Assert.That(metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("v2");
        using var bytes = new MemoryStream();
        await next.CopyDocumentToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("02");
    }

    [Test]
    public async Task ActualMapPreservesAbsentValuesDeletesNullsAndReplacesWholeMaps()
    {
        await using var app = await StartRegistryAsync();
        using var client = Client(app);
        using var initialBytes = new MemoryStream([1]);
        using var initial = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, initialBytes, RegistryJson.Parse("""
            {"name":"keep","description":"remove","labels":{"old":"remove","a.b":"replace"},"contenttype":"text/plain"}
            """), s_resource);
        await Assert.That(initial.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var empty = new MemoryStream();
        using var replacement = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, empty,
            RegistryJson.Parse("""{"description":null,"labels":{"a.b":""}}"""), s_resource);
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var replaced = replacement.ReadHeaderMetadata(s_resource);
        await Assert.That(replaced.RootElement.GetProperty("name").GetString()).IsEqualTo("keep");
        await Assert.That(replaced.GetPresence("description")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(replaced.RootElement.GetProperty("labels").TryGetProperty("old", out _)).IsFalse();
        await Assert.That(replaced.RootElement.GetProperty("labels").GetProperty("a.b").GetString()).IsEqualTo("");
        await Assert.That(replaced.GetPresence("contenttype")).IsEqualTo(JsonPresence.Absent);
        using var bytes = new MemoryStream();
        await replacement.CopyDocumentToAsync(bytes);
        await Assert.That(bytes.Length).IsEqualTo(0L);

        using var cleared = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, empty,
            RegistryJson.Parse("""{"description":"","labels":null,"contenttype":null}"""), s_resource);
        await Assert.That(cleared.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var final = cleared.ReadHeaderMetadata(s_resource);
        await Assert.That(final.RootElement.GetProperty("name").GetString()).IsEqualTo("keep");
        await Assert.That(final.RootElement.GetProperty("description").GetString()).IsEqualTo("");
        await Assert.That(final.GetPresence("labels")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(final.GetPresence("contenttype")).IsEqualTo(JsonPresence.Absent);
    }

    [Test]
    public async Task ActualMapEpochGuardsKeepExactLargeValuesAndRejectWithoutChangingDocument()
    {
        await using var app = await StartRegistryAsync();
        using var client = Client(app);
        using var firstBytes = new MemoryStream([1]);
        using var created = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, firstBytes, RegistryJson.Parse("{}"), s_resource);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var nextBytes = new MemoryStream([2]);
        using var updated = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, nextBytes,
            RegistryJson.Parse("""{"epoch":0,"name":"updated"}"""), s_resource);
        await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(updated.ReadHeaderMetadata(s_resource).RootElement.GetProperty("epoch").GetRawText()).IsEqualTo("1");

        foreach (var epoch in new[] { "0", "184467440737095516160001" })
        {
            using var rejectedBytes = new MemoryStream([3]);
            using var rejected = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, rejectedBytes,
                RegistryJson.Parse("{\"epoch\":" + epoch + ",\"name\":\"not written\"}"), s_resource);
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var problem = await rejected.ReadMetadataAsync();
            await Assert.That(problem.RootElement.GetProperty("code").GetString()).IsEqualTo("mismatched_epoch");
            await Assert.That(problem.RootElement.GetProperty("args").GetProperty("bad_epoch").GetString()).IsEqualTo(epoch);
            await Assert.That(problem.RootElement.GetProperty("args").GetProperty("epoch").GetString()).IsEqualTo("1");
        }

        using var current = await client.SendAsync(HttpMethod.Get, DocumentPath);
        await Assert.That(current.ReadHeaderMetadata(s_resource).RootElement.GetProperty("name").GetString()).IsEqualTo("updated");
        using var currentBytes = new MemoryStream();
        await current.CopyDocumentToAsync(currentBytes);
        await Assert.That(Convert.ToHexString(currentBytes.ToArray())).IsEqualTo("02");
    }

    [Test]
    public async Task LiteralNullWrittenThroughDetailsRemainsLiteralInDocumentResponse()
    {
        await using var app = await StartRegistryAsync();
        using var client = Client(app);
        using var bytes = new MemoryStream([0, 255]);
        using var created = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, bytes, RegistryJson.Parse("{}"), s_resource);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var changed = await client.SendAsync(HttpMethod.Patch, DocumentPath + "$details",
            Encoding.UTF8.GetBytes("""{"name":"null","labels":{"a":"null"}}"""), "application/json");
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var response = await client.SendAsync(HttpMethod.Get, DocumentPath);
        await Assert.That(response.Headers.GetValues("xRegistry-name").Single()).IsEqualTo("null");
        var metadata = response.ReadHeaderMetadata(s_resource);
        await Assert.That(metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("null");
        await Assert.That(metadata.RootElement.GetProperty("labels").GetProperty("a").GetString()).IsEqualTo("null");
        using var body = new MemoryStream();
        await response.CopyDocumentToAsync(body);
        await Assert.That(Convert.ToHexString(body.ToArray())).IsEqualTo("00FF");
    }

    [Test]
    [Arguments("xRegistry-name", "%C0%A0", "header_error", "An attribute header has invalid quoting or percent encoding.")]
    [Arguments("xRegistry-file", "null", "extra_xregistry_header", "This attribute must not be sent as an xRegistry header.")]
    [Arguments("xRegistry-contenttype", "text/plain", "extra_xregistry_header", "This attribute must not be sent as an xRegistry header.")]
    [Arguments("xRegistry-enabled", "1", "header_error", "A typed scalar header has the wrong value kind.")]
    [Arguments("xRegistry-unknown", "value", "header_error", "The header attribute is not defined by the model.")]
    public async Task ActualMapRetainsStandardizedHeaderErrorStatusSubjectAndName(
        string name, string value, string code, string message)
    {
        await using var app = await StartRegistryAsync();
        using var transport = new RegistryHttpConnectionPolicy(Root(app), allowLoopbackHttp: true).CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(Root(app), DocumentPath))
        {
            Content = new ByteArrayContent([1])
        };
        request.Headers.TryAddWithoutValidation(name, value);
        using var rejected = await transport.SendAsync(request);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem.GetProperty("type").GetString())
            .IsEqualTo("https://github.com/xregistry/spec/blob/main/core/http.md#" + code);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(problem.GetProperty("subject").GetString()).IsEqualTo("/registry/" + DocumentPath);
        await Assert.That(problem.GetProperty("args").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(problem.GetProperty("args").GetProperty("error_detail").GetString()).IsEqualTo(message.TrimEnd('.'));
        using var client = Client(app);
        using var root = await client.SendAsync(HttpMethod.Get);
        using var metadata = await root.ReadMetadataAsync();
        await Assert.That(metadata.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"file":null}""", "extra_xregistry_header")]
    [Arguments("""{"filebase64":"AP8="}""", "extra_xregistry_header")]
    [Arguments("""{"name":"null"}""", "header_error")]
    [Arguments("""{"labels":{"a":"null"}}""", "header_error")]
    [Arguments("""{"labels":{}}""", "header_error")]
    [Arguments("""{"labels":{"":"bad"}}""", "header_error")]
    [Arguments("""{"labels":{"a":"one","A":"two"}}""", "header_error")]
    [Arguments("""{"labels":{"a:b":"bad"}}""", "header_error")]
    [Arguments("""{"items":[]}""", "header_error")]
    [Arguments("""{"anyvalue":42}""", "header_error")]
    [Arguments("""{"epoch":1.5}""", "header_error")]
    [Arguments("""{"contenttype":"not a media type"}""", "header_error")]
    [Arguments("""{"contenttype":"text/plain\r\nHost: other"}""", "header_error")]
    [Arguments("""{"contenttype":42}""", "header_error")]
    [Arguments("""{"name":"\uD800"}""", "invalid_unicode")]
    public async Task InvalidMetadataFailsBeforeCredentialsStreamReadsAndDispatch(string json, string code)
    {
        var credentials = 0;
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app =>
            app.Map("/{**path}", () => { Interlocked.Increment(ref requests); return Results.StatusCode(503); }));
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            AuthorizationProvider = (_, _) =>
            {
                Interlocked.Increment(ref credentials);
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var source = new ObservedStream([1, 2, 3]);
        var failure = await RejectAsync(() => client.SendDocumentAsync(
            HttpMethod.Put, DocumentPath, source, RegistryJson.Parse(json), s_resource));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(credentials).IsEqualTo(0);
        await Assert.That(requests).IsEqualTo(0);
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments("PUT", "teams/g/files/f$details")]
    [Arguments("PUT", "teams/g/files/f/meta")]
    [Arguments("POST", "teams/g/files")]
    [Arguments("PUT", "teams/g")]
    [Arguments("PUT", "teams/g/files/f/versions")]
    [Arguments("PUT", "teams/g/other/f")]
    [Arguments("PATCH", "teams/g/files/f")]
    [Arguments("DELETE", "teams/g/files/f")]
    [Arguments("GET", "teams/g/files/f")]
    public async Task MetadataBodyOrMismatchedTargetsAreRejectedBeforeCredentialsAndStreamReads(string method, string path)
    {
        var credentials = 0;
        using var client = new XRegistryHttpClient(new Uri("https://example.invalid/registry/"), new()
        {
            AuthorizationProvider = (_, _) =>
            {
                credentials++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var source = new ObservedStream([1]);
        await Assert.That(async () =>
        {
            using var response = await client.SendDocumentAsync(new HttpMethod(method), path, source,
                RegistryJson.Parse("""{"name":"no dispatch"}"""), s_resource);
        }).Throws<ArgumentException>();
        await Assert.That(credentials).IsEqualTo(0);
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task DocumentlessModelsAndNonemptyExternalUrlStreamsFailBeforeCredentials()
    {
        var credentials = 0;
        using var client = new XRegistryHttpClient(new Uri("https://example.invalid/"), new()
        {
            AuthorizationProvider = (_, _) =>
            {
                credentials++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var source = new ObservedStream([1]);
        await Assert.That(async () =>
        {
            using var response = await client.SendDocumentAsync(HttpMethod.Put, "teams/g/notes/n", source,
                RegistryJson.Parse("{}"), s_model.Groups["teams"].Resources["notes"]);
        }).Throws<ArgumentException>();
        await Assert.That(async () =>
        {
            using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source,
                RegistryJson.Parse("""{"fileurl":"https://documents.example/file"}"""), s_resource);
        }).Throws<ArgumentException>();
        await Assert.That(credentials).IsEqualTo(0);
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ExternalDocumentUrlAcceptsAKnownEmptyStreamWithoutFollowingTheUrl()
    {
        string? url = null;
        long? length = null;
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapPut("/registry/" + DocumentPath,
            (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                url = context.Request.Headers["xRegistry-fileurl"].ToString();
                length = context.Request.ContentLength;
                context.Response.StatusCode = 204;
                return Task.CompletedTask;
            }));
        using var client = Client(app);
        using var source = new ObservedStream([], seekable: true);
        using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source,
            RegistryJson.Parse("""{"fileurl":"https://documents.example/file"}"""), s_resource);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(url).IsEqualTo("https://documents.example/file");
        await Assert.That(length).IsEqualTo(0L);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task HeaderByteBoundaryAndPlusOneAreEnforcedBeforeCredentialsOrDispatch()
    {
        var credentials = 0;
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapPut("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                context.Response.StatusCode = 204;
            }));
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            MaxHeaderBytes = 49,
            MaxHeaderCount = 2,
            AuthorizationProvider = (_, _) =>
            {
                Interlocked.Increment(ref credentials);
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var first = new ObservedStream([1]);
        using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, first,
            RegistryJson.Parse("""{"name":"\u20ac","labels":{"k":""}}"""), s_resource);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var extra = new ObservedStream([2]);
        var failure = await RejectAsync(() => client.SendDocumentAsync(HttpMethod.Put, DocumentPath, extra,
            RegistryJson.Parse("""{"name":"\u20aca","labels":{"k":""}}"""), s_resource));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("request_headers_too_large");
        await Assert.That(credentials).IsEqualTo(1);
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(extra.ReadCalls).IsEqualTo(0);
        await Assert.That(first.DisposeCalls).IsEqualTo(0);
        await Assert.That(extra.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(0, 1024, 64, "request_headers_too_large")]
    [Arguments(128, 11, 64, "byte_limit")]
    [Arguments(128, 1024, 1, "depth_limit")]
    public async Task HeaderCountAndJsonBudgetsFailBeforeCredentialsAndStreamReads(int count, int bytes, int depth, string code)
    {
        var credentials = 0;
        using var client = new XRegistryHttpClient(new Uri("https://example.invalid/"), new()
        {
            MaxHeaderCount = count,
            MaxMetadataBytes = bytes,
            MaxJsonDepth = depth,
            AuthorizationProvider = (_, _) =>
            {
                credentials++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var source = new ObservedStream([1]);
        var metadata = RegistryJson.Parse(depth == 1 ? """{"labels":{"k":"v"}}""" : """{"name":"x"}""");
        var failure = await RejectAsync(() => client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, metadata, s_resource));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(credentials).IsEqualTo(0);
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ModelAwareWritesPreserveOrderedQueriesContentNegotiationAndDecodedBytes(bool compressed)
    {
        string? query = null;
        string? negotiation = null;
        string? upload = null;
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapPut("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                query = context.Request.Path + context.Request.QueryString;
                negotiation = context.Request.Headers.AcceptEncoding.ToString();
                using var received = new MemoryStream();
                await context.Request.Body.CopyToAsync(received, context.RequestAborted);
                upload = Convert.ToHexString(received.ToArray());
                context.Response.Headers["xRegistry-name"] = "\"quoted%20value\"";
                context.Response.Headers["xRegistry-epoch"] = "184467440737095516160001";
                context.Response.ContentType = "application/octet-stream";
                if (compressed)
                {
                    context.Response.Headers.ContentEncoding = "gzip";
                }

                await context.Response.Body.WriteAsync(compressed ?
                    Convert.FromBase64String("H4sIAAAAAAACCmP4z8vF2MAAABuixNQHAAAA") : [0, 255, 13, 10, 1, 128, 0],
                    context.RequestAborted);
            }));
        using var client = Client(app, new() { AllowLoopbackHttp = true, EnableContentDecoding = compressed });
        using var source = new ObservedStream([0, 255, 13, 10, 1, 128, 0]);
        using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source,
            RegistryJson.Parse("""{"contenttype":"application/octet-stream"}"""), s_resource,
            [new("filter", "a=1"), new("filter", "a=2"), new("inline", null), new("empty", "")]);
        await Assert.That(query).IsEqualTo("/registry/" + DocumentPath + "?filter=a%3D1&filter=a%3D2&inline&empty=");
        await Assert.That(negotiation).IsEqualTo(compressed ? "gzip, deflate, br" : "identity");
        await Assert.That(upload).IsEqualTo("00FF0D0A018000");
        var metadata = response.ReadHeaderMetadata(s_resource);
        await Assert.That(metadata.RootElement.GetProperty("name").GetString()).IsEqualTo("quoted value");
        await Assert.That(metadata.RootElement.GetProperty("epoch").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        using var bytes = new MemoryStream();
        await response.CopyDocumentToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("00FF0D0A018000");
        await Assert.That(string.Join(", ", response.ContentHeaders.ContentEncoding)).IsEqualTo(compressed ? "gzip" : "");
    }

    [Test]
    public async Task MetadataCannotOverrideAuthorizationHostCookiesOrProxyCredentials()
    {
        string? authorization = null;
        string? host = null;
        string? cookies = null;
        string? proxyAuthorization = null;
        string? metadataAuthorization = null;
        string? metadataHost = null;
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapPut("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                authorization = context.Request.Headers.Authorization.ToString();
                host = context.Request.Headers.Host.ToString();
                cookies = context.Request.Headers.Cookie.ToString();
                proxyAuthorization = context.Request.Headers["Proxy-Authorization"].ToString();
                metadataAuthorization = context.Request.Headers["xRegistry-authorization"].ToString();
                metadataHost = context.Request.Headers["xRegistry-host"].ToString();
                await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                context.Response.StatusCode = 204;
            }));
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            AuthorizationProvider = (_, _) => ValueTask.FromResult<AuthenticationHeaderValue?>(new("Bearer", "fixture-only"))
        });
        var resource = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"files":{"singular":"file","attributes":{"*":{"type":"string"}}}}}}}
            """)).Groups["teams"].Resources["files"];
        using var source = new ObservedStream([]);
        using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, RegistryJson.Parse("""
            {"authorization":"metadata-only","host":"foreign.invalid","cookie":"not-a-cookie","proxy_authorization":"not-a-credential"}
            """), resource);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(authorization).IsEqualTo("Bearer fixture-only");
        await Assert.That(host).IsEqualTo(Root(app).Authority);
        await Assert.That(cookies).IsEqualTo("");
        await Assert.That(proxyAuthorization).IsEqualTo("");
        await Assert.That(metadataAuthorization).IsEqualTo("metadata-only");
        await Assert.That(metadataHost).IsEqualTo("foreign.invalid");
    }

    [Test]
    public async Task ServiceUnavailableNeverRetriesOrDisposesTheBorrowedUploadStream()
    {
        var requests = 0;
        var credentials = 0;
        string? body = null;
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapPut("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                using var received = new MemoryStream();
                await context.Request.Body.CopyToAsync(received, context.RequestAborted);
                body = Convert.ToHexString(received.ToArray());
                context.Response.StatusCode = 503;
            }));
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            AuthorizationProvider = (_, _) =>
            {
                Interlocked.Increment(ref credentials);
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var source = new ObservedStream([0, 255, 13, 10]);
        using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source,
            RegistryJson.Parse("""{"name":"once","contenttype":"application/octet-stream"}"""), s_resource);
        response.Dispose();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body).IsEqualTo("00FF0D0A");
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(credentials).IsEqualTo(1);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
        await Assert.That(source.CanRead).IsTrue();
    }

    [Test]
    public async Task SeekableDocumentSizeAndQueryLimitsStillRejectBeforeCredentials()
    {
        var credentials = 0;
        using var client = new XRegistryHttpClient(new Uri("https://example.invalid/"), new()
        {
            MaxDocumentBytes = 3,
            MaxQueryParameters = 1,
            AuthorizationProvider = (_, _) =>
            {
                credentials++;
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var source = new ObservedStream([1, 2, 3, 4], seekable: true);
        await Assert.That(async () =>
        {
            using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, RegistryJson.Parse("{}"), s_resource);
        }).Throws<ArgumentException>();
        await Assert.That(async () =>
        {
            using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, RegistryJson.Parse("{}"), s_resource,
                [new("one", null), new("two", "")]);
        }).Throws<ArgumentException>();
        await Assert.That(credentials).IsEqualTo(0);
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(source.Position).IsEqualTo(0L);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task NonseekableDocumentSizeIsEnforcedWithoutReplayOrDisposal()
    {
        var requests = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapPut("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                try
                {
                    await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                }
                catch (IOException) when (context.RequestAborted.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                }
            }));
        using var client = Client(app, new() { AllowLoopbackHttp = true, MaxDocumentBytes = 3 });
        using var source = new ObservedStream([1, 2, 3, 4]);
        await Assert.That(async () =>
        {
            using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, RegistryJson.Parse("{}"), s_resource);
        }).Throws<InvalidDataException>();
        await Assert.That(requests).IsLessThanOrEqualTo(1);
        await Assert.That(source.ReadCalls).IsEqualTo(1);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
        await Assert.That(source.CanRead).IsTrue();
    }

    [Test]
    public async Task ModelAwareUploadStillUsesTheOriginalRequestDeadline()
    {
        var credentials = 0;
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapPut("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                try
                {
                    await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                }
                catch (IOException) when (context.RequestAborted.IsCancellationRequested)
                {
                }
            }));
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            RequestTimeout = TimeSpan.FromSeconds(2),
            AuthorizationProvider = (_, _) =>
            {
                Interlocked.Increment(ref credentials);
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var source = new BlockingStream();
        var send = client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source,
            RegistryJson.Parse("""{"name":"deadline"}"""), s_resource).AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => { using var response = await send; }).Throws<OperationCanceledException>();
        await Assert.That(credentials).IsEqualTo(1);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments("xRegistry-name", "%C0%A0")]
    [Arguments("xRegistry-epoch", "not-a-number")]
    [Arguments("xRegistry-isdefault", "1")]
    [Arguments("xRegistry-items", "[]")]
    public async Task InvalidResponseMetadataDoesNotConsumeOrInvalidateDocumentBytes(string name, string value)
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapGet("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                context.Response.Headers[name] = value;
                await context.Response.Body.WriteAsync(new byte[] { 0, 255 }, context.RequestAborted);
            }));
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, DocumentPath);
        await Assert.That(() => response.ReadHeaderMetadata(s_resource)).Throws<RegistryException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        using var bytes = new MemoryStream();
        await response.CopyDocumentToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("00FF");
    }

    [Test]
    public async Task RepeatedRawResponseFieldsAreNotHiddenByTypedHeaderNormalization()
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapGet("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                context.Response.Headers.Append("xRegistry-labels.a", "one");
                context.Response.Headers.Append("XREGISTRY-LABELS.A", "two");
                await context.Response.Body.WriteAsync(new byte[] { 0, 255 }, context.RequestAborted);
            }));
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, DocumentPath);
        await Assert.That(() => response.ReadHeaderMetadata(s_resource)).Throws<RegistryException>();
        using var bytes = new MemoryStream();
        await response.CopyDocumentToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("00FF");
    }

    [Test]
    public async Task ResponseHeaderBudgetsAreIndependentOfBodyConsumption()
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapGet("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                context.Response.Headers["xRegistry-name"] = "value";
                await context.Response.Body.WriteAsync(new byte[] { 0, 255 }, context.RequestAborted);
            }));
        using var client = Client(app, new() { AllowLoopbackHttp = true, MaxHeaderCount = 0 });
        using var response = await client.SendAsync(HttpMethod.Get, DocumentPath);
        await Assert.That(() => response.ReadHeaderMetadata(s_resource)).Throws<RegistryException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        using var bytes = new MemoryStream();
        await response.CopyDocumentToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("00FF");
    }

    [Test]
    [Arguments(1, 1024, 64, "too_large")]
    [Arguments(32768, 11, 64, "byte_limit")]
    [Arguments(32768, 1024, 1, "depth_limit")]
    public async Task ResponseHeaderByteAndJsonLimitsUseClientBudgets(int headerBytes, int jsonBytes, int depth, string code)
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapGet("/registry/" + DocumentPath,
            (HttpContext context) =>
            {
                context.Response.Headers["xRegistry-labels.k"] = "v";
                context.Response.StatusCode = 200;
                return Task.CompletedTask;
            }));
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            MaxHeaderBytes = headerBytes,
            MaxMetadataBytes = jsonBytes,
            MaxJsonDepth = depth
        });
        using var response = await client.SendAsync(HttpMethod.Get, DocumentPath);
        RegistryException? failure = null;
        try
        {
            response.ReadHeaderMetadata(s_resource);
        }
        catch (RegistryException exception)
        {
            failure = exception;
        }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        using var bytes = new MemoryStream();
        await response.CopyDocumentToAsync(bytes);
        await Assert.That(bytes.Length).IsEqualTo(0L);
    }

    [Test]
    public async Task ReadingHeaderMetadataDoesNotResetTheResponseBodyDeadline()
    {
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapGet("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                context.Response.Headers["xRegistry-name"] = "available%20now";
                await context.Response.Body.WriteAsync(new byte[] { 1 }, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                }
            }));
        using var client = Client(app, new() { AllowLoopbackHttp = true, RequestTimeout = TimeSpan.FromSeconds(2) });
        using var response = await client.SendAsync(HttpMethod.Get, DocumentPath);
        await Assert.That(response.ReadHeaderMetadata(s_resource).RootElement.GetProperty("name").GetString()).IsEqualTo("available now");
        using var bytes = new MemoryStream();
        await Assert.That(async () => await response.CopyDocumentToAsync(bytes)).Throws<OperationCanceledException>();
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("01");
    }

    [Test]
    public async Task NewHeaderLimitsAndDisposedClientsAreValidatedExplicitly()
    {
        var root = new Uri("https://example.invalid/");
        await Assert.That(() => new XRegistryHttpClient(root, new() { MaxHeaderBytes = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new XRegistryHttpClient(root, new() { MaxHeaderBytes = 1024 * 1024 + 1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new XRegistryHttpClient(root, new() { MaxHeaderCount = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new XRegistryHttpClient(root, new() { MaxHeaderCount = 65537 })).Throws<ArgumentOutOfRangeException>();
        using var client = new XRegistryHttpClient(root);
        using var source = new ObservedStream([1]);
        client.Dispose();
        await Assert.That(async () =>
        {
            using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, RegistryJson.Parse("{}"), s_resource);
        }).Throws<ObjectDisposedException>();
        await Assert.That(source.ReadCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ConditionalContentTypeHeadersWorkThroughClientAndActualMapXRegistry()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"files":{"singular":"file","attributes":{
              "contenttype":{"type":"string","ifvalues":{"application/octet-stream":{"siblingattributes":{
                "measurements":{"type":"map","item":{"type":"uinteger"}}}}}}}}}}}}
            """));
        var resource = model.Groups["teams"].Resources["files"];
        var input = RegistryJson.Parse("""{"measurements":{"a.b":184467440737095516160001},"contenttype":"application/octet-stream"}""");
        var valid = RegistryMetadataValidator.Validate(input, resource.Attributes, new() { Mode = RegistryMetadataMode.ClientInput });
        await Assert.That(valid.Metadata.RootElement.GetProperty("measurements").GetProperty("a.b").GetRawText())
            .IsEqualTo("184467440737095516160001");
        var requests = 0;
        string? measurement = null;
        await using var app = await StartRegistryAsync(context =>
        {
            Interlocked.Increment(ref requests);
            if (context.Request.Method == "PUT")
            {
                measurement = context.Request.Headers["xRegistry-measurements.a.b"].ToString();
            }
        }, model);
        using var client = Client(app);
        using var source = new ObservedStream([0, 255, 13, 10]);
        using var created = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, input, resource);

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(measurement).IsEqualTo("184467440737095516160001");
        var headers = created.ReadHeaderMetadata(resource);
        await Assert.That(created.BodyBytesRead).IsEqualTo(0L);
        await Assert.That(headers.RootElement.GetProperty("measurements").GetProperty("a.b").GetRawText())
            .IsEqualTo("184467440737095516160001");
        await Assert.That(headers.RootElement.GetProperty("contenttype").GetString()).IsEqualTo("application/octet-stream");
        using var bytes = new MemoryStream();
        await created.CopyDocumentToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("00FF0D0A");
        await Assert.That(source.DisposeCalls).IsEqualTo(0);

        using var read = await client.SendAsync(HttpMethod.Get, DocumentPath);
        await Assert.That(read.ReadHeaderMetadata(resource).RootElement.GetProperty("measurements").GetProperty("a.b").GetRawText())
            .IsEqualTo("184467440737095516160001");
        await Assert.That(requests).IsEqualTo(2);
    }

    [Test]
    public async Task ConditionalNestedClientWritePreservesReadonlyResponseTypesAndSingleDispatch()
    {
        var model = ConditionalModel();
        var resource = model.Groups["teams"].Resources["files"];
        var requests = 0;
        var credentials = 0;
        string? sample = null;
        string? scale = null;
        await using var app = await StartRegistryAsync(context =>
        {
            Interlocked.Increment(ref requests);
            sample = context.Request.Headers["xRegistry-samples.a.b"].ToString();
            scale = context.Request.Headers["xRegistry-scale"].ToString();
        }, model);
        using var source = new ObservedStream([0, 128, 255, 13, 10]);
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            AuthorizationProvider = (_, _) =>
            {
                if (source.ReadCalls != 0)
                {
                    throw new InvalidOperationException("The source was read before authorization.");
                }
                Interlocked.Increment(ref credentials);
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var response = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, RegistryJson.Parse("""
            {"samples":{"a.b":184467440737095516160001,"zero":0},"scale":1.2345678901234567890123456789,
             "generated":"invalid but ignored","serial":184467440737095516160002,"extra":"string",
             "revision":"v2","family":"SENSOR","contenttype":"application/octet-stream"}
            """), resource);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(credentials).IsEqualTo(1);
        await Assert.That(sample).IsEqualTo("184467440737095516160001");
        await Assert.That(scale).IsEqualTo("1.2345678901234567890123456789");
        await Assert.That(response.Headers.Contains("xRegistry-generated")).IsTrue();
        var metadata = response.ReadHeaderMetadata(resource);
        await Assert.That(metadata.RootElement.GetProperty("samples").GetProperty("a.b").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("samples").GetProperty("zero").GetInt32()).IsEqualTo(0);
        await Assert.That(metadata.RootElement.GetProperty("scale").GetRawText()).IsEqualTo("1.2345678901234567890123456789");
        await Assert.That(metadata.RootElement.GetProperty("generated").GetRawText()).IsEqualTo("184467440737095516160003");
        await Assert.That(metadata.RootElement.GetProperty("serial").GetRawText()).IsEqualTo("184467440737095516160002");
        await Assert.That(metadata.RootElement.GetProperty("extra").GetString()).IsEqualTo("string");
        await Assert.That(metadata.RootElement.GetProperty("family").GetString()).IsEqualTo("SENSOR");
        await Assert.That(metadata.RootElement.GetProperty("revision").GetString()).IsEqualTo("v2");
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        using var bytes = new MemoryStream();
        await response.CopyDocumentToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("0080FF0D0A");
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConditionalRawNestedHeadersWorkInEitherOrderThroughActualMap(bool reverse)
    {
        var model = ConditionalModel();
        var resource = model.Groups["teams"].Resources["files"];
        await using var app = await StartRegistryAsync(model: model);
        using var transport = new RegistryHttpConnectionPolicy(Root(app), allowLoopbackHttp: true).CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(Root(app), DocumentPath))
        {
            Content = new ByteArrayContent([0, 255])
        };
        KeyValuePair<string, string>[] fields =
        [
            new("xRegistry-samples.a.b", "184467440737095516160001"),
            new("xRegistry-scale", "1.2345678901234567890123456789"),
            new("xRegistry-generated", "%invalid"),
            new("xRegistry-revision", "\"v%32\""),
            new("xRegistry-family", "%73eNsOr")
        ];
        foreach (var field in reverse ? Enumerable.Reverse(fields) : fields)
        {
            request.Headers.TryAddWithoutValidation(field.Key, field.Value);
        }
        using var created = await transport.SendAsync(request);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.GetValues("xRegistry-samples.a.b").Single()).IsEqualTo("184467440737095516160001");
        await Assert.That(created.Headers.GetValues("xRegistry-generated").Single()).IsEqualTo("184467440737095516160003");
        await Assert.That(Convert.ToHexString(await created.Content.ReadAsByteArrayAsync())).IsEqualTo("00FF");
        using var client = Client(app);
        using var read = await client.SendAsync(HttpMethod.Get, DocumentPath);
        var metadata = read.ReadHeaderMetadata(resource);
        await Assert.That(metadata.RootElement.GetProperty("samples").GetProperty("a.b").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("scale").GetRawText()).IsEqualTo("1.2345678901234567890123456789");
        await Assert.That(metadata.RootElement.GetProperty("revision").GetString()).IsEqualTo("v2");
    }

    [Test]
    [Arguments("""{"samples":{"a.b":1},"revision":"v2"}""")]
    [Arguments("""{"samples":{"a.b":1},"family":"off"}""")]
    [Arguments("""{"samples":{"a.b":"1"},"family":"sensor","revision":"v2"}""")]
    [Arguments("""{"samples":{"a.b":1},"family":"sensor","revision":42}""")]
    public async Task ConditionalInvalidOrMissingContextFailsBeforeCredentialsReadsAndDispatch(string json)
    {
        var model = ConditionalModel();
        var resource = model.Groups["teams"].Resources["files"];
        var requests = 0;
        var credentials = 0;
        await using var app = await StartRegistryAsync(_ => Interlocked.Increment(ref requests), model);
        using var client = Client(app, new()
        {
            AllowLoopbackHttp = true,
            AuthorizationProvider = (_, _) =>
            {
                Interlocked.Increment(ref credentials);
                return ValueTask.FromResult<AuthenticationHeaderValue?>(null);
            }
        });
        using var source = new ObservedStream([1]);
        var failure = await RejectAsync(() => client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source, RegistryJson.Parse(json), resource));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(credentials).IsEqualTo(0);
        await Assert.That(requests).IsEqualTo(0);
        await Assert.That(source.ReadCalls).IsEqualTo(0);
        await Assert.That(source.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments("inactive")]
    [Arguments("missing")]
    public async Task ConditionalRawInactiveOrMissingContextKeepsNamedErrorsAndNoPublication(string context)
    {
        var model = ConditionalModel();
        await using var app = await StartRegistryAsync(model: model);
        using var transport = new RegistryHttpConnectionPolicy(Root(app), allowLoopbackHttp: true).CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(Root(app), DocumentPath))
        {
            Content = new ByteArrayContent([1])
        };
        request.Headers.TryAddWithoutValidation("xRegistry-samples.a.b", "42");
        if (context == "inactive")
        {
            request.Headers.TryAddWithoutValidation("xRegistry-family", "off");
        }
        using var rejected = await transport.SendAsync(request);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("header_error");
        await Assert.That(problem.GetProperty("args").GetProperty("name").GetString()).IsEqualTo("xRegistry-samples.a.b");
        await Assert.That(problem.GetProperty("subject").GetString()).IsEqualTo("/registry/" + DocumentPath);
        using var client = Client(app);
        using var root = await client.SendAsync(HttpMethod.Get);
        using var metadata = await root.ReadMetadataAsync();
        await Assert.That(metadata.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task ConditionalDiscriminatorEnumsRemainValidatedByTheActualEngine()
    {
        var model = ModelWithAttributes("""
            {"kind":{"type":"string","enum":["counter","off"],"ifvalues":{"counter":{"siblingattributes":{"reading":{"type":"integer"}}}}}}
            """);
        var resource = model.Groups["teams"].Resources["files"];
        await using var app = await StartRegistryAsync(model: model);
        using var client = Client(app);
        using var source = new ObservedStream([1]);
        using var rejected = await client.SendDocumentAsync(HttpMethod.Put, DocumentPath, source,
            RegistryJson.Parse("""{"kind":"COUNTER","reading":42}"""), resource);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var problem = await rejected.ReadMetadataAsync();
        await Assert.That(problem.RootElement.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(problem.RootElement.GetProperty("args").GetProperty("name").GetString()).IsEqualTo("kind");
        using var root = await client.SendAsync(HttpMethod.Get);
        using var metadata = await root.ReadMetadataAsync();
        await Assert.That(metadata.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task ConditionalConflictKeepsTheCoreErrorCodeAndAttributeNameThroughActualMap()
    {
        var model = ModelWithAttributes("""
            {"left":{"type":"string","ifvalues":{"on":{"siblingattributes":{"reading":{"type":"integer"}}}}},
             "right":{"type":"string","ifvalues":{"on":{"siblingattributes":{"reading":{"type":"decimal"}}}}}}
            """);
        await using var app = await StartRegistryAsync(model: model);
        using var transport = new RegistryHttpConnectionPolicy(Root(app), allowLoopbackHttp: true).CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(Root(app), DocumentPath))
        {
            Content = new ByteArrayContent([1])
        };
        request.Headers.TryAddWithoutValidation("xRegistry-reading", "42");
        request.Headers.TryAddWithoutValidation("xRegistry-left", "on");
        request.Headers.TryAddWithoutValidation("xRegistry-right", "on");
        using var rejected = await transport.SendAsync(request);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem.GetProperty("type").GetString()).IsEqualTo("https://github.com/xregistry/spec/blob/main/core/spec.md#invalid_attribute");
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(problem.GetProperty("args").GetProperty("name").GetString()).IsEqualTo("reading");
        await Assert.That(problem.GetProperty("args").GetProperty("error_detail").GetString()).IsEqualTo("Active conditional definitions conflict");
    }

    [Test]
    public async Task ConditionalMissingResponseContextDoesNotReadOrRefetchTheDocument()
    {
        var requests = 0;
        var resource = ConditionalModel().Groups["teams"].Resources["files"];
        await using var app = await XRegistryHttpClientTests.StartAsync(app => app.MapGet("/registry/" + DocumentPath,
            async (HttpContext context) =>
            {
                Interlocked.Increment(ref requests);
                context.Response.Headers["xRegistry-samples.a.b"] = "42";
                await context.Response.Body.WriteAsync(new byte[] { 0, 255 }, context.RequestAborted);
            }));
        using var client = Client(app);
        using var response = await client.SendAsync(HttpMethod.Get, DocumentPath);
        await Assert.That(() => response.ReadHeaderMetadata(resource)).Throws<RegistryException>();
        await Assert.That(response.BodyBytesRead).IsEqualTo(0L);
        using var bytes = new MemoryStream();
        await response.CopyDocumentToAsync(bytes);
        await Assert.That(Convert.ToHexString(bytes.ToArray())).IsEqualTo("00FF");
        await Assert.That(requests).IsEqualTo(1);
    }

    private static RegistryModel ModelWithAttributes(string attributes) => RegistryModel.Compile(RegistryJson.Parse(
        """{"groups":{"teams":{"singular":"team","resources":{"files":{"singular":"file","attributes":""" +
        attributes + "}}}}}"));

    private static RegistryModel ConditionalModel() => RegistryModel.Compile(RegistryJson.Parse("""
        {
          "groups": {
            "teams": {
              "singular": "team",
              "resources": {
                "files": {
                  "singular": "file",
                  "attributes": {
                    "serial": { "type": "uinteger" },
                    "family": {
                      "type": "string",
                      "ifvalues": {
                        "sensor": {
                          "siblingattributes": {
                            "revision": {
                              "type": "string",
                              "ifvalues": {
                                "v2": {
                                  "siblingattributes": {
                                    "samples": { "type": "map", "item": { "type": "uinteger" } },
                                    "scale": { "type": "decimal" },
                                    "generated": { "type": "uinteger", "readonly": true, "required": true, "default": 184467440737095516160003 },
                                    "*": { "type": "string" }
                                  }
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """));

    private static Uri Root(WebApplication app) => new(new Uri(app.Urls.Single()), "/registry/");

    private static XRegistryHttpClient Client(WebApplication app, XRegistryHttpClientOptions? options = null) =>
        new(Root(app), options ?? new() { AllowLoopbackHttp = true });

    private static Task<WebApplication> StartRegistryAsync(Action<HttpContext>? observe = null, RegistryModel? model = null) =>
        XRegistryHttpClientTests.StartAsync(app =>
        {
            app.Use(async (context, next) =>
            {
                observe?.Invoke(context);
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "header-test")], "test"));
                await next(context);
            });
            var engine = new RegistryEngine(new()
            {
                RegistryId = "headers-test",
                PublicRoot = new Uri("https://public.example/registry"),
                Model = model ?? s_model,
                AllowAnonymousReads = true
            }, new InMemoryRegistryPersistence(), new PermitPolicy());
            app.MapXRegistry(engine, new() { MountPath = "/registry" });
        });

    private static async Task<RegistryException> RejectAsync(Func<ValueTask<XRegistryHttpResponse>> operation)
    {
        try
        {
            using var response = await operation();
        }
        catch (RegistryException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected a RegistryException before dispatch.");
    }

    private sealed class PermitPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class ObservedStream(byte[] bytes, bool seekable = false) : Stream
    {
        private readonly MemoryStream _source = new(bytes, writable: false);
        internal int ReadCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public override bool CanRead => _source.CanRead;
        public override bool CanWrite => false;
        public override bool CanSeek => seekable;
        public override long Length => seekable ? _source.Length : throw new NotSupportedException();
        public override long Position
        {
            get => seekable ? _source.Position : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return _source.Read(buffer, offset, count);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return _source.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
                _source.Dispose();
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposeCalls++; }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
