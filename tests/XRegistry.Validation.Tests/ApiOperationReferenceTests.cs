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

    [Test]
    public async Task ParsesAnOperationIdReference()
    {
        var parsed = ApiOperationReference.Parse(
            "https://registry.example/apigroups/users/apis/Profile#operations/GetUserProfile");

        await Assert.That(parsed.EntityReference)
            .IsEqualTo("https://registry.example/apigroups/users/apis/Profile");
        await Assert.That(parsed.RegistryEntityPointer).IsNull();
        await Assert.That(parsed.Selector.Kind).IsEqualTo(ApiOperationSelectorKind.Operation);
        await Assert.That(parsed.Selector.Tokens).IsEquivalentTo(s_operationTokens);
    }

    [Test]
    public async Task ParsesServiceAndGraphQlSelectors()
    {
        var service = ApiOperationReference.Parse(
            "/apigroups/users/apis/Profile#services/com.example.Profile/operations/GetProfile");
        var graphQl = ApiOperationReference.Parse(
            "/apigroups/users/apis/Profile#operations/subscription/profileChanged");

        await Assert.That(service.Selector.Kind).IsEqualTo(ApiOperationSelectorKind.ServiceOperation);
        await Assert.That(service.Selector.Tokens).IsEquivalentTo(s_serviceTokens);
        await Assert.That(graphQl.Selector.Kind).IsEqualTo(ApiOperationSelectorKind.GraphQlField);
        await Assert.That(graphQl.Selector.Tokens).IsEquivalentTo(s_graphQlTokens);
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
        await Assert.That(overload.Selector.Tokens).IsEquivalentTo(s_interfaceTokens);
        await Assert.That(pointer.Selector.Kind).IsEqualTo(ApiOperationSelectorKind.JsonPointer);
        await Assert.That(pointer.Selector.Tokens).IsEquivalentTo(s_pointerTokens);
    }

    [Test]
    public async Task ParsesDocumentViewReferencesAndDecodesOnce()
    {
        var parsed = ApiOperationReference.Parse(
            "#/apigroups/users/apis/Profile/versions/2:operations/a~01b%252F");
        var selector = parsed.Selector.Tokens;

        await Assert.That(parsed.EntityReference).IsNull();
        await Assert.That(parsed.RegistryEntityPointer).IsEquivalentTo(s_registryPointerTokens);
        await Assert.That(selector).IsEquivalentTo(s_singleDecodeTokens);
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
        await Assert.That(ApiOperationDocumentResolver.Capabilities.Formats)
            .IsEquivalentTo(["OpenAPI/3.0", "OpenAPI/3.1", "Swagger/2.0", "OpenRPC/1.4"]);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.ContentTypes)
            .IsEquivalentTo(["application/json"]);
        await Assert.That(ApiOperationDocumentResolver.Capabilities.Selectors)
            .IsEquivalentTo([ApiOperationSelectorKind.Operation, ApiOperationSelectorKind.JsonPointer]);
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
        await Assert.That(requests).IsEquivalentTo(["https://api.example/shared.json"]);
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
            methods.Add(new JsonObject { ["$ref"] = $"https://api.example/{index}.json" });
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
