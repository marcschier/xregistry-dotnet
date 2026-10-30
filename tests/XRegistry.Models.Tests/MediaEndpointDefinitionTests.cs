// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

public class MediaEndpointDefinitionTests
{
    [Test]
    [Arguments("SRT", "producer", "mpegts", "H264", "AAC")]
    [Arguments("WebRTC", "producer", "srtp", "H264", "Opus")]
    [Arguments("RTSP/1.0", "producer", "rtp", "H264", "AAC")]
    [Arguments("RTP/2", "producer", "rtp", "H264", "AAC")]
    [Arguments("MPEGTS-UDP", "producer", "mpegts", "H264", "AAC")]
    [Arguments("RTMP/1.0", "producer", "flv", "H264", "AAC")]
    [Arguments("RIST-Simple/2020", "producer", "rtp-mpegts", "H264", "AAC")]
    [Arguments("RIST-Main/2024", "producer", "rtp-mpegts", "H264", "AAC")]
    public async Task ProducerDefaultsMatchTheMediaDraft(string protocol, string usage, string streamFormat,
        string videoCodec, string audioCodec)
    {
        var result = EndpointDefinition.MaterializeMedia(ValidEndpoint(protocol, usage));

        await Assert.That(result.GetProtocolOption("streamformat").GetString()).IsEqualTo(streamFormat);
        await Assert.That(result.GetProtocolOption("videocodec").GetString()).IsEqualTo(videoCodec);
        await Assert.That(result.GetProtocolOption("audiocodec").GetString()).IsEqualTo(audioCodec);
    }

    [Test]
    [Arguments("SRT", "consumer")]
    [Arguments("WebRTC", "consumer")]
    [Arguments("RTSP/1.0", "consumer")]
    [Arguments("RTSP/2.0", "consumer")]
    [Arguments("RTP/2", "consumer")]
    [Arguments("MPEGTS-UDP", "consumer")]
    [Arguments("RTMP/1.0", "consumer")]
    [Arguments("HLS", "consumer")]
    [Arguments("DASH", "consumer")]
    [Arguments("RIST-Simple/2020", "consumer")]
    [Arguments("RIST-Main/2024", "consumer")]
    public async Task ConsumerSelectorsMaterializeWithoutProducerDefaults(string protocol, string usage)
    {
        var result = EndpointDefinition.MaterializeMedia(ValidEndpoint(protocol, usage));

        await Assert.That(result.GetProtocolOption("streamformat").ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(result.Messages).IsEmpty();
    }

    [Test]
    [Arguments("RTSP/2.0", "producer", "{}", "/usage")]
    [Arguments("HLS", "producer", "{}", "/usage")]
    [Arguments("DASH", "producer", "{}", "/usage")]
    [Arguments("WebRTC", "consumer", """{"signaling":"WHIP/RFC9725"}""", "/usage")]
    [Arguments("WebRTC", "producer", """{"signaling":"WHEP/draft-ietf-wish-whep-03"}""", "/usage")]
    public async Task ProtocolDirectionRestrictionsRejectInvalidRoles(string protocol, string usage,
        string options, string path)
    {
        var input = Endpoint(protocol, usage, options);
        await Rejects(input, path);
    }

    [Test]
    [Arguments("RTMP/1.0", "producer", """{"streamname":"camera"}""", "/protocoloptions/application")]
    [Arguments("RTP/2", "producer", "{}", "/protocoloptions/sessiondescription")]
    [Arguments("WebRTC", "producer", """{"signaling":"external"}""", "/protocoloptions/signalingreference")]
    [Arguments("WebRTC", "producer", "{}", "/protocoloptions/signaling")]
    [Arguments("SRT", "consumer", """{"videocodec":"H264"}""", "/protocoloptions/videocodec")]
    [Arguments("SRT", "producer", """{"videocodec":"none","audiocodec":"none"}""", "/protocoloptions/audiocodec")]
    [Arguments("SRT", "producer", """{"streamformat":"flv"}""", "/protocoloptions/streamformat")]
    [Arguments("RTP/2", "producer", """{"sessiondescription":"http://media.example.test/session.sdp"}""",
        "/protocoloptions/sessiondescription")]
    [Arguments("WebRTC", "producer",
        """{"signaling":"external","signalingreference":"http://media.example.test/signaling"}""",
        "/protocoloptions/signalingreference")]
    [Arguments("RTMP/1.0", "consumer", """{"application":12,"streamname":"camera"}""",
        "/protocoloptions/application")]
    public async Task RequiredAndProducerInputRulesFailAtTheOption(string protocol, string usage,
        string options, string path)
    {
        await Rejects(Endpoint(protocol, usage, options), path);
    }

    [Test]
    [Arguments("videocodec", "AAC", "/protocoloptions/videocodec")]
    [Arguments("videocodec", "Opus", "/protocoloptions/videocodec")]
    [Arguments("videocodec", "PCMA", "/protocoloptions/videocodec")]
    [Arguments("videocodec", "PCMU", "/protocoloptions/videocodec")]
    [Arguments("audiocodec", "H264", "/protocoloptions/audiocodec")]
    [Arguments("audiocodec", "H265", "/protocoloptions/audiocodec")]
    [Arguments("audiocodec", "VP8", "/protocoloptions/audiocodec")]
    [Arguments("audiocodec", "VP9", "/protocoloptions/audiocodec")]
    [Arguments("audiocodec", "AV1", "/protocoloptions/audiocodec")]
    public async Task KnownCodecsCannotBeDeclaredForTheOtherMediaKind(string option, string codec, string path)
    {
        var options = option == "videocodec"
            ? $$"""{"videocodec":"{{codec}}"}"""
            : $$"""{"audiocodec":"{{codec}}"}""";

        await Rejects(Endpoint("SRT", "producer", options), path);
    }

    [Test]
    public async Task CodecIdentifiersRemainCaseSensitiveAndOpenEnded()
    {
        var result = EndpointDefinition.MaterializeMedia(Endpoint("SRT", "producer",
            """{"videocodec":"h264","audiocodec":"aac"}"""));

        await Assert.That(result.GetProtocolOption("videocodec").GetString()).IsEqualTo("h264");
        await Assert.That(result.GetProtocolOption("audiocodec").GetString()).IsEqualTo("aac");
    }

    [Test]
    [Arguments("SRT", "mpegts")]
    [Arguments("WebRTC", "srtp")]
    [Arguments("RTSP/1.0", "rtp")]
    [Arguments("RTSP/1.0", "rtp-mpegts")]
    [Arguments("RTP/2", "rtp")]
    [Arguments("RTP/2", "rtp-mpegts")]
    [Arguments("MPEGTS-UDP", "mpegts")]
    [Arguments("RTMP/1.0", "flv")]
    [Arguments("RIST-Simple/2020", "rtp-mpegts")]
    [Arguments("RIST-Main/2024", "rtp-mpegts")]
    public async Task DeclaredStreamFormatsAcceptIndependentKnownCodecDeclarations(string protocol, string streamFormat)
    {
        var input = ProducerOptions(protocol);
        input["protocoloptions"]!["streamformat"] = streamFormat;
        input["protocoloptions"]!["videocodec"] = "AV1";
        input["protocoloptions"]!["audiocodec"] = "PCMU";

        var result = EndpointDefinition.MaterializeMedia(RegistryJson.Parse(input.ToJsonString()));

        await Assert.That(result.GetProtocolOption("streamformat").GetString()).IsEqualTo(streamFormat);
        await Assert.That(result.GetProtocolOption("videocodec").GetString()).IsEqualTo("AV1");
        await Assert.That(result.GetProtocolOption("audiocodec").GetString()).IsEqualTo("PCMU");
    }

    [Test]
    [Arguments("SRT")]
    [Arguments("WebRTC")]
    [Arguments("RTSP/1.0")]
    [Arguments("RTP/2")]
    [Arguments("MPEGTS-UDP")]
    [Arguments("RTMP/1.0")]
    [Arguments("RIST-Simple/2020")]
    [Arguments("RIST-Main/2024")]
    public async Task StreamFormatEnumsRejectUnknownCaseVariantsAndNonStringValues(string protocol)
    {
        var input = ProducerOptions(protocol);
        var declared = EndpointDefinition.MaterializeMedia(ValidEndpoint(protocol, "producer"))
            .GetProtocolOption("streamformat").GetString()!;
        foreach (var invalid in new[] { "\"not-declared\"", "\"" + declared.ToUpperInvariant() + "\"", "42", "null", "\"\"" })
        {
            input["protocoloptions"]!["streamformat"] = JsonNode.Parse(invalid);
            await Rejects(RegistryJson.Parse(input.ToJsonString()), "/protocoloptions/streamformat");
        }
    }

    [Test]
    [Arguments("aac", "h264")]
    [Arguments("Aac", "H264/1")]
    [Arguments("VendorVideo/8", "VendorAudio/9")]
    public async Task UnrecognizedCodecNamesRemainExactForBothMediaKinds(string videoCodec, string audioCodec)
    {
        var input = ProducerOptions("SRT");
        input["protocoloptions"]!["videocodec"] = videoCodec;
        input["protocoloptions"]!["audiocodec"] = audioCodec;

        var result = EndpointDefinition.MaterializeMedia(RegistryJson.Parse(input.ToJsonString()));

        await Assert.That(result.GetProtocolOption("videocodec").GetString()).IsEqualTo(videoCodec);
        await Assert.That(result.GetProtocolOption("audiocodec").GetString()).IsEqualTo(audioCodec);
    }

    [Test]
    public async Task MediaAddressSchemeMustMatchTheSelectedProtocol()
    {
        var input = Endpoint("SRT", "producer", """{"endpoints":[{"uri":"https://media.example.test:9000"}]}""");

        await Rejects(input, "/protocoloptions/endpoints/0/uri", "invalid_endpoint_address");
    }

    [Test]
    public async Task OpenCodecIdentifiersArePreservedAndConsumerDefaultsAreNotInjected()
    {
        var producer = EndpointDefinition.MaterializeMedia(Endpoint("SRT", "producer",
            """{"videocodec":"VendorCodec/8","audiocodec":"none"}"""));
        var consumer = EndpointDefinition.MaterializeMedia(ValidEndpoint("SRT", "consumer"));

        await Assert.That(producer.GetProtocolOption("videocodec").GetString()).IsEqualTo("VendorCodec/8");
        await Assert.That(producer.GetProtocolOption("audiocodec").GetString()).IsEqualTo("none");
        await Assert.That(consumer.GetProtocolOption("videocodec").ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments("""{"envelope":"CloudEvents/1.0"}""", "/envelope")]
    [Arguments("""{"envelopeoptions":{"mode":"binary"}}""", "/envelopeoptions")]
    [Arguments("""{"messagegroups":["/messagegroups/orders"]}""", "/messagegroups")]
    [Arguments("""{"messages":{"m":{"messageid":"m"}}}""", "/messages")]
    [Arguments("""{"messagescount":1}""", "/messagescount")]
    [Arguments("""{"messagesurl":"https://example.test/messages"}""", "/messagesurl")]
    public async Task MediaEndpointsRejectEnvelopeAndMessageAssociations(string members, string path)
    {
        var input = RegistryJson.Parse(
            "{\"usage\":[\"consumer\"],\"protocol\":\"HLS\",\"protocoloptions\":{\"endpoints\":[{\"uri\":\"https://media.example.test/master.m3u8\"}]},"
            + members[1..^1] + "}");

        await Rejects(input, path);
    }

    [Test]
    public async Task EmptyInheritedMessageCollectionsRemainValid()
    {
        var input = RegistryJson.Parse("""
            {"usage":["consumer"],"protocol":"HLS",
             "protocoloptions":{"endpoints":[{"uri":"https://media.example.test/master.m3u8"}]},
             "messages":{},"messagesurl":"https://registry.example/endpoints/media/messages",
             "messagescount":0,"messagegroups":[]}
            """);

        var result = EndpointDefinition.MaterializeMedia(input);

        await Assert.That(result.Messages).IsEmpty();
    }

    [Test]
    public async Task SharedHttpSelectorUsesExplicitMediaValidationWithoutChangingMessageEndpoints()
    {
        var media = EndpointDefinition.MaterializeMedia(Endpoint("HTTP", "consumer",
            """{"method":"GET","endpoints":[{"uri":"https://media.example.test/live"}]}"""));
        var messaging = EndpointDefinition.Materialize(RegistryJson.Parse("""
            {"usage":["producer"],"protocol":"HTTP",
             "messages":{"created":{"messageid":"created"}},
             "protocoloptions":{"endpoints":[{"uri":"https://api.example.test/events"}]}
            }
            """));

        await Assert.That(media.Protocol).IsEqualTo("HTTP");
        await Assert.That(media.Messages).IsEmpty();
        await Assert.That(messaging.Messages).Count().IsEqualTo(1);
    }

    [Test]
    public async Task EndpointMetadataValidationAppliesMediaDirectionRules()
    {
        var invalid = Endpoint("HLS", "producer", """{"endpoints":[{"uri":"https://media.example.test/master.m3u8"}]}""");
        var failure = await Assert.That(() => RegistryDomainRules.ValidateEndpointMetadata(invalid.RootElement))
            .Throws<RegistryException>() ?? throw new InvalidOperationException("The built-in Endpoint validation path accepted HLS producer usage.");

        await Assert.That(failure.Diagnostic.Path).IsEqualTo("/usage");
        await Assert.That(() => RegistryDomainRules.ValidateEndpointMetadata(ValidEndpoint("SRT", "producer").RootElement))
            .ThrowsNothing();
    }

    private static JsonObject ProducerOptions(string protocol) =>
        JsonNode.Parse(ValidEndpoint(protocol, "producer").RootElement.GetRawText())!.AsObject();

    private static RegistryJson ValidEndpoint(string protocol, string usage)
    {
        var options = protocol switch
        {
            "SRT" => """{"endpoints":[{"uri":"srt://media.example.test:9000"}]}""",
            "WebRTC" => $$"""{"endpoints":[{"uri":"https://media.example.test/whip"}],"signaling":"{{(usage == "producer" ? "WHIP/RFC9725" : "WHEP/draft-ietf-wish-whep-03")}}"}""",
            "RTSP/1.0" => """{"endpoints":[{"uri":"rtsp://media.example.test/live"}]}""",
            "RTSP/2.0" => """{"endpoints":[{"uri":"rtsp://media.example.test/live"}]}""",
            "RTP/2" => usage == "producer"
                ? """{"endpoints":[{"uri":"udp://media.example.test:5004"}],"sessiondescription":"https://media.example.test/session.sdp"}"""
                : """{"endpoints":[{"uri":"udp://media.example.test:5004"}]}""",
            "MPEGTS-UDP" => """{"endpoints":[{"uri":"udp://media.example.test:5004"}]}""",
            "RTMP/1.0" => """{"endpoints":[{"uri":"rtmp://media.example.test:1935"}],"application":"live","streamname":"camera"}""",
            "HLS" => """{"endpoints":[{"uri":"https://media.example.test/master.m3u8"}]}""",
            "DASH" => """{"endpoints":[{"uri":"https://media.example.test/manifest.mpd"}]}""",
            "RIST-Simple/2020" or "RIST-Main/2024" => """{"endpoints":[{"uri":"rist://media.example.test:9000"}]}""",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
        return Endpoint(protocol, usage, options);
    }

    private static RegistryJson Endpoint(string protocol, string usage, string options) =>
        RegistryJson.Parse($$"""{"usage":["{{usage}}"],"protocol":"{{protocol}}","protocoloptions":{{options}}}""");

    private static async Task Rejects(RegistryJson input, string path, string code = "invalid_attribute")
    {
        var exception = await Assert.That(() => EndpointDefinition.MaterializeMedia(input)).Throws<RegistryException>()
            ?? throw new InvalidOperationException("The invalid Media Endpoint was accepted.");
        await Assert.That(exception.Diagnostic.Code).IsEqualTo(code);
        await Assert.That(exception.Diagnostic.Path).IsEqualTo(path);
    }
}
