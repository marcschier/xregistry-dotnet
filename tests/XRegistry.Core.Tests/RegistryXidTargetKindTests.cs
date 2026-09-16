// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryXidTargetKindTests
{
    [Test]
    [Arguments("/gs/rs")]
    [Arguments("/gs/rs[/versions]")]
    public async Task ResourceTargetsRejectMeta(string target)
    {
        var model = Model(target);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"ref":"/gs/g/rs/r/meta"}"""), model.Attributes["body"].Attributes,
            new() { Model = model }));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/ref");
    }

    [Test]
    [Arguments("/gs", "/gs/not-created")]
    [Arguments("/gs/rs", "/gs/not-created/rs/missing")]
    [Arguments("/gs/rs[/versions]", "/gs/not-created/rs/missing")]
    [Arguments("/gs/rs[/versions]", "/gs/not-created/rs/missing/versions/v1")]
    [Arguments("/gs/rs/versions", "/gs/not-created/rs/missing/versions/v1")]
    public async Task DeclaredTargetsAcceptDanglingEntityIdsWithoutExistenceChecks(string target, string xid)
    {
        var model = Model(target);
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("{\"ref\":\"" + xid + "\"}"),
            model.Attributes["body"].Attributes, new() { Model = model });

        await Assert.That(result.Metadata.RootElement.GetProperty("ref").GetString()).IsEqualTo(xid);
        await Assert.That(result.Obligations.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("/gs/rs", "/gs/g/rs/r/versions/v1")]
    [Arguments("/gs/rs/versions", "/gs/g/rs/r")]
    [Arguments("/gs/rs/versions", "/gs/g/rs/r/meta")]
    [Arguments("/gs", "/gs/g/rs/r")]
    [Arguments("/gs/rs", "/other/g/rs/r")]
    [Arguments("/gs/rs", "/gs/g/others/r")]
    public async Task TargetsRejectTheWrongEntityKindOrDeclaredModelType(string target, string xid)
    {
        var model = Model(target);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"ref\":\"" + xid + "\"}"), model.Attributes["body"].Attributes,
            new() { Model = model }));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/ref");
    }

    [Test]
    [Arguments("/gs/rs", "array", """["/gs/g/rs/r/meta"]""", "/ref/0")]
    [Arguments("/gs/rs", "map", """{"item":"/gs/g/rs/r/meta"}""", "/ref/item")]
    [Arguments("/gs/rs[/versions]", "array", """["/gs/g/rs/r/meta"]""", "/ref/0")]
    [Arguments("/gs/rs[/versions]", "map", """{"item":"/gs/g/rs/r/meta"}""", "/ref/item")]
    public async Task CollectionItemTargetsRejectMetaAtTheItemPointer(string target, string collectionType,
        string value, string expectedPath)
    {
        var model = Model(target, collectionType);
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"ref\":" + value + "}"), model.Attributes["body"].Attributes,
            new() { Model = model }));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("/")]
    [Arguments("/gs/g/rs/r/meta")]
    public async Task TargetlessXidsKeepTheirUnrestrictedEntityKinds(string xid)
    {
        var model = Model(null);
        var result = RegistryMetadataValidator.Validate(RegistryJson.Parse("{\"ref\":\"" + xid + "\"}"),
            model.Attributes["body"].Attributes, new() { Model = model });

        await Assert.That(result.Metadata.RootElement.GetProperty("ref").GetString()).IsEqualTo(xid);
        await Assert.That(result.Obligations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MissingModelContextReturnsAnExplicitObligationWithoutResolvingEntities()
    {
        var model = Model("/gs/rs");
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"ref":"/gs/not-created/rs/missing"}"""), model.Attributes["body"].Attributes);

        await Assert.That(result.Metadata.RootElement.GetProperty("ref").GetString()).IsEqualTo("/gs/not-created/rs/missing");
        await Assert.That(result.Obligations.Count).IsEqualTo(1);
        await Assert.That(result.Obligations[0].Kind).IsEqualTo("target");
        await Assert.That(result.Obligations[0].Path).IsEqualTo("/ref");
    }

    private static RegistryModel Model(string? target, string? collectionType = null)
    {
        var reference = target is null ? """{"type":"xid"}""" : "{\"type\":\"xid\",\"target\":\"" + target + "\"}";
        var definition = collectionType is null ? reference : "{\"type\":\"" + collectionType + "\",\"item\":" + reference + "}";
        return RegistryModel.Compile(RegistryJson.Parse(
            "{\"attributes\":{\"body\":{\"type\":\"object\",\"attributes\":{\"ref\":" + definition + "}}}," +
            """
            "groups":{
              "gs":{"singular":"g","resources":{"rs":{"singular":"r"},"others":{"singular":"other"}}},
              "other":{"singular":"other","resources":{"rs":{"singular":"r"}}}
            }}
            """));
    }
}
