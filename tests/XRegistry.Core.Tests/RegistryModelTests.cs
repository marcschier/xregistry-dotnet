// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Numerics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryModelTests
{
    [Test]
    public async Task CompilesIndependentMinimalSpecModel()
    {
        // core/sample-model.json, frozen 1.0-rc4.
        var source = RegistryJson.Parse("""{"groups":{"dirs":{"singular":"dir","resources":{"files":{"singular":"file"}}}}}""");
        var model = RegistryModel.Compile(source);
        var group = model.Groups["dirs"];
        var resource = group.Resources["files"];

        await Assert.That(group.Plural).IsEqualTo("dirs");
        await Assert.That(group.Singular).IsEqualTo("dir");
        await Assert.That(resource.Plural).IsEqualTo("files");
        await Assert.That(resource.Singular).IsEqualTo("file");
        await Assert.That(resource.MaxVersions).IsEqualTo(BigInteger.Zero);
        await Assert.That(resource.HasDocument).IsTrue();
        await Assert.That(resource.SetVersionId).IsTrue();
        await Assert.That(resource.ValidateFormat).IsFalse();
        await Assert.That(resource.ValidateCompatibility).IsFalse();
        await Assert.That(resource.Annotations.Description).IsNull();
        await Assert.That(resource.Annotations.ModelVersion).IsNull();
        await Assert.That(resource.Annotations.Labels.Count).IsEqualTo(0);
        await Assert.That(resource.VersionMode).IsEqualTo("manual");
        await Assert.That(resource.SingleVersionRoot).IsFalse();
        await Assert.That(model.Attributes["specversion"].DefaultValue.GetString()).IsEqualTo("1.0-rc4");
        await Assert.That(model.Source.RootElement.GetProperty("groups").GetProperty("dirs")
            .TryGetProperty("plural", out _)).IsFalse();
        var effective = model.EffectiveModel.RootElement;
        var effectiveGroup = effective.GetProperty("groups").GetProperty("dirs");
        var effectiveResource = effectiveGroup.GetProperty("resources").GetProperty("files");
        await Assert.That(effective.TryGetProperty("attributes", out _)).IsTrue();
        await Assert.That(effectiveGroup.TryGetProperty("attributes", out _)).IsTrue();
        await Assert.That(effectiveResource.TryGetProperty("attributes", out _)).IsTrue();
        await Assert.That(effectiveGroup.GetProperty("plural").GetString()).IsEqualTo("dirs");
        await Assert.That(resource.Attributes["versionid"].Required).IsTrue();
        await Assert.That(resource.MetaAttributes["defaultversionsticky"].DefaultValue.GetBoolean()).IsFalse();
    }

    [Test]
    public async Task CoreEntityIdsUseRequiredImmutableAttributeNames()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false}}}}}
            """));
        var resource = model.Groups["teams"].Resources["notes"];
        var identifiers = new[]
        {
            model.Attributes["registryid"],
            model.Groups["teams"].Attributes["teamid"],
            resource.ResourceAttributes["noteid"],
            resource.Attributes["versionid"],
            resource.Attributes["noteid"]
        };

        foreach (var identifier in identifiers)
        {
            await Assert.That(identifier.Required).IsTrue();
            await Assert.That(identifier.Immutable).IsTrue();
        }
    }

    [Test]
    public async Task CoreSelfAttributesAreRequiredImmutableUrlsAcrossEntityPlanes()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false}}}}}
            """));
        var resource = model.Groups["teams"].Resources["notes"];
        var selfAttributes = new[]
        {
            model.Attributes["self"],
            model.Groups["teams"].Attributes["self"],
            resource.ResourceAttributes["self"],
            resource.Attributes["self"],
            resource.MetaAttributes["self"]
        };

        foreach (var self in selfAttributes)
        {
            await Assert.That(self.Type).IsEqualTo(RegistryValueType.Url);
            await Assert.That(self.Required).IsTrue();
            await Assert.That(self.Immutable).IsTrue();
            await Assert.That(self.ReadOnly).IsTrue();
        }
    }

    [Test]
    public async Task CoreXidAttributesAreRequiredImmutableReadonlyXidsAcrossEntityPlanes()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false}}}}}
            """));
        var resource = model.Groups["teams"].Resources["notes"];
        var xids = new[]
        {
            model.Attributes["xid"],
            model.Groups["teams"].Attributes["xid"],
            resource.ResourceAttributes["xid"],
            resource.Attributes["xid"],
            resource.MetaAttributes["xid"]
        };

        foreach (var xid in xids)
        {
            await Assert.That(xid.Type).IsEqualTo(RegistryValueType.Xid);
            await Assert.That(xid.Required).IsTrue();
            await Assert.That(xid.Immutable).IsTrue();
            await Assert.That(xid.ReadOnly).IsTrue();
        }
    }

    [Test]
    public async Task CoreEpochAttributesAreRequiredReadonlyUnsignedIntegersAcrossEntityKinds()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false}}}}}
            """));
        var resource = model.Groups["teams"].Resources["notes"];
        var epochs = new[]
        {
            model.Attributes["epoch"],
            model.Groups["teams"].Attributes["epoch"],
            resource.Attributes["epoch"],
            resource.MetaAttributes["epoch"]
        };

        foreach (var epoch in epochs)
        {
            await Assert.That(epoch.Type).IsEqualTo(RegistryValueType.UInteger);
            await Assert.That(epoch.Required).IsTrue();
            await Assert.That(epoch.ReadOnly).IsTrue();
        }
    }

    [Test]
    public async Task CoreShortSelfAttributesAreImmutableReadonlyUrlsAcrossEntityPlanes()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false}}}}}
            """));
        var resource = model.Groups["teams"].Resources["notes"];
        var shortSelfAttributes = new[]
        {
            model.Attributes["shortself"],
            model.Groups["teams"].Attributes["shortself"],
            resource.ResourceAttributes["shortself"],
            resource.Attributes["shortself"],
            resource.MetaAttributes["shortself"]
        };

        foreach (var shortSelf in shortSelfAttributes)
        {
            await Assert.That(shortSelf.Type).IsEqualTo(RegistryValueType.Url);
            await Assert.That(shortSelf.Required).IsFalse();
            await Assert.That(shortSelf.Immutable).IsTrue();
            await Assert.That(shortSelf.ReadOnly).IsTrue();
        }
    }

    [Test]
    public async Task ResourceAnnotationsPreserveModelVersionAndEmptyLabelValues()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"dirs":{"singular":"dir","resources":{"files":{
              "singular":"file","description":"Files published for the team",
              "modelversion":"1.0-rc4+local","labels":{"empty":"","owner":"team"}
            }}}}}
            """));
        var resource = model.Groups["dirs"].Resources["files"];

        await Assert.That(resource.Annotations.Description).IsEqualTo("Files published for the team");
        await Assert.That(resource.Annotations.ModelVersion).IsEqualTo("1.0-rc4+local");
        await Assert.That(resource.Annotations.Labels.Count).IsEqualTo(2);
        await Assert.That(resource.Annotations.Labels["empty"]).IsEqualTo("");
        await Assert.That(resource.Annotations.Labels["owner"]).IsEqualTo("team");

        var effective = model.EffectiveModel.RootElement.GetProperty("groups").GetProperty("dirs")
            .GetProperty("resources").GetProperty("files");
        await Assert.That(effective.GetProperty("description").GetString()).IsEqualTo("Files published for the team");
        await Assert.That(effective.GetProperty("modelversion").GetString()).IsEqualTo("1.0-rc4+local");
        await Assert.That(effective.GetProperty("labels").GetProperty("empty").GetString()).IsEqualTo("");
        await Assert.That(effective.GetProperty("labels").GetProperty("owner").GetString()).IsEqualTo("team");
    }

    [Test]
    public async Task GroupLabelsAreOptionalAndPreserveEmptyStringValues()
    {
        var omitted = RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"dirs":{"singular":"dir"}}}"""));
        var labeled = RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"dirs":{"singular":"dir","labels":{"empty":"","owner":"team"}}}}"""));

        await Assert.That(omitted.Groups["dirs"].Annotations.Labels.Count).IsEqualTo(0);
        await Assert.That(labeled.Groups["dirs"].Annotations.Labels.Count).IsEqualTo(2);
        await Assert.That(labeled.Groups["dirs"].Annotations.Labels["empty"]).IsEqualTo("");
        var effective = labeled.EffectiveModel.RootElement.GetProperty("groups").GetProperty("dirs");
        await Assert.That(effective.GetProperty("labels").GetProperty("empty").GetString()).IsEqualTo("");
        await Assert.That(effective.GetProperty("labels").GetProperty("owner").GetString()).IsEqualTo("team");
    }

    [Test]
    public async Task GroupModelVersionIsAnOptionalOpaqueString()
    {
        var omitted = RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"dirs":{"singular":"dir"}}}"""));
        var authored = RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"dirs":{"singular":"dir","modelversion":"vendor build / rC"}}}"""));

        await Assert.That(omitted.Groups["dirs"].Annotations.ModelVersion).IsNull();
        await Assert.That(authored.Groups["dirs"].Annotations.ModelVersion).IsEqualTo("vendor build / rC");
        await Assert.That(authored.EffectiveModel.RootElement.GetProperty("groups").GetProperty("dirs")
            .GetProperty("modelversion").GetString()).IsEqualTo("vendor build / rC");
    }

    [Test]
    public async Task GroupDescriptionIsOptionalAndPreservedInEffectiveModel()
    {
        var omitted = RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"dirs":{"singular":"dir"}}}"""));
        var authored = RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"dirs":{"singular":"dir","description":"Files & related data"}}}"""));

        await Assert.That(omitted.Groups["dirs"].Annotations.Description).IsNull();
        await Assert.That(authored.Groups["dirs"].Annotations.Description).IsEqualTo("Files & related data");
        await Assert.That(authored.EffectiveModel.RootElement.GetProperty("groups").GetProperty("dirs")
            .GetProperty("description").GetString()).IsEqualTo("Files & related data");
    }

    [Test]
    public async Task InvalidGroupLabelMapsFailAtTheirModelPaths()
    {
        foreach (var (labels, path) in new[]
        {
            ("[]", "/groups/dirs/labels"),
            ("""{"":"value"}""", "/groups/dirs/labels/"),
            ("""{"owner":1}""", "/groups/dirs/labels/owner")
        })
        {
            var error = TestErrors.Capture(() => RegistryModel.Compile(
                RegistryJson.Parse("{\"groups\":{\"dirs\":{\"singular\":\"dir\",\"labels\":" + labels + "}}}")));
            await Assert.That(error.Code).IsEqualTo("model_error");
            await Assert.That(error.Path).IsEqualTo(path);
        }
    }

    [Test]
    public async Task RootDescriptionAndLabelsAreOptionalAndEmptyLabelsAreValid()
    {
        var omitted = RegistryModel.Compile(RegistryJson.Parse("{}"));
        var empty = RegistryModel.Compile(RegistryJson.Parse("""{"description":"Root model","labels":{}}"""));

        await Assert.That(omitted.Annotations.Description).IsNull();
        await Assert.That(omitted.Annotations.Labels.Count).IsEqualTo(0);
        await Assert.That(empty.Annotations.Description).IsEqualTo("Root model");
        await Assert.That(empty.Annotations.Labels.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RootLabelsAcceptNonemptyKeysAndEmptyStringValues()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(
            """{"labels":{"empty":"","display name":"Registry"} }"""));

        await Assert.That(model.Annotations.Labels.Count).IsEqualTo(2);
        await Assert.That(model.Annotations.Labels["empty"]).IsEqualTo("");
        await Assert.That(model.Annotations.Labels["display name"]).IsEqualTo("Registry");
    }

    [Test]
    public async Task InvalidRootLabelMapsFailAtTheirModelPaths()
    {
        foreach (var (labels, path) in new[]
        {
            ("[]", "/labels"),
            ("""{"":"value"}""", "/labels/"),
            ("""{"owner":1}""", "/labels/owner")
        })
        {
            var error = TestErrors.Capture(() => RegistryModel.Compile(
                RegistryJson.Parse("{\"labels\":" + labels + "}")));
            await Assert.That(error.Code).IsEqualTo("model_error");
            await Assert.That(error.Path).IsEqualTo(path);
        }
    }

    [Test]
    public async Task PreservesSourceAndOwnsEffectiveDefinitions()
    {
        RegistryModel model;
        using (var document = JsonDocument.Parse(
            """{"attributes":{"large":{"type":"integer","required":true,"default":9007199254740993},"unused":{"type":"string","default":null}}}"""))
        {
            model = RegistryModel.Compile(RegistryJson.FromElement(document.RootElement));
        }

        await Assert.That(model.Attributes["large"].DefaultValue.GetRawText()).IsEqualTo("9007199254740993");
        await Assert.That(model.Attributes["unused"].DefaultValue.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(model.Source.RootElement.GetProperty("attributes").GetProperty("unused")
            .GetProperty("default").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(model.EffectiveModel.RootElement.GetProperty("attributes").GetProperty("large")
            .GetProperty("name").GetString()).IsEqualTo("large");
        await Assert.That(() => ((IDictionary<string, RegistryAttributeDefinition>)model.Attributes)
            .Add("bad", model.Attributes["large"])).Throws<NotSupportedException>();
    }

    [Test]
    public async Task UsesNormativeNameLengthsRatherThanDerivedSchemaLengths()
    {
        var group = new string('g', 57);
        var singular = new string('s', 61);
        var model = RegistryModel.Compile(RegistryJson.Parse(
            $$$$"""{"groups":{"{{{{group}}}}":{"singular":"{{{{singular}}}}"}}}"""));
        await Assert.That(model.Groups[group].Singular).IsEqualTo(singular);
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(
            $$$$"""{"groups":{"{{{{new string('g', 58)}}}}":{"singular":"g"}}}"""))).Path)
            .IsEqualTo("/groups/" + new string('g', 58) + "/plural");
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(
            $$$$"""{"groups":{"g":{"singular":"{{{{new string('s', 62)}}}}"}}}"""))).Path)
            .IsEqualTo("/groups/g/singular");
        var resourcePlural = new string('r', 57);
        var resourceType = RegistryModel.Compile(RegistryJson.Parse(
            "{\"groups\":{\"dirs\":{\"singular\":\"dir\",\"resources\":{\"" + resourcePlural +
            "\":{\"singular\":\"resource\"}}}}}"));
        await Assert.That(resourceType.Groups["dirs"].Resources[resourcePlural].Plural).IsEqualTo(resourcePlural);
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(
            "{\"groups\":{\"dirs\":{\"singular\":\"dir\",\"resources\":{\"" + new string('r', 58) +
            "\":{\"singular\":\"resource\"}}}}}"))).Path)
            .IsEqualTo("/groups/dirs/resources/" + new string('r', 58) + "/plural");
        var resourceSingular = new string('s', 57);
        var resource = CompileResource($$""""{"singular":"{{resourceSingular}}"}"""");
        await Assert.That(resource.Singular).IsEqualTo(resourceSingular);
        await Assert.That(TestErrors.Capture(() => CompileResource(
            $$""""{"singular":"{{new string('s', 58)}}"}"""")).Path)
            .IsEqualTo("/groups/dirs/resources/files/singular");
        await Assert.That(RegistryModel.Compile(RegistryJson.Parse("""{"attributes":{"_extension":"string"}}"""))
            .Attributes["_extension"].Type).IsEqualTo(RegistryValueType.String);
    }

    [Test]
    public async Task AttributeNamesAreLimitedToOneThroughSixtyThreeCharacters()
    {
        var shortest = RegistryModel.Compile(RegistryJson.Parse("""{"attributes":{"a":"string"}}"""));
        var longestName = new string('a', 63);
        var longest = RegistryModel.Compile(RegistryJson.Parse(
            "{\"attributes\":{\"" + longestName + "\":\"string\"}}"));
        var tooLongName = new string('a', 64);
        var rejected = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(
            "{\"attributes\":{\"" + tooLongName + "\":\"string\"}}")));

        await Assert.That(shortest.Attributes.ContainsKey("a")).IsTrue();
        await Assert.That(longest.Attributes.ContainsKey(longestName)).IsTrue();
        await Assert.That(rejected.Code).IsEqualTo("model_error");
        await Assert.That(rejected.Path).IsEqualTo("/attributes/" + tooLongName);
    }

    [Test]
    [Arguments("a", true)]
    [Arguments("a0", true)]
    [Arguments("_leading", true)]
    [Arguments("a_name", true)]
    [Arguments("Upper", false)]
    [Arguments("has-hyphen", false)]
    [Arguments("has.period", false)]
    [Arguments("1leading", false)]
    public async Task AttributeNamesFollowCoreCharacterRules(string name, bool valid)
    {
        var modelJson = "{\"attributes\":{\"" + name + "\":\"string\"}}";
        if (valid)
        {
            var model = RegistryModel.Compile(RegistryJson.Parse(modelJson));
            await Assert.That(model.Attributes.ContainsKey(name)).IsTrue();
            return;
        }

        var rejected = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(modelJson)));
        await Assert.That(rejected.Code).IsEqualTo("model_error");
        await Assert.That(rejected.Path).IsEqualTo("/attributes/" + name);
    }

    [Test]
    public async Task CollectionItemEnumsAndStrictRoundTripThroughTheEffectiveModel()
    {
        var model = CompileCollectionItemModel();
        var labels = model.Attributes["customstrings"].Item!;
        var scores = model.Attributes["customints"].Item!;

        await Assert.That(labels.Strict).IsTrue();
        await Assert.That(labels.EnumValues.Count).IsEqualTo(2);
        await Assert.That(labels.EnumValues[0].GetString()).IsEqualTo("red");
        await Assert.That(labels.EnumValues[1].GetString()).IsEqualTo("blue");
        await Assert.That(scores.Strict).IsFalse();
        await Assert.That(scores.EnumValues.Count).IsEqualTo(2);
        await Assert.That(scores.EnumValues[0].GetInt32()).IsEqualTo(1);
        await Assert.That(scores.EnumValues[1].GetInt32()).IsEqualTo(2);

        var effective = model.EffectiveModel.RootElement.GetProperty("attributes");
        var labelsItem = effective.GetProperty("customstrings").GetProperty("item");
        var scoresItem = effective.GetProperty("customints").GetProperty("item");
        await Assert.That(labelsItem.GetProperty("strict").GetBoolean()).IsTrue();
        await Assert.That(labelsItem.GetProperty("enum").GetRawText()).IsEqualTo("""["red","blue"]""");
        await Assert.That(scoresItem.GetProperty("strict").GetBoolean()).IsFalse();
        await Assert.That(scoresItem.GetProperty("enum").GetRawText()).IsEqualTo("[1,2]");

        var roundTripped = RegistryModel.Compile(RegistryJson.Parse(model.EffectiveModel.RootElement.GetRawText()));
        var roundTrippedLabels = roundTripped.Attributes["customstrings"].Item!;
        await Assert.That(roundTrippedLabels.EnumValues.Count).IsEqualTo(2);
        await Assert.That(roundTrippedLabels.EnumValues[0].GetString()).IsEqualTo("red");
        await Assert.That(roundTrippedLabels.EnumValues[1].GetString()).IsEqualTo("blue");
        await Assert.That(roundTrippedLabels.Strict).IsTrue();
        var roundTrippedScores = roundTripped.Attributes["customints"].Item!;
        await Assert.That(roundTrippedScores.EnumValues.Count).IsEqualTo(2);
        await Assert.That(roundTrippedScores.EnumValues[0].GetInt32()).IsEqualTo(1);
        await Assert.That(roundTrippedScores.EnumValues[1].GetInt32()).IsEqualTo(2);
        await Assert.That(roundTrippedScores.Strict).IsFalse();
    }

    [Test]
    public async Task StrictCollectionItemEnumsRejectDisallowedValuesAtTheItemPath()
    {
        var model = CompileCollectionItemModel();
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var accepted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"customstrings":["red","blue"]}"""), model.Attributes, options);
        await Assert.That(accepted.Metadata.RootElement.GetProperty("customstrings").GetRawText())
            .IsEqualTo("""["red","blue"]""");

        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"customstrings":["red","green"]}"""), model.Attributes, options));
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/customstrings/1");
    }

    [Test]
    public async Task NonStrictCollectionItemEnumsAreAdvisoryButKeepTheirScalarType()
    {
        var model = CompileCollectionItemModel();
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var accepted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"customints":{"high":3}}"""), model.Attributes, options);
        await Assert.That(accepted.Metadata.RootElement.GetProperty("customints").GetProperty("high").GetInt32())
            .IsEqualTo(3);

        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"customints":{"high":"3"}}"""), model.Attributes, options));
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/customints/high");
    }

    [Test]
    public async Task NonStrictScalarEnumIsAdvisoryButKeepsItsScalarType()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"suggested":{"type":"string","enum":["alpha"],"strict":false}}}
            """));
        var options = new RegistryMetadataValidationOptions { Mode = RegistryMetadataMode.ClientInput };
        var accepted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"suggested":"beta"}"""), model.Attributes, options);

        await Assert.That(accepted.Metadata.RootElement.GetProperty("suggested").GetString()).IsEqualTo("beta");

        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"suggested":2}"""), model.Attributes, options));
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/suggested");
    }

    [Test]
    public async Task EmptyStrictScalarEnumAllowsAnyValueOfItsDeclaredType()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"choice":{"type":"string","enum":[]}}}
            """));
        var accepted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"choice":"outside-the-enum"}"""), model.Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput });

        await Assert.That(accepted.Metadata.RootElement.GetProperty("choice").GetString()).IsEqualTo("outside-the-enum");

        var wrongType = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"choice":1}"""), model.Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput }));
        await Assert.That(wrongType.Code).IsEqualTo("invalid_attribute");
        await Assert.That(wrongType.Path).IsEqualTo("/choice");
    }

    [Test]
    public async Task EveryCoreDataTypeCompilesAsMutableByDefault()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{
              "anyvalue":"any",
              "arrayvalue":{"type":"array","item":{"type":"string"}},
              "booleanvalue":"boolean",
              "decimalvalue":"decimal",
              "integervalue":"integer",
              "mapvalue":{"type":"map","item":{"type":"string"}},
              "objectvalue":{"type":"object","attributes":{}},
              "stringvalue":"string",
              "timestampvalue":"timestamp",
              "uintegervalue":"uinteger",
              "urivalue":"uri",
              "uriabsolutevalue":"uriabsolute",
              "urirelativevalue":"urirelative",
              "uritemplatevalue":"uritemplate",
              "urlvalue":"url",
              "urlabsolutevalue":"urlabsolute",
              "urlrelativevalue":"urlrelative",
              "xidvalue":"xid",
              "xidtypevalue":"xidtype"
            }}
            """));
        var expected = new Dictionary<string, RegistryValueType>(StringComparer.Ordinal)
        {
            ["anyvalue"] = RegistryValueType.Any,
            ["arrayvalue"] = RegistryValueType.Array,
            ["booleanvalue"] = RegistryValueType.Boolean,
            ["decimalvalue"] = RegistryValueType.Decimal,
            ["integervalue"] = RegistryValueType.Integer,
            ["mapvalue"] = RegistryValueType.Map,
            ["objectvalue"] = RegistryValueType.Object,
            ["stringvalue"] = RegistryValueType.String,
            ["timestampvalue"] = RegistryValueType.Timestamp,
            ["uintegervalue"] = RegistryValueType.UInteger,
            ["urivalue"] = RegistryValueType.Uri,
            ["uriabsolutevalue"] = RegistryValueType.UriAbsolute,
            ["urirelativevalue"] = RegistryValueType.UriRelative,
            ["uritemplatevalue"] = RegistryValueType.UriTemplate,
            ["urlvalue"] = RegistryValueType.Url,
            ["urlabsolutevalue"] = RegistryValueType.UrlAbsolute,
            ["urlrelativevalue"] = RegistryValueType.UrlRelative,
            ["xidvalue"] = RegistryValueType.Xid,
            ["xidtypevalue"] = RegistryValueType.XidType
        };

        foreach (var (name, type) in expected)
        {
            var attribute = model.Attributes[name];
            await Assert.That(attribute.Type).IsEqualTo(type);
            await Assert.That(attribute.ReadOnly).IsFalse();
            await Assert.That(attribute.Immutable).IsFalse();
        }
    }

    [Test]
    public async Task ScalarAttributeAspectsUseNormativeDefaults()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"suggested":{"type":"string","enum":["alpha"]}},
             "groups":{"dirs":{"singular":"dir","resources":{"files":{
               "singular":"file","attributes":{"tag":"string"}
             }}}}}
            """));
        var suggested = model.Attributes["suggested"];
        var tag = model.Groups["dirs"].Resources["files"].Attributes["tag"];

        await Assert.That(suggested.Strict).IsTrue();
        await Assert.That(suggested.ReadOnly).IsFalse();
        await Assert.That(suggested.Immutable).IsFalse();
        await Assert.That(tag.MatchVersions).IsFalse();

        var effective = model.EffectiveModel.RootElement;
        var effectiveSuggested = effective.GetProperty("attributes").GetProperty("suggested");
        await Assert.That(effectiveSuggested.GetProperty("strict").GetBoolean()).IsTrue();
        await Assert.That(effectiveSuggested.GetProperty("readonly").GetBoolean()).IsFalse();
        await Assert.That(effectiveSuggested.GetProperty("immutable").GetBoolean()).IsFalse();
        await Assert.That(effective.GetProperty("groups").GetProperty("dirs").GetProperty("resources")
            .GetProperty("files").GetProperty("attributes").GetProperty("tag").GetProperty("matchversions").GetBoolean())
            .IsFalse();
    }

    [Test]
    public async Task ExtendedObjectNameCharsetAppliesToConditionalSiblingAttributes()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"properties":{"type":"object","namecharset":"EXTENDED","attributes":{
              "mode":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra-field":"integer"}}}},
              "direct-name":"integer","*":"string"
            }}}}
            """));
        var extended = model.Attributes["properties"];
        var result = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""
                {"properties":{"mode":"on","extra-field":3,"direct-name":4,
                  "1leading":"value","with.dot":"value","with:colon":"value"}}
                """),
            model.Attributes, new() { Mode = RegistryMetadataMode.ClientInput });

        await Assert.That(extended.NameCharset).IsEqualTo("extended");
        var value = result.Metadata.RootElement.GetProperty("properties");
        await Assert.That(value.GetProperty("extra-field").GetInt32()).IsEqualTo(3);
        await Assert.That(value.GetProperty("direct-name").GetInt32()).IsEqualTo(4);
        await Assert.That(value.GetProperty("1leading").GetString()).IsEqualTo("value");
        await Assert.That(value.GetProperty("with.dot").GetString()).IsEqualTo("value");
        await Assert.That(value.GetProperty("with:colon").GetString()).IsEqualTo("value");

        var invalidName = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"properties":{"Upper":"value"}}"""), model.Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput }));
        await Assert.That(invalidName.Code).IsEqualTo("invalid_attribute");
        await Assert.That(invalidName.Path).IsEqualTo("/properties/Upper");

        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"properties":{"type":"object","attributes":{
              "mode":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra-field":"integer"}}}}
            }}}}
            """)));
        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path)
            .IsEqualTo("/attributes/properties/attributes/mode/ifvalues/on/siblingattributes/extra-field");
    }

    [Test]
    public async Task ObjectNameCharsetDefaultsToStrict()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"properties":{"type":"object","attributes":{"*":"string"}}}}
            """));
        var properties = model.Attributes["properties"];
        var accepted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"properties":{"valid_name":"value"}}"""), model.Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput });

        await Assert.That(properties.NameCharset).IsEqualTo("strict");
        await Assert.That(model.EffectiveModel.RootElement.GetProperty("attributes").GetProperty("properties")
            .GetProperty("namecharset").GetString()).IsEqualTo("strict");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("properties").GetProperty("valid_name").GetString())
            .IsEqualTo("value");

        var rejected = TestErrors.Capture(() => RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"properties":{"invalid-name":"value"}}"""), model.Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput }));
        await Assert.That(rejected.Code).IsEqualTo("invalid_attribute");
        await Assert.That(rejected.Path).IsEqualTo("/properties/invalid-name");
    }

    [Test]
    public async Task ItemNameCharsetAppliesOnlyToObjectItems()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"properties":{"type":"array","item":{"type":"object","namecharset":"EXTENDED",
              "attributes":{"*":"string"}}}}}
            """));
        var item = model.Attributes["properties"].Item!;
        var accepted = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"properties":[{"extra-name":"value"}]}"""), model.Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput });

        await Assert.That(item.NameCharset).IsEqualTo("extended");
        await Assert.That(accepted.Metadata.RootElement.GetProperty("properties")[0]!.GetProperty("extra-name")
            .GetString()).IsEqualTo("value");

        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{"properties":{"type":"array","item":{"type":"string","namecharset":"extended"}}}}
            """)));
        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path).IsEqualTo("/attributes/properties/item/namecharset");
    }

    [Test]
    public async Task ObjectAttributeDefinitionsCanBeAbsentOrEmpty()
    {
        foreach (var json in new[]
        {
            """{"attributes":{"empty":{"type":"object"}}}""",
            """{"attributes":{"empty":{"type":"object","attributes":{}}}}"""
        })
        {
            var model = RegistryModel.Compile(RegistryJson.Parse(json));
            await Assert.That(model.Attributes["empty"].Attributes.Count).IsEqualTo(0);
            await Assert.That(model.EffectiveModel.RootElement.GetProperty("attributes").GetProperty("empty")
                .GetProperty("attributes").EnumerateObject().Count()).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("""{"attributes":{"a":{}}}""", "/attributes/a/type", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"array"}}}""", "/attributes/a/item", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"array","item":{}}}}""", "/attributes/a/item/type", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"array","item":{"type":"array"}}}}""", "/attributes/a/item/item", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"map","item":{"type":"string","required":true}}}}""", "/attributes/a/item/required", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"array","item":{"type":"string","attributes":{}}}}}""", "/attributes/a/item/attributes", "model_error")]
    [Arguments("""{"groups":{"dirs":{"resources":{"files":{"singular":"file"}}}}}""", "/groups/dirs/singular", "model_error")]
    [Arguments("""{"groups":{"dirs":{"singular":"dir","resources":{"files":{}}}}}""", "/groups/dirs/resources/files/singular", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","item":{"type":"string"}}}}""", "/attributes/a/item", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","attributes":{}}}}""", "/attributes/a/attributes", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"object","namecharset":"unknown"}}}""", "/attributes/a/namecharset", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","namecharset":"strict"}}}""", "/attributes/a/namecharset", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","name":"b"}}}""", "/attributes/a/name", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"STRING"}}}""", "/attributes/a/type", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"binary"}}}""", "/attributes/a/type", "unsupported_model_type")]
    [Arguments("""{"attributes":{"a":{"type":"string","required":"true"}}}""", "/attributes/a/required", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","default":"x"}}}""", "/attributes/a/required", "model_required_true")]
    [Arguments("""{"attributes":{"a":{"type":"object","required":true,"default":{}}}}""", "/attributes/a/default", "model_scalar_default")]
    [Arguments("""{"attributes":{"a":{"type":"integer","required":true,"default":1.5}}}""", "/attributes/a/default", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","required":true,"default":"c","enum":["a","b"]}}}""", "/attributes/a/default", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"integer","enum":[1,"2"]}}}""", "/attributes/a/enum/1", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"array","item":{"type":"integer"},"enum":[]}}}""", "/attributes/a/enum", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","immutable":true}}}""", "/attributes/a/immutable", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","matchversions":true}}}""", "/attributes/a/matchversions", "model_error")]
    [Arguments("""{"attributes":{"*":{"type":"any","required":true}}}""", "/attributes/*", "model_error")]
    [Arguments("""{"attributes":{"*":{"type":"any","readonly":true}}}""", "/attributes/*", "model_error")]
    [Arguments("""{"attributes":{"*":{"type":"any","ifvalues":{"x":{"siblingattributes":{}}}}}}""", "/attributes/*", "model_error")]
    [Arguments("""{"attributes":{"*":{"type":"string"},"mode":{"type":"string","ifvalues":{"open":{"siblingattributes":{"*":{"type":"string"}}}}}}}""", "/attributes/mode/ifvalues/open/siblingattributes/*", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","target":"/dirs/files"}}}""", "/attributes/a/target", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"urlabsolute","target":"/dirs/files"}}}""", "/attributes/a/target", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"uri","target":"/dirs/files/meta"}}}""", "/attributes/a/target", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","ifvalues":{"^future":{"siblingattributes":{}}}}}}""", "/attributes/a/ifvalues/^future", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","ifvalues":{"":{"siblingattributes":{}}}}}}""", "/attributes/a/ifvalues/", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","ifvalues":{"A":{"siblingattributes":{}},"a":{"siblingattributes":{}}}}}}""", "/attributes/a/ifvalues/a", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","enum":["x"],"ifvalues":{"y":{"siblingattributes":{}}}}}}""", "/attributes/a/ifvalues/y", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","ifvalues":{"x":{"siblingattributes":{"a":"string"}}}}}}""", "/attributes/a/ifvalues/x/siblingattributes/a", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"map","item":{"type":"string"},"ifvalues":{}}}}""", "/attributes/a/ifvalues", "model_error")]
    [Arguments("""{"attributes":{"a":{"type":"string","future":true}}}""", "/attributes/a/future", "model_error")]
    [Arguments("""{"future":true}""", "/future", "model_error")]
    public async Task RejectsInvalidModelAspectsWithExactPointers(string json, string expectedPath, string code)
    {
        var diagnostic = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(json)));
        await Assert.That(diagnostic.Code).IsEqualTo(code);
        await Assert.That(diagnostic.Path).IsEqualTo(expectedPath);
    }

    [Test]
    [Arguments("""{"attributes":{"epoch":{"type":"string"}}}""")]
    [Arguments("""{"attributes":{"self":{"readonly":false}}}""")]
    [Arguments("""{"attributes":{"shortself":"string"}}""")]
    [Arguments("""{"attributes":{"specversion":{"required":false}}}""")]
    [Arguments("""{"attributes":{"specversion":{"default":null}}}""")]
    [Arguments("""{"groups":{"model":{"singular":"m"}}}""")]
    [Arguments("""{"groups":{"as":{"singular":"a"},"a":{"singular":"b"}}}""")]
    [Arguments("""{"groups":{"as":{"plural":"bs","singular":"a"}}}""")]
    [Arguments("""{"groups":{"dirs":{"singular":"dir","resources":{"files":{"singular":"file","resourceattributes":{"custom":"string"}}}}}}""")]
    public async Task EnforcesNamingAndSystemAttributeOverlayRules(string json)
    {
        await Assert.That(TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse(json))).Code).IsEqualTo("model_error");
    }

    [Test]
    public async Task DocumentAndCollectionAttributeNamesRespectSystemReservations()
    {
        var documentConflict = TestErrors.Capture(() => CompileResource(
            """{"singular":"file","attributes":{"fileurl":"string"}}"""));
        var withoutDocument = CompileResource(
            """{"singular":"file","hasdocument":false,"attributes":{"fileurl":"string","filebase64":"string"}}""");
        var collectionConflict = TestErrors.Capture(() => RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"dirs":{"singular":"dir","attributes":{"filescount":"string"},
              "resources":{"files":{"singular":"file"}}}}}
            """)));

        await Assert.That(documentConflict.Code).IsEqualTo("model_error");
        await Assert.That(documentConflict.Path).IsEqualTo("/groups/dirs/resources/files/attributes/fileurl");
        await Assert.That(withoutDocument.Attributes["fileurl"].Type).IsEqualTo(RegistryValueType.String);
        await Assert.That(withoutDocument.Attributes["filebase64"].Type).IsEqualTo(RegistryValueType.String);
        await Assert.That(collectionConflict.Code).IsEqualTo("model_error");
    }

    [Test]
    public async Task CompilesNestedItemsAndStaticVersionMatchObligations()
    {
        var resource = CompileResource("""
            {"singular":"file","attributes":{
              "properties":{"type":"object","namecharset":"EXTENDED","attributes":{
                "a-b":{"type":"integer","matchversions":true},
                "bag":{"type":"array","item":{"type":"map","item":{"type":"object","attributes":{"x":"string"}}}}
              }}
            }}
            """);
        await Assert.That(resource.Attributes["properties"].NameCharset).IsEqualTo("extended");
        await Assert.That(resource.Attributes["properties"].Attributes["a-b"].MatchVersions).IsTrue();
        await Assert.That(resource.Attributes["properties"].Attributes["bag"].Item!.Item!.Attributes["x"].Type)
            .IsEqualTo(RegistryValueType.String);
        await Assert.That(TestErrors.Capture(() => CompileResource("""
            {"singular":"file","attributes":{"bag":{"type":"array","item":{"type":"object",
              "attributes":{"a":{"type":"integer","matchversions":true}}}}}}
            """)).Path).IsEqualTo("/groups/dirs/resources/files/attributes/bag/item/attributes/a/matchversions");
    }

    [Test]
    [Arguments("""{"singular":"file","attributes":{"a":{"type":"object","matchversions":true}}}""", "/attributes/a/matchversions")]
    [Arguments("""{"singular":"file","attributes":{"a":{"type":"array","item":"string","matchversions":true}}}""", "/attributes/a/matchversions")]
    [Arguments("""{"singular":"file","attributes":{"a":{"type":"map","item":"string","matchversions":true}}}""", "/attributes/a/matchversions")]
    [Arguments("""{"singular":"file","attributes":{"*":{"type":"integer","matchversions":true}}}""", "/attributes/*/matchversions")]
    [Arguments("""{"singular":"file","attributes":{"mode":{"type":"string","ifvalues":{"on":{"siblingattributes":{"value":{"type":"integer","matchversions":true}}}}}}}""", "/attributes/mode/ifvalues/on/siblingattributes/value/matchversions")]
    [Arguments("""{"singular":"file","attributes":{"items":{"type":"array","item":{"type":"object","attributes":{"value":{"type":"integer","matchversions":true}}}}}}""", "/attributes/items/item/attributes/value/matchversions")]
    [Arguments("""{"singular":"file","attributes":{"items":{"type":"map","item":{"type":"object","attributes":{"value":{"type":"integer","matchversions":true}}}}}}""", "/attributes/items/item/attributes/value/matchversions")]
    public async Task RejectsMatchVersionsOutsideStaticScalarVersionAttributes(string resource, string path)
    {
        var diagnostic = TestErrors.Capture(() => CompileResource(resource));
        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path).IsEqualTo("/groups/dirs/resources/files" + path);
    }

    [Test]
    [Arguments("manual", false)]
    [Arguments("createdat", true)]
    [Arguments("MODIFIEDAT", true)]
    [Arguments("SemVer", true)]
    public async Task EnforcesVersionAndDocumentControls(string mode, bool singleRoot)
    {
        var singleRootSetting = singleRoot ? ",\"singleversionroot\":true" : "";
        var resource = CompileResource($$"""
            {"singular":"file","maxversions":9007199254740993,"versionmode":"{{mode}}",
             "hasdocument":false{{singleRootSetting}},"setversionid":false,"validateformat":true,
             "validatecompatibility":true,"strictvalidation":true}
            """);
        await Assert.That(resource.MaxVersions.ToString(System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo("9007199254740993");
        await Assert.That(resource.SingleVersionRoot).IsEqualTo(singleRoot);
        await Assert.That(resource.HasDocument).IsFalse();
        await Assert.That(resource.SetVersionId).IsFalse();
        await Assert.That(resource.ValidateFormat).IsTrue();
        await Assert.That(resource.ValidateCompatibility).IsTrue();
        await Assert.That(resource.StrictValidation).IsTrue();
        await Assert.That(resource.Attributes.ContainsKey("file")).IsFalse();
        await Assert.That(resource.Attributes.ContainsKey("fileurl")).IsFalse();
        await Assert.That(resource.Attributes.ContainsKey("filebase64")).IsFalse();
    }

    [Test]
    [Arguments("""{"singular":"file","versionmode":"random"}""", "versionmode")]
    [Arguments("""{"singular":"file","versionmode":null}""", "versionmode")]
    [Arguments("""{"singular":"file","versionmode":1}""", "versionmode")]
    [Arguments("""{"singular":"file","versionmode":"createdat"}""", "singleversionroot")]
    [Arguments("""{"singular":"file","versionmode":"modifiedat"}""", "singleversionroot")]
    [Arguments("""{"singular":"file","versionmode":"semver"}""", "singleversionroot")]
    [Arguments("""{"singular":"file","versionmode":"createdat","singleversionroot":false}""", "singleversionroot")]
    [Arguments("""{"singular":"file","versionmode":"modifiedat","singleversionroot":false}""", "singleversionroot")]
    [Arguments("""{"singular":"file","versionmode":"semver","singleversionroot":false}""", "singleversionroot")]
    [Arguments("""{"singular":"file","singleversionroot":null}""", "singleversionroot")]
    [Arguments("""{"singular":"file","singleversionroot":"true"}""", "singleversionroot")]
    [Arguments("""{"singular":"file","singleversionroot":1}""", "singleversionroot")]
    [Arguments("""{"singular":"file","hasdocument":null}""", "hasdocument")]
    [Arguments("""{"singular":"file","validateformat":null}""", "validateformat")]
    [Arguments("""{"singular":"file","validatecompatibility":null}""", "validatecompatibility")]
    [Arguments("""{"singular":"file","setversionid":null}""", "setversionid")]
    [Arguments("""{"singular":"file","strictvalidation":null}""", "strictvalidation")]
    [Arguments("""{"singular":"file","validatecompatibility":true}""", "validatecompatibility")]
    [Arguments("""{"singular":"file","maxversions":-1}""", "maxversions")]
    [Arguments("""{"singular":"file","maxversions":0.5}""", "maxversions")]
    [Arguments("""{"singular":"file","maxversions":"1"}""", "maxversions")]
    [Arguments("""{"singular":"file","maxversions":null}""", "maxversions")]
    [Arguments("""{"singular":"file","hasdocument":1}""", "hasdocument")]
    [Arguments("""{"singular":"file","typemap":{"a**b":"json"}}""", "typemap/a**b")]
    [Arguments("""{"singular":"file","typemap":{"a":"yaml"}}""", "typemap/a")]
    public async Task RejectsInconsistentResourceControls(string resource, string suffix)
    {
        var diagnostic = TestErrors.Capture(() => CompileResource(resource));
        await Assert.That(diagnostic.Code).IsEqualTo("model_error");
        await Assert.That(diagnostic.Path).IsEqualTo("/groups/dirs/resources/files/" + suffix);
    }

    [Test]
    [Arguments("manual", "", false)]
    [Arguments("manual", "false", false)]
    [Arguments("manual", "true", true)]
    [Arguments("createdat", "true", true)]
    [Arguments("modifiedat", "true", true)]
    [Arguments("semver", "true", true)]
    public async Task SingleVersionRootDefaultsFalseAndAutomaticModesRequireTrue(
        string mode, string authored, bool expected)
    {
        var setting = authored.Length == 0 ? "" : ",\"singleversionroot\":" + authored;
        var resource = $$"""
            {"singular":"file","versionmode":"{{mode}}"{{setting}}}
            """;
        var compiled = CompileResource(resource);
        await Assert.That(compiled.SingleVersionRoot).IsEqualTo(expected);
    }

    [Test]
    public async Task TypeMapUsesImplicitDefaultsAndConflictingWildcardRules()
    {
        var resource = CompileResource("""{"singular":"file","typemap":{"text/*":"STRING","text/mine":"json"}}""");
        await Assert.That(resource.ResolveDocumentFormat("APPLICATION/JSON; charset=utf-8")).IsEqualTo(RegistryDocumentFormat.Json);
        await Assert.That(resource.ResolveDocumentFormat("application/problem+json")).IsEqualTo(RegistryDocumentFormat.Json);
        await Assert.That(resource.ResolveDocumentFormat("TEXT/PLAIN")).IsEqualTo(RegistryDocumentFormat.String);
        await Assert.That(resource.ResolveDocumentFormat("text/mine")).IsEqualTo(RegistryDocumentFormat.Binary);
        await Assert.That(resource.ResolveDocumentFormat("application/unknown")).IsEqualTo(RegistryDocumentFormat.Binary);
        var overridden = CompileResource("""{"singular":"file","typemap":{"Application/JSON":"binary"}}""");
        await Assert.That(overridden.ResolveDocumentFormat("application/json")).IsEqualTo(RegistryDocumentFormat.Binary);
    }

    private static RegistryResourceDefinition CompileResource(string resource) =>
        RegistryModel.Compile(RegistryJson.Parse(
            """{"groups":{"dirs":{"singular":"dir","resources":{"files":""" + resource + "}}}}")).Groups["dirs"].Resources["files"];

    private static RegistryModel CompileCollectionItemModel() =>
        RegistryModel.Compile(RegistryJson.Parse("""
            {"attributes":{
              "customstrings":{"type":"array","item":{"type":"string","enum":["red","blue"],"strict":true}},
              "customints":{"type":"map","item":{"type":"integer","enum":[1,2],"strict":false}}
            }}
            """));
}
