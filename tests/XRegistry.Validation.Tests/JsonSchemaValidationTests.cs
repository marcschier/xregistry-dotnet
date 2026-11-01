// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Validation.Tests;

public class JsonSchemaValidationTests
{
    [Test]
    public async Task JsonSchemaPatternPropertyKeysUseDecodedCharacterRangeOrdering()
    {
        var validator = new BuiltInDocumentValidator();
        var valid = await validator.ValidateAsync("JsonSchema/draft-07",
            """{"patternProperties":{"[\\x41-\\u005a]":{"type":"string"}}}"""u8.ToArray());
        var invalid = await validator.ValidateAsync("JsonSchema/draft-07",
            """{"patternProperties":{"[\\x5a-\\u0041]":{"type":"string"}}}"""u8.ToArray());
        await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(invalid.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(invalid.Diagnostics.Single().Code).IsEqualTo("schema.pattern");
        await Assert.That(invalid.Diagnostics.Single().Path).IsEqualTo("$/patternProperties");
    }

    [Test]
    [Arguments(@"[\x41-\x5a]")]
    [Arguments(@"[\u0041-\u005A]")]
    [Arguments(@"[A-\u005a]")]
    [Arguments(@"[\x41-Z]")]
    [Arguments(@"[\uD800-\uDFFF]")]
    [Arguments(@"[^\x00-\x1f]")]
    [Arguments(@"[\b-\t]")]
    [Arguments(@"[\n-\r]")]
    [Arguments(@"[\cA-\cZ]")]
    [Arguments(@"[\0-\x09]")]
    [Arguments(@"[\d\w\s]")]
    [Arguments(@"[\x2d]")]
    [Arguments(@"[\[-\]]")]
    [Arguments(@"[\x41-]")]
    [Arguments(@"[]")]
    [Arguments(@"[^]")]
    public async Task JsonSchemaPatternsDecodeSupportedClassCharactersBeforeRangeComparison(string pattern)
    {
        foreach (var format in new[] { "JsonSchema/draft-07", "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12" })
        {
            var schema = Encoding.UTF8.GetBytes("{\"pattern\":\"" + JsonEncodedText.Encode(pattern) + "\"}");
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, schema);
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(@"[\x5a-\x41]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\u005A-\u0041]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[Z-\x41]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\r-\n]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\cZ-\cA]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\x0]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\u001]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\u00xz]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\c]", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\x41", DocumentValidationStatus.Invalid)]
    [Arguments(@"[\d-Z]", DocumentValidationStatus.Unsupported)]
    [Arguments(@"[A-\w]", DocumentValidationStatus.Unsupported)]
    [Arguments(@"[\p{L}]", DocumentValidationStatus.Unsupported)]
    [Arguments(@"[\01]", DocumentValidationStatus.Unsupported)]
    [Arguments(@"\01", DocumentValidationStatus.Unsupported)]
    public async Task JsonSchemaClassRangeFailuresRemainExplicit(string pattern, DocumentValidationStatus status)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12",
            Encoding.UTF8.GetBytes("{\"pattern\":\"" + JsonEncodedText.Encode(pattern) + "\"}"));
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(
            status == DocumentValidationStatus.Invalid ? "schema.pattern" : "schema.pattern_unsupported");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/pattern");
    }

    [Test]
    [Arguments(-1, DocumentValidationStatus.Indeterminate)]
    [Arguments(0, DocumentValidationStatus.Valid)]
    [Arguments(1, DocumentValidationStatus.Valid)]
    public async Task JsonSchemaEscapedClassCharactersRetainTheExactPatternWorkBudget(int delta, DocumentValidationStatus status)
    {
        const string pattern = @"[\x41-\u005a]";
        var bytes = Encoding.UTF8.GetBytes("{\"pattern\":\"" + JsonEncodedText.Encode(pattern) + "\"}");
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", bytes,
            new() { MaxWork = bytes.Length + 1 + pattern.Length + delta });
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("limit.work"); }
    }

    [Test]
    public async Task DraftSevenNamedSchemasRetainContainingPointerRoots()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", """
            {"definitions":{"Named":{"$id":"#name","allOf":[{"$ref":"#/definitions/Target"}]},
              "Target":{"type":"string"},"Nested":{"$id":"child","definitions":{
                "Named":{"$id":"#name","allOf":[{"$ref":"#/definitions/Target"}]},"Target":{"type":"integer"}}}}}
            """u8.ToArray(), new() { DocumentUri = new("https://schemas.test/root"), MaxReferences = 0 });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DraftSevenAcquiredDocumentsRegisterNestedAndNamedAliases()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", """
            {"$id":"https://schemas.test/root","allOf":[{"$ref":"retrieved#name"},
              {"$ref":"child#name"},{"$ref":"child#/definitions/Named"}]}
            """u8.ToArray(), new()
            {
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>( """
                        {"$id":"https://schemas.test/canonical","definitions":{"Named":{"$id":"#name","type":"string"},
                          "Nested":{"$id":"child","definitions":{"Named":{"$id":"#name","type":"integer"}}}}}
                        """u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/retrieved"])).IsTrue();
    }

    [Test]
    [Arguments(-1, DocumentValidationStatus.Indeterminate)]
    [Arguments(0, DocumentValidationStatus.Valid)]
    [Arguments(1, DocumentValidationStatus.Valid)]
    public async Task DraftSevenNamedIdentifierRegistrationHasExactWorkBounds(int delta, DocumentValidationStatus status)
    {
        var bytes = """{"definitions":{"Value":{"$id":"#node","type":"string"}}}"""u8.ToArray();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", bytes,
            new() { MaxWork = bytes.Length + 2 + "node".Length + delta });
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("limit.work"); }
    }

    [Test]
    public async Task DraftSevenCircularNamedIdentifiersTerminateWithoutAcquisition()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", """
            {"definitions":{"A":{"$id":"#alice","allOf":[{"$ref":"#bob"}]},
              "B":{"$id":"#bob","allOf":[{"$ref":"#alice"}]}}}
            """u8.ToArray(), new() { MaxReferences = 0 });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("#/definitions/A")]
    [Arguments("#foo")]
    [Arguments("#%66oo")]
    [Arguments("other.json#bar")]
    [Arguments("other.json#/definitions/X")]
    [Arguments("#/definitions/B/definitions/Y")]
    [Arguments("t/inner.json")]
    [Arguments("urn:example:leaf")]
    public async Task DraftSevenPublishedIdExampleResolvesAllIdentifierForms(string reference)
    {
        var calls = 0;
        var schema = Encoding.UTF8.GetBytes($$$$$"""
            {"$id":"https://schemas.test/root.json","allOf":[{"$ref":"{{{{{reference}}}}}"}],
             "definitions":{"A":{"$id":"#foo","type":"string"},
               "B":{"$id":"other.json","definitions":{"X":{"$id":"#bar","type":"string"},
                 "Y":{"$id":"t/inner.json","type":"integer"}}},
               "C":{"$id":"urn:example:leaf","type":"boolean"}}}
            """);
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", schema, new()
        {
            MaxReferences = 0,
            ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task DraftSevenNamedFragmentsKeepContainingBaseForNestedReferences()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", """
            {"$id":"https://schemas.test/root","allOf":[{"$ref":"#node"},{"$ref":"nested/#node"}],
             "definitions":{"Root":{"$id":"#node","properties":{"value":{"$ref":"part"}}},
              "Nested":{"$id":"nested/","definitions":{"Value":{"$id":"#node","properties":{"value":{"$ref":"part"}}}}}}}
            """u8.ToArray(), new()
            {
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>( """{"type":"string"}"""u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/part", "https://schemas.test/nested/part"])).IsTrue();
    }

    [Test]
    [Arguments("#a")]
    [Arguments("#Z0-._:z")]
    public async Task DraftSevenRootFragmentIdentifiersNameTheCapturedRoot(string identifier)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes($$$"""
                {"$id":"{{{identifier}}}","allOf":[{"$ref":"{{{identifier}}}"}]}
                """), new() { DocumentUri = new("https://schemas.test/root"), MaxReferences = 0 });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("#_node", DocumentValidationStatus.Invalid, "schema.anchor")]
    [Arguments("#0node", DocumentValidationStatus.Invalid, "schema.anchor")]
    [Arguments("#a%3Ab", DocumentValidationStatus.Invalid, "schema.anchor")]
    [Arguments("#/definitions/X", DocumentValidationStatus.Unsupported, "schema.id_fragment_unsupported")]
    [Arguments("https://schemas.test/root#node", DocumentValidationStatus.Unsupported, "schema.id_fragment_unsupported")]
    public async Task DraftSevenFragmentIdentifierBoundariesAreExplicit(
        string identifier, DocumentValidationStatus status, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes($$$$"""
                {"$id":"https://schemas.test/root","definitions":{"Value":{"$id":"{{{{identifier}}}}"}}}
                """));
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/definitions/Value/$id");
    }

    [Test]
    [Arguments("""{"definitions":{"A":{"$id":"#node"},"B":{"$id":"#node"}}}""", "schema.duplicate_anchor")]
    [Arguments("""{"definitions":{"A":{"$id":"child"},"B":{"$id":"child#"}}}""", "schema.duplicate_id")]
    public async Task DraftSevenIdentifierCollisionsRejectAmbiguousSchemas(string schema, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", Encoding.UTF8.GetBytes(schema),
            new() { DocumentUri = new("https://schemas.test/root") });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
    }

    [Test]
    public async Task DraftSevenReferenceObjectsNeverRegisterIgnoredIdentifiers()
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", """
            {"$id":"https://schemas.test/root","definitions":{"Ignored":{"$ref":"#/definitions/Real","$id":"#node"},
             "Real":{"type":"string"}},"allOf":[{"$ref":"#node"}]}
            """u8.ToArray(), new() { MaxReferences = 0 });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("reference.not_found");
    }

    [Test]
    [Arguments("child#node")]
    [Arguments("child#%6Eode")]
    [Arguments("child#/properties/value")]
    [Arguments("#/$defs/Child/properties/value")]
    public async Task EmbeddedResourcesResolveCanonicalAndContainingPointersWithoutAcquisition(string reference)
    {
        foreach (var format in new[] { "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12" })
        {
            var schema = RegistryJson.Create(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("$id", "https://schemas.test/root");
                writer.WriteString("$ref", reference); writer.WriteStartObject("$defs"); writer.WriteStartObject("Child");
                writer.WriteString("$id", "child#"); writer.WriteStartObject("properties"); writer.WriteStartObject("value");
                writer.WriteString("$anchor", "node"); writer.WriteString("type", "string");
                writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
            });
            var calls = 0;
            var result = await new BuiltInDocumentValidator().ValidateAsync(format,
                Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()), new()
                {
                    MaxReferences = 0,
                    ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
                });
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            await Assert.That(calls).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task EmbeddedReferenceBasesAndAnchorNamespacesRemainResourceLocal(string format)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, """
            {"$id":"https://schemas.test/root","$anchor":"node",
             "$ref":"left/#node","$defs":{
               "Left":{"$id":"left/","$anchor":"node","$ref":"#/$defs/Value",
                 "$defs":{"Value":{"$ref":"part"}}},
               "Right":{"$id":"right/","$anchor":"node","$ref":"part"}}}
            """u8.ToArray(), new()
            {
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>("""{"type":"string"}"""u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/left/part", "https://schemas.test/right/part"])).IsTrue();
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task NestedEmbeddedIdsResolveAgainstParentsWithoutLeakingToSiblings(string format)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, """
            {"$id":"https://schemas.test/root","$defs":{
               "Parent":{"$id":"nested/","$defs":{"Child":{"$id":"child/","$ref":"part"}}},
               "Sibling":{"$ref":"part"}}}
            """u8.ToArray(), new()
            {
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>("""{"type":"string"}"""u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/nested/child/part", "https://schemas.test/part"])).IsTrue();
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task EmbeddedCircularAndForwardReferencesReuseCapturedResources(string format)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, """
            {"$id":"https://schemas.test/root","$ref":"first","$defs":{
               "First":{"$id":"first","$ref":"second"},
               "Second":{"$id":"second","$ref":"first"}}}
            """u8.ToArray(), new() { MaxReferences = 0 });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task EscapedContainingPointersRetainTheirNearestEmbeddedScope(string format)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, """
            {"$id":"https://schemas.test/root","$ref":"#/$defs/A~1B/properties/value","$defs":{
              "A/B":{"$id":"nested/","properties":{"value":{"$ref":"part"}}}},
             "x-custom":{"$id":"https://schemas.test/root","$anchor":"duplicate","$ref":"never"}}
            """u8.ToArray(), new()
            {
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>( """{"type":"string"}"""u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/nested/part"])).IsTrue();
    }

    [Test]
    [Arguments("""{"$id":"https://schemas.test/root","$defs":{"A":{"$id":"same"},"B":{"$id":"same#"}}}""",
        "schema.duplicate_id")]
    [Arguments("""{"$id":"https://schemas.test/root","$defs":{"A":{"$id":"#"}}}""", "schema.duplicate_id")]
    [Arguments("""{"$id":"https://schemas.test/root","$defs":{"A":{"$id":"child","$defs":{"X":{"$anchor":"node"},"Y":{"$anchor":"node"}}}}}""",
        "schema.duplicate_anchor")]
    [Arguments("""{"$id":"https://schemas.test/root","$ref":"#node","$defs":{"A":{"$id":"child","$anchor":"node"}}}""",
        "reference.not_found")]
    [Arguments("""{"$id":"https://schemas.test/root","$ref":"child#/missing","$defs":{"A":{"$id":"child","type":"string"}}}""",
        "reference.not_found")]
    [Arguments("""{"$id":"https://schemas.test/root","$ref":"child#/title","$defs":{"A":{"$id":"child","title":"annotation"}}}""",
        "schema.object")]
    [Arguments("""{"$id":"https://schemas.test/root","$defs":{"A":{"$id":"child#node"}}}""", "schema.id_fragment")]
    public async Task EmbeddedResourceIdentityAndTargetFailuresRemainExplicit(string schema, string code)
    {
        foreach (var format in new[] { "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema),
                new() { MaxReferences = 0 });
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
        }
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task AcquiredDocumentsRegisterEmbeddedAliasesForLaterReferences(string format)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, """
            {"allOf":[{"$ref":"https://schemas.test/container"},
                      {"$ref":"https://schemas.test/child#node"}]}
            """u8.ToArray(), new()
            {
                MaxReferences = 1,
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>( """
                        {"$id":"https://schemas.test/container","$defs":{
                          "Child":{"$id":"child","$anchor":"node","type":"string"}}}
                        """u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/container"])).IsTrue();
    }

    [Test]
    [Arguments(-1, DocumentValidationStatus.Indeterminate)]
    [Arguments(0, DocumentValidationStatus.Valid)]
    [Arguments(1, DocumentValidationStatus.Valid)]
    public async Task EmbeddedResourceRegistrationHasAnExactInclusiveWorkBudget(int delta, DocumentValidationStatus status)
    {
        var bytes = """{"$id":"https://schemas.test/root","$defs":{"Value":{"$id":"child","type":"string"}}}"""u8.ToArray();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12", bytes,
            new() { MaxWork = bytes.Length + 2 + "https://schemas.test/child".Length + delta });
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("limit.work"); }
    }

    [Test]
    [Arguments(-1, DocumentValidationStatus.Indeterminate)]
    [Arguments(0, DocumentValidationStatus.Valid)]
    [Arguments(1, DocumentValidationStatus.Valid)]
    public async Task EmbeddedResourcesChargeCapturedDocumentBytesOnlyOnce(int delta, DocumentValidationStatus status)
    {
        var bytes = """{"$id":"https://schemas.test/root","$defs":{"Value":{"$id":"child","type":"string"}}}"""u8.ToArray();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12", bytes,
            new() { MaxTotalBytes = bytes.Length + delta });
        await Assert.That(result.Status).IsEqualTo(status);
        if (status == DocumentValidationStatus.Valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("limit.total_bytes"); }
    }

    [Test]
    [Arguments(5, 64, DocumentValidationStatus.Indeterminate, "limit.nodes")]
    [Arguments(6, 64, DocumentValidationStatus.Valid, "")]
    [Arguments(7, 64, DocumentValidationStatus.Valid, "")]
    [Arguments(100, 3, DocumentValidationStatus.Indeterminate, "limit.depth")]
    [Arguments(100, 4, DocumentValidationStatus.Valid, "")]
    [Arguments(100, 5, DocumentValidationStatus.Valid, "")]
    public async Task EmbeddedResourcesRemainWithinWholeDocumentStructuralBudgets(
        int nodes, int depth, DocumentValidationStatus status, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12",
            """{"$id":"https://schemas.test/root","$defs":{"Value":{"$id":"child","type":"string"}}}"""u8.ToArray(),
            new() { MaxNodes = nodes, MaxDepth = depth });
        await Assert.That(result.Status).IsEqualTo(status);
        if (code.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code); }
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", "https://json-schema.org/draft/2019-09/schema")]
    [Arguments("JsonSchema/draft/2020-12", "https://json-schema.org/draft/2020-12/schema")]
    public async Task SameDialectDeclarationsAreAllowedAtEmbeddedResourceRoots(string format, string dialect)
    {
        var schema = Encoding.UTF8.GetBytes($$$$$"""
            {"$id":"https://schemas.test/root","$defs":{"Child":{"$schema":"{{{{{dialect}}}}}","$id":"child","type":"string"}}}
            """);
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, schema);
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2020-12", """{"$id":"child","$schema":"http://json-schema.org/draft-07/schema#"}""",
        "schema.dialect_unsupported")]
    [Arguments("JsonSchema/draft-07", """{"$id":"child#node"}""", "schema.id_fragment_unsupported")]
    public async Task UnqualifiedEmbeddedDialectsRemainUnsupported(string format, string child, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            Encoding.UTF8.GetBytes($$$$"""
                {"$id":"https://schemas.test/root","properties":{"value":{{{{child}}}}}}
                """));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("https://schemas.test/canonical", "https://schemas.test/canonical")]
    [Arguments("https://schemas.test/canonical#", "https://schemas.test/canonical")]
    [Arguments("canonical#", "https://schemas.test/canonical")]
    [Arguments("#", "https://schemas.test/retrieval")]
    public async Task ModernCanonicalRootIdsReuseTheSuppliedDocument(string id, string reference)
    {
        foreach (var format in new[] { "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12" })
        {
            var calls = 0;
            var schema = RegistryJson.Create(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("$id", id); writer.WriteString("$ref", reference + "#node");
                writer.WriteStartObject("$defs"); writer.WriteStartObject("target");
                writer.WriteString("$anchor", "node"); writer.WriteString("type", "string");
                writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
            });
            var result = await new BuiltInDocumentValidator().ValidateAsync(format,
                Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()), new()
                {
                    DocumentUri = new("https://schemas.test/retrieval#captured"),
                    MaxReferences = 0,
                    ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
                });
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            await Assert.That(calls).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("relative", "schema.id_absolute")]
    [Arguments("", "schema.id_absolute")]
    [Arguments("#", "schema.id_absolute")]
    [Arguments("https://schemas.test/root#node", "schema.id_fragment")]
    [Arguments("https://schemas.test/root##", "schema.uri")]
    [Arguments("https://schemas.test/%GG", "schema.uri")]
    public async Task InvalidModernRootIdsRejectBeforeReferenceAcquisition(string id, string code)
    {
        foreach (var format in new[] { "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12" })
        {
            var calls = 0;
            var schema = RegistryJson.Create(writer =>
            {
                writer.WriteStartObject(); writer.WriteString("$id", id);
                writer.WriteString("$ref", "https://never.test/"); writer.WriteEndObject();
            });
            var result = await new BuiltInDocumentValidator().ValidateAsync(format,
                Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()), new()
                {
                    ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
                });
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
            await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/$id");
            await Assert.That(calls).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task RelativeCanonicalIdsResolveDependenciesAgainstTheNewBaseExactlyOnce(string format)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            """{"$id":"nested/root#","$ref":"part#node"}"""u8.ToArray(), new()
            {
                DocumentUri = new("https://schemas.test/retrieval"),
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        """{"$id":"https://schemas.test/part#","$anchor":"node","type":"string"}"""u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/nested/part"])).IsTrue();
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task DistinctDocumentsCannotDeclareTheSameCanonicalIdWithAnEmptyFragment(string format)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, """
            {"allOf":[{"$ref":"https://schemas.test/first"},{"$ref":"https://schemas.test/second"}]}
            """u8.ToArray(), new()
            {
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(reference.EndsWith("first", StringComparison.Ordinal)
                        ? """{"$id":"https://schemas.test/shared","type":"string"}"""u8.ToArray()
                        : """{"$id":"https://schemas.test/shared#","type":"integer"}"""u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.duplicate_id");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/$id");
        await Assert.That(calls.SequenceEqual(["https://schemas.test/first", "https://schemas.test/second"])).IsTrue();
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", true)]
    [Arguments("JsonSchema/draft/2019-09", false)]
    [Arguments("JsonSchema/draft/2020-12", true)]
    [Arguments("JsonSchema/draft/2020-12", false)]
    public async Task OrdinarySchemasDoNotActivateDeclaredVocabularySupport(string format, bool required)
    {
        var calls = 0;
        var schema = Encoding.UTF8.GetBytes($$$"""
            {"$vocabulary":{"https://unknown.test/vocabulary":{{{required.ToString().ToLowerInvariant()}}}},
             "type":"string","x-custom":{"$ref":"https://never.test/"}}
            """);
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, schema, new()
        {
            ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", "https://json-schema.org/draft/2019-09/schema")]
    [Arguments("JsonSchema/draft/2020-12", "https://json-schema.org/draft/2020-12/schema")]
    public async Task ReferencedOrdinarySchemasKeepVocabularyDeclarationsInactive(string format, string dialect)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            """{"$ref":"https://schemas.test/target"}"""u8.ToArray(), new()
            {
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes($$$"""
                        {"$schema":"{{{dialect}}}","$vocabulary":{"https://unknown.test/vocabulary":true},"type":"string"}
                        """));
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/target"])).IsTrue();
    }

    [Test]
    [Arguments("""{"$vocabulary":{"relative":true}}""", "schema.uri")]
    [Arguments("""{"$vocabulary":{"https://unknown.test/%GG":true}}""", "schema.uri")]
    [Arguments("""{"$vocabulary":{"https://unknown.test/vocabulary":null}}""", "schema.boolean")]
    [Arguments("""{"$vocabulary":{"https://unknown.test/vocabulary":true},"type":"invalid"}""", "schema.type")]
    public async Task IgnoredVocabularySupportDoesNotDisableSchemaSyntaxChecks(string schema, string code)
    {
        foreach (var format in new[] { "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
        }
    }

    [Test]
    [Arguments("JsonSchema/draft-07", "http://json-schema.org/draft-07/schema#")]
    [Arguments("JsonSchema/draft/2019-09", "https://json-schema.org/draft/2019-09/schema")]
    [Arguments("JsonSchema/draft/2020-12", "https://json-schema.org/draft/2020-12/schema")]
    public async Task DialectDeclarationsRejectNonResourceSubschemas(string format, string dialect)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes($$$$"""
            {"properties":{"value":{"$schema":"{{{{dialect}}}}","type":"string"}}}
            """));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.dialect_location");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/properties/value/$schema");
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task VocabularyDeclarationsRejectSubschemas(string format)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            """{"properties":{"value":{"$vocabulary":{},"type":"string"}}}"""u8.ToArray());
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.vocabulary_location");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/properties/value/$vocabulary");
    }

    [Test]
    [Arguments("relative")]
    [Arguments("https://example.test/%GG")]
    [Arguments("https://json-schema.org/draft/2020-12/schema##")]
    public async Task MalformedDialectUrisAreInvalidRatherThanUnsupported(string dialect)
    {
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("$schema", dialect); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12",
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.uri");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/$schema");
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", "a", true)]
    [Arguments("JsonSchema/draft/2019-09", "A0-._:z", true)]
    [Arguments("JsonSchema/draft/2019-09", "_node", false)]
    [Arguments("JsonSchema/draft/2019-09", ":node", false)]
    [Arguments("JsonSchema/draft/2020-12", "_", true)]
    [Arguments("JsonSchema/draft/2020-12", "_A0-._z", true)]
    [Arguments("JsonSchema/draft/2020-12", "a:z", false)]
    [Arguments("JsonSchema/draft/2020-12", "0node", false)]
    [Arguments("JsonSchema/draft/2019-09", "", false)]
    [Arguments("JsonSchema/draft/2020-12", "", false)]
    [Arguments("JsonSchema/draft/2019-09", "caf\u00e9", false)]
    [Arguments("JsonSchema/draft/2020-12", "caf\u00e9", false)]
    [Arguments("JsonSchema/draft/2019-09", "a/b", false)]
    [Arguments("JsonSchema/draft/2020-12", "a/b", false)]
    [Arguments("JsonSchema/draft/2019-09", "a b", false)]
    [Arguments("JsonSchema/draft/2020-12", "a b", false)]
    public async Task AnchorGrammarFollowsTheSelectedCoreMetaSchema(string format, string anchor, bool valid)
    {
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("$anchor", anchor); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()));
        await Assert.That(result.Status).IsEqualTo(valid ? DocumentValidationStatus.Valid : DocumentValidationStatus.Invalid);
        if (valid) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else
        {
            await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.anchor");
            await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/$anchor");
        }
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", "a:z", "#a:z")]
    [Arguments("JsonSchema/draft/2019-09", "a:z", "#a%3Az")]
    [Arguments("JsonSchema/draft/2020-12", "_node", "#_node")]
    [Arguments("JsonSchema/draft/2020-12", "_node", "#%5Fnode")]
    public async Task DialectSpecificAnchorsResolveWithoutExternalAcquisition(string format, string anchor, string reference)
    {
        var calls = 0;
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("$ref", reference);
            writer.WriteStartObject("$defs"); writer.WriteStartObject("target");
            writer.WriteString("$anchor", anchor); writer.WriteString("type", "string");
            writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()), new()
            {
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", "$recursiveRef", "part%GG", "schema.uri")]
    [Arguments("JsonSchema/draft/2019-09", "$recursiveRef", "part\\name", "schema.uri")]
    [Arguments("JsonSchema/draft/2020-12", "$recursiveRef", "part%GG", "schema.uri")]
    [Arguments("JsonSchema/draft/2020-12", "$dynamicRef", "part#node#other", "schema.uri")]
    [Arguments("JsonSchema/draft/2020-12", "$dynamicRef", "part name", "schema.uri")]
    [Arguments("JsonSchema/draft/2020-12", "$dynamicAnchor", "node:part", "schema.anchor")]
    [Arguments("JsonSchema/draft/2020-12", "$dynamicAnchor", "", "schema.anchor")]
    [Arguments("JsonSchema/draft/2020-12", "$recursiveAnchor", "node:part", "schema.anchor")]
    public async Task MalformedDynamicKeywordsRejectBeforeUnsupportedSemantics(
        string format, string keyword, string value, string code)
    {
        var calls = 0;
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteString(keyword, value); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()), new()
            {
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/" + keyword);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", """{"$recursiveAnchor":"node"}""", DocumentValidationStatus.Invalid, "schema.boolean")]
    [Arguments("JsonSchema/draft/2020-12", """{"$recursiveAnchor":true}""", DocumentValidationStatus.Invalid, "schema.string")]
    [Arguments("JsonSchema/draft/2020-12", """{"$recursiveAnchor":false}""", DocumentValidationStatus.Invalid, "schema.string")]
    [Arguments("JsonSchema/draft/2020-12", """{"$recursiveAnchor":"_node"}""", DocumentValidationStatus.Unsupported, "schema.dynamic_reference")]
    [Arguments("JsonSchema/draft/2020-12", """{"$dynamicAnchor":"_node"}""", DocumentValidationStatus.Unsupported, "schema.dynamic_reference")]
    [Arguments("JsonSchema/draft/2019-09", """{"$recursiveRef":"part#node"}""", DocumentValidationStatus.Unsupported, "schema.dynamic_reference")]
    [Arguments("JsonSchema/draft/2020-12", """{"$dynamicRef":"part#node"}""", DocumentValidationStatus.Unsupported, "schema.dynamic_reference")]
    public async Task RecursiveAndDynamicKeywordsRetainDialectSpecificShapeAndUnsupportedBoundaries(
        string format, string schema, DocumentValidationStatus status, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(status);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
    }

    [Test]
    [Arguments("JsonSchema/draft-07", "$defs")]
    [Arguments("JsonSchema/draft-07", "$anchor")]
    [Arguments("JsonSchema/draft-07", "$vocabulary")]
    [Arguments("JsonSchema/draft-07", "$dynamicRef")]
    [Arguments("JsonSchema/draft-07", "$dynamicAnchor")]
    [Arguments("JsonSchema/draft-07", "$recursiveRef")]
    [Arguments("JsonSchema/draft-07", "$recursiveAnchor")]
    [Arguments("JsonSchema/draft-07", "dependentSchemas")]
    [Arguments("JsonSchema/draft-07", "dependentRequired")]
    [Arguments("JsonSchema/draft-07", "minContains")]
    [Arguments("JsonSchema/draft-07", "maxContains")]
    [Arguments("JsonSchema/draft-07", "unevaluatedItems")]
    [Arguments("JsonSchema/draft-07", "unevaluatedProperties")]
    [Arguments("JsonSchema/draft-07", "contentSchema")]
    [Arguments("JsonSchema/draft-07", "prefixItems")]
    [Arguments("JsonSchema/draft-07", "deprecated")]
    [Arguments("JsonSchema/draft/2019-09", "$dynamicRef")]
    [Arguments("JsonSchema/draft/2019-09", "$dynamicAnchor")]
    [Arguments("JsonSchema/draft/2019-09", "prefixItems")]
    [Arguments("JsonSchema/draft/2020-12", "additionalItems")]
    public async Task KeywordsAbsentFromSelectedDialectDoNotValidateTheirAnnotationValues(string format, string keyword)
    {
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteNull(keyword); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", "$defs", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "$anchor", "schema.string")]
    [Arguments("JsonSchema/draft/2019-09", "$vocabulary", "schema.object")]
    [Arguments("JsonSchema/draft/2020-12", "$dynamicRef", "schema.string")]
    [Arguments("JsonSchema/draft/2020-12", "$dynamicAnchor", "schema.string")]
    [Arguments("JsonSchema/draft/2019-09", "$recursiveRef", "schema.string")]
    [Arguments("JsonSchema/draft/2019-09", "$recursiveAnchor", "schema.boolean")]
    [Arguments("JsonSchema/draft/2019-09", "dependentSchemas", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "dependentRequired", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "minContains", "schema.number")]
    [Arguments("JsonSchema/draft/2019-09", "maxContains", "schema.number")]
    [Arguments("JsonSchema/draft/2019-09", "unevaluatedItems", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "unevaluatedProperties", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "contentSchema", "schema.object")]
    [Arguments("JsonSchema/draft/2020-12", "prefixItems", "schema.array")]
    [Arguments("JsonSchema/draft/2019-09", "deprecated", "schema.boolean")]
    [Arguments("JsonSchema/draft-07", "additionalItems", "schema.object")]
    [Arguments("JsonSchema/draft/2019-09", "additionalItems", "schema.object")]
    public async Task SelectedDialectStillValidatesRecognizedKeywordShapes(string format, string keyword, string code)
    {
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteNull(keyword); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code);
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/" + keyword);
    }

    [Test]
    [Arguments("JsonSchema/draft-07", """{"$defs":{"ignored":{"type":"invalid","$ref":"never"}}}""")]
    [Arguments("JsonSchema/draft-07", """{"dependentSchemas":{"ignored":{"$ref":"never"}}}""")]
    [Arguments("JsonSchema/draft-07", """{"contentSchema":{"$ref":"never"},"unevaluatedItems":{"$ref":"never"},"unevaluatedProperties":{"$ref":"never"}}""")]
    [Arguments("JsonSchema/draft-07", """{"$dynamicRef":"never","$recursiveRef":"never"}""")]
    [Arguments("JsonSchema/draft/2019-09", """{"prefixItems":[{"$anchor":"ignored","$ref":"never"}],"$dynamicRef":"never"}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"additionalItems":{"$anchor":"ignored","$ref":"never"}}""")]
    public async Task UnknownDialectSubschemasNeverAcquireDependencies(string format, string schema)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema), new()
        {
            MaxReferences = 0,
            ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
        });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(4, 64, DocumentValidationStatus.Indeterminate, "limit.nodes")]
    [Arguments(5, 64, DocumentValidationStatus.Valid, "")]
    [Arguments(6, 64, DocumentValidationStatus.Valid, "")]
    [Arguments(100, 3, DocumentValidationStatus.Indeterminate, "limit.depth")]
    [Arguments(100, 4, DocumentValidationStatus.Valid, "")]
    [Arguments(100, 5, DocumentValidationStatus.Valid, "")]
    public async Task UnknownDialectContentStillConsumesWholeDocumentBudgets(
        int nodes, int depth, DocumentValidationStatus status, string code)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07",
            """{"$defs":{"ignored":{"type":"invalid","$ref":"never"}}}"""u8.ToArray(), new()
            {
                MaxNodes = nodes,
                MaxDepth = depth,
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(status);
        if (code.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code); }
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", """{"prefixItems":[{"$anchor":"ignored"}],"$ref":"#ignored"}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"additionalItems":{"$anchor":"ignored"},"$ref":"#ignored"}""")]
    public async Task AnchorsInsideUnknownDialectKeywordsAreNotRegistered(string format, string schema)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("reference.not_found");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/$ref");
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09", """{"$recursiveRef":"#"}""")]
    [Arguments("JsonSchema/draft/2019-09", """{"$recursiveAnchor":true}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"$dynamicRef":"#node"}""")]
    [Arguments("JsonSchema/draft/2020-12", """{"$dynamicAnchor":"node"}""")]
    public async Task RecognizedDynamicKeywordsRemainExplicitlyUnsupported(string format, string schema)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Unsupported);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.dynamic_reference");
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task DeprecatedReservedDefinitionsAndDependenciesRetainMetaSchemaChecks(string format)
    {
        foreach (var schema in new[] { """{"definitions":null}""", """{"dependencies":null}""" })
        {
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes(schema));
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
            await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.object");
        }
    }

    [Test]
    [Arguments("""{"$ref":"target","type":"invalid","$id":42,"pattern":"[","$schema":false}""")]
    [Arguments("""{"$id":"https://wrong.test/","$ref":"target","properties":{"ignored":{"$ref":"never"}}}""")]
    [Arguments("""{"$ref":"target","$defs":{"ignored":{"$dynamicRef":"#never"}},"required":42}""")]
    public async Task DraftSevenReferenceObjectsIgnoreSiblingsWithoutAcquiringTheirReferences(string schema)
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes(schema), new()
            {
                DocumentUri = new("https://schemas.test/root"),
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(reference == "https://schemas.test/target"
                        ? """{"type":"string"}"""u8.ToArray() : null);
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/target"])).IsTrue();
    }

    [Test]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("JsonSchema/draft/2020-12")]
    public async Task ModernReferenceObjectsContinueToValidateSiblingKeywords(string format)
    {
        var calls = 0;
        var result = await new BuiltInDocumentValidator().ValidateAsync(format,
            """{"$ref":"https://schemas.test/target","type":"invalid"}"""u8.ToArray(), new()
            {
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.type");
        await Assert.That(result.Diagnostics.Single().Path).IsEqualTo("$/type");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"$ref":"#/definitions/target","definitions":{"target":{"type":"string"},"ignored":{"type":"invalid"}}}""",
        DocumentValidationStatus.Valid, "")]
    [Arguments("""{"$ref":"#/definitions/target","definitions":{"target":{"type":"invalid"}}}""",
        DocumentValidationStatus.Invalid, "schema.type")]
    [Arguments("""{"$ref":42,"type":"string"}""", DocumentValidationStatus.Invalid, "schema.string")]
    [Arguments("""{"$ref":"missing","type":"string"}""", DocumentValidationStatus.Indeterminate, "reference.unresolved")]
    [Arguments("""{"$ref":"#","type":"invalid"}""", DocumentValidationStatus.Valid, "")]
    public async Task DraftSevenReferenceTargetsRemainCheckedEvenWhenContainingSiblingsAreIgnored(
        string schema, DocumentValidationStatus status, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft-07", Encoding.UTF8.GetBytes(schema));
        await Assert.That(result.Status).IsEqualTo(status);
        if (code.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code); }
    }

    [Test]
    [Arguments("#node")]
    [Arguments("#%6Eode")]
    [Arguments("#n%6Fde")]
    public async Task EncodedAnchorReferencesDecodeOnceBeforeChoosingTheirReferenceKind(string reference)
    {
        foreach (var format in new[] { "JsonSchema/draft/2019-09", "JsonSchema/draft/2020-12" })
        {
            var calls = 0;
            var result = await new BuiltInDocumentValidator().ValidateAsync(format, Encoding.UTF8.GetBytes($$$$"""
                {"$ref":"{{{{reference}}}}","$defs":{"target":{"$anchor":"node","type":"string"}}}
                """), new()
                {
                    ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
                });
            await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
            await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
            await Assert.That(calls).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("#%2F$defs%2Ftarget", DocumentValidationStatus.Valid, "")]
    [Arguments("#/$defs/a%252Fb", DocumentValidationStatus.Valid, "")]
    [Arguments("#%256Eode", DocumentValidationStatus.Invalid, "reference.not_found")]
    [Arguments("#%C0%AF", DocumentValidationStatus.Invalid, "reference.pointer")]
    [Arguments("#%FF", DocumentValidationStatus.Invalid, "reference.pointer")]
    [Arguments("#missing", DocumentValidationStatus.Invalid, "reference.not_found")]
    public async Task ReferenceFragmentDecodingNeverDoubleDecodesOrAcceptsInvalidUtf8(
        string reference, DocumentValidationStatus status, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12",
            Encoding.UTF8.GetBytes($$$"""
                {"$ref":"{{{reference}}}","$defs":{"target":{"$anchor":"node"},"a%2Fb":true}}
                """));
        await Assert.That(result.Status).IsEqualTo(status);
        if (code.Length == 0) { await Assert.That(result.Diagnostics.Count).IsEqualTo(0); }
        else { await Assert.That(result.Diagnostics.Single().Code).IsEqualTo(code); }
    }

    [Test]
    [Arguments("https://schemas.test/part#%GG")]
    [Arguments("part%GG")]
    [Arguments("part\\name")]
    [Arguments("part#node#other")]
    public async Task MalformedSchemaReferencesRejectBeforeCallbackNormalization(string reference)
    {
        var calls = 0;
        var schema = RegistryJson.Create(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("$ref", reference); writer.WriteEndObject();
        });
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12",
            Encoding.UTF8.GetBytes(schema.RootElement.GetRawText()), new()
            {
                ResolveReference = (_, _) => { calls++; throw new UnauthorizedAccessException(); }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(result.Diagnostics.Single().Code).IsEqualTo("schema.uri");
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task ExternalEncodedAnchorReferencesKeepFragmentFreeSharedAcquisition()
    {
        var calls = new List<string>();
        var result = await new BuiltInDocumentValidator().ValidateAsync("JsonSchema/draft/2020-12", """
            {"allOf":[{"$ref":"part#%6Eode"},{"$ref":"part#node"},{"$ref":"part#%2F$defs%2Ftarget"}]}
            """u8.ToArray(), new()
            {
                DocumentUri = new("https://schemas.test/root"),
                MaxReferences = 1,
                ResolveReference = (reference, _) =>
                {
                    calls.Add(reference);
                    return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                        """{"$defs":{"target":{"$anchor":"node","type":"string"}}}"""u8.ToArray());
                }
            });
        await Assert.That(result.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(calls.SequenceEqual(["https://schemas.test/part"])).IsTrue();
    }

    [Test]
    [Arguments("""{"type":"string","minLength":2.0,"maxLength":10,"pattern":"^[a-z]+$"}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"type":["null","string"],"anyOf":[true,{"const":"x"}]}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"type":["string","string"]}""", DocumentValidationStatus.Invalid, "schema.duplicate")]
    [Arguments("""{"type":42}""", DocumentValidationStatus.Invalid, "schema.string")]
    [Arguments("""{"minimum":"0"}""", DocumentValidationStatus.Invalid, "schema.number")]
    [Arguments("""{"multipleOf":0}""", DocumentValidationStatus.Invalid, "schema.positive")]
    [Arguments("""{"minItems":-1}""", DocumentValidationStatus.Invalid, "schema.nonnegative_integer")]
    [Arguments("""{"minItems":1.5}""", DocumentValidationStatus.Invalid, "schema.nonnegative_integer")]
    [Arguments("""{"minItems":1e40}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"required":["id","id"]}""", DocumentValidationStatus.Invalid, "schema.duplicate")]
    [Arguments("""{"allOf":[]}""", DocumentValidationStatus.Invalid, "schema.empty")]
    [Arguments("""{"items":[{"type":"string"}]}""", DocumentValidationStatus.Invalid, "schema.object")]
    [Arguments("""{"prefixItems":[{"type":"string"}],"items":false}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"$vocabulary":{"https://example.test/vocabulary":true}}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"$vocabulary":{"https://example.test/vocabulary":"true"}}""", DocumentValidationStatus.Invalid, "schema.boolean")]
    [Arguments("""{"$schema":"http://json-schema.org/draft-07/schema#"}""", DocumentValidationStatus.Invalid, "schema.dialect_mismatch")]
    [Arguments("""{"$schema":"https://example.test/unknown"}""", DocumentValidationStatus.Unsupported, "schema.dialect_unsupported")]
    [Arguments("""{"$dynamicRef":"#node"}""", DocumentValidationStatus.Unsupported, "schema.dynamic_reference")]
    [Arguments("""{"pattern":"["}""", DocumentValidationStatus.Invalid, "schema.pattern")]
    [Arguments("""{"pattern":"(?<=x)y"}""", DocumentValidationStatus.Unsupported, "schema.pattern_unsupported")]
    [Arguments("""{"type":"object","unevaluatedProperties":false,"dependentRequired":{"a":["b"]}}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"examples":42}""", DocumentValidationStatus.Invalid, "schema.array")]
    [Arguments("""{"x-note":{"arbitrary":"annotation"}}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"enum":[]}""", DocumentValidationStatus.Valid, "")]
    [Arguments("""{"type":"string","type":"number"}""", DocumentValidationStatus.Invalid, "json.duplicate_property")]
    [Arguments("""{"type":""", DocumentValidationStatus.Invalid, "json.syntax")]
    public async Task MetaVocabularyChecksAreNotJustJsonParsing(string schema, DocumentValidationStatus expected, string code)
    {
        var result = await new BuiltInDocumentValidator().ValidateAsync(
            "JsonSchema/draft/2020-12", Encoding.UTF8.GetBytes(schema));

        await Assert.That(result.Status).IsEqualTo(expected);
        if (code.Length > 0)
        {
            await Assert.That(result.Diagnostics[0].Code).IsEqualTo(code);
        }
    }

    [Test]
    public async Task DraftSevenTupleItemsAndEnumRulesDifferFrom2020()
    {
        var validator = new BuiltInDocumentValidator();
        var tuple = await validator.ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes("""{"items":[{"type":"string"}],"additionalItems":false}"""));
        var emptyEnum = await validator.ValidateAsync("JsonSchema/draft-07", Encoding.UTF8.GetBytes("""{"enum":[]}"""));
        var duplicateEnum = await validator.ValidateAsync("JsonSchema/draft-07", Encoding.UTF8.GetBytes("""{"enum":[1,1.0]}"""));

        await Assert.That(tuple.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(emptyEnum.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(duplicateEnum.Status).IsEqualTo(DocumentValidationStatus.Invalid);
    }

    [Test]
    public async Task ReferencesUseOnlyExplicitDocumentsAndResolveRealTargets()
    {
        var validator = new BuiltInDocumentValidator();
        var schema = Encoding.UTF8.GetBytes("""{"$ref":"https://schemas.test/age#/$defs/age"}""");
        var unresolved = await validator.ValidateAsync("JsonSchema/draft/2020-12", schema);
        var resolved = await validator.ValidateAsync("JsonSchema/draft/2020-12", schema,
            new DocumentValidationOptions
            {
                ResolveReference = (reference, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    reference == "https://schemas.test/age"
                        ? Encoding.UTF8.GetBytes("""{"$defs":{"age":{"type":"integer"}}}""")
                        : null),
            });
        var missing = await validator.ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes("""{"$ref":"#/definitions/missing","definitions":{"present":{"type":"string"}}}"""));
        var recursive = await validator.ValidateAsync("JsonSchema/draft-07",
            Encoding.UTF8.GetBytes("""{"type":"object","properties":{"next":{"$ref":"#"}}}"""));

        await Assert.That(unresolved.Status).IsEqualTo(DocumentValidationStatus.Indeterminate);
        await Assert.That(unresolved.Diagnostics[0].Code).IsEqualTo("reference.unresolved");
        await Assert.That(resolved.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(missing.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(missing.Diagnostics[0].Code).IsEqualTo("reference.not_found");
        await Assert.That(recursive.Status).IsEqualTo(DocumentValidationStatus.Valid);
    }

    [Test]
    [Arguments("JsonSchema/draft-07")]
    [Arguments("JsonSchema/draft/2019-09")]
    [Arguments("jSoNsChEmA/dRaFt/2020-12")]
    public async Task DeclaredObjectAndPropertyTypesAreChecked(string format)
    {
        var validator = new BuiltInDocumentValidator();
        var valid = await validator.ValidateAsync(format,
            Encoding.UTF8.GetBytes("""{"type":"object","properties":{"age":{"type":"integer","minimum":0}},"required":["age"]}"""));
        var invalid = await validator.ValidateAsync(format,
            Encoding.UTF8.GetBytes("""{"type":"object","properties":{"age":{"type":"int32"}}}"""));

        await Assert.That(valid.Status).IsEqualTo(DocumentValidationStatus.Valid);
        await Assert.That(invalid.Status).IsEqualTo(DocumentValidationStatus.Invalid);
        await Assert.That(invalid.Diagnostics[0].Path).IsEqualTo("$/properties/age/type");
        await Assert.That(invalid.Diagnostics[0].Code).IsEqualTo("schema.type");
    }
}
