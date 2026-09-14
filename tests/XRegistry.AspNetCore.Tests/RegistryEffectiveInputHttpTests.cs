using System.Net;
using System.Security.Claims;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class RegistryEffectiveInputHttpTests
{
    private static readonly RegistryModel s_multiGroupModel = RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"teams":{"singular":"team"},"projects":{"singular":"project"}}}
        """));

    [Test]
    public async Task DisabledInlineDoesNotSuppressExportDefaults()
    {
        await using var host = await HttpTests.TestHost.StartAsync(
            allowCapabilityUpdates: true, httpOptions: new() { MountPath = "/registry" });
        await SeedDocumentAsync(host.Client);
        using var configured = await host.Client.PatchAsync("/registry/capabilities", Json("""{"flags":[]}"""));
        await Assert.That(configured.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var ordinary = await GetMetadataAsync(host.Client, "/registry?inline=*");
        await Assert.That(ordinary.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(1);
        await Assert.That(ordinary.RootElement.TryGetProperty("teams", out _)).IsFalse();
        await Assert.That(ordinary.RootElement.TryGetProperty("capabilities", out _)).IsFalse();
        await Assert.That(ordinary.RootElement.TryGetProperty("modelsource", out _)).IsFalse();

        var baseline = await GetMetadataAsync(host.Client, "/registry/export");
        await AssertFullExportAsync(baseline, includeConfiguration: true);

        var requested = await GetMetadataAsync(host.Client, "/registry/export?inline=*");
        await Assert.That(requested.RootElement.TryGetProperty("teams", out _)).IsTrue();
        await Assert.That(requested.RootElement.GetRawText()).IsEqualTo(baseline.RootElement.GetRawText());
    }

    [Test]
    [Arguments("inline")]
    [Arguments("inline=")]
    [Arguments("inline=unknown")]
    [Arguments("inline=model&inline=teams")]
    public async Task DisabledInlineVariantsAreIgnoredByOrdinaryReadsAndExport(string query)
    {
        await using var host = await HttpTests.TestHost.StartAsync(
            allowCapabilityUpdates: true, httpOptions: new() { MountPath = "/registry" });
        await SeedDocumentAsync(host.Client);
        using var configured = await host.Client.PatchAsync("/registry/capabilities", Json("""{"flags":[]}"""));
        await Assert.That(configured.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var ordinary = await GetMetadataAsync(host.Client, "/registry?" + query);
        await Assert.That(ordinary.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(1);
        await Assert.That(ordinary.RootElement.TryGetProperty("teams", out _)).IsFalse();
        await Assert.That(ordinary.RootElement.TryGetProperty("model", out _)).IsFalse();
        await Assert.That(ordinary.RootElement.TryGetProperty("capabilities", out _)).IsFalse();
        await Assert.That(ordinary.RootElement.TryGetProperty("modelsource", out _)).IsFalse();
        await AssertFullExportAsync(await GetMetadataAsync(host.Client, "/registry/export?" + query),
            includeConfiguration: true);
    }

    [Test]
    [Arguments("inline=teams", false)]
    [Arguments("inline=teams,model", true)]
    [Arguments("inline=teams&inline=model", true)]
    public async Task EnabledExplicitInlineOverridesExportDefaults(string query, bool includeModel)
    {
        await using var host = await HttpTests.TestHost.StartAsync(httpOptions: new() { MountPath = "/registry" });
        await SeedDocumentAsync(host.Client);

        var body = (await GetMetadataAsync(host.Client, "/registry/export?" + query)).RootElement;
        await Assert.That(body.GetProperty("self").GetString()).IsEqualTo("#/");
        await Assert.That(body.GetProperty("teamsurl").GetString()).IsEqualTo("#/teams");
        await Assert.That(body.GetProperty("teams").EnumerateObject().Single().Name).IsEqualTo("g");
        var group = body.GetProperty("teams").GetProperty("g");
        await Assert.That(group.GetProperty("filescount").GetInt32()).IsEqualTo(1);
        await Assert.That(group.GetProperty("filesurl").GetString()).IsEqualTo("https://public.example/registry/teams/g/files");
        await Assert.That(group.TryGetProperty("files", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("capabilities", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("modelsource", out _)).IsFalse();
        await Assert.That(body.TryGetProperty("model", out _)).IsEqualTo(includeModel);
        if (includeModel)
        {
            await Assert.That(body.GetProperty("model").GetProperty("groups").GetProperty("teams")
                .GetProperty("singular").GetString()).IsEqualTo("team");
        }
    }

    [Test]
    [Arguments("inline")]
    [Arguments("inline=")]
    [Arguments("inline=*")]
    public async Task EnabledBareInlineOverridesExportConfigurationDefaults(string query)
    {
        await using var host = await HttpTests.TestHost.StartAsync(httpOptions: new() { MountPath = "/registry" });
        await SeedDocumentAsync(host.Client);

        await AssertFullExportAsync(await GetMetadataAsync(host.Client, "/registry/export?" + query),
            includeConfiguration: false);
    }

    [Test]
    public async Task RootPostDoesNotReinterpretIgnoredConfiguration()
    {
        await using var host = await HttpTests.TestHost.StartAsync(
            allowCapabilityUpdates: true, httpOptions: new() { MountPath = "/registry" });
        using var configured = await host.Client.PatchAsync("/registry/capabilities", Json("""{"pagination":false}"""));
        await Assert.That(configured.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var capabilities = await GetMetadataAsync(host.Client, "/registry/capabilities");
        var model = await GetMetadataAsync(host.Client, "/registry/modelsource");

        using var response = await host.Client.PostAsync("/registry?ignore=capabilities,modelsource", Json("""
            {"capabilities":false,"modelsource":42,"teams":{"g":{"name":"created"}}}
            """));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["teams"], StringComparer.Ordinal);
        await Assert.That(body.GetProperty("teams").GetProperty("g").GetProperty("name").GetString()).IsEqualTo("created");
        await Assert.That((await GetMetadataAsync(host.Client, "/registry/capabilities")).RootElement.GetRawText())
            .IsEqualTo(capabilities.RootElement.GetRawText());
        await Assert.That((await GetMetadataAsync(host.Client, "/registry/modelsource")).RootElement.GetRawText())
            .IsEqualTo(model.RootElement.GetRawText());
    }

    [Test]
    [Arguments("{}", "{}")]
    [Arguments("false", "[1]")]
    [Arguments("""{"pagination":true}""", """{"groups":{}}""")]
    public async Task RootPostReturnsOnlyProcessedGroupsAndPreservesIgnoredConfiguration(
        string capabilitiesInput, string modelInput)
    {
        await using var host = await StartMultiGroupHostAsync();
        using var seeded = await host.Client.PutAsync("/registry", Json("""
            {"name":"registry metadata","teams":{
              "old":{"name":"untouched team"},"edit":{"name":"before","description":"remove"}},
             "projects":{"old":{"name":"untouched project"}}}
            """));
        await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await DisablePaginationAsync(host.Client);
        var capabilities = await GetMetadataAsync(host.Client, "/registry/capabilities");
        var model = await GetMetadataAsync(host.Client, "/registry/modelsource");
        await Assert.That(capabilities.RootElement.GetProperty("pagination").GetBoolean()).IsFalse();
        await Assert.That(model.RootElement.GetProperty("groups").EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["teams", "projects"], StringComparer.Ordinal);

        using var response = await host.Client.PostAsync("/registry?ignore=capabilities&ignore=modelsource", Json($$$$"""
            {"capabilities":{{{{capabilitiesInput}}}},"modelsource":{{{{modelInput}}}},
             "teams":{"edit":{"name":"team changed"},"new":{"name":"team new"}},
             "projects":{"new":{"name":"project new"}}}
            """));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["teams", "projects"], StringComparer.Ordinal);
        await Assert.That(body.GetProperty("teams").EnumerateObject().Select(static property => property.Name).ToArray())
            .IsEquivalentTo(["edit", "new"], StringComparer.Ordinal);
        await Assert.That(body.GetProperty("projects").EnumerateObject().Single().Name).IsEqualTo("new");
        await Assert.That(body.GetProperty("teams").GetProperty("edit").GetProperty("name").GetString()).IsEqualTo("team changed");
        await Assert.That(body.GetProperty("teams").GetProperty("edit").TryGetProperty("description", out _)).IsFalse();
        await Assert.That(body.GetProperty("teams").GetProperty("new").GetProperty("name").GetString()).IsEqualTo("team new");
        await Assert.That(body.GetProperty("projects").GetProperty("new").GetProperty("name").GetString()).IsEqualTo("project new");
        await Assert.That((await GetMetadataAsync(host.Client, "/registry/teams/old")).RootElement.GetProperty("name").GetString())
            .IsEqualTo("untouched team");
        await Assert.That((await GetMetadataAsync(host.Client, "/registry/projects/old")).RootElement.GetProperty("name").GetString())
            .IsEqualTo("untouched project");
        var root = (await GetMetadataAsync(host.Client, "/registry")).RootElement;
        await Assert.That(root.GetProperty("name").GetString()).IsEqualTo("registry metadata");
        await Assert.That(root.GetProperty("teamscount").GetInt32()).IsEqualTo(3);
        await Assert.That(root.GetProperty("projectscount").GetInt32()).IsEqualTo(2);
        await AssertConfigurationUnchangedAsync(host.Client, capabilities, model);
    }

    [Test]
    [Arguments("ignore=capabilities,modelsource")]
    [Arguments("ignore")]
    [Arguments("ignore=")]
    [Arguments("ignore=*")]
    public async Task RootPostWithOnlyIgnoredConfigurationReturnsEmptyMap(string query)
    {
        await using var host = await StartMultiGroupHostAsync();
        using var seeded = await host.Client.PutAsync("/registry", Json("""
            {"name":"keep root","teams":{"old":{"name":"keep group"}}}
            """));
        await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await DisablePaginationAsync(host.Client);
        var root = await GetMetadataAsync(host.Client, "/registry");
        var capabilities = await GetMetadataAsync(host.Client, "/registry/capabilities");
        var model = await GetMetadataAsync(host.Client, "/registry/modelsource");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await host.Client.PostAsync("/registry?" + query,
                Json("""{"capabilities":null,"modelsource":false}"""));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("{}");
            await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetRawText())
                .IsEqualTo(root.RootElement.GetRawText());
            await AssertConfigurationUnchangedAsync(host.Client, capabilities, model);
        }
    }

    [Test]
    [Arguments("", "capabilities")]
    [Arguments("?ignore=capabilities", "modelsource")]
    [Arguments("?ignore=modelsource", "capabilities")]
    public async Task RootPostRejectsUnignoredConfigurationBeforePublication(string query, string invalidAttribute)
    {
        await using var host = await StartMultiGroupHostAsync();
        await DisablePaginationAsync(host.Client);
        var root = await GetMetadataAsync(host.Client, "/registry");
        var capabilities = await GetMetadataAsync(host.Client, "/registry/capabilities");
        var model = await GetMetadataAsync(host.Client, "/registry/modelsource");

        using var response = await host.Client.PostAsync("/registry" + query, Json("""
            {"capabilities":false,"modelsource":42,"teams":{"a":{}},"projects":{"b":{}}}
            """));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("groups_only");
        await Assert.That(error.GetProperty("args").GetProperty("name").GetString()).IsEqualTo(invalidAttribute);
        await Assert.That(response.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
        await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetRawText())
            .IsEqualTo(root.RootElement.GetRawText());
        await AssertConfigurationUnchangedAsync(host.Client, capabilities, model);
    }

    [Test]
    public async Task RootPostDoesNotApplyDisabledIgnore()
    {
        await using var host = await StartMultiGroupHostAsync();
        using var configured = await host.Client.PatchAsync("/registry/capabilities", Json("""{"flags":["inline"]}"""));
        await Assert.That(configured.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var root = await GetMetadataAsync(host.Client, "/registry");
        var capabilities = await GetMetadataAsync(host.Client, "/registry/capabilities");
        var model = await GetMetadataAsync(host.Client, "/registry/modelsource");

        using var response = await host.Client.PostAsync("/registry?ignore=capabilities,modelsource", Json("""
            {"capabilities":false,"modelsource":42,"teams":{"a":{}}}
            """));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("groups_only");
        await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetRawText())
            .IsEqualTo(root.RootElement.GetRawText());
        await AssertConfigurationUnchangedAsync(host.Client, capabilities, model);
    }

    [Test]
    public async Task NormalizedRootPostStillAuthorizesEveryGroupBeforePublication()
    {
        await using var host = await StartMultiGroupHostAsync(policy: new DenyProjectWrites());
        await DisablePaginationAsync(host.Client);
        var root = await GetMetadataAsync(host.Client, "/registry");
        var capabilities = await GetMetadataAsync(host.Client, "/registry/capabilities");
        var model = await GetMetadataAsync(host.Client, "/registry/modelsource");

        using var response = await host.Client.PostAsync("/registry?ignore=capabilities,modelsource", Json("""
            {"capabilities":false,"modelsource":42,"teams":{"allowed":{}},"projects":{"blocked":{}}}
            """));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("forbidden");
        await Assert.That(response.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
        await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetRawText())
            .IsEqualTo(root.RootElement.GetRawText());
        using var absent = await host.Client.GetAsync("/registry/teams/allowed");
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await AssertConfigurationUnchangedAsync(host.Client, capabilities, model);
    }

    [Test]
    public async Task OnlyIgnoredRootPostStillRequiresAuthentication()
    {
        await using var host = await StartMultiGroupHostAsync(authenticated: false);
        var root = await GetMetadataAsync(host.Client, "/registry");
        var capabilities = await GetMetadataAsync(host.Client, "/registry/capabilities");
        var model = await GetMetadataAsync(host.Client, "/registry/modelsource");

        using var response = await host.Client.PostAsync("/registry?ignore=capabilities,modelsource",
            Json("""{"capabilities":false,"modelsource":42}"""));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetRawText())
            .IsEqualTo(root.RootElement.GetRawText());
        await AssertConfigurationUnchangedAsync(host.Client, capabilities, model);
    }

    [Test]
    public async Task InvalidGroupInNormalizedRootPostStillRejectsAtomically()
    {
        await using var host = await StartMultiGroupHostAsync();
        await DisablePaginationAsync(host.Client);
        var root = await GetMetadataAsync(host.Client, "/registry");
        var capabilities = await GetMetadataAsync(host.Client, "/registry/capabilities");
        var model = await GetMetadataAsync(host.Client, "/registry/modelsource");

        using var response = await host.Client.PostAsync("/registry?ignore=capabilities,modelsource", Json("""
            {"capabilities":false,"modelsource":42,"teams":{"valid":{}},"projects":{"invalid":{"undeclared":1}}}
            """));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("unknown_attribute");
        await Assert.That(response.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
        await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetRawText())
            .IsEqualTo(root.RootElement.GetRawText());
        await AssertConfigurationUnchangedAsync(host.Client, capabilities, model);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static Task<HttpTests.TestHost> StartMultiGroupHostAsync(
        IRegistryAuthorizationPolicy? policy = null, bool authenticated = true) =>
        HttpTests.TestHost.StartAsync(authenticated: authenticated, policy: policy, model: s_multiGroupModel,
            allowCapabilityUpdates: true, httpOptions: new() { MountPath = "/registry" });

    private static async Task SeedDocumentAsync(HttpClient client)
    {
        using var response = await client.PutAsync("/registry/teams/g/files/f$details", Json("""
            {"name":"stored document","contenttype":"application/octet-stream","filebase64":"AP8="}
            """));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    private static async Task DisablePaginationAsync(HttpClient client)
    {
        using var response = await client.PatchAsync("/registry/capabilities", Json("""{"pagination":false}"""));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task AssertFullExportAsync(RegistryJson metadata, bool includeConfiguration)
    {
        var root = metadata.RootElement;
        await Assert.That(root.GetProperty("self").GetString()).IsEqualTo("#/");
        await Assert.That(root.GetProperty("teamsurl").GetString()).IsEqualTo("#/teams");
        var resource = root.GetProperty("teams").GetProperty("g").GetProperty("files").GetProperty("f");
        await Assert.That(resource.GetProperty("self").GetString()).IsEqualTo("#/teams/g/files/f");
        await Assert.That(resource.TryGetProperty("name", out _)).IsFalse();
        await Assert.That(resource.GetProperty("meta").GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/teams/g/files/f/versions/1");
        var version = resource.GetProperty("versions").GetProperty("1");
        await Assert.That(version.GetProperty("name").GetString()).IsEqualTo("stored document");
        await Assert.That(version.GetProperty("filebase64").GetString()).IsEqualTo("AP8=");
        await Assert.That(root.TryGetProperty("capabilities", out _)).IsEqualTo(includeConfiguration);
        await Assert.That(root.TryGetProperty("modelsource", out _)).IsEqualTo(includeConfiguration);
        await Assert.That(root.TryGetProperty("model", out _)).IsFalse();
    }

    private static async Task AssertConfigurationUnchangedAsync(HttpClient client, RegistryJson capabilities, RegistryJson model)
    {
        await Assert.That((await GetMetadataAsync(client, "/registry/capabilities")).RootElement.GetRawText())
            .IsEqualTo(capabilities.RootElement.GetRawText());
        await Assert.That((await GetMetadataAsync(client, "/registry/modelsource")).RootElement.GetRawText())
            .IsEqualTo(model.RootElement.GetRawText());
    }

    private static async Task<RegistryJson> GetMetadataAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        await Assert.That(response.Content.Headers.ContentType?.CharSet).IsEqualTo("utf-8");
        return RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    private sealed class DenyProjectWrites : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(access == RegistryAccess.Read || path.GroupType != "projects");
    }
}
