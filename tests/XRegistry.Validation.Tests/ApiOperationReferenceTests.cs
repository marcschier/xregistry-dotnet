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
              "example#Get":{"type":"operation","traits":{"example#Custom":{}}},
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
                .IsEqualTo("{}");
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
    [Arguments("enum", """{"A":{"target":"smithy.api#Unit","traits":{"smithy.api#enumValue":""}}}""")]
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
            {"openrpc":"1.4.0","info":{"version":"9.2.0"},"methods":[
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
    [Arguments("""{"openrpc":"1.4.0","methods":[]}""", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    [Arguments("""{"openrpc":"1.4.0","methods":[{"name":"target"}]}""", ApiOperationResolutionStatus.NotFound, "selection.not_found")]
    [Arguments("""{"openrpc":"1.4.0","methods":[{"name":"Target"},{"name":"Target"}]}""", ApiOperationResolutionStatus.Ambiguous, "selection.ambiguous")]
    [Arguments("""{"openrpc":"1.3.2","methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":1.4,"methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":"1.4.garbage","methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":"1.4.01","methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":"1.4.0-01","methods":[]}""", ApiOperationResolutionStatus.Invalid, "document.openrpc_version")]
    [Arguments("""{"openrpc":"1.4.0"}""", ApiOperationResolutionStatus.Invalid, "document.methods")]
    [Arguments("""{"openrpc":"1.4.0","methods":{}}""", ApiOperationResolutionStatus.Invalid, "document.methods")]
    [Arguments("""{"openrpc":"1.4.0","methods":[true]}""", ApiOperationResolutionStatus.Invalid, "document.operation")]
    [Arguments("""{"openrpc":"1.4.0","methods":[{}]}""", ApiOperationResolutionStatus.Invalid, "document.method_name")]
    [Arguments("""{"openrpc":"1.4.0","methods":[{"name":0}]}""", ApiOperationResolutionStatus.Invalid, "document.method_name")]
    [Arguments("""{"openrpc":"1.4.0","methods":[{"name":""}]}""", ApiOperationResolutionStatus.Invalid, "document.method_name")]
    [Arguments("""{"openrpc":"1.4.0","methods":[{"name":"Target","name":"Other"}]}""", ApiOperationResolutionStatus.Invalid, "document.json")]
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
            """{"openrpc":"1.4.0","methods":[{"name":"Target"}]}"""u8.ToArray(),
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
        var document = $$"""{"openrpc":"{{version}}","info":{"version":"7.0.0"},"methods":[{"name":"Target"}]}""";
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
            {"openrpc":"1.4.0","methods":[
              {"$ref":"#/components/methods/Local"},{"$ref":"./shared.json#/methods/0"}
            ],"components":{"methods":{"Local":{"name":"Other"}}}}
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
            {"openrpc":"1.4.0","methods":[{"name":"Target"},{"$ref":"https://other.example/method.json"}]}
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
            """{"openrpc":"1.4.0","methods":[{"$ref":"file:///not-authorized.json"}]}"""u8.ToArray(),
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
        var document = """{"openrpc":"1.4.0","methods":[""" + method + "]}";
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
            {"openrpc":"1.4.0","methods":[
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
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>( """{"name":"Target"}"""u8.ToArray());
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
            ApiOperationDocumentResolver.Capabilities.MaxDocumentNodes - 6 + excess));
        var document = """{"openrpc":"1.4.0","methods":[{"name":"Target"}],"padding":[""" + padding + "]}";
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
        var document = """{"openrpc":"1.4.0","methods":[{"name":"Target"}],"padding":""" +
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
                ? new JsonObject { ["name"] = "Target" }
                : new JsonObject { ["$ref"] = "#/chain/n" + (index + 1) };
        }
        var document = new JsonObject
        {
            ["openrpc"] = "1.4.0",
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
        const string prefix = "{\"openrpc\":\"1.4.0\",\"methods\":[{\"name\":\"Target\"}],\"description\":\"";
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
        var document = new JsonObject { ["openrpc"] = "1.4.0", ["methods"] = methods };
        var loads = 0;
        var result = await ApiOperationDocumentResolver.ResolveAsync("OpenRPC/1.4", "application/json",
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()),
            ApiOperationReference.Parse("/apigroups/users/apis/Profile#operations/Target"),
            resolveReference: (_, _) =>
            {
                var name = loads++ == 0 ? "Target" : "Other";
                var prefix = "{\"name\":\"" + name + "\",\"description\":\"";
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
                """{"openrpc":"1.4.0","methods":[{"$ref":"https://api.example/method.json"}]}"""u8.ToArray(),
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
