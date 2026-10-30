// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Models;

internal static class MediaEndpointSemantics
{
    private static readonly RegistryJson Defaults = RegistryJson.Parse("""
        {
          "SRT":{"streamformat":"mpegts","videocodec":"H264","audiocodec":"AAC"},
          "WebRTC":{"streamformat":"srtp","videocodec":"H264","audiocodec":"Opus"},
          "RTSP/1.0":{"streamformat":"rtp","videocodec":"H264","audiocodec":"AAC"},
          "RTP/2":{"streamformat":"rtp","videocodec":"H264","audiocodec":"AAC"},
          "MPEGTS-UDP":{"streamformat":"mpegts","videocodec":"H264","audiocodec":"AAC"},
          "RTMP/1.0":{"streamformat":"flv","videocodec":"H264","audiocodec":"AAC"},
          "RIST-Simple/2020":{"streamformat":"rtp-mpegts","videocodec":"H264","audiocodec":"AAC"},
          "RIST-Main/2024":{"streamformat":"rtp-mpegts","videocodec":"H264","audiocodec":"AAC"}
        }
        """);

    internal static bool IsDraftMediaProtocol(string? protocol) => protocol is
        "SRT" or "WebRTC" or "RTSP/1.0" or "RTSP/2.0" or "RTP/2" or "MPEGTS-UDP" or
        "RTMP/1.0" or "HLS" or "DASH" or "RIST-Simple/2020" or "RIST-Main/2024";

    internal static bool IsMediaProtocol(string? protocol) => protocol == "HTTP" || IsDraftMediaProtocol(protocol);

    internal static bool IsProducer(JsonElement endpoint) =>
        endpoint.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Array &&
        usage.EnumerateArray().Any(static role => role.ValueKind == JsonValueKind.String && role.GetString() == "producer");

    internal static JsonElement GetDefaultOption(string protocol, string name) =>
        Defaults.RootElement.TryGetProperty(protocol, out var protocolDefaults) &&
        protocolDefaults.TryGetProperty(name, out var value) ? value : default;

    internal static bool RequiresHttpsSignaling(JsonElement options) =>
        options.ValueKind == JsonValueKind.Object &&
        options.TryGetProperty("signaling", out var signaling) &&
        signaling.ValueKind == JsonValueKind.String &&
        signaling.GetString() is "WHIP/RFC9725" or "WHEP/draft-ietf-wish-whep-03";

    internal static void ValidateIfDraftMediaProtocol(JsonElement endpoint, bool resolved, CancellationToken cancellationToken)
    {
        var protocol = EndpointDefinitionSemantics.Protocol(endpoint);
        if (IsDraftMediaProtocol(protocol))
        {
            Validate(endpoint, resolved, cancellationToken);
        }
    }

    internal static void Validate(JsonElement endpoint, bool resolved, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("", "Media Endpoint metadata must be an object.");
        }

        var protocol = EndpointDefinitionSemantics.Protocol(endpoint);
        if (!IsMediaProtocol(protocol))
        {
            throw Invalid("/protocol", "A Media Endpoint requires a selector defined by the Media Endpoints working draft.");
        }
        if (!endpoint.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Array ||
            usage.GetArrayLength() != 1 || usage[0].ValueKind != JsonValueKind.String ||
            usage[0].GetString() is not ("producer" or "consumer"))
        {
            throw Invalid("/usage", "A Media Endpoint has exactly one usage: producer or consumer.");
        }

        RejectDeclared(endpoint, "envelope");
        RejectDeclared(endpoint, "envelopeoptions");
        ValidateMessageAssociations(endpoint, cancellationToken);
        if ((protocol is "HTTP" or "RTSP/2.0" or "HLS" or "DASH") && IsProducer(endpoint))
        {
            throw Invalid("/usage", "The selected media protocol does not define producer usage.");
        }

        if (!endpoint.TryGetProperty("protocoloptions", out var options))
        {
            options = default;
        }
        else if (options.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("/protocoloptions", "Media protocol options must be an object.");
        }

        ValidateStringOptions(options, cancellationToken);
        ValidateProducerOptions(options, protocol!, IsProducer(endpoint), resolved);
        ValidateProtocolRequirements(options, protocol!, IsProducer(endpoint), resolved);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ValidateStringOptions(JsonElement options, CancellationToken cancellationToken)
    {
        foreach (var name in new[]
        {
            "streamformat", "videocodec", "audiocodec", "streamid", "signaling", "signalingreference",
            "sessiondescription", "application", "streamname"
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) &&
                value.ValueKind != JsonValueKind.String)
            {
                throw Invalid("/protocoloptions/" + name, "The media option must be a string.");
            }
        }
    }

    private static void ValidateProducerOptions(JsonElement options, string protocol, bool producer, bool resolved)
    {
        var hasOptions = options.ValueKind == JsonValueKind.Object;
        var producerOnly = protocol is "SRT" or "WebRTC" or "RTSP/1.0" or "RTP/2" or "MPEGTS-UDP" or
            "RTMP/1.0" or "RIST-Simple/2020" or "RIST-Main/2024";
        if (!producer && hasOptions)
        {
            foreach (var name in new[] { "streamformat", "videocodec", "audiocodec" })
            {
                if (options.TryGetProperty(name, out _))
                {
                    throw Invalid("/protocoloptions/" + name, "Producer input options are not valid for consumer usage.");
                }
            }
        }
        if (!producer || !producerOnly)
        {
            return;
        }

        var streamFormat = TextOrDefault(options, "streamformat", protocol);
        if (streamFormat is not null && (resolved || !streamFormat.Contains('{', StringComparison.Ordinal)) &&
            !AllowedStreamFormat(protocol, streamFormat))
        {
            throw Invalid("/protocoloptions/streamformat", "The stream format is not defined for this media protocol.");
        }

        var videoCodec = TextOrDefault(options, "videocodec", protocol);
        var audioCodec = TextOrDefault(options, "audiocodec", protocol);
        ValidateCodecKind(videoCodec, video: true);
        ValidateCodecKind(audioCodec, video: false);
        if (videoCodec == "none" && audioCodec == "none")
        {
            throw Invalid("/protocoloptions/audiocodec", "A producer must declare at least one media kind.");
        }

    }

    private static void ValidateCodecKind(string? codec, bool video)
    {
        if (codec is null or "none")
        {
            return;
        }

        var belongsToOtherKind = video
            ? codec is "AAC" or "Opus" or "PCMA" or "PCMU"
            : codec is "H264" or "H265" or "VP8" or "VP9" or "AV1";
        if (belongsToOtherKind)
        {
            var kind = video ? "video" : "audio";
            throw Invalid("/protocoloptions/" + (video ? "videocodec" : "audiocodec"),
                $"The codec is defined for the other media kind and cannot be used as {kind}.");
        }
    }

    private static string? TextOrDefault(JsonElement options, string name, string protocol)
    {
        if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value))
        {
            var text = value.GetString()!;
            if (text.Length == 0)
            {
                throw Invalid("/protocoloptions/" + name, "The media option must be nonempty.");
            }
            return text;
        }
        var defaultValue = GetDefaultOption(protocol, name);
        return defaultValue.ValueKind == JsonValueKind.String ? defaultValue.GetString() : null;
    }

    private static bool AllowedStreamFormat(string protocol, string format) => protocol switch
    {
        "SRT" or "MPEGTS-UDP" => format == "mpegts",
        "WebRTC" => format == "srtp",
        "RTSP/1.0" or "RTP/2" => format is "rtp" or "rtp-mpegts",
        "RTMP/1.0" => format == "flv",
        "RIST-Simple/2020" or "RIST-Main/2024" => format == "rtp-mpegts",
        _ => true
    };

    private static void ValidateProtocolRequirements(JsonElement options, string protocol, bool producer, bool resolved)
    {
        var hasOptions = options.ValueKind == JsonValueKind.Object;
        if (hasOptions)
        {
            RejectOption(options, protocol, "streamid", "SRT");
            RejectOption(options, protocol, "signaling", "WebRTC");
            RejectOption(options, protocol, "signalingreference", "WebRTC");
            RejectOption(options, protocol, "sessiondescription", "RTP/2");
            RejectOption(options, protocol, "application", "RTMP/1.0");
            RejectOption(options, protocol, "streamname", "RTMP/1.0");
        }
        if (protocol == "RTMP/1.0")
        {
            RequiredText(options, "application", resolved);
            RequiredText(options, "streamname", resolved);
        }
        if (protocol == "WebRTC")
        {
            var signaling = RequiredText(options, "signaling", resolved);
            var signalingIsTemplate = !resolved && signaling?.Contains('{', StringComparison.Ordinal) == true;
            if (signaling is not null && !signalingIsTemplate &&
                signaling is not ("WHIP/RFC9725" or "WHEP/draft-ietf-wish-whep-03" or "external"))
            {
                throw Invalid("/protocoloptions/signaling", "The signaling selector is not defined by the Media Endpoints working draft.");
            }
            var reference = hasOptions && options.TryGetProperty("signalingreference", out var referenceValue)
                ? referenceValue.GetString() : null;
            if (signaling == "external")
            {
                if (reference is null)
                {
                    throw Invalid("/protocoloptions/signalingreference", "External signaling requires an HTTPS signaling reference.");
                }
                ValidateHttpsUri(reference, "/protocoloptions/signalingreference", resolved);
            }
            else if (!signalingIsTemplate && reference is not null)
            {
                throw Invalid("/protocoloptions/signalingreference", "Fixed signaling selectors do not use signalingreference.");
            }
            if ((signaling == "WHIP/RFC9725" && !producer) ||
                (signaling == "WHEP/draft-ietf-wish-whep-03" && producer))
            {
                throw Invalid("/usage", "WHIP defines producer usage and WHEP defines consumer usage.");
            }
        }
        if (protocol == "RTP/2" && producer)
        {
            var description = RequiredText(options, "sessiondescription", resolved);
            if (description is not null)
            {
                ValidateHttpsUri(description, "/protocoloptions/sessiondescription", resolved);
            }
        }
        else if (hasOptions && options.TryGetProperty("sessiondescription", out var sessionDescription))
        {
            ValidateHttpsUri(sessionDescription.GetString()!, "/protocoloptions/sessiondescription", resolved);
        }
    }

    private static string? RequiredText(JsonElement options, string name, bool resolved)
    {
        if (options.ValueKind != JsonValueKind.Object || !options.TryGetProperty(name, out var value))
        {
            throw Invalid("/protocoloptions/" + name, "The required media option is missing.");
        }
        var text = value.GetString()!;
        if (text.Length == 0)
        {
            throw Invalid("/protocoloptions/" + name, "The required media option must be nonempty.");
        }
        if (resolved && text.Contains('{', StringComparison.Ordinal))
        {
            throw Invalid("/protocoloptions/" + name, "The required media option contains an unresolved placeholder.");
        }
        return text;
    }

    private static void ValidateHttpsUri(string value, string path, bool resolved)
    {
        if (!resolved && value.Contains('{', StringComparison.Ordinal))
        {
            return;
        }
        EndpointDefinitionSemantics.ValidateReference(value, path);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0)
        {
            throw Invalid(path, "The value must be an absolute HTTPS URI without embedded credentials.");
        }
    }

    private static void ValidateMessageAssociations(JsonElement endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.TryGetProperty("envelope", out var envelope) && envelope.ValueKind != JsonValueKind.Null ||
            endpoint.TryGetProperty("envelopeoptions", out var envelopeOptions) && envelopeOptions.ValueKind != JsonValueKind.Null)
        {
            throw Invalid("/envelope", "Media Endpoints must not declare an envelope or envelope options.");
        }
        if (endpoint.TryGetProperty("messagegroups", out var references) &&
            references.ValueKind != JsonValueKind.Null &&
            (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() != 0))
        {
            throw Invalid("/messagegroups", "Media Endpoints must not associate Message Groups.");
        }
        if (endpoint.TryGetProperty("messages", out var messages) && messages.ValueKind != JsonValueKind.Null)
        {
            if (messages.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("/messages", "Media Endpoints must not contain Message Resources.");
            }
            foreach (var _ in messages.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Invalid("/messages", "Media Endpoints must not contain Message Resources.");
            }
        }
        var hasEmptyMessageCollection = false;
        if (endpoint.TryGetProperty("messagescount", out var count) && count.ValueKind != JsonValueKind.Null)
        {
            if (count.ValueKind != JsonValueKind.Number ||
                !RegistryNumber.FromElement(count, new RegistryJsonLimits()).Significand.IsZero)
            {
                throw Invalid("/messagescount", "Media Endpoints must not advertise Message Resources.");
            }
            hasEmptyMessageCollection = true;
        }
        if (endpoint.TryGetProperty("messagesurl", out var messagesUrl) && messagesUrl.ValueKind != JsonValueKind.Null &&
            !hasEmptyMessageCollection)
        {
            throw Invalid("/messagesurl", "A Message collection URL is allowed only when the collection is empty.");
        }
    }

    private static void RejectDeclared(JsonElement endpoint, string name)
    {
        if (endpoint.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            throw Invalid("/" + name, "Media Endpoints must not declare this attribute.");
        }
    }

    private static void RejectOption(JsonElement options, string protocol, string name, string requiredProtocol)
    {
        if (protocol != requiredProtocol && options.TryGetProperty(name, out _))
        {
            throw Invalid("/protocoloptions/" + name, $"The option is defined only for the {requiredProtocol} media selector.");
        }
    }

    private static RegistryException Invalid(string path, string message) =>
        new(new RegistryDiagnostic("invalid_attribute", path, message));
}
