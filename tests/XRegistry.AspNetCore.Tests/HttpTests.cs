// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Models;
using XRegistry.Publication;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class HttpTests
{
    [Test]
    public async Task PublicationProjectionRequiresReadOnlyBackendAndEnforcesItsResponseBudget()
    {
        var snapshot = await CreateOpenedSnapshot();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        await using var application = builder.Build();
        var writableBackend = new TestRegistryHostBackend { ReadOnly = false };
        var writable = await Assert.That(() =>
                application.MapPublicationSnapshot(writableBackend, snapshot))
            .Throws<ArgumentException>() ?? throw new InvalidOperationException("Expected writable backend rejection.");
        await Assert.That(writable.ParamName).IsEqualTo("backend");

        var limitedBackend = new TestRegistryHostBackend
        {
            ReadOnly = true,
            Limits = new() { MaxResponseBytes = snapshot.OpenRecord.GetExactBytes().Length - 1 }
        };
        var overBudget = await Assert.That(() =>
                application.MapPublicationSnapshot(limitedBackend, snapshot))
            .Throws<ArgumentOutOfRangeException>() ?? throw new InvalidOperationException("Expected context budget rejection.");
        await Assert.That(overBudget.ParamName).IsEqualTo("snapshot");
    }

    [Test]
    public async Task PublicationContextGetReturnsExactOpenedRecordBytes()
    {
        var snapshot = await CreateOpenedSnapshot();
        var backend = new TestRegistryHostBackend
        {
            ReadOnly = true,
            Limits = new() { MaxResponseBytes = snapshot.OpenRecord.GetExactBytes().Length }
        };
        await using var host = await TestHost.StartAsync(hostBackend: backend, publicationSnapshot: snapshot);

        using var response = await host.Client.GetAsync(PublicationEndpointRouteBuilderExtensions.ContextPath);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That(bytes.SequenceEqual(snapshot.OpenRecord.GetExactBytes())).IsTrue();
        await Assert.That(backend.AuthorizationCount).IsEqualTo(1);
        await Assert.That(backend.ExecutionCount).IsZero();
    }

    [Test]
    public async Task PublicationContextHeadRetainsContentLengthAndHasNoBody()
    {
        var snapshot = await CreateOpenedSnapshot();
        await using var host = await TestHost.StartAsync(hostBackend: new TestRegistryHostBackend { ReadOnly = true },
            publicationSnapshot: snapshot);
        using var request = new HttpRequestMessage(HttpMethod.Head, PublicationEndpointRouteBuilderExtensions.ContextPath);

        using var response = await host.Client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentLength).IsEqualTo(snapshot.OpenRecord.GetExactBytes().Length);
        await Assert.That(await response.Content.ReadAsByteArrayAsync()).IsEmpty();
    }

    [Test]
    public async Task PublicationContextRejectsUnsupportedMethodsWith405()
    {
        var snapshot = await CreateOpenedSnapshot();
        await using var host = await TestHost.StartAsync(hostBackend: new TestRegistryHostBackend { ReadOnly = true },
            publicationSnapshot: snapshot);
        using var response = await host.Client.PostAsync(PublicationEndpointRouteBuilderExtensions.ContextPath, Json("{}"));
        var problem = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("action_not_supported");
    }

    [Test]
    public async Task PublicationContextRouteRequiresReadAuthorization()
    {
        var snapshot = await CreateOpenedSnapshot();
        var backend = new TestRegistryHostBackend { ReadOnly = true, DenyAuthorization = true };
        await using var host = await TestHost.StartAsync(hostBackend: backend, publicationSnapshot: snapshot);
        using var response = await host.Client.GetAsync(PublicationEndpointRouteBuilderExtensions.ContextPath);
        var problem = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("forbidden");
        await Assert.That(backend.AuthorizationCount).IsEqualTo(1);
        await Assert.That(backend.ExecutionCount).IsZero();

        using var headRequest = new HttpRequestMessage(HttpMethod.Head,
            PublicationEndpointRouteBuilderExtensions.ContextPath);
        using var headResponse = await host.Client.SendAsync(headRequest);
        await Assert.That(headResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(backend.AuthorizationCount).IsEqualTo(2);
        await Assert.That(backend.ExecutionCount).IsZero();
    }

    [Test]
    public async Task PublicationSnapshotRootLinkRelationTargetsTheContextUrl()
    {
        var snapshot = await CreateOpenedSnapshot();
        await using var host = await TestHost.StartAsync(hostBackend: new TestRegistryHostBackend { ReadOnly = true },
            publicationSnapshot: snapshot);

        using var response = await host.Client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.GetValues("Link").Single()).IsEqualTo(
            "<https://public.example/registry>;rel=xregistry-root, " +
            "<https://public.example/registry" + PublicationEndpointRouteBuilderExtensions.ContextPath +
            ">;rel=\"https://xregistry.io/workingdrafts/publication/context\"");
    }

    [Test]
    public async Task PublicationProjectionLeavesOrdinaryNonRootLinkHeaderUnchanged()
    {
        var snapshot = await CreateOpenedSnapshot();
        await using var host = await TestHost.StartAsync(hostBackend: new TestRegistryHostBackend { ReadOnly = true },
            publicationSnapshot: snapshot);

        using var response = await host.Client.GetAsync("/teams");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.GetValues("Link").Single())
            .IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
    }

    [Test]
    public async Task KestrelRejectsConcreteAvroIncompatibilityWithoutPublishingTheDocument()
    {
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
            resourceValidator: new BuiltInRegistryResourceValidator());
        using var first = new HttpRequestMessage(HttpMethod.Put, "/schemagroups/g/schemas/s")
        {
            Content = Json("\"int\"")
        };
        first.Headers.TryAddWithoutValidation("xRegistry-format", "Avro/1.11.0");
        using var created = await host.Client.SendAsync(first);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.GetValues("xRegistry-formatvalidated").Single()).IsEqualTo("true");
        using var policy = await host.Client.PatchAsync("/schemagroups/g/schemas/s/meta", Json("""{"compatibility":"forward"}"""));
        await Assert.That(policy.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var next = new HttpRequestMessage(HttpMethod.Post, "/schemagroups/g/schemas/s")
        {
            Content = Json("\"long\"")
        };
        next.Headers.TryAddWithoutValidation("xRegistry-format", "Avro/1.11.0");
        using var rejected = await host.Client.SendAsync(next);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("compatibility_violation");
        await RegistryErrorContractHttpTests.AssertProblem(rejected, 400, "compatibility_violation", "spec.md",
            "The request would cause one or more Versions of \"/schemagroups/g/schemas/s\" to violate its compatibility rule (forward).",
            "/schemagroups/g/schemas/s", """{"compat":"forward"}""");
        using var unchanged = await host.Client.GetAsync("/schemagroups/g/schemas/s");
        await Assert.That(await unchanged.Content.ReadAsStringAsync()).IsEqualTo("\"int\"");
        await Assert.That(unchanged.Headers.GetValues("xRegistry-versionid").Single()).IsEqualTo("1");
        await Assert.That(unchanged.Headers.GetValues("xRegistry-compatibilityvalidated").Single()).IsEqualTo("true");
    }

    [Test]
    public async Task KestrelDistinguishesInvalidSchemasFromUncheckedUnsupportedFormats()
    {
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
            resourceValidator: new BuiltInRegistryResourceValidator());
        using var uncheckedFormat = await host.Client.PutAsync("/schemagroups/g/schemas/unchecked$details",
            Json("""{"format":"XSD/1.1","schema":"not fetched or interpreted as XSD 1.0"}"""));
        await Assert.That(uncheckedFormat.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var metadata = RegistryJson.Parse(await uncheckedFormat.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(metadata.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(metadata.GetProperty("formatvalidatedreason").GetString()!).Contains("format.unsupported");
        using var invalid = await host.Client.PutAsync("/schemagroups/g/schemas/invalid$details",
            Json("""{"format":"Avro/1.11.0","schema":{}}"""));
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await invalid.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("format_violation");
        await RegistryErrorContractHttpTests.AssertProblem(invalid, 400, "format_violation", "spec.md",
            "The request would cause Version \"/schemagroups/g/schemas/invalid/versions/1\" to be non-compliant with its \"format\" (Avro/1.11.0).",
            "/schemagroups/g/schemas/invalid/versions/1", """{"format":"Avro/1.11.0"}""");
        await Assert.That(problem.TryGetProperty("formatvalidated", out _)).IsFalse();
        using var absent = await host.Client.GetAsync("/schemagroups/g/schemas/invalid$details");
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments(RegistryModelKind.Endpoint)]
    [Arguments(RegistryModelKind.CloudEvents)]
    public async Task KestrelEnforcesOptedInEndpointUsageWithoutPublishingInvalidPatches(RegistryModelKind kind)
    {
        await using var host = await TestHost.StartAsync(model: BuiltInRegistryModels.Compile(kind));
        using var created = await host.Client.PutAsync("/endpoints/e",
            Json("""{"usage":["subscriber","consumer"],"protocol":"MQTT/5.0"}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var rejected = await host.Client.PatchAsync("/endpoints/e", Json("""{"protocol":"HTTP"}"""));
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("type").GetString()).IsEqualTo("https://github.com/xregistry/spec/blob/main/core/spec.md#invalid_attribute");
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        using var get = await host.Client.GetAsync("/endpoints/e");
        var unchanged = RegistryJson.Parse(await get.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(unchanged.GetProperty("protocol").GetString()).IsEqualTo("MQTT/5.0");
        await Assert.That(unchanged.GetProperty("usage").GetArrayLength()).IsEqualTo(2);
        await Assert.That(unchanged.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task KestrelDoesNotInferEndpointDomainFromACollectionName()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"endpoints":{"singular":"endpoint","attributes":{"kind":{"type":"string"}}}}}
            """));
        await using var host = await TestHost.StartAsync(model: model);
        using var response = await host.Client.PutAsync("/endpoints/e", Json("""{"kind":"ordinary-group"}"""));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var metadata = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("ordinary-group");
        await Assert.That(metadata.TryGetProperty("usage", out _)).IsFalse();
    }

    [Test]
    public async Task KestrelUsesRawStatusHeadersAndConfiguredRootRatherThanHost()
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f")
        {
            Content = new ByteArrayContent([0, 255, 123, 0])
        };
        request.Headers.Host = "attacker.invalid";
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var created = await host.Client.SendAsync(request);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.AbsoluteUri).IsEqualTo("https://public.example/registry/teams/g/files/f");
        await Assert.That(created.Headers.GetValues("Link").Single()).IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
        await Assert.That(created.Headers.GetValues("xRegistry-versionid").Single()).IsEqualTo("1");
        await Assert.That(Convert.ToBase64String(await created.Content.ReadAsByteArrayAsync())).IsEqualTo("AP97AA==");
        using var metadata = await host.Client.GetAsync("/teams/g/files/f$details");
        await Assert.That(metadata.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var root = RegistryJson.Parse(await metadata.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(root.GetProperty("self").GetString()).IsEqualTo("https://public.example/registry/teams/g/files/f$details");
        await Assert.That(root.GetProperty("fileid").GetString()).IsEqualTo("f");
    }

    [Test]
    public async Task KestrelHostsAnApplicationOwnedRegistryBackend()
    {
        var backend = new TestRegistryHostBackend();
        await using var host = await TestHost.StartAsync(hostBackend: backend);
        using var response = await host.Client.GetAsync("/");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement
            .GetProperty("backend").GetBoolean()).IsTrue();
        await Assert.That(backend.AuthorizationCount).IsEqualTo(2);
        await Assert.That(backend.DescriptionCount).IsEqualTo(1);
        await Assert.That(backend.ExecutionCount).IsEqualTo(1);
        await Assert.That(backend.ResponsePreparationCount).IsEqualTo(1);
    }

    [Test]
    public async Task CustomBackendDoesNotPublishWhenResponsePreparationFails()
    {
        var backend = new TestRegistryHostBackend
        {
            AllowedActions = [RegistryAction.Replace],
            Limits = new() { MaxResponseBytes = 1 },
            ResponseMetadata = RegistryJson.Parse("""{"response":"exceeds budget"}""")
        };
        await using var host = await TestHost.StartAsync(hostBackend: backend);
        using var response = await host.Client.PutAsync("/custom", Json("{}"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotAcceptable);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement
            .GetProperty("code").GetString()).IsEqualTo("too_large");
        await Assert.That(backend.ExecutionCount).IsEqualTo(1);
        await Assert.That(backend.ResponsePreparationCount).IsZero();
        await Assert.That(backend.Published).IsFalse();
    }

    [Test]
    public async Task RequestScopeIsSharedAcrossBackendPhasesAndDisposedAfterResponsePreparation()
    {
        var backend = new TestRegistryHostBackend();
        var scope = new TestRequestScope(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "scoped-caller")], "test")));
        var factoryCalls = 0;
        await using var host = await TestHost.StartAsync(hostBackend: backend, httpOptions: new RegistryHttpOptions
        {
            RequestScopeFactory = (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                factoryCalls++;
                return ValueTask.FromResult<IRegistryRequestScope>(scope);
            }
        });

        using var response = await host.Client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(factoryCalls).IsEqualTo(1);
        await Assert.That(scope.DisposeCount).IsEqualTo(1);
        await Assert.That(backend.RequestScopes).IsNotEmpty();
        await Assert.That(backend.RequestScopes.All(observed => ReferenceEquals(observed, scope))).IsTrue();
        await Assert.That(scope.Caller.FindFirst(ClaimTypes.NameIdentifier)!.Value).IsEqualTo("scoped-caller");
    }

    [Test]
    public async Task RequestScopeAuthorizationFailureIsAnUnauthorizedProblem()
    {
        var backend = new TestRegistryHostBackend();
        await using var host = await TestHost.StartAsync(hostBackend: backend, httpOptions: new RegistryHttpOptions
        {
            RequestScopeFactory = (_, _) =>
                ValueTask.FromException<IRegistryRequestScope>(new UnauthorizedAccessException())
        });

        using var response = await host.Client.GetAsync("/");
        var problem = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("unauthorized");
        await Assert.That(backend.DescriptionCount).IsZero();
        await Assert.That(backend.ExecutionCount).IsZero();
    }

    [Test]
    public async Task RequestScopeIsDisposedWhenBackendAuthorizationRejectsTheRequest()
    {
        var backend = new TestRegistryHostBackend
        {
            AuthorizationFailure = new UnauthorizedAccessException()
        };
        var scope = new TestRequestScope(new ClaimsPrincipal(new ClaimsIdentity("test")));
        await using var host = await TestHost.StartAsync(hostBackend: backend, httpOptions: new RegistryHttpOptions
        {
            RequestScopeFactory = (_, _) => ValueTask.FromResult<IRegistryRequestScope>(scope)
        });

        using var response = await host.Client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var problem = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("server_error");
        await Assert.That(problem.GetProperty("detail").GetString())
            .DoesNotContain("acquire this Registry request scope");
        await Assert.That(backend.ExecutionCount).IsZero();
        await Assert.That(scope.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task RequestScopeDisposalFailureDoesNotSuppressPreparedResponseOrDisposeTwice()
    {
        var backend = new TestRegistryHostBackend();
        var scope = new TestRequestScope(new ClaimsPrincipal(new ClaimsIdentity("test")),
            new TimeoutException("Injected cleanup failure."));
        await using var host = await TestHost.StartAsync(hostBackend: backend, httpOptions: new RegistryHttpOptions
        {
            RequestScopeFactory = (_, _) => ValueTask.FromResult<IRegistryRequestScope>(scope)
        });

        using var response = await host.Client.GetAsync("/");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(backend.ResponsePreparationCount).IsEqualTo(1);
        await Assert.That(scope.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("GET", "/", 200)]
    [Arguments("HEAD", "/", 200)]
    [Arguments("GET", "/.xregistry", 200)]
    [Arguments("GET", "/model", 200)]
    [Arguments("GET", "/modelsource", 200)]
    [Arguments("GET", "/capabilities", 200)]
    [Arguments("GET", "/capabilitiesoffered", 200)]
    [Arguments("GET", "/export", 200)]
    [Arguments("GET", "/teams", 200)]
    [Arguments("PUT", "/teams", 405)]
    [Arguments("DELETE", "/", 405)]
    [Arguments("PATCH", "/model", 405)]
    [Arguments("POST", "/modelsource", 405)]
    [Arguments("GET", "/unknown", 400)]
    public async Task RootAndAdministrativeRouteMatrixHasIndependentStatuses(string method, string path, int expectedStatus)
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await host.Client.SendAsync(request);
        await Assert.That((int)response.StatusCode).IsEqualTo(expectedStatus);
        await Assert.That(response.Headers.Contains("Link")).IsTrue();
        if (expectedStatus == 405)
        {
            await Assert.That(response.Content.Headers.Allow.Contains("GET")).IsTrue();
        }

        if (method == "HEAD")
        {
            await Assert.That((await response.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
            await Assert.That(response.Content.Headers.ContentLength.GetValueOrDefault()).IsGreaterThan(0L);
        }
    }

    [Test]
    public async Task EntityRouteMatrixAndDetailsAreModelDriven()
    {
        await using var host = await TestHost.StartAsync();
        using var created = await host.Client.PutAsync("/teams/g/notes/n", Json("""{"name":"metadata-only"}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        foreach (var path in new[] { "/teams/g", "/teams/g/notes", "/teams/g/notes/n", "/teams/g/notes/n/meta",
            "/teams/g/notes/n/versions", "/teams/g/notes/n/versions/1" })
        {
            using var get = await host.Client.GetAsync(path);
            await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var options = await host.Client.SendAsync(new(HttpMethod.Options, path));
            await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(options.Content.Headers.Allow.Contains("OPTIONS")).IsTrue();
        }

        using var equivalent = await host.Client.GetAsync("/teams/g/notes/n$details");
        await Assert.That(RegistryJson.Parse(await equivalent.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("self").GetString())
            .IsEqualTo("https://public.example/registry/teams/g/notes/n");
        using var patch = await host.Client.PatchAsync("/teams/g/files/f", Json("{}"));
        await Assert.That(patch.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(RegistryJson.Parse(await patch.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("details_required");
        using var deleteMeta = await host.Client.DeleteAsync("/teams/g/notes/n/meta");
        await Assert.That(deleteMeta.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task RawEpochZeroGuardFailsWith400AndNestedFailureHasNoEffects()
    {
        await using var host = await TestHost.StartAsync();
        using var first = await host.Client.PatchAsync("/", Json("""{"epoch":0,"name":"updated"}"""));
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var stale = await host.Client.PatchAsync("/", Json("""{"epoch":0,"name":"not written"}"""));
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await stale.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("type").GetString()).IsEqualTo("https://github.com/xregistry/spec/blob/main/core/spec.md#mismatched_epoch");
        await Assert.That(problem.GetProperty("status").GetInt32()).IsEqualTo(400);
        await Assert.That(problem.GetProperty("args").GetProperty("bad_epoch").GetString()).IsEqualTo("0");
        await Assert.That(problem.GetProperty("args").GetProperty("epoch").GetString()).IsEqualTo("1");
        using var nested = await host.Client.PatchAsync("/", Json("""{"teams":{"one":{},"two":{"notallowed":1}}}"""));
        await Assert.That(nested.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var root = await host.Client.GetAsync("/");
        var metadata = RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(metadata.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        await Assert.That(metadata.GetProperty("name").GetString()).IsEqualTo("updated");
        await Assert.That(metadata.GetProperty("epoch").GetInt32()).IsEqualTo(1);
    }

    [Test]
    [Arguments("application/octet-stream", "eyJhIjoxfQ==")]
    [Arguments("application/json", "/wCAew==")]
    [Arguments("application/json", "")]
    public async Task KestrelPreservesBinaryJsonLookingNonUtf8AndPresentEmptyBodies(string contentType, string base64)
    {
        await using var host = await TestHost.StartAsync();
        using var input = new ByteArrayContent(Convert.FromBase64String(base64));
        input.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var created = await host.Client.PutAsync("/teams/g/files/f", input);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(Convert.ToBase64String(await created.Content.ReadAsByteArrayAsync())).IsEqualTo(base64);
        using var get = await host.Client.GetAsync("/teams/g/files/f");
        await Assert.That(Convert.ToBase64String(await get.Content.ReadAsByteArrayAsync())).IsEqualTo(base64);
        using var inline = await host.Client.GetAsync("/teams/g/files/f$details?inline=file&binary");
        await Assert.That(RegistryJson.Parse(await inline.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("filebase64").GetString()).IsEqualTo(base64);
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, "/teams/g/files/f"));
        await Assert.That(head.Content.Headers.ContentLength).IsEqualTo((long)Convert.FromBase64String(base64).Length);
        await Assert.That((await head.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task ScalarAndMapHeadersUseIndependentEscapedWireValues()
    {
        await using var host = await TestHost.StartAsync();
        using var first = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([1]) };
        first.Headers.TryAddWithoutValidation("xRegistry-name", "hello%20%E2%82%AC%0A");
        first.Headers.TryAddWithoutValidation("xRegistry-labels.a.b", "one%25two");
        first.Headers.TryAddWithoutValidation("xRegistry-labels.old", "remove");
        first.Headers.TryAddWithoutValidation("xRegistry-isdefault", "invalid but ignored");
        using var created = await host.Client.SendAsync(first);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.GetValues("xRegistry-name").Single()).IsEqualTo("hello%20%E2%82%AC%0A");
        await Assert.That(created.Headers.GetValues("xRegistry-labels.a.b").Single()).IsEqualTo("one%25two");
        using var next = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([]) };
        next.Headers.TryAddWithoutValidation("xRegistry-name", "null");
        next.Headers.TryAddWithoutValidation("xRegistry-labels.a.b", "\"quoted%20value\"");
        using var replaced = await host.Client.SendAsync(next);
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replaced.Headers.Contains("xRegistry-name")).IsFalse();
        await Assert.That(replaced.Headers.Contains("xRegistry-labels.old")).IsFalse();
        await Assert.That(replaced.Headers.GetValues("xRegistry-labels.a.b").Single()).IsEqualTo("quoted%20value");
        await Assert.That(replaced.Content.Headers.ContentType).IsNull();
    }

    [Test]
    public async Task AnonymousWritesAndUntrustedIdentityHeadersCannotBypassAuthorization()
    {
        await using var host = await TestHost.StartAsync(authenticated: false);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g") { Content = Json("{}") };
        request.Headers.TryAddWithoutValidation("x-user", "admin");
        request.Headers.TryAddWithoutValidation("x-roles", "administrator");
        using var denied = await host.Client.SendAsync(request);
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var modelDenied = await host.Client.PutAsync("/modelsource", Json("{}"));
        await Assert.That(modelDenied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var root = await host.Client.GetAsync("/");
        await Assert.That(root.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidJsonInvalidHeadersAndOversizeBodiesReturnErrorsNotEmptySuccess()
    {
        await using var host = await TestHost.StartAsync(limits: new() { MaxDocumentBytes = 3 });
        using var malformed = await host.Client.PutAsync("/teams/g", Json("{"));
        await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await malformed.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("parsing_data");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([1]) };
        request.Headers.TryAddWithoutValidation("xRegistry-name", "%C0%A0");
        using var header = await host.Client.SendAsync(request);
        await Assert.That(header.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await header.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("header_error");
        using var oversized = await host.Client.PutAsync("/teams/g/files/f", new ByteArrayContent([1, 2, 3, 4]));
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task EveryNestedAndModelMutationRequiresTheHostPolicy()
    {
        await using var host = await TestHost.StartAsync(policy: new DenyPolicy());
        using var nested = await host.Client.PatchAsync("/", Json("""{"teams":{"allowed":{},"blocked":{}}}"""));
        await Assert.That(nested.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var model = await host.Client.PutAsync("/modelsource", Json("{}"));
        await Assert.That(model.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var inlinedModel = await host.Client.PatchAsync("/", Json("""{"modelsource":{}}"""));
        await Assert.That(inlinedModel.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task HeaderPreparationFailureRejectsBinaryMutationBeforePublication()
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Client.PutAsync("/teams/g/files/f?inline=*",
            new StringContent("body", Encoding.UTF8, "text/plain"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var setup = await host.Client.PatchAsync("/teams/g/files/f$details", Json("""{"contenttype":"not a media type"}"""));
        await Assert.That(setup.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([4, 5]) };
        invalid.Content.Headers.TryAddWithoutValidation("Content-Type", "not a media type");
        using var rejected = await host.Client.SendAsync(invalid);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("header_error");
        using var details = await host.Client.GetAsync("/teams/g/files/f$details?inline=file&binary");
        await Assert.That(RegistryJson.Parse(await details.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("filebase64").GetString()).IsEqualTo("Ym9keQ==");
    }

    [Test]
    public async Task UnknownCommitOutcomeIsAnExplicit503WithoutSuccessfulEntityResponse()
    {
        await using var host = await TestHost.StartAsync(persistence: new UnknownCommitPersistence());
        using var response = await host.Client.PutAsync("/teams/g", Json("{}"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.GetValues("xRegistry-commit-outcome").Single()).IsEqualTo("unknown");
        await Assert.That(response.Headers.Location).IsNull();
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString()).IsEqualTo("commit_outcome_unknown");
    }

    [Test]
    public async Task DisconnectedPartialBodyCancelsWithoutCreatingParents()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TestHost.StartAsync(started: context =>
        {
            if (context.Request.Method == "PUT")
            {
                started.TrySetResult();
                context.RequestAborted.Register(() => aborted.TrySetResult());
            }
        }, completed: context =>
        {
            if (context.Request.Method == "PUT")
            {
                completed.TrySetResult();
            }
        });
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, host.Client.BaseAddress!.Port);
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                "PUT /teams/g/files/f HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\nConnection: close\r\n\r\nx"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            socket.Client.Shutdown(SocketShutdown.Send);
            await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task SlowRequestBodyExpiresWithinConfiguredLifetimeWithoutPublishing()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TestHost.StartAsync(httpOptions: new() { RequestTimeout = TimeSpan.FromMilliseconds(100) },
            completed: context =>
            {
                if (context.Request.Method == "PUT")
                {
                    completed.TrySetResult();
                }
            });
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, host.Client.BaseAddress!.Port);
        var stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "PUT /teams/g/files/f HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\nConnection: close\r\n\r\nx"));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = await stream.ReadAsync(buffer, readTimeout.Token);
        await Assert.That(Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HTTP/1.1 408", StringComparison.Ordinal)).IsTrue();
        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    private static async Task<PublicationOpenedSnapshot> CreateOpenedSnapshot()
    {
        var access = new PublicationRegistryAccess("http", "https://publisher.example.test",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var registryBytes = Encoding.UTF8.GetBytes("""{"registryid":"captured"}""");
        var registryDescriptor = CreateDescriptor(access,
            "/publications/team/objects/registry/versions/content", registryBytes);
        var manifest = new JsonObject
        {
            ["format"] = "xregistry-publication/1",
            ["scope"] = "urn:example:http-projection",
            ["capture"] = "assembled",
            ["completeness"] = "offline-complete",
            ["registry"] = DescriptorJson(registryDescriptor),
            ["documents"] = new JsonArray()
        };
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var snapshotId = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var snapshotDescriptor = CreateDescriptor(access,
            $"/publications/team/snapshots/{snapshotId}/versions/manifest", manifestBytes);
        var channelMetadata = new PublicationChannelMetadata("urn:example:http-projection", snapshotDescriptor,
            new BigInteger(3));
        var binding = new ProjectionPublicationBinding(access, channelMetadata, new Dictionary<string, byte[]>
        {
            [snapshotDescriptor.Reference.Version] = manifestBytes,
            [registryDescriptor.Reference.Version] = registryBytes
        });
        var client = new PublicationReadClient((_, _) =>
            ValueTask.FromResult<IPublicationReadBinding>(binding));
        return await client.OpenChannelAsync(access, "/publications/team/channels/stable");
    }

    private static PublicationByteDescriptor CreateDescriptor(PublicationRegistryAccess access, string version, byte[] bytes) =>
        new(new PublicationVersionReference(access, version),
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length);

    private static JsonObject DescriptorJson(PublicationByteDescriptor descriptor) => new()
    {
        ["reference"] = new JsonObject
        {
            ["registry"] = new JsonObject
            {
                ["binding"] = descriptor.Reference.Registry.Binding,
                ["endpoint"] = descriptor.Reference.Registry.Endpoint
            },
            ["version"] = descriptor.Reference.Version
        },
        ["digest"] = descriptor.Digest,
        ["size"] = (long)descriptor.Size
    };

    private sealed class ProjectionPublicationBinding(PublicationRegistryAccess access,
        PublicationChannelMetadata metadata, IReadOnlyDictionary<string, byte[]> objects) : IPublicationReadBinding
    {
        public PublicationRegistryAccess Access { get; } = access;

        public ValueTask<PublicationChannelMetadata> ReadChannelMetadataAsync(string channelResource,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(metadata);
        }

        public ValueTask<Stream> OpenVersionDocumentAsync(string version, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(objects[version], writable: false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    internal sealed class TestHost(WebApplication application, HttpClient client) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;

        internal static async Task<TestHost> StartAsync(bool authenticated = true, IRegistryAuthorizationPolicy? policy = null,
            RegistryLimits? limits = null, IRegistryPersistence? persistence = null, RegistryHttpOptions? httpOptions = null,
            Action<HttpContext>? completed = null, RegistryModel? model = null, IRegistryResourceValidator? resourceValidator = null,
            bool allowCapabilityUpdates = false, IRegistryDocumentReferencePolicy? documentReferencePolicy = null,
            string pathBase = "", IRegistryHostBackend? hostBackend = null, Action<HttpContext>? started = null,
            PublicationOpenedSnapshot? publicationSnapshot = null)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            if (pathBase.Length != 0)
            {
                application.UsePathBase(pathBase);
            }
            if (started is not null)
            {
                application.Use(async (context, next) =>
                {
                    started(context);
                    await next(context);
                });
            }
            if (completed is not null)
            {
                application.Use(async (context, next) =>
                {
                    try
                    {
                        await next(context);
                    }
                    finally
                    {
                        completed(context);
                    }
                });
            }

            if (authenticated)
            {
                application.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "isolated-loopback-test")], "test"));
                    await next(context);
                });
            }

            var engine = new RegistryEngine(new()
            {
                RegistryId = "http-test",
                PublicRoot = new Uri("https://public.example/registry"),
                Model = model ?? RegistryModel.Compile(RegistryJson.Parse("""
                    {"groups":{"teams":{"singular":"team","resources":{
                     "notes":{"singular":"note","hasdocument":false},"files":{"singular":"file","maxversions":2}}}}}
                    """)),
                AllowAnonymousReads = true,
                AllowCapabilityUpdates = allowCapabilityUpdates,
                DocumentReferencePolicy = documentReferencePolicy,
                ResourceValidator = resourceValidator,
                Limits = limits ?? new()
            }, persistence ?? new InMemoryRegistryPersistence(), policy ?? new PermitPolicy());
            if (publicationSnapshot is not null)
            {
                if (hostBackend is not IReadOnlyRegistryHostBackend readOnlyBackend)
                {
                    throw new ArgumentException("The selected projection test backend must expose read-only state.",
                        nameof(hostBackend));
                }
                application.MapPublicationSnapshot(readOnlyBackend, publicationSnapshot, httpOptions);
            }
            else
            {
                application.MapXRegistry(hostBackend ?? engine, httpOptions);
            }
            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            return new(application, new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

    private sealed class PermitPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class DenyPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(access != RegistryAccess.UpdateModel &&
                !(path.Kind == RegistryPathKind.Group && path.GroupId?.Value == "blocked"));
    }

    private sealed class TestRegistryHostBackend : IReadOnlyRegistryHostBackend
    {
        internal int AuthorizationCount { get; private set; }
        internal int DescriptionCount { get; private set; }
        internal int ExecutionCount { get; private set; }
        internal int ResponsePreparationCount { get; private set; }
        internal bool Published { get; private set; }
        internal UnauthorizedAccessException? AuthorizationFailure { get; init; }
        internal bool DenyAuthorization { get; init; }
        internal bool ReadOnly { get; init; }
        public bool IsReadOnly => ReadOnly;
        internal List<IRegistryRequestScope?> RequestScopes { get; } = [];
        public Uri PublicRoot { get; } = new("https://public.example/registry");
        public RegistryLimits Limits { get; init; } = new();
        internal IReadOnlyList<RegistryAction> AllowedActions { get; init; } = [RegistryAction.Read];
        internal RegistryJson ResponseMetadata { get; init; } = RegistryJson.Parse("""{"backend":true}""");

        public ValueTask AuthorizeRequestAsync(RegistryAction action, RegistryPath path, RegistryOperationContext context,
            CancellationToken cancellationToken = default)
        {
            AuthorizationCount++;
            if (AuthorizationFailure is { } failure)
            {
                return ValueTask.FromException(failure);
            }
            if (DenyAuthorization)
            {
                return ValueTask.FromException(new RegistryException(new("forbidden", path.EscapedPath,
                    "The caller is not authorized to read this registry.")));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<RegistryPath> ResolvePathAsync(RegistryAction action, string escapedPath,
            RegistryOperationContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RegistryPath.Parse(escapedPath));

        public async ValueTask<RegistryRouteDescription> DescribeAsync(RegistryAction action, RegistryPath path,
            RegistryOperationContext context, IReadOnlyList<KeyValuePair<string, string?>> parameters,
            CancellationToken cancellationToken = default)
        {
            DescriptionCount++;
            RequestScopes.Add(context.RequestScope);
            await AuthorizeRequestAsync(action, path, context, cancellationToken);
            return new RegistryRouteDescription(path, "custom", null, AllowedActions);
        }

        public async ValueTask<RegistryResult> ExecuteAsync(RegistryOperation operation, RegistryOperationContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            RequestScopes.Add(context.RequestScope);
            await AuthorizeRequestAsync(operation.Action, operation.Path, context, cancellationToken);
            var result = new RegistryResult(RegistryResultKind.Success, operation.Path, ResponseMetadata);
            if (context.PrepareResponseAsync is { } prepare)
            {
                await prepare(result, cancellationToken);
                ResponsePreparationCount++;
            }

            Published = true;
            return result;
        }
    }

    private sealed class TestRequestScope(ClaimsPrincipal caller, Exception? disposalFailure = null) : IRegistryRequestScope
    {
        public ClaimsPrincipal Caller { get; } = caller;
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return disposalFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposalFailure);
        }
    }

    private sealed class UnknownCommitPersistence : IRegistryPersistence
    {
        private readonly InMemoryRegistryPersistence _inner = new();
        public bool IsReadOnly => false;
        public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) => _inner.ReadSnapshotAsync(cancellationToken);
        public async ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) =>
            new UnknownCommit(await _inner.PrepareAsync(expectedGeneration, mutations, cancellationToken));

        private sealed class UnknownCommit(IRegistryCommit inner) : IRegistryCommit
        {
            public ValueTask<long> CommitAsync(CancellationToken cancellationToken = default) =>
                throw new RegistryCommitOutcomeUnknownException("Injected uncertain publication.");
            public void Dispose() => inner.Dispose();
        }
    }
}
