using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Core.Tests;

public class RegistryModelCaptureTests
{
    [Test]
    [Arguments("""{"$include":"#/attributes/original"}""")]
    [Arguments("""{"$includes":["#/attributes/original","later.json"]}""")]
    public async Task KnownLocalIncludesCannotBeOverriddenByUnrelatedOrLaterExternalCaptures(string include)
    {
        var source = RegistryJson.Parse(
            """{"$include":"unrelated.json","attributes":{"original":{"type":"string"},"copy":""" + include + "}}");
        var resolved = RegistryJson.Parse("""{"attributes":{"original":{"type":"string"},"copy":{"type":"integer"}}}""");

        var error = TestErrors.Capture(() => RegistryModel.CompileCaptured(source, resolved));
        await Assert.That(error.Code).IsEqualTo("model_error");
        await Assert.That(error.Path).IsEqualTo("/attributes/copy/type");
    }

    [Test]
    public async Task EarlierUnknownIncludeCanOverrideLaterLocalMembersButNotExplicitAuthoredValues()
    {
        var source = RegistryJson.Parse("""
            {"attributes":{"original":{"type":"string"},"copy":{
              "$includes":["earlier.json","#/attributes/original"],"description":"authored"}}}
            """);
        var resolved = RegistryJson.Parse("""
            {"attributes":{"original":{"type":"string"},"copy":{"type":"integer","description":"authored"}}}
            """);
        var model = RegistryModel.CompileCaptured(source, resolved);

        await Assert.That(model.Attributes["copy"].Type).IsEqualTo(RegistryValueType.Integer);
        await Assert.That(model.Attributes["copy"].Description).IsEqualTo("authored");
        await Assert.That(model.Source.RootElement.GetProperty("attributes").GetProperty("copy").GetProperty("$includes")[0].GetString())
            .IsEqualTo("earlier.json");
        var changed = RegistryJson.Parse("""
            {"attributes":{"original":{"type":"string"},"copy":{"type":"integer","description":"not-authored"}}}
            """);
        await Assert.That(TestErrors.Capture(() => RegistryModel.CompileCaptured(source, changed)).Path)
            .IsEqualTo("/attributes/copy/description");
    }

    [Test]
    public async Task CapturedImportsRetainOriginalSourceAndActualResourceTypeSharing()
    {
        var source = RegistryJson.Parse("""{"$include":"https://never-fetch.invalid/captured.json#/model"}""");
        var resolved = RegistryJson.Parse("""
            {"groups":{"original":{"singular":"origin","resources":{"files":{"singular":"file"}}},
              "mirrors":{"singular":"mirror","ximportresources":["/original/files"]}}}
            """);
        var model = RegistryModel.CompileCaptured(source, resolved);

        await Assert.That(ReferenceEquals(model.Groups["original"].Resources["files"], model.Groups["mirrors"].Resources["files"])).IsTrue();
        await Assert.That(model.Source.RootElement.GetProperty("$include").GetString())
            .IsEqualTo("https://never-fetch.invalid/captured.json#/model");
        await Assert.That(model.EffectiveModel.RootElement.GetProperty("groups").GetProperty("mirrors").TryGetProperty("ximportresources", out _)).IsFalse();
    }

    [Test]
    public async Task LocalIncludeExpansionHonorsLocalAndEarlierPrecedence()
    {
        var source = RegistryJson.Parse("""
            {"attributes":{"first":{"type":"integer","description":"first"},"second":{"type":"string"},
              "copy":{"$includes":["#/attributes/first","#/attributes/second"],"description":"local"}}}
            """);
        var resolved = RegistryJson.Parse("""
            {"attributes":{"first":{"type":"integer","description":"first"},"second":{"type":"string"},
              "copy":{"type":"integer","description":"local"}}}
            """);
        var model = RegistryModel.CompileCaptured(source, resolved);

        await Assert.That(model.Attributes["copy"].Type).IsEqualTo(RegistryValueType.Integer);
        await Assert.That(model.Attributes["copy"].Description).IsEqualTo("local");
        await Assert.That(model.Source.RootElement.GetProperty("attributes").GetProperty("copy").GetProperty("$includes").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    [Arguments("""{"$include":"https://external.invalid/model","attributes":{"loop":{"$include":"#/attributes/loop"}}}""", "model_include_cycle")]
    [Arguments("""{"$include":"https://external.invalid/model","attributes":{"loop":{"$include":"#/missing"}}}""", "model_reference_not_found")]
    [Arguments("""{"description":"scalar","$include":"#/description"}""", "model_error")]
    [Arguments("""{"$include":"https://external.invalid/model#/%ff"}""", "invalid_json_pointer")]
    [Arguments("""{"$include":"https://external.invalid/model#/a~2b"}""", "invalid_json_pointer")]
    [Arguments("""{"$includes":[7]}""", "model_error")]
    public async Task InvalidOriginalReferencesFailEvenWhenResolvedMetadataIsValid(string original, string expectedCode)
    {
        var error = TestErrors.Capture(() => RegistryModel.CompileCaptured(RegistryJson.Parse(original), RegistryJson.Parse("{}")));
        await Assert.That(error.Code).IsEqualTo(expectedCode);
    }

    [Test]
    [Arguments("""{"description":"authored"}""", "{}", "/description")]
    [Arguments("{}", """{"description":"extra"}""", "")]
    [Arguments("""{"$includes":[]}""", """{"description":"extra"}""", "")]
    [Arguments("""{"attributes":{"x":{"type":"integer","enum":[1,2]}}}""", """{"attributes":{"x":{"type":"integer","enum":[2,1]}}}""", "/attributes/x/enum/0")]
    [Arguments("""{"attributes":{"x":{"type":"boolean","default":true}}}""", """{"attributes":{"x":{"type":"boolean","default":false}}}""", "/attributes/x/default")]
    public async Task IncludeFreeCapturesRetainAuthoredPresenceOrderingAndValueKinds(string original, string resolved, string path)
    {
        var error = TestErrors.Capture(() => RegistryModel.CompileCaptured(RegistryJson.Parse(original), RegistryJson.Parse(resolved)));
        await Assert.That(error.Code).IsEqualTo("model_error");
        await Assert.That(error.Path).IsEqualTo(path);
    }

    [Test]
    public async Task SourceEqualityUsesExactNumbersWithoutFloatingPointNarrowing()
    {
        var source = RegistryJson.Parse("""{"attributes":{"x":{"type":"integer","required":true,"default":9007199254740993}}}""");
        var equal = RegistryJson.Parse("""{"attributes":{"x":{"type":"integer","required":true,"default":9007199254740993.0}}}""");
        var model = RegistryModel.CompileCaptured(source, equal);
        await Assert.That(model.Attributes["x"].DefaultValue.GetRawText()).IsEqualTo("9007199254740993.0");
        var different = RegistryJson.Parse("""{"attributes":{"x":{"type":"integer","required":true,"default":9007199254740992}}}""");
        await Assert.That(TestErrors.Capture(() => RegistryModel.CompileCaptured(source, different)).Path)
            .IsEqualTo("/attributes/x/default");
    }

    [Test]
    public async Task LocalChainDepthIsCheckedIndependentlyOfJsonDepth()
    {
        var source = RegistryJson.Parse("""
            {"attributes":{"a":{"$include":"#/attributes/b"},"b":{"$include":"#/attributes/c"},"c":{"type":"string"}}}
            """);
        var resolved = RegistryJson.Parse("""{"attributes":{"a":{"type":"string"},"b":{"type":"string"},"c":{"type":"string"}}}""");
        var model = RegistryModel.CompileCaptured(source, resolved, new() { MaxIncludeDepth = 2 });
        await Assert.That(model.Attributes["a"].Type).IsEqualTo(RegistryValueType.String);
        await Assert.That(TestErrors.Capture(() => RegistryModel.CompileCaptured(source, resolved,
            new() { MaxIncludeDepth = 1 })).Code).IsEqualTo("model_expansion_limit");
    }

    [Test]
    public async Task ExcessiveLocalReferenceChainsStopAtTheFirstDisallowedHop()
    {
        var attributes = new JsonObject();
        for (var index = 0; index < 128; index++)
        {
            attributes["a" + index] = index == 127 ? JsonNode.Parse("""{"type":"string"}""") :
                new JsonObject { ["$include"] = "#/attributes/a" + (index + 1) };
        }
        var source = RegistryJson.Parse(new JsonObject { ["attributes"] = attributes }.ToJsonString());
        var error = TestErrors.Capture(() => RegistryModel.CompileCaptured(source, RegistryJson.Parse("{}"),
            new() { MaxIncludeDepth = 2 }));

        await Assert.That(error.Code).IsEqualTo("model_expansion_limit");
        await Assert.That(error.Path).IsEqualTo("/attributes/a2/$include");
    }

    [Test]
    public async Task CapturedValidationWorkLimitIsInclusiveAndCannotBeBypassedByAnEmptyModel()
    {
        var empty = RegistryJson.Parse("{}");
        var model = RegistryModel.CompileCaptured(empty, empty, new() { MaxExpandedNodes = 5 });
        await Assert.That(model.Groups.Count).IsEqualTo(0);
        await Assert.That(model.Attributes.ContainsKey("registryid")).IsTrue();
        await Assert.That(TestErrors.Capture(() => RegistryModel.CompileCaptured(empty, empty,
            new() { MaxExpandedNodes = 4 })).Code).IsEqualTo("model_expansion_limit");
    }

    [Test]
    public async Task OriginalSourceByteBudgetIsCheckedEvenWhenTheResolvedModelIsSmall()
    {
        var text = """{"$include":"https://capture.example/""" + new string('a', 32768) + "\"}";
        var bytes = Encoding.UTF8.GetByteCount(text);
        var source = RegistryJson.Parse(text);
        var resolved = RegistryJson.Parse("{}");
        var model = RegistryModel.CompileCaptured(source, resolved, new() { JsonLimits = new() { MaxBytes = bytes } });
        await Assert.That(model.Source.RootElement.GetRawText()).IsEqualTo(text);
        await Assert.That(TestErrors.Capture(() => RegistryModel.CompileCaptured(source, resolved,
            new() { JsonLimits = new() { MaxBytes = bytes - 1 } })).Code).IsEqualTo("byte_limit");
    }

    [Test]
    public async Task ExplicitOriginalDocumentUrisUseTheCapturedSourceBaseForLocalReferenceIdentity()
    {
        var source = RegistryJson.Parse("""
            {"attributes":{"original":{"type":"string"},"copy":{"$include":"root.json#/attributes/original"}}}
            """);
        var resolved = RegistryJson.Parse("""{"attributes":{"original":{"type":"string"},"copy":{"type":"integer"}}}""");
        var error = TestErrors.Capture(() => RegistryModel.CompileCaptured(source, resolved,
            new() { SourceUri = new Uri("https://capture.example/root.json") }));
        await Assert.That(error.Code).IsEqualTo("model_error");
        await Assert.That(error.Path).IsEqualTo("/attributes/copy/type");
    }

    [Test]
    public async Task ResolvedMaterialMustBeSelfContainedAndAValidModel()
    {
        var original = RegistryJson.Parse("""{"$include":"https://never-fetch.invalid/model"}""");
        await Assert.That(TestErrors.Capture(() => RegistryModel.CompileCaptured(original,
            RegistryJson.Parse("""{"attributes":{"x":{"$include":"again.json"}}}"""))).Code).IsEqualTo("model_error");
        await Assert.That(TestErrors.Capture(() => RegistryModel.CompileCaptured(original,
            RegistryJson.Parse("""{"attributes":{"x":{"type":"not-a-core-type"}}}"""))).Code).IsEqualTo("model_error");
    }

    [Test]
    public async Task NoExternalResolverIsAcceptedOrInvokedAndPreCancellationStopsCompilation()
    {
        var source = RegistryJson.Parse("""{"$include":"https://never-fetch.invalid/model"}""");
        var resolved = RegistryJson.Parse("{}");
        var resolver = new NoAcquisition();
        await Assert.That(() => RegistryModel.CompileCaptured(source, resolved, new() { Resolver = resolver }))
            .Throws<ArgumentException>();
        await Assert.That(resolver.Calls).IsEqualTo(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => RegistryModel.CompileCaptured(source, resolved, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(() => RegistryModel.CompileCaptured(null!, resolved)).Throws<ArgumentNullException>();
        await Assert.That(() => RegistryModel.CompileCaptured(source, null!)).Throws<ArgumentNullException>();
    }

    private sealed class NoAcquisition : IRegistryModelResolver
    {
        internal int Calls { get; private set; }

        public RegistryJson Resolve(Uri documentUri)
        {
            Calls++;
            throw new InvalidOperationException("Captured model compilation must never acquire external content.");
        }
    }
}
