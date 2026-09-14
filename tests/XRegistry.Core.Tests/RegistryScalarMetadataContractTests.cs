using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryScalarMetadataContractTests
{
    private static readonly RegistryModel s_model = RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false}}}}}
        """));

    [Test]
    [Arguments("name", false)]
    [Arguments("documentation", false)]
    [Arguments("icon", false)]
    [Arguments("format", true)]
    public async Task SubmittedSystemFieldsRejectEmptyStrings(string name, bool version)
    {
        var definitions = version ? s_model.Groups["gs"].Resources["rs"].Attributes : s_model.Groups["gs"].Attributes;
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"" + name + "\":\"\"}"), definitions,
            new() { Mode = RegistryMetadataMode.ClientInput }));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/" + name);
    }

    [Test]
    [Arguments(4085, true)]
    [Arguments(4086, false)]
    [Arguments(4096, false)]
    public async Task ScalarDescriptionCountsItsNameAtTheInclusive4096ByteBoundary(int length, bool accepted)
    {
        var text = new string('x', length);
        var input = RegistryJson.Parse("{\"description\":\"" + text + "\"}");
        var definitions = s_model.Groups["gs"].Attributes;
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };

        if (accepted)
        {
            var result = RegistryMetadataValidator.Validate(input, definitions, options);
            await Assert.That(result.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo(text);
        }
        else
        {
            var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(input, definitions, options));
            await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
            await Assert.That(diagnostic.Path).IsEqualTo("/description");
        }
    }

    [Test]
    [Arguments("name", false)]
    [Arguments("documentation", false)]
    [Arguments("icon", false)]
    [Arguments("format", true)]
    public async Task CompleteEntitiesRejectEmptySystemFields(string name, bool version)
    {
        var definitions = version ? s_model.Groups["gs"].Resources["rs"].Attributes : s_model.Groups["gs"].Attributes;
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            CompleteMetadata(version, name, ""), definitions));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/" + name);
    }

    [Test]
    [Arguments("name", " ", false)]
    [Arguments("documentation", "docs/about", false)]
    [Arguments("icon", "#icon", false)]
    [Arguments("format", " ", true)]
    public async Task SystemNonemptyRulesDoNotRequireNonWhitespaceOrAbsoluteUris(string name, string text, bool version)
    {
        var definitions = version ? s_model.Groups["gs"].Resources["rs"].Attributes : s_model.Groups["gs"].Attributes;
        var result = RegistryMetadataValidator.Validate(CompleteMetadata(version, name, text), definitions);

        await Assert.That(result.Metadata.RootElement.GetProperty(name).GetString()).IsEqualTo(text);
    }

    [Test]
    [Arguments("name", false)]
    [Arguments("documentation", false)]
    [Arguments("icon", false)]
    [Arguments("format", true)]
    public async Task NullSystemFieldsRemainDeletionRequestsOrAbsentOptionalValues(string name, bool version)
    {
        var definitions = version ? s_model.Groups["gs"].Resources["rs"].Attributes : s_model.Groups["gs"].Attributes;
        var submitted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"" + name + "\":null}"), definitions,
            new() { Mode = RegistryMetadataMode.ClientInput });
        var complete = RegistryMetadataValidator.Validate(CompleteMetadata(version, name, null), definitions);

        await Assert.That(submitted.Metadata.GetPresence(name)).IsEqualTo(JsonPresence.Null);
        await Assert.That(complete.Metadata.GetPresence(name)).IsEqualTo(JsonPresence.Absent);
    }

    [Test]
    [Arguments(RegistryMetadataMode.ClientInput)]
    [Arguments(RegistryMetadataMode.CompleteEntity)]
    public async Task EmptyDescriptionRemainsAPresentLegalString(RegistryMetadataMode mode)
    {
        var result = RegistryMetadataValidator.Validate(CompleteMetadata(false, "description", ""),
            s_model.Groups["gs"].Attributes, new() { Mode = mode });

        await Assert.That(result.Metadata.GetPresence("description")).IsEqualTo(JsonPresence.Value);
        await Assert.That(result.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo("");
    }

    [Test]
    public async Task EmptySameNamedExtensionsAndRootModelUriAnnotationsRemainLegal()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"documentation":"","attributes":{"body":{"type":"object","attributes":{
              "name":"string","format":"string","documentation":"url","icon":"url"}}},
             "groups":{"gs":{"singular":"g","attributes":{"format":"string"},
               "resources":{"rs":{"singular":"r","modelcompatiblewith":""}}}}}
            """));
        var extensions = RegistryMetadataValidator.Validate(RegistryJson.Parse("""
            {"name":"","format":"","documentation":"","icon":""}
            """), model.Attributes["body"].Attributes);
        var group = RegistryMetadataValidator.Validate(Text("format", ""), model.Groups["gs"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput });

        foreach (var name in new[] { "name", "format", "documentation", "icon" })
        {
            await Assert.That(extensions.Metadata.GetPresence(name)).IsEqualTo(JsonPresence.Value);
            await Assert.That(extensions.Metadata.RootElement.GetProperty(name).GetString()).IsEqualTo("");
        }

        await Assert.That(group.Metadata.RootElement.GetProperty("format").GetString()).IsEqualTo("");
        await Assert.That(model.Annotations.Documentation).IsEqualTo("");
        await Assert.That(model.Groups["gs"].Resources["rs"].Annotations.ModelCompatibleWith).IsEqualTo("");
    }

    [Test]
    [Arguments("name", "string", false)]
    [Arguments("documentation", "url", false)]
    [Arguments("icon", "url", false)]
    [Arguments("format", "string", true)]
    public async Task EmptySystemDefaultsRejectWhenAppliedToEntityMetadata(string name, string type, bool version)
    {
        var attributes = "{\"" + name + "\":{\"type\":\"" + type + "\",\"required\":true,\"default\":\"\"}}";
        var model = RegistryModel.Compile(RegistryJson.Parse(version
            ? "{\"groups\":{\"gs\":{\"singular\":\"g\",\"resources\":{\"rs\":{\"singular\":\"r\",\"hasdocument\":false,\"attributes\":" +
                attributes + "}}}}}"
            : "{\"groups\":{\"gs\":{\"singular\":\"g\",\"attributes\":" + attributes +
                ",\"resources\":{\"rs\":{\"singular\":\"r\",\"hasdocument\":false}}}}}"));
        var definitions = version ? model.Groups["gs"].Resources["rs"].Attributes : model.Groups["gs"].Attributes;
        var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            CompleteMetadata(version, name, null), definitions));

        await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(diagnostic.Path).IsEqualTo("/" + name);
    }

    [Test]
    [Arguments("empty")]
    [Arguments("oversized")]
    [Arguments("wrongtype")]
    public async Task SubmittedReadonlyFieldsAreIgnoredBeforeScalarAdmission(string inputKind)
    {
        var group = Group("""{"name":{"type":"string","readonly":true,"required":true,"default":"retained"}}""");
        var value = inputKind switch
        {
            "empty" => "\"\"",
            "oversized" => "\"" + new string('x', 8192) + "\"",
            _ => """{"not":"a string"}"""
        };
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"name\":" + value + ",\"epoch\":{\"invalid\":true},\"description\":\"\"}"),
            group.Attributes, new()
            {
                Mode = RegistryMetadataMode.ClientInput,
                RetainedMetadata = RegistryJson.Parse("""{"name":"retained","epoch":3}""").RootElement
            });

        await Assert.That(result.Metadata.RootElement.GetRawText()).IsEqualTo("""{"description":""}""");
    }

    [Test]
    [Arguments(false, 1, 4095)]
    [Arguments(false, 63, 4033)]
    [Arguments(true, 1, 4095)]
    [Arguments(true, 63, 4033)]
    public async Task DeclaredAndWildcardScalarsCountTheActualAttributeName(bool wildcard, int nameLength, int valueLength)
    {
        var name = new string('a', nameLength);
        var definitions = Shape("{\"" + (wildcard ? "*" : name) + "\":\"string\"}");
        var text = new string('x', valueLength);
        var accepted = RegistryMetadataValidator.Validate(Text(name, text), definitions);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Text(name, text + "x"), definitions));

        await Assert.That(accepted.Metadata.RootElement.GetProperty(name).GetString()).IsEqualTo(text);
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/" + name);
    }

    [Test]
    [Arguments("name", 4092, false)]
    [Arguments("format", 4090, true)]
    [Arguments("documentation", 4083, false)]
    [Arguments("icon", 4092, false)]
    public async Task SystemStringAndUrlFieldsAlsoEnforceInclusiveScalarBoundaries(string name, int length, bool version)
    {
        var definitions = version ? s_model.Groups["gs"].Resources["rs"].Attributes : s_model.Groups["gs"].Attributes;
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var text = new string('x', length);
        var accepted = RegistryMetadataValidator.Validate(Text(name, text), definitions, options);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Text(name, text + "x"), definitions, options));

        await Assert.That(accepted.Metadata.RootElement.GetProperty(name).GetString()).IsEqualTo(text);
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/" + name);
    }

    [Test]
    public async Task TimestampScalarBoundaryPreservesAllFractionalDigits()
    {
        var definitions = Shape("""{"at":"timestamp"}""");
        var prefix = "2026-09-12T00:00:00." + new string('1', 4073);
        var accepted = RegistryMetadataValidator.Validate(Text("at", prefix + "Z"), definitions);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Text("at", prefix + "1Z"), definitions));

        await Assert.That(accepted.Metadata.RootElement.GetProperty("at").GetString()).IsEqualTo(prefix + "Z");
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/at");
    }

    [Test]
    [Arguments("\u00e9", 2042, 1)]
    [Arguments("\u20ac", 1361, 2)]
    [Arguments("\U0001f600", 1021, 1)]
    public async Task ScalarBoundaryCountsUtf8BytesRatherThanUtf16Characters(string unit, int count, int padding)
    {
        var text = string.Concat(Enumerable.Repeat(unit, count)) + new string('x', padding);
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var definitions = s_model.Groups["gs"].Attributes;
        var accepted = RegistryMetadataValidator.Validate(Text("description", text), definitions, options);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Text("description", text + "x"), definitions, options));

        await Assert.That(accepted.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo(text);
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/description");
    }

    [Test]
    [Arguments("\\u0061", "a", 4084)]
    [Arguments("\\n", "\n", 4084)]
    [Arguments("\\\"", "\"", 4084)]
    [Arguments("\\\\", "\\", 4084)]
    [Arguments("\\u20ac", "\u20ac", 4082)]
    [Arguments("\\ud83d\\ude00", "\U0001f600", 4081)]
    public async Task JsonNameAndValueEscapesDoNotConsumeTheScalarByteBudget(string escaped, string decoded, int padding)
    {
        var prefix = new string('x', padding);
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var definitions = s_model.Groups["gs"].Attributes;
        var accepted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"descri\\u0070tion\":\"" + prefix + escaped + "\"}"), definitions, options);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("{\"description\":\"" + prefix + escaped + "x\"}"), definitions, options));

        await Assert.That(accepted.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo(prefix + decoded);
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/description");
    }

    [Test]
    [Arguments("object", "/container/value")]
    [Arguments("map", "/container/key/value")]
    [Arguments("array", "/container/0/value")]
    public async Task NestedScalarAttributesUseLocalNamesAndPrecisePointers(string containerType, string expectedPath)
    {
        const string item = """{"type":"object","attributes":{"value":"string"}}""";
        var definitions = Shape("{\"container\":" + (containerType == "object"
            ? item : "{\"type\":\"" + containerType + "\",\"item\":" + item + "}") + "}");
        var text = new string('x', 4091);
        RegistryJson Input(string value)
        {
            var child = "{\"value\":\"" + value + "\"}";
            return RegistryJson.Parse("{\"container\":" + (containerType switch
            {
                "map" => "{\"key\":" + child + "}",
                "array" => "[" + child + "]",
                _ => child
            }) + "}");
        }

        var input = Input(text);
        var accepted = RegistryMetadataValidator.Validate(input, definitions);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Input(text + "x"), definitions));

        await Assert.That(accepted.Metadata.RootElement.GetRawText()).IsEqualTo(input.RootElement.GetRawText());
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo(expectedPath);
    }

    [Test]
    public async Task TypedCollectionItemsHaveNoInventedAttributeNameOrAggregateScalarLimit()
    {
        var definitions = Shape("""
            {"items":{"type":"array","item":{"type":"string"}},"values":{"type":"map","item":{"type":"string"}}}
            """);
        var text = new string('x', 8192);
        var input = RegistryJson.Parse(new JsonObject
        {
            ["items"] = new JsonArray(text, text),
            ["values"] = new JsonObject { ["key.with.dots"] = text }
        }.ToJsonString());
        var accepted = RegistryMetadataValidator.Validate(input, definitions);

        await Assert.That(accepted.Metadata.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(2);
        await Assert.That(accepted.Metadata.RootElement.GetProperty("items")[0].GetString()).IsEqualTo(text);
        await Assert.That(accepted.Metadata.RootElement.GetProperty("items")[1].GetString()).IsEqualTo(text);
        await Assert.That(accepted.Metadata.RootElement.GetProperty("values").GetProperty("key.with.dots").GetString()).IsEqualTo(text);
    }

    [Test]
    public async Task OpaqueAnyScopesRemainExemptEvenWhenTheirRuntimeValueIsScalar()
    {
        var definitions = Shape("""
            {"opaque":"any","values":{"type":"map","item":{"type":"any"}},"*":"any"}
            """);
        var text = new string('x', 8192);
        var input = RegistryJson.Parse(new JsonObject
        {
            ["opaque"] = new JsonObject { ["Invalid/Name~"] = text, ["name"] = "" },
            ["values"] = new JsonObject { ["key"] = text },
            ["dynamic"] = text
        }.ToJsonString());
        var accepted = RegistryMetadataValidator.Validate(input, definitions);

        await Assert.That(accepted.Metadata.RootElement.GetProperty("opaque").GetProperty("Invalid/Name~").GetString()).IsEqualTo(text);
        await Assert.That(accepted.Metadata.RootElement.GetProperty("opaque").GetProperty("name").GetString()).IsEqualTo("");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("values").GetProperty("key").GetString()).IsEqualTo(text);
        await Assert.That(accepted.Metadata.RootElement.GetProperty("dynamic").GetString()).IsEqualTo(text);
    }

    [Test]
    public async Task InlineDocumentsAreExemptButDocumentReferenceUrlsAreScalarMetadata()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r"}}}}}
            """));
        var definitions = model.Groups["gs"].Resources["rs"].Attributes;
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var text = new string('x', 8192);
        var inline = RegistryMetadataValidator.Validate(Text("r", text), definitions, options);
        var base64 = Convert.ToBase64String(new byte[6144]);
        var encoded = RegistryMetadataValidator.Validate(Text("rbase64", base64), definitions, options);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            Text("rurl", "https://example.test/" + text), definitions, options));

        await Assert.That(inline.Metadata.RootElement.GetProperty("r").GetString()).IsEqualTo(text);
        await Assert.That(encoded.Metadata.RootElement.GetProperty("rbase64").GetString()).IsEqualTo(base64);
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/rurl");
    }

    [Test]
    public async Task DocumentlessSameNamedExtensionsDoNotAcquireTheDocumentExemption()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,
              "attributes":{"rbase64":"string"}}}}}}
            """));
        var definitions = model.Groups["gs"].Resources["rs"].Attributes;
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var text = new string('x', 4089);
        var accepted = RegistryMetadataValidator.Validate(Text("rbase64", text), definitions, options);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Text("rbase64", text + "x"), definitions, options));

        await Assert.That(accepted.Metadata.RootElement.GetProperty("rbase64").GetString()).IsEqualTo(text);
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/rbase64");
    }

    [Test]
    [Arguments("integer")]
    [Arguments("uinteger")]
    [Arguments("decimal")]
    public async Task NumericTokenBudgetStillBoundsScalarRepresentationBefore4096Bytes(string type)
    {
        var name = new string('n', 63);
        var definitions = Shape("{\"" + name + "\":\"" + type + "\"}");
        var token = new string('9', 1024);
        var input = RegistryJson.Parse("{\"" + name + "\":" + token + "}");
        var accepted = RegistryMetadataValidator.Validate(input, definitions);
        var overTokenBudget = RegistryJson.Parse("{\"" + name + "\":" + token + "9}",
            new() { MaxNumberCharacters = 1025 });
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(overTokenBudget, definitions));

        await Assert.That(accepted.Metadata.RootElement.GetProperty(name).GetRawText()).IsEqualTo(token);
        await Assert.That(rejected.Code).IsEqualTo("number_limit");
    }

    [Test]
    [Arguments("integer", "1e10000")]
    [Arguments("uinteger", "1e10000")]
    [Arguments("decimal", "1e-10000")]
    [Arguments("boolean", "true")]
    [Arguments("boolean", "false")]
    public async Task ShortNumericAndBooleanSerializationsAreNotExpandedForTheScalarLimit(string type, string token)
    {
        var name = new string('n', 63);
        var definitions = Shape("{\"" + name + "\":\"" + type + "\"}");
        var accepted = RegistryMetadataValidator.Validate(RegistryJson.Parse("{\"" + name + "\":" + token + "}"), definitions);

        await Assert.That(accepted.Metadata.RootElement.GetProperty(name).GetRawText()).IsEqualTo(token);
    }

    [Test]
    [Arguments(4085, true, false)]
    [Arguments(4086, false, false)]
    [Arguments(4085, true, true)]
    [Arguments(4086, false, true)]
    public async Task ScalarDefaultsAreCheckedWhenNullRequestsApplyThem(int length, bool accepted, bool readOnly)
    {
        var text = new string('x', length);
        var group = Group("{\"description\":{\"type\":\"string\",\"required\":true,\"readonly\":" +
            (readOnly ? "true" : "false") + ",\"default\":\"" + text + "\"}}");
        var input = CompleteMetadata(false, "description", null);
        if (accepted)
        {
            var result = RegistryMetadataValidator.Validate(input, group.Attributes);
            await Assert.That(result.Metadata.RootElement.GetProperty("description").GetString()).IsEqualTo(text);
        }
        else
        {
            var diagnostic = TestErrors.Capture(() => RegistryMetadataValidator.Validate(input, group.Attributes));
            await Assert.That(diagnostic.Code).IsEqualTo("invalid_attribute");
            await Assert.That(diagnostic.Path).IsEqualTo("/description");
        }
    }

    [Test]
    public async Task RetainedPatchDiscriminatorsStillSelectScalarConstraintsWithoutMergingTheirValues()
    {
        var definitions = Shape("""
            {"kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra":"string"}}}}}
            """);
        var options = new RegistryMetadataValidationOptions
        {
            Mode = RegistryMetadataMode.ClientInput,
            RetainedMetadata = RegistryJson.Parse("""{"kind":"on","extra":"retained"}""").RootElement
        };
        var text = new string('x', 4091);
        var accepted = RegistryMetadataValidator.Validate(Text("extra", text), definitions, options);
        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Text("extra", text + "x"), definitions, options));
        var deletion = RegistryMetadataValidator.Validate(RegistryJson.Parse("""{"extra":null}"""), definitions, options);

        await Assert.That(accepted.Metadata.RootElement.GetProperty("extra").GetString()).IsEqualTo(text);
        await Assert.That(accepted.Metadata.GetPresence("kind")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/extra");
        await Assert.That(deletion.Metadata.RootElement.GetRawText()).IsEqualTo("""{"extra":null}""");
    }

    [Test]
    public async Task WholeJsonAndWorkBudgetsRemainIndependentOfTheScalarLimit()
    {
        var definitions = Shape("""{"value":"string"}""");
        var input = Text("value", new string('x', 4091));
        var bytes = TestErrors.Capture(() => RegistryMetadataValidator.Validate(input, definitions,
            new() { Limits = new() { MaxBytes = 64 } }));
        var nodes = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Text("value", "small"), definitions,
            new() { Limits = new() { MaxNodes = 1 } }));
        var scalar = TestErrors.Capture(() => RegistryMetadataValidator.Validate(Text("value", new string('x', 4092)), definitions,
            new() { Limits = new() { MaxBytes = 16 * 1024 } }));

        await Assert.That(bytes.Code).IsEqualTo("byte_limit");
        await Assert.That(nodes.Code).IsEqualTo("node_limit");
        await Assert.That(scalar.Code).IsEqualTo("invalid_attribute");
        await Assert.That(scalar.Path).IsEqualTo("/value");
    }

    private static RegistryJson Text(string name, string text) =>
        RegistryJson.Parse(new JsonObject { [name] = text }.ToJsonString());

    private static IReadOnlyDictionary<string, RegistryAttributeDefinition> Shape(string attributes) =>
        RegistryModel.Compile(RegistryJson.Parse("{\"attributes\":{\"body\":{\"type\":\"object\",\"attributes\":" +
            attributes + "}}}")).Attributes["body"].Attributes;

    private static RegistryGroupDefinition Group(string attributes) =>
        RegistryModel.Compile(RegistryJson.Parse("{\"groups\":{\"gs\":{\"singular\":\"g\",\"attributes\":" + attributes +
            ",\"resources\":{\"rs\":{\"singular\":\"r\",\"hasdocument\":false}}}}}")).Groups["gs"];

    private static RegistryJson CompleteMetadata(bool version, string name, string? text)
    {
        var metadata = JsonNode.Parse(version ? """
            {"rid":"r","versionid":"v1","self":"https://example.test/gs/g/rs/r/versions/v1",
             "xid":"/gs/g/rs/r/versions/v1","epoch":0,"ancestorid":"v1","isdefault":true,
             "createdat":"2026-09-12T00:00:00Z","modifiedat":"2026-09-12T00:00:00Z"}
            """ : """
            {"gid":"g","self":"https://example.test/gs/g","xid":"/gs/g","epoch":0,
             "rsurl":"https://example.test/gs/g/rs","rscount":0,
             "createdat":"2026-09-12T00:00:00Z","modifiedat":"2026-09-12T00:00:00Z"}
            """)!.AsObject();
        metadata[name] = text;
        return RegistryJson.Parse(metadata.ToJsonString());
    }
}
