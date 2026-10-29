// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class ApiRegistryRulesTests
{
    [Test]
    [Arguments("""{"format":""}""")]
    [Arguments("""{"format":"/3.1"}""")]
    [Arguments("""{"format":"OpenAPI/"}""")]
    [Arguments("""{"format":"OpenAPI/3.1/preview"}""")]
    [Arguments("""{"format":"Open API/3.1"}""")]
    public async Task ApiGroupFormatMustBeAnIdentifier(string metadata)
    {
        var input = RegistryJson.Parse(metadata);
        var failure = await Assert.That(() => RegistryDomainRules.ValidateApiGroupMetadata(input.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an API format diagnostic.");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/format");
    }

    [Test]
    public async Task ApiGroupMayOmitFormat()
    {
        var input = RegistryJson.Parse("{}");
        await Assert.That(() => RegistryDomainRules.ValidateApiGroupMetadata(input.RootElement)).ThrowsNothing();
    }

    [Test]
    public async Task ApiVersionFormatMatchesGroupCaseInsensitively()
    {
        var group = RegistryJson.Parse("""{"format":"OpenAPI/3.1"}""");
        var version = RegistryJson.Parse("""{"format":"openapi/3.1"}""");

        await Assert.That(() => RegistryDomainRules.ValidateApiVersionMetadata(version.RootElement, group.RootElement))
            .ThrowsNothing();
    }

    [Test]
    public async Task ApiVersionMayUseAnUnversionedFormatIdentifier()
    {
        var group = RegistryJson.Parse("{}");
        var version = RegistryJson.Parse("""{"format":"Thrift"}""");

        await Assert.That(() => RegistryDomainRules.ValidateApiVersionMetadata(version.RootElement, group.RootElement))
            .ThrowsNothing();
    }

    [Test]
    public async Task ApiVersionFormatRejectsDifferentGroupFormat()
    {
        var group = RegistryJson.Parse("""{"format":"OpenAPI/3.1"}""");
        var version = RegistryJson.Parse("""{"format":"OpenAPI/3.0"}""");
        var failure = await Assert.That(() =>
            RegistryDomainRules.ValidateApiVersionMetadata(version.RootElement, group.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an API format diagnostic.");

        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/format");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"format":""}""")]
    public async Task ApiVersionFormatIsRequiredAndNonempty(string metadata)
    {
        var group = RegistryJson.Parse("{}");
        var version = RegistryJson.Parse(metadata);
        var failure = await Assert.That(() =>
            RegistryDomainRules.ValidateApiVersionMetadata(version.RootElement, group.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("Expected an API format diagnostic.");

        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/format");
    }

    [Test]
    public async Task ApiGroupEqualsConstraintUsesCaseInsensitiveFormatComparison()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Api);
        var group = model.Groups["apigroups"];
        var resource = group.Resources["apis"];
        var groupMetadata = RegistryJson.Parse("""{"format":"OpenAPI/3.1"}""");
        var versionMetadata = RegistryJson.Parse("""{"format":"openapi/3.1"}""");

        await Assert.That(() => RegistryMetadataValidator.ValidateGroupConstraints(
            versionMetadata, group, resource, groupMetadata.RootElement)).ThrowsNothing();
    }

    [Test]
    public async Task ApiMatchVersionsUsesCoreCaseSensitiveFormatComparison()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Api);
        var resource = model.Groups["apigroups"].Resources["apis"];
        var first = JsonNode.Parse("\"OpenAPI/3.1\"");
        var sameFormat = JsonNode.Parse("\"openapi/3.1\"");
        var differentVersion = JsonNode.Parse("\"OpenAPI/3.0\"");

        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            first, first)).IsTrue();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            first, sameFormat)).IsFalse();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            first, differentVersion)).IsFalse();

        using var firstDocument = System.Text.Json.JsonDocument.Parse("\"OpenAPI/3.1\"");
        using var sameFormatDocument = System.Text.Json.JsonDocument.Parse("\"openapi/3.1\"");
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            firstDocument.RootElement, firstDocument.RootElement)).IsTrue();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            firstDocument.RootElement, sameFormatDocument.RootElement)).IsFalse();
    }

    [Test]
    public async Task ApiVersionFormatsEqualJsonNodeUsesOrdinalCaseInsensitiveCompleteIdentifierEquality()
    {
        var exact = JsonNode.Parse("\"OpenAPI/3.1\"");
        var caseOnly = JsonNode.Parse("\"openapi/3.1\"");
        var differentVersion = JsonNode.Parse("\"OpenAPI/3.0\"");
        var differentFamily = JsonNode.Parse("\"AsyncAPI/3.1\"");

        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, exact)).IsTrue();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, caseOnly)).IsTrue();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, differentVersion)).IsFalse();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, differentFamily)).IsFalse();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(null, null)).IsFalse();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, JsonNode.Parse("31"))).IsFalse();
    }

    [Test]
    public async Task ApiVersionFormatsEqualJsonElementUsesOrdinalCaseInsensitiveCompleteIdentifierEquality()
    {
        using var exactDocument = System.Text.Json.JsonDocument.Parse("\"OpenAPI/3.1\"");
        using var caseOnlyDocument = System.Text.Json.JsonDocument.Parse("\"openapi/3.1\"");
        using var differentVersionDocument = System.Text.Json.JsonDocument.Parse("\"OpenAPI/3.0\"");
        using var differentFamilyDocument = System.Text.Json.JsonDocument.Parse("\"AsyncAPI/3.1\"");
        using var nullDocument = System.Text.Json.JsonDocument.Parse("null");
        using var numberDocument = System.Text.Json.JsonDocument.Parse("31");

        var exact = exactDocument.RootElement;
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, exact)).IsTrue();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, caseOnlyDocument.RootElement)).IsTrue();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, differentVersionDocument.RootElement)).IsFalse();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, differentFamilyDocument.RootElement)).IsFalse();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, nullDocument.RootElement)).IsFalse();
        await Assert.That(RegistryMetadataValidator.ApiVersionFormatsEqual(exact, numberDocument.RootElement)).IsFalse();
    }

    [Test]
    public async Task NonApiStringMatchVersionsRemainOrdinalAndCaseSensitive()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note"}}}}}
            """));
        var resource = model.Groups["teams"].Resources["notes"];
        var exactNode = JsonNode.Parse("\"OpenAPI/3.1\"");
        var caseOnlyNode = JsonNode.Parse("\"openapi/3.1\"");

        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            exactNode, exactNode)).IsTrue();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            exactNode, caseOnlyNode)).IsFalse();

        using var exactDocument = System.Text.Json.JsonDocument.Parse("\"OpenAPI/3.1\"");
        using var caseOnlyDocument = System.Text.Json.JsonDocument.Parse("\"openapi/3.1\"");
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            exactDocument.RootElement, exactDocument.RootElement)).IsTrue();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            exactDocument.RootElement, caseOnlyDocument.RootElement)).IsFalse();
    }

    [Test]
    public async Task ApiMatchVersionsRemainCaseSensitiveForFormatAndNestedPaths()
    {
        var model = BuiltInRegistryModels.Compile(RegistryModelKind.Api);
        var resource = model.Groups["apigroups"].Resources["apis"];
        var exactNode = JsonNode.Parse("\"OpenAPI/3.1\"");
        var caseOnlyNode = JsonNode.Parse("\"openapi/3.1\"");

        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            exactNode, caseOnlyNode)).IsFalse();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["name"], RegistryValueType.String,
            exactNode, caseOnlyNode)).IsFalse();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["nested", "format"], RegistryValueType.String,
            exactNode, caseOnlyNode)).IsFalse();

        using var exactDocument = System.Text.Json.JsonDocument.Parse("\"OpenAPI/3.1\"");
        using var caseOnlyDocument = System.Text.Json.JsonDocument.Parse("\"openapi/3.1\"");
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["format"], RegistryValueType.String,
            exactDocument.RootElement, caseOnlyDocument.RootElement)).IsFalse();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["name"], RegistryValueType.String,
            exactDocument.RootElement, caseOnlyDocument.RootElement)).IsFalse();
        await Assert.That(RegistryMetadataValidator.MatchVersionsEqual(resource, ["nested", "format"], RegistryValueType.String,
            exactDocument.RootElement, caseOnlyDocument.RootElement)).IsFalse();
    }
}
