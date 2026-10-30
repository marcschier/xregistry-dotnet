// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class ApiOperationReferenceTests
{
    private const string OpenApiDocument = """
        {
          "openapi": "3.1.1",
          "paths": {
            "/users/{id}": {
              "get": { "operationId": "GetUser" },
              "post": { "operationId": "CreateUser" }
            }
          }
        }
        """;

    private static readonly string[] s_operationTokens = ["operations", "GetUserProfile"];
    private static readonly string[] s_serviceTokens = ["services", "com.example.Profile", "operations", "GetProfile"];
    private static readonly string[] s_graphQlTokens = ["operations", "subscription", "profileChanged"];
    private static readonly string[] s_interfaceTokens =
        ["interfaces", "{urn:example:users}Profile", "operations", "Get", "input", "Request", "output", "Response"];
    private static readonly string[] s_pointerTokens = ["paths", "/users/{userId}", "get"];
    private static readonly string[] s_registryPointerTokens =
        ["apigroups", "users", "apis", "Profile", "versions", "2"];
    private static readonly string[] s_singleDecodeTokens = ["operations", "a~1b%2F"];
    private static readonly string[] s_supportedFormats =
        ["OpenAPI/3.0", "OpenAPI/3.1", "Swagger/2.0", "OpenRPC/1.4", "AvroProtocol/1.12.0", "Smithy/2.0"];
    private static readonly ApiOperationSelectorKind[] s_supportedSelectors =
        [ApiOperationSelectorKind.Operation, ApiOperationSelectorKind.JsonPointer];

    [Test]
    public async Task ParsesAnOperationIdReference()
    {
        var parsed = ApiOperationReference.Parse(
            "https://registry.example/apigroups/users/apis/Profile#operations/GetUserProfile");

        await Assert.That(parsed.EntityReference)
            .IsEqualTo("https://registry.example/apigroups/users/apis/Profile");
        await Assert.That(parsed.RegistryEntityPointer).IsNull();
        await Assert.That(parsed.Selector.Kind).IsEqualTo(ApiOperationSelectorKind.Operation);
        await Assert.That(parsed.Selector.Tokens.SequenceEqual(s_operationTokens)).IsTrue();
    }

    [Test]
    public async Task ParsesServiceAndGraphQlSelectors()
    {
        var service = ApiOperationReference.Parse(
            "/apigroups/users/apis/Profile#services/com.example.Profile/operations/GetProfile");
        var graphQl = ApiOperationReference.Parse(
            "/apigroups/users/apis/Profile#operations/subscription/profileChanged");

        await Assert.That(service.Selector.Kind).IsEqualTo(ApiOperationSelectorKind.ServiceOperation);
        await Assert.That(service.Selector.Tokens.SequenceEqual(s_serviceTokens)).IsTrue();
        await Assert.That(graphQl.Selector.Kind).IsEqualTo(ApiOperationSelectorKind.GraphQlField);
        await Assert.That(graphQl.Selector.Tokens.SequenceEqual(s_graphQlTokens)).IsTrue();
    }

    [Test]
    public async Task ParsesInterfaceOverloadAndJsonPointerSelectors()
    {
        var overload = ApiOperationReference.Parse(
            "/apigroups/users/apis/Profile#interfaces/%7Burn:example:users%7DProfile/operations/Get/input/Request/output/Response");
        var pointer = ApiOperationReference.Parse(
            "/apigroups/users/apis/Profile#/paths/~1users~1%7BuserId%7D/get");

        await Assert.That(overload.Selector.Kind)
            .IsEqualTo(ApiOperationSelectorKind.InterfaceOperationInputOutput);
        await Assert.That(overload.Selector.Tokens.SequenceEqual(s_interfaceTokens)).IsTrue();
        await Assert.That(pointer.Selector.Kind).IsEqualTo(ApiOperationSelectorKind.JsonPointer);
        await Assert.That(pointer.Selector.Tokens.SequenceEqual(s_pointerTokens)).IsTrue();
    }

    [Test]
    public async Task ParsesDocumentViewReferencesAndDecodesOnce()
    {
        var parsed = ApiOperationReference.Parse(
            "#/apigroups/users/apis/Profile/versions/2:operations/a~01b%252F");
        var selector = parsed.Selector.Tokens;

        await Assert.That(parsed.EntityReference).IsNull();
        var registryPointer = parsed.RegistryEntityPointer ?? throw new InvalidOperationException("Expected registry entity pointer.");
        await Assert.That(registryPointer.SequenceEqual(s_registryPointerTokens)).IsTrue();
        await Assert.That(selector.SequenceEqual(s_singleDecodeTokens)).IsTrue();
    }

    [Test]
    [Arguments("/apigroups/x/apis/y")]
    [Arguments("/apigroups/x/apis/y#")]
    [Arguments("/apigroups/x/apis/y#operations/a%")]
    [Arguments("/apigroups/x/apis/y#operations/a%GG")]
    [Arguments("/apigroups/x/apis/y#operations/%C3")]
    [Arguments("/apigroups/x/apis/y#operations/has space")]
    [Arguments("/apigroups/x/apis/y#operations/{userId}")]
    [Arguments("/apigroups/x/apis/y#operations/a~2b")]
    [Arguments("/apigroups/x/apis/y#operations//get")]
    [Arguments("/apigroups/x/apis/y#Operations/get")]
    [Arguments("/apigroups/x/apis/y#operations/get#extra")]
    [Arguments("#/apigroups/x/apis/y")]
    [Arguments("#/apigroups/x/apis/y:")]
    public async Task MalformedReferencesAreRejected(string reference)
    {
        await Assert.That(() => ApiOperationReference.Parse(reference)).Throws<FormatException>();
    }

    [Test]
    public async Task ResolvesExactOpenApiOperationIdAndAdvertisesTheFeature()
    {
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json; charset=utf-8",
            System.Text.Encoding.UTF8.GetBytes(OpenApiDocument), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("GetUser");
        await Assert.That(result.DocumentPath).IsEqualTo("$/paths/~1users~1{id}/get");
        await Assert.That(ApiOperationDocumentResolver.Capabilities.Formats
            .Order(StringComparer.Ordinal)
            .SequenceEqual(s_supportedFormats.Order(StringComparer.Ordinal))).IsTrue();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.ContentTypes
            .SequenceEqual(["application/json"])).IsTrue();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.Selectors.Order()
            .SequenceEqual(s_supportedSelectors.Order())).IsTrue();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsCallerProvidedPathItemReferences)
            .IsTrue();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsCallerProvidedOperationReferences)
            .IsTrue();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.MaxReferencedDocuments).IsEqualTo(32);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes).IsEqualTo(1_048_576);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.MaxTotalDocumentBytes).IsEqualTo(4_194_304L);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.MaxReferenceDepth).IsEqualTo(64);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.MaxDocumentDepth).IsEqualTo(64);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.MaxDocumentNodes).IsEqualTo(100_000);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.MaxDuration).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsOperationResolution).IsTrue();
    }

    [Test]
    public async Task ResolvesOnlyTheOperationSelectedByJsonPointer()
    {
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#/paths/~1users~1%7Bid%7D/post");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(OpenApiDocument), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("CreateUser");
        await Assert.That(result.DocumentPath).IsEqualTo("$/paths/~1users~1{id}/post");
    }

    [Test]
    [Arguments("OpenAPI/3.0", "openapi", "3.0.4")]
    [Arguments("OpenAPI/3.1", "openapi", "3.1.1")]
    [Arguments("Swagger/2.0", "swagger", "2.0")]
    public async Task OperationIdSelectionIgnoresPathsExtensionsWithoutAcquiringTheirReferences(
        string format, string versionField, string version)
    {
        var calls = 0;
        var document = new System.Text.Json.Nodes.JsonObject
        {
            [versionField] = version,
            ["paths"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {"x-null":null,"x-array":[],"x-number":7,
                 "x-metadata":{"get":{"operationId":"GetUser"},"$ref":"https://untrusted.example"},
                 "/users":{"get":{"operationId":"GetUser"}}}
                """)
        };
        var result = await ApiOperationDocumentResolver.ResolveAsync(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/GetUser"),
            new Uri("https://registry.example/apis/a"),
            (_, _) => { calls++; throw new InvalidOperationException("Extensions are not dependencies."); });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/paths/~1users/get");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("")]
    [Arguments("users")]
    [Arguments("X-metadata")]
    [Arguments("https://example.test/users")]
    public async Task OperationIdSelectionRejectsNonPathKeysOutsideTheExtensionNamespace(string key)
    {
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["openapi"] = "3.1.1",
            ["paths"] = new System.Text.Json.Nodes.JsonObject
            {
                [key] = System.Text.Json.Nodes.JsonNode.Parse("""{"get":{"operationId":"GetUser"}}""")
            }
        };
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/GetUser"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.path");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("x-metadata")]
    [Arguments("users")]
    public async Task JsonPointerSelectionCannotTreatPathsExtensionDataAsAnOperation(string key)
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes("""
                {"openapi":"3.1.1","paths":{"KEY":{"get":{"operationId":"GetUser"}}}}
                """.Replace("KEY", key, StringComparison.Ordinal)),
            ApiOperationReference.Parse("/apigroups/g/apis/a#/paths/" + key + "/get"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.operation_pointer");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"components":{}}""", "operations/GetUser")]
    [Arguments("""{"webhooks":{}}""", "operations/GetUser")]
    [Arguments("""{"components":{},"webhooks":{}}""", "operations/GetUser")]
    [Arguments("""{"webhooks":{"event":{"post":{"operationId":"Other"}}}}""", "operations/GetUser")]
    [Arguments("""{"components":{}}""", "/paths/~1users/get")]
    [Arguments("""{"webhooks":{}}""", "/paths/~1users/get")]
    public async Task OpenApi31DocumentsWithoutPathsReportNoPathOperation(string content, string selector)
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes("""{"openapi":"3.1.1","info":{"title":"Types","version":"1"},""" +
                content[1..]),
            ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.not_found");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("OpenAPI/3.0", """{"openapi":"3.0.4","components":{}}""")]
    [Arguments("Swagger/2.0", """{"swagger":"2.0","components":{}}""")]
    [Arguments("OpenAPI/3.1", """{"openapi":"3.1.1"}""")]
    [Arguments("OpenAPI/3.1", """{"openapi":"3.1.1","components":null}""")]
    [Arguments("OpenAPI/3.1", """{"openapi":"3.1.1","paths":null,"components":{}}""")]
    [Arguments("OpenAPI/3.1", """{"openapi":"3.1.1","paths":[],"webhooks":{}}""")]
    public async Task OptionalOpenApi31PathsDoNotRelaxOtherRootRequirements(string format, string document)
    {
        var result = ApiOperationDocumentResolver.Resolve(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/GetUser"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.paths");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("event", "operations/Notify", "$/webhooks/event/post")]
    [Arguments("event", "/webhooks/event/post", "$/webhooks/event/post")]
    [Arguments("x-event", "operations/Notify", "$/webhooks/x-event/post")]
    [Arguments("x-event", "/webhooks/x-event/post", "$/webhooks/x-event/post")]
    [Arguments("a/~b", "/webhooks/a~1~0b/post", "$/webhooks/a~1~0b/post")]
    [Arguments("", "operations/Notify", "$/webhooks//post")]
    public async Task OpenApi31SelectsNativeWebhookOperationsWithTheirDocumentProvenance(
        string webhook, string selector, string expectedPath)
    {
        var source = new Uri("https://registry.example/apis/hooks");
        var operation = System.Text.Json.Nodes.JsonNode.Parse("""
            {"operationId":"Notify","requestBody":{"content":{"application/json":{"schema":{"type":"string"}}}},
             "responses":{"200":{"description":"Accepted"}},"x-native":{"streaming":true}}
            """)!;
        var expected = operation.ToJsonString();
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["openapi"] = "3.1.1",
            ["webhooks"] = new System.Text.Json.Nodes.JsonObject
            {
                [webhook] = new System.Text.Json.Nodes.JsonObject { ["post"] = operation }
            }
        };
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector), source);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo(expectedPath);
        await Assert.That(result.DocumentUri).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""{"event":{"post":{"operationId":"Notify"}},"other":{"get":{"operationId":"Notify"}}}""", "{}", ApiOperationResolutionStatus.Ambiguous, "selection.ambiguous")]
    [Arguments("""{"event":{"post":{"operationId":"Notify"}}}""", """{"/users":{"get":{"operationId":"Notify"}}}""", ApiOperationResolutionStatus.Ambiguous, "selection.ambiguous")]
    [Arguments("""{"event":{"post":{"operationId":"notify"}}}""", "{}", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    [Arguments("""{"event":null}""", "{}", ApiOperationResolutionStatus.Invalid, "document.path_item")]
    [Arguments("""{"event":{"post":true}}""", "{}", ApiOperationResolutionStatus.Invalid, "document.operation")]
    [Arguments("""{"event":{"post":{"operationId":1}}}""", "{}", ApiOperationResolutionStatus.Invalid, "document.operation_id")]
    [Arguments("[]", "{}", ApiOperationResolutionStatus.Invalid, "document.webhooks")]
    [Arguments("null", "{}", ApiOperationResolutionStatus.Invalid, "document.webhooks")]
    [Arguments("""{"good":{"post":{"operationId":"Notify"}},"unavailable":{"$ref":"./missing.json"}}""", "{}", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("""{"unavailable":{"$ref":"./missing.json"}}""", """{"/users":{"get":{"operationId":"Notify"}}}""", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    public async Task OpenApi31WebhookSelectionRejectsAmbiguousMalformedAndUnavailableDefinitions(
        string webhooks, string paths, ApiOperationResolutionStatus status, string code)
    {
        var document = """{"openapi":"3.1.1","paths":PATHS,"webhooks":HOOKS}"""
            .Replace("PATHS", paths, StringComparison.Ordinal).Replace("HOOKS", webhooks, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"),
            new Uri("https://registry.example/apis/hooks"));
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OpenApi31WebhookOperationIdsFollowOnlyCallerSuppliedReferences(bool operationReference)
    {
        var calls = 0;
        var document = operationReference
            ? """{"openapi":"3.1.1","webhooks":{"event":{"post":{"$ref":"./shared.json#/operation"}}}}"""
            : """{"openapi":"3.1.1","webhooks":{"event":{"$ref":"./shared.json#/item"}}}""";
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"),
            new Uri("https://registry.example/apis/hooks"),
            (uri, _) =>
            {
                calls++;
                if (uri != "https://registry.example/apis/shared.json") { throw new InvalidOperationException(uri); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    """{"item":{"post":{"operationId":"Notify"}},"operation":{"operationId":"Notify"}}"""u8.ToArray());
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo(operationReference ? "$/operation" : "$/item/post");
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://registry.example/apis/shared.json"));
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("Notify");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("OpenAPI/3.0", "openapi", "3.0.4")]
    [Arguments("Swagger/2.0", "swagger", "2.0")]
    public async Task OlderFormatsDoNotAcquireOrSelectOpenApi31Webhooks(string format, string field, string version)
    {
        var document = """{"FIELD":"VERSION","paths":{},"webhooks":{"event":{"post":{"operationId":"Notify"}}}}"""
            .Replace("FIELD", field, StringComparison.Ordinal).Replace("VERSION", version, StringComparison.Ordinal);
        var result = ApiOperationDocumentResolver.Resolve(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        var pointer = ApiOperationDocumentResolver.Resolve(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#/webhooks/event/post"));
        await Assert.That(pointer.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(pointer.Diagnostic!.Code).IsEqualTo("selection.operation_pointer");
    }

    [Test]
    [Arguments("""{"event":{"$ref":"./shared.json#/item"}}""", ApiOperationResolutionStatus.NotFound)]
    [Arguments("""{"event":{"post":{"$ref":"./shared.json#/operation"}}}""", ApiOperationResolutionStatus.Invalid)]
    [Arguments("""{}""", ApiOperationResolutionStatus.NotFound)]
    public async Task WebhookJsonPointersDoNotTraverseReferences(string webhooks, ApiOperationResolutionStatus status)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes("""{"openapi":"3.1.1","webhooks":""" + webhooks + "}"),
            ApiOperationReference.Parse("/apigroups/g/apis/a#/webhooks/event/post"),
            new Uri("https://registry.example/apis/hooks"),
            (_, _) => { calls++; throw new InvalidOperationException("Pointers do not dereference."); });
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task WebhookJsonPointerSelectsAnOperationWithoutAnOperationId()
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            """{"openapi":"3.1.1","webhooks":{"event":{"post":{"summary":"Receive an event"}}}}"""u8.ToArray(),
            ApiOperationReference.Parse("/apigroups/g/apis/a#/webhooks/event/post"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/webhooks/event/post");
        await Assert.That(result.Operation.GetProperty("summary").GetString()).IsEqualTo("Receive an event");
        await Assert.That(result.Operation.TryGetProperty("operationId", out _)).IsFalse();
    }

    [Test]
    [Arguments("OpenAPI/3.0", "3.0.4", "operations/Notify")]
    [Arguments("OpenAPI/3.1", "3.1.1", "operations/Notify")]
    [Arguments("OpenAPI/3.0", "3.0.4", "/paths/~1subscribe/post/callbacks/x-notify/%7B$request.query.url%7D/put")]
    [Arguments("OpenAPI/3.1", "3.1.1", "/paths/~1subscribe/post/callbacks/x-notify/%7B$request.query.url%7D/put")]
    public async Task OpenApiCallbackOperationsRetainTheirNativeObjectAndPointer(
        string format, string version, string selector)
    {
        var document = """
            {"openapi":"VERSION","paths":{"/subscribe":{"post":{"callbacks":{
              "x-notify":{"{$request.query.url}":{"put":{"operationId":"Notify","x-native":[1,true]}}},
              "empty":{} }}}}}
            """.Replace("VERSION", version, StringComparison.Ordinal);
        var result = ApiOperationDocumentResolver.Resolve(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/paths/~1subscribe/post/callbacks/x-notify/{$request.query.url}/put");
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("Notify");
        await Assert.That(result.Operation.GetProperty("x-native").GetArrayLength()).IsEqualTo(2);
        await Assert.That(result.DocumentUri).IsNull();
    }

    [Test]
    [Arguments("operations/Deep")]
    [Arguments("/webhooks/event/post/callbacks/first/%7B$request.body%23~1url%7D/put/callbacks/second/https:~1~1client.example~1hook/delete")]
    public async Task NestedWebhookCallbacksSelectExactOperationsWithoutEvaluatingRuntimeExpressions(string selector)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json", """
            {"openapi":"3.1.1","webhooks":{"event":{"post":{"callbacks":{
              "first":{"{$request.body#/url}":{"put":{"callbacks":{
                "second":{"https://client.example/hook":{"delete":{"operationId":"Deep","summary":"Nested callback"}}}
              }}}}
            }}}}}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector),
            resolveReference: (_, _) => { calls++; throw new InvalidOperationException("Runtime expressions are not imports."); });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo(
            "$/webhooks/event/post/callbacks/first/{$request.body#~1url}/put/callbacks/second/https:~1~1client.example~1hook/delete");
        await Assert.That(result.Operation.GetProperty("summary").GetString()).IsEqualTo("Nested callback");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task CallbackReferencesResolveRelativeToTheContainingReferencedDocument()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json", """
            {"openapi":"3.1.1","paths":{"/subscribe":{"$ref":"./nested/path.json#/item"}}}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"),
            new Uri("https://api.example/root.json"), (uri, _) =>
            {
                calls.Add(uri);
                var json = uri switch
                {
                    "https://api.example/nested/path.json" =>
                        """{"item":{"post":{"callbacks":{"event":{"$ref":"./callbacks/map.json#/callback","summary":"Annotation"}}}}}""",
                    "https://api.example/nested/callbacks/map.json" =>
                        """{"callback":{"{$request.query.url}":{"$ref":"../target.json#/item"}}}""",
                    "https://api.example/nested/target.json" =>
                        """{"item":{"post":{"operationId":"Notify","summary":"External callback"}}}""",
                    _ => throw new InvalidOperationException(uri)
                };
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(json));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/item/post");
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://api.example/nested/target.json"));
        await Assert.That(result.Operation.GetProperty("summary").GetString()).IsEqualTo("External callback");
        await Assert.That(calls.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments("$url")]
    [Arguments("$method")]
    [Arguments("$statusCode")]
    [Arguments("$request.header.X-Callback")]
    [Arguments("$response.header.!#$%&'*+-.^_`|~012AZaz")]
    [Arguments("$request.query.")]
    [Arguments("$response.path.id")]
    [Arguments("$request.query.a\"b\\c")]
    [Arguments("$request.body")]
    [Arguments("$response.body#")]
    [Arguments("$request.body#/")]
    [Arguments("$request.body#/a~0b/~1/")]
    [Arguments("$request.body#/~01~10")]
    [Arguments("$request.body#/a%2Fb")]
    [Arguments("{$request.body#/a~01b}")]
    [Arguments("https://client.example/hook?id={$request.body#/id}&email={$request.body#/email}")]
    [Arguments("{$request.query.callback}")]
    [Arguments("https://client.example/hook")]
    [Arguments("url")]
    public async Task CallbackRuntimeSyntaxPreservesNativeKeysWithoutEvaluation(string expression)
    {
        foreach (var (format, version) in new[] { ("OpenAPI/3.0", "3.0.4"), ("OpenAPI/3.1", "3.1.1") })
        {
            var callback = new System.Text.Json.Nodes.JsonObject
            {
                [expression] = new System.Text.Json.Nodes.JsonObject
                {
                    ["post"] = new System.Text.Json.Nodes.JsonObject { ["operationId"] = "Notify" }
                }
            };
            var document = System.Text.Encoding.UTF8.GetBytes(
                """
                {"openapi":"VERSION","paths":{"/s":{"post":{"callbacks":{"c":CALLBACK}}}},
                 "components":{"callbacks":{"c":CALLBACK}}}
                """
                .Replace("VERSION", version, StringComparison.Ordinal)
                .Replace("CALLBACK", callback.ToJsonString(), StringComparison.Ordinal));
            var token = Uri.EscapeDataString(expression.Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal));
            foreach (var selector in new[] { "operations/Notify",
                "/paths/~1s/post/callbacks/c/" + token + "/post",
                "/components/callbacks/c/" + token + "/post" })
            {
                var result = await ApiOperationDocumentResolver.ResolveAsync(format, "application/json", document,
                    ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector),
                    resolveReference: (_, _) => throw new InvalidOperationException("Callback keys are not imports."));
                await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
                await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("Notify");
                await Assert.That(result.DocumentPath!.Contains(expression.Replace("~", "~0", StringComparison.Ordinal)
                    .Replace("/", "~1", StringComparison.Ordinal), StringComparison.Ordinal)).IsTrue();
            }
        }
    }

    [Test]
    [Arguments("$unknown")]
    [Arguments("$request.unknown.id")]
    [Arguments("$response.header.")]
    [Arguments("$request.header.bad name")]
    [Arguments("$request.header.a:b")]
    [Arguments("$request.header.ä")]
    [Arguments("$request.body#not-a-pointer")]
    [Arguments("$request.body#%2Furl")]
    [Arguments("$request.body#/a~")]
    [Arguments("$request.body#/a~2b")]
    [Arguments("{$url")]
    [Arguments("$url}")]
    [Arguments("https://client.example/}")]
    [Arguments("{}")]
    [Arguments("{url}")]
    [Arguments("{$request.body#/a~3}")]
    [Arguments("https://client.example/?a={$url}&b={$request.bad}")]
    public async Task MalformedCallbackExpressionsPreventEarlierMatchesAndInlineSelection(string expression)
    {
        foreach (var (format, version) in new[] { ("OpenAPI/3.0", "3.0.4"), ("OpenAPI/3.1", "3.1.1") })
        {
            var callback = new System.Text.Json.Nodes.JsonObject
            {
                [expression] = new System.Text.Json.Nodes.JsonObject
                {
                    ["post"] = new System.Text.Json.Nodes.JsonObject { ["operationId"] = "Other" }
                }
            };
            var document = System.Text.Encoding.UTF8.GetBytes(
                """
                {"openapi":"VERSION","paths":{"/s":{"post":{"operationId":"Notify","callbacks":{"c":CALLBACK}}}},
                 "components":{"callbacks":{"c":CALLBACK}}}
                """
                .Replace("VERSION", version, StringComparison.Ordinal)
                .Replace("CALLBACK", callback.ToJsonString(), StringComparison.Ordinal));
            var token = Uri.EscapeDataString(expression.Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal));
            foreach (var selector in new[] { "operations/Notify",
                "/paths/~1s/post/callbacks/c/" + token + "/post",
                "/components/callbacks/c/" + token + "/post" })
            {
                var result = await ApiOperationDocumentResolver.ResolveAsync(format, "application/json", document,
                    ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector),
                    resolveReference: (_, _) => throw new InvalidOperationException("Invalid keys are not imports."));
                await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.callback_expression");
                await Assert.That(result.Diagnostic.Path.EndsWith("/" + expression.Replace("~", "~0", StringComparison.Ordinal)
                    .Replace("/", "~1", StringComparison.Ordinal), StringComparison.Ordinal)).IsTrue();
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReferencedCallbackExpressionsAreCheckedButExtensionsAndUnusedDefinitionsAreNotTraversed(bool malformed)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json", """
            {"openapi":"3.1.1","paths":{"/s":{"post":{"operationId":"Notify","callbacks":{"c":{"$ref":"./callback.json"}}}}},
             "components":{"callbacks":{"unused":{"{$invalid}":null}}}}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"),
            new Uri("https://api.example/root.json"), (_, _) =>
            {
                calls++;
                var callback = malformed ? """
                    {"x-{$invalid}":null,"{$request.body#/bad~2}":{"$ref":"https://must-not-load.example/item"}}
                    """ : """
                    {"x-{$invalid}":null,"{$request.body#/url}":{"post":{"operationId":"Other"}}}
                    """;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(callback));
            });
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(malformed
            ? ApiOperationResolutionStatus.Invalid : ApiOperationResolutionStatus.Selected);
        if (malformed)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.callback_expression");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/{$request.body#~1bad~02}");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
        else
        {
            await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("Notify");
            await Assert.That(result.DocumentPath).IsEqualTo("$/paths/~1s/post");
        }
    }

    [Test]
    [Arguments("operations/Notify")]
    [Arguments("/paths/~1s/post/callbacks/outer/url/put/callbacks/inner/%7B$request.body%23~1bad~02%7D/post")]
    [Arguments("/components/pathItems/item/post/callbacks/outer/url/put/callbacks/inner/%7B$request.body%23~1bad~02%7D/post")]
    public async Task DeepCallbackExpressionFailuresAreNotHiddenByValidOuterExpressions(string selector)
    {
        var document = """
            {"openapi":"3.1.1","paths":{"/s":ITEM},"components":{"pathItems":{"item":ITEM}}}
            """.Replace("ITEM", """
            {"post":{"operationId":"Notify","callbacks":{"outer":{"url":{"put":{"callbacks":{
              "inner":{"{$request.body#/bad~2}":{"post":{"operationId":"Other"}}}
            }}}}}}}
            """, StringComparison.Ordinal);
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.callback_expression");
        await Assert.That(result.Diagnostic.Path.EndsWith("/callbacks/inner/{$request.body#~1bad~02}",
            StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"callbacks":[]}""", ApiOperationResolutionStatus.Invalid, "document.callbacks")]
    [Arguments("""{"callbacks":{"event":null}}""", ApiOperationResolutionStatus.Invalid, "document.callback")]
    [Arguments("""{"callbacks":{"event":{"$ref":true}}}""", ApiOperationResolutionStatus.Invalid, "dependency.callback")]
    [Arguments("""{"callbacks":{"event":{"{$request.query.url}":null}}}""", ApiOperationResolutionStatus.Invalid, "document.path_item")]
    [Arguments("""{"callbacks":{"event":{"{$request.query.url}":{"post":{"operationId":true}}}}}""", ApiOperationResolutionStatus.Invalid, "document.operation_id")]
    [Arguments("""{"callbacks":{"event":{"$ref":"./missing.json"}}}""", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("""{"operationId":"Notify","callbacks":{"event":{"$ref":"./missing.json"}}}""", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("""{"operationId":"Notify","callbacks":{"event":{"url":{"post":{"operationId":"Notify"}}}}}""", ApiOperationResolutionStatus.Ambiguous, "selection.ambiguous")]
    [Arguments("""{"callbacks":{"event":{"url":{"post":{"operationId":"notify"}}}}}""", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    [Arguments("""{"callbacks":{"event":{"x-extension":{"post":{"operationId":"Notify"}}}}}""", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    public async Task CallbackSelectionDoesNotReturnPartialOrAmbiguousOperations(
        string operation, ApiOperationResolutionStatus status, string code)
    {
        var document = """{"openapi":"3.1.1","paths":{"/subscribe":{"post":OPERATION}}}"""
            .Replace("OPERATION", operation, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"), new Uri("https://api.example/root.json"));
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallbackReferenceAndOperationCyclesAreIndeterminate(bool callbackReference)
    {
        var document = callbackReference ? """
            {"openapi":"3.1.1","paths":{"/s":{"post":{"operationId":"Notify","callbacks":{"c":{"$ref":"#/components/callbacks/c"}}}}},
             "components":{"callbacks":{"c":{"$ref":"#/components/callbacks/c"}}}}
            """ : """
            {"openapi":"3.1.1","paths":{"/s":{"post":{"operationId":"Notify","callbacks":{"c":{"url":{"$ref":"#/paths/~1s"}}}}}}}
            """;
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.cycle");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(64, ApiOperationResolutionStatus.Selected)]
    [Arguments(65, ApiOperationResolutionStatus.Indeterminate)]
    public async Task CallbackTraversalBoundsShallowReferencedGraphs(int edges, ApiOperationResolutionStatus status)
    {
        var items = new System.Text.Json.Nodes.JsonObject();
        for (var index = 0; index <= edges; index++)
        {
            var operation = index == edges
                ? new System.Text.Json.Nodes.JsonObject { ["operationId"] = "Notify" }
                : new System.Text.Json.Nodes.JsonObject
                {
                    ["callbacks"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["next"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["url"] = new System.Text.Json.Nodes.JsonObject { ["$ref"] = "#/items/i" + (index + 1) }
                        }
                    }
                };
            items["i" + index] = new System.Text.Json.Nodes.JsonObject { ["post"] = operation };
        }
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["openapi"] = "3.1.1",
            ["paths"] = new System.Text.Json.Nodes.JsonObject
            {
                ["/s"] = new System.Text.Json.Nodes.JsonObject { ["$ref"] = "#/items/i0" }
            },
            ["items"] = items
        };
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"));
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.DocumentPath).IsEqualTo("$/items/i64/post");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.callback_depth");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("""{"$ref":"./missing.json"}""", ApiOperationResolutionStatus.NotFound)]
    [Arguments("""{"url":{"$ref":"./missing.json"}}""", ApiOperationResolutionStatus.NotFound)]
    [Arguments("""{"url":{"post":{"$ref":"./missing.json"}}}""", ApiOperationResolutionStatus.Invalid)]
    public async Task CallbackPointersNeverAcquireReferencedTargets(string callback, ApiOperationResolutionStatus status)
    {
        var calls = 0;
        var document = """{"openapi":"3.1.1","paths":{"/s":{"post":{"callbacks":{"c":CALLBACK}}}}}"""
            .Replace("CALLBACK", callback, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#/paths/~1s/post/callbacks/c/url/post"),
            resolveReference: (_, _) => { calls++; throw new InvalidOperationException("Pointers do not dereference."); });
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ReusedCallbackDefinitionsRemainDistinctOperationOccurrences()
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json", """
            {"openapi":"3.1.1","paths":{"/s":{"post":{"callbacks":{
              "first":{"$ref":"#/shared"},"second":{"$ref":"#/shared"},"third":{"$ref":"#/shared"}
            }}}},"shared":{"url":{"post":{"operationId":"Notify"}}}}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Ambiguous);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.ambiguous");
    }

    [Test]
    public async Task CallbackAcquisitionPreservesCallerDenialAndCancellation()
    {
        var document = """
            {"openapi":"3.1.1","paths":{"/s":{"post":{"operationId":"Notify","callbacks":{"c":{"$ref":"./callback.json"}}}}}}
            """u8.ToArray();
        var reference = ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify");
        var denied = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            document, reference, new Uri("https://api.example/root.json"),
            (_, _) => throw new UnauthorizedAccessException("Blocked by caller policy."));
        await Assert.That(denied.Status).IsEqualTo(ApiOperationResolutionStatus.Denied);
        await Assert.That(denied.Diagnostic!.Code).IsEqualTo("dependency.denied");
        await Assert.That(denied.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
                document, reference, new Uri("https://api.example/root.json"), (_, _) =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>("{}"u8.ToArray());
                }, cancellation.Token));
    }

    [Test]
    public async Task SwaggerDoesNotInterpretCallbackShapedDataAsOpenApiOperations()
    {
        var document = """
            {"swagger":"2.0","paths":{"/s":{"post":{"callbacks":{"c":{"url":{"post":{"operationId":"Notify"}}}}}}}}
            """u8.ToArray();
        var reference = ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify");
        var result = ApiOperationDocumentResolver.Resolve("Swagger/2.0", "application/json", document, reference);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        var nested = ApiOperationDocumentResolver.Resolve("Swagger/2.0", "application/json", document,
            ApiOperationReference.Parse("/apigroups/g/apis/a#/paths/~1s/post/callbacks/c/url/post"));
        await Assert.That(nested.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(nested.Diagnostic!.Code).IsEqualTo("selection.operation_pointer");
    }

    [Test]
    [Arguments(64, ApiOperationResolutionStatus.Selected)]
    [Arguments(65, ApiOperationResolutionStatus.Indeterminate)]
    public async Task CallbackReferenceChainsHonorTheAdvertisedReferenceDepth(
        int edges, ApiOperationResolutionStatus status)
    {
        var callbacks = new System.Text.Json.Nodes.JsonObject();
        for (var index = 0; index < edges; index++)
        {
            callbacks["c" + index] = index == edges - 1
                ? System.Text.Json.Nodes.JsonNode.Parse("""{"url":{"post":{"operationId":"Notify"}}}""")
                : new System.Text.Json.Nodes.JsonObject { ["$ref"] = "#/definitions/c" + (index + 1) };
        }
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["openapi"] = "3.1.1",
            ["paths"] = System.Text.Json.Nodes.JsonNode.Parse("""
                {"/s":{"post":{"callbacks":{"c":{"$ref":"#/definitions/c0"}}}}}
                """),
            ["definitions"] = callbacks
        };
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"));
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.DocumentPath).IsEqualTo("$/definitions/c63/url/post");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.reference_depth");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("operations/Start", "$/paths/~1s/post", "Parent")]
    [Arguments("/paths/~1s/post/callbacks/c/url/post", "$/paths/~1s/post/callbacks/c/url/post", "Anonymous callback")]
    public async Task CallbackSelectionIgnoresExtensionsAndUnusedDefinitionsAndPermitsAnonymousPointers(
        string selector, string expectedPath, string summary)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json", """
            {"openapi":"3.1.1","paths":{"/s":{"post":{"operationId":"Start","summary":"Parent",
              "callbacks":{"c":{"x-extension":{"$ref":"https://untrusted.example/not-imported"},
                               "url":{"post":{"summary":"Anonymous callback"}}}}}}},
             "components":{"callbacks":{"unused":{"$ref":"https://untrusted.example/unused"}}}}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector),
            resolveReference: (_, _) => { calls++; throw new InvalidOperationException("No dependency is reachable."); });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo(expectedPath);
        await Assert.That(result.Operation.GetProperty("summary").GetString()).IsEqualTo(summary);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("OpenAPI/3.1", "3.1.1", """{"pathItems":{"reusable":{"get":{"summary":"Component operation"}}}}""",
        "/components/pathItems/reusable/get", "$/components/pathItems/reusable/get")]
    [Arguments("OpenAPI/3.1", "3.1.1", """{"pathItems":{"x-reusable":{"get":{"summary":"Component operation"}}}}""",
        "/components/pathItems/x-reusable/get", "$/components/pathItems/x-reusable/get")]
    [Arguments("OpenAPI/3.0", "3.0.4", """{"callbacks":{"reusable":{"url":{"get":{"summary":"Component operation"}}}}}""",
        "/components/callbacks/reusable/url/get", "$/components/callbacks/reusable/url/get")]
    [Arguments("OpenAPI/3.1", "3.1.1", """{"callbacks":{"x-reusable":{"{$request.body#/url}":{"get":{"summary":"Component operation"}}}}}""",
        "/components/callbacks/x-reusable/%7B$request.body%23~1url%7D/get",
        "$/components/callbacks/x-reusable/{$request.body#~1url}/get")]
    [Arguments("OpenAPI/3.1", "3.1.1", """{"pathItems":{"reusable":{"post":{"callbacks":{"done":{"url":{"get":{"summary":"Component operation"}}}}}}}}""",
        "/components/pathItems/reusable/post/callbacks/done/url/get",
        "$/components/pathItems/reusable/post/callbacks/done/url/get")]
    [Arguments("OpenAPI/3.0", "3.0.4", """{"callbacks":{"reusable":{"url":{"post":{"callbacks":{"done":{"url":{"get":{"summary":"Component operation"}}}}}}}}}""",
        "/components/callbacks/reusable/url/post/callbacks/done/url/get",
        "$/components/callbacks/reusable/url/post/callbacks/done/url/get")]
    public async Task OpenApiPointersSelectInlineOperationsInReusableComponentDefinitions(
        string format, string version, string components, string selector, string expectedPath)
    {
        var calls = 0;
        var document = """{"openapi":"VERSION","paths":{},"components":COMPONENTS}"""
            .Replace("VERSION", version, StringComparison.Ordinal).Replace("COMPONENTS", components, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector),
            resolveReference: (_, _) => { calls++; throw new InvalidOperationException("Inline pointer must not acquire dependencies."); });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo(expectedPath);
        await Assert.That(result.Operation.GetProperty("summary").GetString()).IsEqualTo("Component operation");
        await Assert.That(result.Operation.TryGetProperty("operationId", out _)).IsFalse();
        await Assert.That(result.DocumentUri).IsNull();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("OpenAPI/3.0", "openapi", "3.0.4", "/components/pathItems/reusable/get")]
    [Arguments("Swagger/2.0", "swagger", "2.0", "/components/pathItems/reusable/get")]
    [Arguments("Swagger/2.0", "swagger", "2.0", "/components/callbacks/reusable/url/get")]
    [Arguments("OpenAPI/3.1", "openapi", "3.1.1", "/components/schemas/reusable/get")]
    [Arguments("OpenAPI/3.1", "openapi", "3.1.1", "/components/callbacks/reusable/x-extension/get")]
    [Arguments("OpenAPI/3.1", "openapi", "3.1.1", "/components/pathItems/reusable/summary")]
    [Arguments("OpenAPI/3.1", "openapi", "3.1.1", "/components/pathItems/reusable/get/responses")]
    public async Task ComponentOperationPointersEnforceNativeVersionAndObjectPositions(
        string format, string field, string version, string selector)
    {
        var document = """{"FIELD":"VERSION","paths":{},"components":{}}"""
            .Replace("FIELD", field, StringComparison.Ordinal).Replace("VERSION", version, StringComparison.Ordinal);
        var result = ApiOperationDocumentResolver.Resolve(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.operation_pointer");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"pathItems":{"reusable":{"$ref":"./external.json#/item"}}}""",
        "/components/pathItems/reusable/get", ApiOperationResolutionStatus.NotFound)]
    [Arguments("""{"pathItems":{"reusable":{"get":{"$ref":"./external.json#/operation"}}}}""",
        "/components/pathItems/reusable/get", ApiOperationResolutionStatus.Invalid)]
    [Arguments("""{"callbacks":{"reusable":{"$ref":"./external.json#/callback"}}}""",
        "/components/callbacks/reusable/url/get", ApiOperationResolutionStatus.NotFound)]
    [Arguments("""{"callbacks":{"reusable":{"url":{"$ref":"./external.json#/item"}}}}""",
        "/components/callbacks/reusable/url/get", ApiOperationResolutionStatus.NotFound)]
    [Arguments("""{"pathItems":{"reusable":{"get":true}}}""",
        "/components/pathItems/reusable/get", ApiOperationResolutionStatus.Invalid)]
    [Arguments("""{}""", "/components/pathItems/reusable/get", ApiOperationResolutionStatus.NotFound)]
    [Arguments("""{"pathItems":null}""", "/components/pathItems/reusable/get", ApiOperationResolutionStatus.NotFound)]
    public async Task ComponentOperationPointersRejectReferencesAndMissingOrNonOperationTargets(
        string components, string selector, ApiOperationResolutionStatus status)
    {
        var calls = 0;
        var document = """{"openapi":"3.1.1","paths":{},"components":COMPONENTS}"""
            .Replace("COMPONENTS", components, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector),
            resolveReference: (_, _) => { calls++; throw new InvalidOperationException("Pointers do not dereference."); });
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ComponentDefinitionsAreOnlyOperationIdCandidatesWhenReachedThroughTheApi()
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json", """
            {"openapi":"3.1.1","paths":{"/s":{"$ref":"#/components/pathItems/used"}},
             "components":{"pathItems":{
               "used":{"get":{"operationId":"Notify","summary":"Reachable"}},
               "unused":{"get":{"operationId":"Notify"}}},
               "callbacks":{"unused":{"url":{"post":{"operationId":"Notify"}}}}}}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Notify"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/components/pathItems/used/get");
        await Assert.That(result.Operation.GetProperty("summary").GetString()).IsEqualTo("Reachable");
    }

    [Test]
    [Arguments("OpenAPI/3.0", """{"openapi":"3.0.3","paths":{"/users":{"get":{"operationId":"GetUser"}}}}""")]
    [Arguments("Swagger/2.0", """{"swagger":"2.0","paths":{"/users":{"get":{"operationId":"GetUser"}}}}""")]
    public async Task ResolvesOperationIdsForEveryAdvertisedDocumentFormat(string format, string document)
    {
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("GetUser");
    }

    [Test]
    [Arguments("OpenAPI/3.0", "3.0.0")]
    [Arguments("OpenAPI/3.0", "3.0.4")]
    [Arguments("OpenAPI/3.0", "3.0.4-rc.1")]
    [Arguments("OpenAPI/3.0", "3.0.999999999999999999999999999999999999")]
    [Arguments("OpenAPI/3.1", "3.1.0")]
    [Arguments("OpenAPI/3.1", "3.1.1")]
    [Arguments("OpenAPI/3.1", "3.1.01")]
    [Arguments("OpenAPI/3.1", "3.1.1-rc.1")]
    [Arguments("OpenAPI/3.1", "3.1.999999999999999999999999999999999999")]
    public async Task OpenApiVersionCheckingAcceptsTheSupportedFeatureSetWithoutRestrictingPatchNumbers(
        string format, string version)
    {
        var result = ApiOperationDocumentResolver.Resolve(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes("""
                {"openapi":"VERSION","info":{"title":"Users","version":"independent release name"},
                 "paths":{"/users":{"get":{"operationId":"GetUser"}}}}
                """.Replace("VERSION", version, StringComparison.Ordinal)),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/GetUser"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("GetUser");
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("3.1.")]
    [Arguments("3.1.garbage")]
    [Arguments("3.1.1-")]
    [Arguments("3.1.-1")]
    [Arguments("3.1.+1")]
    [Arguments("3.1.1.0")]
    [Arguments("3.1.1suffix")]
    [Arguments("3.1.1\n")]
    [Arguments("3.1.1 ")]
    [Arguments(" 3.1.1")]
    [Arguments("3.1")]
    [Arguments("3.2.0")]
    [Arguments("3.10.0")]
    [Arguments("3.1.\u0661")]
    public async Task OpenApiVersionCheckingRejectsMalformedVersionsBeforeDependencyAcquisition(string version)
    {
        var calls = 0;
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["openapi"] = version,
            ["paths"] = System.Text.Json.Nodes.JsonNode.Parse("""{"/users":{"$ref":"./shared.json#/path"}}""")
        };
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/GetUser"),
            new Uri("https://registry.example/apis/a"),
            (_, _) => { calls++; throw new InvalidOperationException("Invalid roots must not acquire dependencies."); });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.openapi_version");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/openapi");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("OpenAPI/3.0", "3.1.1")]
    [Arguments("OpenAPI/3.1", "3.0.4")]
    [Arguments("OpenAPI/3.0", "3.0.garbage")]
    [Arguments("OpenAPI/3.0", "3.0.")]
    [Arguments("OpenAPI/3.0", "3.0.4\n")]
    [Arguments("OpenAPI/3.1", "3.1.garbage")]
    public async Task OpenApiJsonPointerSelectionCannotBypassTheDeclaredVersionCheck(string format, string version)
    {
        var document = new System.Text.Json.Nodes.JsonObject
        {
            ["openapi"] = version,
            ["paths"] = System.Text.Json.Nodes.JsonNode.Parse("""{"/users":{"get":{"operationId":"GetUser"}}}""")
        };
        var result = ApiOperationDocumentResolver.Resolve(format, "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#/paths/~1users/get"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.openapi_version");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("{", "document.json")]
    [Arguments("[]", "document.root")]
    [Arguments("""{"openapi":"3.1.1"}""", "document.paths")]
    public async Task RejectsMalformedApiDocumentShapes(string document, string diagnosticCode)
    {
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnosticCode);
    }

    [Test]
    [Arguments("""{"openapi":"3.1.1","paths":{"/users":true}}""", "document.path_item")]
    [Arguments("""{"openapi":"3.1.1","paths":{"/users":{"get":true}}}""", "document.operation")]
    [Arguments("""{"openapi":"3.1.1","paths":{"/users":{"get":{"operationId":7}}}}""", "document.operation_id")]
    public async Task RejectsMalformedPathItemsAndOperationIdentifiers(string document, string diagnosticCode)
    {
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnosticCode);
    }

    [Test]
    [Arguments("getuser", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    [Arguments("missing", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    public async Task OperationIdSelectionIsExactAndReportsMissingOperations(string operationId,
        ApiOperationResolutionStatus expected, string diagnosticCode)
    {
        var reference = ApiOperationReference.Parse($"/apigroups/users/apis/Profile#operations/{operationId}");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(OpenApiDocument), reference);

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnosticCode);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task DuplicateOperationIdsAreAmbiguous()
    {
        const string document = """
            {"openapi":"3.1.1","paths":{"/a":{"get":{"operationId":"same"}},"/b":{"post":{"operationId":"same"}}}}
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/same");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Ambiguous);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.ambiguous");
    }

    [Test]
    public async Task OperationIdSelectionFollowsLocalPathItemReferences()
    {
        const string document = """
            {
              "openapi":"3.1.1",
              "paths":{"/users":{"$ref":"#/components/pathItems/User"}},
              "components":{"pathItems":{"User":{"get":{"operationId":"GetUser"}}}}
            }
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("GetUser");
        await Assert.That(result.DocumentPath).IsEqualTo("$/components/pathItems/User/get");
    }

    [Test]
    public async Task OperationIdSelectionLoadsExternalPathItemsOnlyThroughTheSuppliedResolver()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"$ref":"./shared.json#/path"}}}""";
        const string dependency = """{"path":{"get":{"operationId":"GetUser"}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        string? requestedUri = null;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"),
            (uri, _) =>
            {
                requestedUri = uri;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(dependency));
            });

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(requestedUri).IsEqualTo("https://api.example/shared.json");
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("GetUser");
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://api.example/shared.json"));
        await Assert.That(result.DocumentPath).IsEqualTo("$/path/get");
    }

    [Test]
    public async Task OperationIdSelectionFollowsLocalOperationReferences()
    {
        const string document = """
            {
              "openapi":"3.1.1",
              "paths":{"/users":{"get":{"$ref":"#/components/x-operations/GetUser"}}},
              "components":{"x-operations":{"GetUser":{"operationId":"GetUser"}}}
            }
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("GetUser");
        await Assert.That(result.DocumentPath).IsEqualTo("$/components/x-operations/GetUser");
    }

    [Test]
    public async Task OperationIdSelectionLoadsExternalOperationReferencesOnlyThroughTheSuppliedResolver()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"get":{"$ref":"./operations.json#/GetUser"}}}}""";
        const string dependency = """{"GetUser":{"operationId":"GetUser"}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        string? requestedUri = null;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"),
            (uri, _) =>
            {
                requestedUri = uri;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(dependency));
            });

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(requestedUri).IsEqualTo("https://api.example/operations.json");
        await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("GetUser");
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://api.example/operations.json"));
        await Assert.That(result.DocumentPath).IsEqualTo("$/GetUser");
    }

    [Test]
    public async Task OperationReferenceCyclesAreIndeterminate()
    {
        const string document = """
            {
              "openapi":"3.1.1",
              "paths":{"/users":{"get":{"$ref":"#/components/x-operations/A"}}},
              "components":{"x-operations":{
                "A":{"$ref":"#/components/x-operations/B"},
                "B":{"$ref":"#/components/x-operations/A"}
              }}
            }
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.cycle");
    }

    [Test]
    [Arguments("""{"$ref":true}""", ApiOperationResolutionStatus.Invalid, "dependency.operation")]
    [Arguments("""{"$ref":"#/components/x-operations/Value"}""", ApiOperationResolutionStatus.Invalid, "document.operation")]
    [Arguments("""{"$ref":"#/components/x-operations/GetUser","operationId":"GetUser"}""",
        ApiOperationResolutionStatus.Indeterminate, "dependency.operation_siblings")]
    public async Task InvalidOrUnsupportedOperationReferencesAreReported(string operationReference,
        ApiOperationResolutionStatus expectedStatus, string diagnosticCode)
    {
        var document = $$$"""
            {
              "openapi":"3.1.1",
              "paths":{"/users":{"get":{{{operationReference}}}}},
              "components":{"x-operations":{"GetUser":{"operationId":"GetUser"},"Value":7}}
            }
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(expectedStatus);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnosticCode);
    }

    [Test]
    public async Task ExternalOperationReferencesAreNotFetchedWithoutAResolver()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"get":{"$ref":"./operations.json#/GetUser"}}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"));

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
    }

    [Test]
    public async Task ReferencedPathItemMergesNonConflictingOperations()
    {
        const string document = """
            {
              "openapi":"3.1.1",
              "paths":{"/users":{"$ref":"#/components/pathItems/User","patch":{"operationId":"UpdateUser"}}},
              "components":{"pathItems":{"User":{"get":{"operationId":"Original"},"post":{"operationId":"CreateUser"}}}}
            }
            """;
        var localReference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/UpdateUser");
        var referencedReference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Original");
        var inheritedReference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/CreateUser");

        var local = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), localReference);
        var referenced = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), referencedReference);
        var inherited = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), inheritedReference);

        await Assert.That(local.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(local.Operation.GetProperty("operationId").GetString()).IsEqualTo("UpdateUser");
        await Assert.That(local.DocumentPath).IsEqualTo("$/paths/~1users/patch");
        await Assert.That(referenced.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(referenced.Operation.GetProperty("operationId").GetString()).IsEqualTo("Original");
        await Assert.That(referenced.DocumentPath).IsEqualTo("$/components/pathItems/User/get");
        await Assert.That(inherited.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(inherited.Operation.GetProperty("operationId").GetString()).IsEqualTo("CreateUser");
        await Assert.That(inherited.DocumentPath).IsEqualTo("$/components/pathItems/User/post");
    }

    [Test]
    public async Task ConflictingPathItemReferenceSiblingsAreIndeterminate()
    {
        const string document = """
            {
              "openapi":"3.1.1",
              "paths":{"/users":{"$ref":"#/components/pathItems/User","get":{"operationId":"Override"}}},
              "components":{"pathItems":{"User":{"get":{"operationId":"Original"}}}}
            }
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Override");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.path_item_conflict");
    }

    [Test]
    public async Task MissingExternalPathItemReturnsIndeterminateAndUsesNormalizedDocumentUri()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"$ref":"./shared.json#/path"}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        string? requestedUri = null;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"),
            (uri, _) =>
            {
                requestedUri = uri;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(requestedUri).IsEqualTo("https://api.example/shared.json");
    }

    [Test]
    public async Task ExternalPathItemReferencesAreNotFetchedWithoutAResolver()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"$ref":"./shared.json#/path"}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"));

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
    }

    [Test]
    public async Task PathItemReferenceCyclesAreIndeterminate()
    {
        const string document = """
            {
              "openapi":"3.1.1",
              "paths":{"/users":{"$ref":"#/components/pathItems/A"}},
              "components":{"pathItems":{
                "A":{"$ref":"#/components/pathItems/B"},
                "B":{"$ref":"#/components/pathItems/A"}
              }}
            }
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.cycle");
    }

    [Test]
    public async Task InvalidLocalReferencePointerIsReportedAsInvalid()
    {
        const string document = """
            {
              "openapi":"3.1.1",
              "paths":{"/users":{"$ref":"#/components/pathItems/User~2"}},
              "components":{"pathItems":{"User":{"get":{"operationId":"GetUser"}}}}
            }
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.pointer");
    }

    [Test]
    public async Task MalformedPathItemReferenceUriIsReportedAsInvalid()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"$ref":"http://[invalid"}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"));

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.uri");
    }

    [Test]
    public async Task OperationIdDuplicatesAcrossReferencedPathItemsAreAmbiguous()
    {
        const string document = """
            {
              "openapi":"3.1.1",
              "paths":{
                "/a":{"$ref":"#/components/pathItems/Shared"},
                "/b":{"$ref":"#/components/pathItems/Shared"}
              },
              "components":{"pathItems":{"Shared":{"get":{"operationId":"same"}}}}
            }
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/same");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Ambiguous);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.ambiguous");
    }

    [Test]
    public async Task InvalidReferencedJsonIsReportedAsInvalid()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"$ref":"./shared.json#/path"}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"),
            (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes("{")));

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.json");
    }

    [Test]
    [Arguments(32, ApiOperationResolutionStatus.Selected, 32)]
    [Arguments(33, ApiOperationResolutionStatus.Indeterminate, 32)]
    public async Task ExternalPathItemReferenceLimitIsEnforced(int documentCount,
        ApiOperationResolutionStatus expectedStatus, int expectedLoads)
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"$ref":"./0.json"}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var loadCount = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"),
            (uri, _) =>
            {
                loadCount++;
                var index = int.Parse(Path.GetFileNameWithoutExtension(new Uri(uri).AbsolutePath),
                    System.Globalization.CultureInfo.InvariantCulture);
                var dependency = index + 1 == documentCount
                    ? """{"get":{"operationId":"GetUser"}}"""
                    : $$"""{"$ref":"./{{index + 1}}.json"}""";
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(dependency));
            });

        await Assert.That(result.Status).IsEqualTo(expectedStatus);
        await Assert.That(loadCount).IsEqualTo(expectedLoads);
        if (expectedStatus == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("operationId").GetString()).IsEqualTo("GetUser");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.references");
        }
    }

    [Test]
    public async Task CancellationIsObservedAfterReferenceResolution()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/users":{"$ref":"./shared.json#/path"}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
                System.Text.Encoding.UTF8.GetBytes(document), reference,
                new Uri("https://api.example/openapi.json"),
                (_, _) =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        System.Text.Encoding.UTF8.GetBytes("""{"path":{"get":{"operationId":"GetUser"}}}"""));
                }, cancellation.Token));
    }

    [Test]
    public async Task UnresolvedPathItemReferenceMakesOperationIdSelectionIndeterminate()
    {
        const string document = """
            {"openapi":"3.1.1","paths":{"/a":{"$ref":"./shared.json#/path"}}}
            """;
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.base_uri");
    }

    [Test]
    public async Task OperationPointerDoesNotTraverseReferencedPathItems()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/a":{"$ref":"./shared.json#/path"}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#/paths/~1a/get");
        var resolverCalled = false;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference, new Uri("https://api.example/openapi.json"),
            (_, _) =>
            {
                resolverCalled = true;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(ReadOnlyMemory<byte>.Empty);
            });

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.not_found");
        await Assert.That(resolverCalled).IsFalse();
    }

    [Test]
    public async Task OperationPointerDoesNotTraverseReferencedOperations()
    {
        const string document = """{"openapi":"3.1.1","paths":{"/a":{"get":{"$ref":"./operation.json"}}}}""";
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#/paths/~1a/get");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.not_operation");
    }

    [Test]
    [Arguments("OpenAPI/3.2", "application/json", ApiOperationResolutionStatus.Unsupported, "format.unsupported")]
    [Arguments("OpenAPI/3.1", "application/yaml", ApiOperationResolutionStatus.Unsupported, "contenttype.unsupported")]
    [Arguments("OpenAPI/3.0", "application/json", ApiOperationResolutionStatus.Invalid, "document.openapi_version")]
    public async Task RejectsUnsupportedOrMismatchedApiFormats(string format, string contentType,
        ApiOperationResolutionStatus expected, string diagnosticCode)
    {
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");
        var result = ApiOperationDocumentResolver.Resolve(format, contentType,
            System.Text.Encoding.UTF8.GetBytes(OpenApiDocument), reference);

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnosticCode);
    }

    [Test]
    public async Task UnsupportedSelectorFormsAreNotTreatedAsMissingOperations()
    {
        var reference = ApiOperationReference.Parse(
            "/apigroups/users/apis/Profile#services/com.example.Profile/operations/GetProfile");
        var result = ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
            System.Text.Encoding.UTF8.GetBytes(OpenApiDocument), reference);

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selector.unsupported");
    }

    [Test]
    public async Task CancellationIsObservedBeforeParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reference = ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser");

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            ApiOperationDocumentResolver.Resolve("OpenAPI/3.1", "application/json",
                System.Text.Encoding.UTF8.GetBytes(OpenApiDocument), reference, cancellation.Token));
    }

    [Test]
    public async Task AvroProtocolSelectionPreservesTheDeclaredMessageWithoutResolvingMetadataReferences()
    {
        const string document = """
            {"protocol":"Users","namespace":"com.example",
             "types":[{"type":"record","name":"User","fields":[{"name":"id","type":"long"}]},
                      {"type":"error","name":"Denied","fields":[]}],
             "messages":{
               "Other":{"request":[],"response":"null","one-way":true},
               "GetUser":{"request":[{"name":"id","type":"long","default":0}],
                          "response":"User","errors":["Denied"],"one-way":false,
                          "$ref":"https://untrusted.example/not-an-avro-import",
                          "custom":{"native":true}}}}
            """;
        var calls = 0;
        var source = new Uri("https://registry.example/apis/users");
        var result = await ApiOperationDocumentResolver.ResolveAsync("AvroProtocol/1.12.0",
            "application/json; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser"),
            source, (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Avro message metadata is not a document dependency.");
            });

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/messages/GetUser");
        await Assert.That(result.DocumentUri).IsEqualTo(source);
        await Assert.That(result.Diagnostic).IsNull();
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("messages").GetProperty("GetUser").GetRawText());
        await Assert.That(result.Operation.GetProperty("request")[0].GetProperty("default").GetInt32()).IsEqualTo(0);
        await Assert.That(result.Operation.GetProperty("response").GetString()).IsEqualTo("User");
        await Assert.That(result.Operation.GetProperty("errors")[0].GetString()).IsEqualTo("Denied");
        await Assert.That(result.Operation.GetProperty("one-way").GetBoolean()).IsFalse();
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("""{"protocol":"Users"}""")]
    [Arguments("""{"protocol":"Users","messages":{}}""")]
    [Arguments("""{"protocol":"Users","messages":{"getuser":{"request":[],"response":"null"}}}""")]
    public async Task AvroProtocolMissingAndDifferentCaseMessagesDoNotSelectAResult(string document)
    {
        var result = ResolveAvroProtocol(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.not_found");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
        await Assert.That(result.DocumentUri).IsNull();
    }

    [Test]
    [Arguments("""{"protocol":"bad-name"}""", "document.protocol", "$/protocol")]
    [Arguments("""{"protocol":"9Users"}""", "document.protocol", "$/protocol")]
    [Arguments("""{"protocol":"com..Users"}""", "document.protocol", "$/protocol")]
    [Arguments("""{"protocol":"Users","namespace":"bad-name"}""", "document.namespace", "$/namespace")]
    [Arguments("""{"protocol":"Users","doc":null}""", "document.doc", "$/doc")]
    [Arguments("""{"protocol":"Users","types":{}}""", "document.types", "$/types")]
    [Arguments("""{"protocol":"Users","types":[null]}""", "document.type_declaration", "$/types/0")]
    [Arguments("""{"protocol":"Users","types":["string"]}""", "document.type_declaration", "$/types/0")]
    [Arguments("""{"protocol":"Users","types":[{"type":"array","items":"long"}]}""", "document.type_declaration", "$/types/0")]
    [Arguments("""{"protocol":"Users","types":[{"type":"record"}]}""", "document.type_name", "$/types/0/name")]
    [Arguments("""{"protocol":"Users","types":[{"type":"record","name":"bad-name"}]}""", "document.type_name", "$/types/0/name")]
    [Arguments("""{"protocol":"Users","types":[{"type":"record","name":"User","namespace":1}]}""", "document.namespace", "$/types/0/namespace")]
    [Arguments("""{"protocol":"Users","types":[{"type":"record","name":"User","namespace":"bad-name"}]}""", "document.namespace", "$/types/0/namespace")]
    [Arguments("""{"protocol":"Users","namespace":"com.example","types":[{"type":"record","name":"User","fields":[]},{"type":"error","name":"com.example.User","fields":[]}]}""", "document.duplicate_type", "$/types/1/name")]
    [Arguments("""{"protocol":"com.example.Users","namespace":"ignored","types":[{"type":"record","name":"User","fields":[]},{"type":"fixed","name":"com.example.User","size":1}]}""", "document.duplicate_type", "$/types/1/name")]
    public async Task AvroProtocolRejectsMalformedNamedDeclarationsBeforeMessageSelection(
        string document, string code, string path)
    {
        var result = ResolveAvroProtocol(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo(path);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""[null]""", "document.parameter", "/request/0")]
    [Arguments("""["long"]""", "document.parameter", "/request/0")]
    [Arguments("""[{}]""", "document.parameter_name", "/request/0/name")]
    [Arguments("""[{"name":true,"type":"long"}]""", "document.parameter_name", "/request/0/name")]
    [Arguments("""[{"name":"","type":"long"}]""", "document.parameter_name", "/request/0/name")]
    [Arguments("""[{"name":"bad-name","type":"long"}]""", "document.parameter_name", "/request/0/name")]
    [Arguments("""[{"name":"x.id","type":"long"}]""", "document.parameter_name", "/request/0/name")]
    [Arguments("""[{"name":"id"}]""", "document.parameter_type", "/request/0/type")]
    [Arguments("""[{"name":"id","type":true}]""", "document.parameter_type", "/request/0/type")]
    [Arguments("""[{"name":"id","type":null}]""", "document.parameter_type", "/request/0/type")]
    [Arguments("""[{"name":"id","type":"long"},{"name":"id","type":"string"}]""", "document.duplicate_parameter", "/request/1/name")]
    [Arguments("""[{"name":"id","type":"long","doc":false}]""", "document.doc", "/request/0/doc")]
    [Arguments("""[{"name":"id","type":"long","order":1}]""", "document.parameter_order", "/request/0/order")]
    [Arguments("""[{"name":"id","type":"long","order":"Ascending"}]""", "document.parameter_order", "/request/0/order")]
    [Arguments("""[{"name":"id","type":"long","aliases":"previous"}]""", "document.parameter_aliases", "/request/0/aliases")]
    [Arguments("""[{"name":"id","type":"long","aliases":["previous",null]}]""", "document.parameter_aliases", "/request/0/aliases")]
    public async Task AvroRequestsRequireUniqueNamedTypedParameterFields(string request, string code, string path)
    {
        var document = """{"protocol":"Users","messages":{"GetUser":{"request":REQUEST,"response":"null"}}}"""
            .Replace("REQUEST", request, StringComparison.Ordinal);
        var result = ResolveAvroProtocol(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/messages/GetUser" + path);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("ascending")]
    [Arguments("descending")]
    [Arguments("ignore")]
    public async Task AvroRequestSelectionPreservesValidNativeFieldMetadata(string order)
    {
        var document = """
            {"protocol":"com.example.Users","namespace":"ignored-when-qualified","doc":"Protocol",
             "types":[{"type":"record","name":"a.User","namespace":"ignored-when-qualified","fields":[]},
                      {"type":"record","name":"b.User","fields":[]},
                      {"type":"error","name":"Denied","fields":[]},
                      {"type":"enum","name":"Status","symbols":["Ok"]},
                      {"type":"fixed","name":"Token","size":16}],
             "messages":{"GetUser":{"doc":"Message","request":[
               {"name":"id","type":"long","doc":"Field","default":0,"aliases":["old-name"],"order":"ORDER"},
               {"name":"Id","type":["null","string"],"default":null},
               {"name":"_value","type":{"type":"array","items":"a.User"}}
             ],"response":"null"}}}
            """.Replace("ORDER", order, StringComparison.Ordinal);
        var result = ResolveAvroProtocol(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("request").GetArrayLength()).IsEqualTo(3);
        await Assert.That(result.Operation.GetProperty("request")[0].GetProperty("order").GetString()).IsEqualTo(order);
        await Assert.That(result.Operation.GetProperty("request")[0].GetProperty("aliases")[0].GetString()).IsEqualTo("old-name");
        await Assert.That(result.Operation.GetProperty("request")[0].GetProperty("default").GetInt32()).IsEqualTo(0);
        await Assert.That(result.Operation.GetProperty("request")[1].GetProperty("default").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(result.Operation.GetProperty("doc").GetString()).IsEqualTo("Message");
    }

    [Test]
    [Arguments("""{"doc":false,"request":[],"response":"null"}""", "document.doc", "$/messages/Other/doc")]
    [Arguments("""{"request":[{"name":"id"}],"response":"null"}""", "document.parameter_type", "$/messages/Other/request/0/type")]
    public async Task AvroDeclarationChecksCannotBeBypassedByAnEarlierMatchingMessage(
        string other, string code, string path)
    {
        var document = """{"protocol":"Users","messages":{"GetUser":{"request":[],"response":"null"},"Other":OTHER}}"""
            .Replace("OTHER", other, StringComparison.Ordinal);
        var result = ResolveAvroProtocol(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo(path);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"request":[{"name":"id","type":"Missing"}],"response":"null"}""", "avro.unknown_name", "/request/0/type")]
    [Arguments("""{"request":[],"response":"Missing"}""", "avro.unknown_name", "/response")]
    [Arguments("""{"request":[],"response":{}}""", "schema.required", "/response/type")]
    [Arguments("""{"request":[],"response":[]}""", "avro.union", "/response")]
    [Arguments("""{"request":[],"response":["null","null"]}""", "avro.union", "/response")]
    [Arguments("""{"request":[],"response":[["null"]]}""", "avro.union", "/response")]
    [Arguments("""{"request":[],"response":{"type":"array"}}""", "schema.required", "/response/items")]
    [Arguments("""{"request":[],"response":{"type":"map","values":"Missing"}}""", "avro.unknown_name", "/response/values")]
    [Arguments("""{"request":[],"response":{"type":"record","name":"R","fields":[{"name":"x","type":"long"},{"name":"x","type":"long"}]}}""", "avro.duplicate_field", "/response/fields/1")]
    [Arguments("""{"request":[],"response":{"type":"enum","name":"State","symbols":["Ok","Ok"]}}""", "schema.duplicate", "/response/symbols")]
    [Arguments("""{"request":[],"response":{"type":"enum","name":"State","symbols":["Ok"],"default":"Missing"}}""", "avro.default", "/response/default")]
    [Arguments("""{"request":[],"response":{"type":"fixed","name":"Token","size":-1}}""", "avro.fixed_size", "/response/size")]
    [Arguments("""{"request":[],"response":"null","errors":["string"]}""", "avro.error", "/errors/0")]
    [Arguments("""{"request":[],"response":"null","errors":[{"type":"record","name":"NotAnError","fields":[]}]}""", "avro.error", "/errors/0")]
    [Arguments("""{"request":[],"response":"null","errors":["Missing"]}""", "avro.unknown_name", "/errors/0")]
    [Arguments("""{"request":[],"response":"null","errors":[["string"]]}""", "avro.error", "/errors/0")]
    public async Task AvroProtocolBindsAndValidatesEveryMessageSchemaBeforeSelection(
        string message, string code, string suffix)
    {
        var result = ResolveAvroProtocol(
            """{"protocol":"Users","messages":{"GetUser":MESSAGE}}""".Replace("MESSAGE", message, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/messages/GetUser" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""[{"type":"record","name":"A","fields":[{"name":"next","type":"B"}]},{"type":"record","name":"B","fields":[]}]""",
        "avro.unknown_name", "$/types/0/fields/0/type")]
    [Arguments("""[{"type":"record","name":"R"}]""", "schema.required", "$/types/0/fields")]
    [Arguments("""[{"type":"error","name":"E","fields":[{"name":"id","type":"Missing"}]}]""", "avro.unknown_name", "$/types/0/fields/0/type")]
    [Arguments("""[{"type":"enum","name":"State","symbols":["bad-name"]}]""", "avro.symbol", "$/types/0/symbols")]
    [Arguments("""[{"type":"fixed","name":"Token","size":"16"}]""", "avro.fixed_size", "$/types/0/size")]
    public async Task AvroProtocolDoesNotSkipUnusedInvalidTypeDefinitions(string types, string code, string path)
    {
        var document = """{"protocol":"Users","types":TYPES,"messages":{"GetUser":{"request":[],"response":"null"}}}"""
            .Replace("TYPES", types, StringComparison.Ordinal);
        var result = ResolveAvroProtocol(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AvroProtocolResolvesRecursiveNamedTypesInNativeDeclarationOrder(bool qualified)
    {
        var ns = qualified ? "actual" : "com.example";
        var document = """
            {"protocol":"PROTOCOL","namespace":"com.example",
             "types":[{"type":"record","name":"User","fields":[{"name":"next","type":["null","User"]}]},
                      {"type":"enum","name":"State","symbols":["Ok"],"default":"Ok"},
                      {"type":"fixed","name":"Token","size":16},
                      {"type":"error","name":"Denied","fields":[{"name":"user","type":"User"}]}],
             "messages":{"GetUser":{
               "request":[{"name":"user","type":"User"},{"name":"token","type":"NAMESPACE.Token"}],
               "response":{"type":"array","items":{"type":"map","values":"User"}},
               "errors":["NAMESPACE.Denied"],"custom":{"$ref":"not-an-import"}}}}
            """.Replace("PROTOCOL", qualified ? "actual.Users" : "Users", StringComparison.Ordinal)
            .Replace("NAMESPACE", ns, StringComparison.Ordinal);
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("AvroProtocol/1.12.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/GetUser"),
            resolveReference: (_, _) => { calls++; throw new InvalidOperationException("Avro schemas have no document imports."); });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("response").GetProperty("items").GetProperty("values").GetString()).IsEqualTo("User");
        await Assert.That(result.Operation.GetProperty("errors")[0].GetString()).IsEqualTo(ns + ".Denied");
        await Assert.That(result.Operation.GetProperty("request")[1].GetProperty("type").GetString()).IsEqualTo(ns + ".Token");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task AvroProtocolSchemaBindingIncludesLaterMessagesAndAnonymousRequestDeclarations()
    {
        var result = ResolveAvroProtocol("""
            {"protocol":"Users","messages":{
              "GetUser":{"request":[{"name":"user","type":{"type":"record","name":"User","fields":[]}}],
                         "response":"User"},
              "Other":{"request":[{"name":"user","type":"User"}],"response":"Missing"}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("avro.unknown_name");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/messages/Other/response");
        var valid = ResolveAvroProtocol("""
            {"protocol":"Users","messages":{
              "GetUser":{"request":[{"name":"user","type":{"type":"record","name":"User","fields":[]}}],
                         "response":"User"},
              "Other":{"request":[{"name":"user","type":"User"}],"response":"User"}}}
            """);
        await Assert.That(valid.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(valid.Operation.GetProperty("response").GetString()).IsEqualTo("User");
    }

    [Test]
    [Arguments("""["Denied","Denied"]""", "avro.union", "$/messages/GetUser/errors/1")]
    [Arguments("""["User"]""", "avro.error", "$/messages/GetUser/errors/0")]
    [Arguments("""["denied"]""", "avro.unknown_name", "$/messages/GetUser/errors/0")]
    public async Task AvroProtocolDeclaredErrorsRequireUniqueExactErrorTypeIdentities(
        string errors, string code, string path)
    {
        var result = ResolveAvroProtocol("""
            {"protocol":"Users","types":[{"type":"record","name":"User","fields":[]},
              {"type":"error","name":"Denied","fields":[]}],
             "messages":{"GetUser":{"request":[],"response":"null","errors":ERRORS}}}
            """.Replace("ERRORS", errors, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo(path);
    }

    [Test]
    public async Task AvroProtocolLogicalTypePolicyIsExplicitlyUnsupportedRatherThanInvalidOrSelected()
    {
        var result = ResolveAvroProtocol("""
            {"protocol":"Users","messages":{"GetUser":{"request":[],
              "response":{"type":"bytes","logicalType":"decimal","precision":10,"scale":2}}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("avro.logical_type");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/messages/GetUser/response/logicalType");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"name":"x","type":["null","string"],"default":"later-branch"}""")]
    [Arguments("""{"name":"x","type":["string","null"],"default":null}""")]
    [Arguments("""{"name":"x","type":["int","long"],"default":2147483648}""")]
    [Arguments("""{"name":"x","type":["string",{"type":"array","items":"long"}],"default":[1,2]}""")]
    [Arguments("""{"name":"x","type":"int","default":-2147483648}""")]
    [Arguments("""{"name":"x","type":"int","default":2147483647}""")]
    [Arguments("""{"name":"x","type":"long","default":-9223372036854775808}""")]
    [Arguments("""{"name":"x","type":"long","default":9223372036854775807}""")]
    [Arguments("""{"name":"x","type":"bytes","default":"\u0000\u00ff"}""")]
    [Arguments("""{"name":"x","type":{"type":"fixed","name":"F","size":2},"default":"ab"}""")]
    [Arguments("""{"name":"x","type":{"type":"enum","name":"E","symbols":["Ok"]},"default":"Ok"}""")]
    [Arguments("""{"name":"x","type":{"type":"map","values":"boolean"},"default":{"a":true}}""")]
    [Arguments("""{"name":"x","type":{"type":"record","name":"R","fields":[{"name":"id","type":"long","default":1}]},"default":{}}""")]
    [Arguments("""{"name":"x","type":{"type":"record","name":"Node","fields":[{"name":"next","type":["null","Node"],"default":null}]},"default":{"next":null}}""")]
    public async Task AvroProtocolChecksDefaultsUsingTheFirstMatchingAvro112UnionBranch(string field)
    {
        var result = ResolveAvroProtocol(
            """{"protocol":"Users","messages":{"GetUser":{"request":[FIELD],"response":"null"}}}"""
            .Replace("FIELD", field, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        using var expected = JsonDocument.Parse(field);
        await Assert.That(result.Operation.GetProperty("request")[0].GetRawText()).IsEqualTo(expected.RootElement.GetRawText());
    }

    [Test]
    [Arguments("""{"name":"x","type":["null","string"],"default":true}""")]
    [Arguments("""{"name":"x","type":["int","long"],"default":9223372036854775808}""")]
    [Arguments("""{"name":"x","type":"int","default":2147483648}""")]
    [Arguments("""{"name":"x","type":"int","default":-2147483649}""")]
    [Arguments("""{"name":"x","type":"long","default":-9223372036854775809}""")]
    [Arguments("""{"name":"x","type":"int","default":1.5}""")]
    [Arguments("""{"name":"x","type":"bytes","default":"\u0100"}""")]
    [Arguments("""{"name":"x","type":{"type":"fixed","name":"F","size":2},"default":"a"}""")]
    [Arguments("""{"name":"x","type":{"type":"fixed","name":"F","size":2},"default":"abc"}""")]
    [Arguments("""{"name":"x","type":{"type":"enum","name":"E","symbols":["Ok"]},"default":"Missing"}""")]
    [Arguments("""{"name":"x","type":{"type":"array","items":"long"},"default":[1,"bad"]}""")]
    [Arguments("""{"name":"x","type":{"type":"map","values":"boolean"},"default":{"a":"bad"}}""")]
    [Arguments("""{"name":"x","type":{"type":"record","name":"R","fields":[{"name":"id","type":"long"}]},"default":{}}""")]
    public async Task AvroProtocolRejectsDefaultsThatDoNotConformToTheirBoundSchemas(string field)
    {
        var result = ResolveAvroProtocol(
            """{"protocol":"Users","messages":{"GetUser":{"request":[FIELD],"response":"null"}}}"""
            .Replace("FIELD", field, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("avro.default");
        await Assert.That(result.Diagnostic.Path.StartsWith("$/messages/GetUser/request/0/default", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AvroProtocolChecksDefaultsEvenWithoutASelectedMessage(bool hasMessage)
    {
        var document = """
            {"protocol":"Users","types":[{"type":"record","name":"R","fields":[
              {"name":"id","type":"long","default":"invalid"}]}],"messages":MESSAGES}
            """.Replace("MESSAGES", hasMessage ? """{"Other":{"request":[],"response":"null"}}""" : "{}", StringComparison.Ordinal);
        var result = ResolveAvroProtocol(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("avro.default");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/types/0/fields/0/default");
    }

    [Test]
    public async Task RecursiveAvroProtocolDefaultExpansionIsBoundedInsteadOfOverflowingTheStack()
    {
        var result = ResolveAvroProtocol("""
            {"protocol":"Users","types":[{"type":"record","name":"R","fields":[
              {"name":"self","type":"R","default":{}}]}],
              "messages":{"GetUser":{"request":[],"response":"R"}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.depth");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"type":"record","name":"Users","fields":[]}""", "document.protocol", "$/protocol")]
    [Arguments("""{"protocol":null}""", "document.protocol", "$/protocol")]
    [Arguments("""{"protocol":""}""", "document.protocol", "$/protocol")]
    [Arguments("""{"protocol":"Users","namespace":1}""", "document.namespace", "$/namespace")]
    [Arguments("""{"protocol":"Users","messages":[]}""", "document.messages", "$/messages")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":null}}""", "document.message", "$/messages/GetUser")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":{"response":"null"}}}""", "document.request", "$/messages/GetUser/request")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":{"request":{},"response":"null"}}}""", "document.request", "$/messages/GetUser/request")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":{"request":[]}}}""", "document.response", "$/messages/GetUser/response")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":{"request":[],"response":null}}}""", "document.response", "$/messages/GetUser/response")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":{"request":[],"response":"null","errors":{}}}}""", "document.errors", "$/messages/GetUser/errors")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":{"request":[],"response":"null","one-way":"true"}}}""", "document.one_way", "$/messages/GetUser/one-way")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":{"request":[],"response":"string","one-way":true}}}""", "document.one_way", "$/messages/GetUser/one-way")]
    [Arguments("""{"protocol":"Users","messages":{"GetUser":{"request":[],"response":"null","errors":["Denied"],"one-way":true}}}""", "document.one_way", "$/messages/GetUser/one-way")]
    public async Task AvroProtocolSelectionRejectsInvalidProtocolAndMessageShapes(string document, string code, string path)
    {
        var result = ResolveAvroProtocol(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo(path);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    public async Task AvroProtocolDoesNotHideMalformedMessagesAfterAnEarlyMatch()
    {
        var result = ResolveAvroProtocol("""
            {"protocol":"Users","messages":{
              "GetUser":{"request":[],"response":"string"},
              "Other":false}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.message");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/messages/Other");
    }

    [Test]
    [Arguments("""{"request":[],"response":"null","one-way":true}""")]
    [Arguments("""{"request":[],"response":{"type":"null"},"errors":[],"one-way":true}""")]
    [Arguments("""{"request":[],"response":["null","string"]}""")]
    [Arguments("""{"request":[],"response":{"type":"record","name":"User","fields":[]}}""")]
    public async Task AvroProtocolReturnsOwnedNativeResponseAndOneWayShapes(string message)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            """{"protocol":"Users","messages":{"GetUser":""" + message + "}}");
        var result = ApiOperationDocumentResolver.Resolve("AvroProtocol/1.12.0", "application/json", bytes,
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser"));
        Array.Fill(bytes, (byte)' ');

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(message);
    }

    [Test]
    [Arguments("/messages/GetUser")]
    [Arguments("services/Users/operations/GetUser")]
    [Arguments("operations/query/GetUser")]
    public async Task AvroProtocolOnlyAcceptsItsNativeMessageNameLocator(string selector)
    {
        var result = ApiOperationDocumentResolver.Resolve("AvroProtocol/1.12.0", "application/json",
            """{"protocol":"Users","messages":{}}"""u8.ToArray(),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#" + selector));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selector.unsupported");
    }

    [Test]
    public async Task AvroProtocolMessageNamesArePercentDecodedExactlyOnce()
    {
        const string document = """{"protocol":"Users","messages":{"GetUser":{"request":[],"response":"null"}}}""";
        var result = ResolveAvroProtocol(document, "Get%55ser");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/messages/GetUser");
        var doubleEncoded = ResolveAvroProtocol(document, "%2547etUser");
        await Assert.That(doubleEncoded.Status).IsEqualTo(ApiOperationResolutionStatus.NotFound);
        await Assert.That(doubleEncoded.Diagnostic!.Code).IsEqualTo("selection.not_found");
    }

    [Test]
    public async Task AvroProtocolDuplicateMessagesFailInsteadOfSelectingTheLastDeclaration()
    {
        var result = ResolveAvroProtocol("""
            {"protocol":"Users","messages":{
              "GetUser":{"request":[],"response":"string"},
              "GetUser":{"request":[],"response":"null"}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.json");
    }

    [Test]
    public async Task AvroProtocolUsesTheSharedByteLimitAndCallerCancellation()
    {
        const string document = """{"protocol":"Users","messages":{"GetUser":{"request":[],"response":"null"}}}""";
        var padded = document.PadRight(ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes);
        var boundary = ResolveAvroProtocol(padded);
        await Assert.That(boundary.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var result = ResolveAvroProtocol(padded + " ");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.bytes");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            ApiOperationDocumentResolver.Resolve("AvroProtocol/1.12.0", "application/json",
                """{"protocol":"Users"}"""u8.ToArray(),
                ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser"), cancellation.Token));
    }

    [Test]
    [Arguments("AvroProtocol/1.12.1", "application/json", "format.unsupported")]
    [Arguments("AvroProtocol/1.11.0", "application/json", "format.unsupported")]
    [Arguments("avroprotocol/1.12.0", "application/json", "format.unsupported")]
    [Arguments("AvroIDL/1.12.0", "application/json", "format.unsupported")]
    [Arguments("AvroProtocol/1.12.0", "text/plain", "contenttype.unsupported")]
    public async Task AvroProtocolDoesNotAdvertiseOtherReleasesOrIdlParsing(string format, string mediaType, string code)
    {
        var result = ApiOperationDocumentResolver.Resolve(format, mediaType,
            """{"protocol":"Users","messages":{}}"""u8.ToArray(),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/GetUser"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    private static ApiOperationResolutionResult ResolveAvroProtocol(string document, string message = "GetUser") =>
        ApiOperationDocumentResolver.Resolve("AvroProtocol/1.12.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/" + message));

    [Test]
    [Arguments("2")]
    [Arguments("2.0")]
    public async Task SmithyJsonSelectsTheExactOwnedOperationWithItsNativeReferencesAndTraits(string version)
    {
        var document = """
            {"smithy":"VERSION","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"},
                "output":{"target":"smithy.api#Unit"},"errors":[{"target":"example#Failure"}],
                "traits":{"smithy.api#readonly":{},"example#Custom":{"note":"native"}}},
              "example#Input":{"type":"structure","members":{
                "name":{"target":"smithy.api#String","traits":{"smithy.api#required":{}}},
                "next":{"target":"example#Input"}}},
              "example#Failure":{"type":"structure","traits":{"smithy.api#error":"client"}},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{}},
                "members":{"note":{"target":"smithy.api#String"}}}}}
            """.Replace("VERSION", version, StringComparison.Ordinal);
        var bytes = System.Text.Encoding.UTF8.GetBytes(document);
        var calls = 0;
        var source = new Uri("https://registry.example/apis/model");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            bytes, ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"), source,
            (_, _) => { calls++; throw new InvalidOperationException("Shape IDs are not document URLs."); });
        Array.Fill(bytes, (byte)' ');
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/shapes/example#Get");
        await Assert.That(result.DocumentUri).IsEqualTo(source);
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
        await Assert.That(result.Operation.GetProperty("errors")[0].GetProperty("target").GetString()).IsEqualTo("example#Failure");
        await Assert.That(result.Operation.GetProperty("traits").GetProperty("example#Custom").GetProperty("note").GetString())
            .IsEqualTo("native");
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}""", "example%23get", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"},"example#Data":{"type":"structure"}}}""", "example%23Data", ApiOperationResolutionStatus.Invalid, "selection.not_operation")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"},"example#Service":{"type":"service"}}}""", "example%23Service", ApiOperationResolutionStatus.Invalid, "selection.not_operation")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"},"example#Resource":{"type":"resource"}}}""", "example%23Resource", ApiOperationResolutionStatus.Invalid, "selection.not_operation")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Data":{"type":"structure"}}}""", "example%23Data", ApiOperationResolutionStatus.Invalid, "document.operations")]
    [Arguments("""{"smithy":"1.0","shapes":{}}""", "example%23Get", ApiOperationResolutionStatus.Invalid, "document.smithy_version")]
    [Arguments("""{"smithy":2,"shapes":{}}""", "example%23Get", ApiOperationResolutionStatus.Invalid, "document.smithy_version")]
    [Arguments("""{"smithy":"2.0","shapes":[]}""", "example%23Get", ApiOperationResolutionStatus.Invalid, "document.shapes")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"},"Example#GET":{"type":"operation"}}}""", "example%23Get", ApiOperationResolutionStatus.Invalid, "document.shape_id")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"},"Bad":{"type":"string"}}}""", "example%23Get", ApiOperationResolutionStatus.Invalid, "document.shape_id")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"},"example#Bad":null}}""", "example%23Get", ApiOperationResolutionStatus.Invalid, "document.shape")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}""", "Get", ApiOperationResolutionStatus.Invalid, "selector.shape_id")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}""", "example%23Get$member", ApiOperationResolutionStatus.Invalid, "selector.shape_id")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}""", "example%2523Get", ApiOperationResolutionStatus.Invalid, "selector.shape_id")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}""", "2example%23Get", ApiOperationResolutionStatus.Invalid, "selector.shape_id")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}""", "example%23_", ApiOperationResolutionStatus.Invalid, "selector.shape_id")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}""", "example%23G%C3%A9t", ApiOperationResolutionStatus.Invalid, "selector.shape_id")]
    [Arguments("""{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"},"example#Get":{"type":"operation"}}}""", "example%23Get", ApiOperationResolutionStatus.Invalid, "document.json")]
    public async Task SmithyJsonRejectsInvalidRootsAndNonOperationOrNonAbsoluteSelectors(
        string document, string selector, ApiOperationResolutionStatus status, string code)
    {
        var result = ResolveSmithy(document, selector);
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("operation", "documentation", "null")]
    [Arguments("operation", "documentation", "0")]
    [Arguments("operation", "documentation", "false")]
    [Arguments("operation", "documentation", "[]")]
    [Arguments("operation", "documentation", "{}")]
    [Arguments("operation", "readonly", "true")]
    [Arguments("operation", "readonly", "null")]
    [Arguments("operation", "readonly", "[]")]
    [Arguments("operation", "idempotent", "false")]
    [Arguments("operation", "idempotent", "\"marker\"")]
    [Arguments("structure", "input", "true")]
    [Arguments("structure", "input", "[]")]
    [Arguments("structure", "output", "null")]
    [Arguments("member", "required", "true")]
    [Arguments("member", "required", "null")]
    [Arguments("member", "required", "[]")]
    [Arguments("member", "required", "1")]
    [Arguments("member", "required", "\"required\"")]
    [Arguments("member", "clientOptional", "null")]
    [Arguments("member", "clientOptional", "false")]
    [Arguments("member", "clientOptional", "[]")]
    [Arguments("operation", "private", "null")]
    [Arguments("structure", "private", "false")]
    [Arguments("member", "private", "[]")]
    [Arguments("definition", "private", "\"private\"")]
    [Arguments("member", "documentation", "{}")]
    [Arguments("payload", "error", "\"Client\"")]
    [Arguments("payload", "error", "\"service\"")]
    [Arguments("payload", "error", "null")]
    [Arguments("payload", "error", "{}")]
    [Arguments("definition", "trait", "true")]
    [Arguments("definition", "trait", "null")]
    [Arguments("definition", "trait", "[]")]
    public async Task SmithyJsonRejectsMalformedPreludeTraitValuesInTheSelectedClosure(
        string position, string name, string value)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"},
                "traits":{"example#Custom":{}}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Payload"}}},
              "example#Payload":{"type":"structure"},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{}}}}}
            """)!.AsObject();
        var shapes = document["shapes"]!.AsObject();
        var item = position switch
        {
            "operation" => shapes["example#Get"]!.AsObject(),
            "structure" => shapes["example#Input"]!.AsObject(),
            "payload" => shapes["example#Payload"]!.AsObject(),
            "definition" => shapes["example#Custom"]!.AsObject(),
            _ => shapes["example#Input"]!["members"]!["value"]!.AsObject()
        };
        var traits = item["traits"] as JsonObject ?? new JsonObject();
        item["traits"] = traits;
        traits["smithy.api#" + name] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        var path = position switch
        {
            "operation" => "$/shapes/example#Get",
            "structure" => "$/shapes/example#Input",
            "payload" => "$/shapes/example#Payload",
            "definition" => "$/shapes/example#Custom",
            _ => "$/shapes/example#Input/members/value"
        };
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo(path + "/traits/smithy.api#" + name);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("client", "readonly")]
    [Arguments("server", "readonly")]
    [Arguments("client", "idempotent")]
    [Arguments("server", "idempotent")]
    public async Task SmithyJsonPreservesValidPreludeTraitValuesWithoutInterpretingPayloadReferences(
        string error, string behavior)
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"},
                "output":{"target":"example#Output"},
                "traits":{"smithy.api#documentation":"","smithy.api#BEHAVIOR":{},
                  "example#Custom":{"$ref":"https://must-not-load.example/data"}}},
              "example#Input":{"type":"structure","traits":{"smithy.api#input":{}},
                "members":{"value":{"target":"example#Payload",
                  "traits":{"smithy.api#required":{},"smithy.api#documentation":"Payload"}}}},
              "example#Output":{"type":"structure","traits":{"smithy.api#output":{}}},
              "example#Payload":{"type":"structure","traits":{"smithy.api#error":"ERROR"}},
              "example#Custom":{"type":"document","traits":{"smithy.api#trait":{}}},
              "example#Unused":{"type":"string","traits":{"smithy.api#documentation":false}}}}
            """.Replace("ERROR", error, StringComparison.Ordinal).Replace("BEHAVIOR", behavior, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Trait payloads are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
        await Assert.That(result.Operation.GetProperty("traits").GetProperty("smithy.api#documentation").GetString()).IsEqualTo("");
        await Assert.That(result.Operation.GetProperty("traits").GetProperty("example#Custom").GetProperty("$ref").GetString())
            .IsEqualTo("https://must-not-load.example/data");
    }

    [Test]
    [Arguments("operation", """{"smithy.api#required":{}}""")]
    [Arguments("operation", """{"smithy.api#input":{}}""")]
    [Arguments("operation", """{"smithy.api#output":{}}""")]
    [Arguments("operation", """{"smithy.api#error":"client"}""")]
    [Arguments("operation", """{"smithy.api#trait":{}}""")]
    [Arguments("structure", """{"smithy.api#required":{}}""")]
    [Arguments("structure", """{"smithy.api#readonly":{}}""")]
    [Arguments("structure", """{"smithy.api#idempotent":{}}""")]
    [Arguments("operation", """{"smithy.api#clientOptional":{}}""")]
    [Arguments("structure", """{"smithy.api#clientOptional":{}}""")]
    [Arguments("listMember", """{"smithy.api#clientOptional":{}}""")]
    [Arguments("mapMember", """{"smithy.api#clientOptional":{}}""")]
    [Arguments("unionMember", """{"smithy.api#clientOptional":{}}""")]
    [Arguments("operation", """{"smithy.api#addedDefault":{}}""")]
    [Arguments("structure", """{"smithy.api#addedDefault":{}}""")]
    [Arguments("member", """{"smithy.api#addedDefault":{}}""")]
    [Arguments("listMember", """{"smithy.api#addedDefault":{}}""")]
    [Arguments("mapMember", """{"smithy.api#addedDefault":{}}""")]
    [Arguments("unionMember", """{"smithy.api#addedDefault":{}}""")]
    [Arguments("operation", """{"smithy.api#sparse":{}}""")]
    [Arguments("structure", """{"smithy.api#sparse":{}}""")]
    [Arguments("member", """{"smithy.api#sparse":{}}""")]
    [Arguments("listMember", """{"smithy.api#sparse":{}}""")]
    [Arguments("mapMember", """{"smithy.api#sparse":{}}""")]
    [Arguments("unionMember", """{"smithy.api#sparse":{}}""")]
    [Arguments("operation", """{"smithy.api#uniqueItems":{}}""")]
    [Arguments("structure", """{"smithy.api#uniqueItems":{}}""")]
    [Arguments("member", """{"smithy.api#uniqueItems":{}}""")]
    [Arguments("listMember", """{"smithy.api#uniqueItems":{}}""")]
    [Arguments("mapMember", """{"smithy.api#uniqueItems":{}}""")]
    [Arguments("unionMember", """{"smithy.api#uniqueItems":{}}""")]
    [Arguments("member", """{"smithy.api#input":{}}""")]
    [Arguments("member", """{"smithy.api#output":{}}""")]
    [Arguments("member", """{"smithy.api#error":"server"}""")]
    [Arguments("member", """{"smithy.api#readonly":{}}""")]
    [Arguments("member", """{"smithy.api#idempotent":{}}""")]
    [Arguments("member", """{"smithy.api#trait":{}}""")]
    [Arguments("listMember", """{"smithy.api#required":{}}""")]
    [Arguments("mapMember", """{"smithy.api#required":{}}""")]
    [Arguments("unionMember", """{"smithy.api#required":{}}""")]
    [Arguments("reference", """{"smithy.api#required":{}}""")]
    [Arguments("reference", """{"smithy.api#input":{}}""")]
    [Arguments("operation", """{"smithy.api#enumValue":"bad"}""")]
    [Arguments("structure", """{"smithy.api#enumValue":"bad"}""")]
    [Arguments("member", """{"smithy.api#enumValue":"bad"}""")]
    [Arguments("listMember", """{"smithy.api#enumValue":"bad"}""")]
    [Arguments("mapMember", """{"smithy.api#enumValue":"bad"}""")]
    [Arguments("unionMember", """{"smithy.api#enumValue":"bad"}""")]
    [Arguments("reference", """{"smithy.api#enumValue":"bad"}""")]
    [Arguments("operation", """{"smithy.api#default":null}""")]
    [Arguments("structure", """{"smithy.api#default":null}""")]
    [Arguments("member", """{"smithy.api#default":null}""")]
    [Arguments("listMember", """{"smithy.api#default":null}""")]
    [Arguments("mapMember", """{"smithy.api#default":null}""")]
    [Arguments("unionMember", """{"smithy.api#default":null}""")]
    [Arguments("reference", """{"smithy.api#default":null}""")]
    public async Task SmithyJsonRejectsPreludeTraitsOutsideTheirExplicitTargets(string position, string traits)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Container"}}},
              "example#Container":{"type":"structure"}}}
            """)!.AsObject();
        var shapes = document["shapes"]!.AsObject();
        JsonObject item;
        var path = "$/shapes/";
        switch (position)
        {
            case "operation":
                item = shapes["example#Get"]!.AsObject();
                path += "example#Get";
                break;
            case "structure":
                item = shapes["example#Input"]!.AsObject();
                path += "example#Input";
                break;
            case "reference":
                item = shapes["example#Get"]!["input"]!.AsObject();
                path += "example#Get/input";
                break;
            case "member":
                item = shapes["example#Input"]!["members"]!["value"]!.AsObject();
                path += "example#Input/members/value";
                break;
            default:
                var kind = position == "listMember" ? "list" : position == "mapMember" ? "map" : "union";
                var container = new JsonObject { ["type"] = kind };
                item = new JsonObject { ["target"] = "smithy.api#String" };
                if (kind == "list") { container["member"] = item; path += "example#Container/member"; }
                else if (kind == "map")
                {
                    container["key"] = new JsonObject { ["target"] = "smithy.api#String" };
                    container["value"] = item;
                    path += "example#Container/value";
                }
                else
                {
                    container["members"] = new JsonObject { ["value"] = item };
                    path += "example#Container/members/value";
                }
                shapes["example#Container"] = container;
                break;
        }
        item["traits"] = JsonNode.Parse(traits);
        var traitName = item["traits"]!.AsObject().First().Key;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        var operationTrait = position == "operation" && traitName == "smithy.api#trait";
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(position == "reference" ? "document.reference_property"
            : operationTrait ? "document.target_type" : "document.trait_target");
        await Assert.That(result.Diagnostic.Path).IsEqualTo(position == "reference" ? path + "/traits"
            : operationTrait ? path : path + "/traits/" + traitName);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("operation", """{"smithy.api#readonly":{},"smithy.api#idempotent":{}}""")]
    [Arguments("operation", """{"smithy.api#idempotent":{},"smithy.api#readonly":{}}""")]
    [Arguments("structure", """{"smithy.api#input":{},"smithy.api#output":{}}""")]
    [Arguments("structure", """{"smithy.api#output":{},"smithy.api#input":{}}""")]
    [Arguments("structure", """{"smithy.api#input":{},"smithy.api#error":"client"}""")]
    [Arguments("structure", """{"smithy.api#error":"client","smithy.api#input":{}}""")]
    [Arguments("structure", """{"smithy.api#output":{},"smithy.api#error":"server"}""")]
    [Arguments("structure", """{"smithy.api#error":"server","smithy.api#output":{}}""")]
    [Arguments("definition", """{"smithy.api#trait":{},"smithy.api#error":"client"}""")]
    [Arguments("definition", """{"smithy.api#error":"client","smithy.api#trait":{}}""")]
    public async Task SmithyJsonRejectsDeclaredPreludeConflictsInEitherPropertyOrder(string position, string traits)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"},"traits":{"example#Custom":{}}},
              "example#Input":{"type":"structure"},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{}}}}}
            """)!.AsObject();
        var id = position == "operation" ? "example#Get" : position == "structure" ? "example#Input" : "example#Custom";
        var item = document["shapes"]![id]!.AsObject();
        item["traits"] = JsonNode.Parse(traits);
        if (position == "operation") { item["traits"]!["example#Custom"] = new JsonObject(); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path!.StartsWith("$/shapes/" + id + "/traits/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string", "\"\"", true)]
    [Arguments("string", "1", false)]
    [Arguments("boolean", "true", true)]
    [Arguments("boolean", "false", true)]
    [Arguments("boolean", "\"false\"", false)]
    [Arguments("byte", "-128", true)]
    [Arguments("byte", "127", true)]
    [Arguments("byte", "-129", false)]
    [Arguments("byte", "128", false)]
    [Arguments("short", "-32768", true)]
    [Arguments("short", "32767", true)]
    [Arguments("short", "-32769", false)]
    [Arguments("short", "32768", false)]
    [Arguments("integer", "-2147483648", true)]
    [Arguments("integer", "2147483647", true)]
    [Arguments("integer", "-2147483649", false)]
    [Arguments("integer", "2147483648", false)]
    [Arguments("long", "-9223372036854775808", true)]
    [Arguments("long", "9223372036854775807", true)]
    [Arguments("long", "-9223372036854775809", false)]
    [Arguments("long", "9223372036854775808", false)]
    [Arguments("integer", "1.0", true)]
    [Arguments("integer", "1e0", true)]
    [Arguments("integer", "1.000000000000000000000000001", false)]
    [Arguments("integer", "1.5", false)]
    [Arguments("bigInteger", "1e1000", true)]
    [Arguments("bigInteger", "1e-1000", false)]
    [Arguments("bigDecimal", "1e1000", true)]
    [Arguments("bigDecimal", "1e-1000", true)]
    [Arguments("float", "1.5", true)]
    [Arguments("float", "3e38", true)]
    [Arguments("float", "4e38", false)]
    [Arguments("double", "1e308", true)]
    [Arguments("double", "1e309", false)]
    [Arguments("double", "\"1\"", false)]
    [Arguments("list", "[]", true)]
    [Arguments("list", "[null]", false)]
    [Arguments("list", "{}", false)]
    [Arguments("map", "{}", true)]
    [Arguments("map", "{\"key\":null}", false)]
    [Arguments("map", "[]", false)]
    [Arguments("document", "true", true)]
    [Arguments("document", "false", true)]
    [Arguments("document", "\"literal\"", true)]
    [Arguments("document", "1", true)]
    [Arguments("document", "[]", true)]
    [Arguments("document", "{}", true)]
    [Arguments("document", "[1]", false)]
    [Arguments("document", "{\"$ref\":\"https://must-not-load.example/data\"}", false)]
    [Arguments("blob", "\"\"", true)]
    [Arguments("blob", "\"YQ==\"", true)]
    [Arguments("blob", "\"not-base64!\"", false)]
    [Arguments("blob", "[]", false)]
    [Arguments("enum", "\"A\"", true)]
    [Arguments("enum", "\"authored\"", true)]
    [Arguments("enum", "\"B\"", false)]
    [Arguments("enum", "\"AUTHORED\"", false)]
    [Arguments("enum", "1", false)]
    [Arguments("intEnum", "1", true)]
    [Arguments("intEnum", "1.0", true)]
    [Arguments("intEnum", "2", false)]
    [Arguments("intEnum", "\"1\"", false)]
    public async Task SmithyJsonDefaultsCheckNativeValuesWithoutApplyingThem(string kind, string value, bool valid)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            var document = SmithyDefaultDocument(kind, value, rootDefault);
            var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
                System.Text.Encoding.UTF8.GetBytes(document),
                ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
                resolveReference: (_, _) => throw new InvalidOperationException("Defaults are not imports."));
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid)
            {
                await Assert.That(result.Operation.GetRawText())
                    .IsEqualTo("""{"type":"operation","input":{"target":"example#Input"}}""");
            }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_value");
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments("string")]
    [Arguments("document")]
    [Arguments("integer")]
    [Arguments("list")]
    [Arguments("map")]
    public async Task SmithyJsonNullDefaultsAreExplicitMemberOverridesNotRootValues(string kind)
    {
        var member = ResolveSmithy(SmithyDefaultDocument(kind, "null", false));
        var root = ResolveSmithy(SmithyDefaultDocument(kind, "null", true));
        await Assert.That(member.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(root.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(root.Diagnostic!.Code).IsEqualTo("document.default_value");
        await Assert.That(root.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure", "{}")]
    [Arguments("structure", "null")]
    [Arguments("union", "{}")]
    [Arguments("union", "null")]
    public async Task SmithyJsonDefaultsCannotTargetAggregateStructuresOrUnions(string kind, string value)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            var result = ResolveSmithy(SmithyDefaultDocument(kind, value, rootDefault));
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("1", true, ApiOperationResolutionStatus.Selected)]
    [Arguments("1.0", true, ApiOperationResolutionStatus.Selected)]
    [Arguments("1e0", true, ApiOperationResolutionStatus.Selected)]
    [Arguments("null", true, ApiOperationResolutionStatus.Selected)]
    [Arguments("2", true, ApiOperationResolutionStatus.Invalid)]
    [Arguments("1", false, ApiOperationResolutionStatus.Invalid)]
    public async Task SmithyJsonStructureMembersMustRepeatOrNullOverrideTargetDefaults(
        string value, bool present, ApiOperationResolutionStatus status)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("integer", "1", true))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        if (present) { member["traits"]!["smithy.api#default"] = JsonNode.Parse(value); }
        else { member.AsObject().Remove("traits"); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == ApiOperationResolutionStatus.Invalid)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_repeat");
            await Assert.That(result.Diagnostic.Path)
                .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
        else { await Assert.That(result.Diagnostic).IsNull(); }
    }

    [Test]
    [Arguments("list")]
    [Arguments("map")]
    public async Task SmithyJsonContainerMembersDoNotInheritRootDefaultRepetition(string kind)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Container"}}},
              "example#Container":CONTAINER,
              "example#Value":{"type":"string","traits":{"smithy.api#default":"default"}}}}
            """.Replace("CONTAINER", kind == "list"
                ? """{"type":"list","member":{"target":"example#Value"}}"""
                : """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Value"}}""",
                StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonTimestampDefaultsRemainExplicitlyUnsupported()
    {
        var result = ResolveSmithy(SmithyDefaultDocument("timestamp", "\"2026-01-01T00:00:00.0001Z\"", false));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.timestamp_default");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("bigInteger", "\"+123\"")]
    [Arguments("bigDecimal", "\"NaN\"")]
    public async Task SmithyJsonStringEncodedNumericDefaultsRemainExplicitlyUnsupported(string kind, string value)
    {
        var result = ResolveSmithy(SmithyDefaultDocument(kind, value, false));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.numeric_string_default");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("float", "NaN", true)]
    [Arguments("float", "Infinity", true)]
    [Arguments("float", "-Infinity", true)]
    [Arguments("double", "NaN", true)]
    [Arguments("double", "Infinity", true)]
    [Arguments("double", "-Infinity", true)]
    [Arguments("float", "nan", false)]
    [Arguments("double", "infinity", false)]
    [Arguments("float", "+Infinity", false)]
    [Arguments("double", " Infinity", false)]
    [Arguments("float", "1.5", false)]
    [Arguments("double", "", false)]
    public async Task SmithyJsonFloatingSpecialDefaultsUseExactNativeStrings(string kind, string value, bool valid)
    {
        var literal = JsonSerializer.Serialize(value);
        foreach (var rootDefault in new[] { false, true })
        {
            var result = ResolveSmithy(SmithyDefaultDocument(kind, literal, rootDefault));
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid)
            {
                await Assert.That(result.Operation.GetRawText())
                    .IsEqualTo("""{"type":"operation","input":{"target":"example#Input"}}""");
            }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_value");
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments("String", "\"value\"", ApiOperationResolutionStatus.Selected)]
    [Arguments("Integer", "2147483648", ApiOperationResolutionStatus.Invalid)]
    [Arguments("Boolean", "1", ApiOperationResolutionStatus.Invalid)]
    [Arguments("Document", "{}", ApiOperationResolutionStatus.Selected)]
    [Arguments("Unit", "null", ApiOperationResolutionStatus.Invalid)]
    public async Task SmithyJsonMemberDefaultsCheckImplicitPreludeTargetKinds(
        string target, string value, ApiOperationResolutionStatus status)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#TARGET",
                "traits":{"smithy.api#default":VALUE}}}}}}
            """.Replace("TARGET", target, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == ApiOperationResolutionStatus.Invalid)
        {
            await Assert.That(result.Diagnostic!.Code)
                .IsEqualTo(target == "Unit" ? "document.trait_target" : "document.default_value");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
        else { await Assert.That(result.Diagnostic).IsNull(); }
    }

    [Test]
    [Arguments("input", "traits", "{}")]
    [Arguments("output", "traits", "{}")]
    [Arguments("errors", "traits", "{}")]
    [Arguments("input", "traits", """{"smithy.api#documentation":"Not a member"}""")]
    [Arguments("output", "traits", """{"example#Missing":{}}""")]
    [Arguments("errors", "traits", "null")]
    [Arguments("input", "$ref", "\"https://must-not-load.example/schema\"")]
    [Arguments("output", "type", "\"structure\"")]
    [Arguments("errors", "Target", "\"example#Payload\"")]
    [Arguments("input", "a/b~c", "true")]
    public async Task SmithyJsonPlainReferencesRejectExtraPropertiesBeforeTraversingThem(
        string position, string property, string value)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation"},
              "example#Payload":{"type":"structure","traits":{"smithy.api#error":"client"}}}}
            """)!;
        var reference = new JsonObject { ["target"] = "example#Payload", [property] = JsonNode.Parse(value) };
        document["shapes"]!["example#Get"]![position] = position == "errors" ? new JsonArray(reference) : reference;
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Reference properties are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.reference_property");
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Get/" + position + (position == "errors" ? "/0" : "") + "/" +
                property.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("structure", "members/value")]
    [Arguments("union", "members/value")]
    [Arguments("enum", "members/value")]
    [Arguments("intEnum", "members/value")]
    [Arguments("list", "member")]
    [Arguments("map", "key")]
    [Arguments("map", "value")]
    public async Task SmithyJsonMemberReferencesPermitTraitsButRejectOtherProperties(string kind, string position)
    {
        var target = kind is "enum" or "intEnum" ? "smithy.api#Unit" : "smithy.api#String";
        var member = new JsonObject
        {
            ["target"] = target,
            ["traits"] = JsonNode.Parse(kind == "intEnum"
                ? """{"smithy.api#documentation":"Member","smithy.api#enumValue":1}"""
                : """{"smithy.api#documentation":"Member"}""")
        };
        var container = new JsonObject { ["type"] = kind };
        if (kind == "list") { container["member"] = member; }
        else if (kind == "map")
        {
            container["key"] = position == "key" ? member : new JsonObject { ["target"] = target };
            container["value"] = position == "value" ? member : new JsonObject { ["target"] = target };
        }
        else { container["members"] = new JsonObject { ["value"] = member }; }
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Container"}}}}}
            """)!;
        document["shapes"]!["example#Container"] = container;
        var valid = ResolveSmithy(document.ToJsonString());
        await Assert.That(valid.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(valid.Diagnostic).IsNull();
        await Assert.That(valid.Operation.GetRawText())
            .IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        member["type"] = "member";
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.reference_property");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/" + position + "/type");
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonPlainReferencePropertyErrorsIdentifyTheArrayOccurrence()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","errors":[
                {"target":"example#Failure"},{"target":"example#Failure","traits":{}}]},
              "example#Failure":{"type":"structure","traits":{"smithy.api#error":"server"}}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.reference_property");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/errors/1/traits");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonReferencePropertyChecksDoNotValidateUnrelatedShapesOrLiteralPayloads()
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"example#Literal":{
                "target":"example#Unknown","traits":{"$ref":"https://must-not-load.example/schema"}}}},
              "example#Literal":{"type":"document","traits":{"smithy.api#trait":{}}},
              "example#Other":{"type":"operation","input":{"target":"example#Unknown","traits":{}}}}}
            """;
        var result = ResolveSmithy(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("PrimitiveBoolean", "false")]
    [Arguments("PrimitiveByte", "0")]
    [Arguments("PrimitiveShort", "-0")]
    [Arguments("PrimitiveInteger", "0.0")]
    [Arguments("PrimitiveLong", "0e10")]
    [Arguments("PrimitiveFloat", "-0.000")]
    [Arguments("PrimitiveDouble", "0e-1000")]
    public async Task SmithyJsonPrimitivePreludeDefaultsRequireExplicitRepetitionOrNull(string target, string value)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#TARGET"}}}}}
            """.Replace("TARGET", target, StringComparison.Ordinal))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        var absent = ResolveSmithy(document.ToJsonString());
        await Assert.That(absent.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(absent.Diagnostic!.Code).IsEqualTo("document.default_repeat");
        await Assert.That(absent.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
        await Assert.That(absent.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        member["traits"] = new JsonObject { ["smithy.api#default"] = JsonNode.Parse(value) };
        var repeated = ResolveSmithy(document.ToJsonString());
        await Assert.That(repeated.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(repeated.Diagnostic).IsNull();
        await Assert.That(repeated.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        member["traits"]!["smithy.api#default"] = null;
        var overridden = ResolveSmithy(document.ToJsonString());
        await Assert.That(overridden.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(overridden.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("PrimitiveBoolean", "true")]
    [Arguments("PrimitiveBoolean", "0")]
    [Arguments("PrimitiveByte", "-1")]
    [Arguments("PrimitiveShort", "1")]
    [Arguments("PrimitiveInteger", "0.001")]
    [Arguments("PrimitiveLong", "false")]
    [Arguments("PrimitiveFloat", "\"NaN\"")]
    [Arguments("PrimitiveDouble", "\"0\"")]
    public async Task SmithyJsonPrimitivePreludeDefaultsRejectMismatchedValues(string target, string value)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#TARGET",
                "traits":{"smithy.api#default":VALUE}}}}}}
            """.Replace("TARGET", target, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_repeat");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("PrimitiveBoolean")]
    [Arguments("PrimitiveByte")]
    [Arguments("PrimitiveShort")]
    [Arguments("PrimitiveInteger")]
    [Arguments("PrimitiveLong")]
    [Arguments("PrimitiveFloat")]
    [Arguments("PrimitiveDouble")]
    public async Task SmithyJsonPrimitivePreludeDefaultsDoNotApplyToListMapOrUnionMembers(string target)
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{
                "list":{"target":"example#Values"},"map":{"target":"example#Mapping"},"union":{"target":"example#Choice"}}},
              "example#Values":{"type":"list","member":{"target":"smithy.api#TARGET"}},
              "example#Mapping":{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#TARGET"}},
              "example#Choice":{"type":"union","members":{"value":{"target":"smithy.api#TARGET"}}}}}
            """.Replace("TARGET", target, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Prelude types are not document imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
    }

    [Test]
    [Arguments("PrimitiveBoolean")]
    [Arguments("PrimitiveInteger")]
    [Arguments("PrimitiveDouble")]
    public async Task SmithyJsonPrimitivePreludeTypesRetainNativeKindRestrictions(string target)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"smithy.api#TARGET"}}}}
            """.Replace("TARGET", target, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.target_type");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("Primitiveboolean")]
    [Arguments("PrimitiveBigInteger")]
    [Arguments("PrimitiveString")]
    public async Task SmithyJsonPrimitivePreludeNamesAreExactAndDoNotCreateUnspecifiedTypes(string target)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#TARGET",
                "traits":{"smithy.api#default":null}}}}}}
            """.Replace("TARGET", target, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/smithy.api#" + target);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("null", false)]
    [Arguments("null", true)]
    [Arguments("\"en\"", false)]
    [Arguments("\"en\"", true)]
    public async Task SmithyJsonOptionalityAnnotationsPreserveRequiredAndDefaultDeclarations(string value, bool reverse)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#String"}}}}}
            """)!;
        var traits = new JsonObject();
        if (reverse) { traits["smithy.api#default"] = JsonNode.Parse(value); }
        traits["smithy.api#addedDefault"] = new JsonObject();
        traits["smithy.api#clientOptional"] = new JsonObject();
        traits["smithy.api#required"] = new JsonObject();
        if (!reverse) { traits["smithy.api#default"] = JsonNode.Parse(value); }
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"] = traits;
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Prelude annotations are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("null")]
    [Arguments("false")]
    [Arguments("[]")]
    [Arguments("1")]
    [Arguments("\"added\"")]
    public async Task SmithyJsonAddedDefaultRequiresAnObjectEvenWithAValidDefault(string value)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "\"en\"", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#addedDefault"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#addedDefault");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("list", "{}", true)]
    [Arguments("map", "{}", true)]
    [Arguments("list", "null", false)]
    [Arguments("map", "false", false)]
    [Arguments("list", "[]", false)]
    [Arguments("map", "\"sparse\"", false)]
    public async Task SmithyJsonSparseAnnotationsApplyToCollectionsAndRequireObjects(string kind, string value, bool valid)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, kind == "list" ? "[]" : "{}", false))!;
        document["shapes"]!["example#Value"]!["traits"] =
            new JsonObject { ["smithy.api#sparse"] = JsonNode.Parse(value) };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Value/traits/smithy.api#sparse");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonClientOptionalDoesNotSuppressAuthoritativeDefaultValidation()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("integer", "\"not a number\"", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#clientOptional"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_value");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("smithy.api#Float", false)]
    [Arguments("smithy.api#Double", false)]
    [Arguments("smithy.api#Document", false)]
    [Arguments("smithy.api#PrimitiveFloat", false)]
    [Arguments("smithy.api#PrimitiveDouble", false)]
    [Arguments("smithy.api#String", true)]
    [Arguments("smithy.api#Integer", true)]
    [Arguments("smithy.api#BigDecimal", true)]
    [Arguments("smithy.api#Timestamp", true)]
    [Arguments("smithy.api#Blob", true)]
    [Arguments("smithy.api#Boolean", true)]
    [Arguments("smithy.api#Unit", true)]
    public async Task SmithyJsonUniqueItemsChecksImplicitPreludeValueKinds(string target, bool valid)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"values":{"target":"example#Values"}}},
              "example#Values":{"type":"list","member":{"target":"TARGET"},"traits":{"smithy.api#uniqueItems":{}}}}}
            """.Replace("TARGET", target, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.unique_items_target");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Values/traits/smithy.api#uniqueItems");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure", "float")]
    [Arguments("union", "double")]
    [Arguments("map", "document")]
    [Arguments("list", "float")]
    public async Task SmithyJsonUniqueItemsRejectsTransitiveIneligibleDataKinds(string containerKind, string leafKind)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"values":{"target":"example#Values"}}},
              "example#Values":{"type":"list","member":{"target":"example#Container"},"traits":{"smithy.api#uniqueItems":{}}},
              "example#Leaf":{"type":"LEAF"}}}
            """.Replace("LEAF", leafKind, StringComparison.Ordinal))!;
        var container = new JsonObject { ["type"] = containerKind };
        var member = JsonNode.Parse("""{"target":"example#Leaf"}""");
        if (containerKind == "list") { container["member"] = member; }
        else if (containerKind == "map")
        {
            container["key"] = JsonNode.Parse("""{"target":"smithy.api#String"}""");
            container["value"] = member;
        }
        else { container["members"] = new JsonObject { ["value"] = member }; }
        document["shapes"]!["example#Container"] = container;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.unique_items_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonUniqueItemsConflictsWithSparseInEitherPropertyOrder(bool reverse)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("list", "[]", false))!;
        var traits = new JsonObject
        {
            [reverse ? "smithy.api#sparse" : "smithy.api#uniqueItems"] = new JsonObject(),
            [reverse ? "smithy.api#uniqueItems" : "smithy.api#sparse"] = new JsonObject()
        };
        document["shapes"]!["example#Value"]!["traits"] = traits;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Value/traits/" +
            (reverse ? "smithy.api#sparse" : "smithy.api#uniqueItems"));
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("[]")]
    [Arguments("\"unique\"")]
    public async Task SmithyJsonUniqueItemsRequiresAnObjectAnnotation(string value)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("list", "[]", false))!;
        document["shapes"]!["example#Value"]!["traits"] =
            new JsonObject { ["smithy.api#uniqueItems"] = JsonNode.Parse(value) };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonUniqueItemsAllowsTerminatingRecursionAndIgnoresTraitDefinitionDataAndUnusedLists()
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"values":{"target":"example#Values"}}},
              "example#Values":{"type":"list","member":{"target":"example#Node"},
                "traits":{"smithy.api#uniqueItems":{},"example#Custom":1.5}},
              "example#Node":{"type":"structure","members":{"next":{"target":"example#Values"}}},
              "example#Custom":{"type":"double","traits":{"smithy.api#trait":{}}},
              "example#Unused":{"type":"list","member":{"target":"smithy.api#Document"},"traits":{"smithy.api#uniqueItems":{}}}}}
            """;
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Prelude annotations are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("Float")]
    [Arguments("Double")]
    [Arguments("Document")]
    public async Task SmithyJsonUnconstrainedListsCanContainNonEquivalenceKinds(string kind)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("list", "[]", false))!;
        document["shapes"]!["example#Value"]!["member"]!["target"] = "smithy.api#" + kind;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("example", true)]
    [Arguments("Example", false)]
    [Arguments("example.child", false)]
    [Arguments("other", false)]
    public async Task SmithyJsonPrivateTargetsRequireTheExactDeclaringNamespace(string targetNamespace, bool valid)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"NAMESPACE#Input"}},
              "NAMESPACE#Input":{"type":"structure","traits":{"smithy.api#private":{}}}}}
            """.Replace("NAMESPACE", targetNamespace, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.private_access");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/input");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure", "members/value")]
    [Arguments("union", "members/value")]
    [Arguments("list", "member")]
    [Arguments("map", "key")]
    [Arguments("map", "value")]
    public async Task SmithyJsonPrivateAccessChecksAllReachedDataMemberKinds(string kind, string position)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Container"}}},
              "other#Secret":{"type":"string","traits":{"smithy.api#private":{}}}}}
            """)!;
        var member = JsonNode.Parse("""{"target":"other#Secret"}""");
        var container = new JsonObject { ["type"] = kind };
        if (kind == "list") { container["member"] = member; }
        else if (kind == "map")
        {
            container["key"] = position == "key" ? member : JsonNode.Parse("""{"target":"smithy.api#String"}""");
            container["value"] = position == "value" ? member : JsonNode.Parse("""{"target":"smithy.api#String"}""");
        }
        else { container["members"] = new JsonObject { ["value"] = member }; }
        document["shapes"]!["example#Container"] = container;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.private_access");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/" + position);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonPrivateAccessIsCheckedAgainAfterAnAllowedReference(bool reverse)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{}},
              "example#Secret":{"type":"string","traits":{"smithy.api#private":{}}},
              "other#Container":{"type":"structure","members":{"secret":{"target":"example#Secret"}}}}}
            """)!;
        var members = document["shapes"]!["example#Input"]!["members"]!.AsObject();
        if (!reverse) { members["own"] = JsonNode.Parse("""{"target":"example#Secret"}"""); }
        members["foreign"] = JsonNode.Parse("""{"target":"other#Container"}""");
        if (reverse) { members["own"] = JsonNode.Parse("""{"target":"example#Secret"}"""); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.private_access");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/other#Container/members/secret");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("example", true)]
    [Arguments("other", false)]
    public async Task SmithyJsonPrivateCustomTraitsRespectTheApplyingShapesNamespace(string targetNamespace, bool valid)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"NAMESPACE#Custom":{}}},
              "NAMESPACE#Custom":{"type":"structure","traits":{"smithy.api#trait":{},"smithy.api#private":{}}}}}
            """.Replace("NAMESPACE", targetNamespace, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.private_access");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/other#Custom");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonPrivateMemberMarkingsAndLiteralDataDoNotPrivatizeTheirTargets()
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"other#Input"},
                "traits":{"smithy.api#private":{},"example#Custom":{"target":"secret#Missing"}}},
              "other#Input":{"type":"structure","members":{"value":{"target":"smithy.api#String","traits":{"smithy.api#private":{}}}}},
              "example#Custom":{"type":"document","traits":{"smithy.api#trait":{}}},
              "unused#Operation":{"type":"operation","input":{"target":"secret#Value"}},
              "secret#Value":{"type":"structure","traits":{"smithy.api#private":{}}}}}
            """;
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Private shape IDs are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("output")]
    [Arguments("errors")]
    public async Task SmithyJsonPrivateAccessChecksOperationOutputAndErrorReferences(string position)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation"},
              "other#Payload":{"type":"structure","traits":{"smithy.api#private":{},"smithy.api#error":"server"}}}}
            """)!;
        var reference = JsonNode.Parse("""{"target":"other#Payload"}""");
        document["shapes"]!["example#Get"]![position] = position == "errors" ? new JsonArray(reference) : reference;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.private_access");
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Get/" + position + (position == "errors" ? "/0" : ""));
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonPrivateMemberTraitUsesTheMembersDeclaringNamespaceNotItsTargetNamespace()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"other#Input"}},
              "other#Input":{"type":"structure","members":{"value":{"target":"example#String","traits":{"example#Custom":{}}}}},
              "example#String":{"type":"string"},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{},"smithy.api#private":{}}}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.private_access");
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/other#Input/members/value/traits/example#Custom");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("null", "document.length_bounds", "")]
    [Arguments("[]", "document.length_bounds", "")]
    [Arguments("{}", "document.length_bounds", "")]
    [Arguments("""{"min":null}""", "document.length_bound", "/min")]
    [Arguments("""{"min":"1"}""", "document.length_bound", "/min")]
    [Arguments("""{"min":false}""", "document.length_bound", "/min")]
    [Arguments("""{"max":1.1}""", "document.length_bound", "/max")]
    [Arguments("""{"max":9223372036854775808}""", "document.length_bound", "/max")]
    [Arguments("""{"min":-9223372036854775809}""", "document.length_bound", "/min")]
    [Arguments("""{"min":0,"max":{}}""", "document.length_bound", "/max")]
    public async Task SmithyJsonLengthDeclarationsRequireIntegralLongBounds(string bounds, string code, string suffix)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#String",
                "traits":{"smithy.api#length":BOUNDS}}}}}}
            """.Replace("BOUNDS", bounds, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#length" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"min":0}""")]
    [Arguments("""{"max":9223372036854775807}""")]
    [Arguments("""{"min":-9223372036854775808}""")]
    [Arguments("""{"min":1.0,"max":1e1}""")]
    public async Task SmithyJsonLengthDeclarationsRetainExactBoundsWithoutAcquisition(string bounds)
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#String",
                "traits":{"smithy.api#length":BOUNDS}}}}}}
            """.Replace("BOUNDS", bounds, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Length bounds are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("string", false, true)]
    [Arguments("string", true, true)]
    [Arguments("blob", false, true)]
    [Arguments("blob", true, true)]
    [Arguments("enum", false, true)]
    [Arguments("enum", true, true)]
    [Arguments("list", false, true)]
    [Arguments("list", true, true)]
    [Arguments("map", false, true)]
    [Arguments("map", true, true)]
    [Arguments("integer", false, false)]
    [Arguments("integer", true, false)]
    [Arguments("structure", false, false)]
    [Arguments("structure", true, false)]
    [Arguments("union", false, false)]
    [Arguments("union", true, false)]
    public async Task SmithyJsonLengthDeclarationTargetsRespectRootAndMemberKinds(string kind, bool onMember, bool valid)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member.AsObject().Remove("traits");
        var target = onMember ? member : document["shapes"]!["example#Value"]!;
        target["traits"] = JsonNode.Parse("""{"smithy.api#length":{"min":0}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
                ? "$/shapes/example#Input/members/value" : "$/shapes/example#Value") + "/traits/smithy.api#length");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("list", "member")]
    [Arguments("map", "key")]
    [Arguments("map", "value")]
    [Arguments("union", "members/value")]
    public async Task SmithyJsonLengthAnnotationsAlsoApplyToNonStructureMembers(string kind, string position)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!.AsObject().Remove("traits");
        var member = document["shapes"]!["example#Value"]!;
        foreach (var segment in (kind == "union" ? "members/A" : position).Split('/')) { member = member[segment]!; }
        member["traits"] = JsonNode.Parse("""{"smithy.api#length":{"max":10}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonLengthDeclarationsDoNotResolveMissingTargetsOrValidateUnrelatedDefinitions()
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Missing",
                "traits":{"smithy.api#length":{"min":1}}}}},
              "example#Unused":{"type":"string","traits":{"smithy.api#length":null}}}}
            """)!;
        var missing = ResolveSmithy(document.ToJsonString());
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missing.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(missing.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        document["shapes"]!["example#Missing"] = JsonNode.Parse("""{"type":"string"}""");
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("string", "\"\"", """{"min":0,"max":0}""", true)]
    [Arguments("string", "\"a\"", """{"min":2}""", false)]
    [Arguments("string", "\"ab\"", """{"min":2,"max":2}""", true)]
    [Arguments("string", "\"abc\"", """{"max":2}""", false)]
    [Arguments("string", "\"0123456789\"", """{"min":1e1,"max":10.0}""", true)]
    [Arguments("string", "\"\"", """{"max":-1}""", false)]
    [Arguments("string", "\"a\"", """{"min":-9223372036854775808,"max":9223372036854775807}""", true)]
    [Arguments("string", "\"\\uD83D\\uDE00\"", """{"min":1,"max":1}""", true)]
    [Arguments("string", "\"e\\u0301\"", """{"min":2,"max":2}""", true)]
    [Arguments("string", "\"\\uD83D\\uDE00\"", """{"min":2}""", false)]
    [Arguments("blob", "\"\"", """{"min":0,"max":0}""", true)]
    [Arguments("blob", "\"YQ==\"", """{"min":1,"max":1}""", true)]
    [Arguments("blob", "\"YWI=\"", """{"max":1}""", false)]
    [Arguments("blob", "\"YQ==\"", """{"min":2}""", false)]
    [Arguments("list", "[]", """{"max":0}""", true)]
    [Arguments("list", "[]", """{"min":1}""", false)]
    [Arguments("map", "{}", """{"min":0,"max":0}""", true)]
    [Arguments("map", "{}", """{"min":1}""", false)]
    [Arguments("enum", "\"authored\"", """{"min":8,"max":8}""", true)]
    [Arguments("enum", "\"authored\"", """{"min":8e0,"max":8.0}""", true)]
    [Arguments("enum", "\"authored\"", """{"min":7,"max":7}""", false)]
    public async Task SmithyJsonDefaultLengthsUseScalarByteAndCollectionCounts(
        string kind, string value, string bounds, bool valid)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument(kind, value, rootDefault))!;
            var owner = rootDefault ? document["shapes"]!["example#Value"]!
                : document["shapes"]!["example#Input"]!["members"]!["value"]!;
            owner["traits"]!["smithy.api#length"] = JsonNode.Parse(bounds);
            if (rootDefault)
            {
                document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#default"] = null;
            }
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid)
            {
                await Assert.That(result.Diagnostic).IsNull();
                await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
            }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_length");
                await Assert.That(result.Diagnostic.Path).IsEqualTo((rootDefault
                    ? "$/shapes/example#Value" : "$/shapes/example#Input/members/value") + "/traits/smithy.api#default");
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SmithyJsonMemberLengthSupersedesRatherThanMergesTargetBounds(bool overrideLength, bool narrow)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "\"ab\"", false))!;
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse(narrow
            ? """{"smithy.api#length":{"min":0,"max":10}}"""
            : """{"smithy.api#length":{"min":3,"max":4}}""");
        if (overrideLength)
        {
            document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#length"] =
                JsonNode.Parse(narrow ? """{"max":1}""" : """{"min":1}""");
        }
        var result = ResolveSmithy(document.ToJsonString());
        var valid = overrideLength != narrow;
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_length");
            await Assert.That(result.Diagnostic.Path)
                .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonNullDefaultOverridesAreNotMeasuredAgainstLengthConstraints(bool onMember)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""{"smithy.api#length":{"min":10}}""");
        if (onMember)
        {
            document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#length"] =
                JsonNode.Parse("""{"min":20}""");
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonDefaultLengthChecksDoNotAcquireOrScanUnrelatedAndLiteralValues()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "\"a\"", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#length"] =
            JsonNode.Parse("""{"min":1,"max":1}""");
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#length":{"min":2},"smithy.api#default":"a"}}
            """);
        document["metadata"] = JsonNode.Parse("""
            {"lengthLiteral":{"target":"https://example.test/not-an-import",
              "traits":{"smithy.api#length":{"min":2},"smithy.api#default":"a"}}}
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Length defaults are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("integer", "null", false, "document.range_bounds", "")]
    [Arguments("integer", "[]", false, "document.range_bounds", "")]
    [Arguments("integer", "{}", false, "document.range_bounds", "")]
    [Arguments("integer", """{"min":null}""", false, "document.range_bound", "/min")]
    [Arguments("integer", """{"max":true}""", false, "document.range_bound", "/max")]
    [Arguments("byte", """{"min":-128,"max":127}""", true, "", "")]
    [Arguments("byte", """{"min":-129}""", false, "document.range_bound", "/min")]
    [Arguments("byte", """{"max":128}""", false, "document.range_bound", "/max")]
    [Arguments("short", """{"min":-32768,"max":32767}""", true, "", "")]
    [Arguments("short", """{"min":-32769}""", false, "document.range_bound", "/min")]
    [Arguments("short", """{"max":32768}""", false, "document.range_bound", "/max")]
    [Arguments("integer", """{"min":-2147483648,"max":2147483647}""", true, "", "")]
    [Arguments("integer", """{"min":-2147483649}""", false, "document.range_bound", "/min")]
    [Arguments("integer", """{"max":2147483648}""", false, "document.range_bound", "/max")]
    [Arguments("long", """{"min":-9223372036854775808,"max":9223372036854775807}""", true, "", "")]
    [Arguments("long", """{"min":-9223372036854775809}""", false, "document.range_bound", "/min")]
    [Arguments("long", """{"max":9223372036854775808}""", false, "document.range_bound", "/max")]
    [Arguments("intEnum", """{"min":-2147483648,"max":2147483647}""", true, "", "")]
    [Arguments("intEnum", """{"max":2147483648}""", false, "document.range_bound", "/max")]
    [Arguments("integer", """{"min":1.5}""", false, "document.range_bound", "/min")]
    [Arguments("bigInteger", """{"max":1.5}""", false, "document.range_bound", "/max")]
    [Arguments("bigInteger", """{"min":-1e100,"max":1e100}""", true, "", "")]
    [Arguments("integer", """{"min":1.0,"max":1e1}""", true, "", "")]
    [Arguments("bigDecimal", """{"min":-1.25e1000,"max":1.25e1000}""", true, "", "")]
    [Arguments("float", """{"min":-340282346638528859811704183484516925440,"max":340282346638528859811704183484516925440}""", true, "", "")]
    [Arguments("float", """{"min":-340282346638528859811704183484516925441}""", false, "document.range_bound", "/min")]
    [Arguments("float", """{"max":340282346638528859811704183484516925441}""", false, "document.range_bound", "/max")]
    [Arguments("double", """{"min":-1.7976931348623157e308,"max":1.7976931348623157e308}""", true, "", "")]
    [Arguments("double", """{"min":-1.7976931348623158e308}""", false, "document.range_bound", "/min")]
    [Arguments("double", """{"max":1.7976931348623158e308}""", false, "document.range_bound", "/max")]
    [Arguments("float", """{"min":-0.125,"max":0.125}""", true, "", "")]
    public async Task SmithyJsonRangeDeclarationsCheckExactNativeNumericBounds(
        string kind, string bounds, bool valid, string code, string suffix)
    {
        foreach (var onMember in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
            var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
            member.AsObject().Remove("traits");
            var owner = onMember ? member : document["shapes"]!["example#Value"]!;
            owner["traits"] = new JsonObject { ["smithy.api#range"] = JsonNode.Parse(bounds) };
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
                await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
                    ? "$/shapes/example#Input/members/value" : "$/shapes/example#Value") + "/traits/smithy.api#range" + suffix);
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments("integer", "0", """{"min":1}""", false)]
    [Arguments("integer", "1", """{"min":1,"max":2}""", true)]
    [Arguments("integer", "2", """{"min":1,"max":2}""", true)]
    [Arguments("integer", "3", """{"max":2}""", false)]
    [Arguments("long", "9007199254740993", """{"max":9007199254740992}""", false)]
    [Arguments("long", "-9007199254740993", """{"min":-9007199254740992}""", false)]
    [Arguments("bigInteger", "1e100", """{"min":1e100,"max":1e100}""", true)]
    [Arguments("bigInteger", "100000000000000000001", """{"max":1e20}""", false)]
    [Arguments("bigDecimal", "0.10000000000000000001", """{"max":0.1}""", false)]
    [Arguments("bigDecimal", "0.1", """{"min":1e-1,"max":0.100}""", true)]
    [Arguments("float", "-0.25", """{"min":-0.125}""", false)]
    [Arguments("float", "0.125", """{"min":-0.125,"max":0.125}""", true)]
    [Arguments("double", "0.25", """{"max":0.125}""", false)]
    [Arguments("double", "-0.125", """{"min":-0.125,"max":0.125}""", true)]
    [Arguments("intEnum", "1", """{"min":1,"max":1}""", true)]
    [Arguments("intEnum", "1", """{"min":2}""", false)]
    public async Task SmithyJsonNumericDefaultsMeetInclusiveEffectiveRangeBounds(
        string kind, string value, string bounds, bool valid)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument(kind, value, rootDefault))!;
            var owner = rootDefault ? document["shapes"]!["example#Value"]!
                : document["shapes"]!["example#Input"]!["members"]!["value"]!;
            owner["traits"]!["smithy.api#range"] = JsonNode.Parse(bounds);
            if (rootDefault)
            {
                document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#default"] = null;
            }
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_range");
                await Assert.That(result.Diagnostic.Path).IsEqualTo((rootDefault
                    ? "$/shapes/example#Value" : "$/shapes/example#Input/members/value") + "/traits/smithy.api#default");
            }
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SmithyJsonMemberRangeSupersedesRatherThanMergesTargetBounds(bool overrideRange, bool narrow)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("integer", "2", false))!;
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse(narrow
            ? """{"smithy.api#range":{"min":0,"max":10}}"""
            : """{"smithy.api#range":{"min":3,"max":4}}""");
        if (overrideRange)
        {
            document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#range"] =
                JsonNode.Parse(narrow ? """{"max":1}""" : """{"min":1}""");
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(overrideRange != narrow
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (overrideRange != narrow) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_range");
            await Assert.That(result.Diagnostic.Path)
                .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
        }
    }

    [Test]
    [Arguments("string")]
    [Arguments("boolean")]
    [Arguments("blob")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("structure")]
    [Arguments("union")]
    [Arguments("enum")]
    [Arguments("document")]
    [Arguments("timestamp")]
    public async Task SmithyJsonRangeRejectsNonNumericRootsAndMembers(string kind)
    {
        foreach (var onMember in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
            var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
            member.AsObject().Remove("traits");
            var owner = onMember ? member : document["shapes"]!["example#Value"]!;
            owner["traits"] = JsonNode.Parse("""{"smithy.api#range":{"min":0}}""");
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
                ? "$/shapes/example#Input/members/value" : "$/shapes/example#Value") + "/traits/smithy.api#range");
        }
    }

    [Test]
    [Arguments("float", "\"NaN\"")]
    [Arguments("float", "\"Infinity\"")]
    [Arguments("double", "\"-Infinity\"")]
    public async Task SmithyJsonRangeSpecialFloatingDefaultsRemainExplicitlyUnsupported(string kind, string value)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, value, false))!;
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""{"smithy.api#range":{"min":0}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.special_range_default");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
    }

    [Test]
    [Arguments("min", "\"+1.25\"")]
    [Arguments("max", "\"NaN\"")]
    public async Task SmithyJsonStringEncodedRangeBoundsRemainExplicitlyUnsupported(string name, string value)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("bigDecimal", "null", false))!;
        document["shapes"]!["example#Value"]!["traits"] = new JsonObject
        {
            ["smithy.api#range"] = new JsonObject { [name] = JsonNode.Parse(value) }
        };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.numeric_string_range");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Value/traits/smithy.api#range/" + name);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonNullDefaultOverridesAreNotComparedWithRangeBounds(bool onMember)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("integer", "1", true))!;
        document["shapes"]!["example#Value"]!["traits"]!["smithy.api#range"] = JsonNode.Parse("""{"min":1}""");
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member["traits"]!["smithy.api#default"] = null;
        if (onMember) { member["traits"]!["smithy.api#range"] = JsonNode.Parse("""{"min":2}"""); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("list", "member")]
    [Arguments("map", "value")]
    [Arguments("union", "members/A")]
    public async Task SmithyJsonRangeChecksApplyToAggregateMemberTargets(string kind, string position)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!.AsObject().Remove("traits");
        var owner = document["shapes"]!["example#Value"]!;
        foreach (var segment in position.Split('/')) { owner = owner[segment]!; }
        owner["target"] = "smithy.api#Byte";
        owner["traits"] = JsonNode.Parse("""{"smithy.api#range":{"max":127}}""");
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        owner["traits"]!["smithy.api#range"]!["max"] = 128;
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.range_bound");
        await Assert.That(invalid.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Value/" + position + "/traits/smithy.api#range/max");
    }

    [Test]
    [Arguments("smithy.api#Byte", 127, true)]
    [Arguments("smithy.api#Byte", 128, false)]
    [Arguments("smithy.api#PrimitiveByte", 127, true)]
    [Arguments("smithy.api#PrimitiveByte", 128, false)]
    [Arguments("smithy.api#Float", 1, true)]
    [Arguments("smithy.api#PrimitiveFloat", 1, true)]
    public async Task SmithyJsonRangeUsesImplicitPreludeNumericKinds(string target, int maximum, bool valid)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#Byte",
                "traits":{"smithy.api#range":{"max":127},"smithy.api#default":0}}}}}}
            """)!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member["target"] = target;
        member["traits"]!["smithy.api#range"]!["max"] = maximum;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.range_bound");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#range/max");
        }
    }

    [Test]
    public async Task SmithyJsonRangeChecksPreserveNativeSelectionWithoutAcquisitionOrUnrelatedScanning()
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Missing",
                "traits":{"smithy.api#range":{"min":1},"smithy.api#default":1}}}},
              "example#Unused":{"type":"integer","traits":{"smithy.api#range":null}}}}
            """)!;
        var missing = ResolveSmithy(document.ToJsonString());
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missing.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        document["shapes"]!["example#Missing"] = JsonNode.Parse("""{"type":"integer"}""");
        document["metadata"] = JsonNode.Parse("""
            {"literal":{"target":"https://example.test/not-an-import",
              "traits":{"smithy.api#range":{"min":10},"smithy.api#default":1}}}
            """);
        var selected = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Range bounds are not imports."));
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        await Assert.That(selected.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("\"\"", ApiOperationResolutionStatus.Selected, "")]
    [Arguments("\"^[a-zA-Z]+$\"", ApiOperationResolutionStatus.Selected, "")]
    [Arguments("\"[0-9]{1,3}\"", ApiOperationResolutionStatus.Selected, "")]
    [Arguments("\"(?:ab|cd)*?\"", ApiOperationResolutionStatus.Selected, "")]
    [Arguments("\"\\\\w+\"", ApiOperationResolutionStatus.Selected, "")]
    [Arguments("\"\\\\u0041\\\\x42\"", ApiOperationResolutionStatus.Selected, "")]
    [Arguments("\"[\"", ApiOperationResolutionStatus.Invalid, "schema.pattern")]
    [Arguments("\"[z-a]\"", ApiOperationResolutionStatus.Invalid, "schema.pattern")]
    [Arguments("\"(\"", ApiOperationResolutionStatus.Invalid, "schema.pattern")]
    [Arguments("\")\"", ApiOperationResolutionStatus.Invalid, "schema.pattern")]
    [Arguments("\"*\"", ApiOperationResolutionStatus.Invalid, "schema.pattern")]
    [Arguments("\"a{3,2}\"", ApiOperationResolutionStatus.Invalid, "schema.pattern")]
    [Arguments("\"\\\\\"", ApiOperationResolutionStatus.Invalid, "schema.pattern")]
    [Arguments("\"\\\\u00gg\"", ApiOperationResolutionStatus.Invalid, "schema.pattern")]
    [Arguments("\"(?<=x)y\"", ApiOperationResolutionStatus.Unsupported, "schema.pattern_unsupported")]
    [Arguments("\"(?=x)y\"", ApiOperationResolutionStatus.Unsupported, "schema.pattern_unsupported")]
    [Arguments("\"(x)\\\\1\"", ApiOperationResolutionStatus.Unsupported, "schema.pattern_unsupported")]
    [Arguments("\"\\\\p{L}\"", ApiOperationResolutionStatus.Unsupported, "schema.pattern_unsupported")]
    [Arguments("null", ApiOperationResolutionStatus.Invalid, "document.trait_value")]
    [Arguments("{}", ApiOperationResolutionStatus.Invalid, "document.trait_value")]
    [Arguments("1", ApiOperationResolutionStatus.Invalid, "document.trait_value")]
    [Arguments("true", ApiOperationResolutionStatus.Invalid, "document.trait_value")]
    public async Task SmithyJsonPatternDeclarationsUseBoundedEcmaSyntax(
        string pattern, ApiOperationResolutionStatus status, string code)
    {
        foreach (var onMember in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
            var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
            member.AsObject().Remove("traits");
            var owner = onMember ? member : document["shapes"]!["example#Value"]!;
            owner["traits"] = new JsonObject { ["smithy.api#pattern"] = JsonNode.Parse(pattern) };
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(status);
            if (status == ApiOperationResolutionStatus.Selected) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
                await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
                    ? "$/shapes/example#Input/members/value" : "$/shapes/example#Value") + "/traits/smithy.api#pattern");
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments("string", true)]
    [Arguments("enum", true)]
    [Arguments("integer", false)]
    [Arguments("intEnum", false)]
    [Arguments("boolean", false)]
    [Arguments("blob", false)]
    [Arguments("list", false)]
    [Arguments("map", false)]
    [Arguments("structure", false)]
    [Arguments("union", false)]
    [Arguments("document", false)]
    [Arguments("timestamp", false)]
    public async Task SmithyJsonPatternChecksRootAndMemberStringApplicability(string kind, bool valid)
    {
        foreach (var onMember in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
            var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
            member.AsObject().Remove("traits");
            var owner = onMember ? member : document["shapes"]!["example#Value"]!;
            owner["traits"] = JsonNode.Parse("""{"smithy.api#pattern":"[a-z]"}""");
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
                await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
                    ? "$/shapes/example#Input/members/value" : "$/shapes/example#Value") + "/traits/smithy.api#pattern");
            }
        }
    }

    [Test]
    [Arguments("list", "member")]
    [Arguments("map", "key")]
    [Arguments("map", "value")]
    [Arguments("union", "members/A")]
    public async Task SmithyJsonPatternChecksNonStructureMembers(string kind, string position)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!.AsObject().Remove("traits");
        var owner = document["shapes"]!["example#Value"]!;
        foreach (var segment in position.Split('/')) { owner = owner[segment]!; }
        owner["traits"] = JsonNode.Parse("""{"smithy.api#pattern":"\\w+"}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        owner["traits"]!["smithy.api#pattern"] = "[";
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("schema.pattern");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Value/" + position + "/traits/smithy.api#pattern");
    }

    [Test]
    [Arguments("string", "\"a\"")]
    [Arguments("string", "\"\"")]
    [Arguments("enum", "\"authored\"")]
    public async Task SmithyJsonPatternConstrainedDefaultsRemainExplicitlyUnsupported(string kind, string value)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            foreach (var onMember in new[] { false, true })
            {
                var document = JsonNode.Parse(SmithyDefaultDocument(kind, value, rootDefault))!;
                var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
                var owner = onMember ? member : document["shapes"]!["example#Value"]!;
                if (owner["traits"] is null) { owner["traits"] = new JsonObject(); }
                owner["traits"]!["smithy.api#pattern"] = "[a-z]";
                var result = ResolveSmithy(document.ToJsonString());
                await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.pattern_default");
                await Assert.That(result.Diagnostic.Path)
                    .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonRootPatternDefaultsRemainUnsupportedAfterNullMemberOverride(bool withLength)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "\"a\"", true))!;
        var target = document["shapes"]!["example#Value"]!;
        target["traits"]!["smithy.api#pattern"] = "a";
        if (withLength) { target["traits"]!["smithy.api#length"] = JsonNode.Parse("""{"min":1,"max":1}"""); }
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#default"] = null;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.pattern_default");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Value/traits/smithy.api#default");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonNullMemberDefaultsDoNotRequirePatternEvaluation(bool onMember)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        var owner = onMember ? document["shapes"]!["example#Input"]!["members"]!["value"]!
            : document["shapes"]!["example#Value"]!;
        if (owner["traits"] is null) { owner["traits"] = new JsonObject(); }
        owner["traits"]!["smithy.api#pattern"] = "^[a-z]+$";
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonPatternChecksPreserveNativeSelectionAndUnresolvedBoundaries()
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Missing",
                "traits":{"smithy.api#pattern":"[a-z]"}}}},
              "example#Unused":{"type":"string","traits":{"smithy.api#pattern":"["}}}}
            """)!;
        var missing = ResolveSmithy(document.ToJsonString());
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(missing.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        document["shapes"]!["example#Input"]!["members"]!["value"]!["target"] = "smithy.api#String";
        document["metadata"] = JsonNode.Parse("""
            {"literal":{"target":"https://example.test/not-an-import","traits":{"smithy.api#pattern":"["}}}
            """);
        var selected = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Patterns are not imports."));
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        await Assert.That(selected.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null", "document.enum_trait", "")]
    [Arguments("{}", "document.enum_trait", "")]
    [Arguments("[]", "document.enum_trait", "")]
    [Arguments("[null]", "document.enum_trait", "/0")]
    [Arguments("[{}]", "document.enum_trait_value", "/0/value")]
    [Arguments("""[{"value":null}]""", "document.enum_trait_value", "/0/value")]
    [Arguments("""[{"value":1}]""", "document.enum_trait_value", "/0/value")]
    [Arguments("""[{"value":""}]""", "document.enum_trait_value", "/0/value")]
    [Arguments("""[{"value":"a"},{"value":"a"}]""", "document.enum_trait_value", "/1/value")]
    [Arguments("""[{"value":"a","name":null}]""", "document.enum_trait_name", "/0/name")]
    [Arguments("""[{"value":"a","name":""}]""", "document.enum_trait_name", "/0/name")]
    [Arguments("""[{"value":"a","name":"1A"}]""", "document.enum_trait_name", "/0/name")]
    [Arguments("""[{"value":"a","name":"a-b"}]""", "document.enum_trait_name", "/0/name")]
    [Arguments("""[{"value":"a","name":"A\n"}]""", "document.enum_trait_name", "/0/name")]
    [Arguments("""[{"value":"a","name":"\u00c4"}]""", "document.enum_trait_name", "/0/name")]
    [Arguments("""[{"value":"a","name":"A"},{"value":"b","name":"A"}]""", "document.enum_trait_name", "/1/name")]
    [Arguments("""[{"value":"a","name":"A"},{"value":"b"}]""", "document.enum_trait_name", "/1/name")]
    [Arguments("""[{"value":"a"},{"value":"b","name":"B"}]""", "document.enum_trait_name", "/1/name")]
    [Arguments("""[{"value":"a","documentation":null}]""", "document.enum_trait_metadata", "/0/documentation")]
    [Arguments("""[{"value":"a","documentation":1}]""", "document.enum_trait_metadata", "/0/documentation")]
    [Arguments("""[{"value":"a","deprecated":"true"}]""", "document.enum_trait_metadata", "/0/deprecated")]
    [Arguments("""[{"value":"a","deprecated":null}]""", "document.enum_trait_metadata", "/0/deprecated")]
    [Arguments("""[{"value":"a","tags":null}]""", "document.enum_trait_metadata", "/0/tags")]
    [Arguments("""[{"value":"a","tags":{}}]""", "document.enum_trait_metadata", "/0/tags")]
    [Arguments("""[{"value":"a","tags":[""]}]""", "document.enum_trait_metadata", "/0/tags/0")]
    [Arguments("""[{"value":"a","tags":["tag",null]}]""", "document.enum_trait_metadata", "/0/tags/1")]
    public async Task SmithyJsonLegacyEnumDeclarationsCheckValuesNamesAndMetadata(string declaration, string code, string suffix)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Value"]!["traits"] =
            new JsonObject { ["smithy.api#enum"] = JsonNode.Parse(declaration) };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Value/traits/smithy.api#enum" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""[{"value":"a"}]""")]
    [Arguments("""[{"value":"a"},{"value":"A"}]""")]
    [Arguments("""[{"value":"a","name":"A"},{"value":"b","name":"a"}]""")]
    [Arguments("""[{"value":"a","name":"_"},{"value":"b","name":"a_1"}]""")]
    [Arguments("""[{"value":"\u00e9"},{"value":"e\u0301"}]""")]
    [Arguments("""[{"value":"a","documentation":"","tags":[],"deprecated":false}]""")]
    [Arguments("""[{"value":"a","documentation":"text","tags":["tag","tag"],"deprecated":true}]""")]
    public async Task SmithyJsonLegacyEnumsRetainExactIdentifiersAndOptionalMetadata(string declaration)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Value"]!["traits"] =
            new JsonObject { ["smithy.api#enum"] = JsonNode.Parse(declaration) };
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#enum":[{"value":""}]}}
            """);
        document["metadata"] = JsonNode.Parse("""
            {"literal":{"target":"https://example.test/not-an-import","traits":{"smithy.api#enum":null}}}
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Enum values are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("\"a\"", true)]
    [Arguments("\"b\"", true)]
    [Arguments("\"A\"", false)]
    [Arguments("\"\"", false)]
    [Arguments("\"c\"", false)]
    public async Task SmithyJsonLegacyEnumDefaultsMustMatchExactDeclaredValues(string value, bool valid)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument("string", value, rootDefault))!;
            var target = document["shapes"]!["example#Value"]!;
            if (target["traits"] is null) { target["traits"] = new JsonObject(); }
            target["traits"]!["smithy.api#enum"] = JsonNode.Parse("""[{"value":"a"},{"value":"b"}]""");
            if (rootDefault)
            {
                document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#default"] = null;
            }
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_enum");
                await Assert.That(result.Diagnostic.Path).IsEqualTo((rootDefault
                    ? "$/shapes/example#Value" : "$/shapes/example#Input/members/value") + "/traits/smithy.api#default");
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments("enum", false)]
    [Arguments("intEnum", false)]
    [Arguments("integer", false)]
    [Arguments("blob", false)]
    [Arguments("list", false)]
    [Arguments("structure", false)]
    [Arguments("string", true)]
    public async Task SmithyJsonLegacyEnumTraitRejectsNonStringRootsAndAllMembers(string kind, bool onMember)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member.AsObject().Remove("traits");
        var owner = onMember ? member : document["shapes"]!["example#Value"]!;
        owner["traits"] = JsonNode.Parse("""{"smithy.api#enum":[{"value":"a"}]}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
            ? "$/shapes/example#Input/members/value" : "$/shapes/example#Value") + "/traits/smithy.api#enum");
    }

    [Test]
    public async Task SmithyJsonLegacyEnumDefaultsRetainNullOverrideAndLengthConstraints()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#enum":[{"value":"ab"}],"smithy.api#length":{"min":2,"max":2}}
            """);
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        var nullResult = ResolveSmithy(document.ToJsonString());
        await Assert.That(nullResult.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        member["traits"]!["smithy.api#default"] = "ab";
        var valid = ResolveSmithy(document.ToJsonString());
        await Assert.That(valid.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        member["traits"]!["smithy.api#length"] = JsonNode.Parse("""{"max":1}""");
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.default_length");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
    }

    [Test]
    [Arguments("deprecated", "null", false, "")]
    [Arguments("deprecated", "\"text\"", false, "")]
    [Arguments("deprecated", """{"message":null}""", false, "/message")]
    [Arguments("deprecated", """{"message":1}""", false, "/message")]
    [Arguments("deprecated", """{"since":[]}""", false, "/since")]
    [Arguments("deprecated", """{"since":false}""", false, "/since")]
    [Arguments("deprecated", "{}", true, "")]
    [Arguments("deprecated", """{"message":""}""", true, "")]
    [Arguments("deprecated", """{"since":"not-a-date"}""", true, "")]
    [Arguments("deprecated", """{"message":"Use next.","since":"v2"}""", true, "")]
    [Arguments("recommended", "null", false, "")]
    [Arguments("recommended", "[]", false, "")]
    [Arguments("recommended", """{"reason":null}""", false, "/reason")]
    [Arguments("recommended", """{"reason":true}""", false, "/reason")]
    [Arguments("recommended", "{}", true, "")]
    [Arguments("recommended", """{"reason":""}""", true, "")]
    [Arguments("recommended", """{"reason":"Usually useful."}""", true, "")]
    public async Task SmithyJsonLifecycleAnnotationTextRetainsDeclaredTypes(
        string trait, string value, bool valid, string suffix)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member["traits"] = new JsonObject { ["smithy.api#" + trait] = JsonNode.Parse(value) };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
            await Assert.That(result.Diagnostic.Path)
                .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#" + trait + suffix);
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("required")]
    [Arguments("recommended")]
    public async Task SmithyJsonRecommendedAndRequiredConflictInEitherPropertyOrder(string first)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        var second = first == "required" ? "recommended" : "required";
        member["traits"] = new JsonObject
        {
            ["smithy.api#" + first] = new JsonObject(),
            ["smithy.api#" + second] = new JsonObject()
        };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#" + first);
    }

    [Test]
    [Arguments("list", "member")]
    [Arguments("map", "key")]
    [Arguments("map", "value")]
    [Arguments("union", "members/A")]
    [Arguments("enum", "members/A")]
    [Arguments("intEnum", "members/A")]
    public async Task SmithyJsonDeprecationAllowsMembersButRecommendationRequiresStructure(string kind, string position)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!.AsObject().Remove("traits");
        var member = document["shapes"]!["example#Value"]!;
        foreach (var segment in position.Split('/')) { member = member[segment]!; }
        if (member["traits"] is null) { member["traits"] = new JsonObject(); }
        member["traits"]!["smithy.api#deprecated"] = JsonNode.Parse("""{"message":"Use next."}""");
        var deprecated = ResolveSmithy(document.ToJsonString());
        await Assert.That(deprecated.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        member["traits"]!["smithy.api#recommended"] = new JsonObject();
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(invalid.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Value/" + position + "/traits/smithy.api#recommended");
    }

    [Test]
    [Arguments("example#Get")]
    [Arguments("example#Input")]
    [Arguments("example#Value")]
    public async Task SmithyJsonDeprecationAllowsRootsButRecommendationDoesNot(string id)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]![id]!["traits"] = JsonNode.Parse("""{"smithy.api#deprecated":{"since":"vNext"}}""");
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        document["shapes"]![id]!["traits"]!["smithy.api#recommended"] = new JsonObject();
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/" + id + "/traits/smithy.api#recommended");
    }

    [Test]
    [Arguments(false, "{}")]
    [Arguments(false, "null")]
    [Arguments(true, "{}")]
    [Arguments(true, "null")]
    public async Task SmithyJsonBoxIsForbiddenInVersionTwoAtRootsAndMembers(bool onMember, string value)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("integer", "null", false))!;
        var owner = onMember ? document["shapes"]!["example#Input"]!["members"]!["value"]!
            : document["shapes"]!["example#Value"]!;
        owner["traits"] = new JsonObject { ["smithy.api#box"] = JsonNode.Parse(value) };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.smithy2_box");
        await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
            ? "$/shapes/example#Input/members/value" : "$/shapes/example#Value") + "/traits/smithy.api#box");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonLifecycleAnnotationsPreserveNativeMetadataWithoutAcquisitionOrUnrelatedScanning()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#deprecated":{"message":"https://example.test/not-an-import","since":"vNext"}}
            """);
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#recommended":{"reason":"https://example.test/not-a-dependency"},
             "smithy.api#clientOptional":{},"smithy.api#default":"a"}
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"integer","traits":{"smithy.api#box":{},"smithy.api#deprecated":null}}
            """);
        document["metadata"] = JsonNode.Parse("""{"literal":{"traits":{"smithy.api#box":{}}}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Lifecycle text is not an import."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("0", true)]
    [Arguments("-0", true)]
    [Arguments("0e-1000", true)]
    [Arguments("0.001", true)]
    [Arguments("-0.001", true)]
    [Arguments("1.234000", true)]
    [Arguments("1234e-3", true)]
    [Arguments("-62135596800", true)]
    [Arguments("-62135596799.999", true)]
    [Arguments("-62135596800.001", false)]
    [Arguments("253402300799.998", true)]
    [Arguments("253402300799.999", true)]
    [Arguments("253402300800", false)]
    [Arguments("0.0001", false)]
    [Arguments("-0.0001", false)]
    [Arguments("1.2341", false)]
    [Arguments("1e1000", false)]
    [Arguments("-1e1000", false)]
    [Arguments("1e-1000", false)]
    public async Task SmithyJsonNumericTimestampDefaultsUseExactEpochRangeAndMilliseconds(string value, bool valid)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument("timestamp", value, rootDefault))!;
            if (rootDefault)
            {
                document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#default"] = null;
            }
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_timestamp");
                await Assert.That(result.Diagnostic.Path).IsEqualTo((rootDefault
                    ? "$/shapes/example#Value" : "$/shapes/example#Input/members/value") + "/traits/smithy.api#default");
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments("true")]
    [Arguments("false")]
    [Arguments("[]")]
    [Arguments("{}")]
    public async Task SmithyJsonTimestampDefaultsRejectNonTimestampNodeKinds(string value)
    {
        var result = ResolveSmithy(SmithyDefaultDocument("timestamp", value, false));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
    }

    [Test]
    [Arguments("null")]
    [Arguments("1.234")]
    public async Task SmithyJsonImplicitTimestampDefaultsPreserveNativeSelectionWithoutAcquisition(string value)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("timestamp", value, false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["target"] = "smithy.api#Timestamp";
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"timestamp","traits":{"smithy.api#default":0.0001}}
            """);
        document["metadata"] = JsonNode.Parse("""
            {"literal":{"target":"https://example.test/not-an-import","traits":{"smithy.api#default":1e1000}}}
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Epoch values are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    public async Task SmithyJsonNumericTimestampRootDefaultsRequireExactMemberRepetition()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("timestamp", "1.234", true))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member["traits"]!["smithy.api#default"] = JsonNode.Parse("1234e-3");
        var same = ResolveSmithy(document.ToJsonString());
        await Assert.That(same.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        member["traits"]!["smithy.api#default"] = JsonNode.Parse("1.235");
        var different = ResolveSmithy(document.ToJsonString());
        await Assert.That(different.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(different.Diagnostic!.Code).IsEqualTo("document.default_repeat");
        member["traits"]!["smithy.api#default"] = null;
        var optional = ResolveSmithy(document.ToJsonString());
        await Assert.That(optional.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
    }

    [Test]
    [Arguments("0001-01-01T00:00:00Z", true)]
    [Arguments("9999-12-31T23:59:59.999Z", true)]
    [Arguments("1985-04-12T23:20:50.52Z", true)]
    [Arguments("2000-02-29t12:34:56z", true)]
    [Arguments("2024-02-29T00:00:00.123000000Z", true)]
    [Arguments("2026-01-01T00:00:00.000000000Z", true)]
    [Arguments("2026-01-01T00:00:00.1Z", true)]
    [Arguments("0000-01-01T00:00:00Z", false)]
    [Arguments("10000-01-01T00:00:00Z", false)]
    [Arguments("1900-02-29T00:00:00Z", false)]
    [Arguments("2025-02-29T00:00:00Z", false)]
    [Arguments("2026-04-31T00:00:00Z", false)]
    [Arguments("2026-00-01T00:00:00Z", false)]
    [Arguments("2026-13-01T00:00:00Z", false)]
    [Arguments("2026-01-00T00:00:00Z", false)]
    [Arguments("2026-01-01T24:00:00Z", false)]
    [Arguments("2026-01-01T00:60:00Z", false)]
    [Arguments("2026-01-01T00:00:61Z", false)]
    [Arguments("2026-01-01T00:00:00+00:00", false)]
    [Arguments("2026-01-01T00:00:00-00:00", false)]
    [Arguments("2026-01-01T00:00:00", false)]
    [Arguments("2026-01-01 00:00:00Z", false)]
    [Arguments("2026-01-01T00:00:00.Z", false)]
    [Arguments("2026-01-01T00:00:00Z\n", false)]
    [Arguments("", false)]
    public async Task SmithyJsonStringTimestampDefaultsCheckUtcGregorianDates(string text, bool valid)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument("timestamp", JsonSerializer.Serialize(text), rootDefault))!;
            if (rootDefault)
            {
                document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#default"] = null;
            }
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_timestamp");
                await Assert.That(result.Diagnostic.Path).IsEqualTo((rootDefault
                    ? "$/shapes/example#Value" : "$/shapes/example#Input/members/value") + "/traits/smithy.api#default");
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments("2016-12-31T23:59:60Z")]
    [Arguments("2026-01-01T00:00:00.0001Z")]
    [Arguments("2026-01-01T00:00:00.12301Z")]
    public async Task SmithyJsonStringTimestampDefaultsDoNotRoundUnsupportedInstants(string text)
    {
        var result = ResolveSmithy(SmithyDefaultDocument("timestamp", JsonSerializer.Serialize(text), false));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.timestamp_default");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonUtcTimestampDefaultsRetainImplicitTargetsAndLiteralBoundaries()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("timestamp", "\"2026-01-01T00:00:00Z\"", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["target"] = "smithy.api#Timestamp";
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"timestamp","traits":{"smithy.api#default":"invalid"}}
            """);
        document["metadata"] = JsonNode.Parse("""{"literal":{"target":"https://example.test/not-an-import"}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Timestamp strings are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("title", "\"\"", true, "")]
    [Arguments("title", "\"An authored title\"", true, "")]
    [Arguments("title", "null", false, "")]
    [Arguments("title", "{}", false, "")]
    [Arguments("title", "1", false, "")]
    [Arguments("since", "\"\"", true, "")]
    [Arguments("since", "\"not-a-date\"", true, "")]
    [Arguments("since", "true", false, "")]
    [Arguments("since", "[]", false, "")]
    [Arguments("since", "null", false, "")]
    [Arguments("internal", "{}", true, "")]
    [Arguments("internal", "null", false, "")]
    [Arguments("internal", "false", false, "")]
    [Arguments("internal", "[]", false, "")]
    [Arguments("tags", "[]", true, "")]
    [Arguments("tags", """["","a","a","A"]""", true, "")]
    [Arguments("tags", "null", false, "")]
    [Arguments("tags", "{}", false, "")]
    [Arguments("tags", "\"a\"", false, "")]
    [Arguments("tags", "[null]", false, "/0")]
    [Arguments("tags", """["a",1]""", false, "/1")]
    public async Task SmithyJsonDocumentationAnnotationsRetainRootAndMemberValueTypes(
        string trait, string value, bool valid, string suffix)
    {
        foreach (var onMember in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
            var owner = onMember ? document["shapes"]!["example#Input"]!["members"]!["value"]!
                : document["shapes"]!["example#Get"]!;
            owner["traits"] = new JsonObject { ["smithy.api#" + trait] = JsonNode.Parse(value) };
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
                await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
                    ? "$/shapes/example#Input/members/value" : "$/shapes/example#Get") + "/traits/smithy.api#" + trait + suffix);
                await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
            }
        }
    }

    [Test]
    [Arguments("string")]
    [Arguments("boolean")]
    [Arguments("blob")]
    [Arguments("integer")]
    [Arguments("timestamp")]
    [Arguments("document")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("structure")]
    [Arguments("union")]
    [Arguments("enum")]
    [Arguments("intEnum")]
    public async Task SmithyJsonSensitiveAnnotationAcceptsDataRoots(string kind)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!.AsObject().Remove("traits");
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""{"smithy.api#sensitive":{}}""");
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        document["shapes"]!["example#Value"]!["traits"]!["smithy.api#sensitive"] = null;
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Value/traits/smithy.api#sensitive");
    }

    [Test]
    [Arguments("string", "structureMember")]
    [Arguments("list", "member")]
    [Arguments("map", "key")]
    [Arguments("map", "value")]
    [Arguments("union", "members/A")]
    [Arguments("enum", "members/A")]
    [Arguments("intEnum", "members/A")]
    [Arguments("string", "operation")]
    public async Task SmithyJsonSensitiveAnnotationRejectsOperationsAndMembers(string kind, string position)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        var path = position == "operation" ? "example#Get"
            : position == "structureMember" ? "example#Input/members/value" : "example#Value/" + position;
        var owner = document["shapes"]!;
        foreach (var segment in path.Split('/')) { owner = owner[segment]!; }
        if (owner["traits"] is null) { owner["traits"] = new JsonObject(); }
        owner["traits"]!["smithy.api#sensitive"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/" + path + "/traits/smithy.api#sensitive");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("list", "member")]
    [Arguments("map", "key")]
    [Arguments("map", "value")]
    [Arguments("union", "members/A")]
    [Arguments("enum", "members/A")]
    [Arguments("intEnum", "members/A")]
    public async Task SmithyJsonDocumentationAnnotationsAllowAllAggregateMembers(string kind, string position)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!.AsObject().Remove("traits");
        var owner = document["shapes"]!["example#Value"]!;
        foreach (var segment in position.Split('/')) { owner = owner[segment]!; }
        if (owner["traits"] is null) { owner["traits"] = new JsonObject(); }
        owner["traits"]!["smithy.api#title"] = "Member";
        owner["traits"]!["smithy.api#since"] = "vNext";
        owner["traits"]!["smithy.api#tags"] = JsonNode.Parse("""["","tag","tag"]""");
        owner["traits"]!["smithy.api#internal"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonDocumentationMetadataStaysNativeWithoutAcquisitionOrUnrelatedScanning()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "\"a\"", false))!;
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#title":"https://example.test/not-an-import","smithy.api#since":"vNext",
             "smithy.api#tags":["","tag","tag"],"smithy.api#internal":{}}
            """);
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""{"smithy.api#sensitive":{}}""");
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#tags":null,"smithy.api#sensitive":null}}
            """);
        document["metadata"] = JsonNode.Parse("""{"literal":{"traits":{"smithy.api#tags":[null]}}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Documentation is not an import."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    public async Task SmithyJsonSensitiveDefaultFailuresDoNotEchoAuthoredValues()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "\"secret-default-sentinel\"", false))!;
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#sensitive":{},"smithy.api#length":{"max":1}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_length");
        await Assert.That(result.Diagnostic.Detail.Contains("secret-default-sentinel", StringComparison.Ordinal)).IsFalse();
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("bigInteger", "123456789012345678901234567890", true)]
    [Arguments("bigInteger", "-123456789012345678901234567890", true)]
    [Arguments("bigInteger", "0", true)]
    [Arguments("bigInteger", "-0.000", true)]
    [Arguments("bigInteger", "1.000", true)]
    [Arguments("bigInteger", "123e1000", true)]
    [Arguments("bigInteger", "1000e-3", true)]
    [Arguments("bigInteger", "1.5", false)]
    [Arguments("bigInteger", "1e-1", false)]
    [Arguments("bigDecimal", "12345678901234567890.123456789", true)]
    [Arguments("bigDecimal", "-0.000000000000000000001", true)]
    [Arguments("bigDecimal", "1E+1000", true)]
    [Arguments("bigDecimal", "-1e-1000", true)]
    public async Task SmithyJsonStringEncodedBigNumberDefaultsUseExactNumericValues(string kind, string text, bool valid)
    {
        foreach (var rootDefault in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument(kind, JsonSerializer.Serialize(text), rootDefault))!;
            if (rootDefault)
            {
                document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#default"] = null;
            }
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(valid
                ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
            if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
            else
            {
                await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_value");
                await Assert.That(result.Diagnostic.Path).IsEqualTo((rootDefault
                    ? "$/shapes/example#Value" : "$/shapes/example#Input/members/value") + "/traits/smithy.api#default");
            }
        }
    }

    [Test]
    [Arguments("bigInteger", "\"9007199254740993\"", """{"max":"9007199254740992"}""", false, "document.default_range")]
    [Arguments("bigInteger", "\"9007199254740993\"", """{"min":9007199254740993,"max":"9007199254740993"}""", true, "")]
    [Arguments("bigDecimal", "\"0.10000000000000000001\"", """{"max":"0.1"}""", false, "document.default_range")]
    [Arguments("bigDecimal", "\"0.1\"", """{"min":"1e-1","max":"0.100"}""", true, "")]
    [Arguments("bigDecimal", "0.1", """{"min":"0.1","max":"1e-1"}""", true, "")]
    [Arguments("bigDecimal", "\"-0.10000000000000000001\"", """{"min":"-0.1"}""", false, "document.default_range")]
    [Arguments("bigDecimal", "\"-0.1\"", """{"min":"-1e-1","max":"-0.100"}""", true, "")]
    [Arguments("byte", "127", """{"max":"127"}""", true, "")]
    [Arguments("byte", "127", """{"max":"128"}""", false, "document.range_bound")]
    [Arguments("integer", "1", """{"min":"0.5"}""", false, "document.range_bound")]
    [Arguments("float", "1", """{"max":"340282346638528859811704183484516925441"}""", false, "document.range_bound")]
    public async Task SmithyJsonStringEncodedRangeBoundsRetainExactComparisonAndNativeLimits(
        string kind, string value, string bounds, bool valid, string code)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument(kind, value, false))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member["traits"]!["smithy.api#range"] = JsonNode.Parse(bounds);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments(" 1")]
    [Arguments("1 ")]
    [Arguments("+1")]
    [Arguments("01")]
    [Arguments(".1")]
    [Arguments("1.")]
    [Arguments("Infinity")]
    [Arguments("0x10")]
    [Arguments("1e")]
    public async Task SmithyJsonNonJsonNumberStringSpellingsRemainExplicitlyUnsupported(string text)
    {
        var result = ResolveSmithy(SmithyDefaultDocument("bigDecimal", JsonSerializer.Serialize(text), false));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.numeric_string_default");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false, 256, true)]
    [Arguments(false, 257, false)]
    [Arguments(true, 256, true)]
    [Arguments(true, 257, false)]
    public async Task SmithyJsonEncodedNumbersRetainSourceCharacterBudget(bool bound, int length, bool valid)
    {
        var text = new string('1', length);
        var document = JsonNode.Parse(SmithyDefaultDocument("bigDecimal", bound ? "0" : JsonSerializer.Serialize(text), false))!;
        if (bound)
        {
            document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#range"] =
                new JsonObject { ["max"] = text };
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Indeterminate);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else { await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.number"); }
    }

    [Test]
    public async Task SmithyJsonStringEncodedNumbersRetainNativeMetadataAndMemberRangePrecedence()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("bigDecimal", "\"1.25\"", false))!;
        document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""{"smithy.api#range":{"min":"2"}}""");
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#range"] =
            JsonNode.Parse("""{"max":"1.250"}""");
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"bigInteger","traits":{"smithy.api#default":"1.5"}}
            """);
        document["metadata"] = JsonNode.Parse("""{"literal":{"target":"https://example.test/not-an-import"}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Numeric strings are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("String", "string")]
    [Arguments("Blob", "blob")]
    [Arguments("Boolean", "boolean")]
    [Arguments("Document", "document")]
    [Arguments("Byte", "byte")]
    [Arguments("Short", "short")]
    [Arguments("Integer", "integer")]
    [Arguments("Long", "long")]
    [Arguments("Float", "float")]
    [Arguments("Double", "double")]
    [Arguments("BigInteger", "bigInteger")]
    [Arguments("BigDecimal", "bigDecimal")]
    [Arguments("Timestamp", "timestamp")]
    [Arguments("PrimitiveBoolean", "boolean")]
    [Arguments("PrimitiveByte", "byte")]
    [Arguments("PrimitiveShort", "short")]
    [Arguments("PrimitiveInteger", "integer")]
    [Arguments("PrimitiveLong", "long")]
    [Arguments("PrimitiveFloat", "float")]
    [Arguments("PrimitiveDouble", "double")]
    [Arguments("Unit", "structure")]
    public async Task SmithyJsonAuthoredPreludeDataShapesRetainCanonicalKinds(string name, string kind)
    {
        var id = "smithy.api#" + name;
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["target"] = id;
        if (kind == "structure")
        {
            document["shapes"]!["example#Input"]!["members"]!["value"]!.AsObject().Remove("traits");
        }
        document["shapes"]![id] = new JsonObject { ["type"] = kind };
        var correct = ResolveSmithy(document.ToJsonString());
        await Assert.That(correct.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(correct.Diagnostic).IsNull();
        await Assert.That(correct.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        document["shapes"]![id]!["type"] = kind == "string" ? "boolean" : "string";
        var wrong = ResolveSmithy(document.ToJsonString());
        await Assert.That(wrong.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(wrong.Diagnostic!.Code).IsEqualTo("document.prelude_shape");
        await Assert.That(wrong.Diagnostic.Path).IsEqualTo("$/shapes/" + id + "/type");
        await Assert.That(wrong.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("PrimitiveBoolean", "boolean", "false", "true")]
    [Arguments("PrimitiveByte", "byte", "0", "1")]
    [Arguments("PrimitiveShort", "short", "-0", "null")]
    [Arguments("PrimitiveInteger", "integer", "0.0", "0.001")]
    [Arguments("PrimitiveLong", "long", "0e10", "false")]
    [Arguments("PrimitiveFloat", "float", "-0.000", "\"NaN\"")]
    [Arguments("PrimitiveDouble", "double", "0e-1000", "\"0\"")]
    public async Task SmithyJsonAuthoredPrimitivePreludeDefaultsRemainImplicitWhenOmitted(
        string name, string kind, string repeatedDefault, string wrongDefault)
    {
        var id = "smithy.api#" + name;
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member["target"] = id;
        member["traits"]!.AsObject().Remove("smithy.api#default");
        document["shapes"]![id] = new JsonObject { ["type"] = kind };
        var missing = ResolveSmithy(document.ToJsonString());
        await Assert.That(missing.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(missing.Diagnostic!.Code).IsEqualTo("document.default_repeat");
        await Assert.That(missing.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#default");
        member["traits"]!["smithy.api#default"] = JsonNode.Parse(repeatedDefault);
        var repeated = ResolveSmithy(document.ToJsonString());
        await Assert.That(repeated.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(repeated.Diagnostic).IsNull();
        member["traits"]!["smithy.api#default"] = null;
        var overridden = ResolveSmithy(document.ToJsonString());
        await Assert.That(overridden.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(overridden.Diagnostic).IsNull();
        document["shapes"]![id]!["traits"] = new JsonObject { ["smithy.api#default"] = JsonNode.Parse(wrongDefault) };
        var changed = ResolveSmithy(document.ToJsonString());
        await Assert.That(changed.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(changed.Diagnostic!.Code).IsEqualTo("document.prelude_default");
        await Assert.That(changed.Diagnostic.Path).IsEqualTo("$/shapes/" + id + "/traits/smithy.api#default");
        await Assert.That(changed.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        document["shapes"]![id]!["traits"]!["smithy.api#default"] = JsonNode.Parse(repeatedDefault);
        var retained = ResolveSmithy(document.ToJsonString());
        await Assert.That(retained.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(retained.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("""{"min":1}""", false)]
    [Arguments("""{"min":0}""", true)]
    [Arguments("""{"max":0}""", true)]
    [Arguments("""{"max":-1}""", false)]
    public async Task SmithyJsonImplicitPrimitivePreludeDefaultsSatisfyAuthoredRootConstraints(string range, bool valid)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["target"] = "smithy.api#PrimitiveInteger";
        document["shapes"]!["smithy.api#PrimitiveInteger"] = JsonNode.Parse(
            """{"type":"integer","traits":{"smithy.api#range":RANGE}}""".Replace("RANGE", range, StringComparison.Ordinal));
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.default_range");
            await Assert.That(result.Diagnostic.Path)
                .IsEqualTo("$/shapes/smithy.api#PrimitiveInteger/traits/smithy.api#default");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("smithy.api#string")]
    [Arguments("SMITHY.API#String")]
    [Arguments("smithy.api#UNIT")]
    [Arguments("smithy.api#Primitiveinteger")]
    public async Task SmithyJsonExplicitIdsCannotCaseCollideWithImplicitPreludeDataShapes(string id)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]![id] = new JsonObject { ["type"] = "string" };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.shape_id");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/" + id);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"type":"structure","members":{"value":{"target":"smithy.api#String"}}}""")]
    [Arguments("""{"type":"structure","members":null}""")]
    [Arguments("""{"type":"structure","members":[]}""")]
    public async Task SmithyJsonAuthoredUnitPreludeCannotAcquireMembers(string definition)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"smithy.api#Unit"}""");
        document["shapes"]!["smithy.api#Unit"] = JsonNode.Parse(definition);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.prelude_shape");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/smithy.api#Unit/members");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("operation")]
    [Arguments("root")]
    [Arguments("member")]
    public async Task SmithyJsonUnitTypeMarkerRequiresCanonicalUnitShape(string position)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        if (position == "root") { document["shapes"]!["example#Value"]!["type"] = "structure"; }
        var selected = position == "operation" ? document["shapes"]!["example#Get"]! :
            position == "root" ? document["shapes"]!["example#Value"]! :
            document["shapes"]!["example#Input"]!["members"]!["value"]!;
        selected["traits"] ??= new JsonObject();
        selected["traits"]!["smithy.api#unitType"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("\"unit\"")]
    [Arguments("[]")]
    public async Task SmithyJsonUnitTypeMarkerRetainsAnnotationValueType(string value)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"smithy.api#Unit"}""");
        document["shapes"]!["smithy.api#Unit"] = JsonNode.Parse(
            """{"type":"structure","members":{},"traits":{"smithy.api#unitType":VALUE}}"""
                .Replace("VALUE", value, StringComparison.Ordinal));
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(value == "{}"
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (value == "{}") { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/smithy.api#Unit/traits/smithy.api#unitType");
        }
    }

    [Test]
    public async Task SmithyJsonPreludeReconciliationPreservesNativeSelectionAndClosureBoundaries()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["smithy.api#String"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#documentation":"https://must-not-load.example/schema"}}
            """);
        document["shapes"]!["example#Input"]!["members"]!["value"]!["target"] = "smithy.api#String";
        document["shapes"]!["smithy.api#Long"] = JsonNode.Parse("""{"type":"boolean"}""");
        document["shapes"]!["example#PrimitiveInteger"] = JsonNode.Parse("""{"type":"string"}""");
        document["metadata"] = JsonNode.Parse("""{"literal":{"target":"smithy.api#PrimitiveLong"}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("The prelude is not acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""[{"title":""}]""")]
    [Arguments("""[{"title":"input","documentation":"","input":null}]""")]
    [Arguments("""[{"title":"output","output":null}]""")]
    [Arguments("""[{"title":"error","error":{}}]""")]
    [Arguments("""[{"title":"error","error":{"shapeId":"example#Failure"},"allowConstraintErrors":false}]""")]
    [Arguments("""[{"title":"error","error":{"shapeId":"example#Failure","content":null},"allowConstraintErrors":true}]""")]
    [Arguments("""[{"title":"repeat"},{"title":"repeat"}]""")]
    public async Task SmithyJsonOperationExamplesRetainDeclaredMetadataAndOptionalFields(string examples)
    {
        var document = SmithyExampleDocument(examples);
        var result = ResolveSmithy(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
    }

    [Test]
    [Arguments("null", "", "document.examples")]
    [Arguments("{}", "", "document.examples")]
    [Arguments("[null]", "/0", "document.example")]
    [Arguments("[true]", "/0", "document.example")]
    [Arguments("[{}]", "/0/title", "document.example_title")]
    [Arguments("""[{"title":null}]""", "/0/title", "document.example_title")]
    [Arguments("""[{"title":1}]""", "/0/title", "document.example_title")]
    [Arguments("""[{"title":"x","documentation":null}]""", "/0/documentation", "document.example_documentation")]
    [Arguments("""[{"title":"x","documentation":{}}]""", "/0/documentation", "document.example_documentation")]
    [Arguments("""[{"title":"x","error":null}]""", "/0/error", "document.example_error")]
    [Arguments("""[{"title":"x","error":[]}]""", "/0/error", "document.example_error")]
    [Arguments("""[{"title":"x","error":{},"output":null}]""", "/0", "document.example_conflict")]
    [Arguments("""[{"title":"x","output":{},"error":{}}]""", "/0", "document.example_conflict")]
    [Arguments("""[{"title":"x","allowConstraintErrors":false}]""", "/0/allowConstraintErrors", "document.example_constraints")]
    [Arguments("""[{"title":"x","allowConstraintErrors":true}]""", "/0/allowConstraintErrors", "document.example_constraints")]
    [Arguments("""[{"title":"x","error":{},"allowConstraintErrors":null}]""", "/0/allowConstraintErrors", "document.example_constraints")]
    [Arguments("""[{"title":"x","error":{},"allowConstraintErrors":"true"}]""", "/0/allowConstraintErrors", "document.example_constraints")]
    [Arguments("""[{"title":"x","error":{"shapeId":null}}]""", "/0/error/shapeId", "document.example_error")]
    [Arguments("""[{"title":"x","error":{"shapeId":"Failure"}}]""", "/0/error/shapeId", "document.example_error")]
    [Arguments("""[{"title":"x","error":{"shapeId":"example#failure"}}]""", "/0/error/shapeId", "document.example_error")]
    [Arguments("""[{"title":"x","error":{"shapeId":"example#Input"}}]""", "/0/error/shapeId", "document.example_error")]
    [Arguments("""[{"title":"ok"},{"title":"bad","error":{"shapeId":"example#OtherFailure"}}]""",
        "/1/error/shapeId", "document.example_error")]
    public async Task SmithyJsonOperationExamplesRejectMalformedFieldsAndUnboundErrors(
        string examples, string suffix, string code)
    {
        var result = ResolveSmithy(SmithyExampleDocument(examples));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#examples" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("root")]
    [Arguments("member")]
    [Arguments("listMember")]
    public async Task SmithyJsonExamplesTraitRequiresAnOperation(string position)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        var selected = position == "root" ? document["shapes"]!["example#Input"]! :
            position == "member" ? document["shapes"]!["example#Input"]!["members"]!["value"]! :
            document["shapes"]!["example#Values"]!["member"]!;
        selected["traits"] ??= new JsonObject();
        selected["traits"]!["smithy.api#examples"] = new JsonArray();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("missing", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("unmarked", ApiOperationResolutionStatus.Invalid, "document.error_trait")]
    [Arguments("string", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    public async Task SmithyJsonExampleBindingsRetainNormalErrorDependencyValidation(
        string definition, ApiOperationResolutionStatus expected, string code)
    {
        var document = JsonNode.Parse(SmithyExampleDocument(
            """[{"title":"failure","error":{"shapeId":"example#Failure"}}]"""))!;
        if (definition == "missing") { document["shapes"]!.AsObject().Remove("example#Failure"); }
        else
        {
            document["shapes"]!["example#Failure"] = new JsonObject
                { ["type"] = definition == "string" ? "string" : "structure" };
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonExampleErrorsRequireAnOwnOperationBinding(bool emptyErrors)
    {
        var document = JsonNode.Parse(SmithyExampleDocument(
            """[{"title":"failure","error":{"shapeId":"example#Failure"}}]"""))!;
        var operation = document["shapes"]!["example#Get"]!;
        if (emptyErrors) { operation["errors"] = new JsonArray(); }
        else { operation.AsObject().Remove("errors"); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.example_error");
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Get/traits/smithy.api#examples/0/error/shapeId");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("null")]
    [Arguments("false")]
    [Arguments("42")]
    [Arguments("\"text\"")]
    [Arguments("[]")]
    [Arguments("""{"target":"example#Missing","$ref":"https://must-not-load.example/schema","traits":{"smithy.api#required":false}}""")]
    public async Task SmithyJsonExamplePayloadsRemainLiteralWithoutAcquisitionOrInstanceValidation(string literal)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse(
            """
            [{"title":"output","input":LITERAL,"output":LITERAL},
             {"title":"error","error":{"shapeId":"example#Failure","content":LITERAL}}]
            """
                .Replace("LITERAL", literal, StringComparison.Ordinal));
        document["shapes"]!["example#Unused"] = JsonNode.Parse(
            """{"type":"operation","traits":{"smithy.api#examples":[{"title":null}]}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Example payloads are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"throttling":false}""")]
    [Arguments("""{"throttling":true}""")]
    public async Task SmithyJsonRetryableErrorsRetainOptionalBooleanThrottling(string trait)
    {
        foreach (var category in new[] { "client", "server" })
        {
            var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
            var traits = document["shapes"]!["example#Failure"]!["traits"]!;
            traits["smithy.api#retryable"] = JsonNode.Parse(trait);
            traits["smithy.api#error"] = category;
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
    }

    [Test]
    [Arguments("null", "")]
    [Arguments("true", "")]
    [Arguments("[]", "")]
    [Arguments("\"retry\"", "")]
    [Arguments("""{"throttling":null}""", "/throttling")]
    [Arguments("""{"throttling":1}""", "/throttling")]
    [Arguments("""{"throttling":"true"}""", "/throttling")]
    [Arguments("""{"throttling":{}}""", "/throttling")]
    public async Task SmithyJsonRetryableRejectsMalformedTraitValues(string trait, string suffix)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Failure"]!["traits"]!["smithy.api#retryable"] = JsonNode.Parse(trait);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Failure/traits/smithy.api#retryable" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("operation")]
    [Arguments("unmarkedStructure")]
    [Arguments("string")]
    [Arguments("member")]
    [Arguments("listMember")]
    public async Task SmithyJsonRetryableRequiresAnErrorStructure(string position)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        var selected = position switch
        {
            "operation" => document["shapes"]!["example#Get"]!,
            "unmarkedStructure" => document["shapes"]!["example#Input"]!,
            "member" => document["shapes"]!["example#Input"]!["members"]!["value"]!,
            "listMember" => document["shapes"]!["example#Values"]!["member"]!,
            _ => document["shapes"]!["example#Values"]!
        };
        if (position == "string")
        {
            selected.AsObject().Remove("member");
            selected["type"] = "string";
        }
        selected["traits"] ??= new JsonObject();
        selected["traits"]!["smithy.api#retryable"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("smithy.api#String", null, true)]
    [Arguments("example#Token", """{"type":"string"}""", true)]
    [Arguments("example#Token", """{"type":"enum","members":{"A":{"target":"smithy.api#Unit"}}}""", true)]
    [Arguments("smithy.api#Integer", null, false)]
    [Arguments("example#Token", """{"type":"blob"}""", false)]
    [Arguments("example#Token", """{"type":"structure"}""", false)]
    [Arguments("example#Token", """{"type":"list","member":{"target":"smithy.api#String"}}""", false)]
    public async Task SmithyJsonIdempotencyTokenRequiresAStringTarget(string target, string? definition, bool valid)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        var member = document["shapes"]!["example#Input"]!["members"]!["value"]!;
        member["target"] = target;
        member["traits"] = JsonNode.Parse("""{"smithy.api#idempotencyToken":{}}""");
        if (definition is not null) { document["shapes"]![target] = JsonNode.Parse(definition); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Diagnostic.Path)
                .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#idempotencyToken");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("[]")]
    [Arguments("\"token\"")]
    public async Task SmithyJsonIdempotencyTokenRequiresAnObjectAnnotation(string trait)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#idempotencyToken"] =
            JsonNode.Parse(trait);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Input/members/value/traits/smithy.api#idempotencyToken");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("operation")]
    [Arguments("root")]
    [Arguments("listMember")]
    [Arguments("unionMember")]
    [Arguments("mapKey")]
    [Arguments("mapValue")]
    public async Task SmithyJsonIdempotencyTokenRequiresAStructureMember(string position)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        var selected = position == "operation" ? document["shapes"]!["example#Get"]! :
            position == "root" ? document["shapes"]!["example#Input"]! :
            document["shapes"]!["example#Values"]!["member"]!;
        if (position is "unionMember" or "mapKey" or "mapValue")
        {
            document["shapes"]!["example#Values"] = JsonNode.Parse(position == "unionMember"
                ? """{"type":"union","members":{"a":{"target":"smithy.api#String"}}}"""
                : """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}""");
            selected = position == "unionMember" ? document["shapes"]!["example#Values"]!["members"]!["a"]! :
                document["shapes"]!["example#Values"]![position == "mapKey" ? "key" : "value"]!;
        }
        selected["traits"] ??= new JsonObject();
        selected["traits"]!["smithy.api#idempotencyToken"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonIdempotencyTokensAreExclusivePerStructureWithoutSharingTargetState()
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Input"]!["members"] = JsonNode.Parse("""
            {"a":{"target":"smithy.api#String","traits":{"smithy.api#idempotencyToken":{}}},
             "b":{"target":"smithy.api#String"},"nested":{"target":"example#Nested"}}
            """);
        document["shapes"]!["example#Nested"] = JsonNode.Parse("""
            {"type":"structure","members":{"token":{"target":"smithy.api#String","traits":{"smithy.api#idempotencyToken":{}}}}}
            """);
        var oneEach = ResolveSmithy(document.ToJsonString());
        await Assert.That(oneEach.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(oneEach.Diagnostic).IsNull();
        document["shapes"]!["example#Input"]!["members"]!["b"]!["traits"] =
            JsonNode.Parse("""{"smithy.api#idempotencyToken":{}}""");
        var multiple = ResolveSmithy(document.ToJsonString());
        await Assert.That(multiple.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(multiple.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
        await Assert.That(multiple.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/b");
        await Assert.That(multiple.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonIdempotencyTokenMissingTargetsRemainUnresolvedWithoutAcquisition()
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["target"] = "example#Missing";
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"] =
            JsonNode.Parse("""{"smithy.api#idempotencyToken":{}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Shape IDs are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Missing");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonRetryTraitsRetainNativeMetadataAndUnrelatedClosureBoundaries()
    {
        var document = JsonNode.Parse(SmithyExampleDocument(
            """[{"title":"literal","input":{"smithy.api#retryable":null,"smithy.api#idempotencyToken":false}}]"""))!;
        document["shapes"]!["example#Failure"]!["traits"]!["smithy.api#retryable"] =
            JsonNode.Parse("""{"throttling":true}""");
        document["shapes"]!["example#Values"]!["member"]!["target"] = "example#Nested";
        document["shapes"]!["example#Nested"] = JsonNode.Parse("""
            {"type":"structure","members":{"token":{"target":"smithy.api#String","traits":{"smithy.api#idempotencyToken":{}}}}}
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"structure","traits":{"smithy.api#retryable":false,"smithy.api#idempotencyToken":[]}}
            """);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Retry annotations are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("structure", "\"\"", true)]
    [Arguments("union", "\"wire-name\"", true)]
    [Arguments("structure", "\"wire/name~雪\"", true)]
    [Arguments("structure", "null", false)]
    [Arguments("union", "1", false)]
    [Arguments("structure", "{}", false)]
    [Arguments("union", "[]", false)]
    public async Task SmithyJsonJsonNameRetainsMemberStringValues(string kind, string value, bool valid)
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("string", "null", false))!;
        document["shapes"]!["example#Value"] = JsonNode.Parse(
            """{"type":"KIND","members":{"a":{"target":"smithy.api#String","traits":{"smithy.api#jsonName":VALUE}}}}"""
                .Replace("KIND", kind, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal));
        document["shapes"]!["example#Input"]!["members"]!["value"]!.AsObject().Remove("traits");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Value/members/a/traits/smithy.api#jsonName");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure", "same", "same", false)]
    [Arguments("union", "", "", false)]
    [Arguments("structure", "Name", "name", true)]
    [Arguments("union", "é", "e\u0301", true)]
    [Arguments("structure", "a/b~c", "a/b~c", false)]
    public async Task SmithyJsonJsonNamesAreExactAndUniqueWithinTheirContainer(
        string kind, string first, string second, bool valid)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Values"] = new JsonObject
        {
            ["type"] = kind,
            ["members"] = new JsonObject
            {
                ["a"] = new JsonObject { ["target"] = "smithy.api#String",
                    ["traits"] = new JsonObject { ["smithy.api#jsonName"] = first } },
                ["b"] = new JsonObject { ["target"] = "smithy.api#String",
                    ["traits"] = new JsonObject { ["smithy.api#jsonName"] = second } }
            }
        };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.json_name");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Values/members/b/traits/smithy.api#jsonName");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("operation")]
    [Arguments("root")]
    [Arguments("listMember")]
    [Arguments("mapKey")]
    [Arguments("mapValue")]
    [Arguments("enumMember")]
    public async Task SmithyJsonJsonNameRequiresAStructureOrUnionMember(string position)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        var selected = position == "operation" ? document["shapes"]!["example#Get"]! :
            position == "root" ? document["shapes"]!["example#Input"]! :
            document["shapes"]!["example#Values"]!["member"]!;
        if (position is "mapKey" or "mapValue" or "enumMember")
        {
            document["shapes"]!["example#Values"] = JsonNode.Parse(position == "enumMember"
                ? """{"type":"enum","members":{"a":{"target":"smithy.api#Unit"}}}"""
                : """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}""");
            selected = position == "enumMember" ? document["shapes"]!["example#Values"]!["members"]!["a"]! :
                document["shapes"]!["example#Values"]![position == "mapKey" ? "key" : "value"]!;
        }
        selected["traits"] ??= new JsonObject();
        selected["traits"]!["smithy.api#jsonName"] = "wire";
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("date-time")]
    [Arguments("http-date")]
    [Arguments("epoch-seconds")]
    public async Task SmithyJsonTimestampFormatRetainsRootAndAggregateMemberDeclarations(string format)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Input"]!["members"] = JsonNode.Parse("""
            {"plain":{"target":"example#Time"},"list":{"target":"example#Times"},"map":{"target":"example#TimeMap"},
             "union":{"target":"example#TimeChoice"},"implicit":{"target":"smithy.api#Timestamp"}}
            """);
        document["shapes"]!["example#Time"] = new JsonObject { ["type"] = "timestamp",
            ["traits"] = new JsonObject { ["smithy.api#timestampFormat"] = format } };
        document["shapes"]!["example#Times"] = JsonNode.Parse("""{"type":"list","member":{"target":"example#Time"}}""");
        document["shapes"]!["example#TimeMap"] = JsonNode.Parse(
            """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Time"}}""");
        document["shapes"]!["example#TimeChoice"] = JsonNode.Parse(
            """{"type":"union","members":{"time":{"target":"example#Time"}}}""");
        foreach (var member in new[]
        {
            document["shapes"]!["example#Input"]!["members"]!["plain"]!,
            document["shapes"]!["example#Input"]!["members"]!["implicit"]!,
            document["shapes"]!["example#Times"]!["member"]!,
            document["shapes"]!["example#TimeMap"]!["value"]!,
            document["shapes"]!["example#TimeChoice"]!["members"]!["time"]!
        })
        {
            member["traits"] = new JsonObject { ["smithy.api#timestampFormat"] = format };
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("\"DATE-TIME\"")]
    [Arguments("\"rfc3339\"")]
    [Arguments("\"\"")]
    [Arguments("null")]
    [Arguments("{}")]
    [Arguments("1")]
    [Arguments("true")]
    public async Task SmithyJsonTimestampFormatRejectsUnrecognizedOrNonStringValues(string value)
    {
        foreach (var onMember in new[] { false, true })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument("timestamp", "0", false))!;
            var selected = onMember ? document["shapes"]!["example#Input"]!["members"]!["value"]! :
                document["shapes"]!["example#Value"]!;
            selected["traits"] ??= new JsonObject();
            selected["traits"]!["smithy.api#timestampFormat"] = JsonNode.Parse(value);
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
            await Assert.That(result.Diagnostic.Path).IsEqualTo((onMember
                ? "$/shapes/example#Input/members/value" : "$/shapes/example#Value") + "/traits/smithy.api#timestampFormat");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("operation")]
    [Arguments("stringRoot")]
    [Arguments("structureRoot")]
    [Arguments("stringMember")]
    [Arguments("mapKey")]
    public async Task SmithyJsonTimestampFormatRequiresATimestampTarget(string position)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        var selected = position == "operation" ? document["shapes"]!["example#Get"]! :
            position == "structureRoot" ? document["shapes"]!["example#Input"]! :
            document["shapes"]!["example#Values"]!;
        if (position == "stringRoot")
        {
            selected.AsObject().Remove("member");
            selected["type"] = "string";
        }
        if (position == "stringMember") { selected = document["shapes"]!["example#Values"]!["member"]!; }
        if (position == "mapKey")
        {
            document["shapes"]!["example#Values"] = JsonNode.Parse(
                """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#Timestamp"}}""");
            selected = document["shapes"]!["example#Values"]!["key"]!;
        }
        selected["traits"] ??= new JsonObject();
        selected["traits"]!["smithy.api#timestampFormat"] = "date-time";
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("date-time")]
    [Arguments("http-date")]
    [Arguments("epoch-seconds")]
    public async Task SmithyJsonTimestampFormatDoesNotRedefineDefaultNodeRepresentation(string format)
    {
        foreach (var value in new[] { "0", "\"1970-01-01T00:00:00Z\"" })
        {
            var document = JsonNode.Parse(SmithyDefaultDocument("timestamp", value, true))!;
            document["shapes"]!["example#Value"]!["traits"]!["smithy.api#timestampFormat"] = format;
            document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#timestampFormat"] =
                format == "http-date" ? "epoch-seconds" : "http-date";
            var result = ResolveSmithy(document.ToJsonString());
            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
            await Assert.That(result.Diagnostic).IsNull();
        }
    }

    [Test]
    public async Task SmithyJsonSerializationAnnotationsPreserveNativeMetadataWithoutAcquisitionOrUnrelatedScanning()
    {
        var document = JsonNode.Parse(SmithyExampleDocument(
            """[{"title":"literal","input":{"smithy.api#jsonName":false,"smithy.api#timestampFormat":"not-a-format"}}]"""))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"] =
            JsonNode.Parse("""{"smithy.api#jsonName":"https://must-not-load.example/schema"}""");
        document["shapes"]!["example#Values"]!["member"]!["target"] = "example#Time";
        document["shapes"]!["example#Time"] = JsonNode.Parse(
            """{"type":"timestamp","traits":{"smithy.api#timestampFormat":"epoch-seconds"}}""");
        document["shapes"]!["example#Unused"] = JsonNode.Parse(
            """{"type":"string","traits":{"smithy.api#jsonName":null,"smithy.api#timestampFormat":null}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Serialization annotations are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    public async Task SmithyJsonTimestampFormatMissingTargetsRemainUnresolved()
    {
        var document = JsonNode.Parse(SmithyDefaultDocument("timestamp", "0", false))!;
        document["shapes"]!.AsObject().Remove("example#Value");
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"]!["smithy.api#timestampFormat"] = "date-time";
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Value");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonExplicitJsonNamesAreScopedToEachStructureOrUnion()
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Input"]!["members"]!["value"]!["traits"] =
            JsonNode.Parse("""{"smithy.api#jsonName":"wire"}""");
        document["shapes"]!["example#Values"] = JsonNode.Parse("""
            {"type":"union","members":{"a":{"target":"smithy.api#String","traits":{"smithy.api#jsonName":"wire"}}}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("1", true)]
    [Arguments("1.0", true)]
    [Arguments("10e-1", true)]
    [Arguments("70000", true)]
    [Arguments("2147483647", true)]
    [Arguments("214748364700e-2", true)]
    [Arguments("0", false)]
    [Arguments("-0.000", false)]
    [Arguments("-1", false)]
    [Arguments("0.999999999999999999999", false)]
    [Arguments("1.000000000000000000001", false)]
    [Arguments("2147483648", false)]
    [Arguments("2147483647.000000000001", false)]
    [Arguments("1e1000", false)]
    [Arguments("1e-1000", false)]
    public async Task SmithyJsonLongPollChecksExactPositiveIntegerTimeoutBoundaries(string value, bool valid)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#longPoll"] =
            new JsonObject { ["timeoutMillis"] = JsonNode.Parse(value) };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.long_poll_timeout");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#longPoll/timeoutMillis");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("null", "", "document.trait_value")]
    [Arguments("true", "", "document.trait_value")]
    [Arguments("[]", "", "document.trait_value")]
    [Arguments("\"poll\"", "", "document.trait_value")]
    [Arguments("{}", "/timeoutMillis", "document.long_poll_timeout")]
    [Arguments("""{"timeoutMillis":null}""", "/timeoutMillis", "document.long_poll_timeout")]
    [Arguments("""{"timeoutMillis":false}""", "/timeoutMillis", "document.long_poll_timeout")]
    [Arguments("""{"timeoutMillis":"70000"}""", "/timeoutMillis", "document.long_poll_timeout")]
    [Arguments("""{"timeoutMillis":[]}""", "/timeoutMillis", "document.long_poll_timeout")]
    [Arguments("""{"timeoutMillis":{}}""", "/timeoutMillis", "document.long_poll_timeout")]
    public async Task SmithyJsonLongPollRequiresAnObjectAndNumericTimeout(string trait, string suffix, string code)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#longPoll"] = JsonNode.Parse(trait);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#longPoll" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure")]
    [Arguments("list")]
    [Arguments("error")]
    [Arguments("member")]
    [Arguments("listMember")]
    public async Task SmithyJsonLongPollRequiresAnOperation(string position)
    {
        var document = JsonNode.Parse(SmithyExampleDocument("[]"))!;
        var selected = position switch
        {
            "structure" => document["shapes"]!["example#Input"]!,
            "list" => document["shapes"]!["example#Values"]!,
            "error" => document["shapes"]!["example#Failure"]!,
            "member" => document["shapes"]!["example#Input"]!["members"]!["value"]!,
            _ => document["shapes"]!["example#Values"]!["member"]!
        };
        selected["traits"] ??= new JsonObject();
        selected["traits"]!["smithy.api#longPoll"] = new JsonObject { ["timeoutMillis"] = 1 };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonLongPollPreservesNativeMetadataWithoutAcquisitionOrUnrelatedScanning()
    {
        var document = JsonNode.Parse(SmithyExampleDocument(
            """[{"title":"literal","input":{"smithy.api#longPoll":{"timeoutMillis":false},"target":"https://must-not-load.example/schema"}}]"""))!;
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#longPoll"] =
            new JsonObject { ["timeoutMillis"] = 70000 };
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#readonly"] = new JsonObject();
        document["shapes"]!["example#Unused"] = JsonNode.Parse(
            """{"type":"operation","traits":{"smithy.api#longPoll":{"timeoutMillis":0}}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Long-poll declarations are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SmithyJsonStreamingBlobsSupportTopLevelInputAndOutputMembers(bool output, bool useDefault)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        if (output)
        {
            document["shapes"]!["example#Get"]!.AsObject().Remove("input");
            document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Input"}""");
        }
        if (useDefault)
        {
            document["shapes"]!["example#Input"]!["members"]!["data"]!["traits"] =
                JsonNode.Parse("""{"smithy.api#default":""}""");
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    public async Task SmithyJsonStreamingBlobMembersRequireExplicitRequiredOrDefault()
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Input"]!["members"]!["data"]!.AsObject().Remove("traits");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.streaming_member");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/data");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("input", true)]
    [Arguments("output", false)]
    [Arguments("both", false)]
    public async Task SmithyJsonRequiresLengthRestrictsStreamingBlobsToInputs(string slot, bool valid)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Stream"]!["traits"]!["smithy.api#requiresLength"] = new JsonObject();
        if (slot != "input")
        {
            document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Input"}""");
            if (slot == "output") { document["shapes"]!["example#Get"]!.AsObject().Remove("input"); }
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.streaming_usage");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/data");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure", """{"type":"structure","members":{"data":{"target":"example#Stream","traits":{"smithy.api#required":{}}}}}""")]
    [Arguments("union", """{"type":"union","members":{"data":{"target":"example#Stream"}}}""")]
    [Arguments("list", """{"type":"list","member":{"target":"example#Stream"}}""")]
    [Arguments("map", """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Stream"}}""")]
    public async Task SmithyJsonStreamingShapesRejectNonTopLevelAggregateReferences(string kind, string container)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Other"] = JsonNode.Parse(container);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.streaming_usage");
        await Assert.That(result.Diagnostic.Path).IsEqualTo(kind is "structure" or "union"
            ? "$/shapes/example#Other/members/data" : "$/shapes/example#Other/" + (kind == "list" ? "member" : "value"));
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"type":"structure","members":{"nested":{"target":"example#Input"}}}""", "/members/nested")]
    [Arguments("""{"type":"union","members":{"nested":{"target":"example#Input"}}}""", "/members/nested")]
    [Arguments("""{"type":"list","member":{"target":"example#Input"}}""", "/member")]
    [Arguments("""{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Input"}}""", "/value")]
    public async Task SmithyJsonStreamingContainersCannotBeNestedInOtherMembers(string container, string suffix)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Other"] = JsonNode.Parse(container);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.streaming_usage");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Other" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonStreamingTargetsAreExclusiveWithinEachStructure(bool distinctTarget)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        if (distinctTarget)
        {
            document["shapes"]!["example#OtherStream"] = JsonNode.Parse(
                """{"type":"blob","traits":{"smithy.api#streaming":{}}}""");
        }
        document["shapes"]!["example#Input"]!["members"]!["second"] = JsonNode.Parse(
            """{"target":"TARGET","traits":{"smithy.api#required":{}}}"""
                .Replace("TARGET", distinctTarget ? "example#OtherStream" : "example#Stream", StringComparison.Ordinal));
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/second");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure", true)]
    [Arguments("error", true)]
    [Arguments("string", false)]
    [Arguments("blob", false)]
    [Arguments("union", false)]
    public async Task SmithyJsonEventStreamsRequireStructureAlternatives(string kind, bool valid)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Stream"] = JsonNode.Parse(
            """{"type":"union","traits":{"smithy.api#streaming":{}},"members":{"event":{"target":"example#Event"}}}""");
        document["shapes"]!["example#Input"]!["members"]!["data"]!.AsObject().Remove("traits");
        document["shapes"]!["example#Event"] = JsonNode.Parse(kind switch
        {
            "error" => """{"type":"structure","traits":{"smithy.api#error":"server"}}""",
            "union" => """{"type":"union","members":{"done":{"target":"smithy.api#Unit"}}}""",
            _ => """{"type":"KIND"}""".Replace("KIND", kind, StringComparison.Ordinal)
        });
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.target_type");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("streaming", "string", true)]
    [Arguments("streaming", "structure", true)]
    [Arguments("streaming", "blob", false)]
    [Arguments("requiresLength", "blob", true)]
    [Arguments("requiresLength", "union", true)]
    public async Task SmithyJsonStreamingAnnotationsRejectIneligibleShapes(string trait, string kind, bool root)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        var selected = root ? document["shapes"]!["example#Stream"]! :
            document["shapes"]!["example#Input"]!["members"]!["data"]!;
        if (root)
        {
            selected.AsObject().Clear();
            selected["type"] = kind;
        }
        selected["traits"] ??= new JsonObject();
        selected["traits"]!["smithy.api#" + trait] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("streaming", "null")]
    [Arguments("streaming", "true")]
    [Arguments("streaming", "[]")]
    [Arguments("requiresLength", "null")]
    [Arguments("requiresLength", "\"known\"")]
    public async Task SmithyJsonStreamingAnnotationsRequireObjectValues(string trait, string value)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Stream"]!["traits"]!["smithy.api#" + trait] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Stream/traits/smithy.api#" + trait);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonStreamingPreservesNativeMetadataAndIgnoresUnrelatedStreamsAndLiteralTargets()
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#examples":[{"title":"literal","input":{"target":"example#Input","smithy.api#streaming":false}}]}
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse(
            """{"type":"blob","traits":{"smithy.api#streaming":false,"smithy.api#requiresLength":[]}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Streaming shapes are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    public async Task SmithyJsonStreamingExclusivityIsIndependentForSeparateOperationStructures()
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Output"}""");
        document["shapes"]!["example#Output"] = document["shapes"]!["example#Input"]!.DeepClone();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonStreamingInboundExclusivityCountsUnvisitedSiblingTargets()
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Other"] = JsonNode.Parse("""
            {"type":"structure","members":{
              "first":{"target":"example#Stream","traits":{"smithy.api#required":{}}},
              "second":{"target":"example#OtherStream","traits":{"smithy.api#required":{}}}}}
            """);
        document["shapes"]!["example#OtherStream"] = JsonNode.Parse(
            """{"type":"blob","traits":{"smithy.api#streaming":{}}}""");
        document["shapes"]!["example#OtherOperation"] = JsonNode.Parse(
            """{"type":"operation","input":{"target":"example#Other"}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Other/members/second");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonEventStreamsReportMissingAlternativesWithoutAcquisition()
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Stream"] = JsonNode.Parse(
            """{"type":"union","traits":{"smithy.api#streaming":{}},"members":{"event":{"target":"example#Missing"}}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Shape IDs are not acquisition URLs."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Missing");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""["gzip"]""")]
    [Arguments("""["GZIP"]""")]
    [Arguments("""["GzIp","gzip"]""")]
    public async Task SmithyJsonRequestCompressionAcceptsCaseInsensitiveGzipWithoutRewriting(string encodings)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse(
            """{"smithy.api#requestCompression":{"encodings":ENCODINGS}}""".Replace("ENCODINGS", encodings, StringComparison.Ordinal));
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("""{}""", "/encodings", "document.request_compression")]
    [Arguments("""{"encodings":null}""", "/encodings", "document.request_compression")]
    [Arguments("""{"encodings":"gzip"}""", "/encodings", "document.request_compression")]
    [Arguments("""{"encodings":[]}""", "/encodings", "document.request_compression")]
    [Arguments("""{"encodings":[null]}""", "/encodings/0", "document.request_compression")]
    [Arguments("""{"encodings":["gzip",1]}""", "/encodings/1", "document.request_compression")]
    [Arguments("""{"encodings":["gzip","deflate"]}""", "/encodings/1", "document.request_compression")]
    [Arguments("""{"encodings":[" gzip"]}""", "/encodings/0", "document.request_compression")]
    [Arguments("""{"encodings":[""]}""", "/encodings/0", "document.request_compression")]
    [Arguments("null", "", "document.trait_value")]
    [Arguments("[]", "", "document.trait_value")]
    public async Task SmithyJsonRequestCompressionChecksRequiredEncodingsAndNodeTypes(string annotation, string suffix, string code)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Get"]!["traits"] = new JsonObject
        {
            ["smithy.api#requestCompression"] = JsonNode.Parse(annotation)
        };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#requestCompression" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonRequestCompressionCannotAnnotateShapesOrMembers(bool member)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        var target = member ? document["shapes"]!["example#Input"]!["members"]!["data"]! :
            document["shapes"]!["example#Stream"]!;
        target["traits"]!["smithy.api#requestCompression"] = JsonNode.Parse("""{"encodings":["gzip"]}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonRequestCompressionRejectsLengthRequiringStreamingInputs()
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""{"smithy.api#requestCompression":{"encodings":["gzip"]}}""");
        document["shapes"]!["example#Stream"]!["traits"]!["smithy.api#requiresLength"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.request_compression");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#requestCompression");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("absent")]
    [Arguments("empty")]
    [Arguments("implicit")]
    public async Task SmithyJsonRequestCompressionAllowsAbsentOrNonStreamingInputs(string input)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""{"smithy.api#requestCompression":{"encodings":["gzip"]}}""");
        if (input == "absent") { document["shapes"]!["example#Get"]!.AsObject().Remove("input"); }
        else if (input == "empty") { document["shapes"]!["example#Input"]!.AsObject().Remove("members"); }
        else { document["shapes"]!["example#Input"]!["members"]!["data"]!["target"] = "smithy.api#String"; }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("eventPayload", "blob", true)]
    [Arguments("eventPayload", "string", true)]
    [Arguments("eventPayload", "enum", true)]
    [Arguments("eventPayload", "structure", true)]
    [Arguments("eventPayload", "union", true)]
    [Arguments("eventPayload", "boolean", false)]
    [Arguments("eventPayload", "integer", false)]
    [Arguments("eventPayload", "timestamp", false)]
    [Arguments("eventPayload", "list", false)]
    [Arguments("eventPayload", "map", false)]
    [Arguments("eventPayload", "document", false)]
    [Arguments("eventHeader", "boolean", true)]
    [Arguments("eventHeader", "byte", true)]
    [Arguments("eventHeader", "short", true)]
    [Arguments("eventHeader", "integer", true)]
    [Arguments("eventHeader", "intEnum", true)]
    [Arguments("eventHeader", "long", true)]
    [Arguments("eventHeader", "blob", true)]
    [Arguments("eventHeader", "string", true)]
    [Arguments("eventHeader", "enum", true)]
    [Arguments("eventHeader", "timestamp", true)]
    [Arguments("eventHeader", "float", false)]
    [Arguments("eventHeader", "double", false)]
    [Arguments("eventHeader", "bigInteger", false)]
    [Arguments("eventHeader", "bigDecimal", false)]
    [Arguments("eventHeader", "structure", false)]
    [Arguments("eventHeader", "union", false)]
    [Arguments("eventHeader", "list", false)]
    [Arguments("eventHeader", "map", false)]
    [Arguments("eventHeader", "document", false)]
    public async Task SmithyJsonEventBindingTargetsMatchPreludeSelectors(string trait, string kind, bool valid)
    {
        var document = SmithyEventBindingDocument(trait);
        document["shapes"]!["example#Target"] = JsonNode.Parse(kind switch
        {
            "enum" => """{"type":"enum","members":{"one":{"target":"smithy.api#Unit"}}}""",
            "intEnum" => """{"type":"intEnum","members":{"one":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""",
            "union" => """{"type":"union","members":{"one":{"target":"smithy.api#Unit"}}}""",
            "list" => """{"type":"list","member":{"target":"smithy.api#String"}}""",
            "map" => """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}""",
            _ => """{"type":"KIND"}""".Replace("KIND", kind, StringComparison.Ordinal)
        });
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/data/traits/smithy.api#" + trait);
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("eventPayload", "root")]
    [Arguments("eventPayload", "union")]
    [Arguments("eventPayload", "list")]
    [Arguments("eventPayload", "map")]
    [Arguments("eventHeader", "root")]
    [Arguments("eventHeader", "union")]
    [Arguments("eventHeader", "list")]
    [Arguments("eventHeader", "map")]
    public async Task SmithyJsonEventBindingsRequireStructureMembers(string trait, string container)
    {
        var document = SmithyEventBindingDocument(trait);
        var member = document["shapes"]!["example#Event"]!["members"]!["data"]!.DeepClone();
        if (container == "root")
        {
            document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!.AsObject().Clear();
            document["shapes"]!["example#Target"]!["traits"] = member["traits"]!.DeepClone();
        }
        else
        {
            var aggregate = new JsonObject { ["type"] = container };
            if (container == "union") { aggregate["members"] = new JsonObject { ["data"] = member }; }
            else if (container == "list") { aggregate["member"] = member; }
            else
            {
                aggregate["key"] = JsonNode.Parse("""{"target":"smithy.api#String"}""");
                aggregate["value"] = member;
            }
            document["shapes"]!["example#Event"] = aggregate;
            document["shapes"]!["example#Stream"]!["traits"]!.AsObject().Clear();
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("eventPayload", "null")]
    [Arguments("eventPayload", "true")]
    [Arguments("eventPayload", "[]")]
    [Arguments("eventHeader", "null")]
    [Arguments("eventHeader", "\"text\"")]
    [Arguments("eventHeader", "[]")]
    public async Task SmithyJsonEventBindingsRequireObjectAnnotations(string trait, string annotation)
    {
        var document = SmithyEventBindingDocument(trait);
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#" + trait] = JsonNode.Parse(annotation);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/data/traits/smithy.api#" + trait);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("eventPayload", "eventHeader")]
    [Arguments("eventHeader", "eventPayload")]
    public async Task SmithyJsonEventPayloadAndHeaderConflictInEitherPropertyOrder(string first, string second)
    {
        var document = SmithyEventBindingDocument(first);
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#" + second] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/data/traits/smithy.api#" + first);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonEventPayloadIsStructurallyExclusiveByMember(bool distinctTarget)
    {
        var document = SmithyEventBindingDocument("eventPayload");
        var second = document["shapes"]!["example#Event"]!["members"]!["data"]!.DeepClone();
        if (distinctTarget) { second["target"] = "smithy.api#String"; }
        document["shapes"]!["example#Event"]!["members"]!["second"] = second;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/second");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task SmithyJsonExplicitEventPayloadRequiresHeadersOnEveryOtherMember(bool payloadLast, bool marked)
    {
        var document = SmithyEventBindingDocument("eventPayload");
        var members = document["shapes"]!["example#Event"]!["members"]!.AsObject();
        var payload = members["data"]!.DeepClone();
        if (payloadLast) { members.Remove("data"); }
        members["other"] = JsonNode.Parse(marked
            ? """{"target":"smithy.api#String","traits":{"smithy.api#eventHeader":{}}}"""
            : """{"target":"smithy.api#String"}""");
        if (payloadLast) { members["data"] = payload; }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(marked
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (marked) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.event_binding");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/other");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonEventHeadersPermitImplicitPayloadMembersAndMultipleHeaders()
    {
        var document = SmithyEventBindingDocument("eventHeader");
        document["shapes"]!["example#Event"]!["members"]!["second"] =
            document["shapes"]!["example#Event"]!["members"]!["data"]!.DeepClone();
        document["shapes"]!["example#Event"]!["members"]!["body"] = JsonNode.Parse("""{"target":"smithy.api#String"}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonEventPayloadExclusivityIsScopedToEachEventStructure()
    {
        var document = SmithyEventBindingDocument("eventPayload");
        document["shapes"]!["example#OtherEvent"] = document["shapes"]!["example#Event"]!.DeepClone();
        document["shapes"]!["example#Stream"]!["members"]!["other"] = JsonNode.Parse("""{"target":"example#OtherEvent"}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("eventPayload")]
    [Arguments("eventHeader")]
    public async Task SmithyJsonEventBindingMissingTargetsRemainUnresolvedWithoutAcquisition(string trait)
    {
        var document = SmithyEventBindingDocument(trait);
        document["shapes"]!.AsObject().Remove("example#Target");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Event shape IDs are not acquisition URLs."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonEventBindingsPreserveNativeMetadataAndLiteralUnrelatedBoundaries(bool eventStream)
    {
        var document = SmithyEventBindingDocument("eventPayload");
        if (!eventStream) { document["shapes"]!["example#Stream"]!["traits"]!.AsObject().Clear(); }
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse(
            """{"smithy.api#examples":[{"title":"literal","input":{"smithy.api#eventPayload":false,"target":"example#Missing"}}]}""");
        document["shapes"]!["example#Unused"] = JsonNode.Parse(
            """{"type":"structure","members":{"data":{"target":"smithy.api#Float","traits":{"smithy.api#eventHeader":[]}}}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("a", true)]
    [Arguments("_", true)]
    [Arguments("_A-z_09", true)]
    [Arguments("p:Name-09", true)]
    [Arguments("_:_", true)]
    [Arguments("", false)]
    [Arguments("0name", false)]
    [Arguments("-name", false)]
    [Arguments("name.part", false)]
    [Arguments("name ", false)]
    [Arguments(" name", false)]
    [Arguments("a\n", false)]
    [Arguments(":name", false)]
    [Arguments("name:", false)]
    [Arguments("p:0name", false)]
    [Arguments("p:x:y", false)]
    [Arguments("caf\u00e9", false)]
    [Arguments("\u03b1", false)]
    public async Task SmithyJsonXmlNamesFollowThePublisherAsciiGrammar(string name, bool valid)
    {
        var document = SmithyEventBindingDocument("xmlName");
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#xmlName"] = name;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.xml_name");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/data/traits/smithy.api#xmlName");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure", true)]
    [Arguments("union", true)]
    [Arguments("string", false)]
    [Arguments("blob", false)]
    [Arguments("list", false)]
    [Arguments("map", false)]
    [Arguments("enum", false)]
    [Arguments("intEnum", false)]
    [Arguments("operation", false)]
    public async Task SmithyJsonXmlNamesRestrictRootShapeEligibility(string kind, bool valid)
    {
        var document = SmithyEventBindingDocument("eventHeader");
        var selected = kind == "operation" ? document["shapes"]!["example#Get"]! :
            document["shapes"]!["example#Target"]!;
        if (kind != "operation") { selected["type"] = kind; }
        selected["traits"] = JsonNode.Parse("""{"smithy.api#xmlName":"p:renamed"}""");
        if (kind == "union") { selected["members"] = JsonNode.Parse("""{"one":{"target":"smithy.api#Unit"}}"""); }
        document["shapes"]!["example#Event"]!["members"]!["data"]!.AsObject().Remove("traits");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure")]
    [Arguments("union")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("enum")]
    [Arguments("intEnum")]
    public async Task SmithyJsonXmlNamesCanAnnotateEveryDataMemberKind(string kind)
    {
        var document = SmithyEventBindingDocument("eventHeader");
        document["shapes"]!["example#Event"]!["members"]!["data"]!.AsObject().Remove("traits");
        var aggregate = JsonNode.Parse(kind switch
        {
            "list" => """{"type":"list","member":{"target":"smithy.api#String"}}""",
            "map" => """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}""",
            "enum" => """{"type":"enum","members":{"one":{"target":"smithy.api#Unit"}}}""",
            "intEnum" => """{"type":"intEnum","members":{"one":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""",
            _ => """{"type":"KIND","members":{"one":{"target":"smithy.api#String"}}}""".Replace("KIND", kind, StringComparison.Ordinal)
        })!;
        var members = kind == "list" ? new[] { aggregate["member"]! } :
            kind == "map" ? new[] { aggregate["key"]!, aggregate["value"]! } : new[] { aggregate["members"]!["one"]! };
        foreach (var member in members)
        {
            member["traits"] ??= new JsonObject();
            member["traits"]!["smithy.api#xmlName"] = "p:Field";
        }
        document["shapes"]!["example#Target"] = aggregate;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("xmlAttribute", "boolean", true)]
    [Arguments("xmlAttribute", "byte", true)]
    [Arguments("xmlAttribute", "short", true)]
    [Arguments("xmlAttribute", "integer", true)]
    [Arguments("xmlAttribute", "long", true)]
    [Arguments("xmlAttribute", "float", true)]
    [Arguments("xmlAttribute", "double", true)]
    [Arguments("xmlAttribute", "bigInteger", true)]
    [Arguments("xmlAttribute", "bigDecimal", true)]
    [Arguments("xmlAttribute", "string", true)]
    [Arguments("xmlAttribute", "enum", true)]
    [Arguments("xmlAttribute", "intEnum", true)]
    [Arguments("xmlAttribute", "timestamp", true)]
    [Arguments("xmlAttribute", "blob", false)]
    [Arguments("xmlAttribute", "document", false)]
    [Arguments("xmlAttribute", "structure", false)]
    [Arguments("xmlAttribute", "union", false)]
    [Arguments("xmlAttribute", "list", false)]
    [Arguments("xmlAttribute", "map", false)]
    [Arguments("xmlFlattened", "list", true)]
    [Arguments("xmlFlattened", "map", true)]
    [Arguments("xmlFlattened", "string", false)]
    [Arguments("xmlFlattened", "blob", false)]
    [Arguments("xmlFlattened", "integer", false)]
    [Arguments("xmlFlattened", "structure", false)]
    [Arguments("xmlFlattened", "union", false)]
    [Arguments("xmlFlattened", "document", false)]
    public async Task SmithyJsonXmlBindingTargetsFollowAttributeAndFlatteningSelectors(string trait, string kind, bool valid)
    {
        var document = SmithyEventBindingDocument(trait);
        document["shapes"]!["example#Target"] = JsonNode.Parse(kind switch
        {
            "enum" => """{"type":"enum","members":{"one":{"target":"smithy.api#Unit"}}}""",
            "intEnum" => """{"type":"intEnum","members":{"one":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""",
            "union" => """{"type":"union","members":{"one":{"target":"smithy.api#Unit"}}}""",
            "list" => """{"type":"list","member":{"target":"smithy.api#String"}}""",
            "map" => """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}""",
            _ => """{"type":"KIND"}""".Replace("KIND", kind, StringComparison.Ordinal)
        });
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/data/traits/smithy.api#" + trait);
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("xmlAttribute", "structure", true)]
    [Arguments("xmlAttribute", "union", false)]
    [Arguments("xmlAttribute", "list", false)]
    [Arguments("xmlAttribute", "map", false)]
    [Arguments("xmlAttribute", "root", false)]
    [Arguments("xmlFlattened", "structure", true)]
    [Arguments("xmlFlattened", "union", true)]
    [Arguments("xmlFlattened", "list", false)]
    [Arguments("xmlFlattened", "map", false)]
    [Arguments("xmlFlattened", "root", false)]
    public async Task SmithyJsonXmlBindingsRestrictTheirContainingShapes(string trait, string container, bool valid)
    {
        var document = SmithyEventBindingDocument(trait);
        document["shapes"]!["example#Stream"]!["traits"]!.AsObject().Clear();
        if (trait == "xmlFlattened")
        { document["shapes"]!["example#Target"] = JsonNode.Parse("""{"type":"list","member":{"target":"smithy.api#String"}}"""); }
        var member = document["shapes"]!["example#Event"]!["members"]!["data"]!.DeepClone();
        if (container == "root")
        {
            document["shapes"]!["example#Event"]!["traits"] = member["traits"]!.DeepClone();
            document["shapes"]!["example#Event"]!["members"]!["data"]!.AsObject().Remove("traits");
        }
        else if (container == "union") { document["shapes"]!["example#Event"]!["type"] = "union"; }
        else if (container == "list")
        { document["shapes"]!["example#Event"] = new JsonObject { ["type"] = "list", ["member"] = member }; }
        else if (container == "map")
        {
            document["shapes"]!["example#Event"] = new JsonObject
            {
                ["type"] = "map", ["key"] = JsonNode.Parse("""{"target":"smithy.api#String"}"""), ["value"] = member
            };
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("xmlName", "null")]
    [Arguments("xmlName", "1")]
    [Arguments("xmlName", "{}")]
    [Arguments("xmlAttribute", "null")]
    [Arguments("xmlAttribute", "true")]
    [Arguments("xmlAttribute", "[]")]
    [Arguments("xmlFlattened", "null")]
    [Arguments("xmlFlattened", "\"true\"")]
    [Arguments("xmlFlattened", "[]")]
    public async Task SmithyJsonXmlBindingAnnotationsRequireTheirDeclaredNodeTypes(string trait, string value)
    {
        var document = SmithyEventBindingDocument(trait);
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#" + trait] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/data/traits/smithy.api#" + trait);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("xmlAttribute", "xmlNamespace")]
    [Arguments("xmlNamespace", "xmlAttribute")]
    public async Task SmithyJsonXmlAttributeAndNamespaceConflictInEitherPropertyOrder(string first, string second)
    {
        var document = SmithyEventBindingDocument(first);
        var traits = document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!;
        traits["smithy.api#" + first] = first == "xmlNamespace" ? JsonNode.Parse("""{"uri":"urn:example"}""") : new JsonObject();
        traits["smithy.api#" + second] = second == "xmlNamespace" ? JsonNode.Parse("""{"uri":"urn:example"}""") : new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/data/traits/smithy.api#" + first);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("xmlName")]
    [Arguments("xmlAttribute")]
    [Arguments("xmlFlattened")]
    public async Task SmithyJsonXmlBindingsPreserveMissingDependencySemantics(string trait)
    {
        var document = SmithyEventBindingDocument(trait);
        if (trait == "xmlName")
        { document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#xmlName"] = "Field"; }
        document["shapes"]!.AsObject().Remove("example#Target");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("XML member targets are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("xmlAttribute", "smithy.api#String")]
    [Arguments("xmlAttribute", "smithy.api#Integer")]
    [Arguments("xmlAttribute", "smithy.api#Timestamp")]
    [Arguments("xmlFlattened", "example#Target")]
    public async Task SmithyJsonXmlBindingsAllowMultipleMembersWithoutInventingNameUniqueness(string trait, string target)
    {
        var document = SmithyEventBindingDocument(trait);
        if (trait == "xmlFlattened")
        { document["shapes"]!["example#Target"] = JsonNode.Parse("""{"type":"list","member":{"target":"smithy.api#String"}}"""); }
        var member = document["shapes"]!["example#Event"]!["members"]!["data"]!;
        member["target"] = target;
        member["traits"]!["smithy.api#xmlName"] = "same";
        document["shapes"]!["example#Event"]!["members"]!["second"] = member.DeepClone();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonXmlDeclarationsPreserveNativeMetadataWithoutSerializingOrScanningUnrelatedShapes()
    {
        var document = SmithyEventBindingDocument("xmlAttribute");
        document["shapes"]!["example#Event"]!["traits"] = JsonNode.Parse("""{"smithy.api#xmlName":"p:Event"}""");
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#xmlName"] = "p:value";
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#examples":[{"title":"literal","input":{"smithy.api#xmlName":"not a name","target":"example#Absent"}}]}
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""{"type":"string","traits":{"smithy.api#xmlName":false}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("urn:example:events", null)]
    [Arguments("https://example.test/Events/%7e?q=Value#Part", "P_ref-09")]
    [Arguments("../events", "_")]
    [Arguments("events", "ns")]
    [Arguments("#fragment", "fragment")]
    [Arguments("#", "emptyFragment")]
    [Arguments("../events?q=One#Part%20Two/?more", "relative")]
    [Arguments("?query#fragment", "query")]
    public async Task SmithyJsonXmlNamespaceRetainsUriReferencesAndOptionalPrefixes(string uri, string? prefix)
    {
        var document = SmithyEventBindingDocument("xmlNamespace");
        var annotation = new JsonObject { ["uri"] = uri };
        if (prefix is not null) { annotation["prefix"] = prefix; }
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#xmlNamespace"] = annotation;
        document["shapes"]!["example#Event"]!["traits"] = new JsonObject
        { ["smithy.api#xmlNamespace"] = annotation.DeepClone() };
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Namespace references are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("null", "", "document.trait_value")]
    [Arguments("[]", "", "document.trait_value")]
    [Arguments("\"urn:example\"", "", "document.trait_value")]
    [Arguments("{}", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":null}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":1}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":""}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":"not a uri"}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":"http://["}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":"https://example.test/%zz"}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":"events#bad fragment"}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":"events#bad%zz"}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":"#one#two"}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":"bad path#fragment"}""", "/uri", "document.xml_namespace")]
    [Arguments("""{"uri":"urn:example","prefix":null}""", "/prefix", "document.xml_namespace")]
    [Arguments("""{"uri":"urn:example","prefix":1}""", "/prefix", "document.xml_namespace")]
    [Arguments("""{"uri":"urn:example","prefix":""}""", "/prefix", "document.xml_namespace")]
    [Arguments("""{"uri":"urn:example","prefix":"0name"}""", "/prefix", "document.xml_namespace")]
    [Arguments("""{"uri":"urn:example","prefix":"a:b"}""", "/prefix", "document.xml_namespace")]
    [Arguments("""{"uri":"urn:example","prefix":"a.b"}""", "/prefix", "document.xml_namespace")]
    [Arguments("""{"uri":"urn:example","prefix":"a "}""", "/prefix", "document.xml_namespace")]
    [Arguments("""{"uri":"urn:example","prefix":"caf\u00e9"}""", "/prefix", "document.xml_namespace")]
    public async Task SmithyJsonXmlNamespaceChecksRequiredUriAndPrefixGrammar(string annotation, string suffix, string code)
    {
        var document = SmithyEventBindingDocument("xmlNamespace");
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#xmlNamespace"] = JsonNode.Parse(annotation);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Event/members/data/traits/smithy.api#xmlNamespace" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("blob")]
    [Arguments("boolean")]
    [Arguments("document")]
    [Arguments("string")]
    [Arguments("byte")]
    [Arguments("short")]
    [Arguments("integer")]
    [Arguments("long")]
    [Arguments("float")]
    [Arguments("double")]
    [Arguments("bigInteger")]
    [Arguments("bigDecimal")]
    [Arguments("timestamp")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("structure")]
    [Arguments("union")]
    [Arguments("enum")]
    [Arguments("intEnum")]
    public async Task SmithyJsonXmlNamespaceSupportsDataRootsAndTheirMembers(string kind)
    {
        var document = SmithyEventBindingDocument("xmlNamespace");
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#xmlNamespace"] =
            JsonNode.Parse("""{"uri":"urn:member","prefix":"m"}""");
        var target = JsonNode.Parse(kind switch
        {
            "list" => """{"type":"list","member":{"target":"smithy.api#String"}}""",
            "map" => """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}""",
            "union" => """{"type":"union","members":{"one":{"target":"smithy.api#Unit"}}}""",
            "enum" => """{"type":"enum","members":{"one":{"target":"smithy.api#Unit"}}}""",
            "intEnum" => """{"type":"intEnum","members":{"one":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""",
            _ => """{"type":"KIND"}""".Replace("KIND", kind, StringComparison.Ordinal)
        })!;
        target["traits"] = JsonNode.Parse("""{"smithy.api#xmlNamespace":{"uri":"urn:root","prefix":"r"}}""");
        IEnumerable<JsonNode> members = kind switch
        {
            "list" => new[] { target["member"]! },
            "map" => new[] { target["key"]!, target["value"]! },
            "union" or "enum" or "intEnum" => new[] { target["members"]!["one"]! },
            _ => []
        };
        foreach (var member in members)
        {
            member["traits"] ??= new JsonObject();
            member["traits"]!["smithy.api#xmlNamespace"] = JsonNode.Parse("""{"uri":"urn:nested"}""");
        }
        document["shapes"]!["example#Target"] = target;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonXmlNamespaceCannotAnnotateAnOperation()
    {
        var document = SmithyEventBindingDocument("eventHeader");
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""{"smithy.api#xmlNamespace":{"uri":"urn:operation"}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#xmlNamespace");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonXmlNamespaceDoesNotReplaceMissingMemberTargetDependencies()
    {
        var document = SmithyEventBindingDocument("xmlNamespace");
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"]!["smithy.api#xmlNamespace"] =
            JsonNode.Parse("""{"uri":"https://example.test/namespace","prefix":"n"}""");
        document["shapes"]!.AsObject().Remove("example#Target");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Namespaces cannot supply missing shape definitions."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonXmlNamespaceDoesNotBindPrefixesOrInterpretLiteralReferences()
    {
        var document = SmithyEventBindingDocument("xmlNamespace");
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"] =
            JsonNode.Parse("""{"smithy.api#xmlNamespace":{"uri":"urn:unfetched","prefix":"declared"},"smithy.api#xmlName":"other:Data"}""");
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#examples":[{"title":"literal","input":{"smithy.api#xmlNamespace":{"uri":null},"target":"example#Absent"}}]}
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse(
            """{"type":"string","traits":{"smithy.api#xmlNamespace":{"prefix":"bad prefix"}}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("application/json")]
    [Arguments("Video/QuickTime")]
    [Arguments("application/vnd.example.events+json")]
    [Arguments("example/x.private")]
    [Arguments("a/0")]
    [Arguments("a!#$&-^_.+/b!#$&-^_.+")]
    [Arguments("text/plain; charset=utf-8")]
    [Arguments("text/plain; note=\"two words\"")]
    public async Task SmithyJsonMediaTypesRetainOpenNamesCasingAndParameters(string mediaType)
    {
        var document = SmithyMediaTypeDocument("string", JsonValue.Create(mediaType));
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("")]
    [Arguments("application")]
    [Arguments("/json")]
    [Arguments("application/")]
    [Arguments("application/json/extra")]
    [Arguments("*/*")]
    [Arguments("application/*")]
    [Arguments("_application/json")]
    [Arguments("application/.json")]
    [Arguments("application/j%son")]
    [Arguments("application/j'son")]
    [Arguments("application/j*son")]
    [Arguments("application/j\u00e9son")]
    [Arguments("application/json\r\nX-Header: value")]
    [Arguments("text/plain; charset")]
    [Arguments("text/plain; charset=")]
    [Arguments("text/plain; note=\"unterminated")]
    public async Task SmithyJsonMediaTypesRejectInvalidNamesAndParameters(string mediaType)
    {
        var result = ResolveSmithy(SmithyMediaTypeDocument("blob", JsonValue.Create(mediaType)).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.media_type");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Target/traits/smithy.api#mediaType");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(true, 126, true)]
    [Arguments(true, 127, true)]
    [Arguments(true, 128, false)]
    [Arguments(false, 126, true)]
    [Arguments(false, 127, true)]
    [Arguments(false, 128, false)]
    public async Task SmithyJsonMediaTypeComponentLengthsUseTheRfc6838Boundary(bool type, int length, bool valid)
    {
        var name = new string('a', length);
        var mediaType = type ? name + "/json" : "application/" + name;
        var result = ResolveSmithy(SmithyMediaTypeDocument("string", JsonValue.Create(mediaType)).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.media_type");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("blob", true)]
    [Arguments("string", true)]
    [Arguments("enum", true)]
    [Arguments("integer", false)]
    [Arguments("intEnum", false)]
    [Arguments("document", false)]
    [Arguments("structure", false)]
    [Arguments("union", false)]
    [Arguments("list", false)]
    [Arguments("map", false)]
    [Arguments("operation", false)]
    [Arguments("member", false)]
    public async Task SmithyJsonMediaTypeRequiresABlobOrStringRoot(string kind, bool valid)
    {
        var result = ResolveSmithy(SmithyMediaTypeDocument(kind, JsonValue.Create("application/json")).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("1")]
    [Arguments("{}")]
    [Arguments("[]")]
    public async Task SmithyJsonMediaTypeRequiresAStringValue(string value)
    {
        var result = ResolveSmithy(SmithyMediaTypeDocument("string", JsonNode.Parse(value)).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Target/traits/smithy.api#mediaType");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string", "not-json")]
    [Arguments("blob", "bm90LWpzb24=")]
    public async Task SmithyJsonMediaTypeDoesNotValidateOrConvertDefaultContent(string kind, string value)
    {
        var document = SmithyMediaTypeDocument(kind, JsonValue.Create("application/json"));
        document["shapes"]!["example#Target"]!["traits"]!["smithy.api#default"] = value;
        document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"] = JsonNode.Parse("""{"smithy.api#default":null}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    public async Task SmithyJsonMediaTypesPreserveNativeMetadataWithoutAcquisitionOrUnrelatedScanning()
    {
        var document = SmithyMediaTypeDocument("string", JsonValue.Create("application/json"));
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#examples":[{"title":"literal","input":{"smithy.api#mediaType":false,"target":"example#Absent"}}]}
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""{"type":"integer","traits":{"smithy.api#mediaType":[]}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Media types are not dependency URLs."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("https://example.test/Docs/%7e?q=Value#Part")]
    [Arguments("http://example.test/")]
    [Arguments("ftp://example.test/docs")]
    [Arguments("file:///docs/reference.html")]
    public async Task SmithyJsonExternalDocumentationRetainsNamedUrlsWithoutAcquisition(string url)
    {
        var document = SmithyExternalDocumentationDocument(new JsonObject
        {
            ["Reference"] = url,
            ["reference"] = "https://example.test/other",
            ["API / ~ guide"] = url,
            [" "] = url
        });
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Documentation URLs must not be fetched."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("null", "")]
    [Arguments("[]", "")]
    [Arguments("\"https://example.test/\"", "")]
    [Arguments("{}", "")]
    [Arguments("""{"":"https://example.test/"}""", "/")]
    [Arguments("""{"API / ~ guide":null}""", "/API ~1 ~0 guide")]
    [Arguments("""{"Reference":true}""", "/Reference")]
    [Arguments("""{"Reference":1}""", "/Reference")]
    [Arguments("""{"Reference":{}}""", "/Reference")]
    [Arguments("""{"Reference":[]}""", "/Reference")]
    [Arguments("""{"Reference":""}""", "/Reference")]
    [Arguments("""{"Reference":"docs/reference"}""", "/Reference")]
    [Arguments("""{"Reference":"//example.test/docs"}""", "/Reference")]
    [Arguments("""{"Reference":"#fragment"}""", "/Reference")]
    [Arguments("""{"Reference":"https://example.test/bad path"}""", "/Reference")]
    [Arguments("""{"Reference":"https://example.test/%zz"}""", "/Reference")]
    [Arguments("""{"Reference":"http://["}""", "/Reference")]
    [Arguments("""{"Valid":"https://example.test/","Reference":""}""", "/Reference")]
    public async Task SmithyJsonExternalDocumentationChecksEveryMapEntry(string annotation, string suffix)
    {
        var result = ResolveSmithy(SmithyExternalDocumentationDocument(JsonNode.Parse(annotation)).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.external_documentation");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#externalDocumentation" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("root", false)]
    [Arguments("structure", false)]
    [Arguments("union", false)]
    [Arguments("list", false)]
    [Arguments("mapKey", false)]
    [Arguments("mapValue", false)]
    [Arguments("enum", false)]
    [Arguments("intEnum", false)]
    [Arguments("root", true)]
    [Arguments("structure", true)]
    [Arguments("union", true)]
    [Arguments("list", true)]
    [Arguments("mapKey", true)]
    [Arguments("mapValue", true)]
    [Arguments("enum", true)]
    [Arguments("intEnum", true)]
    public async Task SmithyJsonExternalDocumentationChecksReachedDataRootsAndMembers(string location, bool valid)
    {
        var document = SmithyExternalDocumentationDocument(JsonNode.Parse("""{"Reference":"https://example.test/"}"""));
        document["shapes"]!["example#Event"]!["members"]!["data"]!.AsObject().Remove("traits");
        var target = JsonNode.Parse(location switch
        {
            "list" => """{"type":"list","member":{"target":"smithy.api#String"}}""",
            "mapKey" or "mapValue" => """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}""",
            "enum" => """{"type":"enum","members":{"one":{"target":"smithy.api#Unit"}}}""",
            "intEnum" => """{"type":"intEnum","members":{"one":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""",
            "union" => """{"type":"union","members":{"one":{"target":"smithy.api#String"}}}""",
            _ => """{"type":"structure","members":{"one":{"target":"smithy.api#String"}}}"""
        })!;
        var member = location switch
        {
            "root" => target,
            "list" => target["member"]!,
            "mapKey" => target["key"]!,
            "mapValue" => target["value"]!,
            _ => target["members"]!["one"]!
        };
        member["traits"] ??= new JsonObject();
        member["traits"]!["smithy.api#externalDocumentation"] = new JsonObject
        { ["Reference"] = valid ? "https://example.test/Docs" : "" };
        document["shapes"]!["example#Target"] = target;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            return;
        }
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.external_documentation");
        var at = location switch
        {
            "root" => "",
            "list" => "/member",
            "mapKey" => "/key",
            "mapValue" => "/value",
            _ => "/members/one"
        };
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Target" + at +
            "/traits/smithy.api#externalDocumentation/Reference");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonExternalDocumentationPreservesLiteralAndUnrelatedClosureBoundaries()
    {
        var document = SmithyExternalDocumentationDocument(JsonNode.Parse("""{"Reference":"https://example.test/"}"""));
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal","input":{"smithy.api#externalDocumentation":{},"target":"example#Absent"}}]
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse(
            """{"type":"string","traits":{"smithy.api#externalDocumentation":[]}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    public async Task SmithyJsonExternalDocumentationCannotSupplyMissingShapeDefinitions()
    {
        var document = SmithyExternalDocumentationDocument(JsonNode.Parse("""{"Reference":"https://example.test/schema"}"""));
        document["shapes"]!.AsObject().Remove("example#Target");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Documentation cannot supply missing definitions."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("-2147483648", true)]
    [Arguments("-2147483649", false)]
    [Arguments("2147483647", true)]
    [Arguments("2147483648", false)]
    [Arguments("404", true)]
    [Arguments("500", true)]
    [Arguments("200", true)]
    [Arguments("0", true)]
    [Arguments("-1", true)]
    [Arguments("4.04e2", true)]
    [Arguments("404.000", true)]
    [Arguments("404.00000000000000000001", false)]
    public async Task SmithyJsonHttpErrorUsesExactIntegerBoundsWithoutEnforcingRecommendedRanges(string code, bool valid)
    {
        var document = SmithyHttpStatusDocument();
        document["shapes"]!["example#Failure"]!["traits"]!["smithy.api#httpError"] = JsonNode.Parse(code);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.http_error");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Failure/traits/smithy.api#httpError");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("null")]
    [Arguments("\"404\"")]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("true")]
    public async Task SmithyJsonHttpErrorRequiresANumericTraitValue(string value)
    {
        var document = SmithyHttpStatusDocument();
        document["shapes"]!["example#Failure"]!["traits"]!["smithy.api#httpError"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("client", true)]
    [Arguments("server", true)]
    [Arguments("absent", false)]
    [Arguments("member", false)]
    [Arguments("operation", false)]
    public async Task SmithyJsonHttpErrorRequiresAnErrorMarkedStructure(string target, bool valid)
    {
        var document = SmithyHttpStatusDocument();
        var failure = document["shapes"]!["example#Failure"]!;
        if (target is "client" or "server") { failure["traits"]!["smithy.api#error"] = target; }
        else
        {
            failure["traits"]!.AsObject().Remove("smithy.api#httpError");
            if (target == "absent")
            {
                document["shapes"]!["example#Get"]!.AsObject().Remove("errors");
                document["shapes"]!["example#Input"]!["traits"] = JsonNode.Parse("""{"smithy.api#httpError":404}""");
            }
            else if (target == "operation")
            { document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""{"smithy.api#httpError":404}"""); }
            else
            { document["shapes"]!["example#Input"]!["members"]!["status"]!["traits"]!["smithy.api#httpError"] = 404; }
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("integer", true)]
    [Arguments("intEnum", true)]
    [Arguments("implicit", true)]
    [Arguments("long", false)]
    [Arguments("short", false)]
    [Arguments("byte", false)]
    [Arguments("string", false)]
    [Arguments("float", false)]
    [Arguments("double", false)]
    [Arguments("document", false)]
    [Arguments("structure", false)]
    public async Task SmithyJsonHttpResponseCodeRequiresAnIntegerTarget(string kind, bool valid)
    {
        var document = SmithyHttpStatusDocument();
        if (kind == "implicit") { document["shapes"]!["example#Input"]!["members"]!["status"]!["target"] = "smithy.api#Integer"; }
        else
        {
            document["shapes"]!["example#Status"] = JsonNode.Parse(kind == "intEnum"
                ? """{"type":"intEnum","members":{"ok":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":200}}}}"""
                : """{"type":"KIND"}""".Replace("KIND", kind, StringComparison.Ordinal));
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/status/traits/smithy.api#httpResponseCode");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("input", false)]
    [Arguments("output", true)]
    [Arguments("unmarked", true)]
    [Arguments("error", true)]
    [Arguments("root", false)]
    [Arguments("union", false)]
    [Arguments("list", false)]
    [Arguments("map", false)]
    public async Task SmithyJsonHttpResponseCodeRequiresANonInputStructureMember(string container, bool valid)
    {
        var document = SmithyHttpStatusDocument();
        var input = document["shapes"]!["example#Input"]!;
        if (container is "input" or "output")
        {
            input["traits"] = new JsonObject { ["smithy.api#" + container] = new JsonObject() };
            if (container == "output")
            {
                document["shapes"]!["example#Get"]!.AsObject().Remove("input");
                document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Input"}""");
            }
        }
        else if (container == "error") { input["traits"] = JsonNode.Parse("""{"smithy.api#error":"server"}"""); }
        else if (container == "root")
        {
            input["traits"] = JsonNode.Parse("""{"smithy.api#httpResponseCode":{}}""");
        }
        else if (container != "unmarked")
        {
            var aggregate = container switch
            {
                "union" => JsonNode.Parse("""{"type":"union","members":{"status":{"target":"example#Status","traits":{"smithy.api#httpResponseCode":{}}}}}"""),
                "list" => JsonNode.Parse("""{"type":"list","member":{"target":"example#Status","traits":{"smithy.api#httpResponseCode":{}}}}"""),
                _ => JsonNode.Parse("""{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Status","traits":{"smithy.api#httpResponseCode":{}}}}""")
            };
            document["shapes"]!["example#Status"] = aggregate;
            input["members"]!["status"]!.AsObject().Remove("traits");
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("httpLabel", false)]
    [Arguments("httpQuery", false)]
    [Arguments("httpHeader", false)]
    [Arguments("httpPrefixHeaders", false)]
    [Arguments("httpPayload", false)]
    [Arguments("httpQueryParams", false)]
    [Arguments("httpLabel", true)]
    [Arguments("httpQuery", true)]
    [Arguments("httpHeader", true)]
    [Arguments("httpPrefixHeaders", true)]
    [Arguments("httpPayload", true)]
    [Arguments("httpQueryParams", true)]
    public async Task SmithyJsonHttpResponseCodeRejectsEveryConflictingBindingInEitherOrder(string binding, bool first)
    {
        var document = SmithyHttpStatusDocument();
        var traits = new JsonObject();
        if (!first) { traits["smithy.api#httpResponseCode"] = new JsonObject(); }
        traits["smithy.api#" + binding] = binding is "httpQuery" or "httpHeader" or "httpPrefixHeaders" ? JsonValue.Create("name") : new JsonObject();
        if (first) { traits["smithy.api#httpResponseCode"] = new JsonObject(); }
        document["shapes"]!["example#Input"]!["members"]!["status"]!["traits"] = traits;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/status/traits/smithy.api#" +
            (first ? binding : "httpResponseCode"));
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonHttpResponseCodeIsExclusiveByMember(bool separateContainer)
    {
        var document = SmithyHttpStatusDocument();
        if (separateContainer)
        {
            document["shapes"]!["example#Output"] = document["shapes"]!["example#Input"]!.DeepClone();
            document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Output"}""");
        }
        else
        {
            document["shapes"]!["example#Input"]!["members"]!["second"] =
                document["shapes"]!["example#Input"]!["members"]!["status"]!.DeepClone();
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(separateContainer
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (separateContainer) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/second");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("\"status\"")]
    [Arguments("[]")]
    public async Task SmithyJsonHttpResponseCodeRequiresAnObjectAnnotation(string annotation)
    {
        var document = SmithyHttpStatusDocument();
        document["shapes"]!["example#Input"]!["members"]!["status"]!["traits"]!["smithy.api#httpResponseCode"] = JsonNode.Parse(annotation);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonHttpStatusDeclarationsRetainMissingTargetSemanticsWithoutAcquisition()
    {
        var document = SmithyHttpStatusDocument();
        document["shapes"]!.AsObject().Remove("example#Status");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Status target IDs cannot be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Status");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonHttpStatusDeclarationsPreserveNativeMetadataAndClosureBoundaries()
    {
        var document = SmithyHttpStatusDocument();
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#examples":[{"title":"literal","output":{"smithy.api#httpError":false,"target":"example#Absent"}}]}
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse(
            """{"type":"string","traits":{"smithy.api#httpError":"bad","smithy.api#httpResponseCode":[]}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"uri":"/"}""")]
    [Arguments("""{"method":"GET"}""")]
    [Arguments("""{"method":null,"uri":"/"}""")]
    [Arguments("""{"method":"GET","uri":42}""")]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("true")]
    public async Task SmithyJsonHttpRequiresAnObjectWithStringMethodAndUri(string value)
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(value.StartsWith('{')
            ? "document.http_binding" : "document.trait_value");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("GET")]
    [Arguments("get")]
    [Arguments("CUSTOM")]
    [Arguments("")]
    [Arguments("not an HTTP token\n")]
    public async Task SmithyJsonHttpPreservesLiteralMethodsWithoutInferringSafetyOrDefaults(string method)
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["method"] = method;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(result.Operation.GetProperty("traits").TryGetProperty("smithy.api#readonly", out _)).IsFalse();
        await Assert.That(result.Operation.GetProperty("traits").GetProperty("smithy.api#http").TryGetProperty("code", out _)).IsFalse();
    }

    [Test]
    [Arguments("99", false)]
    [Arguments("100", true)]
    [Arguments("199", true)]
    [Arguments("200", true)]
    [Arguments("299", true)]
    [Arguments("300", true)]
    [Arguments("999", true)]
    [Arguments("1000", false)]
    [Arguments("0", false)]
    [Arguments("-100", false)]
    [Arguments("2e2", true)]
    [Arguments("999.000", true)]
    [Arguments("200.0000000000000000001", false)]
    [Arguments("\"200\"", false)]
    [Arguments("null", false)]
    [Arguments("{}", false)]
    public async Task SmithyJsonHttpSuccessCodesUseExactMandatoryBoundsNotRecommendedRanges(string code, bool valid)
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["code"] = JsonNode.Parse(code);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.http_code");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#http/code");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure")]
    [Arguments("member")]
    [Arguments("string")]
    public async Task SmithyJsonHttpRequiresAnOperation(string location)
    {
        var document = SmithyHttpOperationDocument();
        var http = document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!.DeepClone();
        document["shapes"]!["example#Get"]!.AsObject().Remove("traits");
        var target = location switch
        {
            "structure" => document["shapes"]!["example#Input"]!,
            "member" => document["shapes"]!["example#Input"]!["members"]!["id"]!,
            _ => document["shapes"]!["example#Identifier"]!
        };
        target["traits"] ??= new JsonObject();
        target["traits"]!["smithy.api#http"] = http;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("/")]
    [Arguments("/objects")]
    [Arguments("/objects/")]
    [Arguments("/%2e/%2E%2e/a%2Fb")]
    [Arguments("/a.b/...")]
    [Arguments("/things?required&kind=value")]
    [Arguments("/things?key=//../x?other")]
    [Arguments("/things?key=")]
    [Arguments("/~!$&'()*+,;=:@_-")]
    public async Task SmithyJsonHttpAcceptsOriginFormLiteralsWithoutNormalization(string uri)
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["uri"] = uri;
        document["shapes"]!["example#Input"]!["members"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetProperty("traits").GetProperty("smithy.api#http").GetProperty("uri").GetString()).IsEqualTo(uri);
    }

    [Test]
    [Arguments("")]
    [Arguments("relative")]
    [Arguments("https://example.com/path")]
    [Arguments("//host/path")]
    [Arguments("/a//b")]
    [Arguments("/a//")]
    [Arguments("/./a")]
    [Arguments("/a/..")]
    [Arguments("/a#fragment")]
    [Arguments("/a?")]
    [Arguments("/a?x#fragment")]
    [Arguments("/a?x={id}")]
    [Arguments("/{id}/a{id}")]
    [Arguments("/{id}{other}")]
    [Arguments("/{id")]
    [Arguments("/id}")]
    [Arguments("/{}")]
    [Arguments("/{1id}")]
    [Arguments("/{id+more}")]
    [Arguments("/{id}/{id}")]
    [Arguments("/{id+}/{other+}")]
    [Arguments("/bad%")]
    [Arguments("/bad%0g")]
    [Arguments("/a b")]
    [Arguments("/a\\b")]
    [Arguments("/a?bad%")]
    public async Task SmithyJsonHttpRejectsMalformedPatternsBeforeBindingLabels(string uri)
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["uri"] = uri;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.http_uri");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#http/uri");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("no-input")]
    [Arguments("unit-input")]
    [Arguments("no-members")]
    [Arguments("no-member")]
    [Arguments("case")]
    [Arguments("missing-annotation")]
    [Arguments("unused-member")]
    public async Task SmithyJsonHttpRequiresExactBidirectionalInputLabelBindings(string problem)
    {
        var document = SmithyHttpOperationDocument();
        var input = document["shapes"]!["example#Input"]!;
        switch (problem)
        {
            case "no-input": document["shapes"]!["example#Get"]!.AsObject().Remove("input"); break;
            case "unit-input": document["shapes"]!["example#Get"]!["input"]!["target"] = "smithy.api#Unit"; break;
            case "no-members": input.AsObject().Remove("members"); break;
            case "no-member": input["members"] = new JsonObject(); break;
            case "case": document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["uri"] = "/{Id}"; break;
            case "missing-annotation": input["members"]!["id"]!["traits"]!.AsObject().Remove("smithy.api#httpLabel"); break;
            case "unused-member": document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["uri"] = "/literal"; break;
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.http_label");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#http/uri");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string", false, true)]
    [Arguments("enum", false, true)]
    [Arguments("boolean", false, true)]
    [Arguments("timestamp", false, true)]
    [Arguments("byte", false, true)]
    [Arguments("short", false, true)]
    [Arguments("integer", false, true)]
    [Arguments("intEnum", false, true)]
    [Arguments("long", false, true)]
    [Arguments("float", false, true)]
    [Arguments("double", false, true)]
    [Arguments("bigInteger", false, true)]
    [Arguments("bigDecimal", false, true)]
    [Arguments("string", true, true)]
    [Arguments("enum", true, true)]
    [Arguments("integer", true, false)]
    [Arguments("intEnum", true, false)]
    [Arguments("boolean", true, false)]
    [Arguments("timestamp", true, false)]
    [Arguments("structure", false, false)]
    [Arguments("document", false, false)]
    [Arguments("blob", false, false)]
    public async Task SmithyJsonHttpLabelsCheckScalarTargetsAndGreedyStringSpecializations(string kind, bool greedy, bool valid)
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["uri"] = greedy ? "/{id+}" : "/{id}";
        document["shapes"]!["example#Identifier"] = JsonNode.Parse(kind switch
        {
            "enum" => """{"type":"enum","members":{"ONE":{"target":"smithy.api#Unit"}}}""",
            "intEnum" => """{"type":"intEnum","members":{"ONE":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""",
            _ => """{"type":"KIND"}""".Replace("KIND", kind, StringComparison.Ordinal)
        });
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo(greedy
                ? "document.http_label" : "document.trait_target");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("required")]
    [Arguments("annotation")]
    [Arguments("container")]
    public async Task SmithyJsonHttpLabelRequiresAnObjectOnARequiredStructureMember(string problem)
    {
        var document = SmithyHttpOperationDocument();
        var member = document["shapes"]!["example#Input"]!["members"]!["id"]!;
        if (problem == "required") { member["traits"]!.AsObject().Remove("smithy.api#required"); }
        else if (problem == "annotation") { member["traits"]!["smithy.api#httpLabel"] = true; }
        else { document["shapes"]!["example#Input"]!["traits"] = JsonNode.Parse("""{"smithy.api#httpLabel":{}}"""); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(problem == "annotation"
            ? "document.trait_value" : "document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("httpQuery", false)]
    [Arguments("httpHeader", false)]
    [Arguments("httpQueryParams", false)]
    [Arguments("httpPrefixHeaders", false)]
    [Arguments("httpPayload", false)]
    [Arguments("httpResponseCode", false)]
    [Arguments("httpQuery", true)]
    [Arguments("httpHeader", true)]
    [Arguments("httpQueryParams", true)]
    [Arguments("httpPrefixHeaders", true)]
    [Arguments("httpPayload", true)]
    [Arguments("httpResponseCode", true)]
    public async Task SmithyJsonHttpLabelRejectsAllOtherMessageBindingsInEitherOrder(string binding, bool first)
    {
        var document = SmithyHttpOperationDocument();
        var traits = new JsonObject { ["smithy.api#required"] = new JsonObject() };
        if (!first) { traits["smithy.api#httpLabel"] = new JsonObject(); }
        traits["smithy.api#" + binding] = binding is "httpQuery" or "httpHeader" or "httpPrefixHeaders"
            ? JsonValue.Create("name") : new JsonObject();
        if (first) { traits["smithy.api#httpLabel"] = new JsonObject(); }
        document["shapes"]!["example#Input"]!["members"]!["id"]!["traits"] = traits;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/id/traits/smithy.api#" +
            (first ? binding : "httpLabel"));
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("httpQuery", "httpHeader", false)]
    [Arguments("httpQuery", "httpQueryParams", false)]
    [Arguments("httpQuery", "httpPrefixHeaders", false)]
    [Arguments("httpQuery", "httpPayload", false)]
    [Arguments("httpHeader", "httpQueryParams", false)]
    [Arguments("httpHeader", "httpPrefixHeaders", false)]
    [Arguments("httpHeader", "httpPayload", false)]
    [Arguments("httpQueryParams", "httpPrefixHeaders", false)]
    [Arguments("httpQueryParams", "httpPayload", false)]
    [Arguments("httpPrefixHeaders", "httpPayload", false)]
    [Arguments("httpQuery", "httpHeader", true)]
    [Arguments("httpQuery", "httpQueryParams", true)]
    [Arguments("httpQuery", "httpPrefixHeaders", true)]
    [Arguments("httpQuery", "httpPayload", true)]
    [Arguments("httpHeader", "httpQueryParams", true)]
    [Arguments("httpHeader", "httpPrefixHeaders", true)]
    [Arguments("httpHeader", "httpPayload", true)]
    [Arguments("httpQueryParams", "httpPrefixHeaders", true)]
    [Arguments("httpQueryParams", "httpPayload", true)]
    [Arguments("httpPrefixHeaders", "httpPayload", true)]
    public async Task SmithyJsonHttpRejectsEveryOtherPairOfMessageBindingsInEitherOrder(string left, string right, bool reverse)
    {
        var document = SmithyHttpOperationDocument();
        var traits = new JsonObject();
        foreach (var binding in reverse ? new[] { right, left } : new[] { left, right })
        {
            traits["smithy.api#" + binding] = binding is "httpQuery" or "httpHeader" or "httpPrefixHeaders"
                ? JsonValue.Create("name") : new JsonObject();
        }
        document["shapes"]!["example#Input"]!["members"]!["id"]!["traits"] = traits;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/id/traits/smithy.api#" +
            (reverse ? right : left));
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("absent")]
    [Arguments("unit")]
    [Arguments("empty")]
    public async Task SmithyJsonHttpLiteralPatternsDoNotRequireInputMembers(string input)
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["uri"] = "/literal";
        if (input == "absent") { document["shapes"]!["example#Get"]!.AsObject().Remove("input"); }
        else if (input == "unit") { document["shapes"]!["example#Get"]!["input"]!["target"] = "smithy.api#Unit"; }
        else { document["shapes"]!["example#Input"]!.AsObject().Remove("members"); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    public async Task SmithyJsonHttpAllowsMultipleLabelsAndNonterminalGreedyLabelsWithoutEnforcingRecommendations()
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Input"]!["members"]!["other"] =
            document["shapes"]!["example#Input"]!["members"]!["id"]!.DeepClone();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["uri"] = "/{id+}/suffix/{other}?literal=value";
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("input")]
    [Arguments("label")]
    public async Task SmithyJsonHttpRetainsMissingDependencySemanticsWithoutAcquiringLabelTargets(string missing)
    {
        var document = SmithyHttpOperationDocument();
        var id = missing == "input" ? "example#Input" : "example#Identifier";
        document["shapes"]!.AsObject().Remove(id);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("HTTP shape IDs cannot be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/" + id);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonHttpIgnoresUnrelatedRoutesAndDoesNotBindOutputOrNestedLabels()
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Other"] = document["shapes"]!["example#Get"]!.DeepClone();
        document["shapes"]!["example#Output"] = document["shapes"]!["example#Input"]!.DeepClone();
        document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Output"}""");
        document["shapes"]!["example#Input"]!["members"]!["nested"] =
            JsonNode.Parse("""{"target":"example#Output"}""");
        document["shapes"]!["example#Output"]!["members"]!["notInUri"] =
            document["shapes"]!["example#Output"]!["members"]!["id"]!.DeepClone();
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""{"type":"operation","traits":{"smithy.api#http":null}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("\"body\"")]
    [Arguments("42")]
    [Arguments("[]")]
    public async Task SmithyJsonHttpPayloadRequiresAnObjectAnnotation(string annotation)
    {
        var document = SmithyHttpPayloadDocument("input");
        document["shapes"]!["example#Container"]!["members"]!["body"]!["traits"]!["smithy.api#httpPayload"] = JsonNode.Parse(annotation);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/members/body/traits/smithy.api#httpPayload");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure")]
    [Arguments("operation")]
    [Arguments("union")]
    [Arguments("list")]
    [Arguments("map")]
    public async Task SmithyJsonHttpPayloadRequiresAStructureMember(string container)
    {
        var document = SmithyHttpPayloadDocument("input");
        if (container is "structure" or "operation")
        {
            var id = container == "structure" ? "example#Container" : "example#Get";
            document["shapes"]![id]!["traits"] = JsonNode.Parse("""{"smithy.api#httpPayload":{}}""");
        }
        else
        {
            document["shapes"]!["example#Body"] = JsonNode.Parse(container switch
            {
                "union" => """{"type":"union","members":{"value":{"target":"smithy.api#String","traits":{"smithy.api#httpPayload":{}}}}}""",
                "list" => """{"type":"list","member":{"target":"smithy.api#String","traits":{"smithy.api#httpPayload":{}}}}""",
                _ => """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String","traits":{"smithy.api#httpPayload":{}}}}"""
            });
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("blob")]
    [Arguments("string")]
    [Arguments("boolean")]
    [Arguments("byte")]
    [Arguments("short")]
    [Arguments("integer")]
    [Arguments("long")]
    [Arguments("float")]
    [Arguments("double")]
    [Arguments("bigInteger")]
    [Arguments("bigDecimal")]
    [Arguments("timestamp")]
    [Arguments("document")]
    [Arguments("enum")]
    [Arguments("intEnum")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("structure")]
    [Arguments("union")]
    public async Task SmithyJsonHttpPayloadAllowsEveryDataTargetWithoutInventingRawBodyRestrictions(string kind)
    {
        var document = SmithyHttpPayloadDocument("input");
        document["shapes"]!["example#Body"] = SmithyHttpBindingTarget(kind);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("input", "httpHeader", true)]
    [Arguments("input", "httpPrefixHeaders", true)]
    [Arguments("input", "httpQuery", true)]
    [Arguments("input", "httpQueryParams", true)]
    [Arguments("input", "httpLabel", true)]
    [Arguments("input", "httpResponseCode", false)]
    [Arguments("input", "none", false)]
    [Arguments("output", "httpHeader", true)]
    [Arguments("output", "httpPrefixHeaders", true)]
    [Arguments("output", "httpQuery", false)]
    [Arguments("output", "httpQueryParams", false)]
    [Arguments("output", "httpLabel", false)]
    [Arguments("output", "httpResponseCode", false)]
    [Arguments("output", "none", false)]
    [Arguments("error", "httpHeader", true)]
    [Arguments("error", "httpPrefixHeaders", true)]
    [Arguments("error", "httpQuery", false)]
    [Arguments("error", "httpQueryParams", false)]
    [Arguments("error", "httpLabel", false)]
    [Arguments("error", "httpResponseCode", false)]
    [Arguments("error", "none", false)]
    public async Task SmithyJsonHttpPayloadChecksSiblingBindingsForEachOperationRole(string role, string binding, bool valid)
    {
        var document = SmithyHttpPayloadDocument(role);
        var sibling = document["shapes"]!["example#Container"]!["members"]!["metadata"]!;
        var traits = new JsonObject();
        if (binding != "none")
        {
            traits["smithy.api#" + binding] = binding is "httpHeader" or "httpPrefixHeaders" or "httpQuery"
                ? JsonValue.Create("metadata") : new JsonObject();
            if (binding == "httpLabel")
            {
                traits["smithy.api#required"] = new JsonObject();
                if (role == "input") { document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"]!["uri"] = "/{metadata}"; }
            }
            if (binding is "httpPrefixHeaders" or "httpQueryParams")
            {
                sibling["target"] = "example#Metadata";
                document["shapes"]!["example#Metadata"] = JsonNode.Parse("""
                    {"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}
                    """);
            }
            else if (binding == "httpResponseCode") { sibling["target"] = "smithy.api#Integer"; }
        }
        sibling["traits"] = traits;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.http_payload_binding");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/members/metadata");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SmithyJsonHttpPayloadIsExclusivePerStructureRegardlessOfTargetIdentity(bool separate, bool differentTarget)
    {
        var document = SmithyHttpPayloadDocument("input");
        var second = document["shapes"]!["example#Container"]!["members"]!["body"]!.DeepClone();
        if (differentTarget) { second["target"] = "smithy.api#Integer"; }
        if (separate)
        {
            document["shapes"]!["example#Output"] = new JsonObject
            {
                ["type"] = "structure",
                ["members"] = new JsonObject { ["second"] = second }
            };
            document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Output"}""");
        }
        else { document["shapes"]!["example#Container"]!["members"]!["second"] = second; }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(separate
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (separate) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/members/second");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonHttpPayloadDoesNotApplyTopLevelSiblingRulesToNestedStructures()
    {
        var document = SmithyHttpPayloadDocument("input");
        document["shapes"]!["example#Nested"] = document["shapes"]!["example#Container"]!.DeepClone();
        document["shapes"]!["example#Nested"]!["members"]!["metadata"]!.AsObject().Remove("traits");
        document["shapes"]!["example#Container"]!["members"] = JsonNode.Parse("""{"nested":{"target":"example#Nested"}}""");
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""{"type":"string","traits":{"smithy.api#httpPayload":false}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("input", "blob", true, true)]
    [Arguments("input", "blob", true, false)]
    [Arguments("output", "blob", true, true)]
    [Arguments("output", "blob", true, false)]
    [Arguments("input", "union", true, true)]
    [Arguments("input", "union", true, false)]
    [Arguments("output", "union", true, true)]
    [Arguments("output", "union", true, false)]
    [Arguments("input", "blob", false, false)]
    [Arguments("output", "blob", false, false)]
    [Arguments("input", "union", false, false)]
    [Arguments("output", "union", false, false)]
    public async Task SmithyJsonHttpStreamingRequiresPayloadForInputAndOutputOnlyWhenHttpBound(
        string role, string kind, bool http, bool payload)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        if (role == "output")
        {
            document["shapes"]!["example#Get"]!.AsObject().Remove("input");
            document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Input"}""");
        }
        if (http)
        {
            document["shapes"]!["example#Get"]!["traits"] =
                JsonNode.Parse("""{"smithy.api#http":{"method":"POST","uri":"/messages"}}""");
        }
        if (payload)
        {
            document["shapes"]!["example#Input"]!["members"]!["data"]!["traits"]!["smithy.api#httpPayload"] = new JsonObject();
        }
        if (kind == "union")
        {
            document["shapes"]!["example#Stream"] = JsonNode.Parse("""
                {"type":"union","traits":{"smithy.api#streaming":{}},"members":{"event":{"target":"example#Event"}}}
                """);
            document["shapes"]!["example#Event"] = JsonNode.Parse("""{"type":"structure"}""");
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(http && !payload
            ? ApiOperationResolutionStatus.Invalid : ApiOperationResolutionStatus.Selected);
        if (!http || payload) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.http_streaming_payload");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/data");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("body")]
    [Arguments("metadata")]
    public async Task SmithyJsonHttpPayloadRetainsMissingTargetSemanticsWithoutAcquisition(string member)
    {
        var document = SmithyHttpPayloadDocument("input");
        document["shapes"]!["example#Container"]!["members"]![member]!["target"] = "example#Missing";
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Payload shape IDs cannot be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Missing");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonHttpPayloadSiblingRulesDoNotDependOnMethodOrExecuteSerialization()
    {
        var document = SmithyHttpPayloadDocument("input");
        document["shapes"]!["example#Get"]!.AsObject().Remove("traits");
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#examples":[{"title":"literal","input":{"httpPayload":false,"target":"example#Missing"}}]}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        document["shapes"]!["example#Container"]!["members"]!["metadata"]!.AsObject().Remove("traits");
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.http_payload_binding");
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string", true)]
    [Arguments("enum", true)]
    [Arguments("boolean", true)]
    [Arguments("timestamp", true)]
    [Arguments("byte", true)]
    [Arguments("short", true)]
    [Arguments("integer", true)]
    [Arguments("intEnum", true)]
    [Arguments("long", true)]
    [Arguments("float", true)]
    [Arguments("double", true)]
    [Arguments("bigInteger", true)]
    [Arguments("bigDecimal", true)]
    [Arguments("blob", false)]
    [Arguments("document", false)]
    [Arguments("structure", false)]
    [Arguments("union", false)]
    [Arguments("map", false)]
    [Arguments("list", false)]
    public async Task SmithyJsonHttpQueryAndHeaderTargetsFollowScalarAndListRules(string kind, bool scalar)
    {
        foreach (var binding in new[] { "httpHeader", "httpQuery" })
        {
            foreach (var list in new[] { false, true })
            {
                var document = SmithyHttpNamedBindingDocument(binding);
                document["shapes"]!["example#Value"] = list
                    ? JsonNode.Parse("""{"type":"list","member":{"target":"example#Element"}}""")
                    : SmithyHttpBindingTarget(kind);
                if (list) { document["shapes"]!["example#Element"] = SmithyHttpBindingTarget(kind); }
                var valid = scalar || kind == "list" && !list;
                var result = ResolveSmithy(document.ToJsonString());
                await Assert.That(result.Status).IsEqualTo(valid
                    ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
                if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
                else
                {
                    await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
                    await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/members/metadata/traits/smithy.api#" + binding);
                    await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
                }
            }
        }
    }

    [Test]
    [Arguments("httpPrefixHeaders", "string", false, false, true)]
    [Arguments("httpPrefixHeaders", "enum", false, false, true)]
    [Arguments("httpPrefixHeaders", "string", true, false, false)]
    [Arguments("httpPrefixHeaders", "string", false, true, false)]
    [Arguments("httpPrefixHeaders", "integer", false, false, false)]
    [Arguments("httpPrefixHeaders", "structure", false, false, false)]
    [Arguments("httpQueryParams", "string", false, false, true)]
    [Arguments("httpQueryParams", "enum", false, false, true)]
    [Arguments("httpQueryParams", "string", true, false, true)]
    [Arguments("httpQueryParams", "enum", true, false, true)]
    [Arguments("httpQueryParams", "string", false, true, true)]
    [Arguments("httpQueryParams", "string", true, true, true)]
    [Arguments("httpQueryParams", "integer", false, false, false)]
    [Arguments("httpQueryParams", "integer", true, false, false)]
    [Arguments("httpQueryParams", "map", false, false, false)]
    [Arguments("httpQueryParams", "list", true, false, false)]
    public async Task SmithyJsonHttpMapBindingsCheckValueCollectionsAndSparseEligibility(
        string binding, string kind, bool list, bool sparse, bool valid)
    {
        var document = SmithyHttpNamedBindingDocument(binding);
        document["shapes"]!["example#Value"] = JsonNode.Parse("""
            {"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Element"}}
            """);
        if (sparse) { document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""{"smithy.api#sparse":{}}"""); }
        document["shapes"]!["example#Element"] = list
            ? JsonNode.Parse("""{"type":"list","member":{"target":"example#Item"}}""") : SmithyHttpBindingTarget(kind);
        if (list) { document["shapes"]!["example#Item"] = SmithyHttpBindingTarget(kind); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
        document["shapes"]!["example#Value"] = SmithyHttpBindingTarget("string");
        var nonMap = ResolveSmithy(document.ToJsonString());
        await Assert.That(nonMap.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(nonMap.Diagnostic!.Code).IsEqualTo("document.trait_target");
    }

    [Test]
    [Arguments("httpHeader", "\"X-Name\"", true)]
    [Arguments("httpHeader", "\"Authorization\"", true)]
    [Arguments("httpHeader", "\"!#$%&'*+.^_`|~0-9AZaz-\"", true)]
    [Arguments("httpHeader", "\"\"", false)]
    [Arguments("httpHeader", "\"has space\"", false)]
    [Arguments("httpHeader", "\"bad:colon\"", false)]
    [Arguments("httpHeader", "\"bad\\r\\n\"", false)]
    [Arguments("httpHeader", "\"bad\\u00e9\"", false)]
    [Arguments("httpHeader", "null", false)]
    [Arguments("httpHeader", "{}", false)]
    [Arguments("httpQuery", "\"\"", false)]
    [Arguments("httpQuery", "\"reserved?& =/%\"", true)]
    [Arguments("httpQuery", "null", false)]
    [Arguments("httpQuery", "[]", false)]
    [Arguments("httpPrefixHeaders", "\"\"", true)]
    [Arguments("httpPrefixHeaders", "\"X-\"", true)]
    [Arguments("httpPrefixHeaders", "null", false)]
    [Arguments("httpPrefixHeaders", "42", false)]
    [Arguments("httpQueryParams", "{}", true)]
    [Arguments("httpQueryParams", "null", false)]
    [Arguments("httpQueryParams", "[]", false)]
    [Arguments("httpQueryParams", "true", false)]
    public async Task SmithyJsonHttpNamedBindingsCheckValueKindsAndHeaderTokenSyntax(string binding, string value, bool valid)
    {
        var document = SmithyHttpNamedBindingDocument(binding);
        document["shapes"]!["example#Container"]!["members"]!["metadata"]!["traits"]!["smithy.api#" + binding] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            var tokenFailure = binding == "httpHeader" && value.StartsWith('"') && value != "\"\"";
            await Assert.That(result.Diagnostic!.Code).IsEqualTo(tokenFailure
                ? "document.http_header_name" : "document.trait_value");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("httpHeader", "Name", "Name", false)]
    [Arguments("httpHeader", "Name", "name", false)]
    [Arguments("httpHeader", "Name", "Other", true)]
    [Arguments("httpQuery", "Name", "Name", false)]
    [Arguments("httpQuery", "Name", "name", true)]
    [Arguments("httpQuery", "a", "%61", true)]
    public async Task SmithyJsonHttpHeaderAndQueryNamesUseTheirDeclaredCaseComparison(
        string binding, string first, string second, bool valid)
    {
        var document = SmithyHttpNamedBindingDocument(binding);
        var member = document["shapes"]!["example#Container"]!["members"]!["metadata"]!;
        member["traits"]!["smithy.api#" + binding] = first;
        var other = member.DeepClone();
        other["traits"]!["smithy.api#" + binding] = second;
        document["shapes"]!["example#Container"]!["members"]!["other"] = other;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo(binding == "httpHeader"
                ? "document.http_header_name" : "document.http_query_name");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/members/other/traits/smithy.api#" + binding);
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
        document["shapes"]!["example#Container"]!["members"]!.AsObject().Remove("other");
        document["shapes"]!["example#Output"] = document["shapes"]!["example#Container"]!.DeepClone();
        document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Output"}""");
        await Assert.That(ResolveSmithy(document.ToJsonString()).Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
    }

    [Test]
    [Arguments("", "X-Name", true, false)]
    [Arguments("X-", "Other", true, false)]
    [Arguments("X-", "X-Name", false, false)]
    [Arguments("X-", "x-name", false, false)]
    [Arguments("", "X-Name", true, true)]
    [Arguments("X-", "Other", true, true)]
    [Arguments("X-", "X-Name", false, true)]
    [Arguments("X-", "x-name", false, true)]
    public async Task SmithyJsonHttpHeaderPrefixDisambiguationIsOrderIndependentAndAllowsAnEmptyPrefix(
        string prefix, string name, bool valid, bool reverse)
    {
        var document = SmithyHttpNamedBindingDocument("httpPrefixHeaders");
        var map = document["shapes"]!["example#Container"]!["members"]!["metadata"]!.DeepClone();
        map["traits"]!["smithy.api#httpPrefixHeaders"] = prefix;
        var header = JsonNode.Parse("""{"target":"smithy.api#String","traits":{"smithy.api#httpHeader":"header"}}""")!;
        header["traits"]!["smithy.api#httpHeader"] = name;
        var members = new JsonObject();
        if (reverse) { members["header"] = header; }
        members["metadata"] = map;
        if (!reverse) { members["header"] = header; }
        document["shapes"]!["example#Container"]!["members"] = members;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.http_header_prefix");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/members");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("httpQueryParams", false)]
    [Arguments("httpQueryParams", true)]
    [Arguments("httpPrefixHeaders", false)]
    [Arguments("httpPrefixHeaders", true)]
    public async Task SmithyJsonHttpMapBindingsAreExclusivePerStructure(string binding, bool separate)
    {
        var document = SmithyHttpNamedBindingDocument(binding);
        if (separate)
        {
            document["shapes"]!["example#Output"] = document["shapes"]!["example#Container"]!.DeepClone();
            document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Output"}""");
        }
        else
        {
            document["shapes"]!["example#Container"]!["members"]!["other"] =
                document["shapes"]!["example#Container"]!["members"]!["metadata"]!.DeepClone();
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(separate
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (separate) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/members/other");
        }
    }

    [Test]
    [Arguments("httpHeader")]
    [Arguments("httpQuery")]
    [Arguments("httpPrefixHeaders")]
    [Arguments("httpQueryParams")]
    public async Task SmithyJsonHttpNamedBindingsRequireStructureMembers(string binding)
    {
        var document = SmithyHttpNamedBindingDocument(binding);
        var annotation = document["shapes"]!["example#Container"]!["members"]!["metadata"]!["traits"]!.DeepClone();
        document["shapes"]!["example#Container"]!["traits"] = annotation;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("map")]
    [Arguments("list")]
    public async Task SmithyJsonHttpCollectionBindingsPreserveMissingAndMalformedReferenceDiagnostics(string kind)
    {
        var document = SmithyHttpNamedBindingDocument(kind == "map" ? "httpQueryParams" : "httpHeader");
        document["shapes"]!["example#Value"] = kind == "map"
            ? JsonNode.Parse("""{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Missing"}}""")
            : JsonNode.Parse("""{"type":"list","member":{"target":"example#Missing"}}""");
        var unresolved = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Collection IDs cannot be acquired."));
        await Assert.That(unresolved.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(unresolved.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(unresolved.Diagnostic.Path).IsEqualTo("$/shapes/example#Missing");
        foreach (var malformed in new[] { "absent", "null", "true", "{}" })
        {
            var property = kind == "map" ? "value" : "member";
            if (malformed == "absent") { document["shapes"]!["example#Value"]!.AsObject().Remove(property); }
            else { document["shapes"]!["example#Value"]![property] = JsonNode.Parse(malformed); }
            var invalid = ResolveSmithy(document.ToJsonString());
            await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
            await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.target");
            await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    private static JsonNode SmithyHttpNamedBindingDocument(string binding)
    {
        var document = SmithyHttpPayloadDocument("input");
        document["shapes"]!["example#Container"]!["members"]!.AsObject().Remove("body");
        document["shapes"]!["example#Container"]!["members"]!["metadata"]!["target"] = "example#Value";
        document["shapes"]!["example#Container"]!["members"]!["metadata"]!["traits"] = new JsonObject
        {
            ["smithy.api#" + binding] = binding == "httpQueryParams" ? new JsonObject() : JsonValue.Create("name")
        };
        document["shapes"]!["example#Value"] = SmithyHttpBindingTarget(
            binding is "httpQueryParams" or "httpPrefixHeaders" ? "map" : "string");
        return document;
    }

    private static JsonNode SmithyHttpBindingTarget(string kind) => JsonNode.Parse(kind switch
    {
        "enum" => """{"type":"enum","members":{"ONE":{"target":"smithy.api#Unit"}}}""",
        "intEnum" => """{"type":"intEnum","members":{"ONE":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""",
        "list" => """{"type":"list","member":{"target":"smithy.api#String"}}""",
        "map" => """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String"}}""",
        "union" => """{"type":"union","members":{"value":{"target":"smithy.api#String"}}}""",
        _ => """{"type":"KIND"}""".Replace("KIND", kind, StringComparison.Ordinal)
    })!;

    [Test]
    [Arguments("null", "document.trait_value")]
    [Arguments("true", "document.trait_value")]
    [Arguments("[]", "document.trait_value")]
    [Arguments("\"prefix\"", "document.trait_value")]
    [Arguments("{}", "document.endpoint_prefix")]
    [Arguments("""{"hostPrefix":null}""", "document.endpoint_prefix")]
    [Arguments("""{"hostPrefix":42}""", "document.endpoint_prefix")]
    public async Task SmithyJsonEndpointRequiresAnObjectWithStringHostPrefix(string value, string diagnostic)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#endpoint"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnostic);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("")]
    [Arguments("data.")]
    [Arguments("data")]
    [Arguments("{id}.data.")]
    [Arguments("prefix-{id}-suffix")]
    [Arguments("{id}")]
    [Arguments("a%2eb.")]
    [Arguments("~!$&'()*+,;=_-")]
    public async Task SmithyJsonEndpointPreservesPrefixesWithoutEnforcingDnsOrTrailingPeriodRecommendations(string prefix)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#endpoint"]!["hostPrefix"] = prefix;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("https://data.")]
    [Arguments("user@data.")]
    [Arguments("data:443")]
    [Arguments("data/path")]
    [Arguments("data?query")]
    [Arguments("data#fragment")]
    [Arguments("data space")]
    [Arguments("bad%")]
    [Arguments("bad%xx")]
    [Arguments("bad\\name")]
    [Arguments("{id")]
    [Arguments("id}")]
    [Arguments("{}")]
    [Arguments("{1id}")]
    [Arguments("{id+}")]
    [Arguments("{{id}}")]
    [Arguments("{id}.{id}")]
    [Arguments("{id}{other}")]
    public async Task SmithyJsonEndpointRejectsInvalidLiteralsAndMalformedOrRepeatedLabels(string prefix)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#endpoint"]!["hostPrefix"] = prefix;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.endpoint_prefix");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#endpoint/hostPrefix");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("no-input")]
    [Arguments("unit-input")]
    [Arguments("no-members")]
    [Arguments("no-member")]
    [Arguments("wrong-case")]
    [Arguments("no-host-label")]
    public async Task SmithyJsonEndpointLabelsRequireExactTopLevelInputMembers(string problem)
    {
        var document = SmithyEndpointDocument();
        var input = document["shapes"]!["example#Input"]!;
        switch (problem)
        {
            case "no-input": document["shapes"]!["example#Get"]!.AsObject().Remove("input"); break;
            case "unit-input": document["shapes"]!["example#Get"]!["input"]!["target"] = "smithy.api#Unit"; break;
            case "no-members": input.AsObject().Remove("members"); break;
            case "no-member": input["members"] = new JsonObject(); break;
            case "wrong-case": document["shapes"]!["example#Get"]!["traits"]!["smithy.api#endpoint"]!["hostPrefix"] = "{Id}."; break;
            default: input["members"]!["id"]!["traits"]!.AsObject().Remove("smithy.api#hostLabel"); break;
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.endpoint_label");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#endpoint/hostPrefix");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string", true)]
    [Arguments("enum", true)]
    [Arguments("implicit", true)]
    [Arguments("integer", false)]
    [Arguments("intEnum", false)]
    [Arguments("blob", false)]
    [Arguments("boolean", false)]
    [Arguments("timestamp", false)]
    [Arguments("document", false)]
    [Arguments("structure", false)]
    [Arguments("list", false)]
    [Arguments("map", false)]
    [Arguments("union", false)]
    public async Task SmithyJsonHostLabelRequiresAStringTargetIncludingEnumSpecializations(string kind, bool valid)
    {
        var document = SmithyEndpointDocument();
        if (kind == "implicit") { document["shapes"]!["example#Input"]!["members"]!["id"]!["target"] = "smithy.api#String"; }
        else { document["shapes"]!["example#Identifier"] = SmithyHttpBindingTarget(kind); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/id/traits/smithy.api#hostLabel");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("[]")]
    [Arguments("\"host\"")]
    public async Task SmithyJsonHostLabelRequiresAnObjectAnnotation(string annotation)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Input"]!["members"]!["id"]!["traits"]!["smithy.api#hostLabel"] = JsonNode.Parse(annotation);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("required")]
    [Arguments("root")]
    [Arguments("union")]
    [Arguments("list")]
    [Arguments("map")]
    public async Task SmithyJsonHostLabelRequiresARequiredStructureMember(string problem)
    {
        var document = SmithyEndpointDocument();
        if (problem == "required") { document["shapes"]!["example#Input"]!["members"]!["id"]!["traits"]!.AsObject().Remove("smithy.api#required"); }
        else if (problem == "root") { document["shapes"]!["example#Input"]!["traits"] = JsonNode.Parse("""{"smithy.api#hostLabel":{}}"""); }
        else
        {
            document["shapes"]!["example#Identifier"] = JsonNode.Parse(problem switch
            {
                "union" => """{"type":"union","members":{"value":{"target":"smithy.api#String","traits":{"smithy.api#required":{},"smithy.api#hostLabel":{}}}}}""",
                "list" => """{"type":"list","member":{"target":"smithy.api#String","traits":{"smithy.api#hostLabel":{}}}}""",
                _ => """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"smithy.api#String","traits":{"smithy.api#hostLabel":{}}}}"""
            });
            document["shapes"]!["example#Input"]!["members"]!["id"]!["traits"]!.AsObject().Remove("smithy.api#hostLabel");
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("root")]
    [Arguments("member")]
    public async Task SmithyJsonEndpointRequiresAnOperationTarget(string location)
    {
        var document = SmithyEndpointDocument();
        var traits = document["shapes"]!["example#Get"]!["traits"]!.DeepClone();
        document["shapes"]!["example#Get"]!.AsObject().Remove("traits");
        var target = location == "root" ? document["shapes"]!["example#Input"]! :
            document["shapes"]!["example#Input"]!["members"]!["id"]!;
        target["traits"] = traits;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonEndpointSupportsMultipleSeparatedLabelsAndIndependentHttpBindings()
    {
        var document = SmithyEndpointDocument();
        var member = document["shapes"]!["example#Input"]!["members"]!["id"]!;
        member["traits"]!["smithy.api#httpHeader"] = "X-Identifier";
        document["shapes"]!["example#Input"]!["members"]!["other"] = member.DeepClone();
        document["shapes"]!["example#Input"]!["members"]!["other"]!["traits"]!["smithy.api#httpHeader"] = "X-Other";
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#endpoint"]!["hostPrefix"] = "{id}-{other}.data.";
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#http"] = JsonNode.Parse("""{"method":"GET","uri":"/status"}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    public async Task SmithyJsonHostLabelsOutsideEndpointInputDoNotBecomeTemplateDependencies()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#endpoint"]!["hostPrefix"] = "data.";
        document["shapes"]!["example#Nested"] = document["shapes"]!["example#Input"]!.DeepClone();
        document["shapes"]!["example#Input"]!["members"]!["nested"] = JsonNode.Parse("""{"target":"example#Nested"}""");
        document["shapes"]!["example#Get"]!["output"] = JsonNode.Parse("""{"target":"example#Nested"}""");
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""{"type":"operation","traits":{"smithy.api#endpoint":{"hostPrefix":"{bad}{bad}"}}}""");
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        document["shapes"]!["example#Get"]!.AsObject().Remove("traits");
        var noEndpoint = ResolveSmithy(document.ToJsonString());
        await Assert.That(noEndpoint.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(noEndpoint.Diagnostic).IsNull();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("input")]
    [Arguments("label")]
    public async Task SmithyJsonEndpointRetainsUnresolvedTargetsWithoutAcquiringHostsOrShapes(string missing)
    {
        var document = SmithyEndpointDocument();
        var id = missing == "input" ? "example#Input" : "example#Identifier";
        document["shapes"]!.AsObject().Remove(id);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Endpoint metadata cannot acquire hosts or shapes."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/" + id);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonHttpChecksumRequiredPreservesOperationDeclarationsWithoutExecutingChecksums(bool httpBound)
    {
        var document = SmithyHttpOperationDocument();
        if (!httpBound) { document["shapes"]!["example#Get"]!.AsObject().Remove("traits"); }
        document["shapes"]!["example#Get"]!["traits"] ??= new JsonObject();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#httpChecksumRequired"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(result.Operation.GetProperty("traits").TryGetProperty("smithy.api#httpHeader", out _)).IsFalse();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("false")]
    [Arguments("0")]
    [Arguments("\"MD5\"")]
    [Arguments("[]")]
    public async Task SmithyJsonHttpChecksumRequiredRequiresAnObjectAnnotation(string annotation)
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#httpChecksumRequired"] = JsonNode.Parse(annotation);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#httpChecksumRequired");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure")]
    [Arguments("string")]
    [Arguments("member")]
    [Arguments("list")]
    [Arguments("map")]
    public async Task SmithyJsonHttpChecksumRequiredRejectsNonOperationTargets(string location)
    {
        var document = SmithyHttpOperationDocument();
        JsonNode target;
        if (location == "structure") { target = document["shapes"]!["example#Input"]!; }
        else if (location == "string") { target = document["shapes"]!["example#Identifier"]!; }
        else if (location == "member") { target = document["shapes"]!["example#Input"]!["members"]!["id"]!; }
        else
        {
            document["shapes"]!["example#Identifier"] = SmithyHttpBindingTarget(location);
            document["shapes"]!["example#Input"]!["members"]!["id"]!.AsObject().Remove("traits");
            target = document["shapes"]!["example#Identifier"]![location == "list" ? "member" : "value"]!;
        }
        target["traits"] ??= new JsonObject();
        target["traits"]!["smithy.api#httpChecksumRequired"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonHttpChecksumRequiredDoesNotInspectLiteralPayloadsOrUnrelatedOperations()
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"operation","traits":{"smithy.api#httpChecksumRequired":false}}
            """);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal checksum text","input":{"smithy.api#httpChecksumRequired":false,"target":"example#Absent"}}]
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    public async Task SmithyJsonHttpChecksumRequiredCannotSupplyMissingInputDefinitions()
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#httpChecksumRequired"] = new JsonObject();
        document["shapes"]!.AsObject().Remove("example#Input");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Checksum metadata cannot supply shape definitions."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("[]")]
    [Arguments("\"preview\"")]
    [Arguments("100")]
    public async Task SmithyJsonUnstableRequiresAnObjectAnnotation(string annotation)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#unstable"] = JsonNode.Parse(annotation);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#unstable");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("[]")]
    [Arguments("{}")]
    [Arguments("100")]
    public async Task SmithyJsonUnstableFeatureIdRequiresAString(string value)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#unstable"] =
            new JsonObject { ["featureId"] = JsonNode.Parse(value) };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#unstable/featureId");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(99, false)]
    [Arguments(100, false)]
    [Arguments(101, false)]
    [Arguments(99, true)]
    [Arguments(100, true)]
    [Arguments(101, true)]
    public async Task SmithyJsonUnstableFeatureIdsUseUnicodeScalarLengthAtTheDeclaredBoundary(int length, bool supplementary)
    {
        var document = SmithyEndpointDocument();
        var text = supplementary ? string.Concat(Enumerable.Repeat("\U0001F680", length)) : new string('a', length);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#unstable"] = new JsonObject { ["featureId"] = text };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(length <= 100
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (length <= 100)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetProperty("traits").GetProperty("smithy.api#unstable")
                .GetProperty("featureId").GetString()).IsEqualTo(text);
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.unstable_feature");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#unstable/featureId");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonUnstableFeatureIdsCountCombiningCharactersWithoutNormalization()
    {
        var document = SmithyEndpointDocument();
        var composed = new string('\u00e9', 100);
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", 100));
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#unstable"] = new JsonObject { ["featureId"] = composed };
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        await Assert.That(selected.Operation.GetProperty("traits").GetProperty("smithy.api#unstable")
            .GetProperty("featureId").GetString()).IsEqualTo(composed);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#unstable"]!["featureId"] = decomposed;
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.unstable_feature");
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("operation")]
    [Arguments("structure")]
    [Arguments("member")]
    [Arguments("string")]
    [Arguments("union")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("enum")]
    [Arguments("intEnum")]
    public async Task SmithyJsonUnstableAppliesToReachedRootsAndAllDataMemberKinds(string location)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!.AsObject().Remove("traits");
        document["shapes"]!["example#Input"]!["members"]!["id"]!.AsObject().Remove("traits");
        JsonNode target;
        if (location == "operation") { target = document["shapes"]!["example#Get"]!; }
        else if (location == "structure") { target = document["shapes"]!["example#Input"]!; }
        else if (location == "member") { target = document["shapes"]!["example#Input"]!["members"]!["id"]!; }
        else
        {
            document["shapes"]!["example#Identifier"] = SmithyHttpBindingTarget(location);
            target = location switch
            {
                "list" => document["shapes"]!["example#Identifier"]!["member"]!,
                "map" => document["shapes"]!["example#Identifier"]!["value"]!,
                "union" => document["shapes"]!["example#Identifier"]!["members"]!["value"]!,
                "enum" or "intEnum" => document["shapes"]!["example#Identifier"]!["members"]!["ONE"]!,
                _ => document["shapes"]!["example#Identifier"]!
            };
        }
        target["traits"] ??= new JsonObject();
        target["traits"]!["smithy.api#unstable"] = new JsonObject();
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        target["traits"]!["smithy.api#unstable"] = false;
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonUnstableFeatureMetadataDoesNotAcquireShapesOrClaimServiceFeatureValidation()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#unstable"] =
            new JsonObject { ["featureId"] = "example#Absent" };
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal metadata","input":{"smithy.api#unstable":false,"target":"example#Absent"}}]
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""{"type":"string","traits":{"smithy.api#unstable":false}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Feature IDs are not acquisition edges."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    public async Task SmithyJsonUnstableMetadataCannotSupplyMissingDataDefinitions()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#unstable"] = new JsonObject();
        document["shapes"]!.AsObject().Remove("example#Identifier");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Identifier");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonOptionalAuthPreservesOperationAnnotationsWithoutInventingAuthenticationSchemes(bool servicePresent)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#optionalAuth"] = new JsonObject();
        if (servicePresent)
        {
            document["shapes"]!["example#Service"] = JsonNode.Parse("""
                {"type":"service","version":"1","operations":[{"target":"example#Get"}],
                 "traits":{"smithy.api#httpDigestAuth":{}}}
                """);
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(result.Operation.GetProperty("traits").TryGetProperty("smithy.api#auth", out _)).IsFalse();
        await Assert.That(result.Operation.GetProperty("traits").TryGetProperty("smithy.api#httpDigestAuth", out _)).IsFalse();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("false")]
    [Arguments("0")]
    [Arguments("\"anonymous\"")]
    [Arguments("[]")]
    public async Task SmithyJsonOptionalAuthRequiresAnObjectAnnotation(string annotation)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#optionalAuth"] = JsonNode.Parse(annotation);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#optionalAuth");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure")]
    [Arguments("string")]
    [Arguments("member")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("union")]
    public async Task SmithyJsonOptionalAuthRejectsNonOperationTargets(string location)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!.AsObject().Remove("traits");
        document["shapes"]!["example#Input"]!["members"]!["id"]!.AsObject().Remove("traits");
        JsonNode target;
        if (location == "structure") { target = document["shapes"]!["example#Input"]!; }
        else if (location == "string") { target = document["shapes"]!["example#Identifier"]!; }
        else if (location == "member") { target = document["shapes"]!["example#Input"]!["members"]!["id"]!; }
        else
        {
            document["shapes"]!["example#Identifier"] = SmithyHttpBindingTarget(location);
            target = location switch
            {
                "list" => document["shapes"]!["example#Identifier"]!["member"]!,
                "map" => document["shapes"]!["example#Identifier"]!["value"]!,
                _ => document["shapes"]!["example#Identifier"]!["members"]!["value"]!
            };
        }
        target["traits"] = JsonNode.Parse("""{"smithy.api#optionalAuth":{}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonOptionalAuthDoesNotInspectLiteralPayloadsOrUnrelatedDeclarations()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#optionalAuth"] = new JsonObject();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal metadata","input":{"smithy.api#optionalAuth":false,"target":"example#Absent"}}]
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""{"type":"operation","traits":{"smithy.api#optionalAuth":false}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Optional authentication cannot acquire dependencies."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    public async Task SmithyJsonOptionalAuthCannotSupplyMissingInputDefinitions()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#optionalAuth"] = new JsonObject();
        document["shapes"]!.AsObject().Remove("example#Input");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""["smithy.api#httpBasicAuth"]""")]
    [Arguments("""["smithy.api#httpDigestAuth","smithy.api#httpBearerAuth","smithy.api#httpApiKeyAuth","smithy.api#httpBasicAuth"]""")]
    [Arguments("""["example#Scheme"]""")]
    [Arguments("""["example#Scheme","smithy.api#httpBasicAuth"]""")]
    public async Task SmithyJsonAuthPreservesEmptyAndOrderedSchemeListsWithoutServiceInheritance(string schemes)
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#auth"] = JsonNode.Parse(schemes);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#optionalAuth"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null", "document.trait_value")]
    [Arguments("{}", "document.trait_value")]
    [Arguments("true", "document.trait_value")]
    [Arguments("\"smithy.api#httpBasicAuth\"", "document.trait_value")]
    [Arguments("[null]", "document.auth_scheme")]
    [Arguments("[42]", "document.auth_scheme")]
    [Arguments("[{}]", "document.auth_scheme")]
    [Arguments("""[""]""", "document.auth_scheme")]
    [Arguments("""["relative"]""", "document.auth_scheme")]
    [Arguments("""["example#Scheme$member"]""", "document.auth_scheme")]
    [Arguments("""["example#Scheme","example#Scheme"]""", "document.auth_scheme")]
    [Arguments("""["smithy.api#httpBasicAuth","smithy.api#httpBasicAuth"]""", "document.auth_scheme")]
    public async Task SmithyJsonAuthRejectsMalformedAndDuplicateSchemeDeclarations(string schemes, string diagnostic)
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#auth"] = JsonNode.Parse(schemes);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnostic);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure")]
    [Arguments("member")]
    public async Task SmithyJsonAuthRejectsDataTargets(string location)
    {
        var document = SmithyAuthenticationDocument();
        var target = location == "structure" ? document["shapes"]!["example#Input"]! :
            document["shapes"]!["example#Input"]!["members"]!["id"]!;
        target["traits"] ??= new JsonObject();
        target["traits"]!["smithy.api#auth"] = new JsonArray();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("trait")]
    [Arguments("authDefinition")]
    [Arguments("both")]
    public async Task SmithyJsonAuthRequiresExplicitSchemeDefinitionsToCarryBothMarkers(string missing)
    {
        var document = SmithyAuthenticationDocument();
        var markers = document["shapes"]!["example#Scheme"]!["traits"]!.AsObject();
        if (missing is "trait" or "both") { markers.Remove("smithy.api#trait"); }
        if (missing is "authDefinition" or "both") { markers.Remove("smithy.api#authDefinition"); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.auth_scheme");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#auth/0");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string")]
    [Arguments("integer")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("union")]
    public async Task SmithyJsonAuthDefinitionRequiresAStructureTrait(string kind)
    {
        var document = SmithyAuthenticationDocument();
        var markers = document["shapes"]!["example#Scheme"]!["traits"]!.DeepClone();
        document["shapes"]!["example#Scheme"] = SmithyHttpBindingTarget(kind);
        document["shapes"]!["example#Scheme"]!["traits"] = markers;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Scheme/traits/smithy.api#authDefinition");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("null", "document.trait_value")]
    [Arguments("[]", "document.trait_value")]
    [Arguments("true", "document.trait_value")]
    [Arguments("""{"traits":null}""", "document.trait_value")]
    [Arguments("""{"traits":{}}""", "document.trait_value")]
    [Arguments("""{"traits":[null]}""", "document.auth_required_trait")]
    [Arguments("""{"traits":["relative"]}""", "document.auth_required_trait")]
    [Arguments("""{"traits":["example#Dependency$member"]}""", "document.auth_required_trait")]
    public async Task SmithyJsonAuthDefinitionChecksObjectAndRequiredTraitListValues(string value, string diagnostic)
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Scheme"]!["traits"]!["smithy.api#authDefinition"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnostic);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"traits":[]}""")]
    [Arguments("""{"traits":["example#Dependency"]}""")]
    [Arguments("""{"traits":["example#Dependency","example#Dependency"]}""")]
    [Arguments("""{"traits":["example#Scheme"]}""")]
    public async Task SmithyJsonAuthDefinitionTraversesRequiredTraitsWithoutInventingUniqueness(string value)
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Scheme"]!["traits"]!["smithy.api#authDefinition"] = JsonNode.Parse(value);
        document["shapes"]!["example#Dependency"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#trait":{}}}
            """);
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        if (value.Contains("Dependency", StringComparison.Ordinal))
        {
            document["shapes"]!["example#Dependency"]!["traits"]!["smithy.api#documentation"] = false;
            var invalid = ResolveSmithy(document.ToJsonString());
            await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
            await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_value");
            await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Dependency/traits/smithy.api#documentation");
        }
    }

    [Test]
    public async Task SmithyJsonAuthDefinitionRejectsNonTraitDependencies()
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Scheme"]!["traits"]!["smithy.api#authDefinition"] =
            JsonNode.Parse("""{"traits":["example#Identifier"]}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.auth_required_trait");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Scheme/traits/smithy.api#authDefinition/traits/0");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("scheme")]
    [Arguments("dependency")]
    [Arguments("case")]
    public async Task SmithyJsonAuthReferencesPreserveMissingAndExactCaseSemanticsWithoutAcquisition(string missing)
    {
        var document = SmithyAuthenticationDocument();
        string id;
        if (missing == "scheme")
        {
            id = "example#Scheme";
            document["shapes"]!.AsObject().Remove(id);
        }
        else if (missing == "dependency")
        {
            id = "example#Missing";
            document["shapes"]!["example#Scheme"]!["traits"]!["smithy.api#authDefinition"] =
                JsonNode.Parse("""{"traits":["example#Missing"]}""");
        }
        else
        {
            id = "smithy.api#httpbasicauth";
            document["shapes"]!["example#Get"]!["traits"]!["smithy.api#auth"] =
                new JsonArray(JsonValue.Create(id));
        }
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Authentication IDs cannot be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/" + id);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonAuthRetainsPrivateTraitAccessChecks()
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["other#Scheme"] = document["shapes"]!["example#Scheme"]!.DeepClone();
        document["shapes"]!["other#Scheme"]!["traits"]!["smithy.api#private"] = new JsonObject();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#auth"] = JsonNode.Parse("""["other#Scheme"]""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.private_access");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#auth/0");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonAuthIgnoresUnrelatedServiceMembershipAndLiteralExampleMetadata()
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Service"] = JsonNode.Parse("""
            {"type":"service","version":"1","operations":[{"target":"example#Get"}]}
            """);
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"operation","traits":{"smithy.api#auth":false}}
            """);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal metadata","input":{"smithy.api#auth":["example#Missing"]}}]
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    public async Task SmithyJsonAuthDoesNotBypassAuthMarkersOnExplicitPreludeSchemeDefinitions()
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#auth"] = JsonNode.Parse("""["smithy.api#httpBasicAuth"]""");
        document["shapes"]!["smithy.api#httpBasicAuth"] = JsonNode.Parse("""
            {"type":"structure","traits":{"smithy.api#trait":{"selector":"service"}}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.auth_scheme");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonAuthRequiredTraitsRetainPrivateAccessAndTransitiveSchemaDependencies()
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Scheme"]!["traits"]!["smithy.api#authDefinition"] =
            JsonNode.Parse("""{"traits":["other#Dependency"]}""");
        document["shapes"]!["other#Dependency"] = JsonNode.Parse("""
            {"type":"structure","traits":{"smithy.api#trait":{},"smithy.api#private":{}},
             "members":{"value":{"target":"other#Missing"}}}
            """);
        var privateResult = ResolveSmithy(document.ToJsonString());
        await Assert.That(privateResult.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(privateResult.Diagnostic!.Code).IsEqualTo("document.private_access");
        await Assert.That(privateResult.Diagnostic.Path).IsEqualTo("$/shapes/example#Scheme/traits/smithy.api#authDefinition/traits/0");
        document["shapes"]!["other#Dependency"]!["traits"]!.AsObject().Remove("smithy.api#private");
        var unresolved = ResolveSmithy(document.ToJsonString());
        await Assert.That(unresolved.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(unresolved.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(unresolved.Diagnostic.Path).IsEqualTo("$/shapes/other#Missing");
        await Assert.That(unresolved.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"traits":[]}""")]
    [Arguments("""{"traits":["example#Dependency"]}""")]
    [Arguments("""{"traits":["example#Dependency","example#Dependency"],"noInlineDocumentSupport":false}""")]
    [Arguments("""{"traits":["example#Protocol"],"noInlineDocumentSupport":true}""")]
    public async Task SmithyJsonProtocolDefinitionPreservesDeclarationsWithoutServiceClosureInference(string value)
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Protocol"]!["traits"]!["smithy.api#protocolDefinition"] = JsonNode.Parse(value);
        document["shapes"]!["example#Input"]!["members"]!["document"] =
            JsonNode.Parse("""{"target":"smithy.api#Document"}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null", "document.trait_value", "")]
    [Arguments("[]", "document.trait_value", "")]
    [Arguments("true", "document.trait_value", "")]
    [Arguments("""{"noInlineDocumentSupport":null}""", "document.trait_value", "/noInlineDocumentSupport")]
    [Arguments("""{"noInlineDocumentSupport":0}""", "document.trait_value", "/noInlineDocumentSupport")]
    [Arguments("""{"noInlineDocumentSupport":"false"}""", "document.trait_value", "/noInlineDocumentSupport")]
    [Arguments("""{"traits":null}""", "document.trait_value", "/traits")]
    [Arguments("""{"traits":{}}""", "document.trait_value", "/traits")]
    [Arguments("""{"traits":[false]}""", "document.protocol_required_trait", "/traits/0")]
    [Arguments("""{"traits":["relative"]}""", "document.protocol_required_trait", "/traits/0")]
    [Arguments("""{"traits":["example#Dependency$member"]}""", "document.protocol_required_trait", "/traits/0")]
    public async Task SmithyJsonProtocolDefinitionRejectsMalformedDeclarations(string value, string diagnostic, string suffix)
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Protocol"]!["traits"]!["smithy.api#protocolDefinition"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnostic);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Protocol/traits/smithy.api#protocolDefinition" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string")]
    [Arguments("integer")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("union")]
    public async Task SmithyJsonProtocolDefinitionRequiresAStructureTrait(string kind)
    {
        var document = SmithyProtocolDocument();
        var markers = document["shapes"]!["example#Protocol"]!["traits"]!.DeepClone();
        document["shapes"]!["example#Protocol"] = SmithyHttpBindingTarget(kind);
        document["shapes"]!["example#Protocol"]!["traits"] = markers;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Protocol/traits/smithy.api#protocolDefinition");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure")]
    [Arguments("member")]
    public async Task SmithyJsonProtocolDefinitionRejectsNonTraitTargets(string location)
    {
        var document = SmithyEndpointDocument();
        var target = location == "structure" ? document["shapes"]!["example#Input"]! :
            document["shapes"]!["example#Input"]!["members"]!["id"]!;
        target["traits"] ??= new JsonObject();
        target["traits"]!["smithy.api#protocolDefinition"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonProtocolDefinitionTraversesRequiredTraitSchemasAndRejectsDataDependencies()
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Dependency"]!["traits"]!["smithy.api#documentation"] = false;
        var invalidSchema = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalidSchema.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalidSchema.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(invalidSchema.Diagnostic.Path).IsEqualTo("$/shapes/example#Dependency/traits/smithy.api#documentation");
        document["shapes"]!["example#Dependency"]!["traits"]!.AsObject().Clear();
        var nonTrait = ResolveSmithy(document.ToJsonString());
        await Assert.That(nonTrait.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(nonTrait.Diagnostic!.Code).IsEqualTo("document.protocol_required_trait");
        await Assert.That(nonTrait.Diagnostic.Path).IsEqualTo("$/shapes/example#Protocol/traits/smithy.api#protocolDefinition/traits/0");
        await Assert.That(nonTrait.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonProtocolDefinitionRetainsPrivateAccessChecks()
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["other#Dependency"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#trait":{},"smithy.api#private":{}}}
            """);
        document["shapes"]!["example#Protocol"]!["traits"]!["smithy.api#protocolDefinition"] =
            JsonNode.Parse("""{"traits":["other#Dependency"]}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.private_access");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Protocol/traits/smithy.api#protocolDefinition/traits/0");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("example#Missing")]
    [Arguments("example#dependency")]
    public async Task SmithyJsonProtocolDefinitionRetainsMissingAndExactCaseDependenciesWithoutAcquisition(string dependency)
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Protocol"]!["traits"]!["smithy.api#protocolDefinition"] =
            new JsonObject { ["traits"] = new JsonArray(JsonValue.Create(dependency)) };
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Protocol trait IDs cannot be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/" + dependency);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonProtocolDefinitionIgnoresUnrelatedShapesAndLiteralValues()
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"structure","traits":{"smithy.api#protocolDefinition":false}}
            """);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal metadata","input":{"smithy.api#protocolDefinition":{"traits":["example#Missing"]}}}]
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""["Foo"]""")]
    [Arguments("""["Foo.Bar","Foo","Foo.Bar","example#Missing"," "]""")]
    public async Task SmithyJsonSuppressPreservesOrderedLiteralEventIdsWithoutInventingUniqueness(string value)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#suppress"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null", "")]
    [Arguments("{}", "")]
    [Arguments("true", "")]
    [Arguments("\"Foo\"", "")]
    [Arguments("[null]", "/0")]
    [Arguments("[42]", "/0")]
    [Arguments("[{}]", "/0")]
    [Arguments("""[""]""", "/0")]
    [Arguments("""["Foo",""]""", "/1")]
    public async Task SmithyJsonSuppressRejectsMalformedListsAndEmptyEventIds(string value, string suffix)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#suppress"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#suppress" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("structure")]
    [Arguments("member")]
    [Arguments("simple")]
    public async Task SmithyJsonSuppressAcceptsReachedShapeAndMemberAnnotations(string location)
    {
        var document = SmithyEndpointDocument();
        var target = location switch
        {
            "structure" => document["shapes"]!["example#Input"]!,
            "member" => document["shapes"]!["example#Input"]!["members"]!["id"]!,
            _ => document["shapes"]!["example#Identifier"]!
        };
        target["traits"] ??= new JsonObject();
        target["traits"]!["smithy.api#suppress"] = JsonNode.Parse("""["Foo.Bar"]""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        target["traits"]!["smithy.api#suppress"] = JsonNode.Parse("""[""]""");
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonSuppressDoesNotHideStructuralErrors()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#suppress"] =
            JsonNode.Parse("""["document","document.trait_value","*"]""");
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#optionalAuth"] = false;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#optionalAuth");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonSuppressCannotResolveMissingDependencies()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#suppress"] =
            JsonNode.Parse("""["dependency","dependency.unresolved"]""");
        document["shapes"]!.AsObject().Remove("example#Input");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonSuppressIgnoresUnrelatedAndLiteralMetadata()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#suppress":false}}
            """);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal metadata","input":{"smithy.api#suppress":[null]}}]
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"selector":"*","failWhenMissing":false,"errorMessage":""}""")]
    [Arguments("""{"selector":"integer","failWhenMissing":true,"errorMessage":"example#Missing"}""")]
    public async Task SmithyJsonIdRefPreservesDeclarationOptionsWithoutAcquiringLiteralFields(string value)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Identifier"]!["traits"] =
            new JsonObject { ["smithy.api#idRef"] = JsonNode.Parse(value) };
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("idRef declaration fields cannot be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null", "")]
    [Arguments("[]", "")]
    [Arguments("true", "")]
    [Arguments("""{"selector":null}""", "/selector")]
    [Arguments("""{"selector":42}""", "/selector")]
    [Arguments("""{"errorMessage":false}""", "/errorMessage")]
    [Arguments("""{"errorMessage":{}}""", "/errorMessage")]
    [Arguments("""{"failWhenMissing":null}""", "/failWhenMissing")]
    [Arguments("""{"failWhenMissing":"false"}""", "/failWhenMissing")]
    [Arguments("""{"failWhenMissing":0}""", "/failWhenMissing")]
    public async Task SmithyJsonIdRefRejectsMalformedDeclarationOptions(string value, string suffix)
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Identifier"]!["traits"] =
            new JsonObject { ["smithy.api#idRef"] = JsonNode.Parse(value) };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Identifier/traits/smithy.api#idRef" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string")]
    [Arguments("enum")]
    public async Task SmithyJsonIdRefAcceptsStringSpecializationsAndMembers(string kind)
    {
        var document = SmithyHttpPayloadDocument("input");
        document["shapes"]!["example#Body"] = SmithyHttpBindingTarget(kind);
        document["shapes"]!["example#Body"]!["traits"] = JsonNode.Parse("""{"smithy.api#idRef":{}}""");
        document["shapes"]!["example#Container"]!["members"]!["body"]!["traits"]!["smithy.api#idRef"] =
            JsonNode.Parse("""{"failWhenMissing":true}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        document["shapes"]!["example#Container"]!["members"]!["body"]!["traits"]!["smithy.api#idRef"] = false;
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_value");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Container/members/body/traits/smithy.api#idRef");
    }

    [Test]
    [Arguments("integer", false)]
    [Arguments("blob", false)]
    [Arguments("list", false)]
    [Arguments("structure", false)]
    [Arguments("operation", false)]
    [Arguments("integer", true)]
    [Arguments("blob", true)]
    [Arguments("list", true)]
    [Arguments("structure", true)]
    public async Task SmithyJsonIdRefRejectsNonStringShapesAndMemberTargets(string kind, bool member)
    {
        var document = SmithyHttpPayloadDocument("input");
        var target = member ? document["shapes"]!["example#Container"]!["members"]!["body"]! :
            kind == "operation" ? document["shapes"]!["example#Get"]! : document["shapes"]!["example#Body"]!;
        if (kind != "operation")
        {
            document["shapes"]!["example#Body"] = SmithyHttpBindingTarget(kind);
            if (!member) { target = document["shapes"]!["example#Body"]!; }
        }
        target["traits"] ??= new JsonObject();
        target["traits"]!["smithy.api#idRef"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonIdRefDoesNotInventMissingMemberTargetKinds()
    {
        var document = SmithyHttpPayloadDocument("input");
        document["shapes"]!["example#Container"]!["members"]!["body"]!["traits"]!["smithy.api#idRef"] = new JsonObject();
        document["shapes"]!.AsObject().Remove("example#Body");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Body");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonIdRefIgnoresUnrelatedDeclarationsAndLiteralMetadata()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#idRef":false}}
            """);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal metadata","input":{"smithy.api#idRef":{"failWhenMissing":null}}}]
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"NoDocuments":{"selector":"~> member :test(> document)"}}""")]
    [Arguments("""{"event/with~pointer":{"selector":"*","message":"","severity":"ERROR"}}""")]
    [Arguments("""{"One":{"selector":"","severity":"DANGER"},"Two":{"selector":"*","severity":"WARNING"},"Three":{"selector":"*","severity":"NOTE"}}""")]
    public async Task SmithyJsonTraitValidatorsPreserveDeclarationsWithoutExecutingSelectors(string value)
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Protocol"]!["traits"]!["smithy.api#traitValidators"] = JsonNode.Parse(value);
        document["shapes"]!["example#Input"]!["members"]!["document"] =
            JsonNode.Parse("""{"target":"smithy.api#Document"}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("null", "document.trait_value", "")]
    [Arguments("[]", "document.trait_value", "")]
    [Arguments("true", "document.trait_value", "")]
    [Arguments("""{"":{"selector":"*"}}""", "document.trait_validator", "/")]
    [Arguments("""{"E":null}""", "document.trait_validator", "/E")]
    [Arguments("""{"E":[]}""", "document.trait_validator", "/E")]
    [Arguments("""{"E":{}}""", "document.trait_validator", "/E/selector")]
    [Arguments("""{"E":{"selector":null}}""", "document.trait_validator", "/E/selector")]
    [Arguments("""{"E":{"selector":42}}""", "document.trait_validator", "/E/selector")]
    [Arguments("""{"E":{"selector":"*","message":null}}""", "document.trait_validator", "/E/message")]
    [Arguments("""{"E":{"selector":"*","message":false}}""", "document.trait_validator", "/E/message")]
    [Arguments("""{"E":{"selector":"*","severity":null}}""", "document.trait_validator", "/E/severity")]
    [Arguments("""{"E":{"selector":"*","severity":"error"}}""", "document.trait_validator", "/E/severity")]
    [Arguments("""{"E":{"selector":"*","severity":"FATAL"}}""", "document.trait_validator", "/E/severity")]
    [Arguments("""{"event/with~pointer":{"selector":false}}""", "document.trait_validator", "/event~1with~0pointer/selector")]
    public async Task SmithyJsonTraitValidatorsRejectMalformedDeclarations(string value, string diagnostic, string suffix)
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Protocol"]!["traits"]!["smithy.api#traitValidators"] = JsonNode.Parse(value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnostic);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Protocol/traits/smithy.api#traitValidators" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string")]
    [Arguments("integer")]
    [Arguments("list")]
    [Arguments("map")]
    [Arguments("union")]
    public async Task SmithyJsonTraitValidatorsAcceptNonStructureTraitDefinitions(string kind)
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Dependency"] = SmithyHttpBindingTarget(kind);
        document["shapes"]!["example#Dependency"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#trait":{},"smithy.api#traitValidators":{"Rule":{"selector":"*"}}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        document["shapes"]!["example#Dependency"]!["traits"]!["smithy.api#traitValidators"]!["Rule"]!["severity"] = false;
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_validator");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Dependency/traits/smithy.api#traitValidators/Rule/severity");
    }

    [Test]
    [Arguments("operation")]
    [Arguments("structure")]
    [Arguments("member")]
    [Arguments("simple")]
    public async Task SmithyJsonTraitValidatorsRejectTargetsWithoutTraitMarkers(string location)
    {
        var document = SmithyEndpointDocument();
        var target = location switch
        {
            "operation" => document["shapes"]!["example#Get"]!,
            "structure" => document["shapes"]!["example#Input"]!,
            "member" => document["shapes"]!["example#Input"]!["members"]!["id"]!,
            _ => document["shapes"]!["example#Identifier"]!
        };
        target["traits"] ??= new JsonObject();
        target["traits"]!["smithy.api#traitValidators"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonTraitValidatorsCannotResolveMissingDependenciesOrAcquireLiteralSelectors()
    {
        var document = SmithyProtocolDocument();
        document["shapes"]!["example#Protocol"]!["traits"]!["smithy.api#traitValidators"] = JsonNode.Parse("""
            {"example#Missing":{"selector":"[id|name = Missing]","message":"example#Absent"}}
            """);
        document["shapes"]!.AsObject().Remove("example#Dependency");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Validator metadata cannot be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Dependency");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonTraitValidatorsIgnoreUnrelatedDefinitionsAndLiteralExamples()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#traitValidators":false}}
            """);
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#examples"] = JsonNode.Parse("""
            [{"title":"literal metadata","input":{"smithy.api#traitValidators":{"Rule":{"selector":null}}}}]
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("string", "\"text\"")]
    [Arguments("string", "\"\"")]
    [Arguments("boolean", "true")]
    [Arguments("boolean", "false")]
    [Arguments("blob", "\"\"")]
    [Arguments("blob", "\"AQID\"")]
    [Arguments("document", "null")]
    [Arguments("document", """{"target":"example#Absent","values":[1,null,true]}""")]
    [Arguments("float", "\"NaN\"")]
    [Arguments("double", "\"Infinity\"")]
    [Arguments("double", "\"-Infinity\"")]
    [Arguments("bigInteger", "\"9007199254740993123456789\"")]
    [Arguments("bigDecimal", "\"1.234567890123456789e100\"")]
    [Arguments("timestamp", "0")]
    [Arguments("timestamp", "\"2026-10-30T19:00:00Z\"")]
    public async Task SmithyJsonCustomTraitNodesPreserveCompatibleScalarValues(string kind, string value)
    {
        var document = SmithyCustomNodeDocument(kind, value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("string", "42")]
    [Arguments("string", "null")]
    [Arguments("boolean", "\"false\"")]
    [Arguments("blob", "\"not-base64!\"")]
    [Arguments("blob", "[]")]
    [Arguments("integer", "\"1\"")]
    [Arguments("integer", "1.1")]
    [Arguments("float", "\"nan\"")]
    [Arguments("float", "1e39")]
    [Arguments("double", "1e309")]
    [Arguments("bigInteger", "\"1.1\"")]
    [Arguments("bigDecimal", "false")]
    [Arguments("timestamp", "false")]
    [Arguments("list", "{}")]
    [Arguments("map", "[]")]
    [Arguments("structure", "null")]
    [Arguments("union", "[]")]
    public async Task SmithyJsonCustomTraitNodesRejectIncompatibleKindsAndNumericValues(string kind, string value)
    {
        var result = ResolveSmithy(SmithyCustomNodeDocument(kind, value).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_node");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("byte", "-129", false)]
    [Arguments("byte", "-128", true)]
    [Arguments("byte", "127", true)]
    [Arguments("byte", "128", false)]
    [Arguments("short", "-32769", false)]
    [Arguments("short", "-32768", true)]
    [Arguments("short", "32767", true)]
    [Arguments("short", "32768", false)]
    [Arguments("integer", "-2147483649", false)]
    [Arguments("integer", "-2147483648", true)]
    [Arguments("integer", "2147483647", true)]
    [Arguments("integer", "2147483648", false)]
    [Arguments("long", "-9223372036854775809", false)]
    [Arguments("long", "-9223372036854775808", true)]
    [Arguments("long", "9223372036854775807", true)]
    [Arguments("long", "9223372036854775808", false)]
    public async Task SmithyJsonCustomTraitNodesUseExactIntegerTypeBoundaries(string kind, string value, bool valid)
    {
        var result = ResolveSmithy(SmithyCustomNodeDocument(kind, value).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_node");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""[{"one":{"name":"first"}},{"two":{"name":"second"}}]""")]
    public async Task SmithyJsonCustomTraitNodesTraverseNonemptyNestedAggregates(string value)
    {
        var document = SmithyCustomNodeDocument("list", value);
        document["shapes"]!["example#Custom"]!["member"]!["target"] = "example#Map";
        document["shapes"]!["example#Map"] = JsonNode.Parse("""
            {"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Record"}}
            """);
        document["shapes"]!["example#Record"] = JsonNode.Parse("""
            {"type":"structure","members":{"name":{"target":"smithy.api#String","traits":{"smithy.api#required":{}}}}}
            """);
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        document["shapes"]!["example#Get"]!["traits"]!["example#Custom"] =
            JsonNode.Parse("""[{"one":{"name":"first"}},{"a/b~c":{"name":false}}]""");
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_node");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/1/a~1b~0c/name");
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("list", true)]
    [Arguments("list", false)]
    [Arguments("map", true)]
    [Arguments("map", false)]
    public async Task SmithyJsonCustomTraitNodesAllowNullCollectionEntriesOnlyWhenSparse(string kind, bool sparse)
    {
        var document = SmithyCustomNodeDocument(kind, kind == "list" ? """["ok",null]""" : """{"ok":"value","nil":null}""");
        if (sparse) { document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#sparse"] = new JsonObject(); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(sparse ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (sparse) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_node");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/" + (kind == "list" ? "1" : "nil"));
        }
    }

    [Test]
    [Arguments("{}", "/name")]
    [Arguments("""{"unknown":"value"}""", "/unknown")]
    [Arguments("""{"name":null}""", "/name")]
    public async Task SmithyJsonCustomTraitNodesRequireDeclaredAndRequiredStructureMembers(string value, string suffix)
    {
        var document = SmithyCustomNodeDocument("structure", value);
        document["shapes"]!["example#Custom"]!["members"] = JsonNode.Parse("""
            {"name":{"target":"smithy.api#String","traits":{"smithy.api#required":{}}}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_node");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("{}", false)]
    [Arguments("""{"text":"value"}""", true)]
    [Arguments("""{"unit":{}}""", true)]
    [Arguments("""{"text":"value","unit":{}}""", false)]
    [Arguments("""{"unknown":{}}""", false)]
    [Arguments("""{"text":null}""", false)]
    public async Task SmithyJsonCustomTraitNodesRequireExactlyOneKnownUnionMember(string value, bool valid)
    {
        var document = SmithyCustomNodeDocument("union", value);
        document["shapes"]!["example#Custom"]!["members"] = JsonNode.Parse("""
            {"text":{"target":"smithy.api#String"},"unit":{"target":"smithy.api#Unit"}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_node");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonCustomTraitNodesValidateEveryApplicationIncludingMembers()
    {
        var document = SmithyCustomNodeDocument("string", "\"ok\"");
        document["shapes"]!["example#Get"]!["input"] = JsonNode.Parse("""{"target":"example#Input"}""");
        document["shapes"]!["example#Input"] = JsonNode.Parse("""
            {"type":"structure","members":{"value":{"target":"smithy.api#String","traits":{"example#Custom":42}}}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_node");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/value/traits/example#Custom");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonCustomTraitNodesRetainMissingSchemaDependenciesWithoutAcquisition()
    {
        var document = SmithyCustomNodeDocument("structure", "{}");
        document["shapes"]!["example#Custom"]!["members"] = JsonNode.Parse("""{"optional":{"target":"example#Absent"}}""");
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Schema IDs cannot be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Absent");
    }

    [Test]
    [Arguments("enum", "\"yes\"", true)]
    [Arguments("enum", "1", false)]
    [Arguments("intEnum", "1", true)]
    [Arguments("intEnum", "\"1\"", false)]
    [Arguments("intEnum", "1.5", false)]
    [Arguments("intEnum", "2147483648", false)]
    public async Task SmithyJsonCustomTraitNodesCheckEnumUnderlyingNodeTypes(string kind, string value, bool valid)
    {
        var document = SmithyCustomNodeDocument(kind, value);
        document["shapes"]!["example#Custom"]!["members"] = kind == "enum"
            ? JsonNode.Parse("""{"YES":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"yes"}}}""")
            : JsonNode.Parse("""{"ONE":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_node");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonCustomTraitNodesValidateFiniteValuesAgainstRecursiveSchemas()
    {
        var document = SmithyCustomNodeDocument("structure", """{"value":{"next":{"next":{}}}}""");
        document["shapes"]!["example#Custom"]!["members"] = JsonNode.Parse("""
            {"value":{"target":"example#Node","traits":{"smithy.api#required":{}}}}
            """);
        document["shapes"]!["example#Node"] = JsonNode.Parse("""
            {"type":"structure","members":{"next":{"target":"example#Node"}}}
            """);
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        document["shapes"]!["example#Get"]!["traits"]!["example#Custom"]!["value"]!["next"]!["next"] = false;
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_node");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/value/next/next");
    }

    [Test]
    [Arguments("\"2026-02-30T00:00:00Z\"", ApiOperationResolutionStatus.Invalid, "document.default_timestamp")]
    [Arguments("\"2026-10-30T19:00:00+01:00\"", ApiOperationResolutionStatus.Invalid, "document.default_timestamp")]
    [Arguments("\"2026-10-30T19:00:00.0001Z\"", ApiOperationResolutionStatus.Unsupported, "smithy.timestamp_default")]
    public async Task SmithyJsonCustomTraitNodesRetainTimestampValidationAndUnsupportedBoundaries(
        string value, ApiOperationResolutionStatus status, string diagnostic)
    {
        var result = ResolveSmithy(SmithyCustomNodeDocument("timestamp", value).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(diagnostic);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("string", "\"\"", false)]
    [Arguments("string", "\"a\"", true)]
    [Arguments("string", "\"ab\"", true)]
    [Arguments("string", "\"abc\"", false)]
    [Arguments("string", "\"\\uD83D\\uDE00\"", true)]
    [Arguments("string", "\"e\\u0301\"", true)]
    [Arguments("string", "\"e\\u0301x\"", false)]
    [Arguments("blob", "\"\"", false)]
    [Arguments("blob", "\"AQ==\"", true)]
    [Arguments("blob", "\"AQI=\"", true)]
    [Arguments("blob", "\"AQID\"", false)]
    [Arguments("list", "[]", false)]
    [Arguments("list", """["a"]""", true)]
    [Arguments("list", """["a","b"]""", true)]
    [Arguments("list", """["a","b","c"]""", false)]
    [Arguments("map", "{}", false)]
    [Arguments("map", """{"a":"v"}""", true)]
    [Arguments("map", """{"a":"v","b":"w"}""", true)]
    [Arguments("map", """{"a":"v","b":"w","c":"x"}""", false)]
    public async Task SmithyJsonCustomTraitLengthUsesScalarByteAndCollectionCounts(string kind, string value, bool valid)
    {
        var document = SmithyCustomNodeDocument(kind, value);
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#length"] =
            JsonNode.Parse("""{"min":1,"max":2}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_length");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("integer", "0", false)]
    [Arguments("integer", "1", true)]
    [Arguments("integer", "2", true)]
    [Arguments("integer", "3", false)]
    [Arguments("bigDecimal", "\"0.99999999999999999999999999999\"", false)]
    [Arguments("bigDecimal", "\"1.00000000000000000000000000001\"", true)]
    [Arguments("bigDecimal", "\"2.00000000000000000000000000001\"", false)]
    [Arguments("double", "1.5", true)]
    public async Task SmithyJsonCustomTraitRangeUsesExactInclusiveComparisons(string kind, string value, bool valid)
    {
        var document = SmithyCustomNodeDocument(kind, value);
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#range"] =
            JsonNode.Parse("""{"min":1,"max":2}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_range");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure", "length", """{"name":"xx"}""", """{"name":"x"}""")]
    [Arguments("union", "length", """{"name":"xx"}""", """{"name":"x"}""")]
    [Arguments("list", "length", """["xx"]""", """["x"]""")]
    [Arguments("map", "length", """{"key":"xx"}""", """{"key":"x"}""")]
    [Arguments("structure", "range", """{"name":2}""", """{"name":1}""")]
    [Arguments("union", "range", """{"name":2}""", """{"name":1}""")]
    [Arguments("list", "range", "[2]", "[1]")]
    [Arguments("map", "range", """{"key":2}""", """{"key":1}""")]
    public async Task SmithyJsonCustomTraitMemberConstraintsSupersedeRatherThanIntersectTargetConstraints(
        string kind, string constraint, string accepted, string rejected)
    {
        var document = SmithyCustomNodeDocument(kind, accepted);
        var member = JsonNode.Parse("""
            {"target":"example#Value","traits":{}}
            """)!;
        member["traits"]!["smithy.api#" + constraint] = JsonNode.Parse("""{"min":2,"max":2}""");
        document["shapes"]!["example#Value"] = new JsonObject
        {
            ["type"] = constraint == "length" ? "string" : "integer",
            ["traits"] = new JsonObject { ["smithy.api#" + constraint] = JsonNode.Parse("""{"min":1,"max":1}""") }
        };
        if (kind == "list") { document["shapes"]!["example#Custom"]!["member"] = member; }
        else if (kind == "map") { document["shapes"]!["example#Custom"]!["value"] = member; }
        else { document["shapes"]!["example#Custom"]!["members"] = new JsonObject { ["name"] = member }; }
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        document["shapes"]!["example#Get"]!["traits"]!["example#Custom"] = JsonNode.Parse(rejected);
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_" + constraint);
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/" +
            (kind == "list" ? "0" : kind == "map" ? "key" : "name"));
    }

    [Test]
    public async Task SmithyJsonCustomTraitMapKeyConstraintsApplyEvenToSparseNullValues()
    {
        var document = SmithyCustomNodeDocument("map", """{"xx":null}""");
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#sparse"] = new JsonObject();
        document["shapes"]!["example#Custom"]!["key"]!["traits"] =
            JsonNode.Parse("""{"smithy.api#length":{"min":2,"max":2}}""");
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        document["shapes"]!["example#Get"]!["traits"]!["example#Custom"] = JsonNode.Parse("""{"x":null}""");
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_length");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/x");
    }

    [Test]
    [Arguments("float", "\"NaN\"")]
    [Arguments("double", "\"Infinity\"")]
    [Arguments("double", "\"-Infinity\"")]
    public async Task SmithyJsonCustomTraitSpecialFloatingRangesRemainUnsupported(string kind, string value)
    {
        var document = SmithyCustomNodeDocument(kind, value);
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#range"] = JsonNode.Parse("""{"min":0}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.special_range_trait");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("\"aaa\"")]
    [Arguments("\"zzz\"")]
    public async Task SmithyJsonCustomTraitPatternsDoNotSubstituteDotNetMatching(string value)
    {
        var document = SmithyCustomNodeDocument("string", value);
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#pattern"] = "^a+$";
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.pattern_trait");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonCustomTraitOmittedMembersDoNotRequireConstraintEvaluation()
    {
        var document = SmithyCustomNodeDocument("structure", "{}");
        document["shapes"]!["example#Custom"]!["members"] = JsonNode.Parse("""
            {"optional":{"target":"example#Pattern"}}
            """);
        document["shapes"]!["example#Pattern"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#pattern":"^a+$","smithy.api#length":{"min":1}}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetProperty("traits").GetProperty("example#Custom").GetRawText()).IsEqualTo("{}");
    }

    [Test]
    [Arguments("\"\\uD83D\\uDE00\"", 1)]
    [Arguments("\"\\\"\"", 1)]
    [Arguments("\"\\\\\"", 1)]
    [Arguments("\"a/b~c\"", 5)]
    [Arguments("\"\\n\"", 1)]
    public async Task SmithyJsonCustomTraitMapKeysUseDecodedStringsAndInheritedLength(string key, int length)
    {
        var document = SmithyCustomNodeDocument("map", "{" + key + ":\"value\"}");
        document["shapes"]!["example#Custom"]!["key"]!["target"] = "example#Key";
        document["shapes"]!["example#Key"] = new JsonObject
        {
            ["type"] = "string",
            ["traits"] = new JsonObject
            {
                ["smithy.api#length"] = new JsonObject { ["min"] = length, ["max"] = length }
            }
        };
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
        document["shapes"]!["example#Key"]!["traits"]!["smithy.api#length"]!["min"] = length + 1;
        document["shapes"]!["example#Key"]!["traits"]!["smithy.api#length"]!["max"] = length + 1;
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_length");
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("length", """{"name":"longer"}""")]
    [Arguments("range", """{"name":100}""")]
    public async Task SmithyJsonCustomTraitSingleSidedMemberConstraintsReplaceWholeTargetConstraint(
        string constraint, string value)
    {
        var document = SmithyCustomNodeDocument("structure", value);
        document["shapes"]!["example#Custom"]!["members"] = new JsonObject
        {
            ["name"] = new JsonObject
            {
                ["target"] = "example#Value",
                ["traits"] = new JsonObject { ["smithy.api#" + constraint] = JsonNode.Parse("""{"min":2}""") }
            }
        };
        document["shapes"]!["example#Value"] = new JsonObject
        {
            ["type"] = constraint == "length" ? "string" : "integer",
            ["traits"] = new JsonObject { ["smithy.api#" + constraint] = JsonNode.Parse("""{"max":1}""") }
        };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("-62135596800.001", false)]
    [Arguments("-62135596800", true)]
    [Arguments("-62135596799.999", true)]
    [Arguments("253402300799.998", true)]
    [Arguments("253402300799.999", true)]
    [Arguments("253402300800", false)]
    [Arguments("0", true)]
    [Arguments("-0", true)]
    [Arguments("0e-100", true)]
    [Arguments("0.001", true)]
    [Arguments("-0.001", true)]
    [Arguments("0.0001", false)]
    [Arguments("-0.0001", false)]
    [Arguments("1.2340", true)]
    [Arguments("1.2341", false)]
    [Arguments("1234e-3", true)]
    [Arguments("12341e-4", false)]
    [Arguments("1e100", false)]
    public async Task SmithyJsonCustomTraitTimestampsUseExactEpochRangeAndMillisecondBoundaries(string value, bool valid)
    {
        var document = SmithyCustomNodeDocument("timestamp", value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_timestamp");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("structure", """{"time":253402300800}""", "/time")]
    [Arguments("union", """{"time":253402300800}""", "/time")]
    [Arguments("list", "[0,253402300800]", "/1")]
    [Arguments("map", """{"ok":0,"a/b~c":253402300800}""", "/a~1b~0c")]
    public async Task SmithyJsonCustomTraitTimestampsValidateNestedPreludeTargets(string kind, string value, string suffix)
    {
        var document = SmithyCustomNodeDocument(kind, value);
        var reference = JsonNode.Parse("""{"target":"smithy.api#Timestamp"}""");
        if (kind == "list") { document["shapes"]!["example#Custom"]!["member"] = reference; }
        else if (kind == "map") { document["shapes"]!["example#Custom"]!["value"] = reference; }
        else { document["shapes"]!["example#Custom"]!["members"] = new JsonObject { ["time"] = reference }; }
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_timestamp");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom" + suffix);
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        document["shapes"]!["example#Get"]!["traits"]!["example#Custom"] =
            JsonNode.Parse(value.Replace("253402300800", "253402300799.999", StringComparison.Ordinal));
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Diagnostic).IsNull();
    }

    [Test]
    public async Task SmithyJsonCustomTraitTimestampChecksDoNotInterpretDocumentOrUnrelatedValues()
    {
        var document = SmithyCustomNodeDocument("document", """{"time":253402300800,"fraction":0.0001}""");
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"timestamp","traits":{"example#Custom":253402300800}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    [Arguments("string", "[]", true, -1)]
    [Arguments("string", """["one"]""", true, -1)]
    [Arguments("string", """["a","b","a"]""", false, 2)]
    [Arguments("string", """["a","A"]""", true, -1)]
    [Arguments("string", """["\u00e9","e\u0301"]""", true, -1)]
    [Arguments("string", """["\u0061","a"]""", false, 1)]
    [Arguments("string", """["a","a\u0000"]""", true, -1)]
    [Arguments("boolean", "[true,false]", true, -1)]
    [Arguments("boolean", "[false,false]", false, 1)]
    [Arguments("boolean", "[true,true]", false, 1)]
    [Arguments("blob", """["YQ=="," Y Q = = \n"]""", false, 1)]
    [Arguments("blob", """["YQ==","Yg=="]""", true, -1)]
    [Arguments("blob", """[""," \n"]""", false, 1)]
    [Arguments("integer", "[1,2]", true, -1)]
    [Arguments("bigDecimal", """["1.001",1.002]""", true, -1)]
    public async Task SmithyJsonCustomTraitUniqueItemsUseTypedScalarEquality(string kind, string value, bool valid, int duplicateIndex)
    {
        var document = SmithyUniqueTraitDocument(kind, value);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_unique_items");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/" +
                duplicateIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("byte", "[-0,0]")]
    [Arguments("short", "[1,1.00]")]
    [Arguments("integer", "[123,1.23e2]")]
    [Arguments("long", "[9223372036854775807,9223372036854775807.0]")]
    [Arguments("bigInteger", """["123456789012345678901234567890",123456789012345678901234567890]""")]
    [Arguments("bigDecimal", """["1.2340",1234e-3]""")]
    [Arguments("bigDecimal", """["0e-100",0]""")]
    [Arguments("bigDecimal", """["1e1000","10e999"]""")]
    public async Task SmithyJsonCustomTraitUniqueItemsCompareExactMathematicalNumbers(string kind, string value)
    {
        var result = ResolveSmithy(SmithyUniqueTraitDocument(kind, value).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_unique_items");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/1");
    }

    [Test]
    [Arguments("""[0,"1970-01-01T00:00:00Z"]""", false)]
    [Arguments("""[-0.001,"1969-12-31T23:59:59.999Z"]""", false)]
    [Arguments("""[0.1,"1970-01-01T00:00:00.100000Z"]""", false)]
    [Arguments("""[0.12,"1970-01-01T00:00:00.12Z"]""", false)]
    [Arguments("""["1970-01-01T00:00:00.0Z","1970-01-01T00:00:00Z"]""", false)]
    [Arguments("""[-62135596800,"0001-01-01T00:00:00Z"]""", false)]
    [Arguments("""[253402300799.999,"9999-12-31T23:59:59.999Z"]""", false)]
    [Arguments("""[0e-100,"1970-01-01T00:00:00Z"]""", false)]
    [Arguments("""[0,"1970-01-01T00:00:00.001Z"]""", true)]
    [Arguments("""["1969-12-31T23:59:59.999Z","1970-01-01T00:00:00.001Z"]""", true)]
    public async Task SmithyJsonCustomTraitUniqueItemsCompareTimestampInstants(string value, bool valid)
    {
        var result = ResolveSmithy(SmithyUniqueTraitDocument("timestamp", value).ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostic).IsNull(); }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_unique_items");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/1");
        }
    }

    [Test]
    [Arguments("[[],[]]", false)]
    [Arguments("[[1,2],[1.0,2e0]]", false)]
    [Arguments("[[1,2],[2,1]]", true)]
    [Arguments("[[1],[1,2]]", true)]
    [Arguments("[[1,2],[1,3]]", true)]
    public async Task SmithyJsonCustomTraitUniqueItemsPreserveNestedListOrder(string value, bool valid)
    {
        var document = SmithyUniqueTraitDocument("list", value);
        document["shapes"]!["example#Item"]!["member"] = JsonNode.Parse("""{"target":"smithy.api#Integer"}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (!valid) { await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_unique_items"); }
        else { await Assert.That(result.Diagnostic).IsNull(); }
    }

    [Test]
    [Arguments("""[{},{}]""", false)]
    [Arguments("""[{"a":1,"b":2},{"b":2.0,"a":1e0}]""", false)]
    [Arguments("""[{"a":1},{"a":2}]""", true)]
    [Arguments("""[{"a":1},{"b":1}]""", true)]
    [Arguments("""[{"a":1},{"a":1,"b":2}]""", true)]
    [Arguments("""[{"a":null},{"a":null}]""", false)]
    [Arguments("""[{"a":null},{"a":0}]""", true)]
    [Arguments("""[{"a":null},{}]""", true)]
    [Arguments("""[{"a/b~c":1},{"a/b~c":1.0}]""", false)]
    public async Task SmithyJsonCustomTraitUniqueItemsCompareUnorderedSparseMaps(string value, bool valid)
    {
        var document = SmithyUniqueTraitDocument("map", value);
        document["shapes"]!["example#Item"]!["value"] = JsonNode.Parse("""{"target":"smithy.api#Integer"}""");
        document["shapes"]!["example#Item"]!["traits"] = JsonNode.Parse("""{"smithy.api#sparse":{}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (!valid) { await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_unique_items"); }
        else { await Assert.That(result.Diagnostic).IsNull(); }
    }

    [Test]
    [Arguments("structure", """[{"a":1,"b":2},{"b":2.0,"a":1e0}]""", false)]
    [Arguments("structure", """[{"a":1},{"a":1,"b":0}]""", true)]
    [Arguments("structure", """[{"a":1},{"a":2}]""", true)]
    [Arguments("union", """[{"a":1},{"a":1.0}]""", false)]
    [Arguments("union", """[{"a":1},{"b":1}]""", true)]
    [Arguments("union", """[{"a":1},{"a":2}]""", true)]
    public async Task SmithyJsonCustomTraitUniqueItemsCompareTypedAggregateMembers(string kind, string value, bool valid)
    {
        var document = SmithyUniqueTraitDocument(kind, value);
        document["shapes"]!["example#Item"]!["members"] = JsonNode.Parse("""
            {"a":{"target":"smithy.api#Integer"},"b":{"target":"smithy.api#Integer","traits":{"smithy.api#default":0}}}
            """);
        if (kind == "union") { document["shapes"]!["example#Item"]!["members"]!["b"]!.AsObject().Remove("traits"); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (!valid) { await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_unique_items"); }
        else
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
    }

    [Test]
    public async Task SmithyJsonCustomTraitUniqueItemsValidateNestedApplicationsAndEscapedPaths()
    {
        var document = SmithyUniqueTraitDocument("integer", "[1,2]");
        document["shapes"]!["example#Custom"]!["traits"]!.AsObject().Remove("smithy.api#trait");
        document["shapes"]!["example#Wrapper"] = JsonNode.Parse("""
            {"type":"map","traits":{"smithy.api#trait":{}},"key":{"target":"smithy.api#String"},
             "value":{"target":"example#Custom"}}
            """);
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"example#Wrapper":{"a/b~c":[1,1.0]}}
            """);
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_unique_items");
        await Assert.That(invalid.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Wrapper/a~1b~0c/1");
    }

    [Test]
    [Arguments("String", """["a","a"]""")]
    [Arguments("Integer", "[1,1.0]")]
    [Arguments("Timestamp", """[0,"1970-01-01T00:00:00Z"]""")]
    [Arguments("Unit", "[{},{}]")]
    [Arguments("Blob", """["YQ=="," YQ=="]""")]
    [Arguments("Boolean", "[false,false]")]
    public async Task SmithyJsonCustomTraitUniqueItemsCompareImplicitPreludeTargets(string type, string value)
    {
        var document = SmithyUniqueTraitDocument("string", value);
        document["shapes"]!["example#Custom"]!["member"]!["target"] = "smithy.api#" + type;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_unique_items");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/1");
    }

    [Test]
    [Arguments("enum", """["a","a"]""")]
    [Arguments("intEnum", "[1,1.0]")]
    public async Task SmithyJsonCustomTraitUniqueItemsCompareEnumValues(string kind, string value)
    {
        var document = SmithyUniqueTraitDocument(kind, value);
        document["shapes"]!["example#Item"]!["members"] = kind == "enum"
            ? JsonNode.Parse("""{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"a"}}}""")
            : JsonNode.Parse("""{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}""");
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_unique_items");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/1");
    }

    [Test]
    public async Task SmithyJsonCustomTraitUniqueItemsDoNotConstrainOrdinaryListsOrLiteralDocuments()
    {
        var document = SmithyUniqueTraitDocument("integer", "[1,1]");
        document["shapes"]!["example#Custom"]!["traits"]!.AsObject().Remove("smithy.api#uniqueItems");
        document["shapes"]!["example#Unused"] = JsonNode.Parse("""
            {"type":"list","member":{"target":"smithy.api#Integer"},"traits":{"smithy.api#trait":{},"smithy.api#uniqueItems":{}}}
            """);
        document["shapes"]!["example#Literal"] = JsonNode.Parse("""{"type":"document","traits":{"smithy.api#trait":{}}}""");
        document["shapes"]!["example#Get"]!["traits"]!["example#Literal"] = JsonNode.Parse("""
            {"example#Unused":[1,1],"smithy.api#uniqueItems":[true,true]}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    public async Task SmithyJsonCustomTraitUniqueItemsBoundComparisonWork()
    {
        var document = SmithyUniqueTraitDocument("integer", "[]");
        var values = new JsonArray();
        for (var index = 0; index < 1600; index++) { values.Add(index); }
        document["shapes"]!["example#Get"]!["traits"]!["example#Custom"] = values;
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.work");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("\"alpha\"", true)]
    [Arguments("\"Alpha\"", false)]
    [Arguments("\"\"", false)]
    [Arguments("\"0\"", true)]
    [Arguments("\"\\u00e9\"", true)]
    [Arguments("\"e\\u0301\"", false)]
    public async Task SmithyJsonCustomTraitLegacyEnumsMatchExactDeclaredStrings(string value, bool valid)
    {
        var document = SmithyCustomNodeDocument("string", value);
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#enum"] = JsonNode.Parse("""
            [{"value":"alpha"},{"value":"0"},{"value":"\u00e9"}]
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_enum");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("list", """["other"]""", "/0")]
    [Arguments("map", """{"a/b~c":"other"}""", "/a~1b~0c")]
    [Arguments("structure", """{"value":"other"}""", "/value")]
    [Arguments("union", """{"value":"other"}""", "/value")]
    public async Task SmithyJsonCustomTraitLegacyEnumsValidateNestedValues(string kind, string value, string suffix)
    {
        var document = SmithyCustomNodeDocument(kind, value);
        document["shapes"]!["example#Text"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#enum":[{"value":"allowed"}]}}
            """);
        var reference = JsonNode.Parse("""{"target":"example#Text"}""");
        if (kind == "list") { document["shapes"]!["example#Custom"]!["member"] = reference; }
        else if (kind == "map") { document["shapes"]!["example#Custom"]!["value"] = reference; }
        else { document["shapes"]!["example#Custom"]!["members"] = new JsonObject { ["value"] = reference }; }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_enum");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom" + suffix);
        document["shapes"]!["example#Get"]!["traits"]!["example#Custom"] =
            JsonNode.Parse(value.Replace("other", "allowed", StringComparison.Ordinal));
        var valid = ResolveSmithy(document.ToJsonString());
        await Assert.That(valid.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(valid.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("""{"allowed":"value"}""", true)]
    [Arguments("""{"allowed":null}""", true)]
    [Arguments("""{"a/b~c":"value"}""", false)]
    [Arguments("""{"a/b~c":null}""", false)]
    public async Task SmithyJsonCustomTraitLegacyEnumsCheckDecodedMapKeysBeforeSparseValues(string value, bool valid)
    {
        var document = SmithyCustomNodeDocument("map", value);
        document["shapes"]!["example#Key"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#enum":[{"value":"allowed"}]}}
            """);
        document["shapes"]!["example#Custom"]!["key"]!["target"] = "example#Key";
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#sparse"] = new JsonObject();
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (!valid)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_enum");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom/a~1b~0c");
        }
        else { await Assert.That(result.Diagnostic).IsNull(); }
    }

    [Test]
    public async Task SmithyJsonCustomTraitLegacyEnumsCheckEveryReachedApplication()
    {
        var document = SmithyCustomNodeDocument("string", "\"allowed\"");
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#enum"] =
            JsonNode.Parse("""[{"value":"allowed"}]""");
        document["shapes"]!["example#Get"]!["input"] = JsonNode.Parse("""{"target":"example#Input"}""");
        document["shapes"]!["example#Input"] = JsonNode.Parse("""
            {"type":"structure","members":{"value":{"target":"smithy.api#String","traits":{"example#Custom":"other"}}}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_enum");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/value/traits/example#Custom");
    }

    [Test]
    public async Task SmithyJsonCustomTraitLegacyEnumsLeaveOptionalDefaultsOmitted()
    {
        var document = SmithyCustomNodeDocument("structure", "{}");
        document["shapes"]!["example#Text"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#enum":[{"value":"allowed"}]}}
            """);
        document["shapes"]!["example#Custom"]!["members"] = JsonNode.Parse("""
            {"value":{"target":"example#Text","traits":{"smithy.api#default":"allowed"}}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    [Test]
    public async Task SmithyJsonCustomTraitLegacyEnumsDoNotConstrainLiteralOrUnrelatedValues()
    {
        var document = SmithyCustomNodeDocument("document", """{"example#Text":"other"}""");
        document["shapes"]!["example#Text"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#enum":[{"value":"allowed"}],"example#Custom":"other"}}
            """);
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
    }

    private static JsonObject SmithyUniqueTraitDocument(string kind, string value)
    {
        var document = SmithyCustomNodeDocument("list", value);
        document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#uniqueItems"] = new JsonObject();
        document["shapes"]!["example#Custom"]!["member"]!["target"] = "example#Item";
        var item = SmithyCustomNodeDocument(kind, "{}")["shapes"]!["example#Custom"]!.DeepClone();
        item.AsObject().Remove("traits");
        document["shapes"]!["example#Item"] = item;
        return document;
    }

    private static JsonObject SmithyCustomNodeDocument(string kind, string value)
    {
        var shape = new JsonObject { ["type"] = kind, ["traits"] = JsonNode.Parse("""{"smithy.api#trait":{}}""") };
        if (kind == "list") { shape["member"] = JsonNode.Parse("""{"target":"smithy.api#String"}"""); }
        if (kind == "map")
        {
            shape["key"] = JsonNode.Parse("""{"target":"smithy.api#String"}""");
            shape["value"] = JsonNode.Parse("""{"target":"smithy.api#String"}""");
        }
        if (kind == "structure") { shape["members"] = new JsonObject(); }
        if (kind == "union") { shape["members"] = JsonNode.Parse("""{"text":{"target":"smithy.api#String"}}"""); }
        return new JsonObject
        {
            ["smithy"] = "2.0",
            ["shapes"] = new JsonObject
            {
                ["example#Get"] = new JsonObject
                {
                    ["type"] = "operation",
                    ["traits"] = new JsonObject { ["example#Custom"] = JsonNode.Parse(value) }
                },
                ["example#Custom"] = shape
            }
        };
    }

    private static JsonNode SmithyProtocolDocument()
    {
        var document = SmithyAuthenticationDocument();
        document["shapes"]!["example#Scheme"]!["traits"]!["smithy.api#authDefinition"] =
            JsonNode.Parse("""{"traits":["example#Protocol"]}""");
        document["shapes"]!["example#Protocol"] = JsonNode.Parse("""
            {"type":"structure","traits":{"smithy.api#trait":{"selector":"service"},
             "smithy.api#protocolDefinition":{"traits":["example#Dependency"]}}}
            """);
        document["shapes"]!["example#Dependency"] = JsonNode.Parse("""
            {"type":"string","traits":{"smithy.api#trait":{}}}
            """);
        return document;
    }

    private static JsonNode SmithyAuthenticationDocument()
    {
        var document = SmithyEndpointDocument();
        document["shapes"]!["example#Get"]!["traits"]!["smithy.api#auth"] = JsonNode.Parse("""["example#Scheme"]""");
        document["shapes"]!["example#Scheme"] = JsonNode.Parse("""
            {"type":"structure","traits":{"smithy.api#trait":{"selector":"service"},"smithy.api#authDefinition":{}}}
            """);
        return document;
    }

    private static JsonNode SmithyEndpointDocument()
    {
        var document = SmithyHttpOperationDocument();
        document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""
            {"smithy.api#endpoint":{"hostPrefix":"{id}.data."}}
            """);
        document["shapes"]!["example#Input"]!["members"]!["id"]!["traits"]!.AsObject().Remove("smithy.api#httpLabel");
        document["shapes"]!["example#Input"]!["members"]!["id"]!["traits"]!["smithy.api#hostLabel"] = new JsonObject();
        return document;
    }

    private static JsonNode SmithyHttpPayloadDocument(string role)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"smithy.api#http":{"method":"GET","uri":"/"}}},
              "example#Container":{"type":"structure","members":{
                "body":{"target":"example#Body","traits":{"smithy.api#httpPayload":{}}},
                "metadata":{"target":"smithy.api#String","traits":{"smithy.api#httpHeader":"X-Metadata"}}}},
              "example#Body":{"type":"string"}}}
            """)!;
        if (role == "error")
        {
            document["shapes"]!["example#Get"]!["errors"] = JsonNode.Parse("""[{"target":"example#Container"}]""");
            document["shapes"]!["example#Container"]!["traits"] = JsonNode.Parse("""{"smithy.api#error":"server"}""");
        }
        else
        {
            document["shapes"]!["example#Get"]![role] = JsonNode.Parse("""{"target":"example#Container"}""");
        }
        return document;
    }

    private static JsonNode SmithyHttpOperationDocument() => JsonNode.Parse("""
        {"smithy":"2.0","shapes":{
          "example#Get":{"type":"operation","input":{"target":"example#Input"},
            "traits":{"smithy.api#http":{"method":"GET","uri":"/{id}"}}},
          "example#Input":{"type":"structure","members":{
            "id":{"target":"example#Identifier","traits":{"smithy.api#required":{},"smithy.api#httpLabel":{}}}}},
          "example#Identifier":{"type":"string"}}}
        """)!;

    private static JsonNode SmithyHttpStatusDocument() => JsonNode.Parse("""
        {"smithy":"2.0","shapes":{
          "example#Get":{"type":"operation","input":{"target":"example#Input"},"errors":[{"target":"example#Failure"}]},
          "example#Input":{"type":"structure","members":{"status":{"target":"example#Status","traits":{"smithy.api#httpResponseCode":{}}}}},
          "example#Status":{"type":"integer"},
          "example#Failure":{"type":"structure","traits":{"smithy.api#error":"client","smithy.api#httpError":404}}}}
        """)!;

    private static JsonNode SmithyExternalDocumentationDocument(JsonNode? annotation)
    {
        var document = SmithyEventBindingDocument("eventHeader");
        document["shapes"]!["example#Get"]!["traits"] = new JsonObject
        { ["smithy.api#externalDocumentation"] = annotation };
        return document;
    }

    private static JsonNode SmithyMediaTypeDocument(string kind, JsonNode? mediaType)
    {
        var document = SmithyEventBindingDocument("eventHeader");
        document["shapes"]!["example#Event"]!["members"]!["data"]!.AsObject().Remove("traits");
        if (kind == "operation")
        { document["shapes"]!["example#Get"]!["traits"] = new JsonObject { ["smithy.api#mediaType"] = mediaType }; }
        else if (kind == "member")
        { document["shapes"]!["example#Event"]!["members"]!["data"]!["traits"] = new JsonObject { ["smithy.api#mediaType"] = mediaType }; }
        else
        {
            var target = JsonNode.Parse(kind switch
            {
                "enum" => """{"type":"enum","members":{"one":{"target":"smithy.api#Unit"}}}""",
                "intEnum" => """{"type":"intEnum","members":{"one":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""",
                _ => """{"type":"KIND"}""".Replace("KIND", kind, StringComparison.Ordinal)
            })!;
            target["traits"] = new JsonObject { ["smithy.api#mediaType"] = mediaType };
            document["shapes"]!["example#Target"] = target;
        }
        return document;
    }

    private static JsonNode SmithyEventBindingDocument(string trait)
    {
        var document = JsonNode.Parse(SmithyStreamingDocument())!;
        document["shapes"]!["example#Stream"] = JsonNode.Parse(
            """{"type":"union","traits":{"smithy.api#streaming":{}},"members":{"event":{"target":"example#Event"}}}""");
        document["shapes"]!["example#Event"] = JsonNode.Parse("""
            {"type":"structure","members":{"data":{"target":"example#Target","traits":{"smithy.api#TRAIT":{}}}}}
            """.Replace("TRAIT", trait, StringComparison.Ordinal));
        document["shapes"]!["example#Target"] = JsonNode.Parse("""{"type":"string"}""");
        return document;
    }

    private static string SmithyStreamingDocument() => """
        {"smithy":"2.0","shapes":{
          "example#Get":{"type":"operation","input":{"target":"example#Input"}},
          "example#Input":{"type":"structure","members":{"data":{"target":"example#Stream","traits":{"smithy.api#required":{}}}}},
          "example#Stream":{"type":"blob","traits":{"smithy.api#streaming":{}}}}}
        """;

    private static string SmithyExampleDocument(string examples) => """
        {"smithy":"2.0","shapes":{
          "example#Get":{"type":"operation","input":{"target":"example#Input"},
            "errors":[{"target":"example#Failure"}],"traits":{"smithy.api#examples":EXAMPLES}},
          "example#Input":{"type":"structure","members":{"value":{"target":"example#Values"}}},
          "example#Values":{"type":"list","member":{"target":"smithy.api#String"}},
          "example#Failure":{"type":"structure","traits":{"smithy.api#error":"client"}},
          "example#OtherFailure":{"type":"structure","traits":{"smithy.api#error":"server"}},
          "example#Service":{"type":"service","version":"1","operations":[{"target":"example#Get"}],
            "errors":[{"target":"example#OtherFailure"}]}}}
        """.Replace("EXAMPLES", examples, StringComparison.Ordinal);

    private static string SmithyDefaultDocument(string kind, string value, bool rootDefault)
    {
        var target = new JsonObject { ["type"] = kind };
        if (kind == "list") { target["member"] = new JsonObject { ["target"] = "smithy.api#String" }; }
        if (kind == "map")
        {
            target["key"] = new JsonObject { ["target"] = "smithy.api#String" };
            target["value"] = new JsonObject { ["target"] = "smithy.api#String" };
        }
        if (kind is "enum" or "intEnum" or "union")
        {
            target["members"] = JsonNode.Parse(kind == "enum"
                ? """{"A":{"target":"smithy.api#Unit"},"B":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"authored"}}}"""
                : kind == "intEnum"
                    ? """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}"""
                    : """{"A":{"target":"smithy.api#String"}}""");
        }
        if (rootDefault) { target["traits"] = new JsonObject { ["smithy.api#default"] = JsonNode.Parse(value) }; }
        return new JsonObject
        {
            ["smithy"] = "2.0",
            ["shapes"] = new JsonObject
            {
                ["example#Get"] = JsonNode.Parse("""{"type":"operation","input":{"target":"example#Input"}}"""),
                ["example#Input"] = new JsonObject
                {
                    ["type"] = "structure",
                    ["members"] = new JsonObject
                    {
                        ["value"] = new JsonObject
                        {
                            ["target"] = "example#Value",
                            ["traits"] = new JsonObject { ["smithy.api#default"] = JsonNode.Parse(value) }
                        }
                    }
                },
                ["example#Value"] = target
            }
        }.ToJsonString();
    }

    [Test]
    [Arguments("""{"selector":null}""", "document.trait_selector", "/selector")]
    [Arguments("""{"selector":{}}""", "document.trait_selector", "/selector")]
    [Arguments("""{"structurallyExclusive":null}""", "document.trait_exclusive", "/structurallyExclusive")]
    [Arguments("""{"structurallyExclusive":"Member"}""", "document.trait_exclusive", "/structurallyExclusive")]
    [Arguments("""{"structurallyExclusive":"shape"}""", "document.trait_exclusive", "/structurallyExclusive")]
    [Arguments("""{"conflicts":null}""", "document.trait_conflicts", "/conflicts")]
    [Arguments("""{"conflicts":{}}""", "document.trait_conflicts", "/conflicts")]
    [Arguments("""{"conflicts":[1]}""", "document.trait_conflicts", "/conflicts/0")]
    [Arguments("""{"conflicts":["Other"]}""", "document.trait_conflicts", "/conflicts/0")]
    [Arguments("""{"conflicts":["example#Other$member"]}""", "document.trait_conflicts", "/conflicts/0")]
    [Arguments("""{"conflicts":["example#Unknown",null]}""", "document.trait_conflicts", "/conflicts/1")]
    public async Task SmithyJsonTraitMarkerPropertiesRetainTheirDeclaredTypes(string marker, string code, string suffix)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"example#Custom":{}}},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":MARKER}}}}
            """.Replace("MARKER", marker, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Custom/traits/smithy.api#trait" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("null", "document.trait_breaking_changes", "")]
    [Arguments("{}", "document.trait_breaking_changes", "")]
    [Arguments("[]", "document.trait_breaking_changes", "")]
    [Arguments("[null]", "document.trait_breaking_changes", "/0")]
    [Arguments("[1]", "document.trait_breaking_changes", "/0")]
    [Arguments("[{}]", "document.trait_change", "/0/change")]
    [Arguments("""[{"change":null}]""", "document.trait_change", "/0/change")]
    [Arguments("""[{"change":1}]""", "document.trait_change", "/0/change")]
    [Arguments("""[{"change":"ADD"}]""", "document.trait_change", "/0/change")]
    [Arguments("""[{"change":""}]""", "document.trait_change", "/0/change")]
    [Arguments("""[{"change":"delete"}]""", "document.trait_change", "/0/change")]
    [Arguments("""[{"change":"add","severity":null}]""", "document.trait_severity", "/0/severity")]
    [Arguments("""[{"change":"add","severity":1}]""", "document.trait_severity", "/0/severity")]
    [Arguments("""[{"change":"add","severity":"error"}]""", "document.trait_severity", "/0/severity")]
    [Arguments("""[{"change":"add","severity":""}]""", "document.trait_severity", "/0/severity")]
    [Arguments("""[{"change":"add","severity":"FATAL"}]""", "document.trait_severity", "/0/severity")]
    [Arguments("""[{"change":"add","message":null}]""", "document.trait_message", "/0/message")]
    [Arguments("""[{"change":"add","message":{}}]""", "document.trait_message", "/0/message")]
    [Arguments("""[{"change":"add","path":null}]""", "document.trait_pointer", "/0/path")]
    [Arguments("""[{"change":"add","path":[]}]""", "document.trait_pointer", "/0/path")]
    [Arguments("""[{"change":"add","path":"name"}]""", "document.trait_pointer", "/0/path")]
    [Arguments("""[{"change":"add","path":"#/name"}]""", "document.trait_pointer", "/0/path")]
    [Arguments("""[{"change":"add","path":"/~"}]""", "document.trait_pointer", "/0/path")]
    [Arguments("""[{"change":"add","path":"/~2"}]""", "document.trait_pointer", "/0/path")]
    [Arguments("""[{"change":"add"},{"change":"unknown"}]""", "document.trait_change", "/1/change")]
    public async Task SmithyJsonBreakingChangeDeclarationsRejectMalformedFields(string rules, string code, string suffix)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"example#Custom":{}}},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{"breakingChanges":RULES}}}}}
            """.Replace("RULES", rules, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Custom/traits/smithy.api#trait/breakingChanges" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("add", "ERROR", "")]
    [Arguments("remove", "DANGER", "/value")]
    [Arguments("update", "WARNING", "/list/member")]
    [Arguments("any", "NOTE", "/map/key")]
    [Arguments("presence", "ERROR", "/map/value")]
    [Arguments("update", "NOTE", "/a~1b/~0name")]
    [Arguments("any", "DANGER", "/")]
    [Arguments("remove", "WARNING", "//")]
    [Arguments("add", "NOTE", "/~01")]
    public async Task SmithyJsonBreakingChangeDeclarationsPreserveNativeFieldsAndDoNotExecuteRules(
        string change, string severity, string rulePath)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"example#Custom":{}}},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{"breakingChanges":[]}}}}}
            """)!;
        var rules = document["shapes"]!["example#Custom"]!["traits"]!["smithy.api#trait"]!["breakingChanges"]!.AsArray();
        rules.Add(new JsonObject { ["change"] = change, ["severity"] = severity, ["path"] = rulePath,
            ["message"] = "https://must-not-load.example/schema" });
        rules.Add(new JsonObject { ["change"] = "presence", ["message"] = "" });
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Diff rules are declarations, not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(document["shapes"]!["example#Get"]!.ToJsonString());
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    public async Task SmithyJsonBreakingChangeChecksReachNestedTraitDefinitionsButNotUnusedShapesOrLiteralData()
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"example#Custom":{"breakingChanges":null}}},
              "example#Custom":{"type":"document","traits":{"smithy.api#trait":{}}},
              "example#Other":{"type":"structure","traits":{"smithy.api#trait":{"breakingChanges":null}}}}}
            """)!;
        var selected = ResolveSmithy(document.ToJsonString());
        await Assert.That(selected.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(selected.Operation.GetProperty("traits").GetProperty("example#Custom")
            .GetProperty("breakingChanges").ValueKind).IsEqualTo(JsonValueKind.Null);
        document["shapes"]!["example#Custom"]!["traits"]!["example#Other"] = new JsonObject();
        var invalid = ResolveSmithy(document.ToJsonString());
        await Assert.That(invalid.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(invalid.Diagnostic!.Code).IsEqualTo("document.trait_breaking_changes");
        await Assert.That(invalid.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#Other/traits/smithy.api#trait/breakingChanges");
        await Assert.That(invalid.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("operation", false)]
    [Arguments("operation", true)]
    [Arguments("member", false)]
    [Arguments("member", true)]
    [Arguments("definition", false)]
    [Arguments("definition", true)]
    public async Task SmithyJsonCustomTraitConflictsAreDirectionalAndPropertyOrderIndependent(
        string position, bool reverse)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"},"traits":{"example#Outer":{}}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#String"}}},
              "example#Outer":{"type":"structure","traits":{"smithy.api#trait":{}}},
              "example#First":{"type":"structure","traits":{"smithy.api#trait":{"conflicts":["example#Second"]}}},
              "example#Second":{"type":"structure","traits":{"smithy.api#trait":{}}}}}
            """)!;
        var item = position == "operation" ? document["shapes"]!["example#Get"]!
            : position == "member" ? document["shapes"]!["example#Input"]!["members"]!["value"]!
            : document["shapes"]!["example#Outer"]!;
        var traits = new JsonObject();
        if (position == "definition") { traits["smithy.api#trait"] = new JsonObject(); }
        traits[reverse ? "example#Second" : "example#First"] = new JsonObject();
        traits[reverse ? "example#First" : "example#Second"] = new JsonObject();
        item["traits"] = traits;
        var result = ResolveSmithy(document.ToJsonString());
        var path = position == "operation" ? "$/shapes/example#Get"
            : position == "member" ? "$/shapes/example#Input/members/value" : "$/shapes/example#Outer";
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path).IsEqualTo(path + "/traits/example#First");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("member")]
    [Arguments("target")]
    public async Task SmithyJsonUnappliedConflictIdsDoNotRequireDefinitionsOrBecomeImports(string exclusive)
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"example#Custom":{},"example#other":{}}},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{
                "selector":"*","structurallyExclusive":"EXCLUSIVE","conflicts":["example#Unknown","example#Other"]}}},
              "example#other":{"type":"structure","traits":{"smithy.api#trait":{}}}}}
            """.Replace("EXCLUSIVE", exclusive, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Conflict IDs are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
    }

    [Test]
    public async Task SmithyJsonCustomTraitsCanDeclareConflictsWithPreludeTraits()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"smithy.api#readonly":{},"example#Custom":{}}},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{"conflicts":["smithy.api#readonly"]}}}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_conflict");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/example#Custom");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task SmithyJsonTraitConflictsDoNotCrossShapeBoundariesOrValidateUnusedDefinitions()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"},"traits":{"example#First":{}}},
              "example#Input":{"type":"structure","members":{"value":{"target":"smithy.api#String",
                "traits":{"example#Second":{}}}}},
              "example#First":{"type":"structure","traits":{"smithy.api#trait":{"conflicts":["example#Second"]}}},
              "example#Second":{"type":"structure","traits":{"smithy.api#trait":{"conflicts":[]}}},
              "example#Unused":{"type":"structure","traits":{"smithy.api#trait":{"conflicts":false}}}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("traits").TryGetProperty("example#First", out _)).IsTrue();
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("member", 0, true)]
    [Arguments("member", 1, true)]
    [Arguments("member", 2, false)]
    [Arguments("member", 3, false)]
    [Arguments("target", 0, true)]
    [Arguments("target", 1, true)]
    [Arguments("target", 2, false)]
    [Arguments("target", 3, false)]
    public async Task SmithyJsonStructuralExclusivityAllowsAtMostOneMemberPerTrait(
        string mode, int marked, bool valid)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{}},
              "example#Exclusive":{"type":"structure","traits":{"smithy.api#trait":{"structurallyExclusive":"MODE"}}}}}
            """.Replace("MODE", mode, StringComparison.Ordinal))!;
        var shapes = document["shapes"]!.AsObject();
        for (var index = 0; index < 3; index++)
        {
            var member = new JsonObject { ["target"] = "example#Value" + index };
            var target = new JsonObject { ["type"] = "string" };
            if (index < marked)
            {
                (mode == "member" ? member : target)["traits"] = new JsonObject { ["example#Exclusive"] = new JsonObject() };
            }
            shapes["example#Input"]!["members"]!["v" + index] = member;
            shapes["example#Value" + index] = target;
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(valid
            ? ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (valid)
        {
            await Assert.That(result.Operation.GetRawText())
                .IsEqualTo("""{"type":"operation","input":{"target":"example#Input"}}""");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/v1");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonExclusiveTargetCountsEachMemberEvenWhenBothTargetTheSameShape()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{
                "first":{"target":"example#Value"},"second":{"target":"example#Value"}}},
              "example#Value":{"type":"string","traits":{"example#Exclusive":{}}},
              "example#Exclusive":{"type":"structure","traits":{"smithy.api#trait":{"structurallyExclusive":"target"}}}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Input/members/second");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("member")]
    [Arguments("target")]
    public async Task SmithyJsonStructuralExclusivityIsScopedToEachStructureAndEachTrait(string mode)
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"},"output":{"target":"example#Output"}},
              "example#Input":{"type":"structure","members":{
                "first":{"target":"example#One",MEMBER_ONE},"second":{"target":"example#Two",MEMBER_TWO}}},
              "example#Output":{"type":"structure","members":{
                "first":{"target":"example#One",MEMBER_ONE},"second":{"target":"example#Two",MEMBER_TWO}}},
              "example#One":{"type":"string",TARGET_ONE},
              "example#Two":{"type":"string",TARGET_TWO},
              "example#First":{"type":"structure","traits":{"smithy.api#trait":{"structurallyExclusive":"MODE"}}},
              "example#Second":{"type":"structure","traits":{"smithy.api#trait":{"structurallyExclusive":"MODE"}}}}}
            """.Replace("MODE", mode, StringComparison.Ordinal);
        foreach (var name in new[] { "ONE", "TWO" })
        {
            var trait = name == "ONE" ? "example#First" : "example#Second";
            var applied = "\"traits\":{\"" + trait + "\":{}}";
            document = document.Replace("MEMBER_" + name, mode == "member" ? applied : "\"traits\":{}", StringComparison.Ordinal)
                .Replace("TARGET_" + name, mode == "target" ? applied : "\"traits\":{}", StringComparison.Ordinal);
        }
        var result = ResolveSmithy(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetProperty("output").GetProperty("target").GetString()).IsEqualTo("example#Output");
    }

    [Test]
    [Arguments("member", "target")]
    [Arguments("target", "member")]
    public async Task SmithyJsonStructuralExclusivityDoesNotConfuseMemberAndTargetModes(string mode, string position)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{
                "first":{"target":"example#Value"},"second":{"target":"example#Value"}}},
              "example#Value":{"type":"string"},
              "example#Exclusive":{"type":"structure","traits":{"smithy.api#trait":{"structurallyExclusive":"MODE"}}}}}
            """.Replace("MODE", mode, StringComparison.Ordinal))!;
        if (position == "target")
        {
            document["shapes"]!["example#Value"]!["traits"] = JsonNode.Parse("""{"example#Exclusive":{}}""");
        }
        else
        {
            foreach (var member in document["shapes"]!["example#Input"]!["members"]!.AsObject())
            {
                member.Value!["traits"] = JsonNode.Parse("""{"example#Exclusive":{}}""");
            }
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmithyJsonStructuralExclusivityChecksOnlyReachableStructuresIncludingTraitDefinitions(bool applied)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation"},
              "example#Outer":{"type":"structure","traits":{"smithy.api#trait":{}},"members":{
                "first":{"target":"smithy.api#String","traits":{"example#Exclusive":{}}},
                "second":{"target":"smithy.api#String","traits":{"example#Exclusive":{}}}}},
              "example#Exclusive":{"type":"structure","traits":{"smithy.api#trait":{"structurallyExclusive":"member"}}}}}
            """)!;
        if (applied)
        {
            document["shapes"]!["example#Get"]!["traits"] = JsonNode.Parse("""{"example#Outer":{}}""");
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(applied
            ? ApiOperationResolutionStatus.Invalid : ApiOperationResolutionStatus.Selected);
        if (applied)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_exclusivity");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Outer/members/second");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
        else
        {
            await Assert.That(result.Operation.GetRawText()).IsEqualTo("""{"type":"operation"}""");
            await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
        }
    }

    [Test]
    public async Task SmithyJsonStructuralExclusivityDoesNotRestrictUnionAlternatives()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Choice"}}},
              "example#Choice":{"type":"union","members":{
                "first":{"target":"smithy.api#String","traits":{"example#Exclusive":{}}},
                "second":{"target":"smithy.api#String","traits":{"example#Exclusive":{}}}}},
              "example#Exclusive":{"type":"structure","traits":{"smithy.api#trait":{"structurallyExclusive":"member"}}}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
    }

    [Test]
    [Arguments("input", false)]
    [Arguments("input", true)]
    [Arguments("output", false)]
    [Arguments("output", true)]
    public async Task SmithyJsonSpecializedStructuresCannotBeSharedByTwoOperations(string direction, bool otherFirst)
    {
        var operation = """{"type":"operation","DIRECTION":{"target":"example#Special"}}"""
            .Replace("DIRECTION", direction, StringComparison.Ordinal);
        var shapes = new JsonObject();
        if (otherFirst) { shapes["example#Other"] = JsonNode.Parse(operation); }
        shapes["example#Get"] = JsonNode.Parse(operation);
        shapes["example#Special"] = JsonNode.Parse("""
            {"type":"structure","traits":{"smithy.api#DIRECTION":{}}}
            """.Replace("DIRECTION", direction, StringComparison.Ordinal));
        if (!otherFirst) { shapes["example#Other"] = JsonNode.Parse(operation); }
        var document = new JsonObject { ["smithy"] = "2.0", ["shapes"] = shapes };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.input_output_usage");
        await Assert.That(result.Diagnostic.Path)
            .IsEqualTo("$/shapes/example#" + (otherFirst ? "Get" : "Other") + "/" + direction);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("input", """{"type":"operation","output":{"target":"example#Special"}}""", "output")]
    [Arguments("output", """{"type":"operation","input":{"target":"example#Special"}}""", "input")]
    [Arguments("input", """{"type":"operation","errors":[{"target":"example#Special"}]}""", "errors/0")]
    [Arguments("input", """{"type":"structure","members":{"a":{"target":"example#Special"}}}""", "members/a")]
    [Arguments("output", """{"type":"union","members":{"a":{"target":"example#Special"}}}""", "members/a")]
    [Arguments("input", """{"type":"enum","members":{"a":{"target":"example#Special"}}}""", "members/a")]
    [Arguments("input", """{"type":"intEnum","members":{"a":{"target":"example#Special"}}}""", "members/a")]
    [Arguments("input", """{"type":"list","member":{"target":"example#Special"}}""", "member")]
    [Arguments("output", """{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#Special"}}""", "value")]
    [Arguments("input", """{"type":"map","key":{"target":"example#Special"},"value":{"target":"smithy.api#String"}}""", "key")]
    [Arguments("input", """{"type":"service","errors":[{"target":"example#Special"}]}""", "errors/0")]
    [Arguments("input", """{"type":"service","operations":[{"target":"example#Special"}]}""", "operations/0")]
    [Arguments("output", """{"type":"service","resources":[{"target":"example#Special"}]}""", "resources/0")]
    [Arguments("input", """{"type":"resource","identifiers":{"id":{"target":"example#Special"}}}""", "identifiers/id")]
    [Arguments("output", """{"type":"resource","properties":{"a":{"target":"example#Special"}}}""", "properties/a")]
    [Arguments("input", """{"type":"resource","create":{"target":"example#Special"}}""", "create")]
    [Arguments("input", """{"type":"resource","put":{"target":"example#Special"}}""", "put")]
    [Arguments("input", """{"type":"resource","read":{"target":"example#Special"}}""", "read")]
    [Arguments("input", """{"type":"resource","update":{"target":"example#Special"}}""", "update")]
    [Arguments("input", """{"type":"resource","delete":{"target":"example#Special"}}""", "delete")]
    [Arguments("input", """{"type":"resource","list":{"target":"example#Special"}}""", "list")]
    [Arguments("input", """{"type":"resource","operations":[{"target":"example#Special"}]}""", "operations/0")]
    [Arguments("output", """{"type":"resource","collectionOperations":[{"target":"example#Special"}]}""", "collectionOperations/0")]
    [Arguments("input", """{"type":"resource","resources":[{"target":"example#Special"}]}""", "resources/0")]
    [Arguments("input", """{"type":"operation","traits":{"example#Special":{}}}""", "traits/example#Special")]
    [Arguments("input", """{"type":"structure","members":{"a":{"target":"smithy.api#String","traits":{"example#Special":{}}}}}""", "members/a/traits/example#Special")]
    public async Task SmithyJsonSpecializedStructuresRejectOtherNativeReferencePositions(
        string direction, string other, string suffix)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","DIRECTION":{"target":"example#Special"}},
              "example#Special":{"type":"structure","traits":{"smithy.api#DIRECTION":{}}},
              "example#Other":OTHER}}
            """.Replace("DIRECTION", direction, StringComparison.Ordinal).Replace("OTHER", other, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.input_output_usage");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Other/" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("input", false)]
    [Arguments("output", false)]
    [Arguments("input", true)]
    [Arguments("output", true)]
    public async Task SmithyJsonSpecializedUsageRejectsInvalidReferencesWithinTheSelectedClosure(
        string direction, bool nested)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Root"}},
              "example#Root":{"type":"structure","members":{"value":{"target":"example#Special"}}},
              "example#Special":{"type":"structure","traits":{"smithy.api#DIRECTION":{}}}}}
            """.Replace("DIRECTION", direction, StringComparison.Ordinal))!;
        var opposite = direction == "input" ? "output" : "input";
        if (!nested)
        {
            document["shapes"]!["example#Get"] = new JsonObject
            {
                ["type"] = "operation",
                [opposite] = new JsonObject { ["target"] = "example#Special" }
            };
        }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.input_output_usage");
        await Assert.That(result.Diagnostic.Path).IsEqualTo(nested
            ? "$/shapes/example#Root/members/value" : "$/shapes/example#Get/" + opposite);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("input")]
    [Arguments("output")]
    public async Task SmithyJsonSpecializedUsageDoesNotInterpretMetadataTraitPayloadsOrServiceRenames(string direction)
    {
        var document = """
            {"smithy":"2.0","metadata":{"target":"example#Special"},
              "shapes":{
                "example#Get":{"type":"operation","DIRECTION":{"target":"example#Special"},
                  "traits":{"example#Data":{"target":"example#Special","input":{"target":"example#Special"},
                    "$ref":"https://must-not-load.example/data"}}},
                "example#Special":{"type":"structure","traits":{"smithy.api#DIRECTION":{}}},
                "example#Data":{"type":"document","traits":{"smithy.api#trait":{}}},
                "example#Service":{"type":"service","version":"1",
                  "operations":[{"target":"example#Get"}],"rename":{"example#Special":"Renamed"}}}}
            """.Replace("DIRECTION", direction, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Literal data is not a dependency."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
    }

    [Test]
    public async Task SmithyJsonUnmarkedStructuresCanBeSharedAcrossOperationsAndDirections()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Data"},"output":{"target":"example#Data"}},
              "example#Other":{"type":"operation","input":{"target":"example#Data"},"output":{"target":"example#Data"}},
              "example#Data":{"type":"structure"}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("input").GetProperty("target").GetString()).IsEqualTo("example#Data");
        await Assert.That(result.Operation.GetProperty("output").GetProperty("target").GetString()).IsEqualTo("example#Data");
    }

    [Test]
    public async Task SmithyJsonSpecializedUsageChecksDoNotClaimFullValidationOfUnreachableShapes()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation"},
              "example#Other":{"type":"operation","input":{"target":"example#Unused"}},
              "example#Third":{"type":"operation","input":{"target":"example#Unused"}},
              "example#Unused":{"type":"structure","traits":{"smithy.api#input":{}}}}}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetRawText()).IsEqualTo("""{"type":"operation"}""");
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    [Test]
    [Arguments("""{}""")]
    [Arguments("""{"exists":[]}""")]
    [Arguments("""{"notFound":[]}""")]
    [Arguments("""{"exists":["example#Failure"]}""")]
    [Arguments("""{"notFound":["example#Failure"]}""")]
    [Arguments("""{"exists":["example#Failure","example#OtherFailure"]}""")]
    public async Task SmithyJsonPreservesNativeIdempotentErrorHintsWithoutInferringExecution(string hints)
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"smithy.api#idempotent":HINTS},
                "errors":[{"target":"example#Failure"},{"target":"example#OtherFailure"}]},
              "example#Failure":{"type":"structure","traits":{"smithy.api#error":"client"}},
              "example#OtherFailure":{"type":"structure","traits":{"smithy.api#error":"server"}}}}
            """.Replace("HINTS", hints, StringComparison.Ordinal);
        var result = await ApiOperationDocumentResolver.ResolveAsync("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"),
            resolveReference: (_, _) => throw new InvalidOperationException("Error IDs do not invoke retrieval."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        using var original = JsonDocument.Parse(document);
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo(original.RootElement.GetProperty("shapes").GetProperty("example#Get").GetRawText());
        await Assert.That(result.Operation.GetProperty("errors").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    [Arguments("""{"exists":[],"notFound":[]}""", "document.idempotent_conflict", "")]
    [Arguments("""{"notFound":[],"exists":[]}""", "document.idempotent_conflict", "")]
    [Arguments("""{"exists":null}""", "document.idempotent_errors", "/exists")]
    [Arguments("""{"exists":{}}""", "document.idempotent_errors", "/exists")]
    [Arguments("""{"notFound":"example#Failure"}""", "document.idempotent_errors", "/notFound")]
    [Arguments("""{"exists":[null]}""", "document.idempotent_error", "/exists/0")]
    [Arguments("""{"exists":[{}]}""", "document.idempotent_error", "/exists/0")]
    [Arguments("""{"notFound":[1]}""", "document.idempotent_error", "/notFound/0")]
    [Arguments("""{"exists":["Failure"]}""", "document.idempotent_error", "/exists/0")]
    [Arguments("""{"exists":["example#Failure$member"]}""", "document.idempotent_error", "/exists/0")]
    [Arguments("""{"exists":["example#failure"]}""", "document.idempotent_error", "/exists/0")]
    [Arguments("""{"exists":["example#NotDeclared"]}""", "document.idempotent_error", "/exists/0")]
    [Arguments("""{"notFound":["example#Failure","example#NotDeclared"]}""", "document.idempotent_error", "/notFound/1")]
    public async Task SmithyJsonRejectsInvalidOrUndeclaredIdempotentErrorHints(string hints, string code, string suffix)
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"smithy.api#idempotent":HINTS},
                "errors":[{"target":"example#Failure"}]},
              "example#Failure":{"type":"structure","traits":{"smithy.api#error":"client"}},
              "example#NotDeclared":{"type":"structure","traits":{"smithy.api#error":"server"}},
              "example#Service":{"type":"service","errors":[{"target":"example#NotDeclared"}]}}}
            """.Replace("HINTS", hints, StringComparison.Ordinal);
        var result = ResolveSmithy(document);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Get/traits/smithy.api#idempotent" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"type":"structure"}""", ApiOperationResolutionStatus.Invalid, "document.error_trait")]
    [Arguments("""{"type":"string","traits":{"smithy.api#error":"client"}}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"structure","traits":{"smithy.api#error":"Client"}}""", ApiOperationResolutionStatus.Invalid, "document.error_trait")]
    [Arguments("""null""", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    public async Task SmithyJsonIdempotentHintsDoNotBypassNativeErrorDefinitionValidation(
        string definition, ApiOperationResolutionStatus status, string code)
    {
        var document = JsonNode.Parse("""
            {"smithy":"2.0","shapes":{"example#Get":{"type":"operation",
              "traits":{"smithy.api#idempotent":{"exists":["example#Failure"]}},
              "errors":[{"target":"example#Failure"}]}}}
            """)!.AsObject();
        if (definition != "null") { document["shapes"]!["example#Failure"] = JsonNode.Parse(definition); }
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"exists":[]}""", ApiOperationResolutionStatus.Selected)]
    [Arguments("""{"notFound":[]}""", ApiOperationResolutionStatus.Selected)]
    [Arguments("""{"exists":["example#Failure"]}""", ApiOperationResolutionStatus.Invalid)]
    [Arguments("""{"notFound":["example#Failure"]}""", ApiOperationResolutionStatus.Invalid)]
    public async Task SmithyJsonEmptyIdempotentHintsDoNotRequireAnErrorsList(
        string hints, ApiOperationResolutionStatus status)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{"example#Get":{"type":"operation",
              "traits":{"smithy.api#idempotent":HINTS}}}}
            """.Replace("HINTS", hints, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.TryGetProperty("errors", out _)).IsFalse();
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.idempotent_error");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("""{"type":"operation","input":{"target":"example#Missing"}}""", "", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("""{"type":"operation","input":{"target":"smithy.api#String"}}""", "", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"operation","input":null}""", "", ApiOperationResolutionStatus.Invalid, "document.target")]
    [Arguments("""{"type":"operation","input":{"target":"Input"}}""", "", ApiOperationResolutionStatus.Invalid, "document.target")]
    [Arguments("""{"type":"operation","errors":{}}""", "", ApiOperationResolutionStatus.Invalid, "document.errors")]
    [Arguments("""{"type":"operation","errors":[{"target":"example#Input"}]}""", ""","example#Input":{"type":"structure"}""", ApiOperationResolutionStatus.Invalid, "document.error_trait")]
    [Arguments("""{"type":"operation","input":{"target":"example#Input"}}""", ""","example#Input":{"type":"structure","members":{"a":{"target":"example#Missing"}}}""", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("""{"type":"operation","traits":{"example#Missing":{}}}""", "", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("""{"type":"operation","traits":{"example#NotATrait":{}}}""", ""","example#NotATrait":{"type":"structure"}""", ApiOperationResolutionStatus.Invalid, "document.trait_definition")]
    [Arguments("""{"type":"operation","traits":[]}""", "", ApiOperationResolutionStatus.Invalid, "document.traits")]
    [Arguments("""{"type":"operation","traits":{"smithy.api#String":{}}}""", "", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"operation","traits":{"NotAbsolute":{}}}""", "", ApiOperationResolutionStatus.Invalid, "document.trait_id")]
    [Arguments("""{"type":"operation","input":{"target":"example#Input"}}""", ""","example#Input":{"type":"structure","members":{"a":{"target":"example#Future"}}},"example#Future":{"type":"future"}""", ApiOperationResolutionStatus.Unsupported, "smithy.shape_type")]
    [Arguments("""{"type":"operation","input":{"target":"example#Input"}}""", ""","example#Input":{"type":"structure","members":{"a":{"target":"smithy.api#String"},"A":{"target":"smithy.api#String"}}}""", ApiOperationResolutionStatus.Invalid, "document.member_name")]
    [Arguments("""{"type":"operation","input":{"target":"example#Input"}}""", ""","example#Input":{"type":"structure","members":{"a":{"target":"example#Get"}}}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"operation","input":{"target":"example#Input"}}""", ""","example#Input":{"type":"structure","members":{"a":{"target":"example#Trait"}}},"example#Trait":{"type":"string","traits":{"smithy.api#trait":{}}}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"operation","input":{"target":"example#Input"}}""", ""","example#Input":{"type":"structure","members":{"a":{"target":"example#List"}}},"example#List":{"type":"list"}""", ApiOperationResolutionStatus.Invalid, "document.target")]
    [Arguments("""{"type":"operation","input":{"target":"example#Input"}}""", ""","example#Input":{"type":"structure","members":{"a":{"target":"example#Map"}}},"example#Map":{"type":"map","key":{"target":"smithy.api#Integer"},"value":{"target":"smithy.api#String"}}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"operation","input":{"target":"example#Input"}}""", ""","example#Input":{"type":"structure","members":{"a":{"target":"example#Union"}}},"example#Union":{"type":"union","members":{}}""", ApiOperationResolutionStatus.Invalid, "document.members")]
    [Arguments("""{"type":"operation","mixins":[]}""", "", ApiOperationResolutionStatus.Unsupported, "smithy.assembly")]
    [Arguments("""{"type":"operation"}""", ""","example#Other":{"type":"apply","traits":{}}""", ApiOperationResolutionStatus.Unsupported, "smithy.assembly")]
    public async Task SmithyJsonCannotSelectPastMissingMalformedOrUnassembledDependencies(
        string operation, string dependencies, ApiOperationResolutionStatus status, string code)
    {
        var result = ResolveSmithy("""{"smithy":"2.0","shapes":{"example#Get":""" + operation + dependencies + "}}");
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(64, false, ApiOperationResolutionStatus.Selected)]
    [Arguments(65, false, ApiOperationResolutionStatus.Indeterminate)]
    [Arguments(63, true, ApiOperationResolutionStatus.Selected)]
    [Arguments(64, true, ApiOperationResolutionStatus.Indeterminate)]
    public async Task SmithyJsonBoundsTransitiveShapeDepthEvenWhenJsonNestingIsShallow(
        int depth, bool preludeLeaf, ApiOperationResolutionStatus expected)
    {
        var shapes = new System.Text.Json.Nodes.JsonObject
        {
            ["example#Get"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"operation","input":{"target":"example#S1"}}""")
        };
        for (var index = 1; index <= depth; index++)
        {
            shapes["example#S" + index] = index == depth
                ? System.Text.Json.Nodes.JsonNode.Parse(preludeLeaf
                    ? """{"type":"structure","members":{"leaf":{"target":"smithy.api#String"}}}"""
                    : """{"type":"structure"}""")
                : System.Text.Json.Nodes.JsonNode.Parse(
                    """{"type":"structure","members":{"next":{"target":"example#S""" + (index + 1) + "\"}}}");
        }
        var document = new System.Text.Json.Nodes.JsonObject { ["smithy"] = "2.0", ["shapes"] = shapes };
        var result = ResolveSmithy(document.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == ApiOperationResolutionStatus.Indeterminate)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.reference_depth");
        }
    }

    [Test]
    [Arguments("/shapes/example%23Get")]
    [Arguments("services/example%23Service/operations/Get")]
    [Arguments("operations/query/Get")]
    public async Task SmithyJsonOnlyAcceptsItsAbsoluteOperationShapeLocator(string selector)
    {
        var result = ApiOperationDocumentResolver.Resolve("Smithy/2.0", "application/json",
            """{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}"""u8.ToArray(),
            ApiOperationReference.Parse("/apigroups/g/apis/a#" + selector));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selector.unsupported");
    }

    [Test]
    [Arguments("""{"example#A":{"type":"list","member":{"target":"example#A"}}}""")]
    [Arguments("""{"example#A":{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#A"}}}""")]
    [Arguments("""{"example#A":{"type":"list","member":{"target":"example#B"}},"example#B":{"type":"list","member":{"target":"example#A"}}}""")]
    [Arguments("""{"example#A":{"type":"list","member":{"target":"example#B"}},"example#B":{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#A"}}}""")]
    [Arguments("""{"example#A":{"type":"structure","members":{"next":{"target":"example#A","traits":{"smithy.api#required":{}}}}}}""")]
    [Arguments("""{"example#A":{"type":"structure","members":{"next":{"target":"example#B","traits":{"smithy.api#required":{}}}}},"example#B":{"type":"structure","members":{"next":{"target":"example#A","traits":{"smithy.api#required":{}}}}}}""")]
    [Arguments("""{"example#A":{"type":"union","members":{"next":{"target":"example#A"}}}}""")]
    [Arguments("""{"example#A":{"type":"union","members":{"next":{"target":"example#B"}}},"example#B":{"type":"union","members":{"next":{"target":"example#A"}}}}""")]
    [Arguments("""{"example#A":{"type":"union","members":{"next":{"target":"example#B"}}},"example#B":{"type":"structure","members":{"next":{"target":"example#A","traits":{"smithy.api#required":{}}}}}}""")]
    [Arguments("""{"example#A":{"type":"union","members":{"a":{"target":"example#A"},"b":{"target":"example#B"}}},"example#B":{"type":"structure","members":{"next":{"target":"example#A","traits":{"smithy.api#required":{}}}}}}""")]
    [Arguments("""{"example#A":{"type":"structure","members":{"good":{"target":"smithy.api#String","traits":{"smithy.api#required":{}}},"bad":{"target":"example#A","traits":{"smithy.api#required":{}}},"optional":{"target":"smithy.api#String"}}}}""")]
    public async Task SmithyJsonRejectsNonterminatingRecursiveDataDefinitions(string definitions)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#A"}}},
            """ + definitions[1..^1] + "}}");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.recursion");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#A");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    public async Task SmithyJsonChecksEveryUnionAlternativeForInvalidRecursion()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Choice"}}},
              "example#Choice":{"type":"union","members":{
                "good":{"target":"smithy.api#Unit"},"bad":{"target":"example#Loop"}}},
              "example#Loop":{"type":"list","member":{"target":"example#Loop"}}
            }}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.recursion");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Loop");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"example#A":{"type":"structure","members":{"next":{"target":"example#A"}}}}""")]
    [Arguments("""{"example#A":{"type":"structure","members":{"next":{"target":"example#B","traits":{"smithy.api#required":{}}}}},"example#B":{"type":"structure","members":{"next":{"target":"example#A"}}}}""")]
    [Arguments("""{"example#A":{"type":"list","member":{"target":"example#B"}},"example#B":{"type":"structure","members":{"next":{"target":"example#A","traits":{"smithy.api#required":{}}}}}}""")]
    [Arguments("""{"example#A":{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#B"}},"example#B":{"type":"union","members":{"next":{"target":"example#A"}}}}""")]
    [Arguments("""{"example#A":{"type":"union","members":{"next":{"target":"example#A"},"done":{"target":"smithy.api#Unit"}}}}""")]
    [Arguments("""{"example#A":{"type":"union","members":{"next":{"target":"example#B"}}},"example#B":{"type":"union","members":{"next":{"target":"example#A"},"done":{"target":"smithy.api#String"}}}}""")]
    [Arguments("""{"example#A":{"type":"union","members":{"next":{"target":"example#B"}}},"example#B":{"type":"structure","members":{"next":{"target":"example#A"}}}}""")]
    [Arguments("""{"example#A":{"type":"structure","members":{"a":{"target":"example#B","traits":{"smithy.api#required":{}}},"b":{"target":"example#B","traits":{"smithy.api#required":{}}}}},"example#B":{"type":"union","members":{"next":{"target":"example#A"},"done":{"target":"smithy.api#Unit"}}}}""")]
    public async Task SmithyJsonPreservesTerminatingRecursiveDataDefinitions(string definitions)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#A"}}},
            """ + definitions[1..^1] + "}}");
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.DocumentPath).IsEqualTo("$/shapes/example#Get");
        await Assert.That(result.Operation.GetProperty("input").GetProperty("target").GetString())
            .IsEqualTo("example#Input");
    }

    [Test]
    [Arguments(false, ApiOperationResolutionStatus.Selected)]
    [Arguments(true, ApiOperationResolutionStatus.Invalid)]
    public async Task SmithyJsonChecksRecursionInCustomTraitDefinitions(bool required, ApiOperationResolutionStatus expected)
    {
        var document = """
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","traits":{"example#Custom":{"value":{}}}},
              "example#Custom":{"type":"structure","traits":{"smithy.api#trait":{}},
                "members":{"value":{"target":"example#Node","traits":{"smithy.api#required":{}}}}},
              "example#Node":{"type":"structure","members":{"next":{"target":"example#Node"REQUIRED}}}
            }}
            """.Replace("REQUIRED", required ? ""","traits":{"smithy.api#required":{}}""" : "", StringComparison.Ordinal);
        var result = ResolveSmithy(document);
        await Assert.That(result.Status).IsEqualTo(expected);
        if (required)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.recursion");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
        else
        {
            await Assert.That(result.Operation.GetProperty("traits").GetProperty("example#Custom").GetRawText())
                .IsEqualTo("""{"value":{}}""");
        }
    }

    [Test]
    [Arguments(1, true)]
    [Arguments(32, true)]
    [Arguments(63, true)]
    [Arguments(1, false)]
    [Arguments(32, false)]
    [Arguments(63, false)]
    public async Task SmithyJsonPropagatesTerminationThroughRequiredShapeChains(int depth, bool terminating)
    {
        var shapes = new System.Text.Json.Nodes.JsonObject
        {
            ["example#Get"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"operation","input":{"target":"example#S1"}}""")
        };
        for (var index = 1; index <= depth; index++)
        {
            var target = index < depth ? "example#S" + (index + 1) :
                terminating ? "smithy.api#String" : "example#S1";
            shapes["example#S" + index] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "structure",
                ["members"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["next"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["target"] = target,
                        ["traits"] = System.Text.Json.Nodes.JsonNode.Parse("""{"smithy.api#required":{}}""")
                    }
                }
            };
        }
        var result = ResolveSmithy(new System.Text.Json.Nodes.JsonObject
        {
            ["smithy"] = "2.0",
            ["shapes"] = shapes
        }.ToJsonString());
        await Assert.That(result.Status).IsEqualTo(terminating ?
            ApiOperationResolutionStatus.Selected : ApiOperationResolutionStatus.Invalid);
        if (!terminating)
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.recursion");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#S1");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonDoesNotClaimRecursionValidationOutsideTheSelectedClosure()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation"},
              "example#Unreachable":{"type":"list","member":{"target":"example#Unreachable"}}
            }}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("type").GetString()).IsEqualTo("operation");
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    private static ApiOperationResolutionResult ResolveSmithy(string document, string selector = "example%23Get") =>
        ApiOperationDocumentResolver.Resolve("Smithy/2.0", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/" + selector));

    [Test]
    [Arguments("enum", """{}""", "document.members")]
    [Arguments("intEnum", """{}""", "document.members")]
    [Arguments("enum", """{"A":null}""", "document.target")]
    [Arguments("enum", """{"A":{"target":"smithy.api#String"}}""", "document.enum_target")]
    [Arguments("enum", """{"A":{"target":"example#Input"}}""", "document.enum_target")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#String","traits":{"smithy.api#enumValue":1}}}""", "document.enum_target")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}""", "document.enum_value")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":null}}}""", "document.enum_value")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":""}}}""", "document.enum_value")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":true}}}""", "document.enum_value")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":[]}}}""", "document.enum_value")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":{}}}}""", "document.enum_value")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit"},"B":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"A"}}}""", "document.enum_value")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"same"}},"B":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"same"}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit"}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"1"}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":null}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":true}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":0.5}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":2147483648}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":-2147483649}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1e9999}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1e-9999}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1.0000000000000000000000000000001}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}},"B":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1.0}}}""", "document.enum_value")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":0}},"B":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":-0e9999}}}""", "document.enum_value")]
    public async Task SmithyJsonRejectsInvalidEnumerationTargetsValuesAndAliases(string kind, string members, string code)
    {
        var result = ResolveSmithyEnumeration(kind, members);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit"}}""")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit"},"B":{"target":"smithy.api#Unit"}}""")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"x"}}}""")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":" "}}}""")]
    [Arguments("enum", """{"a":{"target":"smithy.api#Unit"},"_1":{"target":"smithy.api#Unit"}}""")]
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"value"}},"B":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"VALUE"}}}""")]
    [Arguments("intEnum", """{"MIN":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":-2147483648}},"MAX":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":2147483647}}}""")]
    [Arguments("intEnum", """{"MIN":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":-2147483648.0}},"MAX":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":2147483647e0}}}""")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":0}}}""")]
    [Arguments("intEnum", """{"a":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1.0}},"_1":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":2e0}}}""")]
    [Arguments("intEnum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":-0e9999}}}""")]
    public async Task SmithyJsonAcceptsNativeEnumerationValuesWithoutRewritingTheOperation(string kind, string members)
    {
        var result = ResolveSmithyEnumeration(kind, members);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetRawText())
            .IsEqualTo("""{"type":"operation","input":{"target":"example#Input"}}""");
        await Assert.That(ApiOperationDocumentResolver.Capabilities.SupportsApiDocumentValidation).IsFalse();
    }

    private static ApiOperationResolutionResult ResolveSmithyEnumeration(string kind, string members) =>
        ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Choice"}}},
              "example#Choice":{"type":"KIND","members":MEMBERS}}}
            """.Replace("KIND", kind, StringComparison.Ordinal).Replace("MEMBERS", members, StringComparison.Ordinal));

    [Test]
    [Arguments("enum", "\"value\"")]
    [Arguments("intEnum", "1")]
    public async Task SmithyJsonEnumValueCannotAnnotateTheEnumerationItself(string kind, string value)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"value":{"target":"example#Choice"}}},
              "example#Choice":{"type":"KIND","traits":{"smithy.api#enumValue":VALUE},
                "members":{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":VALUE}}}}}}
            """.Replace("KIND", kind, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.trait_target");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/shapes/example#Choice/traits/smithy.api#enumValue");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"type":"string"}""", ApiOperationResolutionStatus.Selected, null)]
    [Arguments("""{"type":"enum","members":{"A":{"target":"smithy.api#Unit"}}}""", ApiOperationResolutionStatus.Selected, null)]
    [Arguments("""{"type":"enum","members":{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"a"}}}}""", ApiOperationResolutionStatus.Selected, null)]
    [Arguments("""{"type":"integer"}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"intEnum","members":{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"structure"}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"string","traits":{"smithy.api#trait":{}}}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"enum","traits":{"smithy.api#trait":{}},"members":{"A":{"target":"smithy.api#Unit"}}}""", ApiOperationResolutionStatus.Invalid, "document.target_type")]
    [Arguments("""{"type":"enum","members":{}}""", ApiOperationResolutionStatus.Invalid, "document.members")]
    [Arguments("""{"type":"enum","members":{"A":{"target":"smithy.api#String"}}}""", ApiOperationResolutionStatus.Invalid, "document.enum_target")]
    [Arguments("""{"type":"enum","members":{"A":{"target":"smithy.api#Unit"},"B":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"A"}}}}""", ApiOperationResolutionStatus.Invalid, "document.enum_value")]
    public async Task SmithyJsonMapKeysAcceptStringSpecializationsButStillValidateTheirDefinitions(
        string key, ApiOperationResolutionStatus status, string? code)
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{
                "key":{"target":"example#Key"},"entries":{"target":"example#Map"}}},
              "example#Map":{"type":"map","key":{"target":"example#Key"},"value":{"target":"smithy.api#String"}},
              "example#Key":KEY}}
            """.Replace("KEY", key, StringComparison.Ordinal));
        await Assert.That(result.Status).IsEqualTo(status);
        if (code is null)
        {
            await Assert.That(result.Diagnostic).IsNull();
            await Assert.That(result.Operation.GetProperty("input").GetProperty("target").GetString())
                .IsEqualTo("example#Input");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    public async Task SmithyJsonTraversesAggregateDependenciesButNotTraitPayloadsAsShapeReferences()
    {
        var result = ResolveSmithy("""
            {"smithy":"2.0","shapes":{
              "example#Get":{"type":"operation","input":{"target":"example#Input"}},
              "example#Input":{"type":"structure","members":{"entries":{"target":"example#Map"}}},
              "example#Map":{"type":"map","key":{"target":"smithy.api#String"},"value":{"target":"example#List"}},
              "example#List":{"type":"list","member":{"target":"example#Union"}},
              "example#Union":{"type":"union","members":{"a":{"target":"example#Enum"},"b":{"target":"example#Number"}}},
              "example#Enum":{"type":"enum","members":{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":"a"}}}},
              "example#Number":{"type":"intEnum","members":{"B":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":1}}}}
            }}
            """);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var metadata = ResolveSmithy("""
            {"smithy":"2.0","shapes":{"example#Get":{"type":"operation",
              "traits":{"example#Metadata":{"target":"not-a-shape-id","$ref":"https://untrusted.example"}}},
              "example#Metadata":{"type":"document","traits":{"smithy.api#trait":{}}}}}
            """);
        await Assert.That(metadata.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(metadata.Operation.GetProperty("traits").GetProperty("example#Metadata")
            .GetProperty("$ref").GetString()).IsEqualTo("https://untrusted.example");
    }

    [Test]
    public async Task SmithyJsonUsesTheSharedByteLimitAndPropagatesCallerCancellation()
    {
        const string document = """{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}""";
        var boundary = document.PadRight(ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes);
        await Assert.That(ResolveSmithy(boundary).Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        var exceeded = ResolveSmithy(boundary + " ");
        await Assert.That(exceeded.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(exceeded.Diagnostic!.Code).IsEqualTo("limit.bytes");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            ApiOperationDocumentResolver.Resolve("Smithy/2.0", "application/json",
                System.Text.Encoding.UTF8.GetBytes(document),
                ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"), cancellation.Token));
    }

    [Test]
    [Arguments("Smithy/1.0", "application/json", "format.unsupported")]
    [Arguments("Smithy/2.1", "application/json", "format.unsupported")]
    [Arguments("smithy/2.0", "application/json", "format.unsupported")]
    [Arguments("Smithy/2.0", "text/plain", "contenttype.unsupported")]
    public async Task SmithyJsonDoesNotAdvertiseOtherReleasesOrIdlParsing(string format, string mediaType, string code)
    {
        var result = ApiOperationDocumentResolver.Resolve(format, mediaType,
            """{"smithy":"2.0","shapes":{"example#Get":{"type":"operation"}}}"""u8.ToArray(),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/example%23Get"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
    }

    [Test]
    public async Task OpenRpcSelectionPreservesTheNativeMethodAndDecodesItsNameOnce()
    {
        const string document = """
            {"openrpc":"1.4.0","info":{"title":"Users","version":"9.2.0"},"methods":[
              {"name":"Other","params":[]},
              {"name":"a/~1%2F","params":[{"name":"request","schema":{"type":"string"}}],
               "result":{"name":"reply","schema":{"type":"integer"}},
               "errors":[{"code":123,"message":"Denied"}]}
            ]}
            """;
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json; charset=utf-8",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/a~1~01%252F"));

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/methods/1");
        await Assert.That(result.DocumentUri).IsNull();
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("a/~1%2F");
        await Assert.That(result.Operation.GetProperty("params")[0].GetProperty("name").GetString())
            .IsEqualTo("request");
        await Assert.That(result.Operation.GetProperty("result").GetProperty("schema").GetProperty("type").GetString())
            .IsEqualTo("integer");
        await Assert.That(result.Operation.GetProperty("errors")[0].GetProperty("code").GetInt32()).IsEqualTo(123);
    }

    [Test]
    [Arguments("""{"examples":[]}""")]
    [Arguments("""{"examples":[{"name":"notification","params":[]}]}""")]
    [Arguments("""{"examples":[{"name":"","params":[{"name":"","value":null}],"description":""}]}""")]
    [Arguments("""{"examples":[{"name":"scalars","params":[{"name":"a","value":false},{"name":"b","value":123},{"name":"c","value":"literal"}],"result":{"name":"reply","value":true,"summary":"","description":""}}]}""")]
    [Arguments("""{"examples":[{"name":"containers","params":[{"name":"a","value":[null,1,{}]}],"result":{"name":"reply","value":{"$ref":"https://must-not-load.example/data","method":"NotAnInvocation"}}}]}""")]
    [Arguments("""{"examples":[{"name":"repeat","params":[{"name":"same","value":1},{"name":"same","value":2}]},{"name":"repeat","params":[]}]}""")]
    public async Task OpenRpcExamplesPreserveLiteralJsonAndNotificationAbsenceWithoutAcquisition(string metadata)
    {
        var method = JsonNode.Parse(metadata)!.AsObject();
        method["name"] = "Target";
        method["params"] = new JsonArray();
        var native = method.ToJsonString();
        var document = new JsonObject { ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" }, ["methods"] = new JsonArray(method) };
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Example payloads are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(native);
        await Assert.That(result.Operation.TryGetProperty("result", out _)).IsFalse();
    }

    [Test]
    [Arguments("""{}""")]
    [Arguments("""{"servers":[]}""")]
    [Arguments("""{"info":{"title":"","version":"release not SemVer","contact":{},"license":{}}}""")]
    [Arguments("""
        {"info":{"title":"Users","version":"independent document release",
          "description":"API description","termsOfService":"https://must-not-load.example/terms",
          "contact":{"name":"API contact","url":"https://must-not-load.example/contact","email":"support@example.test"},
          "license":{"name":"MIT","url":"https://must-not-load.example/license"}},
         "externalDocs":{"url":"https://must-not-load.example/docs","description":"Reference"},
         "$schema":"https://must-not-load.example/schema",
         "servers":[{"url":"../{region}","name":"Root server","summary":"Summary","description":"Description",
            "variables":{"region":{"default":"west","enum":["west","east"],"description":"Region"}}}]}
        """)]
    public async Task OpenRpcRootMetadataPreservesMethodSelectionWithoutDefaultOrUrlAcquisition(string metadata)
    {
        var document = JsonNode.Parse(metadata)!.AsObject();
        document["openrpc"] = "1.4.0";
        if (!document.ContainsKey("info"))
        {
            document["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" };
        }
        const string method = """{"name":"Target","params":[]}""";
        document["methods"] = new JsonArray(JsonNode.Parse(method));
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Metadata URLs must not be acquired."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
        await Assert.That(result.Operation.GetProperty("params").GetArrayLength()).IsEqualTo(0);
        await Assert.That(result.Operation.TryGetProperty("servers", out _)).IsFalse();
        await Assert.That(result.Operation.TryGetProperty("info", out _)).IsFalse();
        await Assert.That(result.DocumentPath).IsEqualTo("$/methods/0");
    }

    [Test]
    [Arguments("Target", true)]
    [Arguments("Missing", true)]
    [Arguments("Target", false)]
    [Arguments("Missing", false)]
    public async Task OpenRpcUnrelatedDuplicateMethodNamesPreventSelectionOrNotFound(
        string requested, bool referenced)
    {
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" },
            ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "Target", ["params"] = new JsonArray() },
                new JsonObject { ["name"] = "Other", ["params"] = new JsonArray() },
                referenced
                    ? new JsonObject { ["$ref"] = "https://api.example/other.json" }
                    : new JsonObject { ["name"] = "Other", ["params"] = new JsonArray() })
        };
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/" + requested),
            resolveReference: (uri, _) =>
            {
                calls++;
                if (uri != "https://api.example/other.json") { throw new InvalidOperationException(uri); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    """{"name":"Other","params":[]}"""u8.ToArray());
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.method_duplicate");
        await Assert.That(result.Diagnostic.Path).IsEqualTo(referenced ? "$/name" : "$/methods/2/name");
        await Assert.That(calls).IsEqualTo(referenced ? 1 : 0);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    public async Task OpenRpcMethodNameUniquenessUsesExactCaseAndIgnoresUnusedDefinitions()
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json", """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[]},{"name":"target","params":[]}],
             "components":{"methods":{"unused":{"name":"Target","params":[]}}}}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
        await Assert.That(result.DocumentPath).IsEqualTo("$/methods/0");
    }

    [Test]
    [Arguments("""[]""")]
    [Arguments("""[{"name":"Target","params":[]}]""")]
    [Arguments("""[{"$ref":"https://must-not-load.example/method"}]""")]
    public async Task OpenRpcRequiresInfoEvenForEmptyOrReferencedMethodArrays(string methods)
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes("""{"openrpc":"1.4.0","methods":""" + methods + "}"),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Missing root metadata must fail before imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.info");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/info");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("""{"info":null}""", "document.info", "$/info")]
    [Arguments("""{"info":[]}""", "document.info", "$/info")]
    [Arguments("""{"info":{}}""", "document.info_title", "$/info/title")]
    [Arguments("""{"info":{"title":false,"version":"x"}}""", "document.info_title", "$/info/title")]
    [Arguments("""{"info":{"title":"API"}}""", "document.info_version", "$/info/version")]
    [Arguments("""{"info":{"title":"API","version":1}}""", "document.info_version", "$/info/version")]
    [Arguments("""{"info":{"title":"API","version":"x","description":[]}}""", "document.annotation", "$/info/description")]
    [Arguments("""{"info":{"title":"API","version":"x","termsOfService":true}}""", "document.annotation", "$/info/termsOfService")]
    [Arguments("""{"info":{"title":"API","version":"x","contact":null}}""", "document.info_metadata", "$/info/contact")]
    [Arguments("""{"info":{"title":"API","version":"x","contact":{"name":0}}}""", "document.annotation", "$/info/contact/name")]
    [Arguments("""{"info":{"title":"API","version":"x","contact":{"email":{}}}}""", "document.annotation", "$/info/contact/email")]
    [Arguments("""{"info":{"title":"API","version":"x","contact":{"url":null}}}""", "document.annotation", "$/info/contact/url")]
    [Arguments("""{"info":{"title":"API","version":"x","license":[]}}""", "document.info_metadata", "$/info/license")]
    [Arguments("""{"info":{"title":"API","version":"x","license":{"name":false}}}""", "document.annotation", "$/info/license/name")]
    [Arguments("""{"info":{"title":"API","version":"x","license":{"url":3}}}""", "document.annotation", "$/info/license/url")]
    [Arguments("""{"$schema":{}}""", "document.annotation", "$/$schema")]
    [Arguments("""{"externalDocs":null}""", "document.external_docs", "$/externalDocs")]
    [Arguments("""{"externalDocs":{}}""", "document.external_docs_url", "$/externalDocs/url")]
    [Arguments("""{"externalDocs":{"url":false}}""", "document.external_docs_url", "$/externalDocs/url")]
    [Arguments("""{"externalDocs":{"url":"https://example.test","description":0}}""", "document.annotation", "$/externalDocs/description")]
    [Arguments("""{"servers":null}""", "document.servers", "$/servers")]
    [Arguments("""{"servers":[0]}""", "document.server", "$/servers/0")]
    [Arguments("""{"servers":[{}]}""", "document.server_url", "$/servers/0/url")]
    [Arguments("""{"servers":[{"url":"localhost","variables":{"a/b~c":{"default":0}}}]}""", "document.server_default", "$/servers/0/variables/a~1b~0c/default")]
    [Arguments("""{"servers":[{"url":"localhost","variables":{"region":{"default":"west","enum":[false]}}}]}""", "document.server_enum", "$/servers/0/variables/region/enum/0")]
    public async Task OpenRpcMalformedRootMetadataFailsBeforeMethodDependencyAcquisition(
        string metadata, string code, string path)
    {
        var document = JsonNode.Parse(metadata)!.AsObject();
        document["openrpc"] = "1.4.0";
        if (!document.ContainsKey("info"))
        {
            document["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" };
        }
        document["methods"] = new JsonArray(new JsonObject { ["$ref"] = "https://must-not-load.example/method" });
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Invalid root must fail before imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo(path);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("""{"examples":{}}""", "document.examples", "/examples")]
    [Arguments("""{"examples":[null]}""", "document.operation", "/examples/0")]
    [Arguments("""{"examples":[{}]}""", "document.example_pairing_name", "/examples/0/name")]
    [Arguments("""{"examples":[{"name":0,"params":[]}]}""", "document.example_pairing_name", "/examples/0/name")]
    [Arguments("""{"examples":[{"name":"e"}]}""", "document.example_params", "/examples/0/params")]
    [Arguments("""{"examples":[{"name":"e","params":null}]}""", "document.example_params", "/examples/0/params")]
    [Arguments("""{"examples":[{"name":"e","params":[],"description":false}]}""", "document.annotation", "/examples/0/description")]
    [Arguments("""{"examples":[{"name":"e","params":[false]}]}""", "document.operation", "/examples/0/params/0")]
    [Arguments("""{"examples":[{"name":"e","params":[{}]}]}""", "document.example_name", "/examples/0/params/0/name")]
    [Arguments("""{"examples":[{"name":"e","params":[{"name":null,"value":null}]}]}""", "document.example_name", "/examples/0/params/0/name")]
    [Arguments("""{"examples":[{"name":"e","params":[{"name":"a"}]}]}""", "document.example_value", "/examples/0/params/0/value")]
    [Arguments("""{"examples":[{"name":"e","params":[{"name":"a","externalValue":"https://must-not-load.example/value"}]}]}""", "document.example_value", "/examples/0/params/0/value")]
    [Arguments("""{"examples":[{"name":"e","params":[{"name":"a","value":null,"externalValue":"https://must-not-load.example/value"}]}]}""", "document.example_value_conflict", "/examples/0/params/0/externalValue")]
    [Arguments("""{"examples":[{"name":"e","params":[{"name":"a","value":0,"summary":0}]}]}""", "document.annotation", "/examples/0/params/0/summary")]
    [Arguments("""{"examples":[{"name":"e","params":[{"name":"a","value":0,"description":null}]}]}""", "document.annotation", "/examples/0/params/0/description")]
    [Arguments("""{"examples":[{"name":"e","params":[],"result":null}]}""", "document.operation", "/examples/0/result")]
    [Arguments("""{"examples":[{"name":"e","params":[],"result":{}}]}""", "document.example_name", "/examples/0/result/name")]
    [Arguments("""{"examples":[{"name":"e","params":[],"result":{"name":"r"}}]}""", "document.example_value", "/examples/0/result/value")]
    public async Task OpenRpcMalformedExamplesInLaterMethodsPreventAnEarlierSelection(
        string metadata, string code, string suffix)
    {
        var later = JsonNode.Parse(metadata)!.AsObject();
        later["name"] = "Other";
        later["params"] = new JsonArray();
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" },
            ["methods"] = new JsonArray(new JsonObject { ["name"] = "Target", ["params"] = new JsonArray() }, later)
        };
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Example values are not imports."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/methods/1" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task OpenRpcPairingAndExampleReferencesResolveFromTheirActualContainingDocuments()
    {
        var requests = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json", """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"$ref":"./nested/method.json"}],
             "components":{"examplePairings":{"unused":{"name":false}}}}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            new Uri("https://api.example/root.json"), (uri, _) =>
            {
                requests.Add(uri);
                var json = uri switch
                {
                    "https://api.example/nested/method.json" =>
                        """{"name":"Target","params":[],"examples":[{"$ref":"./examples/pair.json"}]}""",
                    "https://api.example/nested/examples/pair.json" =>
                        """{"name":"e","params":[{"$ref":"../values.json#/input"}],"result":{"$ref":"../values.json#/output"}}""",
                    "https://api.example/nested/values.json" => """
                        {"input":{"name":"a","value":{"$ref":"https://not-an-import.example/input"}},
                         "output":{"$ref":"./parts/output.json"}}
                        """,
                    "https://api.example/nested/parts/output.json" => """{"name":"r","value":null}""",
                    _ => throw new InvalidOperationException("Unexpected import " + uri)
                };
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(json));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://api.example/nested/method.json"));
        await Assert.That(result.Operation.GetProperty("examples")[0].GetProperty("$ref").GetString()).IsEqualTo("./examples/pair.json");
        await Assert.That(requests.SequenceEqual([
            "https://api.example/nested/method.json", "https://api.example/nested/examples/pair.json",
            "https://api.example/nested/values.json", "https://api.example/nested/parts/output.json"])).IsTrue();
    }

    [Test]
    [Arguments("pairing", "unavailable", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("pairing", "denied", ApiOperationResolutionStatus.Denied, "dependency.denied")]
    [Arguments("pairing", "cycle", ApiOperationResolutionStatus.Indeterminate, "dependency.cycle")]
    [Arguments("pairing", "malformed", ApiOperationResolutionStatus.Invalid, "document.example_pairing_name")]
    [Arguments("parameter", "unavailable", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("parameter", "denied", ApiOperationResolutionStatus.Denied, "dependency.denied")]
    [Arguments("parameter", "cycle", ApiOperationResolutionStatus.Indeterminate, "dependency.cycle")]
    [Arguments("parameter", "malformed", ApiOperationResolutionStatus.Invalid, "document.example_name")]
    [Arguments("result", "unavailable", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("result", "denied", ApiOperationResolutionStatus.Denied, "dependency.denied")]
    [Arguments("result", "cycle", ApiOperationResolutionStatus.Indeterminate, "dependency.cycle")]
    [Arguments("result", "malformed", ApiOperationResolutionStatus.Invalid, "document.example_name")]
    public async Task OpenRpcExampleReferenceFailuresRemainExplicit(
        string position, string outcome, ApiOperationResolutionStatus expected, string code)
    {
        var reference = new JsonObject { ["$ref"] = "https://api.example/example.json" };
        var pairing = new JsonObject { ["name"] = "e", ["params"] = new JsonArray() };
        if (position == "parameter") { pairing["params"] = new JsonArray(reference); }
        if (position == "result") { pairing["result"] = reference; }
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" },
            ["methods"] = new JsonArray(new JsonObject
            {
                ["name"] = "Target", ["params"] = new JsonArray(),
                ["examples"] = new JsonArray(position == "pairing" ? reference : pairing)
            })
        };
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) =>
            {
                calls++;
                if (outcome == "denied") { throw new UnauthorizedAccessException("Outside policy."); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(outcome switch
                {
                    "cycle" => """{"$ref":"#"}"""u8.ToArray(),
                    "malformed" => "{}"u8.ToArray(),
                    _ => (ReadOnlyMemory<byte>?)null
                });
            });
        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(63, ApiOperationResolutionStatus.Selected)]
    [Arguments(64, ApiOperationResolutionStatus.Selected)]
    [Arguments(65, ApiOperationResolutionStatus.Indeterminate)]
    public async Task OpenRpcExampleReferencesHonorTheInclusiveDepthLimit(
        int depth, ApiOperationResolutionStatus expected)
    {
        var chain = new JsonObject();
        for (var index = 0; index < depth; index++)
        {
            chain["n" + index] = index == depth - 1
                ? new JsonObject { ["name"] = "literal", ["value"] = null }
                : new JsonObject { ["$ref"] = "#/chain/n" + (index + 1) };
        }
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" },
            ["methods"] = new JsonArray(new JsonObject
            {
                ["name"] = "Target", ["params"] = new JsonArray(),
                ["examples"] = new JsonArray(new JsonObject
                {
                    ["name"] = "e", ["params"] = new JsonArray(),
                    ["result"] = new JsonObject { ["$ref"] = "#/chain/n0" }
                })
            }),
            ["chain"] = chain
        };
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("examples")[0].GetProperty("result").GetProperty("$ref").GetString())
                .IsEqualTo("#/chain/n0");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.reference_depth");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("""{"name":"Target","params":[]}""")]
    [Arguments("""{"name":"Target","paramStructure":"by-name","params":[{"name":"a","schema":true,"required":true},{"name":"A","schema":false}]}""")]
    [Arguments("""{"name":"Target","paramStructure":"by-position","params":[{"name":"","schema":{},"required":false}],"deprecated":true}""")]
    [Arguments("""{"name":"Target","paramStructure":"either","params":[],"result":{"name":"reply","schema":false,"required":true,"deprecated":false,"summary":"","description":""}}""")]
    [Arguments("""{"name":"Target","params":[],"errors":[{"code":1.0,"message":""},{"code":1e100,"message":"Large"},{"code":-32700,"message":"Native","data":{"x":true}}]}""")]
    public async Task OpenRpcContractsPreserveNativeNotificationsDescriptorsAndExactErrorCodes(string method)
    {
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[""" + method + "]}"),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("No imports were declared."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentPath).IsEqualTo("$/methods/0");
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(method);
        await Assert.That(result.Operation.TryGetProperty("result", out _)).IsEqualTo(method.Contains("\"result\"", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("""{"servers":[],"tags":[]}""")]
    [Arguments("""{"servers":[{"url":"../rpc","name":"","summary":"","description":"","variables":{}}]}""")]
    [Arguments("""{"servers":[{"url":"https://{region}.example/{version}","variables":{"region":{"default":"west","enum":["east","west"],"description":"Region"},"version":{"default":"v1","enum":[]}}}]}""")]
    [Arguments("""{"servers":[{"url":"{endpoint}","variables":{"endpoint":{"default":"different","enum":["listed","listed"]}}}]}""")]
    [Arguments("""{"tags":[{"name":"users","description":"","externalDocs":{"url":"https://docs.example/users","description":""}},{"name":"users"}],"externalDocs":{"url":"https://docs.example/method"}}""")]
    public async Task OpenRpcMethodMetadataPreservesNativeServersVariablesAndTagsWithoutInvokingUrls(string metadata)
    {
        var method = JsonNode.Parse(metadata)!.AsObject();
        method["name"] = "Target";
        method["params"] = new JsonArray();
        var native = method.ToJsonString();
        var document = new JsonObject { ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" }, ["methods"] = new JsonArray(method) };
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Metadata URLs must not be invoked."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(native);
        await Assert.That(result.Operation.TryGetProperty("result", out _)).IsFalse();
    }

    [Test]
    [Arguments("""{"servers":{}}""", "document.servers", "/servers")]
    [Arguments("""{"servers":[null]}""", "document.server", "/servers/0")]
    [Arguments("""{"servers":[{"$ref":"https://must-not-load.example/server"}]}""", "document.server_url", "/servers/0/url")]
    [Arguments("""{"servers":[{"url":false}]}""", "document.server_url", "/servers/0/url")]
    [Arguments("""{"servers":[{"url":"/rpc","name":0}]}""", "document.annotation", "/servers/0/name")]
    [Arguments("""{"servers":[{"url":"/rpc","summary":null}]}""", "document.annotation", "/servers/0/summary")]
    [Arguments("""{"servers":[{"url":"/rpc","description":[]}]}""", "document.annotation", "/servers/0/description")]
    [Arguments("""{"servers":[{"url":"/rpc","variables":[]}]}""", "document.server_variables", "/servers/0/variables")]
    [Arguments("""{"servers":[{"url":"/rpc","variables":{"a/b~":null}}]}""", "document.server_variable", "/servers/0/variables/a~1b~0")]
    [Arguments("""{"servers":[{"url":"/rpc","variables":{"v":{}}}]}""", "document.server_default", "/servers/0/variables/v/default")]
    [Arguments("""{"servers":[{"url":"/rpc","variables":{"v":{"default":true}}}]}""", "document.server_default", "/servers/0/variables/v/default")]
    [Arguments("""{"servers":[{"url":"/rpc","variables":{"v":{"default":"","description":false}}}]}""", "document.annotation", "/servers/0/variables/v/description")]
    [Arguments("""{"servers":[{"url":"/rpc","variables":{"v":{"default":"","enum":null}}}]}""", "document.server_enum", "/servers/0/variables/v/enum")]
    [Arguments("""{"servers":[{"url":"/rpc","variables":{"v":{"default":"","enum":["ok",1]}}}]}""", "document.server_enum", "/servers/0/variables/v/enum/1")]
    [Arguments("""{"tags":{}}""", "document.tags", "/tags")]
    [Arguments("""{"tags":[{}]}""", "document.tag_name", "/tags/0/name")]
    [Arguments("""{"tags":[{"name":true}]}""", "document.tag_name", "/tags/0/name")]
    [Arguments("""{"tags":[{"name":"t","description":0}]}""", "document.annotation", "/tags/0/description")]
    [Arguments("""{"externalDocs":false}""", "document.external_docs", "/externalDocs")]
    [Arguments("""{"externalDocs":{}}""", "document.external_docs_url", "/externalDocs/url")]
    [Arguments("""{"externalDocs":{"url":null}}""", "document.external_docs_url", "/externalDocs/url")]
    [Arguments("""{"externalDocs":{"url":"https://docs.example","description":false}}""", "document.annotation", "/externalDocs/description")]
    [Arguments("""{"tags":[{"name":"t","externalDocs":{}}]}""", "document.external_docs_url", "/tags/0/externalDocs/url")]
    public async Task OpenRpcMalformedMethodMetadataInLaterMethodsPreventsSelection(
        string metadata, string code, string suffix)
    {
        var later = JsonNode.Parse(metadata)!.AsObject();
        later["name"] = "Other";
        later["params"] = new JsonArray();
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" },
            ["methods"] = new JsonArray(new JsonObject { ["name"] = "Target", ["params"] = new JsonArray() }, later)
        };
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Metadata URLs must not be invoked."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/methods/1" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task OpenRpcTagReferencesUseTheMethodSourceAndRetainNativeReferences()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json", """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"$ref":"./nested/method.json"}]}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            new Uri("https://api.example/root.json"), (uri, _) =>
            {
                calls.Add(uri);
                var json = uri switch
                {
                    "https://api.example/nested/method.json" =>
                        """{"name":"Target","params":[],"tags":[{"$ref":"./tags.json#/users"},{"$ref":"./tags.json#/users"}]}""",
                    "https://api.example/nested/tags.json" =>
                        """{"users":{"name":"users","externalDocs":{"url":"https://docs.example/users"}}}""",
                    _ => throw new InvalidOperationException("Unexpected URL " + uri)
                };
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(json));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("tags")[1].GetProperty("$ref").GetString()).IsEqualTo("./tags.json#/users");
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://api.example/nested/method.json"));
        await Assert.That(calls.SequenceEqual(["https://api.example/nested/method.json",
            "https://api.example/nested/tags.json"])).IsTrue();
    }

    [Test]
    [Arguments("unavailable", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("denied", ApiOperationResolutionStatus.Denied, "dependency.denied")]
    [Arguments("malformed", ApiOperationResolutionStatus.Invalid, "document.tag_name")]
    [Arguments("cycle", ApiOperationResolutionStatus.Indeterminate, "dependency.cycle")]
    public async Task OpenRpcTagDependencyFailuresCannotProducePartialSelections(
        string outcome, ApiOperationResolutionStatus expected, string code)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json", """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[],"tags":[{"$ref":"https://api.example/tag.json"}]}]}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) =>
            {
                calls++;
                if (outcome == "denied") { throw new UnauthorizedAccessException("Outside policy."); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(outcome switch
                {
                    "malformed" => "{}"u8.ToArray(),
                    "cycle" => """{"$ref":"#"}"""u8.ToArray(),
                    _ => (ReadOnlyMemory<byte>?)null
                });
            });
        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"links":[]}""")]
    [Arguments("""{"links":[{}]}""")]
    [Arguments("""{"links":[{"method":"Target","name":{"native":true},"params":{"a":"$request.params.a"},"summary":"","description":""}]}""")]
    [Arguments("""{"links":[{"method":"Later","server":{"url":"../{region}","variables":{"region":{"default":"west","enum":["east"]}}}}]}""")]
    [Arguments("""{"links":[{"method":"Target"},{"method":"Later"}]}""")]
    public async Task OpenRpcLinksResolveExactNativeMethodNamesWithoutExecutingCalls(string metadata)
    {
        var method = JsonNode.Parse(metadata)!.AsObject();
        method["name"] = "Target";
        method["params"] = new JsonArray();
        var native = method.ToJsonString();
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" }, ["methods"] = new JsonArray(method,
                new JsonObject
                {
                    ["name"] = "Later", ["params"] = new JsonArray(),
                    ["links"] = new JsonArray(new JsonObject { ["method"] = "Target" })
                })
        };
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Links are not invocations."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetRawText()).IsEqualTo(native);
        await Assert.That(result.DocumentPath).IsEqualTo("$/methods/0");
    }

    [Test]
    [Arguments("""{"links":{}}""", "document.links", "/links")]
    [Arguments("""{"links":[null]}""", "document.operation", "/links/0")]
    [Arguments("""{"links":[{"method":false}]}""", "document.annotation", "/links/0/method")]
    [Arguments("""{"links":[{"method":"target"}]}""", "document.link_method", "/links/0/method")]
    [Arguments("""{"links":[{"method":"Missing"}]}""", "document.link_method", "/links/0/method")]
    [Arguments("""{"links":[{"method":""}]}""", "document.link_method", "/links/0/method")]
    [Arguments("""{"links":[{"summary":[]}]}""", "document.annotation", "/links/0/summary")]
    [Arguments("""{"links":[{"description":false}]}""", "document.annotation", "/links/0/description")]
    [Arguments("""{"links":[{"server":null}]}""", "document.server", "/links/0/server")]
    [Arguments("""{"links":[{"server":{"$ref":"https://must-not-load.example/server"}}]}""", "document.server_url", "/links/0/server/url")]
    [Arguments("""{"links":[{"server":{"url":"/rpc","variables":{"v":{}}}}]}""", "document.server_default", "/links/0/server/variables/v/default")]
    public async Task OpenRpcInvalidLinksInLaterMethodsPreventAnEarlierMatch(
        string metadata, string code, string suffix)
    {
        var method = JsonNode.Parse(metadata)!.AsObject();
        method["name"] = "Other";
        method["params"] = new JsonArray();
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" }, ["methods"] = new JsonArray(
                new JsonObject { ["name"] = "Target", ["params"] = new JsonArray() }, method)
        };
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) => throw new InvalidOperationException("Links are not invocations."));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/methods/1" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("Target")]
    [Arguments("Unrelated")]
    public async Task OpenRpcLinkedMethodNamesMustIdentifyOneOccurrenceAcrossTheCompleteMethodArray(string selector)
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json", """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[
              {"name":"Target","params":[],"links":[{"method":"Duplicated"}]},
              {"name":"Duplicated","params":[]},{"$ref":"#/methods/1"}]}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/" + selector));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("document.link_method");
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/methods/0/links/0/method");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task OpenRpcLinkReferencesResolveFromTheirContainingMethodAndPreserveNativeLinkData()
    {
        var calls = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json", """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"$ref":"./nested/method.json"},{"name":"Later","params":[]}]}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            new Uri("https://api.example/root.json"), (uri, _) =>
            {
                calls.Add(uri);
                var json = uri switch
                {
                    "https://api.example/nested/method.json" =>
                        """{"name":"Target","params":[],"links":[{"$ref":"./links.json#/next"}]}""",
                    "https://api.example/nested/links.json" =>
                        """{"next":{"$ref":"./parts/link.json"}}""",
                    "https://api.example/nested/parts/link.json" => """
                        {"method":"Later","params":{"ref":{"$ref":"https://not-an-import.example/value"}},
                         "server":{"url":"https://must-not-invoke.example/rpc"}}
                        """,
                    _ => throw new InvalidOperationException("Unexpected URL " + uri)
                };
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(json));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("links")[0].GetProperty("$ref").GetString()).IsEqualTo("./links.json#/next");
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://api.example/nested/method.json"));
        await Assert.That(calls.SequenceEqual(["https://api.example/nested/method.json",
            "https://api.example/nested/links.json", "https://api.example/nested/parts/link.json"])).IsTrue();
    }

    [Test]
    [Arguments("unavailable", ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments("denied", ApiOperationResolutionStatus.Denied, "dependency.denied")]
    [Arguments("cycle", ApiOperationResolutionStatus.Indeterminate, "dependency.cycle")]
    [Arguments("malformed", ApiOperationResolutionStatus.Invalid, "document.annotation")]
    public async Task OpenRpcLinkDependencyFailuresDoNotBecomeSuccessfulSelections(
        string outcome, ApiOperationResolutionStatus expected, string code)
    {
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json", """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[],"links":[{"$ref":"https://api.example/link.json"}]}]}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) =>
            {
                calls++;
                if (outcome == "denied") { throw new UnauthorizedAccessException("Outside policy."); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(outcome switch
                {
                    "cycle" => """{"$ref":"#"}"""u8.ToArray(),
                    "malformed" => """{"method":0}"""u8.ToArray(),
                    _ => (ReadOnlyMemory<byte>?)null
                });
            });
        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"name":"Other"}""", "document.params", "/params")]
    [Arguments("""{"name":"Other","params":null}""", "document.params", "/params")]
    [Arguments("""{"name":"Other","params":{}}""", "document.params", "/params")]
    [Arguments("""{"name":"Other","params":[null]}""", "document.operation", "/params/0")]
    [Arguments("""{"name":"Other","params":[],"paramStructure":"BY-NAME"}""", "document.param_structure", "/paramStructure")]
    [Arguments("""{"name":"Other","params":[],"paramStructure":true}""", "document.param_structure", "/paramStructure")]
    [Arguments("""{"name":"Other","params":[],"summary":0}""", "document.annotation", "/summary")]
    [Arguments("""{"name":"Other","params":[],"description":null}""", "document.annotation", "/description")]
    [Arguments("""{"name":"Other","params":[],"deprecated":"false"}""", "document.deprecated", "/deprecated")]
    [Arguments("""{"name":"Other","params":[{}]}""", "document.content_name", "/params/0/name")]
    [Arguments("""{"name":"Other","params":[{"name":0,"schema":{}}]}""", "document.content_name", "/params/0/name")]
    [Arguments("""{"name":"Other","params":[{"name":"a"}]}""", "document.content_schema", "/params/0/schema")]
    [Arguments("""{"name":"Other","params":[{"name":"a","schema":[]}]}""", "document.content_schema", "/params/0/schema")]
    [Arguments("""{"name":"Other","params":[{"name":"a","schema":null}]}""", "document.content_schema", "/params/0/schema")]
    [Arguments("""{"name":"Other","params":[{"name":"a","schema":{},"required":0}]}""", "document.content_required", "/params/0/required")]
    [Arguments("""{"name":"Other","params":[{"name":"a","schema":{}},{"name":"a","schema":{}}]}""", "document.parameter_duplicate", "/params/1/name")]
    [Arguments("""{"name":"Other","params":[{"name":"a","schema":{}},{"name":"b","schema":{},"required":true}]}""", "document.parameter_order", "/params/1/required")]
    [Arguments("""{"name":"Other","params":[],"result":{}}""", "document.content_name", "/result/name")]
    [Arguments("""{"name":"Other","params":[],"result":true}""", "document.operation", "/result")]
    [Arguments("""{"name":"Other","params":[],"result":{"name":"r","schema":"string"}}""", "document.content_schema", "/result/schema")]
    [Arguments("""{"name":"Other","params":[],"result":{"name":"r","schema":{},"deprecated":1}}""", "document.deprecated", "/result/deprecated")]
    [Arguments("""{"name":"Other","params":[],"errors":{}}""", "document.errors", "/errors")]
    [Arguments("""{"name":"Other","params":[],"errors":[false]}""", "document.operation", "/errors/0")]
    [Arguments("""{"name":"Other","params":[],"errors":[{}]}""", "document.error_code", "/errors/0/code")]
    [Arguments("""{"name":"Other","params":[],"errors":[{"code":"1","message":"bad"}]}""", "document.error_code", "/errors/0/code")]
    [Arguments("""{"name":"Other","params":[],"errors":[{"code":1.1,"message":"bad"}]}""", "document.error_code", "/errors/0/code")]
    [Arguments("""{"name":"Other","params":[],"errors":[{"code":1}]}""", "document.error_message", "/errors/0/message")]
    [Arguments("""{"name":"Other","params":[],"errors":[{"code":1,"message":null}]}""", "document.error_message", "/errors/0/message")]
    [Arguments("""{"name":"Other","params":[],"errors":[{"code":1,"message":"a"},{"code":1.0,"message":"b"}]}""", "document.error_duplicate", "/errors/1/code")]
    [Arguments("""{"name":"Other","params":[],"errors":[{"code":0,"message":"a"},{"code":-0e99,"message":"b"}]}""", "document.error_duplicate", "/errors/1/code")]
    [Arguments("""{"name":"Other","params":[],"errors":[{"code":1e100,"message":"a"},{"code":10e99,"message":"b"}]}""", "document.error_duplicate", "/errors/1/code")]
    public async Task OpenRpcMalformedContractsInLaterMethodsPreventAnEarlierSelection(
        string method, string code, string suffix)
    {
        var document = """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[]},""" + method + "]}";
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Diagnostic.Path).IsEqualTo("$/methods/1" + suffix);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task OpenRpcDescriptorAndErrorReferencesUseTheirContainingSourcesAndRetainNativeReferences()
    {
        var requests = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json", """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"$ref":"./nested/method.json#/method"}]}
            """u8.ToArray(), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            new Uri("https://api.example/root.json"), (uri, _) =>
            {
                requests.Add(uri);
                var json = uri switch
                {
                    "https://api.example/nested/method.json" => """
                        {"method":{"name":"Target","params":[{"$ref":"./descriptors.json#/required"},{"$ref":"./descriptors.json#/optional"}],
                          "result":{"$ref":"./descriptors.json#/reply"},"errors":[{"$ref":"../errors.json#/denied"}]}}
                        """,
                    "https://api.example/nested/descriptors.json" => """
                        {"required":{"$ref":"./parts/required.json"},
                         "optional":{"name":"b","schema":false},"reply":{"name":"r","schema":{"$ref":"https://schema.example/not-acquired"}}}
                        """,
                    "https://api.example/nested/parts/required.json" =>
                        """{"name":"a","schema":true,"required":true}""",
                    "https://api.example/errors.json" => """{"denied":{"code":42,"message":"Denied"}}""",
                    _ => throw new InvalidOperationException("Unexpected import: " + uri)
                };
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(json));
            });
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://api.example/nested/method.json"));
        await Assert.That(result.DocumentPath).IsEqualTo("$/method");
        await Assert.That(result.Operation.GetProperty("params")[0].GetProperty("$ref").GetString())
            .IsEqualTo("./descriptors.json#/required");
        await Assert.That(result.Operation.GetProperty("result").GetProperty("$ref").GetString())
            .IsEqualTo("./descriptors.json#/reply");
        await Assert.That(requests.SequenceEqual([
            "https://api.example/nested/method.json", "https://api.example/nested/descriptors.json",
            "https://api.example/nested/parts/required.json", "https://api.example/errors.json"])).IsTrue();
    }

    [Test]
    [Arguments("params", false)]
    [Arguments("params", true)]
    [Arguments("result", false)]
    [Arguments("result", true)]
    [Arguments("errors", false)]
    [Arguments("errors", true)]
    public async Task OpenRpcContractDependenciesPreserveUnavailableAndDeniedFailures(string member, bool denied)
    {
        var method = new JsonObject { ["name"] = "Target", ["params"] = new JsonArray() };
        var reference = new JsonObject { ["$ref"] = "https://api.example/contract.json" };
        method[member] = member == "result" ? reference : new JsonArray(reference);
        var document = new JsonObject { ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" }, ["methods"] = new JsonArray(method) };
        var calls = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"),
            resolveReference: (_, _) =>
            {
                calls++;
                if (denied) { throw new UnauthorizedAccessException("Outside policy."); }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(denied
            ? ApiOperationResolutionStatus.Denied : ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(denied ? "dependency.denied" : "dependency.unresolved");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"name":"Target","params":[{"$ref":"#/cycle"}]}""")]
    [Arguments("""{"name":"Target","params":[],"result":{"$ref":"#/cycle"}}""")]
    [Arguments("""{"name":"Target","params":[],"errors":[{"$ref":"#/cycle"}]}""")]
    public async Task OpenRpcContractReferenceCyclesCannotProduceASelection(string method)
    {
        var document = """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[""" + method + """],"cycle":{"$ref":"#/cycle"}}""";
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.cycle");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"name":"Target","params":[{"$ref":"#/contract"},{"$ref":"#/contract"}]}""",
        """{"name":"a","schema":{}}""", "document.parameter_duplicate")]
    [Arguments("""{"name":"Target","params":[{"name":"a","schema":{}},{"$ref":"#/contract"}]}""",
        """{"name":"b","schema":{},"required":true}""", "document.parameter_order")]
    [Arguments("""{"name":"Target","params":[],"errors":[{"$ref":"#/contract"},{"$ref":"#/contract"}]}""",
        """{"code":123,"message":"Denied"}""", "document.error_duplicate")]
    public async Task OpenRpcReferenceReuseDoesNotBypassContractUniquenessOrOrdering(
        string method, string contract, string code)
    {
        var document = """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[""" + method + """],"contract":""" + contract + "}";
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(255, ApiOperationResolutionStatus.Selected)]
    [Arguments(256, ApiOperationResolutionStatus.Selected)]
    [Arguments(257, ApiOperationResolutionStatus.Indeterminate)]
    public async Task OpenRpcErrorCodeNormalizationHonorsTheExactNumberBudget(
        int digits, ApiOperationResolutionStatus expected)
    {
        var document = """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[],"errors":[{"code":CODE,"message":"Large"}]}]}
            """.Replace("CODE", new string('9', digits), StringComparison.Ordinal);
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document), ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("errors")[0].GetProperty("code").GetRawText())
                .IsEqualTo(new string('9', digits));
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.number");
            await Assert.That(result.Diagnostic.Path).IsEqualTo("$/methods/0/errors/0/code");
        }
    }

    [Test]
    [Arguments(63, ApiOperationResolutionStatus.Selected)]
    [Arguments(64, ApiOperationResolutionStatus.Selected)]
    [Arguments(65, ApiOperationResolutionStatus.Indeterminate)]
    public async Task OpenRpcContentReferencesHonorTheInclusiveReferenceDepthLimit(
        int depth, ApiOperationResolutionStatus expected)
    {
        var chain = new JsonObject();
        for (var index = 0; index < depth; index++)
        {
            chain["n" + index] = index == depth - 1
                ? new JsonObject { ["name"] = "value", ["schema"] = true }
                : new JsonObject { ["$ref"] = "#/chain/n" + (index + 1) };
        }
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" },
            ["methods"] = new JsonArray(new JsonObject
            {
                ["name"] = "Target", ["params"] = new JsonArray(),
                ["result"] = new JsonObject { ["$ref"] = "#/chain/n0" }
            }),
            ["chain"] = chain
        };
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/g/apis/a#operations/Target"));
        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("result").GetProperty("$ref").GetString()).IsEqualTo("#/chain/n0");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.reference_depth");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
    }

    [Test]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[]}""", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"target","params":[]}]}""", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[]},{"name":"Target","params":[]}]}""", ApiOperationResolutionStatus.Ambiguous, "selection.ambiguous")]
    [Arguments("""{"openrpc":"1.3.2","methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":1.4,"methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":"1.4.garbage","methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":"1.4.01","methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":"1.4.0-01","methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":"1.4.0"}""", ApiOperationResolutionStatus.Invalid, "document.methods")]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":{}}""", ApiOperationResolutionStatus.Invalid, "document.methods")]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[true]}""", ApiOperationResolutionStatus.Invalid, "document.operation")]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{}]}""", ApiOperationResolutionStatus.Invalid, "document.method_name")]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":0}]}""", ApiOperationResolutionStatus.Invalid, "document.method_name")]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":""}]}""", ApiOperationResolutionStatus.Invalid, "document.method_name")]
    [Arguments("""{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","name":"Other"}]}""", ApiOperationResolutionStatus.Invalid, "document.json")]
    public async Task OpenRpcResolutionDistinguishesAbsentAmbiguousAndInvalidDocuments(string document,
        ApiOperationResolutionStatus expected, string code)
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"));

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.DocumentPath).IsNull();
    }

    [Test]
    [Arguments("/methods/0")]
    [Arguments("services/Profile/operations/Target")]
    [Arguments("operations/query/Target")]
    public async Task OpenRpcDoesNotAcceptAnotherProfilesSelector(string selector)
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target"}]}"""u8.ToArray(),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#" + selector));

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selector.unsupported");
    }

    [Test]
    [Arguments("1.4.99")]
    [Arguments("1.4.0-rc.1")]
    [Arguments("1.4.0+build.01")]
    public async Task OpenRpcUsesTheDeclaredFeatureSetRatherThanTheApplicationVersion(string version)
    {
        var document = $$"""{"openrpc":"{{version}}","info":{"title":"Users","version":"7.0.0"},"methods":[{"name":"Target","params":[]}]}""";
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"));

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
    }

    [Test]
    public async Task OpenRpcResolvesLocalAndExternalMethodsThroughAuthorizedDocuments()
    {
        const string document = """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[
              {"$ref":"#/components/methods/Local"},{"$ref":"./shared.json#/methods/0"}
            ],"components":{"methods":{"Local":{"name":"Other","params":[]}}}}
            """;
        var requests = new List<string>();
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"),
            new Uri("https://api.example/openrpc.json"), (uri, token) =>
            {
                token.ThrowIfCancellationRequested();
                requests.Add(uri);
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                   """{"methods":[{"name":"Target","params":[]}]}"""u8.ToArray());
            });

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Selected);
        await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
        await Assert.That(result.Operation.GetProperty("params").GetArrayLength()).IsEqualTo(0);
        await Assert.That(result.DocumentUri).IsEqualTo(new Uri("https://api.example/shared.json"));
        await Assert.That(result.DocumentPath).IsEqualTo("$/methods/0");
        await Assert.That(requests.SequenceEqual(["https://api.example/shared.json"])).IsTrue();
    }

    [Test]
    [Arguments(false, ApiOperationResolutionStatus.Indeterminate, "dependency.unresolved")]
    [Arguments(true, ApiOperationResolutionStatus.Denied, "dependency.denied")]
    public async Task OpenRpcDoesNotReturnAnEarlyMatchWhenAnotherMethodIsUnavailableOrDenied(bool deny,
        ApiOperationResolutionStatus expected, string code)
    {
        const string document = """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[]},{"$ref":"https://other.example/method.json"}]}
            """;
        var loads = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"),
            resolveReference: (_, _) =>
            {
                loads++;
                if (deny)
                {
                   throw new UnauthorizedAccessException("The dependency is outside the allowlist.");
                }
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            });

        await Assert.That(loads).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task OpenRpcNeverLoadsAnExternalMethodWithoutACallback()
    {
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"$ref":"file:///not-authorized.json"}]}"""u8.ToArray(),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"));

        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("dependency.unresolved");
    }

    [Test]
    [Arguments("""{"$ref":"#/methods/0"}""", "dependency.cycle")]
    [Arguments("""{"$ref":true}""", "dependency.operation")]
    [Arguments("""{"$ref":"#/missing"}""", "dependency.pointer")]
    [Arguments("""{"$ref":"#/%GG"}""", "dependency.pointer")]
    [Arguments("""{"$ref":"#/%C3"}""", "dependency.pointer")]
    public async Task OpenRpcRejectsCyclicAndInvalidMethodReferences(string method, string code)
    {
        var document = """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[""" + method + "]}";
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"));

        await Assert.That(result.Status).IsEqualTo(code == "dependency.cycle"
            ? ApiOperationResolutionStatus.Indeterminate : ApiOperationResolutionStatus.Invalid);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(code);
    }

    [Test]
    public async Task OpenRpcCachesARepeatedDependencyButDoesNotDeduplicateMethodDeclarations()
    {
        const string document = """
            {"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[
              {"$ref":"https://api.example/method.json"},{"$ref":"https://api.example/method.json"}
            ]}
            """;
        var loads = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"),
            resolveReference: (_, _) =>
            {
                loads++;
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>( """{"name":"Target","params":[]}"""u8.ToArray());
            });

        await Assert.That(loads).IsEqualTo(1);
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Ambiguous);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("selection.ambiguous");
    }

    [Test]
    [Arguments(-1, ApiOperationResolutionStatus.Selected)]
    [Arguments(0, ApiOperationResolutionStatus.Selected)]
    [Arguments(1, ApiOperationResolutionStatus.Indeterminate)]
    public async Task ApiResolverEnforcesTheInclusiveJsonNodeLimit(int excess, ApiOperationResolutionStatus expected)
    {
        var padding = string.Join(",", Enumerable.Repeat("null",
            ApiOperationDocumentResolver.Capabilities.MaxDocumentNodes - 10 + excess));
        var document = """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[]}],"padding":[""" + padding + "]}";
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"));

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.nodes");
        }
    }

    [Test]
    [Arguments(61, ApiOperationResolutionStatus.Selected)]
    [Arguments(62, ApiOperationResolutionStatus.Selected)]
    [Arguments(63, ApiOperationResolutionStatus.Indeterminate)]
    public async Task ApiResolverCountsRootAndScalarInItsJsonDepthLimit(int arrays, ApiOperationResolutionStatus expected)
    {
        var document = """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"name":"Target","params":[]}],"padding":""" +
            new string('[', arrays) + "null" + new string(']', arrays) + "}";
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"));

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.depth");
        }
    }

    [Test]
    [Arguments(63, ApiOperationResolutionStatus.Selected)]
    [Arguments(64, ApiOperationResolutionStatus.Selected)]
    [Arguments(65, ApiOperationResolutionStatus.Indeterminate)]
    public async Task LocalMethodReferencesHaveAnInclusiveFiniteDepth(int depth, ApiOperationResolutionStatus expected)
    {
        var chain = new JsonObject();
        for (var index = 0; index < depth; index++)
        {
            chain["n" + index] = index + 1 == depth
                ? new JsonObject { ["name"] = "Target", ["params"] = new JsonArray() }
                : new JsonObject { ["$ref"] = "#/chain/n" + (index + 1) };
        }
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" },
            ["methods"] = new JsonArray(new JsonObject { ["$ref"] = "#/chain/n0" }),
            ["chain"] = chain,
        };
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"));

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
            await Assert.That(result.DocumentPath).IsEqualTo("$/chain/n" + (depth - 1));
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.reference_depth");
        }
    }

    [Test]
    [Arguments(-1, ApiOperationResolutionStatus.Selected)]
    [Arguments(0, ApiOperationResolutionStatus.Selected)]
    [Arguments(1, ApiOperationResolutionStatus.Indeterminate)]
    public async Task ApiResolverEnforcesTheInclusiveDocumentByteLimit(int excess, ApiOperationResolutionStatus expected)
    {
        const string prefix = "{\"openrpc\":\"1.4.0\",\"info\":{\"title\":\"API\",\"version\":\"1\"},\"methods\":[{\"name\":\"Target\",\"params\":[]}],\"description\":\"";
        const string suffix = "\"}";
        var document = prefix + new string('x', ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes
            - prefix.Length - suffix.Length + excess) + suffix;
        var result = ApiOperationDocumentResolver.Resolve("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"));

        await Assert.That(result.Status).IsEqualTo(expected);
        if (expected == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.bytes");
        }
    }

    [Test]
    [Arguments(3, ApiOperationResolutionStatus.Selected)]
    [Arguments(4, ApiOperationResolutionStatus.Indeterminate)]
    public async Task ApiResolverBoundsTotalDependencyBytes(int count, ApiOperationResolutionStatus expected)
    {
        var methods = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            methods.Add((JsonNode)new JsonObject { ["$ref"] = $"https://api.example/{index}.json" });
        }
        var document = new JsonObject { ["openrpc"] = "1.4.0", ["info"] = new JsonObject { ["title"] = "API", ["version"] = "1" }, ["methods"] = methods };
        var loads = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"),
            resolveReference: (_, _) =>
            {
                var name = loads++ == 0 ? "Target" : "Other" + loads.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var prefix = "{\"name\":\"" + name + "\",\"params\":[],\"description\":\"";
                const string suffix = "\"}";
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(System.Text.Encoding.UTF8.GetBytes(prefix +
                   new string('x', ApiOperationDocumentResolver.Capabilities.MaxDocumentBytes
                       - prefix.Length - suffix.Length) + suffix));
            });

        await Assert.That(result.Status).IsEqualTo(expected);
        await Assert.That(loads).IsEqualTo(count);
        if (expected == ApiOperationResolutionStatus.Selected)
        {
            await Assert.That(result.Operation.GetProperty("name").GetString()).IsEqualTo("Target");
        }
        else
        {
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.total_bytes");
        }
    }

    [Test]
    public async Task ApiResolverHasADeadlineEvenWhenTheDependencyCallbackNeverCompletes()
    {
        var pending = new TaskCompletionSource<ReadOnlyMemory<byte>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
                """{"openrpc":"1.4.0","info":{"title":"API","version":"1"},"methods":[{"$ref":"https://api.example/method.json"}]}"""u8.ToArray(),
                ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"),
                resolveReference: (_, _) => new(pending.Task));

            await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Indeterminate);
            await Assert.That(result.Diagnostic!.Code).IsEqualTo("limit.duration");
            await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        }
        finally
        {
            pending.TrySetResult(null);
        }
    }
}
