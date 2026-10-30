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
        await Assert.That(result.Diagnostic!.Code).IsEqualTo(operationTrait ? "document.target_type" : "document.trait_target");
        await Assert.That(result.Diagnostic.Path).IsEqualTo(operationTrait ? path : path + "/traits/" + traitName);
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
        var result = ResolveSmithy(SmithyDefaultDocument("timestamp", "\"2026-01-01T00:00:00Z\"", false));
        await Assert.That(result.Status).IsEqualTo(ApiOperationResolutionStatus.Unsupported);
        await Assert.That(result.Diagnostic!.Code).IsEqualTo("smithy.timestamp_default");
        await Assert.That(result.Operation.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("bigInteger", "\"123456789012345678901234567890\"")]
    [Arguments("bigDecimal", "\"12345678901234567890.123456789\"")]
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
