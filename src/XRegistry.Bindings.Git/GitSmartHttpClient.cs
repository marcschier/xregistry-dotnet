// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using XRegistry.Bindings.Git.Objects;
using XRegistry.Bindings.Git.Protocol;
using XRegistry.Client;
using XRegistry.Federation;

namespace XRegistry.Bindings.Git;

/// <summary>Managed, single-origin smart-HTTP upload-pack acquisition with immutable commit selection.</summary>
/// <remarks>SHA-1 acquisition requires an independently trusted root SHA-256; it does not claim SHA1DC equivalence.</remarks>
public static class GitSmartHttpClient
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Acquires and verifies one selected commit/tag tree using negotiated v2 or legacy smart HTTP.</summary>
    public static async ValueTask<GitSnapshot> FetchAsync(Uri repository, string revision,
        GitFetchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        GitBindingSyntax.ValidateRevision(revision);
        options ??= new();
        options.Validate();
        var policy = new RegistryHttpConnectionPolicy(repository, options.AllowLoopbackHttp, options.AllowPrivateOrigin);
        using var client = policy.CreateClient(options.Timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        var token = deadline.Token;
        var root = repository.AbsoluteUri.TrimEnd('/');
        var advertisement = await ControlAsync(client, new Uri(root + "/info/refs?service=git-upload-pack"),
            null, options, token).ConfigureAwait(false);
        var start = 0;
        if (advertisement.Count > 0 && advertisement[0].Kind == GitPacketKind.Data &&
            Text(advertisement[0]) == "# service=git-upload-pack")
        {
            if (advertisement.Count < 2 || advertisement[1].Kind != GitPacketKind.Flush)
            {
                throw Invalid("The smart-HTTP service advertisement is not terminated.");
            }

            start = 2;
        }

        var v2 = start < advertisement.Count && advertisement[start].Kind == GitPacketKind.Data &&
            Text(advertisement[start]) == "version 2";
        if (v2)
        {
            start++;
        }
        else if (start < advertisement.Count && advertisement[start].Kind == GitPacketKind.Data &&
            Text(advertisement[start]) == "version 1")
        {
            start++;
        }

        var capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
        var references = new Dictionary<string, GitObjectId>(StringComparer.Ordinal);
        if (v2)
        {
            foreach (var packet in advertisement.Skip(start).Where(static packet => packet.Kind == GitPacketKind.Data))
            {
                AddCapability(capabilities, Text(packet), options.MaxReferences);
            }
        }
        else
        {
            var first = advertisement.Skip(start).FirstOrDefault(static packet => packet.Kind == GitPacketKind.Data);
            if (first is not null)
            {
                var text = Text(first);
                var nul = text.IndexOf('\0');
                if (nul >= 0)
                {
                    foreach (var capability in text[(nul + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        AddCapability(capabilities, capability, options.MaxReferences);
                    }
                }
            }
        }

        var algorithm = capabilities.GetValueOrDefault("object-format", "sha1") switch
        {
            "sha1" => GitHashAlgorithm.Sha1,
            "sha256" => GitHashAlgorithm.Sha256,
            _ => throw new GitDataException(GitFailure.UnsupportedFormat, "The repository object format is unsupported.")
        };
        if (algorithm == GitHashAlgorithm.Sha1 && options.TrustedRegistryRootSha256 is null)
        {
            throw new GitDataException(GitFailure.PolicyDenied,
                "Managed SHA-1 network acquisition requires an independently trusted Registry-root SHA-256 until collision hardening is qualified.");
        }

        var isObjectId = revision.Length is 40 or 64 && revision.All(char.IsAsciiHexDigit);
        GitObjectId selected;
        if (v2)
        {
            if (!capabilities.ContainsKey("ls-refs") || !capabilities.ContainsKey("fetch"))
            {
                throw new GitDataException(GitFailure.UnsupportedFormat, "The peer does not advertise required smart-HTTP commands.");
            }

            if (isObjectId)
            {
                selected = GitObjectId.Parse(algorithm, revision);
            }
            else
            {
                var request = Command("ls-refs", algorithm, capabilities,
                    ["peel\n", "ref-prefix " + revision + "\n"]);
                var response = await ControlAsync(client, new Uri(root + "/git-upload-pack"), request, options, token).ConfigureAwait(false);
                ParseReferences(response, algorithm, references, options.MaxReferences);
                selected = references.GetValueOrDefault(revision) ??
                    throw new GitDataException(GitFailure.PathNotFound, "The exact requested Git ref was not advertised.");
            }
        }
        else
        {
            ParseReferences(advertisement.Skip(start), algorithm, references, options.MaxReferences);
            if (!isObjectId)
            {
                selected = references.GetValueOrDefault(revision) ??
                    throw new GitDataException(GitFailure.PathNotFound, "The exact requested Git ref was not advertised.");
            }
            else
            {
                selected = GitObjectId.Parse(algorithm, revision);
                if (!references.ContainsValue(selected) &&
                    !capabilities.ContainsKey("allow-tip-sha1-in-want") &&
                    !capabilities.ContainsKey("allow-reachable-sha1-in-want"))
                {
                    throw new GitDataException(GitFailure.PolicyDenied,
                        "The legacy peer does not permit this unadvertised object request; no branch fallback was attempted.");
                }
            }
        }

        if (selected.IsZero)
        {
            throw new GitDataException(GitFailure.PathNotFound, "The selected Git ref has no commit object.");
        }

        var shallow = v2
            ? capabilities.GetValueOrDefault("fetch", "").Split(' ').Contains("shallow", StringComparer.Ordinal)
            : capabilities.ContainsKey("shallow");
        var sideband = v2 || capabilities.ContainsKey("side-band-64k") || capabilities.ContainsKey("side-band");
        byte[] fetch;
        if (v2)
        {
            var arguments = new List<string> { "want " + selected + "\n" };
            if (shallow) { arguments.Add("deepen 1\n"); }
            arguments.Add("done\n");
            fetch = Command("fetch", algorithm, capabilities, arguments);
        }
        else
        {
            var requested = new List<string>();
            if (capabilities.ContainsKey("side-band-64k")) { requested.Add("side-band-64k"); }
            else if (capabilities.ContainsKey("side-band")) { requested.Add("side-band"); }
            if (capabilities.ContainsKey("ofs-delta")) { requested.Add("ofs-delta"); }
            if (capabilities.ContainsKey("object-format")) { requested.Add("object-format=" + AlgorithmName(algorithm)); }
            using var body = new MemoryStream();
            Packet(body, "want " + selected + (requested.Count == 0 ? "" : " " + string.Join(' ', requested)) + "\n");
            if (shallow) { Packet(body, "deepen 1\n"); }
            body.Write("0000"u8);
            Packet(body, "done\n");
            fetch = body.ToArray();
        }

        using var responseMessage = await SendAsync(client, new Uri(root + "/git-upload-pack"), fetch, options, token).ConfigureAwait(false);
        using var stream = await responseMessage.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var packets = new PacketReader(stream, options.ObjectLimits.MaxEncodedBytes + options.MaxControlBytes,
            options.ObjectLimits.MaxObjects * 128L + options.MaxReferences);
        var band = new GitSidebandDecoder(options.ObjectLimits.MaxEncodedBytes);
        using var pack = new MemoryStream();
        var inPack = false;
        var terminated = false;
        while (await packets.ReadAsync(token).ConfigureAwait(false) is { } packet)
        {
            if (packet.Kind is GitPacketKind.Flush or GitPacketKind.ResponseEnd)
            {
                if (inPack) { terminated = true; }
                continue;
            }

            if (packet.Kind == GitPacketKind.Delimiter)
            {
                if (inPack) { throw Invalid("A delimiter interrupted the pack phase."); }
                continue;
            }

            if (terminated) { throw Invalid("Data followed the completed pack phase."); }
            if (!inPack)
            {
                var text = Text(packet);
                if (text.StartsWith("ERR ", StringComparison.Ordinal))
                {
                    throw new GitDataException(GitFailure.RemoteFatal, "The Git peer rejected the fetch.");
                }

                if (v2 && text == "packfile" || !v2 && (text == "NAK" || text.StartsWith("ACK ", StringComparison.Ordinal)))
                {
                    inPack = true;
                    if (!sideband)
                    {
                        await packets.CopyRawRemainderAsync(pack, options.ObjectLimits.MaxEncodedBytes, token).ConfigureAwait(false);
                        terminated = true;
                        break;
                    }

                    continue;
                }

                if (text is "acknowledgments" or "shallow-info" or "ready" or "NAK" ||
                    text.StartsWith("shallow ", StringComparison.Ordinal) ||
                    text.StartsWith("unshallow ", StringComparison.Ordinal) || text.StartsWith("ACK ", StringComparison.Ordinal))
                {
                    continue;
                }

                throw Invalid("An unexpected control packet appeared before pack data.");
            }

            var message = band.Read(packet);
            if (message.Channel == GitSidebandChannel.Data)
            {
                if (message.Payload.Length > options.ObjectLimits.MaxEncodedBytes - pack.Length)
                {
                    throw new GitDataException(GitFailure.LimitExceeded, "The downloaded pack exceeded its byte budget.");
                }

                pack.Write(message.Payload);
            }
        }

        if (!inPack || !terminated || pack.Length == 0)
        {
            throw new GitDataException(GitFailure.TruncatedInput, "No complete self-contained pack was returned.");
        }

        token.ThrowIfCancellationRequested();
        pack.Position = 0;
        var objects = GitObjectReader.ReadPack(pack, algorithm, options.ObjectLimits, token);
        var snapshot = GitSnapshot.Open(objects, selected, options.ObjectLimits, token);
        if (options.TrustedRegistryRootSha256 is { } expected)
        {
            var rootPath = options.RootPath.Length == 0 ? "registry.json" : options.RootPath + "/registry.json";
            var document = snapshot.ReadBlob(rootPath, token);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(document.Content)), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new GitDataException(GitFailure.IntegrityMismatch, "The acquired Registry root does not match the caller's independent commitment.");
            }
        }

        return snapshot;
    }

    private static async ValueTask<List<GitPacket>> ControlAsync(
        HttpClient client, Uri uri, byte[]? body, GitFetchOptions options, CancellationToken token)
    {
        using var response = await SendAsync(client, uri, body, options, token).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var reader = new PacketReader(stream, options.MaxControlBytes, options.MaxReferences + 128L);
        var result = new List<GitPacket>();
        while (await reader.ReadAsync(token).ConfigureAwait(false) is { } packet) { result.Add(packet); }
        if (result.Count == 0 || result[^1].Kind is not (GitPacketKind.Flush or GitPacketKind.ResponseEnd))
        {
            throw new GitDataException(GitFailure.TruncatedInput, "The Git control response did not terminate.");
        }

        return result;
    }

    private static async ValueTask<HttpResponseMessage> SendAsync(
        HttpClient client, Uri uri, byte[]? body, GitFetchOptions options, CancellationToken token)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        request.Headers.Add("Git-Protocol", "version=2");
        request.Headers.AcceptEncoding.Add(new("identity"));
        if (options.AuthorizationProvider is not null)
        {
            request.Headers.Authorization = await options.AuthorizationProvider(uri, token).ConfigureAwait(false);
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var retained = false;
        try
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new GitDataException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? GitFailure.PolicyDenied : GitFailure.RemoteFatal, $"The Git HTTP operation returned {(int)response.StatusCode}.");
            }

            var media = body is null ? "application/x-git-upload-pack-advertisement" : "application/x-git-upload-pack-result";
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, media, StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentEncoding.Any(static encoding => !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
            {
                throw Invalid("The Git response has an unexpected media type or content encoding.");
            }

            retained = true;
            return response;
        }
        finally
        {
            if (!retained) { response.Dispose(); }
        }
    }

    private static void ParseReferences(IEnumerable<GitPacket> packets, GitHashAlgorithm algorithm,
        Dictionary<string, GitObjectId> references, int limit)
    {
        foreach (var packet in packets.Where(static packet => packet.Kind == GitPacketKind.Data))
        {
            var line = Text(packet).Split('\0')[0];
            if (line.StartsWith("unborn ", StringComparison.Ordinal)) { continue; }
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) { throw Invalid("A Git reference advertisement is malformed."); }
            var id = GitObjectId.Parse(algorithm, fields[0]);
            if (references.Count >= limit) { throw new GitDataException(GitFailure.LimitExceeded, "The Git ref count limit was exceeded."); }
            if (!references.TryAdd(fields[1], id)) { throw Invalid("A Git reference was advertised more than once."); }
        }
    }

    private static byte[] Command(string name, GitHashAlgorithm algorithm, Dictionary<string, string> capabilities, IEnumerable<string> arguments)
    {
        using var body = new MemoryStream();
        Packet(body, "command=" + name + "\n");
        if (capabilities.ContainsKey("object-format")) { Packet(body, "object-format=" + AlgorithmName(algorithm) + "\n"); }
        body.Write("0001"u8);
        foreach (var argument in arguments) { Packet(body, argument); }
        body.Write("0000"u8);
        return body.ToArray();
    }

    private static void Packet(Stream output, string text)
    {
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length + 4 > GitPacketDecoder.MaximumPacketLength) { throw Invalid("A requested Git control line is too large."); }
        output.Write(Encoding.ASCII.GetBytes((bytes.Length + 4).ToString("x4", CultureInfo.InvariantCulture)));
        output.Write(bytes);
    }

    private static void AddCapability(Dictionary<string, string> capabilities, string text, int limit)
    {
        if (capabilities.Count >= limit)
        {
            throw new GitDataException(GitFailure.LimitExceeded, "The Git capability count limit was exceeded.");
        }
        var separator = text.IndexOf('=');
        var name = separator < 0 ? text : text[..separator];
        if (name.Length == 0 || !capabilities.TryAdd(name, separator < 0 ? "" : text[(separator + 1)..]))
        {
            throw Invalid("A Git capability is empty or duplicated.");
        }
    }

    private static string AlgorithmName(GitHashAlgorithm algorithm) => algorithm == GitHashAlgorithm.Sha256 ? "sha256" : "sha1";
    private static string Text(GitPacket packet)
    {
        try { return Utf8.GetString(packet.Payload).TrimEnd('\n'); }
        catch (DecoderFallbackException exception) { throw new GitDataException(GitFailure.MalformedData, "A Git control line has invalid UTF-8.", exception); }
    }
    private static GitDataException Invalid(string message) => new(GitFailure.MalformedData, message);

    private sealed class PacketReader(Stream stream, long maxBytes, long maxPackets)
    {
        private readonly GitPacketDecoder _decoder = new(maxPackets: maxPackets, maxBytes: maxBytes);
        private long _bytes;
        private bool _ended;

        internal async ValueTask<GitPacket?> ReadAsync(CancellationToken token)
        {
            var header = new byte[4];
            var first = await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
            if (first == 0)
            {
                if (!_ended) { _decoder.Complete(); _ended = true; }
                return null;
            }
            if (_ended) { throw Invalid("Data follows the Git response-end marker."); }
            Charge(1);
            await ExactAsync(header.AsMemory(1), token).ConfigureAwait(false);
            if (_decoder.TryRead(header, out _, out var control))
            {
                if (control.Kind == GitPacketKind.ResponseEnd) { _decoder.Complete(); _ended = true; }
                return control;
            }

            var size = int.Parse(Encoding.ASCII.GetString(header), NumberStyles.HexNumber, CultureInfo.InvariantCulture) - 4;
            var payload = new byte[size];
            await ExactAsync(payload, token).ConfigureAwait(false);
            if (!_decoder.TryRead(payload, out _, out var packet)) { throw Invalid("A complete pkt-line did not decode."); }
            return packet;
        }

        internal async ValueTask CopyRawRemainderAsync(Stream destination, long maxPackBytes, CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            long written = 0;
            while (true)
            {
                var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) { break; }
                Charge(count);
                if (count > maxPackBytes - written) { throw new GitDataException(GitFailure.LimitExceeded, "The raw pack exceeds its byte budget."); }
                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                written += count;
            }
        }

        private async ValueTask ExactAsync(Memory<byte> buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
                if (count == 0) { throw new GitDataException(GitFailure.TruncatedInput, "The Git pkt-line response is truncated."); }
                Charge(count);
                offset += count;
            }
        }

        private void Charge(int count)
        {
            if (count > maxBytes - _bytes) { throw new GitDataException(GitFailure.LimitExceeded, "The Git response byte budget was exceeded."); }
            _bytes += count;
        }
    }
}
