// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XRegistry.Federation;
using XRegistry.Queries;

namespace XRegistry.Samples.Bridge;

internal sealed class BridgePageStore(BridgeHostOptions options, IReadOnlyList<FederationSourceRegistration> sources)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Capture Capture, int Index)> _tokens = new(StringComparer.Ordinal);
    private readonly HashSet<Capture> _captures = [];
    private readonly string _model = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        options.Model.Source.RootElement.GetRawText() + "\n" + options.Model.EffectiveModel.RootElement.GetRawText())));
    private long _bytes;

    internal async ValueTask<BridgeResponse> CreateAsync(ClaimsPrincipal caller, RegistryPath path, string rawQuery,
        int limit, ProducerRegistryResult result, BridgeResponse response, RegistryQueryBudget budget)
    {
        RequireEnabled();
        if (result.IsDocument || response.StatusCode != 200 || result.Metadata.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeHttpException(500, "invalid_page", "Only complete collection metadata can become retained pages.");
        }
        var records = result.Metadata.EnumerateObject().ToArray();
        if (records.Length > options.PagingLimits.MaxCaptureRecords)
        {
            throw new BridgeHttpException(413, "too_large", "The complete page capture exceeds its record limit.");
        }
        var pages = new List<byte[]>();
        for (var offset = 0; offset < records.Length || offset == 0; offset += limit)
        {
            budget.Spend();
            using var output = new MemoryStream();
            using (var json = new Utf8JsonWriter(output))
            {
                json.WriteStartObject();
                for (var index = offset; index < Math.Min(offset + limit, records.Length); index++)
                {
                    budget.Spend();
                    records[index].WriteTo(json);
                }
                json.WriteEndObject();
            }
            if (output.Length > options.MaxResponseBytes)
            {
                throw new BridgeHttpException(413, "too_large", "One complete page exceeds the response byte limit.");
            }
            pages.Add(output.ToArray());
        }
        var capture = new Capture(path.EscapedPath, options.PublicRoot.AbsoluteUri.TrimEnd('/'), _model,
            Caller(caller), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawQuery))), pages.ToArray(),
            new(response.Headers, StringComparer.OrdinalIgnoreCase), result.Origins.ToArray(), result.Dependencies.ToArray(),
            records.Length, options.TimeProvider.GetTimestamp());
        if (capture.ChargedBytes > options.PagingLimits.MaxCaptureBytes || capture.Pages.Length > options.PagingLimits.MaxTokens)
        {
            throw new BridgeHttpException(413, "too_large", "The frozen page set exceeds its retention budget.");
        }
        await AuthorizeAsync(caller, capture, budget).ConfigureAwait(false);
        var initial = Response(capture, 0);
        budget.Spend(0);
        lock (_gate)
        {
            foreach (var expired in _captures.Where(Expired).ToArray()) { Remove(expired); }
            if (Expired(capture)) { throw new BridgeHttpException(410, "cursor_expired", "The page capture expired before publication."); }
            if (_captures.Count >= options.PagingLimits.MaxCaptures ||
                capture.ChargedBytes > options.PagingLimits.MaxTotalBytes - _bytes ||
                capture.Tokens.Length > options.PagingLimits.MaxTokens - _tokens.Count)
            {
                throw new BridgeHttpException(503, "cursor_capacity", "The bounded cursor store is full; live captures are not evicted.");
            }
            if (capture.Tokens.Distinct(StringComparer.Ordinal).Count() != capture.Tokens.Length || capture.Tokens.Any(_tokens.ContainsKey))
            {
                throw new BridgeHttpException(503, "cursor_capacity", "Unique cursor tokens could not be allocated.");
            }
            _captures.Add(capture);
            _bytes += capture.ChargedBytes;
            for (var index = 0; index < capture.Tokens.Length; index++) { _tokens.Add(capture.Tokens[index], (capture, index)); }
        }
        return initial;
    }

    internal async ValueTask<BridgeResponse> ReadAsync(ClaimsPrincipal caller, RegistryPath path, string token, RegistryQueryBudget budget)
    {
        RequireEnabled();
        if (token.Length != 43 || token.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_')))
        {
            throw new BridgeHttpException(400, "bad_cursor", "The cursor is not an opaque page token.");
        }
        (Capture Capture, int Index) found;
        var principal = Caller(caller);
        lock (_gate)
        {
            if (!_tokens.TryGetValue(token, out found) || found.Capture.Path != path.EscapedPath ||
                found.Capture.Root != options.PublicRoot.AbsoluteUri.TrimEnd('/') || found.Capture.Model != _model ||
                !CryptographicOperations.FixedTimeEquals(principal, found.Capture.Caller))
            {
                throw new BridgeHttpException(400, "bad_cursor", "The cursor does not belong to this caller, model, root and path.");
            }
            if (Expired(found.Capture))
            {
                Remove(found.Capture);
                throw new BridgeHttpException(410, "cursor_expired", "The frozen query capture expired.");
            }
        }
        await AuthorizeAsync(caller, found.Capture, budget).ConfigureAwait(false);
        if (Expired(found.Capture)) { throw new BridgeHttpException(410, "cursor_expired", "The frozen query capture expired during authorization."); }
        return Response(found.Capture, found.Index);
    }

    private async ValueTask AuthorizeAsync(ClaimsPrincipal caller, Capture capture, RegistryQueryBudget budget)
    {
        foreach (var origin in capture.Origins)
        {
            if (!await budget.RunAsync(token => options.AuthorizeSource(caller, origin.Name, token)).ConfigureAwait(false))
            {
                throw new BridgeHttpException(403, "cursor_forbidden", "The caller no longer has access to a captured source.");
            }
            var registration = sources.Single(source => source.Name == origin.Name);
            string stamp;
            try
            {
                stamp = registration.CurrentCredentialStamp is { } current
                    ? await budget.RunAsync(current).ConfigureAwait(false) : "";
            }
            catch (InvalidOperationException)
            {
                throw new BridgeHttpException(400, "cursor_context_changed", "A captured credential/access profile is no longer available.");
            }
            if (stamp.Length > 256 || stamp != origin.CredentialStamp)
            {
                throw new BridgeHttpException(400, "cursor_context_changed", "The captured credential/access context changed.");
            }
        }
        foreach (var dependency in capture.Dependencies)
        {
            if (!await budget.RunAsync(token => options.AuthorizeRetainedRead!(caller, dependency.Source, dependency.Path, token)).ConfigureAwait(false))
            {
                throw new BridgeHttpException(403, "cursor_forbidden", "Current authorization does not permit a captured read dependency.");
            }
        }
    }

    private BridgeResponse Response(Capture capture, int index)
    {
        var headers = new Dictionary<string, string>(capture.Headers, StringComparer.OrdinalIgnoreCase);
        var links = new List<string>();
        if (headers.TryGetValue("Link", out var existing)) { links.Add(existing); }
        if (index + 1 < capture.Pages.Length) { links.Add(Link("next", index + 1)); }
        if (index != 0) { links.Add(Link("prev", index - 1)); }
        links.Add(Link("first", 0));
        links.Add(Link("last", capture.Pages.Length - 1));
        headers["Link"] = string.Join(", ", links);
        headers["X-Bridge-Capture"] = "frozen-result-bytes";
        BridgeApplication.ValidateResponseHeaders(headers, options.MaxHeaderBytes);
        return new BridgeResponse(200, capture.Pages[index], headers);

        string Link(string relation, int page) =>
            "<" + capture.Root + capture.Path + "?cursor=" + capture.Tokens[page] + ">;rel=" + relation +
            ";count=" + capture.Records.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool Expired(Capture capture) =>
        options.TimeProvider.GetElapsedTime(capture.Started, options.TimeProvider.GetTimestamp()) >= options.PagingLimits.Lifetime;

    private void Remove(Capture capture)
    {
        if (!_captures.Remove(capture)) { return; }
        foreach (var token in capture.Tokens) { _tokens.Remove(token); }
        _bytes -= capture.ChargedBytes;
    }

    internal void Clear()
    {
        lock (_gate) { _tokens.Clear(); _captures.Clear(); _bytes = 0; }
    }

    private void RequireEnabled()
    {
        if (options.AuthorizeRetainedRead is null)
        {
            throw new BridgeHttpException(501, "paging_not_configured", "Retained paging requires an explicit current-read authorization policy.");
        }
    }

    private static byte[] Caller(ClaimsPrincipal caller)
    {
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartArray();
            foreach (var identity in caller.Identities)
            {
                json.WriteStartObject();
                json.WriteBoolean("authenticated", identity.IsAuthenticated);
                json.WriteString("scheme", identity.AuthenticationType);
                json.WriteString("nameType", identity.NameClaimType);
                json.WriteString("roleType", identity.RoleClaimType);
                json.WriteStartArray("claims");
                foreach (var claim in identity.Claims.OrderBy(claim => claim.Type, StringComparer.Ordinal)
                    .ThenBy(claim => claim.Value, StringComparer.Ordinal).ThenBy(claim => claim.Issuer, StringComparer.Ordinal)
                    .ThenBy(claim => claim.OriginalIssuer, StringComparer.Ordinal).ThenBy(claim => claim.ValueType, StringComparer.Ordinal))
                {
                    json.WriteStartArray();
                    json.WriteStringValue(claim.Type);
                    json.WriteStringValue(claim.Value);
                    json.WriteStringValue(claim.ValueType);
                    json.WriteStringValue(claim.Issuer);
                    json.WriteStringValue(claim.OriginalIssuer);
                    json.WriteEndArray();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        if (output.Length > 64 * 1024) { throw new BridgeHttpException(413, "too_large", "The caller identity exceeds its binding budget."); }
        return SHA256.HashData(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
    }

    private sealed class Capture(string path, string root, string model, byte[] caller, string query, byte[][] pages,
        Dictionary<string, string> headers, FederationViewOrigin[] origins, FederationViewDependency[] dependencies, int records, long started)
    {
        internal string Path { get; } = path;
        internal string Root { get; } = root;
        internal string Model { get; } = model;
        internal byte[] Caller { get; } = caller;
        internal string Query { get; } = query;
        internal byte[][] Pages { get; } = pages;
        internal Dictionary<string, string> Headers { get; } = headers;
        internal FederationViewOrigin[] Origins { get; } = origins;
        internal FederationViewDependency[] Dependencies { get; } = dependencies;
        internal int Records { get; } = records;
        internal long Started { get; } = started;
        internal string[] Tokens { get; } = pages.Select(_ => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')).ToArray();
        internal long ChargedBytes => Pages.Sum(page => (long)page.Length + 2048) +
            Headers.Sum(header => Encoding.UTF8.GetByteCount(header.Key + header.Value) * 2L) +
            Dependencies.Sum(dependency => Encoding.UTF8.GetByteCount(dependency.Source + dependency.Path.EscapedPath) * 2L + 64) +
            Origins.Sum(origin => Encoding.UTF8.GetByteCount(origin.Name + origin.Context.Source + origin.Context.Revision + origin.CredentialStamp) * 2L + 256) +
            Encoding.UTF8.GetByteCount(Path + Root + Model + Query) * 2L + Caller.Length;
    }
}
