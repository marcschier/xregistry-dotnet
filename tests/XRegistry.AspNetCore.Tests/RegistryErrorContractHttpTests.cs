using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class RegistryErrorContractHttpTests
{
    [Test]
    [Arguments("DELETE", "/", null, 405, "action_not_supported", "spec.md",
        "The specified action (DELETE) is not supported for: /.", "/", """{"action":"DELETE"}""")]
    [Arguments("TRACE", "/", null, 405, "action_not_supported", "spec.md",
        "The specified action (TRACE) is not supported for: /.", "/", """{"action":"TRACE"}""")]
    [Arguments("GET", "/unknown", null, 400, "unknown_group_type", "spec.md",
        "An unknown Group type (unknown) was specified in \"/unknown\".", "/unknown", """{"name":"unknown"}""")]
    [Arguments("GET", "/teams/g/unknown", null, 400, "unknown_resource_type", "spec.md",
        "An unknown Resource type (unknown) was specified for Group type \"teams\".", "/teams/g/unknown", """{"group":"teams","name":"unknown"}""")]
    [Arguments("GET", "/model/extra", null, 404, "api_not_found", "http.md",
        "The specified API is not supported: /model/extra.", "/model/extra", "{}")]
    [Arguments("PATCH", "/teams/g/files/f", null, 405, "details_required", "http.md",
        "$details suffix is needed when using PATCH for the entity: /teams/g/files/f.", "/teams/g/files/f", "{}")]
    [Arguments("PUT", "/teams/g", null, 400, "missing_body", "http.md",
        "For \"/teams/g\", the request is missing an HTTP body - try '{}'.", "/teams/g", "{}")]
    [Arguments("PUT", "/teams/g", """{"unknown":1}""", 400, "unknown_attribute", "spec.md",
        "An unknown attribute (unknown) was specified for \"/teams/g\".", "/teams/g", """{"name":"unknown"}""")]
    [Arguments("PUT", "/teams/g", """{"name":"one","name":"two"}""", 400, "parsing_data", "spec.md",
        "There was an error parsing \"/teams/g\": Duplicate JSON object members are not allowed.", "/teams/g",
        """{"error_detail":"Duplicate JSON object members are not allowed"}""")]
    [Arguments("GET", "/teams?collections", null, 400, "bad_flag", "spec.md",
        "The specified flag (collections) is not allowed in this context: /teams.", "/teams", """{"flag":"collections"}""")]
    [Arguments("GET", "/teams?filter=", null, 400, "bad_filter", "spec.md",
        "For \"/teams\", an error was found in \"filter\" value (): A filter expression is required.", "/teams",
        """{"value":"","error_detail":"A filter expression is required"}""")]
    [Arguments("GET", "/teams?sort=", null, 400, "bad_sort", "spec.md",
        "For \"/teams\", an error was found in \"sort\" value (): A sort projection is required.", "/teams",
        """{"value":"","error_detail":"A sort projection is required"}""")]
    [Arguments("GET", "/teams?limit=0", null, 400, "bad_flag", "spec.md",
        "The specified flag (limit) is not allowed in this context: /teams.", "/teams", """{"flag":"limit"}""")]
    [Arguments("GET", "/?sort=name", null, 400, "sort_noncollection", "spec.md",
        "Can't sort on a non-collection result set. Query path: /.", "/", "{}")]
    [Arguments("GET", "/?ignore=epoch", null, 400, "bad_ignore", "spec.md",
        "For \"/\", an error was found in \"ignore\" value (epoch): ignore only applies to write operations.", "/",
        """{"value":"epoch","error_detail":"ignore only applies to write operations"}""")]
    [Arguments("GET", "/?inline=name", null, 400, "inline_noninlineable", "spec.md",
        "Attempting to inline a non-inlineable attribute (name) on: /.", "/", """{"name":"name"}""")]
    [Arguments("GET", "/teams$details", null, 400, "bad_details", "spec.md",
        "Use of \"$details\" in this context is not allowed: /teams$details.", "/teams$details", "{}")]
    [Arguments("GET", "/teams/-bad", null, 400, "malformed_id", "spec.md",
        "For \"https://public.example/registry/teams/-bad\", the specified ID value (-bad) is malformed: The identifier does not satisfy the Core ID grammar.",
        "https://public.example/registry/teams/-bad", """{"id":"-bad","error_detail":"The identifier does not satisfy the Core ID grammar"}""")]
    [Arguments("PUT", "/modelsource", """{"attributes":{"field":{"type":"string","default":"x"}}}""", 400, "model_required_true", "spec.md",
        "Model attribute \"field\" needs to have a \"required\" value of \"true\" since a default value is provided.", "/model", """{"name":"field"}""")]
    [Arguments("PUT", "/modelsource", """{"attributes":{"field":{"type":"array","item":{"type":"string"},"default":[]}}}""", 400, "model_scalar_default", "spec.md",
        "Model attribute \"field\" is not allowed to have a default value since it is not a scalar.", "/model", """{"name":"field"}""")]
    public async Task RouteBodyAndFlagErrorsUseIndependentFrozenWireVectors(string method, string path, string? content,
        int status, string code, string document, string title, string subject, string arguments)
    {
        await using var host = await HttpTests.TestHost.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (content is not null)
        {
            request.Content = new StringContent(content, Encoding.UTF8, "application/json");
        }

        using var response = await host.Client.SendAsync(request);
        await AssertProblem(response, status, code, document, title, subject, arguments);
        await Assert.That(response.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
        if (status == 405)
        {
            await Assert.That(string.Join(", ", response.Content.Headers.Allow))
                .IsEqualTo(path == "/" ? "GET, HEAD, OPTIONS, PUT, PATCH, POST" : "GET, HEAD, OPTIONS, PUT, POST, DELETE");
        }
    }

    [Test]
    [Arguments("%C0%A0")]
    [Arguments("%ff")]
    [Arguments("%")]
    [Arguments("\"unterminated")]
    public async Task MalformedPercentEncodedHeaderValuesUseTheHttpCatalogAndHeaderName(string value)
    {
        await using var host = await HttpTests.TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([]) };
        request.Headers.TryAddWithoutValidation("xRegistry-name", value);
        using var response = await host.Client.SendAsync(request);
        await AssertProblem(response, 400, "header_error", "http.md",
            "For \"/teams/g/files/f\", there was an error processing HTTP header \"xRegistry-name\": An attribute header has invalid quoting or percent encoding.",
            "/teams/g/files/f", """{"name":"xRegistry-name","error_detail":"An attribute header has invalid quoting or percent encoding"}""");
        using var missing = await host.Client.GetAsync("/teams/g/files/f$details");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ValidHeaderPercentEscapesDecodeOnceAndWrongHeaderIdsAreNotIgnoredAsReadonly()
    {
        await using var host = await HttpTests.TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([]) };
        request.Headers.TryAddWithoutValidation("xRegistry-name", "space%20Euro%20%e2%82%ac%20%252F");
        using var created = await host.Client.SendAsync(request);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var details = await host.Client.GetAsync("/teams/g/files/f$details");
        await Assert.That(RegistryJson.Parse(await details.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("name").GetString())
            .IsEqualTo("space Euro \u20ac %2F");
        using var wrong = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f") { Content = new ByteArrayContent([1]) };
        wrong.Headers.TryAddWithoutValidation("xRegistry-fileid", "other");
        using var rejected = await host.Client.SendAsync(wrong);
        await AssertProblem(rejected, 400, "mismatched_id", "spec.md",
            "The specified \"fileid\" value (\"other\") for \"/teams/g/files/f\" needs to be \"f\".",
            "/teams/g/files/f", """{"singular":"file","invalid_id":"\"other\"","expected_id":"f"}""");
        using var unchanged = await host.Client.GetAsync("/teams/g/files/f");
        await Assert.That((await unchanged.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
    }

    [Test]
    public async Task MissingEntityUsesTheFrozenCatalogTitleSubjectAndTrustedRoot()
    {
        await using var host = await HttpTests.TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/teams/missing");
        request.Headers.Host = "attacker.invalid";
        using var response = await host.Client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.GetProperty("type").GetString()).IsEqualTo("https://github.com/xregistry/spec/blob/main/core/spec.md#not_found");
        await Assert.That(body.GetProperty("title").GetString()).IsEqualTo("The targeted entity (/teams/missing) cannot be found.");
        await Assert.That(body.GetProperty("subject").GetString()).IsEqualTo("/teams/missing");
        await Assert.That(body.GetProperty("status").GetInt32()).IsEqualTo(404);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("not_found");
        await Assert.That(body.TryGetProperty("args", out _)).IsFalse();
        await Assert.That(response.Headers.GetValues("Link").Single()).IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
        await Assert.That(body.GetRawText().Contains("attacker.invalid", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments("""{"authentication":false}""", "capability_unknown", "Unknown capability specified: authentication.",
        """{"field":"authentication"}""")]
    [Arguments("""{"flags":["filter","*"]}""", "capability_wildcard", "When \"flags\" includes a value of \"*\" then no other values are allowed.",
        """{"field":"flags"}""")]
    [Arguments("""{"specversions":[]}""", "capability_missing_value", "The \"specversions\" capability needs to contain \"1.0-rc4\".",
        """{"name":"specversions","value":"1.0-rc4"}""")]
    [Arguments("""{"pagination":"false"}""", "capability_value", "Invalid value (false) specified for capability \"pagination\". Allowable values include: false, true.",
        """{"field":"pagination","value":"false","list":"false, true"}""")]
    public async Task MutableCapabilityFailuresUseCatalogFieldsAndDoNotPublish(string input, string code, string title, string args)
    {
        await using var host = await HttpTests.TestHost.StartAsync(allowCapabilityUpdates: true);
        using var rejected = await host.Client.PatchAsync("/capabilities", new StringContent(input, Encoding.UTF8, "application/json"));
        await AssertProblem(rejected, 400, code, "spec.md", title, "/capabilities", args);
        using var root = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That(rejected.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
    }

    [Test]
    public async Task DisabledMetadataIsNotAnAbsentEntityAndUnsupportedApiIsDistinct()
    {
        await using var host = await HttpTests.TestHost.StartAsync(allowCapabilityUpdates: true);
        using var changed = await host.Client.PatchAsync("/capabilities", new StringContent("""
            {"available":{"capabilities":{"mutable":true},"entities":{"mutable":true},"model":{"mutable":false}}}
            """, Encoding.UTF8, "application/json"));
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(changed.Headers.Contains("xRegistry-xregcorrelationid")).IsTrue();
        using var disabled = await host.Client.GetAsync("/modelsource");
        await AssertProblem(disabled, 400, "not_available", "spec.md", "The requested data (modelsource) is not available.", "modelsource", "{}");
        using var unsupported = await host.Client.GetAsync("/modelsource/extension");
        await AssertProblem(unsupported, 404, "api_not_found", "http.md", "The specified API is not supported: /modelsource/extension.",
            "/modelsource/extension", "{}");
    }

    [Test]
    public async Task ResponseSizeFailureUses406WhileOversizedRequestsStayQualified413()
    {
        var store = new InMemoryRegistryPersistence();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "sizes",
            PublicRoot = new Uri("https://public.example/registry"),
            Model = RegistryModel.Compile(RegistryJson.Parse("""{"groups":{"teams":{"singular":"team"}}}"""))
        }, store, new Permit());
        var caller = new RegistryOperationContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "fixture")], "test")));
        await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/teams/g"))
        {
            Metadata = RegistryJson.Parse("{\"description\":\"" + new string('a', 512) + "\"}")
        }, caller);
        await using var host = await HttpTests.TestHost.StartAsync(persistence: store, limits: new() { MaxResponseBytes = 128 });
        using var response = await host.Client.GetAsync("/teams/g");
        await AssertProblem(response, 406, "too_large", "spec.md",
            "For \"/teams/g\", the size of the response is too large to return in a single response.", "/teams/g", "{}");

        await using var small = await HttpTests.TestHost.StartAsync(limits: new() { MaxDocumentBytes = 1 });
        using var rejected = await small.Client.PutAsync("/teams/g/files/f", new ByteArrayContent([1, 2]));
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        var problem = RegistryJson.Parse(await rejected.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(problem.GetProperty("code").GetString()).IsEqualTo("request_too_large");
        await Assert.That(problem.GetProperty("type").GetString()).IsEqualTo("urn:xregistry-dotnet:problem:request_too_large");
    }

    [Test]
    public async Task HostPolicyErrorsCannotDiscloseExceptionUrlsOrCredentials()
    {
        await using var host = await HttpTests.TestHost.StartAsync(policy: new SensitivePolicy());
        using var response = await host.Client.GetAsync("/");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.GetProperty("type").GetString()).IsEqualTo("urn:xregistry-dotnet:problem:policy_rejected");
        await Assert.That(body.GetProperty("title").GetString()).IsEqualTo("The host rejected the request.");
        await Assert.That(body.GetProperty("subject").GetString()).IsEqualTo("/");
        await Assert.That(body.GetRawText().Contains("private.invalid", StringComparison.Ordinal)).IsFalse();
        await Assert.That(body.GetRawText().Contains("credential-value", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task MissingRequiredErrorArgumentsProduceAnExplicitInfrastructureProblemNotAMalformedStandardError()
    {
        await using var host = await HttpTests.TestHost.StartAsync(policy: new IncompleteErrorPolicy());
        using var response = await host.Client.GetAsync("/");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.GetProperty("type").GetString()).IsEqualTo("urn:xregistry-dotnet:problem:error_contract");
        await Assert.That(body.GetProperty("title").GetString()).IsEqualTo("The registry could not safely represent the error response.");
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("error_contract");
        await Assert.That(body.TryGetProperty("args", out _)).IsFalse();
        await Assert.That(response.Headers.GetValues("Link").Single()).IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
    }

    [Test]
    public async Task RequiredVersionFieldsAndUnknownDefaultIdsUseVersionAndResourceSubjectsRespectively()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false,
              "attributes":{"field":{"type":"string","required":true}}}}}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model);
        using var required = await host.Client.PutAsync("/teams/g/notes/n", new StringContent("{}", Encoding.UTF8, "application/json"));
        await AssertProblem(required, 400, "required_attribute_missing", "spec.md",
            "One or more mandatory attributes for \"/teams/g/notes/n/versions/1\" are missing: field.",
            "/teams/g/notes/n/versions/1", """{"list":"field"}""");
        using var created = await host.Client.PutAsync("/teams/g/notes/n", new StringContent("""{"field":"ok"}""", Encoding.UTF8, "application/json"));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var unknown = await host.Client.PatchAsync("/teams/g/notes/n/meta",
            new StringContent("""{"defaultversionid":"absent"}""", Encoding.UTF8, "application/json"));
        await AssertProblem(unknown, 400, "unknown_id", "spec.md",
            "While processing \"/teams/g/notes/n\", the \"version\" with a \"versionid\" value of \"absent\" cannot be found.",
            "/teams/g/notes/n", """{"singular":"version","id":"absent"}""");
    }

    [Test]
    public async Task ResponseHeaderPreparationFailureRejectsTheWholeWriteWithoutACorrelationOrPartialState()
    {
        await using var host = await HttpTests.TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g/files/f")
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "not a media type");
        using var response = await host.Client.SendAsync(request);
        await AssertProblem(response, 400, "header_error", "http.md",
            "For \"/teams/g/files/f\", there was an error processing HTTP header \"Content-Type\": The Document Content-Type cannot be represented as an HTTP media type.",
            "/teams/g/files/f", """{"name":"Content-Type","error_detail":"The Document Content-Type cannot be represented as an HTTP media type"}""");
        await Assert.That(response.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
        using var root = await host.Client.GetAsync("/");
        var metadata = RegistryJson.Parse(await root.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(metadata.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        await Assert.That(metadata.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task MetadataBodyRequestsRejectExtraRegistryHeadersWithTheExactHeaderName()
    {
        await using var host = await HttpTests.TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/teams/g")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("xRegistry-name", "extra");
        using var response = await host.Client.SendAsync(request);
        await AssertProblem(response, 400, "extra_xregistry_header", "http.md",
            "For \"/teams/g\", xRegistry HTTP header \"xRegistry-name\" is not allowed on this request: Metadata-body requests must not contain xRegistry headers.",
            "/teams/g", """{"name":"xRegistry-name","error_detail":"Metadata-body requests must not contain xRegistry headers"}""");
    }

    [Test]
    public async Task DocumentUrlUses303AndAnEmptyBodyWithHostPolicyRatherThanProxyFetching()
    {
        var policy = new ReferencePolicy();
        await using var host = await HttpTests.TestHost.StartAsync(documentReferencePolicy: policy);
        using var created = await host.Client.PutAsync("/teams/g/files/f$details",
            new StringContent("""{"contenttype":"text/plain","fileurl":"https://documents.example/item"}""", Encoding.UTF8, "application/json"));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = host.Client.BaseAddress };
        using var response = await client.GetAsync("/teams/g/files/f");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(response.Headers.Location!.AbsoluteUri).IsEqualTo("https://documents.example/item");
        await Assert.That((await response.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        await Assert.That(response.Headers.GetValues("xRegistry-fileid").Single()).IsEqualTo("f");
        await Assert.That(response.Headers.GetValues("xRegistry-versionid").Single()).IsEqualTo("1");
        await Assert.That(policy.Calls).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ReadonlyMetaUses400AndIdentifiesMetaBeforeReadingOrMutatingStorage()
    {
        var persistence = new ReadonlyPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(persistence: persistence);
        using var response = await host.Client.PatchAsync("/teams/g/notes/n/meta",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        await AssertProblem(response, 400, "readonly", "spec.md",
            "Updating a read-only entity (/teams/g/notes/n/meta) is not allowed.", "/teams/g/notes/n/meta", "{}");
        await Assert.That(persistence.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task MalformedTypedXidsUseTheXidCatalogWithoutASecondUriDecode()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"ref":{"type":"xid","target":"/teams"}},"groups":{"teams":{"singular":"team"}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model);
        using var response = await host.Client.PatchAsync("/",
            new StringContent("""{"ref":"/teams/a%252F"}""", Encoding.UTF8, "application/json"));
        await AssertProblem(response, 400, "malformed_xid", "spec.md",
            "For \"https://public.example/registry\", the specified XID value (/teams/a%252F) is malformed: The attribute has the wrong scalar type or is outside its allowed enumeration.",
            "https://public.example/registry",
            """{"xid":"/teams/a%252F","error_detail":"The attribute has the wrong scalar type or is outside its allowed enumeration"}""");
        using var unchanged = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await unchanged.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task ReadInfrastructureFailuresUseDataRetrievalErrorWithoutLeakingStorageDetails()
    {
        await using var host = await HttpTests.TestHost.StartAsync(persistence: new FailingReadPersistence());
        using var response = await host.Client.GetAsync("/");
        await AssertProblem(response, 500, "data_retrieval_error", "spec.md",
            "The server was unable to retrieve all of the requested data.", "/", "{}");
        await Assert.That((await response.Content.ReadAsStringAsync()).Contains("credential-value", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ChunkedDocumentOverflowIsAQualifiedRequest413RatherThanAResponse406()
    {
        await using var host = await HttpTests.TestHost.StartAsync(limits: new() { MaxDocumentBytes = 1 });
        using var content = new StreamContent(new NonSeekableBody([1, 2]));
        using var response = await host.Client.PutAsync("/teams/g/files/f", content);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("request_too_large");
        using var unchanged = await host.Client.GetAsync("/");
        await Assert.That(RegistryJson.Parse(await unchanged.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task ProcessingQuotaFailureIsQualifiedRatherThanMisusingTheResponseSizeCatalog()
    {
        await using var host = await HttpTests.TestHost.StartAsync(limits: new() { MaxEntityOperations = 3 });
        using var response = await host.Client.PutAsync("/teams/g", new StringContent("{}", Encoding.UTF8, "application/json"));
        await Assert.That((int)response.StatusCode).IsEqualTo(422);
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.GetProperty("type").GetString()).IsEqualTo("urn:xregistry-dotnet:problem:operation_limit");
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo("operation_limit");
        await Assert.That(body.GetProperty("title").GetString()).IsEqualTo("The operation exceeds the host's finite processing or persistence budget.");
        await Assert.That(response.Headers.Contains("xRegistry-xregcorrelationid")).IsFalse();
    }

    internal static async Task AssertProblem(HttpResponseMessage response, int status, string code, string document,
        string title, string subject, string arguments)
    {
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        var body = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.GetProperty("type").GetString()).IsEqualTo("https://github.com/xregistry/spec/blob/main/core/" + document + "#" + code);
        await Assert.That(body.GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(body.GetProperty("title").GetString()).IsEqualTo(title);
        await Assert.That(body.GetProperty("subject").GetString()).IsEqualTo(subject);
        await Assert.That(body.GetProperty("status").GetInt32()).IsEqualTo(status);
        var expected = RegistryJson.Parse(arguments).RootElement;
        var actual = body.TryGetProperty("args", out var values) ? values.EnumerateObject().Select(Pair).ToArray() : [];
        await Assert.That(actual).IsEquivalentTo(expected.EnumerateObject().Select(Pair).ToArray(), StringComparer.Ordinal);
        await Assert.That(response.Headers.GetValues("Link").Single()).IsEqualTo("<https://public.example/registry>;rel=xregistry-root");
        static string Pair(JsonProperty property) => property.Name + "=" + property.Value.GetString();
    }

    private sealed class Permit : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class SensitivePolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) =>
            throw new RegistryException(new("policy_rejected", "https://private.invalid/internal",
                "Authorization failed for https://private.invalid/?token=credential-value"));
    }

    private sealed class IncompleteErrorPolicy : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default) =>
            throw new RegistryException(new("invalid_attribute", "/", "A policy omitted the mandatory name argument."));
    }

    private sealed class ReferencePolicy : IRegistryDocumentReferencePolicy
    {
        internal int Calls { get; private set; }
        public ValueTask AuthorizeAsync(ClaimsPrincipal caller, Uri reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (reference.AbsoluteUri != "https://documents.example/item")
            {
                throw new RegistryException(new("forbidden", "/", "The Document location is not authorized."));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReadonlyPersistence : IRegistryPersistence
    {
        internal int Calls { get; private set; }
        public bool IsReadOnly => true;
        public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Readonly write rejection must precede storage access.");
        }

        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Readonly writes must never prepare storage.");
        }
    }

    private sealed class FailingReadPersistence : IRegistryPersistence
    {
        public bool IsReadOnly => false;
        public ValueTask<IRegistrySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("Storage failed at https://private.invalid/?token=credential-value");
        public ValueTask<IRegistryCommit> PrepareAsync(long expectedGeneration, IReadOnlyList<RegistryMutation> mutations,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No write is expected.");
    }

    private sealed class NonSeekableBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
