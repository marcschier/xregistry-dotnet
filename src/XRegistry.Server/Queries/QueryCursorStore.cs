using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XRegistry.Server;

internal sealed record FrozenQueryPage(byte[] Json, int Records);

internal sealed class PreparedQueryCursor(
    string collection, string root, string modelRevision, byte[] caller, byte[] query,
    FrozenQueryPage[] pages, string[] dependencies, int records, DateTimeOffset expires, long createdTimestamp,
    string capabilitiesRevision, bool checkCapabilities)
{
    internal string Collection { get; } = collection;
    internal string Root { get; } = root;
    internal string ModelRevision { get; } = modelRevision;
    internal byte[] Caller { get; } = caller;
    internal byte[] Query { get; } = query;
    internal FrozenQueryPage[] Pages { get; } = pages;
    internal string[] Dependencies { get; } = dependencies;
    internal int Records { get; } = records;
    internal DateTimeOffset Expires { get; } = expires;
    internal long CreatedTimestamp { get; } = createdTimestamp;
    internal string CapabilitiesRevision { get; } = capabilitiesRevision;
    internal bool CheckCapabilities { get; } = checkCapabilities;
    internal string[] Tokens { get; } = pages.Select(static _ => Token()).ToArray();
    internal long ChargedBytes => Pages.Sum(static page => (long)page.Json.Length + 64) +
        Dependencies.Sum(static path => Encoding.UTF8.GetByteCount(path) * 2L + 64) +
        Tokens.Length * 256L + Encoding.UTF8.GetByteCount(Collection + Root + ModelRevision) * 2L + Caller.Length + Query.Length + 512;

    internal RegistryResult Result(int index, RegistryPath path, RegistryJsonLimits limits)
    {
        var links = new List<RegistryPageLink>();
        if (index < Pages.Length - 1)
        {
            links.Add(Link("next", index + 1));
        }

        if (index != 0)
        {
            links.Add(Link("prev", index - 1));
        }

        links.Add(Link("first", 0));
        links.Add(Link("last", Pages.Length - 1));
        return new(RegistryResultKind.Success, path, RegistryJson.Parse(Pages[index].Json, limits))
        {
            ContentType = "application/json; charset=utf-8",
            Page = new((ulong)Records, Expires, links.AsReadOnly())
        };
    }

    private RegistryPageLink Link(string relation, int page) => new(relation,
        new Uri(Root + Collection + "?cursor=" + Tokens[page], UriKind.Absolute));

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class QueryCursorStore(RegistryQueryLimits limits, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (PreparedQueryCursor Cursor, int Page)> _tokens = new(StringComparer.Ordinal);
    private readonly HashSet<PreparedQueryCursor> _sets = [];
    private long _bytes;
    private long _records;

    internal void Admit(PreparedQueryCursor cursor)
    {
        lock (_gate)
        {
            Sweep();
            if (Expired(cursor))
            {
                throw ServerErrors.Create("cursor_expired", cursor.Collection, "The prepared cursor expired before it could be returned.");
            }

            if (cursor.Pages.Length > limits.MaxCursorTokens || cursor.Records > limits.MaxCursorRecords ||
                cursor.ChargedBytes > limits.MaxCursorBytes)
            {
                throw ServerErrors.Create("too_large", cursor.Collection, "The frozen result set exceeds its cursor retention budget.");
            }

            if (_sets.Count >= limits.MaxCursors || cursor.Pages.Length > limits.MaxCursorTokens - _tokens.Count ||
                cursor.Records > limits.MaxTotalCursorRecords - _records ||
                cursor.ChargedBytes > limits.MaxTotalCursorBytes - _bytes)
            {
                throw ServerErrors.Create("server_busy", cursor.Collection, "The bounded cursor store is full; existing cursors are not evicted before expiry.");
            }

            if (cursor.Tokens.Distinct(StringComparer.Ordinal).Count() != cursor.Tokens.Length ||
                cursor.Tokens.Any(_tokens.ContainsKey))
            {
                throw ServerErrors.Create("server_busy", cursor.Collection, "A unique opaque cursor could not be allocated.");
            }

            _sets.Add(cursor);
            for (var index = 0; index < cursor.Tokens.Length; index++)
            {
                _tokens.Add(cursor.Tokens[index], (cursor, index));
            }

            _bytes += cursor.ChargedBytes;
            _records += cursor.Records;
        }
    }

    internal (PreparedQueryCursor Cursor, int Page) Find(string token, string collection, string root, byte[] caller)
    {
        if (token.Length != 43 || token.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw ServerErrors.Create("bad_cursor", collection, "The opaque cursor is malformed.");
        }

        lock (_gate)
        {
            if (!_tokens.TryGetValue(token, out var found))
            {
                Sweep();
                throw ServerErrors.Create("bad_cursor", collection, "The opaque cursor is invalid or is no longer retained.");
            }

            if (Expired(found.Cursor))
            {
                Remove(found.Cursor);
                throw ServerErrors.Create("cursor_expired", collection, "The frozen result set has expired.");
            }

            if (found.Cursor.Collection != collection || found.Cursor.Root != root)
            {
                throw ServerErrors.Create("bad_cursor", collection, "The cursor belongs to a different Registry or collection.");
            }

            if (!CryptographicOperations.FixedTimeEquals(found.Cursor.Caller, caller))
            {
                throw ServerErrors.Create("forbidden", collection, "The cursor belongs to a different caller security context.");
            }

            return found;
        }
    }

    internal void Check(PreparedQueryCursor cursor)
    {
        lock (_gate)
        {
            if (!_sets.Contains(cursor) || Expired(cursor))
            {
                Remove(cursor);
                throw ServerErrors.Create("cursor_expired", cursor.Collection, "The frozen result set is no longer available.");
            }
        }
    }

    internal void Invalidate(PreparedQueryCursor cursor)
    {
        lock (_gate)
        {
            Remove(cursor);
        }
    }

    private void Sweep()
    {
        foreach (var cursor in _sets.Where(Expired).ToArray())
        {
            Remove(cursor);
        }
    }

    private bool Expired(PreparedQueryCursor cursor) => clock.GetUtcNow() >= cursor.Expires ||
        clock.GetElapsedTime(cursor.CreatedTimestamp, clock.GetTimestamp()) >= limits.CursorLifetime;

    private void Remove(PreparedQueryCursor cursor)
    {
        if (!_sets.Remove(cursor))
        {
            return;
        }

        foreach (var token in cursor.Tokens)
        {
            _tokens.Remove(token);
        }

        _bytes -= cursor.ChargedBytes;
        _records -= cursor.Records;
    }
}

internal static class QueryBinding
{
    internal static byte[] Caller(ClaimsPrincipal caller, QueryBudget budget)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var identity in caller.Identities)
            {
                budget.Spend();
                writer.WriteStartObject();
                Write(writer, "authentication", identity.AuthenticationType, budget);
                writer.WriteBoolean("authenticated", identity.IsAuthenticated);
                Write(writer, "nameType", identity.NameClaimType, budget);
                Write(writer, "roleType", identity.RoleClaimType, budget);
                writer.WriteStartArray("claims");
                foreach (var claim in identity.Claims)
                {
                    budget.Spend();
                    writer.WriteStartObject();
                    Write(writer, "type", claim.Type, budget);
                    Write(writer, "value", claim.Value, budget);
                    Write(writer, "valueType", claim.ValueType, budget);
                    Write(writer, "issuer", claim.Issuer, budget);
                    Write(writer, "originalIssuer", claim.OriginalIssuer, budget);
                    budget.Spend(claim.Properties.Count);
                    writer.WriteStartObject("properties");
                    foreach (var property in claim.Properties.OrderBy(static property => property.Key, StringComparer.Ordinal))
                    {
                        Write(writer, property.Key, property.Value, budget);
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    internal static byte[] Query(IReadOnlyList<KeyValuePair<string, string?>> parameters, QueryBudget budget)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var parameter in parameters)
            {
                writer.WriteStartObject();
                Write(writer, "name", parameter.Key, budget);
                Write(writer, "value", parameter.Value, budget);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    private static void Write(Utf8JsonWriter writer, string name, string? value, QueryBudget budget)
    {
        budget.Spend(name.Length + (value?.Length ?? 0) + 1);
        writer.WriteString(name, value);
    }
}
