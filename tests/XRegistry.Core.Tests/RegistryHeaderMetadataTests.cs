using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Http;

namespace XRegistry.Core.Tests;

public class RegistryHeaderMetadataTests
{
    private const string Path = "/teams/g/files/f";
    private static readonly RegistryResourceDefinition s_resource = Resource("""
        {"rank":{"type":"integer"},"amount":{"type":"decimal"},"enabled":{"type":"boolean"},
         "scores":{"type":"map","item":{"type":"uinteger"}},"anyvalue":{"type":"any"},
         "anymap":{"type":"map","item":{"type":"any"}},
         "nested":{"type":"object","attributes":{"value":{"type":"string"}}},
         "items":{"type":"array","item":{"type":"string"}},
         "complexmap":{"type":"map","item":{"type":"object","attributes":{"value":{"type":"string"}}}},
         "readonlyvalue":{"type":"string","readonly":true}}
        """);

    [Test]
    public async Task ClientEncodingMatchesLiteralUtf8ControlPercentAndExactNumberHeaders()
    {
        var headers = Write("""
            {"name":"Euro \u20ac \ud83d\ude00 \r\n\u0000\t\"%",
             "epoch":184467440737095516160001,"rank":-184467440737095516160001,
             "amount":1.2345678901234567890123456789,"enabled":false,
             "labels":{"a.b":"one% two","empty":""},"contenttype":"application/octet-stream"}
            """);

        await Assert.That(headers.Count).IsEqualTo(8);
        await Assert.That(headers["xRegistry-name"]).IsEqualTo("Euro%20%E2%82%AC%20%F0%9F%98%80%20%0D%0A%00%09%22%25");
        await Assert.That(headers["xRegistry-epoch"]).IsEqualTo("184467440737095516160001");
        await Assert.That(headers["xRegistry-rank"]).IsEqualTo("-184467440737095516160001");
        await Assert.That(headers["xRegistry-amount"]).IsEqualTo("1.2345678901234567890123456789");
        await Assert.That(headers["xRegistry-enabled"]).IsEqualTo("false");
        await Assert.That(headers["xRegistry-labels.a.b"]).IsEqualTo("one%25%20two");
        await Assert.That(headers["XREGISTRY-LABELS.EMPTY"]).IsEqualTo("");
        await Assert.That(headers["Content-Type"]).IsEqualTo("application/octet-stream");
        await Assert.That(headers.ContainsKey("xRegistry-contenttype")).IsFalse();
    }

    [Test]
    public async Task RawResponseHeadersRetainReadonlyIdentityAndExactTypedValues()
    {
        var metadata = Read(
            ("XREGISTRY-XID", "%2Fteams%2Fg%2Ffiles%2Ff"),
            ("xRegistry-versionid", "version-1"),
            ("xRegistry-fileid", "f"),
            ("xRegistry-epoch", "184467440737095516160001"),
            ("xRegistry-rank", "-184467440737095516160001"),
            ("xRegistry-amount", "1.2345678901234567890123456789"),
            ("xRegistry-scores.a.b", "184467440737095516160001"),
            ("xRegistry-isdefault", "true"),
            ("xRegistry-readonlyvalue", "locked"),
            ("xRegistry-versionscount", "2"),
            ("xRegistry-xregcorrelationid", "opaque-correlation"),
            ("xRegistry-count", "2"),
            ("xRegistry-commit-outcome", "unknown"),
            ("Content-Type", "application/example; profile=\"a%20b\""));

        await Assert.That(metadata.RootElement.GetProperty("xid").GetString()).IsEqualTo(Path);
        await Assert.That(metadata.RootElement.GetProperty("versionid").GetString()).IsEqualTo("version-1");
        await Assert.That(metadata.RootElement.GetProperty("fileid").GetString()).IsEqualTo("f");
        await Assert.That(metadata.RootElement.GetProperty("epoch").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("rank").GetRawText()).IsEqualTo("-184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("amount").GetRawText()).IsEqualTo("1.2345678901234567890123456789");
        await Assert.That(metadata.RootElement.GetProperty("scores").GetProperty("a.b").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(metadata.RootElement.GetProperty("readonlyvalue").GetString()).IsEqualTo("locked");
        await Assert.That(metadata.RootElement.GetProperty("versionscount").GetInt32()).IsEqualTo(2);
        await Assert.That(metadata.GetPresence("xregcorrelationid")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(metadata.GetPresence("count")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(metadata.GetPresence("commit-outcome")).IsEqualTo(JsonPresence.Absent);
        await Assert.That(metadata.RootElement.GetProperty("contenttype").GetString()).IsEqualTo("application/example; profile=\"a%20b\"");
    }

    [Test]
    [Arguments("\"a\\\"b\\\\c%20d\"", "a\"b\\c d")]
    [Arguments("Euro%20%e2%82%ac%20%f0%9f%98%80", "Euro \u20ac \U0001f600")]
    [Arguments("%2520", "%20")]
    [Arguments("%0D%0A%00%09", "\r\n\0\t")]
    [Arguments("%41", "A")]
    public async Task RawValuesUseLegacyUnquotingAndExactlyOnePercentDecode(string wire, string expected)
    {
        var metadata = Read(("xRegistry-name", wire), ("xRegistry-anyvalue", wire));
        await Assert.That(metadata.RootElement.GetProperty("name").GetString()).IsEqualTo(expected);
        await Assert.That(metadata.RootElement.GetProperty("anyvalue").GetString()).IsEqualTo(expected);
    }

    [Test]
    public async Task NullEmptyAndAbsentRemainDistinctForClientInput()
    {
        var headers = Write("""{"name":null,"description":"","labels":null}""");
        await Assert.That(headers.Count).IsEqualTo(3);
        await Assert.That(headers["xRegistry-name"]).IsEqualTo("null");
        await Assert.That(headers["xRegistry-description"]).IsEqualTo("");
        await Assert.That(headers["xRegistry-labels"]).IsEqualTo("null");
        await Assert.That(headers.ContainsKey("xRegistry-icon")).IsFalse();

        var decoded = RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-name", "null"), ("xRegistry-description", ""), ("xRegistry-labels", "%6Eull")),
            s_resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(decoded.GetPresence("name")).IsEqualTo(JsonPresence.Null);
        await Assert.That(decoded.GetPresence("labels")).IsEqualTo(JsonPresence.Null);
        await Assert.That(decoded.GetPresence("description")).IsEqualTo(JsonPresence.Value);
        await Assert.That(decoded.RootElement.GetProperty("description").GetString()).IsEqualTo("");
        await Assert.That(decoded.GetPresence("icon")).IsEqualTo(JsonPresence.Absent);
    }

    [Test]
    [Arguments("""{"name":"null"}""", "xRegistry-name")]
    [Arguments("""{"labels":{"a":"null"}}""", "xRegistry-labels.a")]
    [Arguments("""{"anyvalue":"null"}""", "xRegistry-anyvalue")]
    [Arguments("""{"anymap":{"a":"null"}}""", "xRegistry-anymap.a")]
    public async Task LiteralNullStringsCannotBeSentAsDeletions(string json, string name)
    {
        var failure = Reject(() => Write(json));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(Path);
        await Assert.That(Arguments(failure)["name"]).IsEqualTo(name);
        await Assert.That(failure.Diagnostic.Message).IsEqualTo(
            "The literal string null is indistinguishable from deletion in a request header; use a metadata-body request.");
    }

    [Test]
    [Arguments("null")]
    [Arguments("\"null\"")]
    [Arguments("%6eull")]
    public async Task ResponseStringNullIsLiteralButRequestNullIsDeletion(string wire)
    {
        var response = Read(("xRegistry-name", wire), ("xRegistry-labels.a", wire));
        var request = RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-name", wire), ("xRegistry-labels.a", wire)), s_resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(response.RootElement.GetProperty("name").GetString()).IsEqualTo("null");
        await Assert.That(response.RootElement.GetProperty("labels").GetProperty("a").GetString()).IsEqualTo("null");
        await Assert.That(request.GetPresence("name")).IsEqualTo(JsonPresence.Null);
        await Assert.That(request.RootElement.GetProperty("labels").GetProperty("a").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(Write("""{"name":"null"}""", RegistryHeaderMetadataDirection.Response)["xRegistry-name"]).IsEqualTo("null");
    }

    [Test]
    [Arguments("Null", "Null")]
    [Arguments("NULL", "NULL")]
    [Arguments("null ", "null%20")]
    public async Task RequestDeletionSentinelIsCaseSensitiveAndDoesNotTrimEncodedData(string value, string wire)
    {
        var headers = Write("{\"name\":\"" + value + "\"}");
        await Assert.That(headers["xRegistry-name"]).IsEqualTo(wire);
        var metadata = RegistryHeaderMetadata.Decode(Fields(("xRegistry-name", wire)), s_resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(metadata.GetPresence("name")).IsEqualTo(JsonPresence.Value);
        await Assert.That(metadata.RootElement.GetProperty("name").GetString()).IsEqualTo(value);
    }

    [Test]
    public async Task EmptyMapsRequireMetadataBodyRatherThanBecomingAbsent()
    {
        var failure = Reject(() => Write("""{"labels":{}}"""));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo("xRegistry-labels");
        await Assert.That(Write("{}").Count).IsEqualTo(0);
        await Assert.That(Write("""{"labels":{}}""", RegistryHeaderMetadataDirection.Response).Count).IsEqualTo(0);
        await Assert.That(Write("""{"labels":null}""")["xRegistry-labels"]).IsEqualTo("null");
    }

    [Test]
    [Arguments("""{"labels":{"":"value"}}""", "xRegistry-labels.")]
    [Arguments("""{"labels":{"a":"first","A":"second"}}""", "xRegistry-labels.A")]
    [Arguments("""{"labels":{"a:b":"value"}}""", "xRegistry-labels.a:b")]
    [Arguments("""{"labels":{"a b":"value"}}""", "xRegistry-labels.a b")]
    [Arguments("""{"labels":{"a\r\nb":"value"}}""", "xRegistry-labels.a\r\nb")]
    [Arguments("""{"labels":{"\u20ac":"value"}}""", "xRegistry-labels.\u20ac")]
    public async Task ClientMapNamesRejectEmptyNonTokenAndCaseCollisions(string json, string name)
    {
        var failure = Reject(() => Write(json));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo(name);
    }

    [Test]
    [Arguments("xRegistry-labels.", "value", "xRegistry-name", "other", "xRegistry-labels.")]
    [Arguments("xRegistry-labels.a", "first", "XREGISTRY-LABELS.A", "second", "XREGISTRY-LABELS.A")]
    [Arguments("xRegistry-labels", "null", "xRegistry-labels.a", "value", "xRegistry-labels.a")]
    [Arguments("xRegistry-labels.a", "value", "xRegistry-labels", "null", "xRegistry-labels")]
    [Arguments("xRegistry-name", "first", "XREGISTRY-NAME", "second", "XREGISTRY-NAME")]
    [Arguments("xRegistry-labels.a:b", "value", "xRegistry-name", "other", "xRegistry-labels.a:b")]
    public async Task RawMapAndScalarNamesRejectDuplicatesAndMixedDeletion(
        string first, string firstValue, string second, string secondValue, string invalidName)
    {
        var failure = Reject(() => RegistryHeaderMetadata.Decode(
            Fields((first, firstValue), (second, secondValue)), s_resource, RegistryHeaderMetadataDirection.ClientInput));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo(invalidName);
    }

    [Test]
    public async Task DotsAndPercentSignsInMapNamesAreNotDecodedOrSplitAgain()
    {
        var metadata = Read(("xRegistry-labels.a.b.c", "one"), ("xRegistry-labels.a%2Eb", "two"));
        var labels = metadata.RootElement.GetProperty("labels");
        await Assert.That(labels.EnumerateObject().Count()).IsEqualTo(2);
        await Assert.That(labels.GetProperty("a.b.c").GetString()).IsEqualTo("one");
        await Assert.That(labels.GetProperty("a%2Eb").GetString()).IsEqualTo("two");
    }

    [Test]
    [Arguments("%")]
    [Arguments("%GG")]
    [Arguments("%C0%A0")]
    [Arguments("%ED%A0%80")]
    [Arguments("%F4%90%80%80")]
    [Arguments("%E2%82")]
    [Arguments("\"unterminated")]
    [Arguments("\"a\"suffix")]
    [Arguments("raw\"quote")]
    [Arguments("raw\r\n folded")]
    [Arguments("\u20ac")]
    public async Task InvalidWireValuesRetainExistingHeaderDiagnostics(string wire)
    {
        var failure = Reject(() => RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-name", wire)), s_resource, RegistryHeaderMetadataDirection.ClientInput, path: Path));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(Path);
        await Assert.That(failure.Diagnostic.Message).IsEqualTo("An attribute header has invalid quoting or percent encoding.");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo("xRegistry-name");
        await Assert.That(failure.Data["xregistry.cause"]).IsEqualTo("FormatException");
    }

    [Test]
    public async Task InvalidUnicodeCannotBecomeReplacementCharactersInHeaders()
    {
        var high = Reject(() => Read(("xRegistry-name", "\uD800")));
        var low = Reject(() => Read(("xRegistry-name", "\uDC00")));
        await Assert.That(high.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(low.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Reject(() => Write("""{"name":"\uD800"}""")).Diagnostic.Code).IsEqualTo("invalid_unicode");
        await Assert.That(Reject(() => Write("""{"name":"\uDC00"}""")).Diagnostic.Code).IsEqualTo("invalid_unicode");
    }

    [Test]
    [Arguments("epoch", "\"42\"")]
    [Arguments("epoch", "-1")]
    [Arguments("epoch", "1.1")]
    [Arguments("rank", "false")]
    [Arguments("amount", "\"1.25\"")]
    [Arguments("enabled", "\"true\"")]
    [Arguments("name", "1")]
    [Arguments("anyvalue", "42")]
    [Arguments("anyvalue", "{}")]
    [Arguments("nested", "{}")]
    [Arguments("nested", "\"text\"")]
    [Arguments("items", "[]")]
    [Arguments("complexmap", """{"a":{"value":"text"}}""")]
    [Arguments("labels", """{"a":{}}""")]
    [Arguments("scores", """{"a":-1}""")]
    [Arguments("anymap", """{"a":true}""")]
    public async Task ClientTypesNeverSilentlyChangeKindOrDropComplexValues(string attribute, string value)
    {
        var failure = Reject(() => Write("{\"" + attribute + "\":" + value + "}"));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(Path);
    }

    [Test]
    [Arguments("epoch", "-1")]
    [Arguments("epoch", "1.5")]
    [Arguments("rank", "1e-1")]
    public async Task RequestNumberSemanticsRemainForServerValidationButResponseNumbersAreTyped(string attribute, string wire)
    {
        var request = RegistryHeaderMetadata.Decode(Fields(("xRegistry-" + attribute, wire)),
            s_resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(request.RootElement.GetProperty(attribute).GetRawText()).IsEqualTo(wire);
        await Assert.That(Reject(() => Read(("xRegistry-" + attribute, wire))).Diagnostic.Code).IsEqualTo("header_error");
    }

    [Test]
    [Arguments("epoch", "true")]
    [Arguments("enabled", "1")]
    [Arguments("enabled", "TRUE")]
    [Arguments("amount", "NaN")]
    [Arguments("rank", "1,2")]
    public async Task InvalidTypedRawScalarsNeverBecomeSuccessShapedDefaults(string attribute, string wire)
    {
        var failure = Reject(() => Read(("xRegistry-" + attribute, wire)));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo("xRegistry-" + attribute);
    }

    [Test]
    public async Task LegacyQuotingIsRemovedBeforeTypedNumberAndBooleanConversion()
    {
        var metadata = Read(("xRegistry-epoch", "\"184467440737095516160001\""),
            ("xRegistry-enabled", "\"true\""), ("xRegistry-rank", "%34%32"));
        await Assert.That(metadata.RootElement.GetProperty("epoch").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(metadata.RootElement.GetProperty("rank").GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task AnyHeadersUseStringFallbackAndComplexHeadersOnlyPermitExplicitDeletion()
    {
        var metadata = Read(("xRegistry-anyvalue", "42"), ("xRegistry-anymap.a", "true"));
        await Assert.That(metadata.RootElement.GetProperty("anyvalue").GetString()).IsEqualTo("42");
        await Assert.That(metadata.RootElement.GetProperty("anymap").GetProperty("a").GetString()).IsEqualTo("true");
        var headers = Write("""{"nested":null,"items":null,"complexmap":null}""");
        await Assert.That(headers.Count).IsEqualTo(3);
        await Assert.That(headers.Values.All(static value => value == "null")).IsTrue();
        var deleted = RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-nested", "null"), ("xRegistry-items", "null")), s_resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(deleted.GetPresence("nested")).IsEqualTo(JsonPresence.Null);
        await Assert.That(deleted.GetPresence("items")).IsEqualTo(JsonPresence.Null);
        await Assert.That(Reject(() => Read(("xRegistry-items", "[]"))).Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Reject(() => Read(("xRegistry-complexmap.a", "{}"))).Diagnostic.Code).IsEqualTo("header_error");
    }

    [Test]
    public async Task ReadonlyInputIsIgnoredExceptIdentityAndEpochGuards()
    {
        var headers = Write("""
            {"self":{"invalid":true},"xid":null,"isdefault":"invalid","readonlyvalue":42,
             "epoch":184467440737095516160001,"versionid":"v1","fileid":"f"}
            """);
        await Assert.That(headers.Count).IsEqualTo(3);
        await Assert.That(headers["xRegistry-epoch"]).IsEqualTo("184467440737095516160001");
        await Assert.That(headers["xRegistry-versionid"]).IsEqualTo("v1");
        await Assert.That(headers["xRegistry-fileid"]).IsEqualTo("f");
        var request = RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-isdefault", "%invalid"), ("xRegistry-xid", "%invalid"), ("xRegistry-fileid", "wrong"),
                ("xRegistry-versionid", "v1"), ("xRegistry-epoch", "0")),
            s_resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(request.RootElement.EnumerateObject().Count()).IsEqualTo(3);
        await Assert.That(request.RootElement.GetProperty("fileid").GetString()).IsEqualTo("wrong");
        await Assert.That(request.RootElement.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(request.RootElement.GetProperty("epoch").GetInt32()).IsEqualTo(0);
        await Assert.That(Reject(() => Read(("xRegistry-isdefault", "%invalid"))).Diagnostic.Code).IsEqualTo("header_error");
    }

    [Test]
    [Arguments("xRegistry-file")]
    [Arguments("xRegistry-filebase64")]
    [Arguments("xRegistry-file.member")]
    [Arguments("xRegistry-contenttype")]
    public async Task ForbiddenHeadersRetainTheExistingExtraHeaderContract(string name)
    {
        var failure = Reject(() => RegistryHeaderMetadata.Decode(
            Fields((name, "null")), s_resource, RegistryHeaderMetadataDirection.ClientInput, path: Path));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("extra_xregistry_header");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(Path);
        await Assert.That(failure.Diagnostic.Message).IsEqualTo("This attribute must not be sent as an xRegistry header.");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo(name);
    }

    [Test]
    [Arguments("file")]
    [Arguments("filebase64")]
    public async Task InlineDocumentMetadataIsRejectedEvenWhenNull(string name)
    {
        var failure = Reject(() => Write("{\"" + name + "\":null}"));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("extra_xregistry_header");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo("xRegistry-" + name);
        await Assert.That(Write("{\"" + name + "\":\"ignored\"}", RegistryHeaderMetadataDirection.Response).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ContentTypeIsMappedWithoutPercentEncodingAndNullMeansOmission()
    {
        var headers = Write("""{"contenttype":"application/example; profile=\"a%20b\""}""");
        await Assert.That(headers.Count).IsEqualTo(1);
        await Assert.That(headers["Content-Type"]).IsEqualTo("application/example; profile=\"a%20b\"");
        await Assert.That(Write("""{"contenttype":null}""").Count).IsEqualTo(0);
        await Assert.That(Read().GetPresence("contenttype")).IsEqualTo(JsonPresence.Absent);
    }

    [Test]
    [Arguments("")]
    [Arguments("not a media type")]
    [Arguments("text/plain\r\nHost: other")]
    [Arguments("text/plain; title=\"\u20ac\"")]
    public async Task InvalidContentTypeRetainsTheExistingDiagnostic(string value)
    {
        var failure = Reject(() => Read(("Content-Type", value)));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(failure.Diagnostic.Message).IsEqualTo("The Document Content-Type cannot be represented as an HTTP media type.");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo("Content-Type");
    }

    [Test]
    [Arguments("xRegistry-name")]
    [Arguments("xRegistry-labels.a")]
    [Arguments("Content-Type")]
    public async Task RepeatedOrMissingFieldValuesCannotBeCombinedIntoOneAttribute(string name)
    {
        var repeated = Reject(() => RegistryHeaderMetadata.Decode(
            [new(name, ["text/plain", "text/html"])], s_resource, RegistryHeaderMetadataDirection.Response));
        var missing = Reject(() => RegistryHeaderMetadata.Decode(
            [new(name, Array.Empty<string>())], s_resource, RegistryHeaderMetadataDirection.Response));
        await Assert.That(repeated.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(missing.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Arguments(repeated)["name"]).IsEqualTo(name);
        await Assert.That(Arguments(missing)["name"]).IsEqualTo(name);
    }

    [Test]
    public async Task AggregateHeaderBytesIncludeNamesEscapesAndFramingAtTheExactBoundary()
    {
        var limits = new RegistryHeaderMetadataOptions { MaxHeaderBytes = 49, MaxHeaderCount = 2 };
        var headers = Write("""{"name":"\u20ac","labels":{"k":""}}""", options: limits);
        await Assert.That(headers.Count).IsEqualTo(2);
        await Assert.That(headers["xRegistry-name"]).IsEqualTo("%E2%82%AC");
        await Assert.That(headers["xRegistry-labels.k"]).IsEqualTo("");
        var atBoundary = RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-name", "%E2%82%AC"), ("xRegistry-labels.k", "")),
            s_resource, RegistryHeaderMetadataDirection.Response, limits);
        await Assert.That(atBoundary.RootElement.GetProperty("name").GetString()).IsEqualTo("\u20ac");

        await Assert.That(Reject(() => Write("""{"name":"\u20aca","labels":{"k":""}}""", options: limits)).Diagnostic.Code)
            .IsEqualTo("request_headers_too_large");
        await Assert.That(Reject(() => Write("""{"name":"\u20ac","labels":{"k":""}}""",
            options: limits with { MaxHeaderBytes = 48 })).Diagnostic.Code).IsEqualTo("request_headers_too_large");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-name", "%E2%82%ACa"), ("xRegistry-labels.k", "")),
            s_resource, RegistryHeaderMetadataDirection.Response, limits)).Diagnostic.Code).IsEqualTo("too_large");
    }

    [Test]
    public async Task ContentTypeAndUnrelatedRawHeadersStillConsumeTheirAggregateBudgets()
    {
        await Assert.That(Write("""{"name":"a","contenttype":"application/octet-stream"}""",
            options: new() { MaxHeaderBytes = 59 }).Count).IsEqualTo(2);
        await Assert.That(Reject(() => Write("""{"name":"a","contenttype":"application/octet-stream"}""",
            options: new() { MaxHeaderBytes = 58 })).Diagnostic.Code).IsEqualTo("request_headers_too_large");
        var headers = Fields(("xRegistry-name", "a"), ("Other", "b"));
        var decoded = RegistryHeaderMetadata.Decode(headers, s_resource, RegistryHeaderMetadataDirection.Response,
            new() { MaxHeaderBytes = 29, MaxHeaderCount = 2 });
        await Assert.That(decoded.RootElement.GetProperty("name").GetString()).IsEqualTo("a");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(headers, s_resource, RegistryHeaderMetadataDirection.Response,
            new() { MaxHeaderBytes = 28 })).Diagnostic.Code).IsEqualTo("too_large");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(headers, s_resource, RegistryHeaderMetadataDirection.Response,
            new() { MaxHeaderCount = 1 })).Diagnostic.Code).IsEqualTo("too_large");
        await Assert.That(Reject(() => Write("""{"name":"a","description":"b"}""",
            options: new() { MaxHeaderCount = 1 })).Diagnostic.Code).IsEqualTo("request_headers_too_large");
        await Assert.That(Write("{}", options: new() { MaxHeaderBytes = 0, MaxHeaderCount = 0 }).Count).IsEqualTo(0);
    }

    [Test]
    public async Task JsonByteNodeAndDepthLimitsApplyToBothDirectionsWithoutDroppingData()
    {
        var limits = new RegistryHeaderMetadataOptions { Json = new() { MaxBytes = 12, MaxNodes = 2, MaxDepth = 1 } };
        await Assert.That(Write("""{"name":"x"}""", options: limits)["xRegistry-name"]).IsEqualTo("x");
        await Assert.That(RegistryHeaderMetadata.Decode(Fields(("xRegistry-name", "x")), s_resource,
            RegistryHeaderMetadataDirection.Response, limits).RootElement.GetProperty("name").GetString()).IsEqualTo("x");
        await Assert.That(Reject(() => Write("""{"name":"x"}""",
            options: limits with { Json = limits.Json with { MaxBytes = 11 } })).Diagnostic.Code).IsEqualTo("byte_limit");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-name", "x")), s_resource,
            RegistryHeaderMetadataDirection.Response, limits with { Json = limits.Json with { MaxBytes = 11 } })).Diagnostic.Code)
            .IsEqualTo("byte_limit");
        await Assert.That(Reject(() => Write("""{"name":"x"}""",
            options: limits with { Json = limits.Json with { MaxNodes = 1 } })).Diagnostic.Code).IsEqualTo("node_limit");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-labels.k", "v")), s_resource,
            RegistryHeaderMetadataDirection.Response, new() { Json = new() { MaxNodes = 2 } })).Diagnostic.Code).IsEqualTo("node_limit");
        await Assert.That(Reject(() => Write("""{"labels":{"k":"v"}}""",
            options: new() { Json = new() { MaxDepth = 1 } })).Diagnostic.Code).IsEqualTo("depth_limit");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-labels.k", "v")), s_resource,
            RegistryHeaderMetadataDirection.Response, new() { Json = new() { MaxDepth = 1 } })).Diagnostic.Code).IsEqualTo("depth_limit");
    }

    [Test]
    public async Task ExactNumbersRetainLexemesAndObeyNumberBudgets()
    {
        var limits = new RegistryHeaderMetadataOptions { Json = new() { MaxNumberCharacters = 5, MaxNumberExponent = 2 } };
        var headers = Write("""{"epoch":1.0e2,"amount":-0.00}""", options: limits);
        await Assert.That(headers["xRegistry-epoch"]).IsEqualTo("1.0e2");
        await Assert.That(headers["xRegistry-amount"]).IsEqualTo("-0.00");
        await Assert.That(Reject(() => Write("""{"epoch":123456}""", options: limits)).Diagnostic.Code).IsEqualTo("number_limit");
        await Assert.That(Reject(() => Write("""{"epoch":1e3}""", options: limits)).Diagnostic.Code).IsEqualTo("number_limit");
        var failure = Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-epoch", "123456")),
            s_resource, RegistryHeaderMetadataDirection.Response, limits));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(failure.Data["xregistry.cause"]).IsEqualTo("number_limit");
    }

    [Test]
    public async Task DynamicWildcardMapsReuseTheSameModelLookupInBothDirections()
    {
        var resource = Resource("""{"*":{"type":"map","item":{"type":"string"}}}""");
        var metadata = RegistryJson.Parse("""{"extension":{"a.b":"v"}}""");
        foreach (var direction in new[] { RegistryHeaderMetadataDirection.ClientInput, RegistryHeaderMetadataDirection.Response })
        {
            var headers = RegistryHeaderMetadata.Encode(metadata, resource, direction);
            await Assert.That(headers["xRegistry-extension.a.b"]).IsEqualTo("v");
            var decoded = RegistryHeaderMetadata.Decode(Fields(("xRegistry-extension.a.b", "v")), resource, direction);
            await Assert.That(decoded.RootElement.GetProperty("extension").GetProperty("a.b").GetString()).IsEqualTo("v");
        }
    }

    [Test]
    public async Task ResponseProjectionNeverSerializesComplexAnyMapMembersAsScalarHeaders()
    {
        var headers = Write("""{"anymap":{"a":{},"b":"scalar"},"anyvalue":{},"items":[],"name":"kept"}""",
            RegistryHeaderMetadataDirection.Response);
        await Assert.That(headers.Count).IsEqualTo(1);
        await Assert.That(headers["xRegistry-name"]).IsEqualTo("kept");
    }

    [Test]
    public async Task ResponseControlHeadersAreNotCapturedByWildcardMapDefinitions()
    {
        var resource = Resource("""{"*":{"type":"map","item":{"type":"string"}}}""");
        var metadata = RegistryHeaderMetadata.Decode(Fields(
            ("xRegistry-xregcorrelationid", "opaque"), ("xRegistry-count", "2"), ("xRegistry-commit-outcome", "unknown"),
            ("xRegistry-extension.a", "kept")), resource, RegistryHeaderMetadataDirection.Response);
        await Assert.That(metadata.RootElement.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(metadata.RootElement.GetProperty("extension").GetProperty("a").GetString()).IsEqualTo("kept");
    }

    [Test]
    public async Task UnknownAttributesAndInvalidOptionsAreExplicitFailures()
    {
        await Assert.That(Reject(() => Write("""{"unknown":"v"}""")).Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Reject(() => Read(("xRegistry-unknown", "v"))).Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(() => Write("[]")).Throws<ArgumentException>();
        await Assert.That(() => Write("{}", options: new() { MaxHeaderBytes = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Write("{}", options: new() { MaxHeaderCount = -1 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Write("{}", options: new() { Json = null! })).Throws<ArgumentNullException>();
        await Assert.That(() => Write("{}", options: new() { Json = new() { MaxDepth = 257 } })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Write("{}", (RegistryHeaderMetadataDirection)42)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConditionalScalarHeadersUseTheActiveModelRegardlessOfFieldOrder(bool discriminatorFirst)
    {
        var resource = Resource("""
            {"kind":{"type":"string","ifvalues":{"counter":{"siblingattributes":{"reading":{"type":"integer"}}}}}}
            """);
        var valid = RegistryMetadataValidator.Validate(
            RegistryJson.Parse("""{"kind":"cOuNtEr","reading":184467440737095516160001}"""), resource.Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput });
        await Assert.That(valid.Metadata.RootElement.GetProperty("reading").GetRawText()).IsEqualTo("184467440737095516160001");
        var fields = discriminatorFirst
            ? Fields(("xRegistry-kind", "%63OuNtEr"), ("xRegistry-reading", "184467440737095516160001"))
            : Fields(("xRegistry-reading", "184467440737095516160001"), ("xRegistry-kind", "%63OuNtEr"));

        var metadata = RegistryHeaderMetadata.Decode(fields, resource, RegistryHeaderMetadataDirection.ClientInput);

        await Assert.That(metadata.RootElement.GetProperty("kind").GetString()).IsEqualTo("cOuNtEr");
        await Assert.That(metadata.RootElement.GetProperty("reading").GetRawText()).IsEqualTo("184467440737095516160001");
    }

    [Test]
    [Arguments(RegistryHeaderMetadataDirection.ClientInput)]
    [Arguments(RegistryHeaderMetadataDirection.Response)]
    public async Task ConditionalMapWritesUseTheActiveDefinitionBeforeTheWildcard(RegistryHeaderMetadataDirection direction)
    {
        var resource = Resource("""
            {"kind":{"type":"string","ifvalues":{"counter":{"siblingattributes":{
                "readings":{"type":"map","item":{"type":"uinteger"}}}}}},"*":{"type":"string"}}
            """);
        var input = RegistryJson.Parse("""{"readings":{"a.b":184467440737095516160001,"zero":0},"kind":"counter"}""");
        var validated = RegistryMetadataValidator.Validate(input, resource.Attributes, new() { Mode = RegistryMetadataMode.ClientInput });
        await Assert.That(validated.Metadata.RootElement.GetProperty("readings").GetProperty("zero").GetInt32()).IsEqualTo(0);

        var headers = RegistryHeaderMetadata.Encode(input, resource, direction);

        await Assert.That(headers.Count).IsEqualTo(3);
        await Assert.That(headers["xRegistry-readings.a.b"]).IsEqualTo("184467440737095516160001");
        await Assert.That(headers["xRegistry-readings.zero"]).IsEqualTo("0");
        await Assert.That(headers["xRegistry-kind"]).IsEqualTo("counter");
    }

    [Test]
    [Arguments("request")]
    [Arguments("request-headers")]
    [Arguments("response-headers")]
    public async Task ConditionalMissingDiscriminatorNeverGuessesTheWildcardType(string operation)
    {
        var resource = Resource("""
            {"kind":{"type":"string","ifvalues":{"counter":{"siblingattributes":{"reading":{"type":"integer"}}}}},
             "*":{"type":"string"}}
            """);
        var failure = Reject(() =>
        {
            if (operation == "request")
            {
                RegistryHeaderMetadata.Encode(RegistryJson.Parse("""{"reading":"42"}"""), resource, RegistryHeaderMetadataDirection.ClientInput);
            }
            else
            {
                RegistryHeaderMetadata.Decode(Fields(("xRegistry-reading", "42")), resource,
                    operation == "request-headers" ? RegistryHeaderMetadataDirection.ClientInput : RegistryHeaderMetadataDirection.Response);
            }
        });

        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Arguments(failure)["name"]).IsEqualTo("xRegistry-reading");
        await Assert.That(failure.Diagnostic.Message).IsEqualTo(
            "A conditional header needs an explicit discriminator context; a discriminator is absent or ignored.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConditionalAbsentContentTypeMeansClearedRatherThanUnknown(bool decode)
    {
        var resource = Resource("""
            {"contenttype":{"type":"string","ifvalues":{"application/octet-stream":{"siblingattributes":{"reading":{"type":"integer"}}}}},
             "*":{"type":"string"}}
            """);
        if (decode)
        {
            var metadata = RegistryHeaderMetadata.Decode(Fields(("xRegistry-reading", "cleared")), resource,
                RegistryHeaderMetadataDirection.ClientInput);
            await Assert.That(metadata.RootElement.GetProperty("reading").GetString()).IsEqualTo("cleared");
            await Assert.That(metadata.GetPresence("contenttype")).IsEqualTo(JsonPresence.Absent);
        }
        else
        {
            var headers = RegistryHeaderMetadata.Encode(RegistryJson.Parse("""{"reading":"cleared"}"""), resource,
                RegistryHeaderMetadataDirection.ClientInput);
            await Assert.That(headers.Count).IsEqualTo(1);
            await Assert.That(headers["xRegistry-reading"]).IsEqualTo("cleared");
        }
    }

    [Test]
    [Arguments(RegistryHeaderMetadataDirection.ClientInput)]
    [Arguments(RegistryHeaderMetadataDirection.Response)]
    public async Task ConditionalNestedHeadersKeepExactTypesMapsAndReadonlyRules(RegistryHeaderMetadataDirection direction)
    {
        var resource = NestedConditionalResource();
        var fields = Fields(
            ("xRegistry-reading", "1.2345678901234567890123456789"),
            ("xRegistry-marks.a.b", "true"),
            ("xRegistry-generated", direction == RegistryHeaderMetadataDirection.ClientInput ? "%invalid" : "184467440737095516160001"),
            ("xRegistry-precision", "%66ine"),
            ("xRegistry-mode", "\"SaMpLiNg\""));

        var metadata = RegistryHeaderMetadata.Decode(fields, resource, direction);

        await Assert.That(metadata.RootElement.GetProperty("mode").GetString()).IsEqualTo("SaMpLiNg");
        await Assert.That(metadata.RootElement.GetProperty("precision").GetString()).IsEqualTo("fine");
        await Assert.That(metadata.RootElement.GetProperty("reading").GetRawText()).IsEqualTo("1.2345678901234567890123456789");
        await Assert.That(metadata.RootElement.GetProperty("marks").GetProperty("a.b").GetBoolean()).IsTrue();
        if (direction == RegistryHeaderMetadataDirection.ClientInput)
        {
            await Assert.That(metadata.GetPresence("generated")).IsEqualTo(JsonPresence.Absent);
            var encoded = RegistryHeaderMetadata.Encode(RegistryJson.Parse("""
                {"generated":{"invalid":true},"reading":1.2345678901234567890123456789,
                 "marks":{"a.b":true},"precision":"fine","mode":"sampling"}
                """), resource, direction);
            await Assert.That(encoded.ContainsKey("xRegistry-generated")).IsFalse();
            await Assert.That(encoded["xRegistry-reading"]).IsEqualTo("1.2345678901234567890123456789");
            await Assert.That(encoded["xRegistry-marks.a.b"]).IsEqualTo("true");
        }
        else
        {
            await Assert.That(metadata.RootElement.GetProperty("generated").GetRawText()).IsEqualTo("184467440737095516160001");
        }
    }

    [Test]
    [Arguments("counter", JsonValueKind.Number)]
    [Arguments("inactive", JsonValueKind.String)]
    public async Task ConditionalNamedDefinitionOverridesWildcardOnlyWhileActive(string kind, JsonValueKind expectedKind)
    {
        var resource = Resource("""
            {"kind":{"type":"string","ifvalues":{"counter":{"siblingattributes":{"reading":{"type":"integer"}}}}},
             "*":{"type":"string"}}
            """);
        var metadata = RegistryHeaderMetadata.Decode(Fields(("xRegistry-reading", "42"), ("xRegistry-kind", kind)),
            resource, RegistryHeaderMetadataDirection.Response);
        await Assert.That(metadata.RootElement.GetProperty("reading").ValueKind).IsEqualTo(expectedKind);
        await Assert.That(expectedKind == JsonValueKind.Number ? metadata.RootElement.GetProperty("reading").GetRawText() :
            metadata.RootElement.GetProperty("reading").GetString()).IsEqualTo("42");
        var invalid = RegistryJson.Parse(kind == "counter" ? """{"kind":"counter","reading":"42"}""" : """{"kind":"inactive","reading":42}""");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Encode(invalid, resource,
            RegistryHeaderMetadataDirection.ClientInput)).Diagnostic.Code).IsEqualTo("header_error");
    }

    [Test]
    public async Task ConditionalInactiveSiblingWithoutWildcardKeepsHeaderErrorAndName()
    {
        var resource = Resource("""
            {"kind":{"type":"string","ifvalues":{"counter":{"siblingattributes":{"reading":{"type":"integer"}}}}}}
            """);
        var failure = Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-reading", "42"), ("xRegistry-kind", "inactive")),
            resource, RegistryHeaderMetadataDirection.ClientInput, path: Path));
        await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(Path);
        await Assert.That(Arguments(failure)["name"]).IsEqualTo("xRegistry-reading");
        await Assert.That(failure.Diagnostic.Message).IsEqualTo("The header attribute is not defined by the model.");
    }

    [Test]
    public async Task ConditionalWildcardNeverOverridesDeclaredAttributes()
    {
        var resource = Resource("""
            {"rank":{"type":"uinteger"},"mode":{"type":"string","ifvalues":{"open":{"siblingattributes":{"*":{"type":"string"}}}}}}
            """);
        var metadata = RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-rank", "184467440737095516160001"), ("xRegistry-extra", "42"), ("xRegistry-mode", "open")),
            resource, RegistryHeaderMetadataDirection.Response);
        await Assert.That(metadata.RootElement.GetProperty("rank").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("extra").GetString()).IsEqualTo("42");
        var unambiguous = RegistryHeaderMetadata.Decode(Fields(("xRegistry-rank", "7")), resource, RegistryHeaderMetadataDirection.Response);
        await Assert.That(unambiguous.RootElement.GetProperty("rank").GetInt32()).IsEqualTo(7);
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-extra", "42")),
            resource, RegistryHeaderMetadataDirection.Response)).Diagnostic.Message).IsEqualTo(
                "A conditional header needs an explicit discriminator context; a discriminator is absent or ignored.");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-extra", "42"), ("xRegistry-mode", "closed")),
            resource, RegistryHeaderMetadataDirection.Response)).Diagnostic.Message).IsEqualTo("The header attribute is not defined by the model.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConditionalConflictsUseTheExistingMetamodelDiagnostic(bool reverse)
    {
        var resource = Resource("""
            {"left":{"type":"string","ifvalues":{"on":{"siblingattributes":{"reading":{"type":"integer"}}}}},
             "right":{"type":"string","ifvalues":{"on":{"siblingattributes":{"reading":{"type":"decimal"}}}}}}
            """);
        var input = RegistryJson.Parse("""{"left":"on","right":"on","reading":42}""");
        var modelFailure = Reject(() => RegistryMetadataValidator.Validate(input, resource.Attributes, new() { Mode = RegistryMetadataMode.ClientInput }));
        var fields = Fields(("xRegistry-left", "on"), ("xRegistry-right", "on"), ("xRegistry-reading", "42"));
        var failure = Reject(() => RegistryHeaderMetadata.Decode(reverse ? fields.Reverse() : fields,
            resource, RegistryHeaderMetadataDirection.ClientInput, path: Path));
        await Assert.That(modelFailure.Diagnostic.Code).IsEqualTo("invalid_attribute");
        await Assert.That(failure.Diagnostic.Code).IsEqualTo(modelFailure.Diagnostic.Code);
        await Assert.That(failure.Diagnostic.Message).IsEqualTo("Active conditional definitions conflict.");
        await Assert.That(failure.Diagnostic.Message).IsEqualTo(modelFailure.Diagnostic.Message);
        await Assert.That(Arguments(failure)["name"]).IsEqualTo("reading");
        await Assert.That(failure.Diagnostic.Path).IsEqualTo(Path);
        var missing = Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-left", "on"), ("xRegistry-reading", "42")),
            resource, RegistryHeaderMetadataDirection.Response));
        await Assert.That(missing.Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(missing.Diagnostic.Message).IsEqualTo(
            "A conditional header needs an explicit discriminator context; a discriminator is absent or ignored.");
    }

    [Test]
    public async Task ConditionalReadonlyDiscriminatorIsNotTrustedAsWritableContext()
    {
        var resource = Resource("""
            {"kind":{"type":"string","readonly":true,"ifvalues":{"counter":{"siblingattributes":{"reading":{"type":"integer"}}}}},
             "*":{"type":"string"}}
            """);
        var ignored = RegistryHeaderMetadata.Decode(Fields(("xRegistry-kind", "%invalid")), resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(ignored.GetPresence("kind")).IsEqualTo(JsonPresence.Absent);
        var failure = Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-kind", "%invalid"), ("xRegistry-reading", "42")),
            resource, RegistryHeaderMetadataDirection.ClientInput));
        await Assert.That(failure.Diagnostic.Message).IsEqualTo(
            "A conditional header needs an explicit discriminator context; a discriminator is absent or ignored.");
        var response = RegistryHeaderMetadata.Decode(Fields(("xRegistry-reading", "42"), ("xRegistry-kind", "counter")),
            resource, RegistryHeaderMetadataDirection.Response);
        await Assert.That(response.RootElement.GetProperty("reading").GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task ConditionalPartialHeadersDoNotInventDiscriminatorDefaults()
    {
        var resource = Resource("""
            {"kind":{"type":"string","required":true,"default":"counter","ifvalues":{"counter":{"siblingattributes":{"reading":{"type":"integer"}}}}}}
            """);
        foreach (var direction in new[] { RegistryHeaderMetadataDirection.ClientInput, RegistryHeaderMetadataDirection.Response })
        {
            var failure = Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-reading", "42")), resource, direction));
            await Assert.That(failure.Diagnostic.Code).IsEqualTo("header_error");
            await Assert.That(Arguments(failure)["name"]).IsEqualTo("xRegistry-reading");
        }
        var explicitValue = RegistryHeaderMetadata.Decode(Fields(("xRegistry-reading", "42"), ("xRegistry-kind", "counter")),
            resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(explicitValue.RootElement.GetProperty("reading").GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task ConditionalNumericDiscriminatorsUseExactScalarSerialization()
    {
        var resource = Resource("""
            {"selector":{"type":"uinteger","ifvalues":{"184467440737095516160001":{"siblingattributes":{"reading":{"type":"boolean"}}}}}}
            """);
        var metadata = RegistryHeaderMetadata.Decode(
            Fields(("xRegistry-reading", "true"), ("xRegistry-selector", "\"184467440737095516160001\"")),
            resource, RegistryHeaderMetadataDirection.Response);
        await Assert.That(metadata.RootElement.GetProperty("selector").GetRawText()).IsEqualTo("184467440737095516160001");
        await Assert.That(metadata.RootElement.GetProperty("reading").GetBoolean()).IsTrue();
        var headers = RegistryHeaderMetadata.Encode(RegistryJson.Parse("""{"reading":true,"selector":184467440737095516160001}"""),
            resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(headers["xRegistry-selector"]).IsEqualTo("184467440737095516160001");
        await Assert.That(headers["xRegistry-reading"]).IsEqualTo("true");
    }

    [Test]
    public async Task ConditionalStringNullIsNotARequestDiscriminatorEscape()
    {
        var resource = Resource("""
            {"kind":{"type":"string","ifvalues":{"null":{"siblingattributes":{"reading":{"type":"integer"}}}}},"*":{"type":"string"}}
            """);
        var fields = Fields(("xRegistry-reading", "42"), ("xRegistry-kind", "%6Eull"));
        var request = RegistryHeaderMetadata.Decode(fields, resource, RegistryHeaderMetadataDirection.ClientInput);
        await Assert.That(request.GetPresence("kind")).IsEqualTo(JsonPresence.Null);
        await Assert.That(request.RootElement.GetProperty("reading").GetString()).IsEqualTo("42");
        var response = RegistryHeaderMetadata.Decode(fields, resource, RegistryHeaderMetadataDirection.Response);
        await Assert.That(response.RootElement.GetProperty("kind").GetString()).IsEqualTo("null");
        await Assert.That(response.RootElement.GetProperty("reading").GetInt32()).IsEqualTo(42);
        var failure = Reject(() => RegistryHeaderMetadata.Encode(RegistryJson.Parse("""{"kind":"null","reading":42}"""),
            resource, RegistryHeaderMetadataDirection.ClientInput));
        await Assert.That(failure.Diagnostic.Message).IsEqualTo(
            "The literal string null is indistinguishable from deletion in a request header; use a metadata-body request.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConditionalLookupBudgetsBoundActiveAndUnresolvedBranches(bool missing)
    {
        var resource = NestedConditionalResource();
        var fields = missing ? Fields(("xRegistry-reading", "1")) :
            Fields(("xRegistry-reading", "1"), ("xRegistry-precision", "fine"), ("xRegistry-mode", "sampling"));
        var depth = Reject(() => RegistryHeaderMetadata.Decode(fields, resource, RegistryHeaderMetadataDirection.Response,
            new() { Json = new() { MaxDepth = 1 } }));
        await Assert.That(depth.Diagnostic.Code).IsEqualTo("depth_limit");
        var nodes = Reject(() => RegistryHeaderMetadata.Decode(fields, resource, RegistryHeaderMetadataDirection.Response,
            new() { Json = new() { MaxNodes = 2 } }));
        await Assert.That(nodes.Diagnostic.Code).IsEqualTo("node_limit");
        if (!missing)
        {
            var value = RegistryHeaderMetadata.Decode(fields, resource, RegistryHeaderMetadataDirection.Response,
                new() { Json = new() { MaxDepth = 2, MaxNodes = 100 } });
            await Assert.That(value.RootElement.GetProperty("reading").GetRawText()).IsEqualTo("1");
        }
    }

    [Test]
    public async Task ConditionalHeadersRetainInclusiveAggregateByteAndCountBudgets()
    {
        var resource = Resource("""
            {"kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{"reading":{"type":"integer"}}}}}}
            """);
        var limits = new RegistryHeaderMetadataOptions { MaxHeaderBytes = 42, MaxHeaderCount = 2 };
        var input = RegistryJson.Parse("""{"reading":1,"kind":"on"}""");
        var headers = RegistryHeaderMetadata.Encode(input, resource, RegistryHeaderMetadataDirection.ClientInput, limits);
        await Assert.That(headers.Count).IsEqualTo(2);
        await Assert.That(headers["xRegistry-reading"]).IsEqualTo("1");
        var fields = Fields(("xRegistry-reading", "1"), ("xRegistry-kind", "on"));
        await Assert.That(RegistryHeaderMetadata.Decode(fields, resource, RegistryHeaderMetadataDirection.Response, limits)
            .RootElement.GetProperty("reading").GetInt32()).IsEqualTo(1);
        await Assert.That(Reject(() => RegistryHeaderMetadata.Encode(RegistryJson.Parse("""{"reading":10,"kind":"on"}"""),
            resource, RegistryHeaderMetadataDirection.ClientInput, limits)).Diagnostic.Code).IsEqualTo("request_headers_too_large");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(fields, resource, RegistryHeaderMetadataDirection.Response,
            limits with { MaxHeaderCount = 1 })).Diagnostic.Code).IsEqualTo("too_large");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-reading", "10"), ("xRegistry-kind", "on")),
            resource, RegistryHeaderMetadataDirection.Response, limits)).Diagnostic.Code).IsEqualTo("too_large");
    }

    [Test]
    public async Task ConditionalMapsWithObjectItemsStillRequireTheMetadataBody()
    {
        var resource = Resource("""
            {"entries":{"type":"map","item":{"type":"object","attributes":{
              "kind":{"type":"string","ifvalues":{"counter":{"siblingattributes":{"reading":{"type":"integer"}}}}}
            }}}}
            """);
        var input = RegistryJson.Parse("""{"entries":{"a":{"kind":"counter","reading":42}}}""");
        var valid = RegistryMetadataValidator.Validate(input, resource.Attributes, new() { Mode = RegistryMetadataMode.ClientInput });
        await Assert.That(valid.Metadata.RootElement.GetProperty("entries").GetProperty("a").GetProperty("reading").GetInt32()).IsEqualTo(42);
        await Assert.That(Reject(() => RegistryHeaderMetadata.Encode(input, resource, RegistryHeaderMetadataDirection.ClientInput))
            .Diagnostic.Code).IsEqualTo("header_error");
        await Assert.That(Reject(() => RegistryHeaderMetadata.Decode(Fields(("xRegistry-entries.a", "42")),
            resource, RegistryHeaderMetadataDirection.ClientInput)).Diagnostic.Message)
            .IsEqualTo("Only scalar-valued maps may be represented by member headers.");
    }

    private static RegistryResourceDefinition NestedConditionalResource() => Resource("""
        {
          "mode": {
            "type": "string",
            "ifvalues": {
              "sampling": {
                "siblingattributes": {
                  "precision": {
                    "type": "string",
                    "ifvalues": {
                      "fine": {
                        "siblingattributes": {
                          "reading": { "type": "decimal" },
                          "marks": { "type": "map", "item": { "type": "boolean" } },
                          "generated": { "type": "uinteger", "readonly": true }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """);

    private static RegistryResourceDefinition Resource(string attributes) => RegistryModel.Compile(RegistryJson.Parse(
        """{"groups":{"teams":{"singular":"team","resources":{"files":{"singular":"file","attributes":""" +
        attributes + "}}}}}")).Groups["teams"].Resources["files"];

    private static IReadOnlyDictionary<string, string> Write(string json,
        RegistryHeaderMetadataDirection direction = RegistryHeaderMetadataDirection.ClientInput,
        RegistryHeaderMetadataOptions? options = null) =>
        RegistryHeaderMetadata.Encode(RegistryJson.Parse(json), s_resource, direction, options, Path);

    private static RegistryJson Read(params (string Name, string Value)[] fields) =>
        RegistryHeaderMetadata.Decode(Fields(fields), s_resource, RegistryHeaderMetadataDirection.Response);

    private static IEnumerable<KeyValuePair<string, IEnumerable<string?>>> Fields(params (string Name, string Value)[] fields) =>
        fields.Select(static field => new KeyValuePair<string, IEnumerable<string?>>(field.Name, new[] { field.Value }));

    private static IReadOnlyDictionary<string, string> Arguments(RegistryException exception) =>
        (IReadOnlyDictionary<string, string>)exception.Data["xregistry.args"]!;

    private static RegistryException Reject(Action operation)
    {
        try
        {
            operation();
        }
        catch (RegistryException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected an explicit RegistryException.");
    }
}
