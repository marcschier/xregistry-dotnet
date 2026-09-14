using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class RegistryDiagnosticBoundaryTests
{
    [Test]
    [Arguments("PATCH", false)]
    [Arguments("PUT", false)]
    [Arguments("PATCH", true)]
    [Arguments("PUT", true)]
    public async Task ExplicitRequiredAttributeDeletionUsesInvalidAttributeAndPublishesNothing(string method, bool nested)
    {
        var persistence = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(persistence: persistence, model: RequiredModel(nested),
            httpOptions: new() { MountPath = "/registry" });
        using var created = await host.Client.PutAsync("/registry/teams/g",
            Json(nested ? """{"settings":{"field":"present"}}""" : """{"field":"present"}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var before = await persistence.ReadSnapshotAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/registry/teams/g")
        {
            Content = Json(nested ? """{"settings":{"field":null}}""" : """{"field":null}""")
        };
        using var rejected = await host.Client.SendAsync(request);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(problem.GetProperty("subject").GetString()).IsEqualTo("/teams/g");
        await Assert.That(problem.GetProperty("args").GetProperty("name").GetString()).IsEqualTo(nested ? "settings.field" : "field");
        using var after = await persistence.ReadSnapshotAsync();
        await Assert.That(after.Generation).IsEqualTo(before.Generation);
        await Assert.That(rejected.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
        using var get = await host.Client.GetAsync("/registry/teams/g");
        var unchanged = RegistryJson.Parse(await get.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That((nested ? unchanged.GetProperty("settings") : unchanged).GetProperty("field").GetString()).IsEqualTo("present");
        await Assert.That(unchanged.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingRequiredValuesOnCreationAndReplacementKeepTheMissingAttributeContract(bool nested)
    {
        await using var host = await HttpTests.TestHost.StartAsync(model: RequiredModel(nested));
        var missing = nested ? """{"settings":{}}""" : "{}";
        using var absent = await host.Client.PutAsync("/teams/new", Json(missing));
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var creation = RegistryJson.Parse(await absent.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(creation.GetProperty("code").GetString()).IsEqualTo("required_attribute_missing");
        await Assert.That(creation.GetProperty("args").GetProperty("list").GetString()).IsEqualTo(nested ? "settings.field" : "field");
        using var created = await host.Client.PutAsync("/teams/existing",
            Json(nested ? """{"settings":{"field":"present"}}""" : """{"field":"present"}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var replacement = await host.Client.PutAsync("/teams/existing", Json(missing));
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await replacement.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("required_attribute_missing");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeletingRequiredValuesWithDefaultsStillRestoresTheirDefaults(bool nested)
    {
        await using var host = await HttpTests.TestHost.StartAsync(model: RequiredModel(nested, withDefault: true));
        using var created = await host.Client.PutAsync("/teams/g",
            Json(nested ? """{"settings":{"field":"present"}}""" : """{"field":"present"}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var changed = await host.Client.PatchAsync("/teams/g",
            Json(nested ? """{"settings":{"field":null}}""" : """{"field":null}"""));
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = RegistryJson.Parse(await changed.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That((nested ? metadata.GetProperty("settings") : metadata).GetProperty("field").GetString()).IsEqualTo("fallback");
    }

    [Test]
    [Arguments("array", "0")]
    [Arguments("map", "first")]
    public async Task RequiredDeletionInsideCollectionItemsKeepsTheExactAttributePath(string kind, string key)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","attributes":{"items":{"type":"KIND",
              "item":{"type":"object","attributes":{"field":{"type":"string","required":true}}}}}}}}
            """.Replace("KIND", kind, StringComparison.Ordinal)));
        await using var host = await HttpTests.TestHost.StartAsync(model: model);
        using var created = await host.Client.PutAsync("/teams/g", Json(kind == "array"
            ? """{"items":[{"field":"present"}]}""" : """{"items":{"first":{"field":"present"}}}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var rejected = await host.Client.PatchAsync("/teams/g", Json(kind == "array"
            ? """{"items":[{"field":null}]}""" : """{"items":{"first":{"field":null}}}"""));
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(problem.GetProperty("args").GetProperty("name").GetString()).IsEqualTo("items." + key + ".field");
    }

    [Test]
    [Arguments("/teams/g/notes/n/versions/1", "/teams/g/notes/n/versions/1")]
    [Arguments("/teams/g/notes/n", "/teams/g/notes/n/versions/1")]
    public async Task RequiredDeletionDiagnosticsIdentifyTheUnderlyingVersionEntity(string path, string subject)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,
              "attributes":{"field":{"type":"string","required":true}}}}}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model);
        using var created = await host.Client.PutAsync("/teams/g",
            Json("""{"notes":{"n":{"field":"version"}}}"""));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var rejected = await host.Client.PatchAsync(path, Json("""{"field":null}"""));
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(problem.GetProperty("subject").GetString()).IsEqualTo(subject);
        await Assert.That(problem.GetProperty("args").GetProperty("name").GetString()).IsEqualTo("field");
    }

    [Test]
    public async Task RequiredDeletionThroughDocumentHeadersCannotReplaceTheStoredBytes()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"files":{"singular":"file",
              "attributes":{"field":{"type":"string","required":true}}}}}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model, httpOptions: new() { MountPath = "/registry" });
        using var create = new HttpRequestMessage(HttpMethod.Put, "/registry/teams/g/files/f") { Content = new ByteArrayContent([7]) };
        create.Headers.TryAddWithoutValidation("xRegistry-field", "present");
        using var created = await host.Client.SendAsync(create);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var replace = new HttpRequestMessage(HttpMethod.Put, "/registry/teams/g/files/f") { Content = new ByteArrayContent([9]) };
        replace.Headers.TryAddWithoutValidation("xRegistry-field", "null");
        using var rejected = await host.Client.SendAsync(replace);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(problem.GetProperty("subject").GetString()).IsEqualTo("/teams/g/files/f/versions/1");
        await Assert.That(problem.GetProperty("args").GetProperty("name").GetString()).IsEqualTo("field");
        using var retained = await host.Client.GetAsync("/registry/teams/g/files/f");
        await Assert.That(Convert.ToHexString(await retained.Content.ReadAsByteArrayAsync())).IsEqualTo("07");
        await Assert.That(retained.Headers.GetValues("xRegistry-field").Single()).IsEqualTo("present");
        await Assert.That(retained.Headers.GetValues("xRegistry-epoch").Single()).IsEqualTo("0");
    }

    [Test]
    [Arguments("", "/registry")]
    [Arguments("/outer", "")]
    [Arguments("/outer", "/registry")]
    public async Task RequestPathErrorSubjectsKeepTheIncomingPrefixButExcludeTheQuery(string pathBase, string mount)
    {
        await using var host = await HttpTests.TestHost.StartAsync(pathBase: pathBase, httpOptions: new() { MountPath = mount });
        var prefix = pathBase + mount;
        using var missingApi = await host.Client.GetAsync(prefix + "/model/extra?unused=private-query");
        await RequireProblem(missingApi, "api_not_found", prefix + "/model/extra", HttpStatusCode.NotFound);
        using var missingBody = await host.Client.PutAsync(prefix + "/teams/g?unused=private-query", Json(""));
        await RequireProblem(missingBody, "missing_body", prefix + "/teams/g", HttpStatusCode.BadRequest);
        using var forbiddenHeader = new HttpRequestMessage(HttpMethod.Put, prefix + "/teams/g") { Content = Json("{}") };
        forbiddenHeader.Headers.TryAddWithoutValidation("xRegistry-name", "not allowed");
        using var header = await host.Client.SendAsync(forbiddenHeader);
        await RequireProblem(header, "extra_xregistry_header", prefix + "/teams/g", HttpStatusCode.BadRequest);
        using var invalidDocument = new HttpRequestMessage(HttpMethod.Put, prefix + "/teams/g/files/f")
        {
            Content = new ByteArrayContent([1])
        };
        invalidDocument.Headers.TryAddWithoutValidation("xRegistry-epoch", "not-an-integer");
        using var invalidHeader = await host.Client.SendAsync(invalidDocument);
        await RequireProblem(invalidHeader, "header_error", prefix + "/teams/g/files/f", HttpStatusCode.BadRequest);
        using var missingVersions = await host.Client.PostAsync(prefix + "/teams/g/files/empty/versions?unused=private-query", Json("{}"));
        await RequireProblem(missingVersions, "missing_versions", prefix + "/teams/g/files/empty/versions", HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CoreAndDetailsRequiredErrorSubjectsRemainRegistryRelativeWhenMounted()
    {
        await using var host = await HttpTests.TestHost.StartAsync(pathBase: "/outer", httpOptions: new() { MountPath = "/registry" });
        using var missing = await host.Client.GetAsync("/outer/registry/teams/absent?unused=private-query");
        await RequireProblem(missing, "not_found", "/teams/absent", HttpStatusCode.NotFound);
        using var details = await host.Client.PatchAsync("/outer/registry/teams/g/files/f", Json("{}"));
        await RequireProblem(details, "details_required", "/teams/g/files/f", HttpStatusCode.MethodNotAllowed);
    }

    private static async Task RequireProblem(HttpResponseMessage response, string code, string subject, HttpStatusCode status)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(body.GetProperty("subject").GetString()).IsEqualTo(subject);
        await Assert.That(body.GetProperty("title").GetString()!).Contains(subject);
        await Assert.That(body.GetRawText().Contains("private-query", StringComparison.Ordinal)).IsFalse();
        await Assert.That(response.Headers.GetValues("Link").Single()).IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static RegistryModel RequiredModel(bool nested, bool withDefault = false)
    {
        var field = new JsonObject { ["type"] = "string", ["required"] = true };
        if (withDefault) { field["default"] = "fallback"; }
        var attributes = nested ? new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = true,
                ["attributes"] = new JsonObject { ["field"] = field }
            }
        } : new JsonObject { ["field"] = field };
        return RegistryModel.Compile(RegistryJson.Parse(new JsonObject
        {
            ["groups"] = new JsonObject
            {
                ["teams"] = new JsonObject { ["singular"] = "team", ["attributes"] = attributes }
            }
        }.ToJsonString()));
    }
}
