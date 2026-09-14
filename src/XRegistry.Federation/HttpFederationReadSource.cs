using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Client;
using XRegistry.Http;

namespace XRegistry.Federation;

/// <summary>A selected, bounded live HTTP Registry source using the existing core HTTP APIs.</summary>
/// <remarks>
/// Owns its client and session deadline. Reads are serialized rather than silently queued.
/// Observations are best-effort live reads, not an atomic or immutable multi-URL snapshot.
/// </remarks>
public sealed partial class HttpFederationReadSource : IFederationReadSource, IDisposable
{
    private readonly XRegistryHttpClient _client;
    private readonly FederationReadBudget _budget;
    private readonly HttpFederationReadOptions _options;
    private readonly CancellationTokenSource _deadline;
    private readonly List<HttpFederationObservation> _observations = [];
    private readonly ReadOnlyCollection<HttpFederationObservation> _observationView;
    private JsonElement _effectiveModel;
    private JsonElement _modelSource;
    private int _reading;
    private int _disposed;

    private HttpFederationReadSource(XRegistryHttpClient client, HttpFederationReadOptions options,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        _client = client;
        _options = options;
        _budget = budget;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _deadline.CancelAfter(options.Timeout);
        _observationView = _observations.AsReadOnly();
        Context = new NativeRegistryContext("http", client.Root.AbsoluteUri);
    }

    /// <inheritdoc />
    public NativeRegistryContext Context { get; }
    /// <summary>Gets the independently verified Core version.</summary>
    public string SpecVersion { get; private set; } = "";
    /// <inheritdoc />
    public RegistryModel Model { get; private set; } = null!;
    /// <inheritdoc />
    public JsonElement Capabilities { get; private set; }
    /// <summary>Gets whether the source supplied enabled capabilities, rather than an absent optional API.</summary>
    public bool HasCapabilitiesEvidence { get; private set; }
    /// <summary>Gets bounded retrieval evidence without authorizing shared caches or granting an immutable pin.</summary>
    public IReadOnlyList<HttpFederationObservation> Observations => _observationView;

    /// <summary>Verifies the Registry version before acquiring model and enabled-capability context.</summary>
    public static ValueTask<HttpFederationReadSource> OpenAsync(
        Uri registryRoot, XRegistryHttpClientOptions? transportOptions = null,
        HttpFederationReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        return OpenAsync(registryRoot, transportOptions, options, new(options.Limits), cancellationToken);
    }

    /// <summary>Opens a source using an existing operation-wide budget, including bootstrap and subsequent HTTP reads.</summary>
    /// <remarks>When options are supplied, their Limits must be the shared budget's Limits instance. No independent counter set is created.</remarks>
    public static async ValueTask<HttpFederationReadSource> OpenAsync(
        Uri registryRoot, XRegistryHttpClientOptions? transportOptions, HttpFederationReadOptions? options,
        FederationReadBudget budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registryRoot);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new() { Limits = budget.Limits };
        ArgumentNullException.ThrowIfNull(options.Limits);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (!ReferenceEquals(options.Limits, budget.Limits))
        {
            throw new ArgumentException("Shared HTTP source options must use budget.Limits.", nameof(options));
        }
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A finite live-capture deadline up to ten minutes is required.");
        }
        if (options.RequireImmutableSnapshot)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "Ordinary live HTTP cannot supply an immutable producer snapshot pin.");
        }

        budget.ChargeSource();
        var source = new HttpFederationReadSource(new XRegistryHttpClient(registryRoot, transportOptions), options, budget, cancellationToken);
        var opened = false;
        try
        {
            var root = await source.MetadataAsync("", "/", null, source._deadline.Token).ConfigureAwait(false);
            source.SpecVersion = FederationJson.String(root, "specversion");
            if (source.SpecVersion != "1.0-rc4")
            {
                throw new FederationException(FederationErrorCode.UnsupportedVersion, "The selected HTTP Registry returns an unsupported Core version.");
            }
            source.ValidateIdentity(root, "/");

            source._effectiveModel = root.TryGetProperty("model", out var model) ? model :
                await source.MetadataAsync("model", null, null, source._deadline.Token).ConfigureAwait(false);
            if (root.TryGetProperty("capabilities", out var capabilities))
            {
                source.Capabilities = capabilities;
                source.HasCapabilitiesEvidence = true;
            }
            else
            {
                try
                {
                    source.Capabilities = await source.MetadataAsync("capabilities", null, null, source._deadline.Token).ConfigureAwait(false);
                    source.HasCapabilitiesEvidence = true;
                }
                catch (FederationException exception) when (exception.Code == FederationErrorCode.UnsupportedOperation)
                {
                    source.Capabilities = RegistryJson.Parse("{}").RootElement;
                    source.HasCapabilitiesEvidence = false;
                }
            }
            FederationCapabilities.GetResolutionOwner(source.Capabilities);

            source._modelSource = root.TryGetProperty("modelsource", out var declared) ? declared : default;
            if (source._modelSource.ValueKind == JsonValueKind.Undefined && source.ApiAvailable("modelsource"))
            {
                source._modelSource = await source.MetadataAsync("modelsource", null, null, source._deadline.Token).ConfigureAwait(false);
            }
            source.Model = RegistryModel.Compile(RegistryJson.FromElement(
                source._modelSource.ValueKind == JsonValueKind.Undefined ? source._effectiveModel : source._modelSource),
                new RegistryModelCompilationOptions
                {
                    SourceUri = new Uri(source._client.Root, "modelsource"),
                    Resolver = options.ModelResolver is null ? null : new ModelResolver(options.ModelResolver, source._budget, source._deadline.Token),
                    JsonLimits = new()
                    {
                        MaxBytes = options.Limits.MaxObjectBytes,
                        MaxDepth = options.Limits.MaxJsonDepth,
                        MaxNodes = (int)Math.Max(1, Math.Min(int.MaxValue, options.Limits.MaxWork - source._budget.Work)),
                    },
                    MaxExpandedNodes = (int)Math.Max(1, Math.Min(int.MaxValue, options.Limits.MaxWork - source._budget.Work)),
                });
            if (!JsonNode.DeepEquals(JsonNode.Parse(source._effectiveModel.GetRawText()),
                JsonNode.Parse(source.Model.EffectiveModel.RootElement.GetRawText())))
            {
                throw FederationJson.Invalid("The HTTP effective model contradicts the captured model material.");
            }
            opened = true;
            return source;
        }
        catch (RegistryException exception) when (exception.Diagnostic.Code is "model_resolution_required" or "model_resolution_failed")
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "The source model requires explicitly authorized include resolution.", exception.Diagnostic.Code, exception);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
        catch (HttpRequestException exception)
        {
            throw TransportFailure(exception);
        }
        finally
        {
            if (!opened)
            {
                source.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<FederationReadResult> ReadAsync(
        FederationReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException("A live HTTP source session permits only one active read.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (request.Operation == FederationOperation.Document)
            {
                return await DocumentAsync(request, linked.Token).ConfigureAwait(false);
            }
            if (request.Operation == FederationOperation.Model)
            {
                var material = new JsonObject
                {
                    ["model"] = JsonNode.Parse(_effectiveModel.GetRawText())
                };
                if (_modelSource.ValueKind != JsonValueKind.Undefined)
                {
                    material["modelsource"] = JsonNode.Parse(_modelSource.GetRawText());
                }
                return FromJson("/", material, linked.Token);
            }
            if (request.Operation == FederationOperation.Capabilities)
            {
                if (!HasCapabilitiesEvidence)
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation,
                        "The selected source supplied no enabled-capabilities representation.");
                }
                return FromJson("/", JsonNode.Parse(Capabilities.GetRawText())!.AsObject(), linked.Token);
            }
            if (request.Operation == FederationOperation.Collection)
            {
                return await CollectionAsync(request, linked.Token).ConfigureAwait(false);
            }
            return await EntityAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
        catch (HttpRequestException exception)
        {
            throw TransportFailure(exception);
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    private async ValueTask<FederationReadResult> DocumentAsync(
        FederationReadRequest request, CancellationToken token, bool followedAlias = false)
    {
        var resource = DocumentTreeFormat.Resource(Model, request.Parts);
        if (!resource.HasDocument)
        {
            throw new FederationException(FederationErrorCode.UnsupportedOperation, "The requested Resource model has no domain Document.");
        }
        var versionXid = request.Target;
        JsonElement meta = default;
        if (request.Parts.Length == 4)
        {
            meta = await MetadataAsync(WirePath(request.Parts) + "/meta", request.Target + "/meta", null, token).ConfigureAwait(false);
            if (meta.TryGetProperty("xref", out var alias))
            {
                if (followedAlias)
                {
                    throw new FederationException(FederationErrorCode.NotFound, "A second alias hop has no retrievable Document.");
                }
                var target = alias.ValueKind == JsonValueKind.String ? alias.GetString()! :
                    throw FederationJson.Invalid("A Resource alias must be a local typed XID.");
                var targetParts = FederationSyntax.Xid(target);
                if (targetParts.Length != 4 ||
                    !ReferenceEquals(resource, DocumentTreeFormat.Resource(Model, targetParts)))
                {
                    throw FederationJson.Invalid("An alias must reference the same Resource model type in this Registry.");
                }
                var selectedResult = await DocumentAsync(
                    new FederationReadRequest(FederationOperation.Document, target, representation: request.Representation),
                    token, followedAlias: true).ConfigureAwait(false);
                var aliasAfter = await MetadataAsync(WirePath(request.Parts) + "/meta",
                    request.Target + "/meta", null, token).ConfigureAwait(false);
                if (!JsonNode.DeepEquals(JsonNode.Parse(meta.GetRawText()), JsonNode.Parse(aliasAfter.GetRawText())))
                {
                    throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The local alias changed during its dependent read.");
                }
                return selectedResult;
            }
            var selected = FederationJson.String(meta, "defaultversionid");
            FederationSyntax.Xid(request.Target + "/versions/" + selected);
            versionXid += "/versions/" + selected;
        }
        try
        {
            var path = WirePath(FederationSyntax.Xid(versionXid));
            using var response = await SendAsync(path, versionXid, null, token).ConfigureAwait(false);
            FederationReadResult result;
            if (response.StatusCode == HttpStatusCode.SeeOther)
            {
                var body = await BodyAsync(response, token).ConfigureAwait(false);
                var location = response.Headers.Location?.OriginalString;
                if (body.Length != 0 || location is null)
                {
                    throw FederationJson.Invalid("An external Document redirect requires an empty body and a Location.");
                }
                var version = await MetadataAsync(path + "$details", versionXid, null, token).ConfigureAwait(false);
                var uri = FederationJson.String(version, resource.Singular + "url");
                FederationJson.UriReference(uri);
                if (!StringComparer.Ordinal.Equals(location, uri))
                {
                    throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The Document redirect disagrees with its Version metadata.");
                }
                var descriptor = new JsonObject { ["kind"] = "external", ["uri"] = uri };
                if (!Uri.IsWellFormedUriString(uri, UriKind.Absolute))
                {
                    descriptor["base"] = new Uri(_client.Root, path).AbsoluteUri;
                }
                result = FederationReadResult.FromExternalDocument(versionXid, Context,
                    RegistryJson.Parse(descriptor.ToJsonString()).RootElement);
            }
            else if (response.StatusCode != HttpStatusCode.OK)
            {
                var failure = await FailureAsync(response, token).ConfigureAwait(false);
                throw meta.ValueKind != JsonValueKind.Undefined && failure.Code is not (FederationErrorCode.PolicyDenied or
                    FederationErrorCode.UnsupportedVersion or FederationErrorCode.IntegrityError or FederationErrorCode.LimitExceeded)
                    ? new FederationException(FederationErrorCode.InconsistentSnapshot,
                        "A previously selected default Version could not complete the Document capture.", failure.Diagnostic, failure)
                    { HttpStatusCode = failure.HttpStatusCode, HttpProblemDetails = failure.HttpProblemDetails }
                    : failure;
            }
            else
            {
                if (response.Headers.TryGetValues("xRegistry-versionid", out var ids))
                {
                    var values = ids.Take(2).ToArray();
                    string decoded;
                    try
                    {
                        decoded = values.Length == 1 ? RegistryHeaderEncoding.Decode(values[0]) :
                            throw new FormatException("The Version identity header is repeated.");
                    }
                    catch (FormatException exception)
                    {
                        throw new FederationException(FederationErrorCode.InvalidPackage,
                            "The HTTP Version identity header is malformed.", innerException: exception)
                        { HttpStatusCode = (int)response.StatusCode };
                    }
                    if (decoded != FederationSyntax.Xid(versionXid)[^1])
                    {
                        throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The Document response belongs to a different Version.");
                    }
                }
                var bytes = await BodyAsync(response, token).ConfigureAwait(false);
                result = FederationReadResult.FromDocument(versionXid, Context, new FederationDocument(
                    bytes, response.ContentHeaders.ContentType?.ToString(), documentOrigin: new Uri(_client.Root, path).AbsoluteUri));
            }
            if (meta.ValueKind != JsonValueKind.Undefined)
            {
                var after = await MetadataAsync(WirePath(request.Parts) + "/meta", request.Target + "/meta", null, token).ConfigureAwait(false);
                if (!JsonNode.DeepEquals(JsonNode.Parse(meta.GetRawText()), JsonNode.Parse(after.GetRawText())))
                {
                    throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The default Version selection changed while reading its Document.");
                }
            }
            return result;
        }
        catch (HttpRequestException exception) when (meta.ValueKind != JsonValueKind.Undefined)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "The transport could not complete a previously selected default-Version capture.", innerException: exception)
            { HttpStatusCode = (int?)exception.StatusCode };
        }
        catch (FederationException exception) when (meta.ValueKind != JsonValueKind.Undefined &&
            exception.Code is FederationErrorCode.NotFound or FederationErrorCode.Unavailable)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "A dependent read could not complete the previously selected default-Version capture.", exception.Diagnostic, exception)
            { HttpStatusCode = exception.HttpStatusCode, HttpProblemDetails = exception.HttpProblemDetails };
        }
    }

    private async ValueTask<JsonElement> MetadataAsync(string path, string? xid,
        IReadOnlyList<KeyValuePair<string, string?>>? query, CancellationToken token)
    {
        using var response = await SendAsync(path, xid, query, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, token).ConfigureAwait(false);
        }
        var json = FederationJson.Parse(await BodyAsync(response, token).ConfigureAwait(false), _budget, token);
        if (xid is not null)
        {
            ValidateIdentity(json, xid);
        }
        return json;
    }

    private async ValueTask<XRegistryHttpResponse> SendAsync(string path, string? xid,
        IReadOnlyList<KeyValuePair<string, string?>>? query, CancellationToken token)
    {
        _budget.ChargeRequest();
        _budget.ChargeObject();
        var response = await _client.SendAsync(HttpMethod.Get, path, query: query, cancellationToken: token).ConfigureAwait(false);
        try
        {
            var uri = new Uri(_client.Root, path);
            if (query is not null)
            {
                uri = new Uri(uri.AbsoluteUri + "?" + string.Join('&', query.Select(parameter => Uri.EscapeDataString(parameter.Key) +
                    (parameter.Value is null ? "" : "=" + Uri.EscapeDataString(parameter.Value)))));
            }
            Observe(response, uri, xid);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void Observe(XRegistryHttpResponse response, Uri uri, string? xid)
    {
        _observations.Add(new HttpFederationObservation
        {
            RequestUri = uri,
            StatusCode = (int)response.StatusCode,
            RetrievedAt = _options.TimeProvider.GetUtcNow(),
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.ContentHeaders.LastModified,
            CacheControl = response.Headers.CacheControl?.ToString(),
            Vary = string.Join(", ", response.Headers.Vary),
            ContentEncoding = string.Join(", ", response.ContentHeaders.ContentEncoding),
            SelectedXid = xid
        });
        foreach (var link in RegistryHttpLink.Parse(response.Headers.TryGetValues("Link", out var fields) ? fields : []))
        {
            if (!link.HasRelation("xregistry-root"))
            {
                continue;
            }
            if (!Uri.TryCreate(uri, link.Reference, out var root) || root.UserInfo.Length != 0 ||
                root.GetLeftPart(UriPartial.Authority) != _client.Root.GetLeftPart(UriPartial.Authority))
            {
                throw new FederationException(FederationErrorCode.PolicyDenied, "The serving Registry link crosses the selected trust boundary.");
            }
            if (root.Query.Length != 0 || root.Fragment.Length != 0 ||
                root.AbsoluteUri.TrimEnd('/') != _client.Root.AbsoluteUri.TrimEnd('/'))
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation, "A relocated Registry root requires a new explicit source selection.");
            }
        }
    }

    private async ValueTask<byte[]> BodyAsync(XRegistryHttpResponse response, CancellationToken token)
    {
        using var destination = new BudgetedContent(_budget);
        try
        {
            await response.CopyDocumentToAsync(destination, token).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new FederationException(FederationErrorCode.IntegrityError, "The HTTP body is invalid or exceeds its transport body limit.",
                innerException: exception);
        }
        return destination.ToArray();
    }

    private async ValueTask<FederationException> FailureAsync(XRegistryHttpResponse response, CancellationToken token)
    {
        var body = await BodyAsync(response, token).ConfigureAwait(false);
        JsonElement problem = default;
        string? type = null;
        if (body.Length != 0 && response.ContentHeaders.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            problem = FederationJson.Parse(body, _budget, token);
            if (problem.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String)
            {
                type = kind.GetString();
            }
        }
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FederationErrorCode.PolicyDenied,
            HttpStatusCode.PreconditionFailed or HttpStatusCode.PartialContent or HttpStatusCode.NotModified => FederationErrorCode.InconsistentSnapshot,
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => FederationErrorCode.UnsupportedOperation,
            HttpStatusCode.NotFound when type?.EndsWith("api_not_found", StringComparison.Ordinal) == true => FederationErrorCode.UnsupportedOperation,
            HttpStatusCode.NotFound when type?.EndsWith("not_found", StringComparison.Ordinal) == true => FederationErrorCode.NotFound,
            HttpStatusCode.NotFound => FederationErrorCode.Unavailable,
            _ when (int)response.StatusCode is >= 300 and < 400 => FederationErrorCode.PolicyDenied,
            _ when type?.EndsWith("unsupported_specversion", StringComparison.Ordinal) == true => FederationErrorCode.UnsupportedVersion,
            _ => FederationErrorCode.Unavailable
        };
        return new FederationException(code, "The selected HTTP source rejected the read.", type)
        {
            HttpStatusCode = (int)response.StatusCode,
            HttpProblemDetails = problem
        };
    }

    private static FederationException TransportFailure(HttpRequestException exception) =>
        new(FederationErrorCode.Unavailable, "The selected HTTP source could not complete its request.", innerException: exception)
        { HttpStatusCode = (int?)exception.StatusCode };

    private bool ApiAvailable(string name) => Capabilities.TryGetProperty("available", out var available) &&
        available.ValueKind == JsonValueKind.Object && available.EnumerateObject().Any(api =>
            string.Equals(api.Name, name, StringComparison.OrdinalIgnoreCase) && api.Value.ValueKind == JsonValueKind.Object);

    private void ValidateIdentity(JsonElement entity, string xid)
    {
        if (!FederationSyntax.SameXid(FederationJson.String(entity, "xid"), xid))
        {
            throw FederationJson.Invalid("An HTTP representation has the wrong typed XID.");
        }
        if (xid == "/" && SpecVersion.Length != 0 && FederationJson.String(entity, "specversion") != SpecVersion)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot, "The source Core version changed during the session.");
        }
    }

    private static string WirePath(IEnumerable<string> parts) => string.Join('/', parts.Select(Uri.EscapeDataString));

    private FederationReadResult FromJson(string xid, JsonObject metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = RegistryJson.Create(writer => metadata.WriteTo(writer), new RegistryJsonLimits
        {
            MaxBytes = _budget.Limits.MaxResultBytes,
            MaxDepth = _budget.Limits.MaxJsonDepth,
            MaxNodes = (int)Math.Max(1, Math.Min(int.MaxValue, _budget.Limits.MaxWork - _budget.Work)),
        });
        FederationJson.CountWork(owned.RootElement, _budget, cancellationToken);
        return FederationReadResult.FromMetadata(xid, Context, owned.RootElement);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _deadline.Cancel();
            _client.Dispose();
            _deadline.Dispose();
        }
    }

    private sealed class ModelResolver(IRegistryModelResolver resolver, FederationReadBudget budget,
        CancellationToken cancellationToken) : IRegistryModelResolver
    {
        public RegistryJson Resolve(Uri documentUri)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeRequest();
            budget.ChargeObject();
            var document = resolver.Resolve(documentUri) ??
                throw new InvalidOperationException("An explicit model resolver returned no owned model document.");
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetByteCount(document.RootElement.GetRawText());
            budget.CheckObjectBytes(bytes);
            budget.ChargeBytes(bytes);
            FederationJson.CountWork(document.RootElement, budget, cancellationToken);
            return document;
        }
    }

    private sealed class BudgetedContent(FederationReadBudget budget) : Stream
    {
        private readonly MemoryStream _buffer = new();
        public byte[] ToArray() => _buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            budget.CheckObjectBytes(_buffer.Length + buffer.Length);
            budget.ChargeBytes(buffer.Length);
            _buffer.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
