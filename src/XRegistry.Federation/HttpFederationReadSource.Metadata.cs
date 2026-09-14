using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Client;

namespace XRegistry.Federation;

public sealed partial class HttpFederationReadSource
{
    private async ValueTask<FederationReadResult> EntityAsync(FederationReadRequest request, CancellationToken token)
    {
        ValidateType(request.Parts);
        var path = WirePath(request.Parts);
        if (request.Parts.Length is 4 or 6 && DocumentTreeFormat.Resource(Model, request.Parts).HasDocument)
        {
            path += "$details";
        }
        var entity = await MetadataAsync(path, request.Target, ViewQuery(request.Representation), token).ConfigureAwait(false);
        return FromJson(request.Target, new JsonObject
        {
            ["kind"] = Kind(request.Parts.Length),
            ["entity"] = EnvelopeEntity(entity, request.Parts, request.Representation, "/entity")
        }, token);
    }

    private async ValueTask<FederationReadResult> CollectionAsync(FederationReadRequest request, CancellationToken token)
    {
        ValidateType(request.Parts);
        var query = ViewQuery(request.Representation);
        var path = WirePath(request.Parts);
        var current = new Uri(_client.Root, path + (query is null ? "" : "?doc"));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong? count = null;
        XRegistryHttpResponse? pending = await SendAsync(path, request.Target, query, token).ConfigureAwait(false);
        try
        {
            while (pending is not null)
            {
                using var response = pending;
                pending = null;
                if (!visited.Add(current.AbsoluteUri))
                {
                    throw new FederationException(FederationErrorCode.LimitExceeded, "HTTP pagination repeated a page.");
                }
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var error = await FailureAsync(response, token).ConfigureAwait(false);
                    throw visited.Count > 1 && error.Code is not (FederationErrorCode.PolicyDenied or FederationErrorCode.UnsupportedVersion)
                        ? new FederationException(FederationErrorCode.InconsistentSnapshot,
                            "A later collection page could not complete the capture.", error.Diagnostic, error)
                        { HttpStatusCode = error.HttpStatusCode, HttpProblemDetails = error.HttpProblemDetails }
                        : error;
                }
                var page = FederationJson.Parse(await BodyAsync(response, token).ConfigureAwait(false), _budget, token);
                foreach (var record in page.EnumerateObject())
                {
                    _budget.ChargeWork();
                    if (!FederationSyntax.Id(record.Name))
                    {
                        throw FederationJson.Invalid("An HTTP collection has an invalid member ID.");
                    }
                    var xid = request.Target + "/" + record.Name;
                    ValidateIdentity(record.Value, xid);
                    if (!records.TryAdd(record.Name, record.Value))
                    {
                        throw new FederationException(FederationErrorCode.InconsistentSnapshot, "Collection pages repeated a member ID.");
                    }
                    if (!identifiers.Add(record.Name))
                    {
                        throw FederationJson.Invalid("Collection members have case-insensitively colliding Core IDs.");
                    }
                }
                RegistryHttpLink? next = null;
                foreach (var link in RegistryHttpLink.Parse(response.Headers.TryGetValues("Link", out var fields) ? fields : []))
                {
                    if (!link.Relations.Any(relation => relation.Equals("next", StringComparison.OrdinalIgnoreCase) ||
                        relation.Equals("prev", StringComparison.OrdinalIgnoreCase) ||
                        relation.Equals("first", StringComparison.OrdinalIgnoreCase) ||
                        relation.Equals("last", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    if (link.Parameters.ContainsKey("anchor"))
                    {
                        throw new FederationException(FederationErrorCode.UnsupportedOperation, "An anchored page link requires a different context.");
                    }
                    if (link.Parameters.TryGetValue("count", out var declared))
                    {
                        if (!ulong.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
                            count is not null && total != count)
                        {
                            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The HTTP collection count is invalid or changed.");
                        }
                        count = total;
                    }
                    if (link.HasRelation("next"))
                    {
                        if (next is not null && next.Reference != link.Reference)
                        {
                            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "An HTTP collection has competing next links.");
                        }
                        next = link;
                    }
                }
                if (count is not null && ((ulong)records.Count > count || next is null && (ulong)records.Count != count))
                {
                    throw new FederationException(FederationErrorCode.InconsistentSnapshot, "HTTP pages do not match the advertised complete count.");
                }
                if (next is not null)
                {
                    _budget.ChargeRequest();
                    _budget.ChargeObject();
                    try
                    {
                        var continuation = await _client.GetLinkAsync(current, next.Reference, token).ConfigureAwait(false);
                        current = continuation.RequestUri;
                        pending = continuation.Response;
                        Observe(pending, current, request.Target);
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new FederationException(FederationErrorCode.PolicyDenied, "The HTTP page target failed locator validation.", innerException: exception);
                    }
                }
            }
        }
        finally
        {
            pending?.Dispose();
        }

        if (request.Selector is not null)
        {
            var matches = new List<KeyValuePair<string, JsonElement>>();
            foreach (var record in records)
            {
                var metadata = await SelectionMetadataAsync(record.Value, [.. request.Parts, record.Key],
                    request.Representation, token).ConfigureAwait(false);
                if (request.Selector.Matches(metadata.TryGetProperty("labels", out var labels) ? labels : default))
                {
                    matches.Add(record);
                }
            }
            if (matches.Count != 1)
            {
                throw new FederationException(matches.Count == 0 ? FederationErrorCode.NotFound : FederationErrorCode.Ambiguous,
                    "The complete HTTP collection does not contain exactly one literal label match.");
            }
            var selected = matches[0];
            return FromJson(request.Target + "/" + selected.Key, new JsonObject
            {
                ["kind"] = Kind(request.Parts.Length + 1),
                ["entity"] = EnvelopeEntity(selected.Value, [.. request.Parts, selected.Key], request.Representation,
                    "/entity", "/" + PointerToken(selected.Key))
            }, token);
        }

        var entities = new JsonObject();
        foreach (var record in records)
        {
            entities[record.Key] = EnvelopeEntity(record.Value, [.. request.Parts, record.Key], request.Representation, "/entities");
        }
        return FromJson(request.Target, new JsonObject
        {
            ["kind"] = "collection",
            ["xid"] = request.Target,
            ["complete"] = true,
            ["entities"] = entities
        }, token);
    }

    private async ValueTask<JsonElement> SelectionMetadataAsync(JsonElement entity, string[] parts,
        FederationRepresentation representation, CancellationToken token)
    {
        if (parts.Length != 4 || representation == FederationRepresentation.ApiView)
        {
            return entity;
        }
        if (entity.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("defaultversionid", out var defaultId) && defaultId.ValueKind == JsonValueKind.String &&
            entity.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Object)
        {
            if (!versions.TryGetProperty(defaultId.GetString()!, out var version))
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot, "Document-view default metadata is absent from its Version collection.");
            }
            ValidateIdentity(version, "/" + WirePath(parts) + "/versions/" + Uri.EscapeDataString(defaultId.GetString()!));
            return version;
        }
        var path = WirePath(parts);
        if (DocumentTreeFormat.Resource(Model, parts).HasDocument) { path += "$details"; }
        var projected = await MetadataAsync(path, "/" + WirePath(parts), null, token).ConfigureAwait(false);
        if (meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("defaultversionid", out var capturedDefault) &&
            (capturedDefault.ValueKind != JsonValueKind.String ||
                !projected.TryGetProperty("versionid", out var observedDefault) || observedDefault.ValueKind != JsonValueKind.String ||
                observedDefault.GetString() != capturedDefault.GetString()))
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "The default Version changed while acquiring metadata for document-view selection.");
        }
        return projected;
    }

    private static string Kind(int segments) => segments switch
    {
        0 => "registry",
        2 => "group",
        4 => "resource",
        5 => "meta",
        6 => "version",
        _ => throw FederationJson.Invalid("Invalid entity containment shape.")
    };

    private void ValidateType(string[] parts)
    {
        if (parts.Length >= 3)
        {
            DocumentTreeFormat.Resource(Model, parts);
        }
        else if (parts.Length != 0)
        {
            DocumentTreeFormat.Group(Model, parts);
        }
    }

    private IReadOnlyList<KeyValuePair<string, string?>>? ViewQuery(FederationRepresentation representation)
    {
        if (representation == FederationRepresentation.ApiView)
        {
            return null;
        }
        if (!Capabilities.TryGetProperty("flags", out var flags) || flags.ValueKind != JsonValueKind.Array ||
            !flags.EnumerateArray().Any(flag => flag.ValueKind == JsonValueKind.String &&
                string.Equals(flag.GetString(), "doc", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "This source has no positive evidence for the document-view flag.");
        }
        return [new("doc", null)];
    }

    private JsonObject EnvelopeEntity(JsonElement entity, string[] parts, FederationRepresentation representation,
        string pointer, string sourcePointer = "")
    {
        var result = JsonNode.Parse(entity.GetRawText())!.AsObject();
        if (representation == FederationRepresentation.DocumentView)
        {
            if (!entity.TryGetProperty("self", out var self) || self.ValueKind != JsonValueKind.String)
            {
                throw FederationJson.Invalid("A document-view entity must retain its self pointer.");
            }
            RebasePointers(result, parts, pointer, sourcePointer);
        }
        return result;
    }

    private void RebasePointers(JsonObject entity, string[] parts, string rootPointer, string sourcePointer)
    {
        var navigation = new HashSet<string>(StringComparer.Ordinal) { "self", "shortself", "metaurl", "defaultversionurl" };
        IEnumerable<string> collections = parts.Length switch
        {
            0 => Model.Groups.Keys,
            2 => Model.Groups[parts[0]].Resources.Keys,
            4 => ["versions"],
            _ => []
        };
        foreach (var collection in collections)
        {
            navigation.Add(collection + "url");
            if (entity[collection] is JsonObject members)
            {
                foreach (var member in members)
                {
                    if (member.Value is not JsonObject child)
                    {
                        throw FederationJson.Invalid("An inlined collection member must be an entity.");
                    }
                    RebasePointers(child, [.. parts, collection, member.Key], rootPointer, sourcePointer);
                }
            }
        }
        if (parts.Length == 4 && entity["meta"] is JsonObject meta)
        {
            RebasePointers(meta, [.. parts, "meta"], rootPointer, sourcePointer);
        }
        foreach (var name in navigation)
        {
            if (entity[name] is JsonValue value && value.TryGetValue<string>(out var link))
            {
                if (!link.StartsWith('#') || (sourcePointer.Length != 0 &&
                    link != "#" + sourcePointer && !link.StartsWith("#" + sourcePointer + "/", StringComparison.Ordinal)))
                {
                    throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                        "The source did not return document-view navigation despite the requested flag.");
                }
                entity[name] = "#" + rootPointer + link[(1 + sourcePointer.Length)..];
            }
        }
    }

    private static string PointerToken(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
